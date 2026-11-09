# Architecture baseline

The core enforces ten non-negotiable invariants:

1. No mutable balances. GL balances derive from posted journal lines; stock derives from posted stock-ledger entries.
2. Financial and quantity arithmetic uses decimal only and passes through CurrencyPolicyService.
3. Journal posting is strict double-entry with postable leaf accounts only.
4. Posted data is append-only. EF interception and SQLite triggers block mutation/deletion.
5. Local financial operations commit document + journal + stock + audit + outbox in one SQLite transaction.
6. Posting is idempotent by IdempotencyKey and UUIDv7 OperationId.
7. LocalTempNumber is local; server official numbering is appended through OfficialSequenceAssignment without changing a posted document.
8. Perpetual inventory supports Moving Weighted Average and FIFO and posts COGS automatically.
9. Every operational table carries CompanyId/BranchId; posting requires UserContext, PermissionGuard and AuditLog.
10. ModuleKey plus IDocumentPostingRule keeps the financial core activity-neutral.

Cancellation never mutates the original posted document. It appends DocumentReversalRecord, a reversal journal and reversing stock movements.
