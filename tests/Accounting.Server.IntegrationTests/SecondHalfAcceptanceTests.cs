using Accounting.Domain;
using Accounting.Infrastructure.Server;
using Accounting.Modules;
using Accounting.SharedKernel;
using Accounting.Sync.Client;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Server.IntegrationTests;

public sealed class SecondHalfAcceptanceTests
{
    [Fact]
    public async Task Replay_Same_Sales_And_Receipt_Batch_100x_Is_Exactly_Idempotent()
    {
        var database = await PostgresTestDatabase.ResetAsync();
        await using var server = database.CreateTenant();
        await server.ConfigureDefaultAsync();
        await server.RegisterAsync();
        var bootstrap = await server.GetBootstrapAsync();
        await using var device = await LocalDeviceHarness.CreateAsync(bootstrap, server.UserId, server.DeviceId);

        await device.PostPurchaseAsync(server.StockItemId, server.UnitId, server.WarehouseId, server.CashAccountId, 10m, 100m, "seed-purchase");
        var seedBatch = await device.PendingBatchAsync();
        var seedResult = await server.Push.HandleAsync(seedBatch);
        await device.SyncStore.ApplyPushResultAsync(device.User, seedResult);
        server.Clock.AdvanceSeconds();

        var sale = await device.PostSaleAsync(server.StockItemId, server.UnitId, server.WarehouseId, server.CashAccountId,
            null, 2m, 250m, RetailModule.ModuleKey, "replay-sale");
        var receipt = await device.PostReceiptAsync(server.CustomerId, server.CashAccountId, server.ReceivableAccountId, 100m, "replay-receipt");
        var repeatedBatch = await device.PendingBatchAsync();
        Assert.Equal(2, repeatedBatch.Messages.Count);

        string? firstSalesNumber = null;
        string? firstReceiptNumber = null;
        for (var i = 0; i < 100; i++)
        {
            var result = await server.Push.HandleAsync(repeatedBatch);
            Assert.Equal(2, result.Acknowledgements.Count);
            Assert.All(result.Acknowledgements, x => Assert.Equal(SyncAckStatus.Accepted, x.Status));

            var salesAck = result.Acknowledgements.Single(x => x.DocumentId == sale.Id);
            var receiptAck = result.Acknowledgements.Single(x => x.DocumentId == receipt.Id);
            firstSalesNumber ??= salesAck.OfficialSequenceNumber;
            firstReceiptNumber ??= receiptAck.OfficialSequenceNumber;
            Assert.Equal(firstSalesNumber, salesAck.OfficialSequenceNumber);
            Assert.Equal(firstReceiptNumber, receiptAck.OfficialSequenceNumber);
        }

        await using var tx = await server.Db.Database.BeginTransactionAsync();
        await server.Session.ApplyAsync();
        Assert.Equal(1, await server.Db.FinancialDocuments.CountAsync(x => x.Id == sale.Id));
        Assert.Equal(1, await server.Db.FinancialDocuments.CountAsync(x => x.Id == receipt.Id));
        Assert.Equal(1, await server.Db.JournalEntries.CountAsync(x => x.SourceDocumentId == sale.Id));
        Assert.Equal(1, await server.Db.JournalEntries.CountAsync(x => x.SourceDocumentId == receipt.Id));
        Assert.Equal(1, await server.Db.StockLedgerEntries.CountAsync(x => x.SourceDocumentId == sale.Id));
        Assert.Equal(1, await server.Db.InboxMessages.CountAsync(x => x.IdempotencyKey == "replay-sale"));
        Assert.Equal(1, await server.Db.InboxMessages.CountAsync(x => x.IdempotencyKey == "replay-receipt"));

        var salesSequence = await server.Db.SequenceCounters.SingleAsync(x => x.DocumentType == nameof(SalesInvoice));
        var receiptSequence = await server.Db.SequenceCounters.SingleAsync(x => x.DocumentType == nameof(ReceiptVoucher));
        Assert.Equal(1, salesSequence.CurrentValue);
        Assert.Equal(1, receiptSequence.CurrentValue);

        var debit = await server.Db.JournalLines.SumAsync(x => x.Debit);
        var credit = await server.Db.JournalLines.SumAsync(x => x.Credit);
        Assert.Equal(debit, credit);
        await tx.CommitAsync();
    }

