using System.Data;
using System.Text.Json;
using Accounting.Domain;
using Accounting.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Infrastructure.Server;

public sealed class ServerSyncConflictException : InvalidOperationException
{
    public string Code { get; }
    public ServerSyncConflictException(string code, string message) : base(message) => Code = code;
}

public sealed class SyncPushCommandHandler
{
    private readonly ServerAccountingDbContext _db;
    private readonly IServerTenantContext _tenant;
    private readonly TenantPostgresSession _session;
    private readonly OfficialSequenceGenerator _sequence;
    private readonly ICurrencyPolicyService _currency;
    private readonly IClock _clock;

    public SyncPushCommandHandler(
        ServerAccountingDbContext db,
        IServerTenantContext tenant,
        TenantPostgresSession session,
        OfficialSequenceGenerator sequence,
        ICurrencyPolicyService currency,
        IClock clock)
    {
        _db = db;
        _tenant = tenant;
        _session = session;
        _sequence = sequence;
        _currency = currency;
        _clock = clock;
    }

    public async Task<SyncPushResult> HandleAsync(SyncPushBatch batch, CancellationToken cancellationToken = default)
    {
        SyncProtocol.EnsureCompatible(batch.ClientSchemaVersion, batch.ProtocolVersion);
        if (!_tenant.IsAuthenticated || _tenant.IsSystem) throw new UnauthorizedAccessException("Authenticated tenant context is required.");
        if (batch.DeviceId != _tenant.DeviceId) throw new UnauthorizedAccessException("JWT DeviceId does not match sync batch.");
        if (batch.Messages.Count > SyncProtocol.MaxBatchSize) throw new InvalidOperationException($"Maximum sync batch size is {SyncProtocol.MaxBatchSize}.");

        var acknowledgements = new List<SyncAck>(batch.Messages.Count);
        foreach (var envelope in batch.Messages.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.EnvelopeId))
        {
            _tenant.DemandTenant(envelope.CompanyId, envelope.BranchId);
            if (envelope.DeviceId != _tenant.DeviceId) throw new UnauthorizedAccessException("Envelope DeviceId does not match JWT DeviceId.");
            DemandPermission(envelope);
            acknowledgements.Add(await ProcessMessageAsync(envelope, cancellationToken).ConfigureAwait(false));
        }

        var max = acknowledgements.Count == 0
            ? await CurrentWatermarkAsync(cancellationToken).ConfigureAwait(false)
            : acknowledgements.Max(x => x.ServerWatermark);

