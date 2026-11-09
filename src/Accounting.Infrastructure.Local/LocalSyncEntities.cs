namespace Accounting.Infrastructure.Local;

public sealed class LocalSyncState
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BranchId { get; set; }
    public Guid DeviceId { get; set; }
    public long LastPulledWatermark { get; set; }
    public DateTimeOffset? LastSuccessfulSyncUtc { get; set; }
    public string? LastError { get; set; }
}

public sealed class LocalSyncConflict
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BranchId { get; set; }
    public Guid OperationId { get; set; }
    public Guid DocumentId { get; set; }
    public string ConflictCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string ServerPayloadJson { get; set; } = string.Empty;
    public DateTimeOffset ServerTimestampUtc { get; set; }
    public bool IsResolved { get; set; }
}

public sealed class LocalSequenceCounter
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BranchId { get; set; }
    public Guid DeviceId { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public long CurrentValue { get; set; }
}
