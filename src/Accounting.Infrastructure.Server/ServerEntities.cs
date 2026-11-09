using Accounting.Domain;
using Accounting.SharedKernel;

namespace Accounting.Infrastructure.Server;

public abstract class ServerTenantEntity
{
    public Guid Id { get; set; } = UuidV7.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid BranchId { get; set; }
}

public sealed class ServerCompanyPolicy : ServerTenantEntity
{
    public string BaseCurrencyCode { get; set; } = "USD";
    public int CurrencyScale { get; set; } = 2;
    public int QuantityScale { get; set; } = 6;
    public MidpointRounding RoundingMode { get; set; } = MidpointRounding.AwayFromZero;
    public InventoryValuationMethod InventoryValuationMethod { get; set; } = InventoryValuationMethod.MovingWeightedAverage;
    public bool AllowNegativeStock { get; set; }
    public bool TaxEnabled { get; set; }
    public decimal TaxRate { get; set; }
    public bool PricesIncludeTax { get; set; }
    public Guid? OutputTaxAccountId { get; set; }
    public Guid? InputTaxAccountId { get; set; }

    public CompanyPolicy ToPolicy() => new(BaseCurrencyCode, CurrencyScale, RoundingMode, InventoryValuationMethod, AllowNegativeStock,
        new TaxPolicy(TaxEnabled, TaxRate, PricesIncludeTax, OutputTaxAccountId, InputTaxAccountId), QuantityScale);
}

public sealed class ServerTenantProfile : ServerTenantEntity
{
    public string CompanyName { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public string EnabledModulesCsv { get; set; } = "RETAIL";
}

public sealed class ServerModuleConfiguration : ServerTenantEntity
{
    public string ModuleKey { get; set; } = string.Empty;
    public string ConfigurationJson { get; set; } = "{}";
}

public sealed class ServerAccount : ServerTenantEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Guid? ParentAccountId { get; set; }
    public AccountType AccountType { get; set; }
    public NormalBalance NormalBalance { get; set; }
    public bool IsPostable { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class ServerUnitOfMeasure : ServerTenantEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class ServerBusinessPartner : ServerTenantEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public PartnerType PartnerType { get; set; }
    public Guid ControlAccountId { get; set; }
    public string? Phone { get; set; }
    public decimal CreditLimit { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class ServerItem : ServerTenantEntity
{
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Barcode { get; set; }
    public Guid BaseUnitOfMeasureId { get; set; }
    public ItemKind ItemKind { get; set; }
    public Guid InventoryAccountId { get; set; }
    public Guid SalesRevenueAccountId { get; set; }
    public Guid CogsAccountId { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class ServerWarehouse : ServerTenantEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool? AllowNegativeStockOverride { get; set; }
}

public sealed class ServerFinancialDocument : ServerTenantEntity
{
    public string DocumentType { get; set; } = string.Empty;
    public string ModuleKey { get; set; } = string.Empty;
    public string ModuleMetadataJson { get; set; } = "{}";
    public string LocalTempNumber { get; set; } = string.Empty;
    public string OfficialSequenceNumber { get; set; } = string.Empty;
    public long OfficialSequence { get; set; }
    public DateTimeOffset DocumentDateUtc { get; set; }
    public Guid OperationId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public Guid SourceDeviceId { get; set; }
    public Guid? BusinessPartnerId { get; set; }
    public Guid? WarehouseId { get; set; }
    public Guid? PrimaryAccountId { get; set; }
    public Guid? CounterAccountId { get; set; }
    public decimal? Amount { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset ServerTimestampUtc { get; set; }
}

public sealed class ServerDocumentLine : ServerTenantEntity
{
    public Guid DocumentId { get; set; }
    public Guid ItemId { get; set; }
    public Guid UnitOfMeasureId { get; set; }
    public decimal Quantity { get; set; }
    public decimal BaseUnitsPerUnit { get; set; }
    public decimal UnitPrice { get; set; }
}

public sealed class ServerJournalEntry : ServerTenantEntity
{
    public Guid SourceDocumentId { get; set; }
    public string SourceDocumentType { get; set; } = string.Empty;
    public Guid OperationId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset EntryDateUtc { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public Guid? ReversedEntryId { get; set; }
    public DateTimeOffset ServerTimestampUtc { get; set; }
}

public sealed class ServerJournalLine : ServerTenantEntity
{
    public Guid JournalEntryId { get; set; }
    public Guid AccountId { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string Memo { get; set; } = string.Empty;
}

public sealed class ServerStockLedgerEntry : ServerTenantEntity
{
    public Guid ItemId { get; set; }
    public Guid WarehouseId { get; set; }
    public Guid SourceDocumentId { get; set; }
    public string SourceDocumentType { get; set; } = string.Empty;
    public Guid OperationId { get; set; }
    public int MovementType { get; set; }
    public decimal QuantityDelta { get; set; }
    public decimal InventoryValueDelta { get; set; }
    public decimal UnitCost { get; set; }
    public Guid? ReversedStockLedgerEntryId { get; set; }
    public DateTimeOffset PostedAtUtc { get; set; }
    public DateTimeOffset ServerTimestampUtc { get; set; }
}

public sealed class InboxMessage : ServerTenantEntity
{
    public Guid DeviceId { get; set; }
    public Guid EnvelopeId { get; set; }
    public Guid OperationId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string ResultJson { get; set; } = string.Empty;
    public DateTimeOffset ProcessedAtUtc { get; set; }
}

public sealed class SequenceCounter : ServerTenantEntity
{
    public string DocumentType { get; set; } = string.Empty;
    public int SequenceYear { get; set; }
    public long CurrentValue { get; set; }
}

public sealed class SyncWatermark : ServerTenantEntity
{
    public Guid DeviceId { get; set; }
    public long LastPulledWatermark { get; set; }
    public DateTimeOffset? LastPushAtUtc { get; set; }
    public DateTimeOffset? LastPullAtUtc { get; set; }
}

public sealed class DeviceRegistration : ServerTenantEntity
{
    public Guid DeviceId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string ClientVersion { get; set; } = string.Empty;
    public int ClientSchemaVersion { get; set; }
    public string ProtocolVersion { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset RegisteredAtUtc { get; set; }
    public DateTimeOffset? LastSeenAtUtc { get; set; }
}

public sealed class SyncChange : ServerTenantEntity
{
    public long ServerWatermark { get; set; }
    public string ChangeType { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public Guid OperationId { get; set; }
    public Guid SourceDeviceId { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
    public DateTimeOffset ServerTimestampUtc { get; set; }
}

public sealed class ServerAuditLog : ServerTenantEntity
{
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public string Action { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string BeforeJson { get; set; } = string.Empty;
    public string AfterJson { get; set; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; set; }
}

public sealed class ServerSyncConflict : ServerTenantEntity
{
    public Guid OperationId { get; set; }
    public Guid DocumentId { get; set; }
    public Guid DeviceId { get; set; }
    public string ConflictCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string OriginalPayloadJson { get; set; } = string.Empty;
    public bool RequiresResolution { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class ServerDocumentReversal : ServerTenantEntity
{
    public Guid SourceDocumentId { get; set; }
    public Guid OriginalJournalEntryId { get; set; }
    public Guid ReversalJournalEntryId { get; set; }
    public Guid OperationId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}
