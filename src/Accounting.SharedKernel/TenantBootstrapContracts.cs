namespace Accounting.SharedKernel;

public sealed record TenantBootstrapRequest(
    int ClientSchemaVersion,
    string ProtocolVersion,
    Guid DeviceId);

public sealed record TenantBootstrapResult(
    int ServerSchemaVersion,
    string ProtocolVersion,
    Guid CompanyId,
    Guid BranchId,
    string CompanyName,
    string BranchCode,
    string BranchName,
    CompanyPolicySyncRecord Policy,
    IReadOnlyList<AccountMasterSyncRecord> Accounts,
    IReadOnlyList<UnitMasterSyncRecord> Units,
    IReadOnlyList<BusinessPartnerMasterSyncRecord> Partners,
    IReadOnlyList<ItemMasterSyncRecord> Items,
    IReadOnlyList<WarehouseMasterSyncRecord> Warehouses,
    IReadOnlyList<string> EnabledModules,
    IReadOnlyList<string> GrantedPermissionCodes,
    DateTimeOffset ServerTimestampUtc);

public sealed record CompanyPolicySyncRecord(
    string BaseCurrencyCode,
    int CurrencyScale,
    int QuantityScale,
    int RoundingMode,
    int InventoryValuationMethod,
    bool AllowNegativeStock,
    bool TaxEnabled,
    decimal TaxRate,
    bool PricesIncludeTax,
    Guid? OutputTaxAccountId,
    Guid? InputTaxAccountId);

public sealed record AccountMasterSyncRecord(
    Guid Id,
    string Code,
    string Name,
    Guid? ParentAccountId,
    int AccountType,
    int NormalBalance,
    bool IsPostable,
    bool IsActive);

public sealed record UnitMasterSyncRecord(
    Guid Id,
    string Code,
    string Name);

public sealed record BusinessPartnerMasterSyncRecord(
    Guid Id,
    string Code,
    string Name,
    int PartnerType,
    Guid ControlAccountId,
    string? Phone,
    decimal CreditLimit,
    bool IsActive);

public sealed record ItemMasterSyncRecord(
    Guid Id,
    string Sku,
    string Name,
    string? Barcode,
    Guid BaseUnitOfMeasureId,
    Guid InventoryAccountId,
    Guid SalesRevenueAccountId,
    Guid CogsAccountId,
    int ItemKind,
    bool IsActive);

public sealed record WarehouseMasterSyncRecord(
    Guid Id,
    string Code,
    string Name,
    bool? AllowNegativeStockOverride);
