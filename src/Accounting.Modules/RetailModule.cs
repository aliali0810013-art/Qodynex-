using Accounting.Application;
using Accounting.Domain;
using Accounting.SharedKernel;

namespace Accounting.Modules;

public sealed class RetailModule : IActivityModule
{
    public const string ModuleKey = "RETAIL";
    private readonly IReadOnlyList<IDocumentPostingRule> _rules;

    public RetailModule(IAccountingDataStore store)
    {
        _rules = new IDocumentPostingRule[] { new RetailPostingRule(store) };
    }

    public string Key => ModuleKey;
    public string DisplayName => "تجزئة";
    public IReadOnlySet<string> Features { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "barcode.quick-entry", "pricing.tax-inclusive", "payment.cash", "payment.card", "pos.fast", "inventory.stock"
    };
    public IReadOnlyList<ModuleScreenDefinition> Screens { get; } =
    [
        new("retail.pos","نقطة البيع",ActivityPlatform.Windows,"pos.fast"),
        new("retail.pos.mobile","بيع ميداني",ActivityPlatform.Android,"pos.fast"),
        new("retail.barcode","بحث الباركود",ActivityPlatform.Windows,"barcode.quick-entry"),
        new("retail.barcode.mobile","مسح الباركود",ActivityPlatform.Android,"barcode.quick-entry")
    ];
    public IReadOnlyList<IDocumentPostingRule> PostingRules => _rules;
}

public sealed class RetailPostingRule : IDocumentPostingRule
{
    private readonly IAccountingDataStore _store;
    public RetailPostingRule(IAccountingDataStore store) => _store = store;
    public string ModuleKey => RetailModule.ModuleKey;

    public async Task ValidateAsync(CommercialDocument document, UserContext userContext, CancellationToken cancellationToken)
    {
        if (document is not SalesInvoice and not PurchaseInvoice) return;
        var policy=(await _store.GetCompanyAsync(userContext.CompanyId,cancellationToken).ConfigureAwait(false)).GetPolicy();
        if (policy.TaxPolicy.Enabled && !policy.TaxPolicy.PricesIncludeTax)
            throw new AccountingDomainException("Retail module requires tax-inclusive pricing when tax is enabled.");
        var itemIds = document switch
        {
            SalesInvoice sales => sales.Lines.Select(x => x.ItemId),
            PurchaseInvoice purchase => purchase.Lines.Select(x => x.ItemId),
            _ => Array.Empty<Guid>()
        };
        foreach (var itemId in itemIds.Distinct())
        {
            var item = await _store.GetItemAsync(userContext.CompanyId, userContext.BranchId, itemId, cancellationToken).ConfigureAwait(false);
            if (item.ItemKind != ItemKind.Stock)
                throw new AccountingDomainException("Retail documents accept stock items only. Use the Services module for service lines.");
        }
    }
}