    [Fact]
    public async Task MultiTenant_RLS_And_Handler_Reject_Cross_Company_Push_And_Read()
    {
        var database = await PostgresTestDatabase.ResetAsync();
        await using var companyA = database.CreateTenant();
        await companyA.ConfigureDefaultAsync();
        await companyA.RegisterAsync();

        await using var companyB = database.CreateTenant();
        await companyB.ConfigureDefaultAsync();
        await companyB.RegisterAsync();
        var bootstrapB = await companyB.GetBootstrapAsync();
        await using var deviceB = await LocalDeviceHarness.CreateAsync(bootstrapB, companyB.UserId, companyB.DeviceId);
        var purchaseB = await deviceB.PostPurchaseAsync(companyB.StockItemId, companyB.UnitId, companyB.WarehouseId,
            companyB.CashAccountId, 5m, 50m, "company-b-purchase");
        var batchB = await deviceB.PendingBatchAsync();
        await companyB.Push.HandleAsync(batchB);

        var forged = batchB with
        {
            DeviceId = companyA.DeviceId,
            Messages = batchB.Messages.Select(x => x with { DeviceId = companyA.DeviceId }).ToArray()
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => companyA.Push.HandleAsync(forged));

        await using var tx = await companyA.Db.Database.BeginTransactionAsync();
        await companyA.Session.ApplyAsync();
        var leaked = await companyA.Db.FinancialDocuments
            .IgnoreQueryFilters()
            .CountAsync(x => x.CompanyId == companyB.CompanyId || x.Id == purchaseB.Id);
        Assert.Equal(0, leaked);
        await tx.CommitAsync();

        var pullA = await companyA.Pull.HandleAsync(new SyncPullRequest(
            SyncProtocol.CurrentClientSchemaVersion, SyncProtocol.CurrentProtocolVersion, companyA.DeviceId, 0, 200));
        Assert.DoesNotContain(pullA.Changes, x => x.EntityId == purchaseB.Id);
    }

