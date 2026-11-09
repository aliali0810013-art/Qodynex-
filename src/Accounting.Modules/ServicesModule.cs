using Accounting.Application;
using Accounting.Domain;
using Accounting.SharedKernel;

namespace Accounting.Modules;

public sealed class ServicesModule : IActivityModule
{
    public const string ModuleKey = "SERVICES";
    private readonly IReadOnlyList<IDocumentPostingRule> _rules;

    public ServicesModule(IAccountingDataStore store)
    {
        _rules = new IDocumentPostingRule[] { new ServicesPostingRule(store) };
    }

    public string Key => ModuleKey;
    public string DisplayName => "خدمات";
    public IReadOnlySet<string> Features { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "service.non-stock", "service.invoice", "service.cost-account", "warehouse.hidden"
    };
    public IReadOnlyList<ModuleScreenDefinition> Screens { get; } =
    [
        new("services.invoice","فاتورة خدمات",ActivityPlatform.Windows,"service.invoice"),
        new("services.invoice.mobile","فاتورة خدمة",ActivityPlatform.Android,"service.invoice")
    ];
    public IReadOnlyList<IDocumentPostingRule> PostingRules => _rules;
}

public sealed class ServicesPostingRule : IDocumentPostingRule
{
    private readonly IAccountingDataStore _store;
    public ServicesPostingRule(IAccountingDataStore store) => _store = store;
    public string ModuleKey => ServicesModule.ModuleKey;

    public async Task ValidateAsync(CommercialDocument document, UserContext userContext, CancellationToken cancellationToken)
    {
        var itemIds = document switch
        {
            SalesInvoice sales => sales.Lines.Select(x => x.ItemId),
            PurchaseInvoice purchase => purchase.Lines.Select(x => x.ItemId),
            _ => Array.Empty<Guid>()
        };

        foreach (var itemId in itemIds.Distinct())
        {
            var item = await _store.GetItemAsync(userContext.CompanyId, userContext.BranchId, itemId, cancellationToken).ConfigureAwait(false);
            if (item.ItemKind != ItemKind.Service)
                throw new AccountingDomainException("Services module documents cannot contain stock items.");
            if (item.SalesRevenueAccountId == Guid.Empty || item.CogsAccountId == Guid.Empty)
                throw new AccountingDomainException("Service items require revenue and service-cost accounts.");
        }
    }
}
