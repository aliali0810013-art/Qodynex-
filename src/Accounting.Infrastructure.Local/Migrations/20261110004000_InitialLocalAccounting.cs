using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Accounting.Infrastructure.Local.Migrations;

[DbContext(typeof(LocalAccountingDbContext))]
[Migration("20261110004000_InitialLocalAccounting")]
public sealed class InitialLocalAccounting : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE Companies (
    Id TEXT NOT NULL PRIMARY KEY,
    CompanyId TEXT NOT NULL,
    BranchId TEXT NOT NULL,
    Name TEXT NOT NULL,
    BaseCurrencyCode TEXT NOT NULL,
    CurrencyScale INTEGER NOT NULL,
    RoundingMode INTEGER NOT NULL,
    QuantityScale INTEGER NOT NULL,
    InventoryValuationMethod INTEGER NOT NULL,
    AllowNegativeStock INTEGER NOT NULL,
    TaxEnabled INTEGER NOT NULL,
    TaxRate TEXT NOT NULL,
    PricesIncludeTax INTEGER NOT NULL,
    OutputTaxAccountId TEXT NULL,
    InputTaxAccountId TEXT NULL
);
""");
        migrationBuilder.Sql("""
CREATE TABLE Branches (
    Id TEXT NOT NULL PRIMARY KEY,
    CompanyId TEXT NOT NULL,
    BranchId TEXT NOT NULL,
    Code TEXT NOT NULL,
    Name TEXT NOT NULL
);
""");
        migrationBuilder.Sql("""
CREATE TABLE AppUsers (
    Id TEXT NOT NULL PRIMARY KEY,
    CompanyId TEXT NOT NULL,
    BranchId TEXT NOT NULL,
    UserName TEXT NOT NULL,
    DisplayName TEXT NOT NULL,
    IsActive INTEGER NOT NULL
);
""");
        migrationBuilder.Sql("""
CREATE TABLE Roles (
    Id TEXT NOT NULL PRIMARY KEY,
    CompanyId TEXT NOT NULL,
    BranchId TEXT NOT NULL,
    Name TEXT NOT NULL
);
""");
        migrationBuilder.Sql("""
CREATE TABLE Permissions (
    Id TEXT NOT NULL PRIMARY KEY,
    CompanyId TEXT NOT NULL,
    BranchId TEXT NOT NULL,
    Code TEXT NOT NULL,
    Name TEXT NOT NULL
);
""");
        migrationBuilder.Sql("""
CREATE TABLE AppUserRoles (
    Id TEXT NOT NULL PRIMARY KEY,
    CompanyId TEXT NOT NULL,
    BranchId TEXT NOT NULL,
    UserId TEXT NOT NULL,
    RoleId TEXT NOT NULL
);
""");
        migrationBuilder.Sql("""
CREATE TABLE RolePermissionAssignments (
    Id TEXT NOT NULL PRIMARY KEY,
    CompanyId TEXT NOT NULL,
    BranchId TEXT NOT NULL,
    RoleId TEXT NOT NULL,
    PermissionId TEXT NOT NULL
);
""");
        migrationBuilder.Sql("""
CREATE TABLE Accounts (
    Id TEXT NOT NULL PRIMARY KEY,
    CompanyId TEXT NOT NULL,
    BranchId TEXT NOT NULL,
    Code TEXT NOT NULL,
    Name TEXT NOT NULL,
    ParentAccountId TEXT NULL,
    AccountType INTEGER NOT NULL,
    NormalBalance INTEGER NOT NULL,
    IsPostable INTEGER NOT NULL,
    IsActive INTEGER NOT NULL
);
""");
        migrationBuilder.Sql("""
CREATE TABLE BusinessPartners (
    Id TEXT NOT NULL PRIMARY KEY,
    CompanyId TEXT NOT NULL,
    BranchId TEXT NOT NULL,
    Code TEXT NOT NULL,
    Name TEXT NOT NULL,
    PartnerType INTEGER NOT NULL,
    ControlAccountId TEXT NOT NULL,
    Phone TEXT NULL,
    IsActive INTEGER NOT NULL
);
""");
        migrationBuilder.Sql("""
CREATE TABLE UnitOfMeasures (
    Id TEXT NOT NULL PRIMARY KEY,
    CompanyId TEXT NOT NULL,
    BranchId TEXT NOT NULL,
    Code TEXT NOT NULL,
    Name TEXT NOT NULL
);
""");
        migrationBuilder.Sql("""
CREATE TABLE Items (
    Id TEXT NOT NULL PRIMARY KEY,
    CompanyId TEXT NOT NULL,
    BranchId TEXT NOT NULL,
    Sku TEXT NOT NULL,
    Name TEXT NOT NULL,
    BaseUnitOfMeasureId TEXT NOT NULL,
    InventoryAccountId TEXT NOT NULL,
    SalesRevenueAccountId TEXT NOT NULL,
    CogsAccountId TEXT NOT NULL,
    IsActive INTEGER NOT NULL
);
""");
        migrationBuilder.Sql("""
