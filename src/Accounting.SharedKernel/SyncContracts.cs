namespace Accounting.SharedKernel;

public static class SyncProtocol
{
    public const int CurrentClientSchemaVersion = 2;
    public const string CurrentProtocolVersion = "2.0";
    public const int MaxBatchSize = 200;

    public static void EnsureCompatible(int clientSchemaVersion, string protocolVersion)
    {
        if (clientSchemaVersion != CurrentClientSchemaVersion || !string.Equals(protocolVersion, CurrentProtocolVersion, StringComparison.Ordinal))
            throw new SyncProtocolMismatchException(clientSchemaVersion, protocolVersion);
    }
}

public sealed class SyncProtocolMismatchException : InvalidOperationException
{
    public int ClientSchemaVersion { get; }
    public string ProtocolVersion { get; }

    public SyncProtocolMismatchException(int schemaVersion, string protocolVersion)
        : base($"Client schema/protocol is incompatible. Required schema={SyncProtocol.CurrentClientSchemaVersion}, protocol={SyncProtocol.CurrentProtocolVersion}; received schema={schemaVersion}, protocol={protocolVersion}. Upgrade the client before synchronizing.")
    {
        ClientSchemaVersion = schemaVersion;
        ProtocolVersion = protocolVersion;
    }
}

public enum SyncAckStatus
{
    Accepted = 1,
    AlreadyProcessed = 2,
    ConflictRequiresResolution = 3,
    Rejected = 4,
    UpgradeRequired = 5
}

public sealed record SyncPushBatch(
    int ClientSchemaVersion,
    string ProtocolVersion,
    Guid DeviceId,
    long LastPulledWatermark,
    IReadOnlyList<SyncEnvelope> Messages);

public sealed record SyncPushResult(
    int ServerSchemaVersion,
    string ProtocolVersion,
    DateTimeOffset ServerTimestampUtc,
    long ServerWatermark,
    IReadOnlyList<SyncAck> Acknowledgements);

public sealed record SyncAck(
    Guid EnvelopeId,
    Guid OperationId,
    string IdempotencyKey,
    SyncAckStatus Status,
    Guid? DocumentId,
    string? OfficialSequenceNumber,
    long? OfficialSequence,
    DateTimeOffset ServerTimestampUtc,
    long ServerWatermark,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record SyncPullRequest(
    int ClientSchemaVersion,
    string ProtocolVersion,
    Guid DeviceId,
    long AfterWatermark,
    int Take = 200);

public sealed record SyncPullResult(
    int ServerSchemaVersion,
    string ProtocolVersion,
    DateTimeOffset ServerTimestampUtc,
    long NextWatermark,
    bool HasMore,
    IReadOnlyList<SyncDelta> Changes);

public sealed record SyncDelta(
    long ServerWatermark,
    string ChangeType,
    Guid EntityId,
    Guid OperationId,
    Guid SourceDeviceId,
    string PayloadJson,
    DateTimeOffset ServerTimestampUtc);

public sealed record PostedOperationSyncPayload(
    PostedDocumentSyncRecord Document,
    PostedJournalSyncRecord Journal,
    IReadOnlyList<PostedStockLedgerSyncRecord> StockEntries);

public sealed record PostedDocumentSyncRecord(
    Guid Id,
    Guid CompanyId,
    Guid BranchId,
    string DocumentType,
    string ModuleKey,
    string LocalTempNumber,
    DateTimeOffset DocumentDateUtc,
    Guid? BusinessPartnerId,
    Guid? WarehouseId,
    Guid? PrimaryAccountId,
    Guid? CounterAccountId,
    decimal? Amount,
    string? Description,
    IReadOnlyList<PostedDocumentLineSyncRecord> Lines);

public sealed record PostedDocumentLineSyncRecord(
    Guid Id,
    Guid ItemId,
    Guid UnitOfMeasureId,
    decimal Quantity,
    decimal BaseUnitsPerUnit,
    decimal UnitPrice);

public sealed record PostedJournalSyncRecord(
    Guid Id,
    Guid CompanyId,
    Guid BranchId,
    Guid SourceDocumentId,
    string SourceDocumentType,
    Guid OperationId,
    string IdempotencyKey,
    DateTimeOffset EntryDateUtc,
    string Description,
    string Reference,
    Guid? ReversedEntryId,
    IReadOnlyList<PostedJournalLineSyncRecord> Lines);

public sealed record PostedJournalLineSyncRecord(
    Guid Id,
    Guid AccountId,
    decimal Debit,
    decimal Credit,
    string Memo);

public sealed record PostedStockLedgerSyncRecord(
    Guid Id,
    Guid ItemId,
    Guid WarehouseId,
    Guid SourceDocumentId,
    string SourceDocumentType,
    Guid OperationId,
    int MovementType,
    decimal QuantityDelta,
    decimal InventoryValueDelta,
    decimal UnitCost,
    Guid? ReversedStockLedgerEntryId,
    DateTimeOffset PostedAtUtc);

public sealed record AcceptedOperationSyncPayload(
    PostedOperationSyncPayload Operation,
    string OfficialSequenceNumber,
    long OfficialSequence,
    DateTimeOffset ServerTimestampUtc,
    long ServerWatermark);

public sealed record SyncConflictPayload(
    Guid ConflictId,
    Guid OperationId,
    Guid DocumentId,
    string ConflictCode,
    string Message,
    string OriginalPayloadJson,
    DateTimeOffset ServerTimestampUtc);
