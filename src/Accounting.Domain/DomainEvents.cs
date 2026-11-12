namespace Accounting.Domain;

public interface IDomainEvent
{
    Guid EventId { get; }
    Guid CompanyId { get; }
    Guid BranchId { get; }
    Guid OperationId { get; }
    DateTimeOffset OccurredAtUtc { get; }
}

public sealed record DocumentPostedDomainEvent(
    Guid EventId,
    Guid CompanyId,
    Guid BranchId,
    Guid OperationId,
    Guid DocumentId,
    string DocumentType,
    Guid JournalEntryId,
    IReadOnlyList<Guid> StockLedgerEntryIds,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record DocumentReversedDomainEvent(
    Guid EventId,
    Guid CompanyId,
    Guid BranchId,
    Guid OperationId,
    Guid DocumentId,
    Guid OriginalJournalEntryId,
    Guid ReversalJournalEntryId,
    IReadOnlyList<Guid> ReversalStockLedgerEntryIds,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;
