using System.Text.Json;
using Accounting.SharedKernel;

namespace Accounting.Domain;

public sealed class Company : TenantEntity
{
    public string Name { get; private set; } = string.Empty;
    public string BaseCurrencyCode { get; private set; } = string.Empty;
    public int CurrencyScale { get; private set; }
    public MidpointRounding RoundingMode { get; private set; }
    public int QuantityScale { get; private set; }
    public InventoryValuationMethod InventoryValuationMethod { get; private set; }
    public bool AllowNegativeStock { get; private set; }
    public bool TaxEnabled { get; private set; }
    public decimal TaxRate { get; private set; }
    public bool PricesIncludeTax { get; private set; }
    public Guid? OutputTaxAccountId { get; private set; }
    public Guid? InputTaxAccountId { get; private set; }

    private Company() { }

    public Company(string name, CompanyPolicy policy) : this(UuidV7.NewGuid(), name, policy) { }

    public Company(Guid id, string name, CompanyPolicy policy)
    {
        if (id == Guid.Empty) throw new AccountingDomainException("Company id is required.");
        Id = id;
        CompanyId = id;
        BranchId = Guid.Empty;
        Name = string.IsNullOrWhiteSpace(name) ? throw new AccountingDomainException("Company name is required.") : name.Trim();
        Apply(policy);
    }

    public CompanyPolicy GetPolicy() => new(BaseCurrencyCode, CurrencyScale, RoundingMode, InventoryValuationMethod,
        AllowNegativeStock, new TaxPolicy(TaxEnabled, TaxRate, PricesIncludeTax, OutputTaxAccountId, InputTaxAccountId), QuantityScale);

    private void Apply(CompanyPolicy policy)
    {
        BaseCurrencyCode = policy.BaseCurrencyCode;
        CurrencyScale = policy.CurrencyScale;
        RoundingMode = policy.RoundingMode;
        QuantityScale = policy.QuantityScale;
        InventoryValuationMethod = policy.InventoryValuationMethod;
        AllowNegativeStock = policy.AllowNegativeStock;
        TaxEnabled = policy.TaxPolicy.Enabled;
        TaxRate = policy.TaxPolicy.Rate;
        PricesIncludeTax = policy.TaxPolicy.PricesIncludeTax;
        OutputTaxAccountId = policy.TaxPolicy.OutputTaxAccountId;
        InputTaxAccountId = policy.TaxPolicy.InputTaxAccountId;
    }
}

public sealed class Branch : TenantEntity
{
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    private Branch() { }
    public Branch(Guid companyId, string code, string name) : this(companyId, UuidV7.NewGuid(), code, name) { }

    public Branch(Guid companyId, Guid branchId, string code, string name)
    {
        if (companyId == Guid.Empty || branchId == Guid.Empty) throw new AccountingDomainException("Company and branch ids are required.");
        Id = branchId;
        CompanyId = companyId;
        BranchId = branchId;
        Code = Required(code, "Branch code");
        Name = Required(name, "Branch name");
    }
    private static string Required(string value, string label) => string.IsNullOrWhiteSpace(value) ? throw new AccountingDomainException($"{label} is required.") : value.Trim();
}

