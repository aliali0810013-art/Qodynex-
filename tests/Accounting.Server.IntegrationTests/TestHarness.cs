using System.Data;
using System.Net.Http;
using Accounting.Application;
using Accounting.Domain;
using Accounting.Infrastructure.Local;
using Accounting.Infrastructure.Server;
using Accounting.Modules;
using Accounting.SharedKernel;
using Accounting.Sync.Client;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Accounting.Server.IntegrationTests;

internal sealed class MutableClock : IClock
{
    public MutableClock(DateTimeOffset now) => UtcNow = now;
    public DateTimeOffset UtcNow { get; private set; }
    public void AdvanceSeconds(int seconds = 1) => UtcNow = UtcNow.AddSeconds(seconds);
}

internal sealed class PostgresTestDatabase
{
    public string ConnectionString { get; }

    private PostgresTestDatabase(string connectionString) => ConnectionString = connectionString;

    public static async Task<PostgresTestDatabase> ResetAsync()
    {
        var connection = Environment.GetEnvironmentVariable("QODYNEX_TEST_POSTGRES")
                         ?? "Host=localhost;Port=5432;Database=qodynex_test;Username=postgres;Password=postgres";
        var systemTenant = new ServerTenantContext();
        systemTenant.SetSystem();
        var options = new DbContextOptionsBuilder<ServerAccountingDbContext>()
            .UseNpgsql(connection)
            .AddInterceptors(new ServerImmutabilityInterceptor())
            .Options;
        await using var db = new ServerAccountingDbContext(options, systemTenant);
        await db.Database.EnsureDeletedAsync();
        await new ServerDatabaseBootstrapper(db).InitializeAsync();

        await db.Database.ExecuteSqlRawAsync("""
DO $
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'qodynex_app') THEN
        CREATE ROLE qodynex_app LOGIN PASSWORD 'qodynex';
    END IF;
END $;
ALTER ROLE qodynex_app WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD 'qodynex';
GRANT USAGE ON SCHEMA public TO qodynex_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO qodynex_app;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO qodynex_app;
""");

        var appConnection = new NpgsqlConnectionStringBuilder(connection)
        {
            Username = "qodynex_app",
            Password = "qodynex"
        }.ConnectionString;
        return new PostgresTestDatabase(appConnection);
    }

    public ServerTenantHarness CreateTenant(
        Guid? companyId = null,
        Guid? branchId = null,
        Guid? userId = null,
        Guid? deviceId = null,
        IEnumerable<string>? roles = null,
        MutableClock? clock = null)
    {
        return new ServerTenantHarness(
            ConnectionString,
            companyId ?? Guid.NewGuid(),
            branchId ?? Guid.NewGuid(),
            userId ?? Guid.NewGuid(),
            deviceId ?? Guid.NewGuid(),
            roles ?? new[] { "tenant.admin", "accounting.post", "accounting.reverse" },
            clock ?? new MutableClock(new DateTimeOffset(2026, 11, 10, 1, 0, 0, TimeSpan.Zero)));
    }
}

internal sealed class ServerTenantHarness : IAsyncDisposable
{
    public Guid CompanyId { get; }
    public Guid BranchId { get; }
    public Guid UserId { get; }
    public Guid DeviceId { get; }
    public Guid CashAccountId { get; } = Guid.NewGuid();
    public Guid ReceivableAccountId { get; } = Guid.NewGuid();
    public Guid InventoryAccountId { get; } = Guid.NewGuid();
    public Guid SalesAccountId { get; } = Guid.NewGuid();
    public Guid CogsAccountId { get; } = Guid.NewGuid();
    public Guid ServiceRevenueAccountId { get; } = Guid.NewGuid();
    public Guid ServiceCostAccountId { get; } = Guid.NewGuid();
    public Guid UnitId { get; } = Guid.NewGuid();
    public Guid WarehouseId { get; } = Guid.NewGuid();
    public Guid StockItemId { get; } = Guid.NewGuid();
    public Guid ServiceItemId { get; } = Guid.NewGuid();
    public Guid CustomerId { get; } = Guid.NewGuid();
    public MutableClock Clock { get; }
    public ServerTenantContext Tenant { get; }
    public ServerAccountingDbContext Db { get; }
    public TenantPostgresSession Session { get; }
    public SyncPushCommandHandler Push { get; }
    public SyncPullQueryHandler Pull { get; }
    public DeviceRegistrationService Devices { get; }
    public TenantAdministrationService Admin { get; }
    public TenantBootstrapService Bootstrap { get; }
    public IReadOnlySet<string> Roles { get; }

