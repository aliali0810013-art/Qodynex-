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

    private async Task<SyncAck> ProcessMessageAsync(SyncEnvelope envelope, CancellationToken cancellationToken)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await _session.ApplyAsync(cancellationToken).ConfigureAwait(false);
        await DemandActiveDeviceAsync(cancellationToken).ConfigureAwait(false);

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
            LocalTempNumber = payload.Document.LocalTempNumber,
            OfficialSequenceNumber = allocation.Number,
            OfficialSequence = allocation.Sequence,
            DocumentDateUtc = payload.Document.DocumentDateUtc,
            OperationId = envelope.OperationId,
            IdempotencyKey = envelope.IdempotencyKey,
            SourceDeviceId = envelope.DeviceId,
            ServerTimestampUtc = now
        };
        _db.FinancialDocuments.Add(document);
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

public sealed record DeviceRegistrationRequest(
    Guid DeviceId,
    string DeviceName,
    string Platform,
    string ClientVersion,
    int ClientSchemaVersion,
    string ProtocolVersion);

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
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return existing;
    }
}