    [Fact]
    public async Task Offline_Android_To_Server_To_Windows_Lifecycle_Assigns_Official_Numbers_And_Does_Not_Create_Remote_Outbox()
    {
        var database = await PostgresTestDatabase.ResetAsync();
        await using var androidServer = database.CreateTenant();
        await androidServer.ConfigureDefaultAsync();
        await androidServer.RegisterAsync();
        var androidBootstrap = await androidServer.GetBootstrapAsync();
        await using var android = await LocalDeviceHarness.CreateAsync(androidBootstrap, androidServer.UserId, androidServer.DeviceId);

        var androidCoordinator = new BackgroundSyncCoordinator(androidServer.Transport(), android.SyncStore,
            new AlwaysOnlineNetwork(), android.User, androidServer.Clock);

        await android.PostPurchaseAsync(androidServer.StockItemId, androidServer.UnitId, androidServer.WarehouseId,
            androidServer.CashAccountId, 10m, 100m, "lifecycle-seed");
        await androidCoordinator.RunOnceAsync();
        androidServer.Clock.AdvanceSeconds();

        var offlineSale = await android.PostSaleAsync(androidServer.StockItemId, androidServer.UnitId, androidServer.WarehouseId,
            androidServer.CashAccountId, null, 3m, 250m, RetailModule.ModuleKey, "offline-sale");
        var offlineReceipt = await android.PostReceiptAsync(androidServer.CustomerId, androidServer.CashAccountId,
            androidServer.ReceivableAccountId, 120m, "offline-receipt");

        Assert.Equal(2, (await android.SyncStore.GetPendingEnvelopesAsync(android.User, 200)).Count);
        var sync = await androidCoordinator.RunOnceAsync();
        Assert.Equal(2, sync.PushedMessages);
        Assert.True(await android.Db.OfficialSequenceAssignments.AnyAsync(x => x.DocumentId == offlineSale.Id));
        Assert.True(await android.Db.OfficialSequenceAssignments.AnyAsync(x => x.DocumentId == offlineReceipt.Id));

        var windowsDeviceId = Guid.NewGuid();
        await using var windowsServer = androidServer.CreateAdditionalDevice(windowsDeviceId);
        await windowsServer.RegisterAsync();
        var windowsBootstrap = await windowsServer.GetBootstrapAsync();
        await using var windows = await LocalDeviceHarness.CreateAsync(windowsBootstrap, windowsServer.UserId, windowsServer.DeviceId);
        var windowsCoordinator = new BackgroundSyncCoordinator(windowsServer.Transport(), windows.SyncStore,
            new AlwaysOnlineNetwork(), windows.User, windowsServer.Clock);

        var windowsSync = await windowsCoordinator.RunOnceAsync();
        Assert.True(windowsSync.PulledChanges >= 3);
        Assert.True(await windows.Db.SalesInvoices.AsNoTracking().AnyAsync(x => x.Id == offlineSale.Id && x.Status == PostingStatus.Posted));
        Assert.True(await windows.Db.ReceiptVouchers.AsNoTracking().AnyAsync(x => x.Id == offlineReceipt.Id && x.Status == PostingStatus.Posted));
        Assert.True(await windows.Db.OfficialSequenceAssignments.AsNoTracking().AnyAsync(x => x.DocumentId == offlineSale.Id));
        Assert.True(await windows.Db.OfficialSequenceAssignments.AsNoTracking().AnyAsync(x => x.DocumentId == offlineReceipt.Id));
        Assert.Equal(0, await windows.Db.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task Services_Only_Hides_Stock_Requirements_And_Wholesale_Rejects_Credit_Limit_Excess()
    {
        var database = await PostgresTestDatabase.ResetAsync();
        await using (var servicesServer = database.CreateTenant())
        {
            await servicesServer.ConfigureDefaultAsync(enabledModules: new[] { ServicesModule.ModuleKey });
            await servicesServer.RegisterAsync();
            var bootstrap = await servicesServer.GetBootstrapAsync();
            await using var services = await LocalDeviceHarness.CreateAsync(bootstrap, servicesServer.UserId, servicesServer.DeviceId);

            var visible = services.Modules.VisibleScreens(services.EnabledModules, ActivityPlatform.Android);
            Assert.Contains(visible, x => x.Key == "services.invoice.mobile");
            Assert.DoesNotContain(visible, x => x.Key.StartsWith("retail.", StringComparison.OrdinalIgnoreCase));
            Assert.True(services.Modules.IsFeatureEnabled(services.EnabledModules, "warehouse.hidden"));

            var invoice = await services.PostSaleAsync(servicesServer.ServiceItemId, servicesServer.UnitId, Guid.Empty,
                servicesServer.CashAccountId, null, 1m, 300m, ServicesModule.ModuleKey, "service-only-sale");
            Assert.Equal(0, await services.Db.StockLedgerEntries.CountAsync(x => x.SourceDocumentId == invoice.Id));
            var serviceJournal = await services.Db.JournalEntries.Include(x => x.Lines).SingleAsync(x => x.SourceDocumentId == invoice.Id);
            Assert.Equal(serviceJournal.Lines.Sum(x => x.Debit), serviceJournal.Lines.Sum(x => x.Credit));
        }

        database = await PostgresTestDatabase.ResetAsync();
        await using var wholesaleServer = database.CreateTenant();
        await wholesaleServer.ConfigureDefaultAsync(customerCreditLimit: 100m, enabledModules: new[] { WholesaleModule.ModuleKey });
        await wholesaleServer.RegisterAsync();
        var wholesaleBootstrap = await wholesaleServer.GetBootstrapAsync();
        await using var wholesale = await LocalDeviceHarness.CreateAsync(wholesaleBootstrap, wholesaleServer.UserId, wholesaleServer.DeviceId);

        var ex = await Assert.ThrowsAsync<AccountingDomainException>(() =>
            wholesale.PostSaleAsync(wholesaleServer.StockItemId, wholesaleServer.UnitId, wholesaleServer.WarehouseId,
                wholesaleServer.ReceivableAccountId, wholesaleServer.CustomerId, 2m, 100m,
                WholesaleModule.ModuleKey, "wholesale-over-limit"));
        Assert.Contains("credit limit", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await wholesale.Db.JournalEntries.CountAsync(x => x.SourceDocumentType == nameof(SalesInvoice)));
    }

    [Fact]
    public async Task Official_Sequence_Is_Contiguous_And_Unique_Under_Concurrent_PostgreSql_Transactions()
    {
        var database = await PostgresTestDatabase.ResetAsync();
        var companyId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var clock = new MutableClock(new DateTimeOffset(2026, 11, 10, 2, 0, 0, TimeSpan.Zero));
        var roles = new[] { "tenant.admin", "accounting.post" };

        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var tenant = database.CreateTenant(companyId, branchId, userId, deviceId, roles, clock);
            await using var tx = await tenant.Db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            await tenant.Session.ApplyAsync();
            var result = await new OfficialSequenceGenerator(tenant.Db)
                .NextAsync(companyId, branchId, nameof(SalesInvoice), clock.UtcNow);
            await tenant.Db.SaveChangesAsync();
            await tx.CommitAsync();
            return result;
        }).ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.Equal(20, results.Select(x => x.Sequence).Distinct().Count());
        Assert.Equal(20, results.Select(x => x.Number).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Enumerable.Range(1, 20).Select(x => (long)x), results.Select(x => x.Sequence).OrderBy(x => x));
        Assert.All(results, x => Assert.Matches(@"^INV-2026-\d{6}$", x.Number));
    }