        return new SyncPushResult(
            SyncProtocol.CurrentClientSchemaVersion,
            SyncProtocol.CurrentProtocolVersion,
            _clock.UtcNow,
            max,
            acknowledgements);
    }

    private async Task AcquireIdempotencyLockAsync(SyncEnvelope envelope, CancellationToken cancellationToken)
    {
        var key = $"idem:{envelope.CompanyId:N}:{envelope.BranchId:N}:{envelope.IdempotencyKey}";
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0));",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task AcquireInventoryLocksAsync(PostedOperationSyncPayload payload, CancellationToken cancellationToken)
    {
        var keys = payload.StockEntries
            .Select(x => $"stock:{payload.Document.CompanyId:N}:{payload.Document.BranchId:N}:{x.ItemId:N}:{x.WarehouseId:N}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        foreach (var key in keys)
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0));",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private void DemandPermission(SyncEnvelope envelope)
    {
        var required = string.Equals(envelope.EventType, "ReversePostedDocument", StringComparison.OrdinalIgnoreCase)
            ? "accounting.reverse"
            : "accounting.post";
        if (!_tenant.RoleClaims.Contains(required) && !_tenant.RoleClaims.Contains("tenant.admin"))
            throw new UnauthorizedAccessException($"JWT lacks required permission '{required}'.");
    }

    private async Task<SyncAck> ProcessMessageAsync(SyncEnvelope envelope, CancellationToken cancellationToken)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await _session.ApplyAsync(cancellationToken).ConfigureAwait(false);
        await DemandActiveDeviceAsync(cancellationToken).ConfigureAwait(false);
        await AcquireIdempotencyLockAsync(envelope, cancellationToken).ConfigureAwait(false);

        var existing = await _db.InboxMessages.SingleOrDefaultAsync(
            x => x.IdempotencyKey == envelope.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            if (existing.OperationId != envelope.OperationId)
                throw new InvalidOperationException("The same IdempotencyKey cannot be reused for a different OperationId.");

            var replay = JsonSerializer.Deserialize<SyncAck>(existing.ResultJson)
                         ?? throw new InvalidOperationException("Stored inbox acknowledgement is invalid.");
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return replay;
        }

        PostedOperationSyncPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<PostedOperationSyncPayload>(envelope.PayloadJson)
                      ?? throw new InvalidOperationException("Sync payload is empty.");
            ValidateEnvelope(payload, envelope);
            var policy = await GetPolicyAsync(cancellationToken).ConfigureAwait(false);
            await ValidateJournalAsync(payload, policy, cancellationToken).ConfigureAwait(false);
            await AcquireInventoryLocksAsync(payload, cancellationToken).ConfigureAwait(false);
            if (!payload.Journal.ReversedEntryId.HasValue)
                await ValidateDocumentAccountingAsync(payload, policy, cancellationToken).ConfigureAwait(false);
            await ValidateStockAsync(payload, policy, cancellationToken).ConfigureAwait(false);

            SyncAck ack;
            if (payload.Journal.ReversedEntryId.HasValue)
                ack = await AcceptReversalAsync(envelope, payload, cancellationToken).ConfigureAwait(false);
            else
                ack = await AcceptPostedOperationAsync(envelope, payload, cancellationToken).ConfigureAwait(false);

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ack;
        }
        catch (ServerSyncConflictException conflict)
        {
            var documentId = TryDocumentId(envelope.PayloadJson);
            var ack = await StoreConflictAsync(envelope, documentId, conflict.Code, conflict.Message, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ack;
        }
        catch (Exception ex) when (ex is AccountingDomainException or InvalidOperationException or JsonException)
        {
            var ack = await StoreRejectedAsync(envelope, ex.Message, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ack;
        }
    }

    private async Task<SyncAck> AcceptPostedOperationAsync(SyncEnvelope envelope, PostedOperationSyncPayload payload, CancellationToken cancellationToken)
    {
        if (await _db.FinancialDocuments.AnyAsync(x => x.Id == payload.Document.Id, cancellationToken).ConfigureAwait(false))
            throw new ServerSyncConflictException("DOCUMENT_ALREADY_EXISTS", "A posted document with this identity already exists on the server; overwrite is forbidden.");

        var now = _clock.UtcNow;
        var allocation = await _sequence.NextAsync(
            envelope.CompanyId,
            envelope.BranchId,
            payload.Document.DocumentType,
            payload.Document.DocumentDateUtc,
            cancellationToken).ConfigureAwait(false);

        var document = new ServerFinancialDocument
        {
            Id = payload.Document.Id,
            CompanyId = envelope.CompanyId,
            BranchId = envelope.BranchId,
            DocumentType = payload.Document.DocumentType,
            ModuleKey = payload.Document.ModuleKey,
            ModuleMetadataJson = payload.Document.ModuleMetadataJson,
            LocalTempNumber = payload.Document.LocalTempNumber,
            OfficialSequenceNumber = allocation.Number,
            OfficialSequence = allocation.Sequence,
            DocumentDateUtc = payload.Document.DocumentDateUtc,
            OperationId = envelope.OperationId,
            IdempotencyKey = envelope.IdempotencyKey,
            SourceDeviceId = envelope.DeviceId,
            BusinessPartnerId = payload.Document.BusinessPartnerId,
            WarehouseId = payload.Document.WarehouseId,
            PrimaryAccountId = payload.Document.PrimaryAccountId,
            CounterAccountId = payload.Document.CounterAccountId,
            Amount = payload.Document.Amount,
            Description = payload.Document.Description,
            ServerTimestampUtc = now
        };
        _db.FinancialDocuments.Add(document);
        foreach (var line in payload.Document.Lines)
        {
            _db.DocumentLines.Add(new ServerDocumentLine
            {
                Id = line.Id,
                CompanyId = envelope.CompanyId,
                BranchId = envelope.BranchId,
                DocumentId = document.Id,
                ItemId = line.ItemId,
                UnitOfMeasureId = line.UnitOfMeasureId,
                Quantity = line.Quantity,
                BaseUnitsPerUnit = line.BaseUnitsPerUnit,
                UnitPrice = line.UnitPrice
            });
        }
        AddJournalAndStock(envelope, payload, now);

        var change = new SyncChange
        {
            CompanyId = envelope.CompanyId,
            BranchId = envelope.BranchId,
            ChangeType = "PostedOperationAccepted",
            EntityId = document.Id,
            OperationId = envelope.OperationId,
            SourceDeviceId = envelope.DeviceId,
            PayloadJson = "{}",
            ServerTimestampUtc = now
        };
        _db.SyncChanges.Add(change);
        AddAudit(envelope, "SyncAcceptPostedOperation", document.Id, payload.Document.DocumentType, "{}", envelope.PayloadJson, now);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var accepted = new AcceptedOperationSyncPayload(payload, allocation.Number, allocation.Sequence, now, change.ServerWatermark);
        change.PayloadJson = JsonSerializer.Serialize(accepted);
        var ack = new SyncAck(envelope.EnvelopeId, envelope.OperationId, envelope.IdempotencyKey, SyncAckStatus.Accepted,
            document.Id, allocation.Number, allocation.Sequence, now, change.ServerWatermark, null, null);

        _db.InboxMessages.Add(CreateInbox(envelope, ack, now));
        TouchPushWatermark(envelope.DeviceId, now);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ack;
    }

    private async Task<SyncAck> AcceptReversalAsync(SyncEnvelope envelope, PostedOperationSyncPayload payload, CancellationToken cancellationToken)
    {
        var original = await _db.FinancialDocuments.SingleOrDefaultAsync(x => x.Id == payload.Document.Id, cancellationToken).ConfigureAwait(false)
                       ?? throw new ServerSyncConflictException("ORIGINAL_DOCUMENT_MISSING", "The original posted document must reach the server before its reversal.");

        var originalJournalId = payload.Journal.ReversedEntryId!.Value;
        var originalJournal = await _db.JournalEntries.SingleOrDefaultAsync(x => x.Id == originalJournalId && x.SourceDocumentId == original.Id, cancellationToken).ConfigureAwait(false)
                             ?? throw new ServerSyncConflictException("ORIGINAL_JOURNAL_MISSING", "The journal targeted by the reversal does not exist.");

        if (await _db.DocumentReversals.AnyAsync(x => x.SourceDocumentId == original.Id, cancellationToken).ConfigureAwait(false))
            throw new ServerSyncConflictException("DOCUMENT_ALREADY_REVERSED", "The posted document already has a server reversal.");

        await ValidateReversalPairsAsync(payload, originalJournal, cancellationToken).ConfigureAwait(false);

        var now = _clock.UtcNow;
        AddJournalAndStock(envelope, payload, now);
        _db.DocumentReversals.Add(new ServerDocumentReversal
        {
            CompanyId = envelope.CompanyId,
            BranchId = envelope.BranchId,
            SourceDocumentId = original.Id,
            OriginalJournalEntryId = originalJournal.Id,
            ReversalJournalEntryId = payload.Journal.Id,
            OperationId = envelope.OperationId,
            IdempotencyKey = envelope.IdempotencyKey,
            CreatedAtUtc = now
        });

        var change = new SyncChange
        {
            CompanyId = envelope.CompanyId,
            BranchId = envelope.BranchId,
            ChangeType = "PostedOperationReversed",
            EntityId = original.Id,
            OperationId = envelope.OperationId,
            SourceDeviceId = envelope.DeviceId,
            PayloadJson = "{}",
            ServerTimestampUtc = now
        };
        _db.SyncChanges.Add(change);
        AddAudit(envelope, "SyncAcceptReversal", original.Id, original.DocumentType, "{}", envelope.PayloadJson, now);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var accepted = new AcceptedOperationSyncPayload(payload, original.OfficialSequenceNumber, original.OfficialSequence, now, change.ServerWatermark);
        change.PayloadJson = JsonSerializer.Serialize(accepted);
        var ack = new SyncAck(envelope.EnvelopeId, envelope.OperationId, envelope.IdempotencyKey, SyncAckStatus.Accepted,
            original.Id, original.OfficialSequenceNumber, original.OfficialSequence, now, change.ServerWatermark, null, null);
        _db.InboxMessages.Add(CreateInbox(envelope, ack, now));
        TouchPushWatermark(envelope.DeviceId, now);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ack;
    }

    private void AddJournalAndStock(SyncEnvelope envelope, PostedOperationSyncPayload payload, DateTimeOffset now)
    {
        _db.JournalEntries.Add(new ServerJournalEntry
        {
            Id = payload.Journal.Id,
            CompanyId = envelope.CompanyId,
            BranchId = envelope.BranchId,
            SourceDocumentId = payload.Journal.SourceDocumentId,
            SourceDocumentType = payload.Journal.SourceDocumentType,
            OperationId = envelope.OperationId,
            IdempotencyKey = envelope.IdempotencyKey,
            EntryDateUtc = payload.Journal.EntryDateUtc,
            Description = payload.Journal.Description,
            Reference = payload.Journal.Reference,
            ReversedEntryId = payload.Journal.ReversedEntryId,
            ServerTimestampUtc = now
        });

        foreach (var line in payload.Journal.Lines)
        {
            _db.JournalLines.Add(new ServerJournalLine
            {
                Id = line.Id,
                CompanyId = envelope.CompanyId,
                BranchId = envelope.BranchId,
                JournalEntryId = payload.Journal.Id,
                AccountId = line.AccountId,
                Debit = line.Debit,
                Credit = line.Credit,
                Memo = line.Memo
            });
        }

        foreach (var stock in payload.StockEntries)
        {
            _db.StockLedgerEntries.Add(new ServerStockLedgerEntry
            {
                Id = stock.Id,
                CompanyId = envelope.CompanyId,
                BranchId = envelope.BranchId,
                ItemId = stock.ItemId,
                WarehouseId = stock.WarehouseId,
                SourceDocumentId = stock.SourceDocumentId,
                SourceDocumentType = stock.SourceDocumentType,
                OperationId = envelope.OperationId,
                MovementType = stock.MovementType,
                QuantityDelta = stock.QuantityDelta,
                InventoryValueDelta = stock.InventoryValueDelta,
                UnitCost = stock.UnitCost,
                ReversedStockLedgerEntryId = stock.ReversedStockLedgerEntryId,
                PostedAtUtc = stock.PostedAtUtc,
                ServerTimestampUtc = now
            });
        }
    }

    private async Task ValidateJournalAsync(PostedOperationSyncPayload payload, CompanyPolicy policy, CancellationToken cancellationToken)
    {
        var journal = payload.Journal;
        if (journal.Lines.Count < 2) throw new AccountingDomainException("Server rejected journal: at least two lines are required.");
        if (journal.SourceDocumentId != payload.Document.Id) throw new AccountingDomainException("Server rejected journal: source document mismatch.");
        if (journal.CompanyId != _tenant.CompanyId || journal.BranchId != _tenant.BranchId) throw new UnauthorizedAccessException("Journal tenant mismatch.");

        var accountIds = journal.Lines.Select(x => x.AccountId).Distinct().ToArray();
        var accounts = await _db.Accounts.Where(x => accountIds.Contains(x.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (accounts.Count != accountIds.Length) throw new AccountingDomainException("Server rejected journal: one or more accounts do not exist.");

        decimal debit = 0m, credit = 0m;
        foreach (var line in journal.Lines)
        {
            if (line.Debit < 0m || line.Credit < 0m)
                throw new AccountingDomainException("Server rejected journal: negative debit/credit values are forbidden.");
            if ((line.Debit > 0m && line.Credit > 0m) || (line.Debit == 0m && line.Credit == 0m))
                throw new AccountingDomainException("Server rejected journal: exactly one side of every line must be positive.");
            var account = accounts.Single(x => x.Id == line.AccountId);
            if (!account.IsPostable || !account.IsActive)
                throw new AccountingDomainException($"Server rejected journal: account {account.Id} is not postable.");
            debit = _currency.Add(debit, line.Debit, policy);
            credit = _currency.Add(credit, line.Credit, policy);
        }

        if (_currency.RoundMoney(debit, policy) != _currency.RoundMoney(credit, policy))
            throw new UnbalancedJournalEntryException(debit, credit);
    }

    private async Task ValidateDocumentAccountingAsync(PostedOperationSyncPayload payload, CompanyPolicy policy, CancellationToken cancellationToken)
    {
        decimal Debit(Guid accountId) => _currency.RoundMoney(payload.Journal.Lines.Where(x => x.AccountId == accountId).Sum(x => x.Debit), policy);
        decimal Credit(Guid accountId) => _currency.RoundMoney(payload.Journal.Lines.Where(x => x.AccountId == accountId).Sum(x => x.Credit), policy);

        if (payload.Document.DocumentType is "ReceiptVoucher" or "PaymentVoucher")
        {
            if (!payload.Document.Amount.HasValue || payload.Document.Amount.Value <= 0m
                || !payload.Document.PrimaryAccountId.HasValue || !payload.Document.CounterAccountId.HasValue)
                throw new AccountingDomainException("Voucher sync payload is incomplete.");

            var amount = _currency.RoundMoney(payload.Document.Amount.Value, policy);
            if (payload.Document.DocumentType == "ReceiptVoucher")
            {
                if (Debit(payload.Document.PrimaryAccountId.Value) != amount || Credit(payload.Document.CounterAccountId.Value) != amount)
                    throw new AccountingDomainException("Receipt voucher journal does not match the source voucher amount/accounts.");
            }
            else
            {
                if (Credit(payload.Document.PrimaryAccountId.Value) != amount || Debit(payload.Document.CounterAccountId.Value) != amount)
                    throw new AccountingDomainException("Payment voucher journal does not match the source voucher amount/accounts.");
            }
            if (payload.StockEntries.Count != 0)
                throw new AccountingDomainException("Receipt/payment vouchers cannot create stock ledger entries.");
            return;
        }

        if (payload.Document.DocumentType is not ("SalesInvoice" or "PurchaseInvoice"))
            throw new AccountingDomainException($"Unsupported synchronized document type: {payload.Document.DocumentType}.");
        if (payload.Document.Lines.Count == 0)
            throw new AccountingDomainException("Invoice source lines are required for server validation.");
        if (!payload.Document.PrimaryAccountId.HasValue)
            throw new AccountingDomainException("Invoice settlement account is required.");

        var itemIds = payload.Document.Lines.Select(x => x.ItemId).Distinct().ToArray();
        var items = await _db.Items.Where(x => itemIds.Contains(x.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (items.Count != itemIds.Length) throw new AccountingDomainException("One or more invoice items do not exist on the server.");

        var moduleKey = payload.Document.ModuleKey.Trim().ToUpperInvariant();
        if (moduleKey == "SERVICES" && items.Any(x => x.ItemKind != ItemKind.Service))
            throw new AccountingDomainException("Services module documents cannot contain stock items.");
        if (moduleKey == "RETAIL")
        {
            if (items.Any(x => x.ItemKind != ItemKind.Stock))
                throw new AccountingDomainException("Retail module documents cannot contain service items.");
            if (policy.TaxPolicy.Enabled && !policy.TaxPolicy.PricesIncludeTax)
                throw new AccountingDomainException("Retail module requires tax-inclusive pricing when tax is enabled.");
        }
        if (moduleKey == "RESTAURANT")
        {
            if (payload.Document.DocumentType != "SalesInvoice")
                throw new AccountingDomainException("Restaurant module posts through SalesInvoice only.");
            using var restaurantMetadata = JsonDocument.Parse(payload.Document.ModuleMetadataJson);
            var root = restaurantMetadata.RootElement;
            JsonElement serviceTypeElement;
            if (!(root.TryGetProperty("serviceType", out serviceTypeElement) || root.TryGetProperty("ServiceType", out serviceTypeElement))
                || !serviceTypeElement.TryGetInt32(out var serviceType) || serviceType is < 1 or > 3)
                throw new AccountingDomainException("Restaurant service type DineIn/Takeaway/Delivery is required.");
        }

        decimal gross = 0m, taxTotal = 0m;
        var accountNet = new Dictionary<Guid, decimal>();
        var expectedStockQty = new Dictionary<Guid, decimal>();
        var expectedPurchaseValue = new Dictionary<Guid, decimal>();
        var expectedCogsByAccount = new Dictionary<Guid, decimal>();
        var expectedInventoryCreditByAccount = new Dictionary<Guid, decimal>();

        foreach (var line in payload.Document.Lines)
        {
            if (line.Quantity <= 0m || line.BaseUnitsPerUnit <= 0m || line.UnitPrice <= 0m)
                throw new AccountingDomainException("Invoice quantities, conversion factors and prices must be positive.");
            var item = items.Single(x => x.Id == line.ItemId);
            if (!item.IsActive) throw new AccountingDomainException($"Item {item.Id} is inactive.");
            var tax = Tax(line.Quantity, line.UnitPrice, policy);
            gross = _currency.Add(gross, tax.Gross, policy);
            taxTotal = _currency.Add(taxTotal, tax.Tax, policy);
            var baseQty = _currency.MultiplyQuantity(line.Quantity, line.BaseUnitsPerUnit, policy);

            if (payload.Document.DocumentType == "SalesInvoice")
            {
                accountNet[item.SalesRevenueAccountId] = accountNet.TryGetValue(item.SalesRevenueAccountId, out var net)
                    ? _currency.Add(net, tax.Net, policy)
                    : tax.Net;
                if (item.ItemKind == ItemKind.Stock)
                    expectedStockQty[item.Id] = expectedStockQty.TryGetValue(item.Id, out var qty)
                        ? _currency.AddQuantity(qty, baseQty, policy)
                        : baseQty;
            }
            else
            {
                var purchaseCost = policy.TaxPolicy.Enabled && policy.TaxPolicy.InputTaxAccountId.HasValue ? tax.Net : tax.Gross;
                var accountId = item.ItemKind == ItemKind.Stock ? item.InventoryAccountId : item.CogsAccountId;
                if (accountId == Guid.Empty) throw new AccountingDomainException($"Item {item.Sku} lacks its purchase cost account.");
                accountNet[accountId] = accountNet.TryGetValue(accountId, out var current)
                    ? _currency.Add(current, purchaseCost, policy)
                    : purchaseCost;
                if (item.ItemKind == ItemKind.Stock)
                {
                    expectedStockQty[item.Id] = expectedStockQty.TryGetValue(item.Id, out var qty)
                        ? _currency.AddQuantity(qty, baseQty, policy)
                        : baseQty;
                    expectedPurchaseValue[item.Id] = expectedPurchaseValue.TryGetValue(item.Id, out var value)
                        ? _currency.Add(value, purchaseCost, policy)
                        : purchaseCost;
                }
            }
        }

        var settlement = payload.Document.PrimaryAccountId.Value;
        if (payload.Document.DocumentType == "SalesInvoice")
        {
            if (Debit(settlement) != gross) throw new AccountingDomainException("Sales settlement debit does not equal source invoice gross.");
            foreach (var expected in accountNet)
                if (Credit(expected.Key) != _currency.RoundMoney(expected.Value, policy))
                    throw new AccountingDomainException($"Sales revenue for account {expected.Key} does not match source invoice lines.");
            if (taxTotal > 0m && policy.TaxPolicy.OutputTaxAccountId is Guid outputTax && Credit(outputTax) != taxTotal)
                throw new AccountingDomainException("Output tax journal line does not match the source invoice.");

            foreach (var expected in expectedStockQty)
            {
                if (!payload.Document.WarehouseId.HasValue) throw new AccountingDomainException("Stock sales require a warehouse.");
                var stockRows = payload.StockEntries.Where(x => x.ItemId == expected.Key && x.WarehouseId == payload.Document.WarehouseId.Value).ToArray();
                var actualQty = stockRows.Sum(x => x.QuantityDelta);
                if (_currency.RoundQuantity(actualQty, policy) != _currency.RoundQuantity(-expected.Value, policy))
                    throw new AccountingDomainException($"Sales stock quantity for item {expected.Key} does not match invoice source lines.");

                var serverLedger = await _db.StockLedgerEntries.AsNoTracking()
                    .Where(x => x.ItemId == expected.Key && x.WarehouseId == payload.Document.WarehouseId.Value)
                    .OrderBy(x => x.PostedAtUtc).ThenBy(x => x.Id)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                var valuationRows = serverLedger.Select(x => new StockLedgerValuationRow(
                    x.Id, x.ItemId, x.WarehouseId, x.QuantityDelta, x.InventoryValueDelta, x.UnitCost, x.PostedAtUtc)).ToArray();
                var valuation = new InventoryValuationEngine(_currency).CalculateIssue(valuationRows, expected.Value, policy,
                    (await _db.Warehouses.AsNoTracking().SingleAsync(x => x.Id == payload.Document.WarehouseId.Value, cancellationToken).ConfigureAwait(false)).AllowNegativeStockOverride ?? policy.AllowNegativeStock);

                var actualValue = _currency.RoundMoney(-stockRows.Sum(x => x.InventoryValueDelta), policy);
                if (actualValue != valuation.TotalCost)
                    throw new ServerSyncConflictException("INVENTORY_COST_CONFLICT",
                        $"Offline COGS for item {expected.Key} is {actualValue}, while authoritative server COGS is {valuation.TotalCost}.");

                var item = items.Single(x => x.Id == expected.Key);
                expectedCogsByAccount[item.CogsAccountId] = expectedCogsByAccount.TryGetValue(item.CogsAccountId, out var cogs)
                    ? _currency.Add(cogs, valuation.TotalCost, policy) : valuation.TotalCost;
                expectedInventoryCreditByAccount[item.InventoryAccountId] = expectedInventoryCreditByAccount.TryGetValue(item.InventoryAccountId, out var inventoryCredit)
                    ? _currency.Add(inventoryCredit, valuation.TotalCost, policy) : valuation.TotalCost;
            }

            foreach (var expected in expectedCogsByAccount)
                if (Debit(expected.Key) != _currency.RoundMoney(expected.Value, policy))
                    throw new ServerSyncConflictException("INVENTORY_COST_CONFLICT",
                        $"Offline journal COGS account {expected.Key} does not match authoritative server valuation.");
            foreach (var expected in expectedInventoryCreditByAccount)
                if (Credit(expected.Key) != _currency.RoundMoney(expected.Value, policy))
                    throw new ServerSyncConflictException("INVENTORY_COST_CONFLICT",
                        $"Offline inventory credit account {expected.Key} does not match authoritative server valuation.");

            if (moduleKey == "WHOLESALE")
            {
                await ValidateWholesalePricingAsync(payload, policy, cancellationToken).ConfigureAwait(false);
            }

            if (moduleKey == "WHOLESALE" && payload.Document.BusinessPartnerId is Guid partnerId)
            {
                var partner = await _db.BusinessPartners.AsNoTracking().SingleOrDefaultAsync(x => x.Id == partnerId, cancellationToken).ConfigureAwait(false)
                              ?? throw new AccountingDomainException("Wholesale customer does not exist.");
                if (settlement == partner.ControlAccountId)
                {
                    var account = await _db.Accounts.AsNoTracking().SingleAsync(x => x.Id == partner.ControlAccountId, cancellationToken).ConfigureAwait(false);
                    var sourceDocumentIds = _db.FinancialDocuments.AsNoTracking()
                        .Where(x => x.BusinessPartnerId == partner.Id)
                        .Select(x => x.Id);
                    var totals = await (from entry in _db.JournalEntries.AsNoTracking()
                                        join line in _db.JournalLines.AsNoTracking() on entry.Id equals line.JournalEntryId
                                        where sourceDocumentIds.Contains(entry.SourceDocumentId) && line.AccountId == partner.ControlAccountId
                                        group line by 1 into g
                                        select new { Debit = g.Sum(x => x.Debit), Credit = g.Sum(x => x.Credit) })
                        .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                    var debitTotal = totals?.Debit ?? 0m;
                    var creditTotal = totals?.Credit ?? 0m;
                    var currentExposure = account.NormalBalance == NormalBalance.Debit
                        ? _currency.Subtract(debitTotal, creditTotal, policy)
                        : _currency.Subtract(creditTotal, debitTotal, policy);
                    var exposureAfterInvoice = _currency.Add(Math.Max(0m, currentExposure), gross, policy);
                    if (exposureAfterInvoice > partner.CreditLimit)
                        throw new ServerSyncConflictException("CREDIT_LIMIT_CONFLICT",
                            $"Wholesale customer credit limit exceeded. Limit={partner.CreditLimit}, exposure={exposureAfterInvoice}.");
                }
            }
        }
        else
        {
            if (Credit(settlement) != gross) throw new AccountingDomainException("Purchase settlement credit does not equal source invoice gross.");
            foreach (var expected in accountNet)
                if (Debit(expected.Key) != _currency.RoundMoney(expected.Value, policy))
                    throw new AccountingDomainException($"Purchase debit for account {expected.Key} does not match source invoice lines.");
            if (taxTotal > 0m && policy.TaxPolicy.InputTaxAccountId is Guid inputTax && Debit(inputTax) != taxTotal)
                throw new AccountingDomainException("Input tax journal line does not match the source invoice.");

            foreach (var expected in expectedStockQty)
            {
                if (!payload.Document.WarehouseId.HasValue) throw new AccountingDomainException("Stock purchases require a warehouse.");
                var stockRows = payload.StockEntries.Where(x => x.ItemId == expected.Key && x.WarehouseId == payload.Document.WarehouseId.Value).ToArray();
                if (_currency.RoundQuantity(stockRows.Sum(x => x.QuantityDelta), policy) != _currency.RoundQuantity(expected.Value, policy))
                    throw new AccountingDomainException($"Purchase stock quantity for item {expected.Key} does not match source lines.");
                if (_currency.RoundMoney(stockRows.Sum(x => x.InventoryValueDelta), policy) != _currency.RoundMoney(expectedPurchaseValue[expected.Key], policy))
                    throw new AccountingDomainException($"Purchase inventory value for item {expected.Key} does not match source lines.");
            }
        }

        var stockItemIds = expectedStockQty.Keys.ToHashSet();
        if (payload.StockEntries.Any(x => !stockItemIds.Contains(x.ItemId)))
            throw new AccountingDomainException("Sync payload contains stock entries not supported by the source invoice lines.");
    }

    private async Task ValidateWholesalePricingAsync(
        PostedOperationSyncPayload payload,
        CompanyPolicy policy,
        CancellationToken cancellationToken)
    {
        var configuration = await _db.ModuleConfigurations.AsNoTracking()
            .Where(x => x.ModuleKey == "WHOLESALE")
            .Select(x => x.ConfigurationJson)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(configuration) || configuration == "{}") return;

        using var document = JsonDocument.Parse(configuration);
        if (!TryGetPropertyIgnoreCase(document.RootElement, "priceTiers", out var tiersElement)
            || tiersElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var line in payload.Document.Lines)
        {
            var baseQuantity = _currency.MultiplyQuantity(line.Quantity, line.BaseUnitsPerUnit, policy);
            decimal? selectedPrice = null;
            decimal selectedMinimum = -1m;

            foreach (var tier in tiersElement.EnumerateArray())
            {
                if (!TryGetPropertyIgnoreCase(tier, "itemId", out var itemElement)
                    || !Guid.TryParse(itemElement.GetString(), out var itemId)
                    || itemId != line.ItemId
                    || !TryGetPropertyIgnoreCase(tier, "minBaseQuantity", out var minimumElement)
                    || !minimumElement.TryGetDecimal(out var minimum)
                    || minimum <= 0m
                    || minimum > baseQuantity
                    || !TryGetPropertyIgnoreCase(tier, "unitPrice", out var priceElement)
                    || !priceElement.TryGetDecimal(out var price)
                    || price < 0m)
                    continue;

                if (minimum > selectedMinimum)
                {
                    selectedMinimum = minimum;
                    selectedPrice = price;
                }
            }

            if (selectedPrice.HasValue
                && _currency.RoundMoney(line.UnitPrice, policy) != _currency.RoundMoney(selectedPrice.Value, policy))
            {
                throw new AccountingDomainException(
                    $"Wholesale line price for item {line.ItemId} must match the authoritative tier price {selectedPrice.Value} at base quantity {baseQuantity}.");
            }
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private (decimal Net, decimal Tax, decimal Gross) Tax(decimal quantity, decimal unitPrice, CompanyPolicy policy)
    {
        var raw = _currency.Multiply(quantity, unitPrice, policy);
        if (!policy.TaxPolicy.Enabled || policy.TaxPolicy.Rate == 0m) return (raw, 0m, raw);
        if (policy.TaxPolicy.PricesIncludeTax)
        {
            var net = _currency.Divide(raw, 1m + policy.TaxPolicy.Rate, policy);
            return (net, _currency.Subtract(raw, net, policy), raw);
        }
        var tax = _currency.Multiply(raw, policy.TaxPolicy.Rate, policy);
        return (raw, tax, _currency.Add(raw, tax, policy));
    }

    private async Task ValidateStockAsync(PostedOperationSyncPayload payload, CompanyPolicy policy, CancellationToken cancellationToken)
    {
        foreach (var row in payload.StockEntries)
        {
            if (row.SourceDocumentId != payload.Document.Id || row.OperationId != payload.Journal.OperationId)
                throw new AccountingDomainException("Server rejected stock entry: document/operation mismatch.");
            if (row.QuantityDelta == 0m || row.UnitCost < 0m)
                throw new AccountingDomainException("Server rejected stock entry: invalid quantity or unit cost.");
        }

        foreach (var group in payload.StockEntries.GroupBy(x => new { x.ItemId, x.WarehouseId }))
        {
            var item = await _db.Items.SingleOrDefaultAsync(x => x.Id == group.Key.ItemId, cancellationToken).ConfigureAwait(false)
                       ?? throw new AccountingDomainException($"Server rejected stock entry: item {group.Key.ItemId} does not exist.");
            if (item.ItemKind != ItemKind.Stock) throw new AccountingDomainException("Service items cannot create stock ledger entries.");

            var warehouse = await _db.Warehouses.SingleOrDefaultAsync(x => x.Id == group.Key.WarehouseId, cancellationToken).ConfigureAwait(false)
                            ?? throw new AccountingDomainException($"Server rejected stock entry: warehouse {group.Key.WarehouseId} does not exist.");

            var allowNegative = warehouse.AllowNegativeStockOverride ?? policy.AllowNegativeStock;
            var quantity = await _db.StockLedgerEntries
                .Where(x => x.ItemId == group.Key.ItemId && x.WarehouseId == group.Key.WarehouseId)
                .SumAsync(x => (decimal?)x.QuantityDelta, cancellationToken).ConfigureAwait(false) ?? 0m;

            foreach (var row in group.OrderBy(x => x.PostedAtUtc).ThenBy(x => x.Id))
            {
                quantity = _currency.AddQuantity(quantity, row.QuantityDelta, policy);
                if (!allowNegative && quantity < 0m)
                    throw new ServerSyncConflictException("NEGATIVE_STOCK_CONFLICT",
                        $"Server stock is insufficient for item {group.Key.ItemId} in warehouse {group.Key.WarehouseId}. The offline operation requires resolution.");
            }
        }
    }

    private async Task ValidateReversalPairsAsync(PostedOperationSyncPayload payload, ServerJournalEntry originalJournal, CancellationToken cancellationToken)
    {
        var originalLines = await _db.JournalLines.Where(x => x.JournalEntryId == originalJournal.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var originalByAccount = originalLines.GroupBy(x => x.AccountId).ToDictionary(
            x => x.Key,
            x => (Debit: x.Sum(v => v.Debit), Credit: x.Sum(v => v.Credit)));
        var reversalByAccount = payload.Journal.Lines.GroupBy(x => x.AccountId).ToDictionary(
            x => x.Key,
            x => (Debit: x.Sum(v => v.Debit), Credit: x.Sum(v => v.Credit)));

        if (originalByAccount.Count != reversalByAccount.Count)
            throw new AccountingDomainException("Reversal journal does not mirror the original journal.");

        foreach (var pair in originalByAccount)
        {
            if (!reversalByAccount.TryGetValue(pair.Key, out var reversal)
                || pair.Value.Debit != reversal.Credit
                || pair.Value.Credit != reversal.Debit)
                throw new AccountingDomainException("Reversal journal does not exactly invert the original journal.");
        }

        foreach (var row in payload.StockEntries)
        {
            if (!row.ReversedStockLedgerEntryId.HasValue)
                throw new AccountingDomainException("Reversal stock entries must reference the original stock ledger entry.");

            var original = await _db.StockLedgerEntries.SingleOrDefaultAsync(x => x.Id == row.ReversedStockLedgerEntryId.Value, cancellationToken).ConfigureAwait(false)
                           ?? throw new AccountingDomainException("Original stock ledger entry for reversal was not found.");

            if (original.QuantityDelta != -row.QuantityDelta || original.InventoryValueDelta != -row.InventoryValueDelta)
                throw new AccountingDomainException("Reversal stock entry must exactly invert quantity and inventory value.");
        }
    }

    private static void ValidateEnvelope(PostedOperationSyncPayload payload, SyncEnvelope envelope)
    {
        if (payload.Document.CompanyId != envelope.CompanyId || payload.Document.BranchId != envelope.BranchId)
            throw new UnauthorizedAccessException("Payload document tenant mismatch.");
        if (payload.Journal.CompanyId != envelope.CompanyId || payload.Journal.BranchId != envelope.BranchId)
            throw new UnauthorizedAccessException("Payload journal tenant mismatch.");
        if (payload.Journal.SourceDocumentId != payload.Document.Id)
            throw new AccountingDomainException("Journal source document does not match document.");
        if (payload.Journal.OperationId != envelope.OperationId)
            throw new AccountingDomainException("Journal OperationId does not match envelope.");
        if (!string.Equals(payload.Journal.IdempotencyKey, envelope.IdempotencyKey, StringComparison.Ordinal))
            throw new AccountingDomainException("Journal IdempotencyKey does not match envelope.");
        if (!UuidV7.IsVersion7(envelope.OperationId))
            throw new AccountingDomainException("OperationId must be UUIDv7.");
    }

    private async Task<CompanyPolicy> GetPolicyAsync(CancellationToken cancellationToken)
    {
        var policy = await _db.CompanyPolicies.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("Server company policy is not configured.");
        return policy.ToPolicy();
    }

    private async Task DemandActiveDeviceAsync(CancellationToken cancellationToken)
    {
        var device = await _db.DeviceRegistrations.SingleOrDefaultAsync(x => x.DeviceId == _tenant.DeviceId, cancellationToken).ConfigureAwait(false)
                     ?? throw new UnauthorizedAccessException("Device is not registered.");
        if (!device.IsActive) throw new UnauthorizedAccessException("Device is disabled.");
        if (device.ClientSchemaVersion != SyncProtocol.CurrentClientSchemaVersion || device.ProtocolVersion != SyncProtocol.CurrentProtocolVersion)
            throw new SyncProtocolMismatchException(device.ClientSchemaVersion, device.ProtocolVersion);
        device.LastSeenAtUtc = _clock.UtcNow;
    }

    private async Task<SyncAck> StoreConflictAsync(SyncEnvelope envelope, Guid documentId, string code, string message, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var conflict = new ServerSyncConflict
        {
            CompanyId = envelope.CompanyId,
            BranchId = envelope.BranchId,
            OperationId = envelope.OperationId,
            DocumentId = documentId,
            DeviceId = envelope.DeviceId,
            ConflictCode = code,
            Message = message,
            OriginalPayloadJson = envelope.PayloadJson,
            CreatedAtUtc = now
        };
        _db.SyncConflicts.Add(conflict);
        var change = new SyncChange
        {
            CompanyId = envelope.CompanyId,
            BranchId = envelope.BranchId,
            ChangeType = "SyncConflictRequiresResolution",
            EntityId = documentId,
            OperationId = envelope.OperationId,
            SourceDeviceId = envelope.DeviceId,
            PayloadJson = "{}",
            ServerTimestampUtc = now
        };
        _db.SyncChanges.Add(change);
        AddAudit(envelope, "SyncConflictRequiresResolution", documentId, "SyncConflict", "{}", envelope.PayloadJson, now);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var conflictPayload = new SyncConflictPayload(conflict.Id, envelope.OperationId, documentId, code, message, envelope.PayloadJson, now);
        change.PayloadJson = JsonSerializer.Serialize(conflictPayload);
        var ack = new SyncAck(envelope.EnvelopeId, envelope.OperationId, envelope.IdempotencyKey, SyncAckStatus.ConflictRequiresResolution,
            documentId == Guid.Empty ? null : documentId, null, null, now, change.ServerWatermark, code, message);
        _db.InboxMessages.Add(CreateInbox(envelope, ack, now));
        TouchPushWatermark(envelope.DeviceId, now);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ack;
    }

    private async Task<SyncAck> StoreRejectedAsync(SyncEnvelope envelope, string message, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var watermark = await CurrentWatermarkAsync(cancellationToken).ConfigureAwait(false);
        var ack = new SyncAck(envelope.EnvelopeId, envelope.OperationId, envelope.IdempotencyKey, SyncAckStatus.Rejected,
            null, null, null, now, watermark, "SERVER_VALIDATION_REJECTED", message);
        AddAudit(envelope, "SyncRejected", Guid.Empty, "SyncEnvelope", "{}", envelope.PayloadJson, now);
        _db.InboxMessages.Add(CreateInbox(envelope, ack, now));
        TouchPushWatermark(envelope.DeviceId, now);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ack;
    }

    private InboxMessage CreateInbox(SyncEnvelope envelope, SyncAck ack, DateTimeOffset now) => new()
    {
        CompanyId = envelope.CompanyId,
        BranchId = envelope.BranchId,
        DeviceId = envelope.DeviceId,
        EnvelopeId = envelope.EnvelopeId,
        OperationId = envelope.OperationId,
        IdempotencyKey = envelope.IdempotencyKey,
        EventType = envelope.EventType,
        ResultJson = JsonSerializer.Serialize(ack),
        ProcessedAtUtc = now
    };

    private void AddAudit(SyncEnvelope envelope, string action, Guid entityId, string entityType, string beforeJson, string afterJson, DateTimeOffset now)
    {
        _db.AuditLogs.Add(new ServerAuditLog
        {
            CompanyId = envelope.CompanyId,
            BranchId = envelope.BranchId,
            UserId = _tenant.UserId,
            DeviceId = envelope.DeviceId,
            Action = action,
            EntityId = entityId,
            EntityType = entityType,
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            TimestampUtc = now
        });
    }

    private void TouchPushWatermark(Guid deviceId, DateTimeOffset now)
    {
        var tracked = _db.SyncWatermarks.Local.SingleOrDefault(x => x.DeviceId == deviceId);
        if (tracked is not null)
        {
            tracked.LastPushAtUtc = now;
            return;
        }

        var existing = _db.SyncWatermarks.SingleOrDefault(x => x.DeviceId == deviceId);
        if (existing is null)
        {
            _db.SyncWatermarks.Add(new SyncWatermark
            {
                CompanyId = _tenant.CompanyId,
                BranchId = _tenant.BranchId,
                DeviceId = deviceId,
                LastPushAtUtc = now
            });
        }
        else
        {
            existing.LastPushAtUtc = now;
        }
    }

    private Task<long> CurrentWatermarkAsync(CancellationToken cancellationToken) =>
        _db.SyncChanges.Select(x => (long?)x.ServerWatermark).MaxAsync(cancellationToken)
            .ContinueWith(x => x.Result ?? 0L, cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static Guid TryDocumentId(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<PostedOperationSyncPayload>(json)?.Document.Id ?? Guid.Empty;
        }
        catch (JsonException)
        {
            return Guid.Empty;
        }
    }
}

public sealed class SyncPullQueryHandler
{
    private readonly ServerAccountingDbContext _db;
    private readonly IServerTenantContext _tenant;
    private readonly TenantPostgresSession _session;
    private readonly IClock _clock;

    public SyncPullQueryHandler(ServerAccountingDbContext db, IServerTenantContext tenant, TenantPostgresSession session, IClock clock)
    {
        _db = db;
        _tenant = tenant;
        _session = session;
        _clock = clock;
    }

    public async Task<SyncPullResult> HandleAsync(SyncPullRequest request, CancellationToken cancellationToken = default)
    {
        SyncProtocol.EnsureCompatible(request.ClientSchemaVersion, request.ProtocolVersion);
        if (!_tenant.IsAuthenticated || _tenant.IsSystem) throw new UnauthorizedAccessException("Authenticated tenant context is required.");
        if (request.DeviceId != _tenant.DeviceId) throw new UnauthorizedAccessException("JWT DeviceId does not match pull request.");
        var take = Math.Clamp(request.Take, 1, SyncProtocol.MaxBatchSize);

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await _session.ApplyAsync(cancellationToken).ConfigureAwait(false);

        var device = await _db.DeviceRegistrations.SingleOrDefaultAsync(x => x.DeviceId == request.DeviceId, cancellationToken).ConfigureAwait(false)
                     ?? throw new UnauthorizedAccessException("Device is not registered.");
        if (!device.IsActive) throw new UnauthorizedAccessException("Device is disabled.");

        var scanned = await _db.SyncChanges.AsNoTracking()
            .Where(x => x.ServerWatermark > request.AfterWatermark)
            .OrderBy(x => x.ServerWatermark)
            .Take(take + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var hasMore = scanned.Count > take;
        var page = scanned.Take(take).ToArray();
        var next = page.Length == 0 ? request.AfterWatermark : page[^1].ServerWatermark;
        var changes = page
            .Where(x => x.SourceDeviceId != request.DeviceId)
            .Select(x => new SyncDelta(x.ServerWatermark, x.ChangeType, x.EntityId, x.OperationId, x.SourceDeviceId, x.PayloadJson, x.ServerTimestampUtc))
            .ToArray();

        var watermark = await _db.SyncWatermarks.SingleOrDefaultAsync(x => x.DeviceId == request.DeviceId, cancellationToken).ConfigureAwait(false);
        if (watermark is null)
        {
            watermark = new SyncWatermark
            {
                CompanyId = _tenant.CompanyId,
                BranchId = _tenant.BranchId,
                DeviceId = request.DeviceId
            };
            _db.SyncWatermarks.Add(watermark);
        }
        watermark.LastPulledWatermark = Math.Max(watermark.LastPulledWatermark, next);
        watermark.LastPullAtUtc = _clock.UtcNow;
        device.LastSeenAtUtc = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new SyncPullResult(SyncProtocol.CurrentClientSchemaVersion, SyncProtocol.CurrentProtocolVersion, _clock.UtcNow, next, hasMore, changes);
    }
}

public sealed class DeviceRegistrationService
{
    private readonly ServerAccountingDbContext _db;
    private readonly IServerTenantContext _tenant;
    private readonly TenantPostgresSession _session;
    private readonly IClock _clock;

    public DeviceRegistrationService(ServerAccountingDbContext db, IServerTenantContext tenant, TenantPostgresSession session, IClock clock)
    {
        _db = db;
        _tenant = tenant;
        _session = session;
        _clock = clock;
    }

    public async Task<DeviceRegistration> RegisterAsync(DeviceRegistrationRequest request, CancellationToken cancellationToken = default)
    {
        SyncProtocol.EnsureCompatible(request.ClientSchemaVersion, request.ProtocolVersion);
        if (request.DeviceId != _tenant.DeviceId) throw new UnauthorizedAccessException("JWT DeviceId must match registered DeviceId.");

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await _session.ApplyAsync(cancellationToken).ConfigureAwait(false);
        var existing = await _db.DeviceRegistrations.SingleOrDefaultAsync(x => x.DeviceId == request.DeviceId, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;
        if (existing is null)
        {
            existing = new DeviceRegistration
            {
                CompanyId = _tenant.CompanyId,
                BranchId = _tenant.BranchId,
                DeviceId = request.DeviceId,
                RegisteredAtUtc = now,
                IsActive = true
            };
            _db.DeviceRegistrations.Add(existing);
        }

        existing.DeviceName = string.IsNullOrWhiteSpace(request.DeviceName) ? request.DeviceId.ToString("N") : request.DeviceName.Trim();
        existing.Platform = request.Platform.Trim();
        existing.ClientVersion = request.ClientVersion.Trim();
        existing.ClientSchemaVersion = request.ClientSchemaVersion;
        existing.ProtocolVersion = request.ProtocolVersion;
        existing.LastSeenAtUtc = now;
        _db.AuditLogs.Add(new ServerAuditLog
        {
            CompanyId = _tenant.CompanyId,
            BranchId = _tenant.BranchId,
            UserId = _tenant.UserId,
            DeviceId = _tenant.DeviceId,
            Action = "RegisterOrRefreshDevice",
            EntityId = existing.Id,
            EntityType = nameof(DeviceRegistration),
            BeforeJson = "{}",
            AfterJson = JsonSerializer.Serialize(new
            {
                existing.DeviceId,
                existing.DeviceName,
                existing.Platform,
                existing.ClientVersion,
                existing.ClientSchemaVersion,
                existing.ProtocolVersion,
                existing.IsActive
            }),
            TimestampUtc = now
        });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return existing;
    }

    public async Task<DeviceRegistration> SetActiveAsync(Guid deviceId, bool isActive, CancellationToken cancellationToken = default)
    {
        if (!_tenant.RoleClaims.Contains("tenant.admin"))
            throw new UnauthorizedAccessException("tenant.admin permission is required to change device state.");

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await _session.ApplyAsync(cancellationToken).ConfigureAwait(false);
        var device = await _db.DeviceRegistrations.SingleOrDefaultAsync(
            x => x.DeviceId == deviceId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Device not found.");

        var before = JsonSerializer.Serialize(new { device.DeviceId, device.IsActive });
        device.IsActive = isActive;
        device.LastSeenAtUtc = _clock.UtcNow;
        _db.AuditLogs.Add(new ServerAuditLog
        {
            CompanyId = _tenant.CompanyId,
            BranchId = _tenant.BranchId,
            UserId = _tenant.UserId,
            DeviceId = _tenant.DeviceId,
            Action = isActive ? "EnableDevice" : "DisableDevice",
            EntityId = device.Id,
            EntityType = nameof(DeviceRegistration),
            BeforeJson = before,
            AfterJson = JsonSerializer.Serialize(new { device.DeviceId, device.IsActive }),
            TimestampUtc = _clock.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return device;
    }
}