    public ServerTenantHarness(string connectionString, Guid companyId, Guid branchId, Guid userId, Guid deviceId,
        IEnumerable<string> roles, MutableClock clock)
    {
        CompanyId = companyId;
        BranchId = branchId;
        UserId = userId;
        DeviceId = deviceId;
        Clock = clock;
        Roles = roles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Tenant = new ServerTenantContext();
        Tenant.SetAuthenticated(companyId, branchId, userId, deviceId, Roles);
        var options = new DbContextOptionsBuilder<ServerAccountingDbContext>()
            .UseNpgsql(connectionString)
            .AddInterceptors(new ServerImmutabilityInterceptor())
            .Options;
        Db = new ServerAccountingDbContext(options, Tenant);
        Session = new TenantPostgresSession(Db, Tenant);
        var sequence = new OfficialSequenceGenerator(Db);
        var currency = new CurrencyPolicyService();
        Push = new SyncPushCommandHandler(Db, Tenant, Session, sequence, currency, Clock);
        Pull = new SyncPullQueryHandler(Db, Tenant, Session, Clock);
        Devices = new DeviceRegistrationService(Db, Tenant, Session, Clock);
        Admin = new TenantAdministrationService(Db, Tenant, Session, Clock);
        Bootstrap = new TenantBootstrapService(Db, Tenant, Session, Clock);
    }

    public async Task ConfigureDefaultAsync(
        decimal customerCreditLimit = 1000m,
        bool allowNegativeStock = false,
        IReadOnlyList<string>? enabledModules = null)
    {
        var modules = enabledModules ?? new[] { RetailModule.ModuleKey, WholesaleModule.ModuleKey, ServicesModule.ModuleKey, RestaurantModule.ModuleKey };
        await Admin.ConfigureAsync(new TenantConfigurationRequest(
            "Qodynex Test",
            "B1",
            "Main Branch",
            modules,
            Array.Empty<ServerModuleConfigurationRegistration>(),
            new TenantPolicyRegistration("USD", 2, 6, MidpointRounding.AwayFromZero,
                InventoryValuationMethod.MovingWeightedAverage, allowNegativeStock, false, 0m, false, null, null),
            new[]
            {
                new ServerAccountRegistration(CashAccountId, "1100", "Cash", null, AccountType.Asset, NormalBalance.Debit, true, true),
                new ServerAccountRegistration(ReceivableAccountId, "1200", "Receivable", null, AccountType.Asset, NormalBalance.Debit, true, true),
                new ServerAccountRegistration(InventoryAccountId, "1300", "Inventory", null, AccountType.Asset, NormalBalance.Debit, true, true),
                new ServerAccountRegistration(SalesAccountId, "4100", "Sales", null, AccountType.Revenue, NormalBalance.Credit, true, true),
                new ServerAccountRegistration(CogsAccountId, "5100", "COGS", null, AccountType.Expense, NormalBalance.Debit, true, true),
                new ServerAccountRegistration(ServiceRevenueAccountId, "4200", "Service Revenue", null, AccountType.Revenue, NormalBalance.Credit, true, true),
                new ServerAccountRegistration(ServiceCostAccountId, "5200", "Service Cost", null, AccountType.Expense, NormalBalance.Debit, true, true)
            },
            new[] { new ServerUnitRegistration(UnitId, "EA", "Each") },
            new[]
            {
                new ServerBusinessPartnerRegistration(CustomerId, "C001", "Customer", PartnerType.Customer,
                    ReceivableAccountId, "777000000", customerCreditLimit, true)
            },
            new[]
            {
                new ServerItemRegistration(StockItemId, "ITEM", "Stock Item", "1234567890123", UnitId, ItemKind.Stock,
                    InventoryAccountId, SalesAccountId, CogsAccountId, true),
                new ServerItemRegistration(ServiceItemId, "SVC", "Service Item", null, UnitId, ItemKind.Service,
                    Guid.Empty, ServiceRevenueAccountId, ServiceCostAccountId, true)
            },
            new[] { new ServerWarehouseRegistration(WarehouseId, "WH", "Main Warehouse", null) }));
    }