public sealed class AppUser : TenantEntity
{
    public string UserName { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    private AppUser() { }
    public AppUser(Guid companyId, Guid branchId, string userName, string displayName, Guid? userId = null) : base(companyId, branchId)
    {
        if (userId.HasValue)
        {
            if (userId.Value == Guid.Empty) throw new AccountingDomainException("User id cannot be empty.");
            Id = userId.Value;
        }
        UserName = string.IsNullOrWhiteSpace(userName) ? throw new AccountingDomainException("User name is required.") : userName.Trim();
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? throw new AccountingDomainException("Display name is required.") : displayName.Trim();
        IsActive = true;
    }
}

public sealed class Role : TenantEntity
{
    public string Name { get; private set; } = string.Empty;
    private Role() { }
    public Role(Guid companyId, Guid branchId, string name) : base(companyId, branchId) =>
        Name = string.IsNullOrWhiteSpace(name) ? throw new AccountingDomainException("Role name is required.") : name.Trim();
}

public sealed class Permission : TenantEntity
{
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    private Permission() { }
    public Permission(Guid companyId, Guid branchId, string code, string name) : base(companyId, branchId)
    {
        Code = string.IsNullOrWhiteSpace(code) ? throw new AccountingDomainException("Permission code is required.") : code.Trim();
        Name = string.IsNullOrWhiteSpace(name) ? throw new AccountingDomainException("Permission name is required.") : name.Trim();
    }
}

public sealed class AppUserRole : TenantEntity
{
    public Guid UserId { get; private set; }
    public Guid RoleId { get; private set; }
    private AppUserRole() { }
    public AppUserRole(Guid companyId, Guid branchId, Guid userId, Guid roleId) : base(companyId, branchId) { UserId = userId; RoleId = roleId; }
}

public sealed class RolePermissionAssignment : TenantEntity
{
    public Guid RoleId { get; private set; }
    public Guid PermissionId { get; private set; }
    private RolePermissionAssignment() { }
    public RolePermissionAssignment(Guid companyId, Guid branchId, Guid roleId, Guid permissionId) : base(companyId, branchId) { RoleId = roleId; PermissionId = permissionId; }
}

public sealed class Account : TenantEntity
{
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public Guid? ParentAccountId { get; private set; }
    public AccountType AccountType { get; private set; }
    public NormalBalance NormalBalance { get; private set; }
    public bool IsPostable { get; private set; }
    public bool IsActive { get; private set; }
    private Account() { }
    public Account(Guid companyId, Guid branchId, string code, string name, AccountType type, NormalBalance normalBalance, bool isPostable, Guid? parentAccountId = null) : base(companyId, branchId)
    {
        Code = string.IsNullOrWhiteSpace(code) ? throw new AccountingDomainException("Account code is required.") : code.Trim();
        Name = string.IsNullOrWhiteSpace(name) ? throw new AccountingDomainException("Account name is required.") : name.Trim();
        AccountType = type; NormalBalance = normalBalance; IsPostable = isPostable; ParentAccountId = parentAccountId; IsActive = true;
    }
}

public sealed class BusinessPartner : TenantEntity
{
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public PartnerType PartnerType { get; private set; }
    public Guid ControlAccountId { get; private set; }
    public string? Phone { get; private set; }
    public decimal CreditLimit { get; private set; }
    public bool IsActive { get; private set; }
    private BusinessPartner() { }
    public BusinessPartner(Guid companyId, Guid branchId, string code, string name, PartnerType partnerType, Guid controlAccountId, string? phone = null, decimal creditLimit = 0m) : base(companyId, branchId)
    {
        if (creditLimit < 0m) throw new AccountingDomainException("Credit limit cannot be negative.");
        Code = string.IsNullOrWhiteSpace(code) ? throw new AccountingDomainException("Partner code is required.") : code.Trim();
        Name = string.IsNullOrWhiteSpace(name) ? throw new AccountingDomainException("Partner name is required.") : name.Trim();
        PartnerType = partnerType; ControlAccountId = controlAccountId; Phone = phone?.Trim(); CreditLimit = creditLimit; IsActive = true;
    }
}

public sealed class UnitOfMeasure : TenantEntity
{
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    private UnitOfMeasure() { }
    public UnitOfMeasure(Guid companyId, Guid branchId, string code, string name) : base(companyId, branchId)
    {
        Code = string.IsNullOrWhiteSpace(code) ? throw new AccountingDomainException("Unit code is required.") : code.Trim();
        Name = string.IsNullOrWhiteSpace(name) ? throw new AccountingDomainException("Unit name is required.") : name.Trim();
    }
}

public sealed class Item : TenantEntity
{
    public string Sku { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? Barcode { get; private set; }
    public Guid BaseUnitOfMeasureId { get; private set; }
    public Guid InventoryAccountId { get; private set; }
    public Guid SalesRevenueAccountId { get; private set; }
    public Guid CogsAccountId { get; private set; }
    public ItemKind ItemKind { get; private set; }
    public bool IsActive { get; private set; }
    private Item() { }
    public Item(Guid companyId, Guid branchId, string sku, string name, Guid baseUnitOfMeasureId, Guid inventoryAccountId, Guid salesRevenueAccountId, Guid cogsAccountId, ItemKind itemKind = ItemKind.Stock, string? barcode = null) : base(companyId, branchId)
    {
        Sku = string.IsNullOrWhiteSpace(sku) ? throw new AccountingDomainException("SKU is required.") : sku.Trim();
        Name = string.IsNullOrWhiteSpace(name) ? throw new AccountingDomainException("Item name is required.") : name.Trim();
        if (itemKind == ItemKind.Stock && (inventoryAccountId == Guid.Empty || cogsAccountId == Guid.Empty))
            throw new AccountingDomainException("Stock items require inventory and COGS accounts.");
        if (salesRevenueAccountId == Guid.Empty) throw new AccountingDomainException("Sales revenue account is required.");
        Barcode = string.IsNullOrWhiteSpace(barcode) ? null : barcode.Trim();
        BaseUnitOfMeasureId = baseUnitOfMeasureId; InventoryAccountId = inventoryAccountId; SalesRevenueAccountId = salesRevenueAccountId; CogsAccountId = cogsAccountId; ItemKind = itemKind; IsActive = true;
    }
}

public sealed class ItemUnitConversion : TenantEntity
{
    public Guid ItemId { get; private set; }
    public Guid UnitOfMeasureId { get; private set; }
    public decimal BaseUnitsPerUnit { get; private set; }
    private ItemUnitConversion() { }
    public ItemUnitConversion(Guid companyId, Guid branchId, Guid itemId, Guid unitOfMeasureId, decimal baseUnitsPerUnit) : base(companyId, branchId)
    {
        if (baseUnitsPerUnit <= 0m) throw new AccountingDomainException("Unit conversion factor must be positive.");
        ItemId = itemId; UnitOfMeasureId = unitOfMeasureId; BaseUnitsPerUnit = baseUnitsPerUnit;
    }
}

public sealed class Warehouse : TenantEntity
{
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public bool? AllowNegativeStockOverride { get; private set; }
    private Warehouse() { }
    public Warehouse(Guid companyId, Guid branchId, string code, string name, bool? allowNegativeStockOverride = null) : base(companyId, branchId)
    {
        Code = string.IsNullOrWhiteSpace(code) ? throw new AccountingDomainException("Warehouse code is required.") : code.Trim();
        Name = string.IsNullOrWhiteSpace(name) ? throw new AccountingDomainException("Warehouse name is required.") : name.Trim();
        AllowNegativeStockOverride = allowNegativeStockOverride;
    }
    public bool AllowsNegativeStock(CompanyPolicy policy) => AllowNegativeStockOverride ?? policy.AllowNegativeStock;
}

public abstract class CommercialDocument : TenantEntity, IImmutablePostedEntity
{
    public string LocalTempNumber { get; private set; } = string.Empty;
    public string ModuleKey { get; private set; } = string.Empty;
    public string ModuleMetadataJson { get; private set; } = "{}";
    public PostingStatus Status { get; private set; }
    public DateTimeOffset DocumentDateUtc { get; private set; }
    public DateTimeOffset? PostedAtUtc { get; private set; }
    public Guid? PostingOperationId { get; private set; }
    public string? PostingIdempotencyKey { get; private set; }