CREATE TABLE ItemUnitConversions (
    Id TEXT NOT NULL PRIMARY KEY,
    CompanyId TEXT NOT NULL,
    BranchId TEXT NOT NULL,
    ItemId TEXT NOT NULL,
    UnitOfMeasureId TEXT NOT NULL,
    BaseUnitsPerUnit TEXT NOT NULL
);
""");
        migrationBuilder.Sql("""
CREATE TABLE Warehouses (
    Id TEXT NOT NULL PRIMARY KEY,
    CompanyId TEXT NOT NULL,
    BranchId TEXT NOT NULL,
    Code TEXT NOT NULL,
    Name TEXT NOT NULL,
    AllowNegativeStockOverride INTEGER NULL
);
""");
        migrationBuilder.Sql("""
CREATE TABLE Documents (
    Id TEXT NOT NULL PRIMARY KEY,
    CompanyId TEXT NOT NULL,
    BranchId TEXT NOT NULL,
    LocalTempNumber TEXT NOT NULL,
    ModuleKey TEXT NOT NULL,
    Status INTEGER NOT NULL,
    DocumentDateUtc TEXT NOT NULL,
    PostedAtUtc TEXT NULL,
    PostingOperationId TEXT NULL,
    PostingIdempotencyKey TEXT NULL,
    DocumentType TEXT NOT NULL,
    CashOrBankAccountId TEXT NULL,
    CounterAccountId TEXT NULL,
    BusinessPartnerId TEXT NULL,
    VoucherAmount TEXT NULL,
    VoucherDescription TEXT NULL,
    WarehouseId TEXT NULL,
    SettlementAccountId TEXT NULL
);
""");
        migrationBuilder.Sql("""
CREATE TABLE SalesInvoiceLines (
    Id TEXT NOT NULL PRIMARY KEY,
    CompanyId TEXT NOT NULL,
    BranchId TEXT NOT NULL,
    SalesInvoiceId TEXT NOT NULL,
    ItemId TEXT NOT NULL,
    UnitOfMeasureId TEXT NOT NULL,
    Quantity TEXT NOT NULL,
    BaseUnitsPerUnit TEXT NOT NULL,
    UnitPrice TEXT NOT NULL,
    FOREIGN KEY (SalesInvoiceId) REFERENCES Documents(Id) ON DELETE RESTRICT
);
""");
        migrationBuilder.Sql("""
CREATE TABLE PurchaseInvoiceLines (
    Id TEXT NOT NULL PRIMARY KEY,
    CompanyId TEXT NOT NULL,
    BranchId TEXT NOT NULL,
    PurchaseInvoiceId TEXT NOT NULL,
    ItemId TEXT NOT NULL,
    UnitOfMeasureId TEXT NOT NULL,
    Quantity TEXT NOT NULL,
    BaseUnitsPerUnit TEXT NOT NULL,
    UnitPrice TEXT NOT NULL,
    FOREIGN KEY (PurchaseInvoiceId) REFERENCES Documents(Id) ON DELETE RESTRICT
);
""");
        migrationBuilder.Sql("""
CREATE TABLE JournalEntries (
    Id TEXT NOT NULL PRIMARY KEY,
    CompanyId TEXT NOT NULL,
    BranchId TEXT NOT NULL,
    EntryDateUtc TEXT NOT NULL,
    Description TEXT NOT NULL,
    Reference TEXT NOT NULL,
    Status INTEGER NOT NULL,
    SourceDocumentId TEXT NOT NULL,
    SourceDocumentType TEXT NOT NULL,
    OperationId TEXT NOT NULL,
    IdempotencyKey TEXT NOT NULL,
    ReversedEntryId TEXT NULL,
    PostedAtUtc TEXT NULL
);
""");
        migrationBuilder.Sql("""
CREATE TABLE JournalLines (
    Id TEXT NOT NULL PRIMARY KEY,
    CompanyId TEXT NOT NULL,
    BranchId TEXT NOT NULL,
    JournalEntryId TEXT NOT NULL,
    AccountId TEXT NOT NULL,
    Debit TEXT NOT NULL,
    Credit TEXT NOT NULL,
    Memo TEXT NOT NULL,
    FOREIGN KEY (JournalEntryId) REFERENCES JournalEntries(Id) ON DELETE RESTRICT
);
""");
        migrationBuilder.Sql("""
