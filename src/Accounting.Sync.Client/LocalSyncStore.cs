using System.Data;
using System.Text.Json;
using Accounting.Domain;
using Accounting.Infrastructure.Local;
using Accounting.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Sync.Client;

public sealed record LocalSyncStatus(
    int PendingOutbox,
    int RetryableFailures,
    int UnresolvedConflicts,
    long LastPulledWatermark,
    DateTimeOffset? LastSuccessfulSyncUtc,
    string? LastError);

public sealed class LocalSyncStore
{
    private readonly LocalAccountingDbContext _db;

    public LocalSyncStore(LocalAccountingDbContext db) => _db = db;

    public async Task<IReadOnlyList<SyncEnvelope>> GetPendingEnvelopesAsync(UserContext user, int take, CancellationToken cancellationToken = default)
    {
        DemandTenant(user);
        // Stable, index-backed LIMIT is executed inside SQLite, before materialization.
        var messages = await _db.OutboxMessages.AsNoTracking()
            .Where(x => x.CompanyId == user.CompanyId && x.BranchId == user.BranchId
                && (x.Status == OutboxStatus.Pending || x.Status == OutboxStatus.Failed))
            .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .Take(Math.Clamp(take, 1, SyncProtocol.MaxBatchSize))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return messages.Select(x => new SyncEnvelope(
            x.Id,
            x.CompanyId,
            x.BranchId,
            user.DeviceId,
            x.OperationId,
            x.IdempotencyKey,
            x.EventType,
            x.PayloadJson,
            x.CreatedAtUtc)).ToArray();
    }

    public async Task<long> GetLastPulledWatermarkAsync(UserContext user, CancellationToken cancellationToken = default)
    {
        DemandTenant(user);
        return await _db.LocalSyncStates.AsNoTracking()
            .Where(x => x.CompanyId == user.CompanyId && x.BranchId == user.BranchId && x.DeviceId == user.DeviceId)
            .Select(x => (long?)x.LastPulledWatermark)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? 0L;
    }