    [Fact]
    public void Wholesale_Price_Tiers_Select_Highest_Applicable_Quantity_Band()
    {
        var itemId = Guid.NewGuid();
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            priceTiers = new[]
            {
                new { itemId, minBaseQuantity = 10m, unitPrice = 90m },
                new { itemId, minBaseQuantity = 50m, unitPrice = 80m }
            }
        });

        var pricing = new WholesalePricingService();
        Assert.Equal(100m, pricing.GetUnitPrice(json, itemId, 5m, 100m));
        Assert.Equal(90m, pricing.GetUnitPrice(json, itemId, 10m, 100m));
        Assert.Equal(80m, pricing.GetUnitPrice(json, itemId, 75m, 100m));
    }

    [Fact]
    public async Task Protocol_Mismatch_Is_Rejected_Before_Server_Writes()
    {
        var database = await PostgresTestDatabase.ResetAsync();
        await using var server = database.CreateTenant();
        await server.ConfigureDefaultAsync();
        await server.RegisterAsync();

        var request = new SyncPullRequest(
            SyncProtocol.CurrentClientSchemaVersion + 1,
            "999.0",
            server.DeviceId,
            0,
            10);

        await Assert.ThrowsAsync<SyncProtocolMismatchException>(() => server.Pull.HandleAsync(request));

        await using var tx = await server.Db.Database.BeginTransactionAsync();
        await server.Session.ApplyAsync();
        Assert.Equal(0, await server.Db.FinancialDocuments.CountAsync());
        Assert.Equal(0, await server.Db.InboxMessages.CountAsync());
        await tx.CommitAsync();
    }

    private sealed class AlwaysOnlineNetwork : INetworkAvailability
    {
        public Task<bool> IsNetworkAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