CREATE TABLE StockLedgerEntries (
    Id TEXT NOT NULL PRIMARY KEY,
    CompanyId TEXT NOT NULL,
    BranchId TEXT NOT NULL,
    ItemId TEXT NOT NULL,
    WarehouseId TEXT NOT NULL,
    SourceDocumentId TEXT NOT NULL,
    SourceDocumentType TEXT NOT NULL,
    OperationId TEXT NOT NULL,
    MovementType INTEGER NOT NULL,
    QuantityDelta TEXT NOT NULL,
    InventoryValueDelta TEXT NOT NULL,
    UnitCost TEXT NOT NULL,
    ReversedStockLedgerEntryId TEXT NULL,
    Status INTEGER NOT NULL,
    PostedAtUtc TEXT NOT NULL
);
""");
        migrationBuilder.Sql("""
CREATE TABLE AuditLogs (
    Id TEXT NOT NULL PRIMARY KEY,
    CompanyId TEXT NOT NULL,
    BranchId TEXT NOT NULL,
    UserId TEXT NOT NULL,
    DeviceId TEXT NOT NULL,
    Action TEXT NOT NULL,
    EntityId TEXT NOT NULL,
    EntityType TEXT NOT NULL,
    BeforeJson TEXT NOT NULL,
    AfterJson TEXT NOT NULL,
    TimestampUtc TEXT NOT NULL
);
""");
        migrationBuilder.Sql("""
CREATE TABLE OutboxMessages (
    Id TEXT NOT NULL PRIMARY KEY,
    CompanyId TEXT NOT NULL,
    BranchId TEXT NOT NULL,
    OperationId TEXT NOT NULL,
    IdempotencyKey TEXT NOT NULL,
    PayloadJson TEXT NOT NULL,
    EventType TEXT NOT NULL,
    Status INTEGER NOT NULL,
    RetryCount INTEGER NOT NULL,
    CreatedAtUtc TEXT NOT NULL,
    LastAttemptAtUtc TEXT NULL
);
""");
        migrationBuilder.Sql("""
CREATE TABLE OfficialSequenceAssignments (
    Id TEXT NOT NULL PRIMARY KEY,
    CompanyId TEXT NOT NULL,
    BranchId TEXT NOT NULL,
    DocumentId TEXT NOT NULL,
    DocumentType TEXT NOT NULL,
    OfficialSequenceNumber TEXT NOT NULL,
    ServerSequence INTEGER NOT NULL,
    AssignedAtUtc TEXT NOT NULL
);
""");
        migrationBuilder.Sql("""