    public async Task<int> GetUnresolvedConflictCountAsync(UserContext user, CancellationToken cancellationToken = default)
    {
        DemandTenant(user);
        return await _db.LocalSyncConflicts.AsNoTracking()
            .CountAsync(x => x.CompanyId == user.CompanyId && x.BranchId == user.BranchId && !x.IsResolved, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<LocalSyncStatus> GetStatusAsync(UserContext user, CancellationToken cancellationToken = default)
    {
        DemandTenant(user);
        var state = await _db.LocalSyncStates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyId == user.CompanyId && x.BranchId == user.BranchId && x.DeviceId == user.DeviceId, cancellationToken)
            .ConfigureAwait(false);
        var pending = await _db.OutboxMessages.AsNoTracking()
            .CountAsync(x => x.CompanyId == user.CompanyId && x.BranchId == user.BranchId && x.Status == OutboxStatus.Pending, cancellationToken)
            .ConfigureAwait(false);
        var failed = await _db.OutboxMessages.AsNoTracking()
            .CountAsync(x => x.CompanyId == user.CompanyId && x.BranchId == user.BranchId && x.Status == OutboxStatus.Failed, cancellationToken)
            .ConfigureAwait(false);
        var conflicts = await GetUnresolvedConflictCountAsync(user, cancellationToken).ConfigureAwait(false);
        return new LocalSyncStatus(pending, failed, conflicts, state?.LastPulledWatermark ?? 0L, state?.LastSuccessfulSyncUtc, state?.LastError);
    }

    public async Task ApplyBootstrapAsync(UserContext user, TenantBootstrapResult bootstrap, CancellationToken cancellationToken = default)
    {
        DemandTenant(user);
        SyncProtocol.EnsureCompatible(bootstrap.ServerSchemaVersion, bootstrap.ProtocolVersion);
        if (bootstrap.CompanyId != user.CompanyId || bootstrap.BranchId != user.BranchId)
            throw new UnauthorizedAccessException("Bootstrap payload belongs to a different tenant.");

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await ApplyBootstrapCoreAsync(user, bootstrap, updateSecurity: true, cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyBootstrapCoreAsync(UserContext user, TenantBootstrapResult bootstrap, bool updateSecurity, CancellationToken cancellationToken)
    {
        var p = bootstrap.Policy;
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO Companies
(Id, CompanyId, BranchId, Name, BaseCurrencyCode, CurrencyScale, RoundingMode, QuantityScale, InventoryValuationMethod,
 AllowNegativeStock, TaxEnabled, TaxRate, PricesIncludeTax, OutputTaxAccountId, InputTaxAccountId)
VALUES
({bootstrap.CompanyId}, {bootstrap.CompanyId}, {Guid.Empty}, {bootstrap.CompanyName}, {p.BaseCurrencyCode}, {p.CurrencyScale},
 {p.RoundingMode}, {p.QuantityScale}, {p.InventoryValuationMethod}, {p.AllowNegativeStock}, {p.TaxEnabled}, {p.TaxRate},
 {p.PricesIncludeTax}, {p.OutputTaxAccountId}, {p.InputTaxAccountId})
ON CONFLICT(Id) DO UPDATE SET
 Name=excluded.Name, BaseCurrencyCode=excluded.BaseCurrencyCode, CurrencyScale=excluded.CurrencyScale,
 RoundingMode=excluded.RoundingMode, QuantityScale=excluded.QuantityScale, InventoryValuationMethod=excluded.InventoryValuationMethod,
 AllowNegativeStock=excluded.AllowNegativeStock, TaxEnabled=excluded.TaxEnabled, TaxRate=excluded.TaxRate,
 PricesIncludeTax=excluded.PricesIncludeTax, OutputTaxAccountId=excluded.OutputTaxAccountId, InputTaxAccountId=excluded.InputTaxAccountId;
""", cancellationToken).ConfigureAwait(false);

        await _db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO Branches (Id, CompanyId, BranchId, Code, Name)
VALUES ({bootstrap.BranchId}, {bootstrap.CompanyId}, {bootstrap.BranchId}, {bootstrap.BranchCode}, {bootstrap.BranchName})
ON CONFLICT(Id) DO UPDATE SET Code=excluded.Code, Name=excluded.Name;
""", cancellationToken).ConfigureAwait(false);

        foreach (var row in bootstrap.Accounts)
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO Accounts
(Id, CompanyId, BranchId, Code, Name, ParentAccountId, AccountType, NormalBalance, IsPostable, IsActive)
VALUES ({row.Id}, {bootstrap.CompanyId}, {bootstrap.BranchId}, {row.Code}, {row.Name}, {row.ParentAccountId},
 {row.AccountType}, {row.NormalBalance}, {row.IsPostable}, {row.IsActive})
ON CONFLICT(Id) DO UPDATE SET
 Code=excluded.Code, Name=excluded.Name, ParentAccountId=excluded.ParentAccountId, AccountType=excluded.AccountType,
 NormalBalance=excluded.NormalBalance, IsPostable=excluded.IsPostable, IsActive=excluded.IsActive;
""", cancellationToken).ConfigureAwait(false);
        }

        foreach (var row in bootstrap.Units)
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO UnitOfMeasures (Id, CompanyId, BranchId, Code, Name)
VALUES ({row.Id}, {bootstrap.CompanyId}, {bootstrap.BranchId}, {row.Code}, {row.Name})
ON CONFLICT(Id) DO UPDATE SET Code=excluded.Code, Name=excluded.Name;
""", cancellationToken).ConfigureAwait(false);
        }

        foreach (var row in bootstrap.Partners)
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO BusinessPartners
(Id, CompanyId, BranchId, Code, Name, PartnerType, ControlAccountId, Phone, CreditLimit, IsActive)
VALUES ({row.Id}, {bootstrap.CompanyId}, {bootstrap.BranchId}, {row.Code}, {row.Name}, {row.PartnerType},
 {row.ControlAccountId}, {row.Phone}, {row.CreditLimit}, {row.IsActive})
ON CONFLICT(Id) DO UPDATE SET
 Code=excluded.Code, Name=excluded.Name, PartnerType=excluded.PartnerType, ControlAccountId=excluded.ControlAccountId,
 Phone=excluded.Phone, CreditLimit=excluded.CreditLimit, IsActive=excluded.IsActive;
""", cancellationToken).ConfigureAwait(false);
        }

        foreach (var row in bootstrap.Items)
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO Items
(Id, CompanyId, BranchId, Sku, Name, Barcode, BaseUnitOfMeasureId, InventoryAccountId, SalesRevenueAccountId, CogsAccountId, IsActive, ItemKind)
VALUES ({row.Id}, {bootstrap.CompanyId}, {bootstrap.BranchId}, {row.Sku}, {row.Name}, {row.Barcode}, {row.BaseUnitOfMeasureId},
 {row.InventoryAccountId}, {row.SalesRevenueAccountId}, {row.CogsAccountId}, {row.IsActive}, {row.ItemKind})
ON CONFLICT(Id) DO UPDATE SET
 Sku=excluded.Sku, Name=excluded.Name, Barcode=excluded.Barcode, BaseUnitOfMeasureId=excluded.BaseUnitOfMeasureId,
 InventoryAccountId=excluded.InventoryAccountId, SalesRevenueAccountId=excluded.SalesRevenueAccountId,
 CogsAccountId=excluded.CogsAccountId, IsActive=excluded.IsActive, ItemKind=excluded.ItemKind;
""", cancellationToken).ConfigureAwait(false);
        }

        foreach (var row in bootstrap.ItemUnitConversions)
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO ItemUnitConversions
(Id, CompanyId, BranchId, ItemId, UnitOfMeasureId, BaseUnitsPerUnit)
VALUES ({row.Id}, {bootstrap.CompanyId}, {bootstrap.BranchId}, {row.ItemId}, {row.UnitOfMeasureId}, {row.BaseUnitsPerUnit})
ON CONFLICT(Id) DO UPDATE SET
 ItemId=excluded.ItemId, UnitOfMeasureId=excluded.UnitOfMeasureId, BaseUnitsPerUnit=excluded.BaseUnitsPerUnit;