    protected CommercialDocument() { }
    protected CommercialDocument(Guid companyId, Guid branchId, string localTempNumber, string moduleKey, DateTimeOffset date) : base(companyId, branchId)
    {
        LocalTempNumber = string.IsNullOrWhiteSpace(localTempNumber) ? throw new AccountingDomainException("Local temporary number is required.") : localTempNumber.Trim();
        ModuleKey = string.IsNullOrWhiteSpace(moduleKey) ? throw new AccountingDomainException("Module key is required.") : moduleKey.Trim();
        DocumentDateUtc = date; Status = PostingStatus.Draft;
    }

    public void SetModuleMetadata(string json)
    {
        if (Status == PostingStatus.Posted) throw new AccountingDomainException("Posted document module metadata is immutable.");
        if (string.IsNullOrWhiteSpace(json)) { ModuleMetadataJson = "{}"; return; }
        using var _ = JsonDocument.Parse(json);
        ModuleMetadataJson = json;
    }

    public void MarkPosted(Guid operationId, string idempotencyKey, DateTimeOffset postedAtUtc)
    {
        if (Status == PostingStatus.Posted)
        {
            if (PostingIdempotencyKey == idempotencyKey) return;
            throw new AccountingDomainException("Document is already posted with another idempotency key.");
        }
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new AccountingDomainException("Idempotency key is required.");
        PostingOperationId = operationId; PostingIdempotencyKey = idempotencyKey.Trim(); PostedAtUtc = postedAtUtc; Status = PostingStatus.Posted;
    }
}

public sealed class ReceiptVoucher : CommercialDocument
{
    public Guid CashOrBankAccountId { get; private set; }
    public Guid CounterAccountId { get; private set; }
    public Guid? BusinessPartnerId { get; private set; }
    public decimal Amount { get; private set; }
    public string Description { get; private set; } = string.Empty;
    private ReceiptVoucher() { }
    public ReceiptVoucher(Guid companyId, Guid branchId, string number, string moduleKey, DateTimeOffset date, Guid cashOrBankAccountId, Guid counterAccountId, decimal amount, string description, Guid? businessPartnerId = null) : base(companyId, branchId, number, moduleKey, date)
    {
        if (amount <= 0m) throw new AccountingDomainException("Receipt amount must be positive.");
        CashOrBankAccountId = cashOrBankAccountId; CounterAccountId = counterAccountId; Amount = amount; Description = description?.Trim() ?? string.Empty; BusinessPartnerId = businessPartnerId;
    }
}

public sealed class PaymentVoucher : CommercialDocument
{
    public Guid CashOrBankAccountId { get; private set; }
    public Guid CounterAccountId { get; private set; }
    public Guid? BusinessPartnerId { get; private set; }
    public decimal Amount { get; private set; }
    public string Description { get; private set; } = string.Empty;
    private PaymentVoucher() { }
    public PaymentVoucher(Guid companyId, Guid branchId, string number, string moduleKey, DateTimeOffset date, Guid cashOrBankAccountId, Guid counterAccountId, decimal amount, string description, Guid? businessPartnerId = null) : base(companyId, branchId, number, moduleKey, date)
    {
        if (amount <= 0m) throw new AccountingDomainException("Payment amount must be positive.");
        CashOrBankAccountId = cashOrBankAccountId; CounterAccountId = counterAccountId; Amount = amount; Description = description?.Trim() ?? string.Empty; BusinessPartnerId = businessPartnerId;
    }
}

public sealed class SalesInvoice : CommercialDocument
{
    private readonly List<SalesInvoiceLine> _lines = new();
    public Guid WarehouseId { get; private set; }
    public Guid SettlementAccountId { get; private set; }
    public Guid? BusinessPartnerId { get; private set; }
    public IReadOnlyCollection<SalesInvoiceLine> Lines => _lines;
    private SalesInvoice() { }
    public SalesInvoice(Guid companyId, Guid branchId, string number, string moduleKey, DateTimeOffset date, Guid warehouseId, Guid settlementAccountId, Guid? businessPartnerId = null) : base(companyId, branchId, number, moduleKey, date)
    { WarehouseId = warehouseId; SettlementAccountId = settlementAccountId; BusinessPartnerId = businessPartnerId; }
    public SalesInvoiceLine AddLine(Guid itemId, Guid uomId, decimal quantity, decimal baseUnitsPerUnit, decimal unitPrice)
    {
        if (Status != PostingStatus.Draft) throw new AccountingDomainException("Posted sales invoices cannot be changed.");
        var line = new SalesInvoiceLine(CompanyId, BranchId, Id, itemId, uomId, quantity, baseUnitsPerUnit, unitPrice); _lines.Add(line); return line;
    }
}

public sealed class SalesInvoiceLine : TenantEntity
{
    public Guid SalesInvoiceId { get; private set; }
    public Guid ItemId { get; private set; }
    public Guid UnitOfMeasureId { get; private set; }
    public decimal Quantity { get; private set; }
    public decimal BaseUnitsPerUnit { get; private set; }
    public decimal UnitPrice { get; private set; }
    private SalesInvoiceLine() { }
    internal SalesInvoiceLine(Guid companyId, Guid branchId, Guid invoiceId, Guid itemId, Guid uomId, decimal quantity, decimal baseUnitsPerUnit, decimal unitPrice) : base(companyId, branchId)
    {
        if (quantity <= 0m || baseUnitsPerUnit <= 0m || unitPrice <= 0m) throw new AccountingDomainException("Sales line values must be positive.");
        SalesInvoiceId = invoiceId; ItemId = itemId; UnitOfMeasureId = uomId; Quantity = quantity; BaseUnitsPerUnit = baseUnitsPerUnit; UnitPrice = unitPrice;
    }
}

public sealed class PurchaseInvoice : CommercialDocument
{
    private readonly List<PurchaseInvoiceLine> _lines = new();
    public Guid WarehouseId { get; private set; }
    public Guid SettlementAccountId { get; private set; }
    public Guid? BusinessPartnerId { get; private set; }
    public IReadOnlyCollection<PurchaseInvoiceLine> Lines => _lines;
    private PurchaseInvoice() { }
    public PurchaseInvoice(Guid companyId, Guid branchId, string number, string moduleKey, DateTimeOffset date, Guid warehouseId, Guid settlementAccountId, Guid? businessPartnerId = null) : base(companyId, branchId, number, moduleKey, date)
    { WarehouseId = warehouseId; SettlementAccountId = settlementAccountId; BusinessPartnerId = businessPartnerId; }
    public PurchaseInvoiceLine AddLine(Guid itemId, Guid uomId, decimal quantity, decimal baseUnitsPerUnit, decimal unitPrice)
    {
        if (Status != PostingStatus.Draft) throw new AccountingDomainException("Posted purchase invoices cannot be changed.");
        var line = new PurchaseInvoiceLine(CompanyId, BranchId, Id, itemId, uomId, quantity, baseUnitsPerUnit, unitPrice); _lines.Add(line); return line;
    }
}

public sealed class PurchaseInvoiceLine : TenantEntity
{
    public Guid PurchaseInvoiceId { get; private set; }
    public Guid ItemId { get; private set; }
    public Guid UnitOfMeasureId { get; private set; }
    public decimal Quantity { get; private set; }
    public decimal BaseUnitsPerUnit { get; private set; }
    public decimal UnitPrice { get; private set; }
    private PurchaseInvoiceLine() { }
    internal PurchaseInvoiceLine(Guid companyId, Guid branchId, Guid invoiceId, Guid itemId, Guid uomId, decimal quantity, decimal baseUnitsPerUnit, decimal unitPrice) : base(companyId, branchId)
    {
        if (quantity <= 0m || baseUnitsPerUnit <= 0m || unitPrice <= 0m) throw new AccountingDomainException("Purchase line values must be positive.");
        PurchaseInvoiceId = invoiceId; ItemId = itemId; UnitOfMeasureId = uomId; Quantity = quantity; BaseUnitsPerUnit = baseUnitsPerUnit; UnitPrice = unitPrice;
    }
}

public sealed class JournalEntry : TenantEntity, IImmutablePostedEntity
{
    private readonly List<JournalLine> _lines = new();
    public DateTimeOffset EntryDateUtc { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public string Reference { get; private set; } = string.Empty;
    public PostingStatus Status { get; private set; }
    public Guid SourceDocumentId { get; private set; }
    public string SourceDocumentType { get; private set; } = string.Empty;
    public Guid OperationId { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public Guid? ReversedEntryId { get; private set; }
    public DateTimeOffset? PostedAtUtc { get; private set; }
    public IReadOnlyCollection<JournalLine> Lines => _lines;
    private JournalEntry() { }
    public JournalEntry(Guid companyId, Guid branchId, DateTimeOffset date, string description, string reference, Guid sourceDocumentId, string sourceType, Guid operationId, string idempotencyKey, Guid? reversedEntryId = null) : base(companyId, branchId)
    {
        EntryDateUtc = date; Description = string.IsNullOrWhiteSpace(description) ? throw new AccountingDomainException("Journal description is required.") : description.Trim();
        Reference = reference?.Trim() ?? string.Empty; SourceDocumentId = sourceDocumentId; SourceDocumentType = sourceType; OperationId = operationId;
        IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? throw new AccountingDomainException("Idempotency key is required.") : idempotencyKey.Trim();
        ReversedEntryId = reversedEntryId; Status = PostingStatus.Draft;
    }
    public JournalLine AddLine(Guid accountId, decimal debit, decimal credit, string memo, CompanyPolicy policy, ICurrencyPolicyService currency)
    {
        if (Status == PostingStatus.Posted) throw new AccountingDomainException("Cannot add a line to a posted journal.");
        var line = new JournalLine(CompanyId, BranchId, Id, accountId, debit, credit, memo, policy, currency); _lines.Add(line); return line;
    }
    public void Post(IReadOnlyDictionary<Guid, Account> accounts, CompanyPolicy policy, ICurrencyPolicyService currency, DateTimeOffset postedAt)
    {
        if (Status == PostingStatus.Posted) return;
        if (_lines.Count < 2) throw new AccountingDomainException("A journal entry must contain at least two lines.");
        decimal debit = 0m, credit = 0m;
        foreach (var line in _lines)
        {
            if (!accounts.TryGetValue(line.AccountId, out var account)) throw new AccountingDomainException($"Account {line.AccountId} was not supplied.");
            if (account.CompanyId != CompanyId || account.BranchId != BranchId) throw new AccountingDomainException("Cross-tenant posting is forbidden.");
            if (!account.IsPostable || !account.IsActive) throw new NonPostableAccountException(account.Id);
            debit = currency.Add(debit, line.Debit, policy); credit = currency.Add(credit, line.Credit, policy);
        }
        if (currency.RoundMoney(debit, policy) != currency.RoundMoney(credit, policy)) throw new UnbalancedJournalEntryException(debit, credit);
        Status = PostingStatus.Posted; PostedAtUtc = postedAt;
    }
    public JournalEntry CreateReversal(Guid operationId, string key, DateTimeOffset date, IReadOnlyDictionary<Guid, Account> accounts, CompanyPolicy policy, ICurrencyPolicyService currency)
    {
        if (Status != PostingStatus.Posted) throw new AccountingDomainException("Only posted journals can be reversed.");
        var r = new JournalEntry(CompanyId, BranchId, date, $"Reversal of {Description}", Reference, SourceDocumentId, $"{SourceDocumentType}.Reversal", operationId, key, Id);
        foreach (var line in _lines) r.AddLine(line.AccountId, line.Credit, line.Debit, $"Reversal: {line.Memo}", policy, currency);
        r.Post(accounts, policy, currency, date); return r;
    }
}

public sealed class JournalLine : TenantEntity
{
    public Guid JournalEntryId { get; private set; }
    public Guid AccountId { get; private set; }
    public decimal Debit { get; private set; }
    public decimal Credit { get; private set; }
    public string Memo { get; private set; } = string.Empty;
    private JournalLine() { }
    internal JournalLine(Guid companyId, Guid branchId, Guid journalEntryId, Guid accountId, decimal debit, decimal credit, string memo, CompanyPolicy policy, ICurrencyPolicyService currency) : base(companyId, branchId)
    {
        debit = currency.RoundMoney(debit, policy); credit = currency.RoundMoney(credit, policy);
        if (debit < 0m || credit < 0m) throw new AccountingDomainException("Journal values cannot be negative.");
        if ((debit > 0m && credit > 0m) || (debit == 0m && credit == 0m)) throw new AccountingDomainException("Exactly one side must be positive.");
        JournalEntryId = journalEntryId; AccountId = accountId; Debit = debit; Credit = credit; Memo = memo?.Trim() ?? string.Empty;
    }
}

public sealed class StockLedgerEntry : TenantEntity, IImmutablePostedEntity
{
    public Guid ItemId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public Guid SourceDocumentId { get; private set; }
    public string SourceDocumentType { get; private set; } = string.Empty;
    public Guid OperationId { get; private set; }
    public StockMovementType MovementType { get; private set; }
    public decimal QuantityDelta { get; private set; }
    public decimal InventoryValueDelta { get; private set; }
    public decimal UnitCost { get; private set; }
    public Guid? ReversedStockLedgerEntryId { get; private set; }
    public PostingStatus Status { get; private set; }
    public DateTimeOffset PostedAtUtc { get; private set; }
    private StockLedgerEntry() { }
    public StockLedgerEntry(Guid companyId, Guid branchId, Guid itemId, Guid warehouseId, Guid sourceDocumentId, string sourceType, Guid operationId,
        StockMovementType movementType, decimal quantityDelta, decimal inventoryValueDelta, decimal unitCost, DateTimeOffset postedAt, Guid? reversedStockLedgerEntryId = null) : base(companyId, branchId)
    {
        if (quantityDelta == 0m) throw new AccountingDomainException("Stock quantity delta cannot be zero.");
        if (unitCost < 0m) throw new AccountingDomainException("Stock unit cost cannot be negative.");
        ItemId = itemId; WarehouseId = warehouseId; SourceDocumentId = sourceDocumentId; SourceDocumentType = sourceType; OperationId = operationId;
        MovementType = movementType; QuantityDelta = quantityDelta; InventoryValueDelta = inventoryValueDelta; UnitCost = unitCost;
        ReversedStockLedgerEntryId = reversedStockLedgerEntryId; Status = PostingStatus.Posted; PostedAtUtc = postedAt;
    }
}

public sealed class AuditLog : TenantEntity
{
    public Guid UserId { get; private set; }
    public Guid DeviceId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public Guid EntityId { get; private set; }
    public string EntityType { get; private set; } = string.Empty;
    public string BeforeJson { get; private set; } = string.Empty;
    public string AfterJson { get; private set; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; private set; }
    private AuditLog() { }
    public AuditLog(Guid companyId, Guid branchId, Guid userId, Guid deviceId, string action, Guid entityId, string entityType, string beforeJson, string afterJson, DateTimeOffset timestamp) : base(companyId, branchId)
    {
        UserId = userId; DeviceId = deviceId; Action = action; EntityId = entityId; EntityType = entityType; BeforeJson = beforeJson; AfterJson = afterJson; TimestampUtc = timestamp;
    }
}

public sealed class OutboxMessage : TenantEntity
{
    public Guid OperationId { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = string.Empty;
    public string EventType { get; private set; } = string.Empty;
    public OutboxStatus Status { get; private set; }
    public int RetryCount { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? LastAttemptAtUtc { get; private set; }
    private OutboxMessage() { }
    public OutboxMessage(Guid companyId, Guid branchId, Guid operationId, string key, string payload, string eventType, DateTimeOffset createdAt) : base(companyId, branchId)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new AccountingDomainException("Outbox idempotency key is required.");
        if (string.IsNullOrWhiteSpace(payload)) throw new AccountingDomainException("Outbox payload is required.");
        OperationId = operationId; IdempotencyKey = key.Trim(); PayloadJson = payload; EventType = eventType; Status = OutboxStatus.Pending; CreatedAtUtc = createdAt;
    }

    public void MarkProcessed(DateTimeOffset attemptedAtUtc)
    {
        LastAttemptAtUtc = attemptedAtUtc;
        Status = OutboxStatus.Processed;
    }

    public void MarkFailed(DateTimeOffset attemptedAtUtc)
    {
        LastAttemptAtUtc = attemptedAtUtc;
        RetryCount++;
        Status = OutboxStatus.Failed;
    }

    public void MarkConflict(DateTimeOffset attemptedAtUtc)
    {
        LastAttemptAtUtc = attemptedAtUtc;
        Status = OutboxStatus.Conflict;
    }

    public void MarkRejected(DateTimeOffset attemptedAtUtc)
    {
        LastAttemptAtUtc = attemptedAtUtc;
        Status = OutboxStatus.Rejected;
    }

    public void MarkPendingForRetry()
    {
        if (Status is OutboxStatus.Processed or OutboxStatus.Conflict or OutboxStatus.Rejected)
            throw new AccountingDomainException("Terminal outbox messages cannot be retried without an explicit corrective operation.");
        Status = OutboxStatus.Pending;
    }
}

public sealed class OfficialSequenceAssignment : TenantEntity
{
    public Guid DocumentId { get; private set; }
    public string DocumentType { get; private set; } = string.Empty;
    public string OfficialSequenceNumber { get; private set; } = string.Empty;
    public long ServerSequence { get; private set; }
    public DateTimeOffset AssignedAtUtc { get; private set; }
    private OfficialSequenceAssignment() { }
    public OfficialSequenceAssignment(Guid companyId, Guid branchId, Guid documentId, string documentType, string officialNumber, long serverSequence, DateTimeOffset assignedAt) : base(companyId, branchId)
    {
        if (serverSequence <= 0) throw new AccountingDomainException("Server sequence must be positive.");
        DocumentId = documentId; DocumentType = documentType; OfficialSequenceNumber = officialNumber; ServerSequence = serverSequence; AssignedAtUtc = assignedAt;
    }
}

public sealed class DocumentReversalRecord : TenantEntity
{
    public Guid SourceDocumentId { get; private set; }
    public string SourceDocumentType { get; private set; } = string.Empty;
    public Guid OriginalJournalEntryId { get; private set; }
    public Guid ReversalJournalEntryId { get; private set; }
    public Guid OperationId { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    private DocumentReversalRecord() { }
    public DocumentReversalRecord(Guid companyId, Guid branchId, Guid sourceDocumentId, string sourceType, Guid originalJournalEntryId, Guid reversalJournalEntryId, Guid operationId, string key, DateTimeOffset createdAt) : base(companyId, branchId)
    {
        SourceDocumentId = sourceDocumentId; SourceDocumentType = sourceType; OriginalJournalEntryId = originalJournalEntryId; ReversalJournalEntryId = reversalJournalEntryId;
        OperationId = operationId; IdempotencyKey = key; CreatedAtUtc = createdAt;
    }
}
