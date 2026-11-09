using Accounting.Domain;
using Accounting.SharedKernel;

namespace Accounting.Application;

public interface IUnitOfWork
{
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default);
}

public interface IAccountingDataStore
{
    Task<Company> GetCompanyAsync(Guid companyId, CancellationToken cancellationToken);
    Task<CommercialDocument?> GetDocumentAsync(Guid companyId, Guid branchId, Guid documentId, CancellationToken cancellationToken);
    Task<SalesInvoice> GetSalesInvoiceAsync(Guid companyId, Guid branchId, Guid documentId, CancellationToken cancellationToken);
    Task<PurchaseInvoice> GetPurchaseInvoiceAsync(Guid companyId, Guid branchId, Guid documentId, CancellationToken cancellationToken);
    Task<ReceiptVoucher> GetReceiptVoucherAsync(Guid companyId, Guid branchId, Guid documentId, CancellationToken cancellationToken);
    Task<PaymentVoucher> GetPaymentVoucherAsync(Guid companyId, Guid branchId, Guid documentId, CancellationToken cancellationToken);
    Task<Warehouse> GetWarehouseAsync(Guid companyId, Guid branchId, Guid warehouseId, CancellationToken cancellationToken);
    Task<Item> GetItemAsync(Guid companyId, Guid branchId, Guid itemId, CancellationToken cancellationToken);
    Task<BusinessPartner> GetBusinessPartnerAsync(Guid companyId, Guid branchId, Guid partnerId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Account>> GetAccountsAsync(Guid companyId, Guid branchId, IEnumerable<Guid> accountIds, CancellationToken cancellationToken);
    Task<IReadOnlyList<StockLedgerEntry>> GetStockLedgerAsync(Guid companyId, Guid branchId, Guid itemId, Guid warehouseId, CancellationToken cancellationToken);
    Task<IReadOnlyList<JournalLine>> GetPostedJournalLinesAsync(Guid companyId, Guid branchId, Guid accountId, CancellationToken cancellationToken);
    Task<IReadOnlyList<StockLedgerEntry>> GetStockBySourceDocumentAsync(Guid companyId, Guid branchId, Guid sourceDocumentId, CancellationToken cancellationToken);
    Task<JournalEntry?> FindJournalByIdempotencyKeyAsync(Guid companyId, Guid branchId, string key, CancellationToken cancellationToken);
    Task<JournalEntry?> GetJournalBySourceDocumentAsync(Guid companyId, Guid branchId, Guid sourceDocumentId, CancellationToken cancellationToken);
    Task<DocumentReversalRecord?> FindReversalByIdempotencyKeyAsync(Guid companyId, Guid branchId, string key, CancellationToken cancellationToken);
    Task<DocumentReversalRecord?> GetReversalForDocumentAsync(Guid companyId, Guid branchId, Guid sourceDocumentId, CancellationToken cancellationToken);
    void Add<TEntity>(TEntity entity) where TEntity : class;
}

public interface IUserAuthorizationReader
{
    Task<bool> HasPermissionAsync(UserContext userContext, string permissionCode, CancellationToken cancellationToken);
}

public interface IPermissionGuard
{
    Task DemandAsync(UserContext userContext, string permissionCode, CancellationToken cancellationToken);
}

public interface IDocumentPostingRule
{
    string ModuleKey { get; }
    Task ValidateAsync(CommercialDocument document, UserContext userContext, CancellationToken cancellationToken);
}

public sealed class DocumentPostingRuleRegistry
{
    private readonly IReadOnlyCollection<IDocumentPostingRule> _rules;
    public DocumentPostingRuleRegistry(IEnumerable<IDocumentPostingRule>? rules = null) => _rules = (rules ?? Array.Empty<IDocumentPostingRule>()).ToArray();
    public async Task ValidateAsync(CommercialDocument document, UserContext userContext, CancellationToken ct)
    {
        foreach (var rule in _rules.Where(x => string.Equals(x.ModuleKey, document.ModuleKey, StringComparison.OrdinalIgnoreCase)))
            await rule.ValidateAsync(document, userContext, ct).ConfigureAwait(false);
    }
}