CREATE TABLE DocumentReversalRecords (
    Id TEXT NOT NULL PRIMARY KEY,
    CompanyId TEXT NOT NULL,
    BranchId TEXT NOT NULL,
    SourceDocumentId TEXT NOT NULL,
    SourceDocumentType TEXT NOT NULL,
    OriginalJournalEntryId TEXT NOT NULL,
    ReversalJournalEntryId TEXT NOT NULL,
    OperationId TEXT NOT NULL,
    IdempotencyKey TEXT NOT NULL,
    CreatedAtUtc TEXT NOT NULL
);
""");

        CreateTenantIndex(migrationBuilder, "Companies");
        CreateTenantIndex(migrationBuilder, "Branches");
        CreateTenantIndex(migrationBuilder, "AppUsers");
        CreateTenantIndex(migrationBuilder, "Roles");
        CreateTenantIndex(migrationBuilder, "Permissions");
        CreateTenantIndex(migrationBuilder, "AppUserRoles");
        CreateTenantIndex(migrationBuilder, "RolePermissionAssignments");
        CreateTenantIndex(migrationBuilder, "Accounts");
        CreateTenantIndex(migrationBuilder, "BusinessPartners");
        CreateTenantIndex(migrationBuilder, "UnitOfMeasures");
        CreateTenantIndex(migrationBuilder, "Items");
        CreateTenantIndex(migrationBuilder, "ItemUnitConversions");
        CreateTenantIndex(migrationBuilder, "Warehouses");
        CreateTenantIndex(migrationBuilder, "Documents");
        CreateTenantIndex(migrationBuilder, "SalesInvoiceLines");
        CreateTenantIndex(migrationBuilder, "PurchaseInvoiceLines");
        CreateTenantIndex(migrationBuilder, "JournalEntries");
        CreateTenantIndex(migrationBuilder, "JournalLines");
        CreateTenantIndex(migrationBuilder, "StockLedgerEntries");
        CreateTenantIndex(migrationBuilder, "AuditLogs");
        CreateTenantIndex(migrationBuilder, "OutboxMessages");
        CreateTenantIndex(migrationBuilder, "OfficialSequenceAssignments");
        CreateTenantIndex(migrationBuilder, "DocumentReversalRecords");

        migrationBuilder.Sql("CREATE UNIQUE INDEX IX_Branches_CompanyId_Code ON Branches (CompanyId, Code);");
        migrationBuilder.Sql("CREATE UNIQUE INDEX IX_AppUsers_CompanyId_BranchId_UserName ON AppUsers (CompanyId, BranchId, UserName);");
        migrationBuilder.Sql("CREATE UNIQUE INDEX IX_Roles_CompanyId_BranchId_Name ON Roles (CompanyId, BranchId, Name);");
        migrationBuilder.Sql("CREATE UNIQUE INDEX IX_Permissions_CompanyId_BranchId_Code ON Permissions (CompanyId, BranchId, Code);");
        migrationBuilder.Sql("CREATE UNIQUE INDEX IX_AppUserRoles_Tenant_User_Role ON AppUserRoles (CompanyId, BranchId, UserId, RoleId);");
        migrationBuilder.Sql("CREATE UNIQUE INDEX IX_RolePermissionAssignments_Tenant_Role_Permission ON RolePermissionAssignments (CompanyId, BranchId, RoleId, PermissionId);");
        migrationBuilder.Sql("CREATE UNIQUE INDEX IX_Accounts_CompanyId_BranchId_Code ON Accounts (CompanyId, BranchId, Code);");
        migrationBuilder.Sql("CREATE UNIQUE INDEX IX_BusinessPartners_CompanyId_BranchId_Code ON BusinessPartners (CompanyId, BranchId, Code);");
        migrationBuilder.Sql("CREATE UNIQUE INDEX IX_UnitOfMeasures_CompanyId_BranchId_Code ON UnitOfMeasures (CompanyId, BranchId, Code);");
        migrationBuilder.Sql("CREATE UNIQUE INDEX IX_Items_CompanyId_BranchId_Sku ON Items (CompanyId, BranchId, Sku);");
        migrationBuilder.Sql("CREATE UNIQUE INDEX IX_ItemUnitConversions_Tenant_Item_Unit ON ItemUnitConversions (CompanyId, BranchId, ItemId, UnitOfMeasureId);");
        migrationBuilder.Sql("CREATE UNIQUE INDEX IX_Warehouses_CompanyId_BranchId_Code ON Warehouses (CompanyId, BranchId, Code);");
        migrationBuilder.Sql("CREATE UNIQUE INDEX IX_Documents_Tenant_LocalTempNumber ON Documents (CompanyId, BranchId, LocalTempNumber);");
        migrationBuilder.Sql("CREATE UNIQUE INDEX IX_Documents_Tenant_Idempotency ON Documents (CompanyId, BranchId, PostingIdempotencyKey);");
        migrationBuilder.Sql("CREATE UNIQUE INDEX IX_JournalEntries_Tenant_Idempotency ON JournalEntries (CompanyId, BranchId, IdempotencyKey);");
        migrationBuilder.Sql("CREATE INDEX IX_JournalEntries_Tenant_Source ON JournalEntries (CompanyId, BranchId, SourceDocumentId);");
        migrationBuilder.Sql("CREATE INDEX IX_StockLedgerEntries_Tenant_Item_Warehouse_Date ON StockLedgerEntries (CompanyId, BranchId, ItemId, WarehouseId, PostedAtUtc);");
        migrationBuilder.Sql("CREATE UNIQUE INDEX IX_OutboxMessages_Tenant_Idempotency ON OutboxMessages (CompanyId, BranchId, IdempotencyKey);");
        migrationBuilder.Sql("CREATE UNIQUE INDEX IX_OfficialSequenceAssignments_Tenant_Document ON OfficialSequenceAssignments (CompanyId, BranchId, DocumentId);");
        migrationBuilder.Sql("CREATE UNIQUE INDEX IX_DocumentReversalRecords_Tenant_Document ON DocumentReversalRecords (CompanyId, BranchId, SourceDocumentId);");
        migrationBuilder.Sql("CREATE UNIQUE INDEX IX_DocumentReversalRecords_Tenant_Idempotency ON DocumentReversalRecords (CompanyId, BranchId, IdempotencyKey);");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in new[]
        {
            "DocumentReversalRecords", "OfficialSequenceAssignments", "OutboxMessages", "AuditLogs",
            "StockLedgerEntries", "JournalLines", "JournalEntries", "PurchaseInvoiceLines", "SalesInvoiceLines",
            "Documents", "Warehouses", "ItemUnitConversions", "Items", "UnitOfMeasures", "BusinessPartners",
            "Accounts", "RolePermissionAssignments", "AppUserRoles", "Permissions", "Roles", "AppUsers",
            "Branches", "Companies"
        })
        {
            migrationBuilder.Sql($"DROP TABLE IF EXISTS {table};");
        }
    }

    private static void CreateTenantIndex(MigrationBuilder migrationBuilder, string table)
    {
        migrationBuilder.Sql($"CREATE UNIQUE INDEX IX_{table}_CompanyId_BranchId_Id ON {table} (CompanyId, BranchId, Id);");
    }
}