""", cancellationToken).ConfigureAwait(false);
        }

        foreach (var row in bootstrap.Warehouses)
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO Warehouses
(Id, CompanyId, BranchId, Code, Name, AllowNegativeStockOverride)
VALUES ({row.Id}, {bootstrap.CompanyId}, {bootstrap.BranchId}, {row.Code}, {row.Name}, {row.AllowNegativeStockOverride})
ON CONFLICT(Id) DO UPDATE SET Code=excluded.Code, Name=excluded.Name, AllowNegativeStockOverride=excluded.AllowNegativeStockOverride;
""", cancellationToken).ConfigureAwait(false);
        }

        foreach (var module in bootstrap.ModuleConfigurations)
        {
            var key = module.ModuleKey.Trim().ToUpperInvariant();
            var row = await _db.LocalModuleConfigurations.SingleOrDefaultAsync(
                x => x.CompanyId == user.CompanyId && x.BranchId == user.BranchId && x.ModuleKey == key,
                cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                row = new LocalModuleConfiguration
                {
                    Id = UuidV7.NewGuid(),
                    CompanyId = user.CompanyId,
                    BranchId = user.BranchId,
                    ModuleKey = key
                };
                _db.LocalModuleConfigurations.Add(row);
            }
            row.ConfigurationJson = string.IsNullOrWhiteSpace(module.ConfigurationJson) ? "{}" : module.ConfigurationJson;
        }

        var state = await GetOrCreateStateAsync(user, cancellationToken).ConfigureAwait(false);
        state.CompanyName = bootstrap.CompanyName;
        state.BranchName = bootstrap.BranchName;
        state.EnabledModulesCsv = string.Join(',', bootstrap.EnabledModules);
        state.LastSuccessfulSyncUtc = bootstrap.ServerTimestampUtc;
        state.LastError = null;

        if (updateSecurity)
            await ApplySecurityProjectionAsync(user, bootstrap.GrantedPermissionCodes, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplySecurityProjectionAsync(UserContext user, IReadOnlyList<string> grantedClaims, CancellationToken cancellationToken)
    {
        var localUser = await _db.AppUsers.SingleOrDefaultAsync(x => x.Id == user.UserId, cancellationToken).ConfigureAwait(false);
        if (localUser is null)
        {
            localUser = new AppUser(user.CompanyId, user.BranchId, user.UserId.ToString("N")[..8], "Server user", user.UserId);
            _db.AppUsers.Add(localUser);
        }

        var roleName = $"SERVER-PROJECTION-{user.UserId:N}";
        var role = await _db.Roles.SingleOrDefaultAsync(
            x => x.CompanyId == user.CompanyId && x.BranchId == user.BranchId && x.Name == roleName,
            cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            role = new Role(user.CompanyId, user.BranchId, roleName);
            _db.Roles.Add(role);
        }

        if (!await _db.AppUserRoles.AnyAsync(x => x.UserId == user.UserId && x.RoleId == role.Id, cancellationToken).ConfigureAwait(false))
            _db.AppUserRoles.Add(new AppUserRole(user.CompanyId, user.BranchId, user.UserId, role.Id));

        var oldAssignments = await _db.RolePermissionAssignments.Where(x => x.RoleId == role.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        _db.RolePermissionAssignments.RemoveRange(oldAssignments);

        var desired = grantedClaims
            .Where(x => x.StartsWith("accounting.", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (grantedClaims.Any(x => string.Equals(x, "tenant.admin", StringComparison.OrdinalIgnoreCase)))
        {
            desired.Add("accounting.post");
            desired.Add("accounting.reverse");
        }

        foreach (var code in desired)
        {
            var permission = await _db.Permissions.SingleOrDefaultAsync(
                x => x.CompanyId == user.CompanyId && x.BranchId == user.BranchId && x.Code == code,
                cancellationToken).ConfigureAwait(false);
            if (permission is null)
            {
                permission = new Permission(user.CompanyId, user.BranchId, code, code);
                _db.Permissions.Add(permission);
            }
            _db.RolePermissionAssignments.Add(new RolePermissionAssignment(user.CompanyId, user.BranchId, role.Id, permission.Id));
        }
    }

    public async Task ApplyPushResultAsync(UserContext user, SyncPushResult result, CancellationToken cancellationToken = default)
    {
        DemandTenant(user);
        SyncProtocol.EnsureCompatible(result.ServerSchemaVersion, result.ProtocolVersion);
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        foreach (var ack in result.Acknowledgements)
        {
            var outbox = await _db.OutboxMessages.SingleOrDefaultAsync(
                x => x.CompanyId == user.CompanyId && x.BranchId == user.BranchId
                     && x.OperationId == ack.OperationId && x.IdempotencyKey == ack.IdempotencyKey,
                cancellationToken).ConfigureAwait(false);
            if (outbox is null) continue;

            switch (ack.Status)
            {
                case SyncAckStatus.Accepted:
                case SyncAckStatus.AlreadyProcessed:
                    if (ack.DocumentId.HasValue && ack.OfficialSequence.HasValue && !string.IsNullOrWhiteSpace(ack.OfficialSequenceNumber))
                    {
                        var exists = await _db.OfficialSequenceAssignments.AnyAsync(
                            x => x.CompanyId == user.CompanyId && x.BranchId == user.BranchId && x.DocumentId == ack.DocumentId.Value,
                            cancellationToken).ConfigureAwait(false);
                        if (!exists)
                        {
                            var document = await _db.Documents.AsNoTracking().SingleOrDefaultAsync(
                                x => x.CompanyId == user.CompanyId && x.BranchId == user.BranchId && x.Id == ack.DocumentId.Value,
                                cancellationToken).ConfigureAwait(false);
                            _db.OfficialSequenceAssignments.Add(new OfficialSequenceAssignment(
                                user.CompanyId, user.BranchId, ack.DocumentId.Value,
                                document?.GetType().Name ?? "Document",
                                ack.OfficialSequenceNumber!, ack.OfficialSequence.Value, ack.ServerTimestampUtc));
                        }
                    }
                    outbox.MarkProcessed(ack.ServerTimestampUtc);
                    break;

                case SyncAckStatus.ConflictRequiresResolution:
                    outbox.MarkConflict(ack.ServerTimestampUtc);
                    await UpsertConflictAsync(user, ack.OperationId, ack.DocumentId ?? Guid.Empty,
                        ack.ErrorCode ?? "SYNC_CONFLICT", ack.ErrorMessage ?? "Sync conflict requires resolution.",
                        JsonSerializer.Serialize(ack), ack.ServerTimestampUtc, cancellationToken).ConfigureAwait(false);
                    break;

                case SyncAckStatus.Rejected:
                case SyncAckStatus.UpgradeRequired:
                    outbox.MarkRejected(ack.ServerTimestampUtc);
                    break;
            }
        }

        var state = await GetOrCreateStateAsync(user, cancellationToken).ConfigureAwait(false);
        state.LastSuccessfulSyncUtc = result.ServerTimestampUtc;
        state.LastError = result.Acknowledgements.FirstOrDefault(x => x.Status is SyncAckStatus.Rejected or SyncAckStatus.UpgradeRequired)?.ErrorMessage;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordUpgradeRequiredAsync(UserContext user, string message, CancellationToken cancellationToken = default)
    {
        DemandTenant(user);
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var state = await GetOrCreateStateAsync(user, cancellationToken).ConfigureAwait(false);
        state.LastError = $"Upgrade required: {message}";
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkTransportFailureAsync(UserContext user, IReadOnlyList<SyncEnvelope> messages, DateTimeOffset attemptedAtUtc, CancellationToken cancellationToken = default)
    {
        DemandTenant(user);
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var operations = messages.Select(x => x.OperationId).ToHashSet();
        var rows = await _db.OutboxMessages
            .Where(x => x.CompanyId == user.CompanyId && x.BranchId == user.BranchId && operations.Contains(x.OperationId)
                && (x.Status == OutboxStatus.Pending || x.Status == OutboxStatus.Failed))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in rows) row.MarkFailed(attemptedAtUtc);
        var state = await GetOrCreateStateAsync(user, cancellationToken).ConfigureAwait(false);
        state.LastError = "Network/server transport failure. Retry will use exponential backoff.";
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyPullResultAsync(UserContext user, SyncPullResult result, CancellationToken cancellationToken = default)
    {
        DemandTenant(user);
        SyncProtocol.EnsureCompatible(result.ServerSchemaVersion, result.ProtocolVersion);
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        foreach (var change in result.Changes.OrderBy(x => x.ServerWatermark))
        {
            if (change.ChangeType == "TenantMasterDataChanged")
            {
                var bootstrap = JsonSerializer.Deserialize<TenantBootstrapResult>(change.PayloadJson)
                                ?? throw new InvalidOperationException("Invalid tenant master-data payload.");
                await ApplyBootstrapCoreAsync(user, bootstrap, updateSecurity: false, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (change.ChangeType == "SyncConflictRequiresResolution")
            {
                var conflict = JsonSerializer.Deserialize<SyncConflictPayload>(change.PayloadJson)
                               ?? throw new InvalidOperationException("Invalid sync conflict payload.");
                await UpsertConflictAsync(user, conflict.OperationId, conflict.DocumentId, conflict.ConflictCode,
                    conflict.Message, change.PayloadJson, conflict.ServerTimestampUtc, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (change.ChangeType is "PostedOperationAccepted" or "PostedOperationReversed")
            {
                var accepted = JsonSerializer.Deserialize<AcceptedOperationSyncPayload>(change.PayloadJson)
                               ?? throw new InvalidOperationException("Invalid accepted operation payload.");
                await ApplyAcceptedOperationAsync(user, accepted, cancellationToken).ConfigureAwait(false);
            }
        }

        var state = await GetOrCreateStateAsync(user, cancellationToken).ConfigureAwait(false);
        state.LastPulledWatermark = Math.Max(state.LastPulledWatermark, result.NextWatermark);
        state.LastSuccessfulSyncUtc = result.ServerTimestampUtc;
        state.LastError = null;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyAcceptedOperationAsync(UserContext user, AcceptedOperationSyncPayload accepted, CancellationToken cancellationToken)
    {
        var payload = accepted.Operation;
        if (payload.Document.CompanyId != user.CompanyId || payload.Document.BranchId != user.BranchId)
            throw new UnauthorizedAccessException("Pulled operation belongs to a different tenant.");

        var documentExists = await _db.Documents.AsNoTracking().AnyAsync(x => x.Id == payload.Document.Id, cancellationToken).ConfigureAwait(false);
        if (!documentExists)
        {
            await InsertDocumentAsync(payload.Document, payload.Journal, accepted.ServerTimestampUtc, cancellationToken).ConfigureAwait(false);
            await InsertDocumentLinesAsync(payload.Document, cancellationToken).ConfigureAwait(false);
        }

        var journalExists = await _db.JournalEntries.AsNoTracking().AnyAsync(x => x.Id == payload.Journal.Id, cancellationToken).ConfigureAwait(false);
        if (!journalExists)
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO JournalEntries
(Id, CompanyId, BranchId, EntryDateUtc, Description, Reference, Status, SourceDocumentId, SourceDocumentType, OperationId, IdempotencyKey, ReversedEntryId, PostedAtUtc)
VALUES
({payload.Journal.Id}, {user.CompanyId}, {user.BranchId}, {payload.Journal.EntryDateUtc.UtcDateTime.Ticks}, {payload.Journal.Description}, {payload.Journal.Reference},
{(int)PostingStatus.Posted}, {payload.Journal.SourceDocumentId}, {payload.Journal.SourceDocumentType}, {payload.Journal.OperationId},
{payload.Journal.IdempotencyKey}, {payload.Journal.ReversedEntryId}, {accepted.ServerTimestampUtc.UtcDateTime.Ticks});
""", cancellationToken).ConfigureAwait(false);

            foreach (var line in payload.Journal.Lines)
            {
                await _db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO JournalLines
(Id, CompanyId, BranchId, JournalEntryId, AccountId, Debit, Credit, Memo)
VALUES ({line.Id}, {user.CompanyId}, {user.BranchId}, {payload.Journal.Id}, {line.AccountId}, {line.Debit}, {line.Credit}, {line.Memo});
""", cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var stock in payload.StockEntries)
        {
            var exists = await _db.StockLedgerEntries.AsNoTracking().AnyAsync(x => x.Id == stock.Id, cancellationToken).ConfigureAwait(false);
            if (exists) continue;
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO StockLedgerEntries
(Id, CompanyId, BranchId, ItemId, WarehouseId, SourceDocumentId, SourceDocumentType, OperationId, MovementType,
 QuantityDelta, InventoryValueDelta, UnitCost, ReversedStockLedgerEntryId, Status, PostedAtUtc)
VALUES
({stock.Id}, {user.CompanyId}, {user.BranchId}, {stock.ItemId}, {stock.WarehouseId}, {stock.SourceDocumentId},
 {stock.SourceDocumentType}, {stock.OperationId}, {stock.MovementType}, {stock.QuantityDelta}, {stock.InventoryValueDelta},
 {stock.UnitCost}, {stock.ReversedStockLedgerEntryId}, {(int)PostingStatus.Posted}, {stock.PostedAtUtc.UtcDateTime.Ticks});
""", cancellationToken).ConfigureAwait(false);
        }

        var officialExists = await _db.OfficialSequenceAssignments.AsNoTracking()
            .AnyAsync(x => x.CompanyId == user.CompanyId && x.BranchId == user.BranchId && x.DocumentId == payload.Document.Id, cancellationToken)
            .ConfigureAwait(false);
        if (!officialExists)
        {
            _db.OfficialSequenceAssignments.Add(new OfficialSequenceAssignment(
                user.CompanyId, user.BranchId, payload.Document.Id, payload.Document.DocumentType,
                accepted.OfficialSequenceNumber, accepted.OfficialSequence, accepted.ServerTimestampUtc));
        }

        if (payload.Journal.ReversedEntryId.HasValue)
        {
            var reversalExists = await _db.DocumentReversalRecords.AsNoTracking()
                .AnyAsync(x => x.CompanyId == user.CompanyId && x.BranchId == user.BranchId && x.SourceDocumentId == payload.Document.Id, cancellationToken)
                .ConfigureAwait(false);
            if (!reversalExists)
            {
                _db.DocumentReversalRecords.Add(new DocumentReversalRecord(
                    user.CompanyId, user.BranchId, payload.Document.Id, payload.Document.DocumentType,
                    payload.Journal.ReversedEntryId.Value, payload.Journal.Id, payload.Journal.OperationId,
                    payload.Journal.IdempotencyKey, accepted.ServerTimestampUtc));
            }
        }

        _db.AuditLogs.Add(new AuditLog(user.CompanyId, user.BranchId, user.UserId, user.DeviceId,
            "SyncPullApplied", payload.Document.Id, payload.Document.DocumentType, "{}", JsonSerializer.Serialize(accepted), accepted.ServerTimestampUtc));
    }

    private async Task InsertDocumentAsync(PostedDocumentSyncRecord document, PostedJournalSyncRecord journal, DateTimeOffset postedAtUtc, CancellationToken cancellationToken)
    {
        Guid? cash = null, counter = null, businessPartner = document.BusinessPartnerId, warehouse = null, settlement = null;
        decimal? voucherAmount = null;
        string? voucherDescription = null;

        if (document.DocumentType is "ReceiptVoucher" or "PaymentVoucher")
        {
            cash = document.PrimaryAccountId;
            counter = document.CounterAccountId;
            voucherAmount = document.Amount;
            voucherDescription = document.Description;
        }
        else
        {
            warehouse = document.WarehouseId;
            settlement = document.PrimaryAccountId;
        }

        await _db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO Documents
(Id, CompanyId, BranchId, LocalTempNumber, ModuleKey, ModuleMetadataJson, Status, DocumentDateUtc, PostedAtUtc, PostingOperationId,
 PostingIdempotencyKey, DocumentType, CashOrBankAccountId, CounterAccountId, BusinessPartnerId, VoucherAmount,
 VoucherDescription, WarehouseId, SettlementAccountId)
VALUES
({document.Id}, {document.CompanyId}, {document.BranchId}, {document.LocalTempNumber}, {document.ModuleKey}, {document.ModuleMetadataJson},
 {(int)PostingStatus.Posted}, {document.DocumentDateUtc.UtcDateTime.Ticks}, {postedAtUtc.UtcDateTime.Ticks}, {journal.OperationId}, {journal.IdempotencyKey},
 {document.DocumentType}, {cash}, {counter}, {businessPartner}, {voucherAmount}, {voucherDescription}, {warehouse}, {settlement});
""", cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertDocumentLinesAsync(PostedDocumentSyncRecord document, CancellationToken cancellationToken)
    {
        if (document.DocumentType == "SalesInvoice")
        {
            foreach (var line in document.Lines)
            {
                await _db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO SalesInvoiceLines
(Id, CompanyId, BranchId, SalesInvoiceId, ItemId, UnitOfMeasureId, Quantity, BaseUnitsPerUnit, UnitPrice)
VALUES ({line.Id}, {document.CompanyId}, {document.BranchId}, {document.Id}, {line.ItemId}, {line.UnitOfMeasureId},
{line.Quantity}, {line.BaseUnitsPerUnit}, {line.UnitPrice});
""", cancellationToken).ConfigureAwait(false);
            }
        }
        else if (document.DocumentType == "PurchaseInvoice")
        {
            foreach (var line in document.Lines)
            {
                await _db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO PurchaseInvoiceLines
(Id, CompanyId, BranchId, PurchaseInvoiceId, ItemId, UnitOfMeasureId, Quantity, BaseUnitsPerUnit, UnitPrice)
VALUES ({line.Id}, {document.CompanyId}, {document.BranchId}, {document.Id}, {line.ItemId}, {line.UnitOfMeasureId},
{line.Quantity}, {line.BaseUnitsPerUnit}, {line.UnitPrice});
""", cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task UpsertConflictAsync(UserContext user, Guid operationId, Guid documentId, string code, string message,
        string serverPayload, DateTimeOffset serverTimestamp, CancellationToken cancellationToken)
    {
        var existing = await _db.LocalSyncConflicts.SingleOrDefaultAsync(
            x => x.CompanyId == user.CompanyId && x.BranchId == user.BranchId && x.OperationId == operationId,
            cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            existing = new LocalSyncConflict
            {
                Id = UuidV7.NewGuid(),
                CompanyId = user.CompanyId,
                BranchId = user.BranchId,
                OperationId = operationId,
                DocumentId = documentId
            };
            _db.LocalSyncConflicts.Add(existing);
        }
        existing.ConflictCode = code;
        existing.Message = message;
        existing.ServerPayloadJson = serverPayload;
        existing.ServerTimestampUtc = serverTimestamp;
        existing.IsResolved = false;
    }

    private async Task<LocalSyncState> GetOrCreateStateAsync(UserContext user, CancellationToken cancellationToken)
    {
        var state = await _db.LocalSyncStates.SingleOrDefaultAsync(
            x => x.CompanyId == user.CompanyId && x.BranchId == user.BranchId && x.DeviceId == user.DeviceId,
            cancellationToken).ConfigureAwait(false);
        if (state is not null) return state;
        state = new LocalSyncState
        {
            Id = UuidV7.NewGuid(),
            CompanyId = user.CompanyId,
            BranchId = user.BranchId,
            DeviceId = user.DeviceId
        };
        _db.LocalSyncStates.Add(state);
        return state;
    }

    private static void DemandTenant(UserContext user)
    {
        if (!user.IsAuthenticated || user.CompanyId == Guid.Empty || user.BranchId == Guid.Empty || user.DeviceId == Guid.Empty)
            throw new UnauthorizedAccessException("Authenticated local tenant context is required.");
    }
}