    public Task<DeviceRegistration> RegisterAsync() =>
        Devices.RegisterAsync(new DeviceRegistrationRequest(
            DeviceId, $"Device-{DeviceId:N}", "Test", "1.0.0",
            SyncProtocol.CurrentClientSchemaVersion, SyncProtocol.CurrentProtocolVersion));

    public Task<TenantBootstrapResult> GetBootstrapAsync() =>
        Bootstrap.GetAsync(new TenantBootstrapRequest(
            SyncProtocol.CurrentClientSchemaVersion, SyncProtocol.CurrentProtocolVersion, DeviceId));

    public ServerTenantHarness CreateAdditionalDevice(Guid deviceId, Guid? userId = null) =>
        new(Db.Database.GetConnectionString()!, CompanyId, BranchId, userId ?? Guid.NewGuid(), deviceId, Roles, Clock);

    public InProcessSyncTransport Transport() => new(this);

    public async ValueTask DisposeAsync() => await Db.DisposeAsync();
}

internal sealed class InProcessSyncTransport : ISyncTransport
{
    private readonly ServerTenantHarness _server;
    public InProcessSyncTransport(ServerTenantHarness server) => _server = server;
    public Task<SyncPushResult> PushAsync(SyncPushBatch batch, CancellationToken cancellationToken = default) =>
        _server.Push.HandleAsync(batch, cancellationToken);
    public Task<SyncPullResult> PullAsync(SyncPullRequest request, CancellationToken cancellationToken = default) =>
        _server.Pull.HandleAsync(request, cancellationToken);
    public Task<TenantBootstrapResult> BootstrapAsync(TenantBootstrapRequest request, CancellationToken cancellationToken = default) =>
        _server.Bootstrap.GetAsync(request, cancellationToken);

    public async Task<DeviceRegistrationResult> RegisterDeviceAsync(DeviceRegistrationRequest request, CancellationToken cancellationToken = default)
    {
        var row = await _server.Devices.RegisterAsync(request, cancellationToken);
        return new DeviceRegistrationResult(row.DeviceId, row.DeviceName, row.Platform, row.ClientVersion,
            row.ClientSchemaVersion, row.ProtocolVersion, row.IsActive, row.RegisteredAtUtc, row.LastSeenAtUtc);
    }
}

internal sealed class LocalDeviceHarness : IAsyncDisposable
{
    private readonly string _path;
    public LocalAccountingDbContext Db { get; }
    public UserContext User { get; }
    public LocalSyncStore SyncStore { get; }
    public AccountingPostingEngine Posting { get; }
    public IAccountingQueryService Queries { get; }
    public LocalTempNumberGenerator Numbers { get; }
    public ModuleRegistry Modules { get; }
    public IReadOnlyList<string> EnabledModules { get; }

    private LocalDeviceHarness(string path, LocalAccountingDbContext db, UserContext user, LocalSyncStore syncStore,
        AccountingPostingEngine posting, IAccountingQueryService queries, LocalTempNumberGenerator numbers,
        ModuleRegistry modules, IReadOnlyList<string> enabledModules)
    {
        _path = path;
        Db = db;
        User = user;
        SyncStore = syncStore;
        Posting = posting;
        Queries = queries;
        Numbers = numbers;
        Modules = modules;
        EnabledModules = enabledModules;
    }

