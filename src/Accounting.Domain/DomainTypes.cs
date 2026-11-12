using Accounting.SharedKernel;

namespace Accounting.Domain;

public enum PostingStatus { Draft = 0, Posted = 1 }
public enum AccountType { Asset = 1, Liability = 2, Equity = 3, Revenue = 4, Expense = 5 }
public enum NormalBalance { Debit = 1, Credit = 2 }
public enum InventoryValuationMethod { MovingWeightedAverage = 1, Fifo = 2 }
public enum PartnerType { Customer = 1, Supplier = 2, Both = 3 }
public enum ItemKind { Stock = 1, Service = 2 }
public enum StockMovementType { PurchaseReceipt = 1, SaleIssue = 2, Reversal = 3, AdjustmentReceipt = 4, AdjustmentIssue = 5 }
public enum OutboxStatus { Pending = 0, Processed = 1, Dispatched = 1, Failed = 2, Conflict = 3, Rejected = 4 }

public interface ITenantEntity
{
    Guid CompanyId { get; }
    Guid BranchId { get; }
}

public interface IImmutablePostedEntity
{
    PostingStatus Status { get; }
}

public class AccountingDomainException : InvalidOperationException
{
    public AccountingDomainException(string message) : base(message) { }
}

public sealed class UnbalancedJournalEntryException : AccountingDomainException
{
    public UnbalancedJournalEntryException(decimal debit, decimal credit)
        : base($"Journal entry is not balanced. Debit={debit}, Credit={credit}.") { }
}

public sealed class NonPostableAccountException : AccountingDomainException
{
    public NonPostableAccountException(Guid accountId) : base($"Account {accountId} is not postable.") { }
}

public sealed class NegativeStockNotAllowedException : AccountingDomainException
{
    public NegativeStockNotAllowedException(Guid itemId, Guid warehouseId, decimal available, decimal requested)
        : base($"Negative stock is not allowed for item {itemId} in warehouse {warehouseId}. Available={available}, Requested={requested}.") { }
}

public readonly record struct Money
{
    public decimal Amount { get; }
    public string CurrencyCode { get; }

    public Money(decimal amount, string currencyCode)
    {
        if (string.IsNullOrWhiteSpace(currencyCode)) throw new AccountingDomainException("Currency code is required.");
        Amount = amount;
        CurrencyCode = currencyCode.Trim().ToUpperInvariant();
    }
}

public readonly record struct Quantity
{
    public decimal Value { get; }

    public Quantity(decimal value, bool allowZero = false)
    {
        if (value < 0m || (!allowZero && value == 0m)) throw new AccountingDomainException("Quantity must be positive.");
        Value = value;
    }

    public static Quantity Zero => new(0m, true);
}

public sealed record TaxPolicy
{
    public bool Enabled { get; init; }
    public decimal Rate { get; init; }
    public bool PricesIncludeTax { get; init; }
    public Guid? OutputTaxAccountId { get; init; }
    public Guid? InputTaxAccountId { get; init; }

    public TaxPolicy(bool enabled, decimal rate, bool pricesIncludeTax, Guid? outputTaxAccountId = null, Guid? inputTaxAccountId = null)
    {
        if (rate < 0m || rate > 1m) throw new AccountingDomainException("Tax rate must be between 0 and 1.");
        if (enabled && rate > 0m && outputTaxAccountId is null) throw new AccountingDomainException("Output tax account is required when tax is enabled.");
        Enabled = enabled;
        Rate = rate;
        PricesIncludeTax = pricesIncludeTax;
        OutputTaxAccountId = outputTaxAccountId;
        InputTaxAccountId = inputTaxAccountId;
    }

    public static TaxPolicy Disabled => new(false, 0m, false);
}

public sealed record CompanyPolicy
{
    public string BaseCurrencyCode { get; init; }
    public int CurrencyScale { get; init; }
    public MidpointRounding RoundingMode { get; init; }
    public int QuantityScale { get; init; }
    public InventoryValuationMethod InventoryValuationMethod { get; init; }
    public bool AllowNegativeStock { get; init; }
    public TaxPolicy TaxPolicy { get; init; }

    public CompanyPolicy(string baseCurrencyCode, int currencyScale, MidpointRounding roundingMode,
        InventoryValuationMethod inventoryValuationMethod, bool allowNegativeStock, TaxPolicy taxPolicy, int quantityScale = 6)
    {
        if (string.IsNullOrWhiteSpace(baseCurrencyCode)) throw new AccountingDomainException("Base currency code is required.");
        if (currencyScale is < 0 or > 8) throw new AccountingDomainException("Currency scale must be between 0 and 8.");
        if (quantityScale is < 0 or > 12) throw new AccountingDomainException("Quantity scale must be between 0 and 12.");
        BaseCurrencyCode = baseCurrencyCode.Trim().ToUpperInvariant();
        CurrencyScale = currencyScale;
        RoundingMode = roundingMode;
        InventoryValuationMethod = inventoryValuationMethod;
        AllowNegativeStock = allowNegativeStock;
        TaxPolicy = taxPolicy ?? throw new ArgumentNullException(nameof(taxPolicy));
        QuantityScale = quantityScale;
    }
}

public abstract class TenantEntity : ITenantEntity
{
    public Guid Id { get; protected set; }
    public Guid CompanyId { get; protected set; }
    public Guid BranchId { get; protected set; }

    protected TenantEntity() { }

    protected TenantEntity(Guid companyId, Guid branchId)
    {
        Id = UuidV7.NewGuid();
        CompanyId = companyId;
        BranchId = branchId;
    }
}