    public static async Task<LocalDeviceHarness> CreateAsync(TenantBootstrapResult bootstrap, Guid userId, Guid deviceId)
    {
        var path = Path.Combine(Path.GetTempPath(), $"qodynex-local-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<LocalAccountingDbContext>()
            .UseSqlite($"Data Source={path}")
            .AddInterceptors(new ImmutabilityInterceptor())
            .Options;
        var db = new LocalAccountingDbContext(options);
        await new LocalDatabaseBootstrapper(db).InitializeAsync();
        var user = UserContext.Authenticated(userId, bootstrap.CompanyId, bootstrap.BranchId, deviceId);
        var syncStore = new LocalSyncStore(db);
        await syncStore.ApplyBootstrapAsync(user, bootstrap);

        var store = new EfAccountingDataStore(db);
        var currency = new CurrencyPolicyService();
        var inventory = new InventoryValuationEngine(currency);
        var queries = new AccountingQueryService(store, currency, inventory);
        var modules = new IActivityModule[]
        {
            new RetailModule(store),
            new WholesaleModule(store, queries, currency),
            new ServicesModule(store),
            new RestaurantModule()
        };
        var registry = new ModuleRegistry(modules);
        var enabled = bootstrap.EnabledModules.ToArray();
        var posting = new AccountingPostingEngine(store, new SqliteUnitOfWork(db), new PermissionGuard(new EfUserAuthorizationReader(db)),
            new DocumentPostingRuleRegistry(registry.PostingRules(enabled)), currency, inventory, new MutableClock(bootstrap.ServerTimestampUtc));
        return new LocalDeviceHarness(path, db, user, syncStore, posting, queries, new LocalTempNumberGenerator(db), registry, enabled);
    }

    public async Task<PurchaseInvoice> PostPurchaseAsync(Guid itemId, Guid unitId, Guid warehouseId, Guid cashAccountId,
        decimal quantity, decimal unitPrice, string key)
    {
        await using var tx = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            var number = await Numbers.NextAsync(User, nameof(PurchaseInvoice));
            var invoice = new PurchaseInvoice(User.CompanyId, User.BranchId, number, RetailModule.ModuleKey,
                DateTimeOffset.UtcNow, warehouseId, cashAccountId);
            invoice.AddLine(itemId, unitId, quantity, 1m, unitPrice);
            Db.PurchaseInvoices.Add(invoice);
            await Db.SaveChangesAsync();
            await Posting.PostPurchaseInvoiceAsync(new PostPurchaseInvoiceCommand(invoice.Id,
                new PostingCommandContext(User, UuidV7.NewGuid(), key)));
            await tx.CommitAsync();
            return invoice;
        }
        catch
        {
            await tx.RollbackAsync();
            Db.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<SalesInvoice> PostSaleAsync(Guid itemId, Guid unitId, Guid warehouseId, Guid settlementAccountId,
        Guid? partnerId, decimal quantity, decimal unitPrice, string moduleKey, string key, string metadata = "{}")
    {
        await using var tx = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            var number = await Numbers.NextAsync(User, nameof(SalesInvoice));
            var invoice = new SalesInvoice(User.CompanyId, User.BranchId, number, moduleKey,
                DateTimeOffset.UtcNow, warehouseId, settlementAccountId, partnerId);
            invoice.SetModuleMetadata(metadata);
            invoice.AddLine(itemId, unitId, quantity, 1m, unitPrice);
            Db.SalesInvoices.Add(invoice);
            await Db.SaveChangesAsync();
            await Posting.PostSalesInvoiceAsync(new PostSalesInvoiceCommand(invoice.Id,
                new PostingCommandContext(User, UuidV7.NewGuid(), key)));
            await tx.CommitAsync();
            return invoice;
        }
        catch
        {
            await tx.RollbackAsync();
            Db.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<ReceiptVoucher> PostReceiptAsync(Guid customerId, Guid cashAccountId, Guid receivableAccountId,
        decimal amount, string key)
    {
        await using var tx = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            var number = await Numbers.NextAsync(User, nameof(ReceiptVoucher));
            var voucher = new ReceiptVoucher(User.CompanyId, User.BranchId, number, "CORE", DateTimeOffset.UtcNow,
                cashAccountId, receivableAccountId, amount, "Collection", customerId);
            Db.ReceiptVouchers.Add(voucher);
            await Db.SaveChangesAsync();
            await Posting.PostReceiptVoucherAsync(new PostReceiptVoucherCommand(voucher.Id,
                new PostingCommandContext(User, UuidV7.NewGuid(), key)));
            await tx.CommitAsync();
            return voucher;
        }
        catch
        {
            await tx.RollbackAsync();
            Db.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<SyncPushBatch> PendingBatchAsync()
    {
        var messages = await SyncStore.GetPendingEnvelopesAsync(User, SyncProtocol.MaxBatchSize);
        var watermark = await SyncStore.GetLastPulledWatermarkAsync(User);
        return new SyncPushBatch(SyncProtocol.CurrentClientSchemaVersion, SyncProtocol.CurrentProtocolVersion,
            User.DeviceId, watermark, messages);
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        TryDelete(_path);
        TryDelete(_path + "-wal");
        TryDelete(_path + "-shm");
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
