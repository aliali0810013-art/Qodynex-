using Accounting.Application;
using Accounting.Domain;
using Accounting.SharedKernel;

namespace Accounting.Modules;

public sealed class WholesaleModule : IActivityModule
{
    public const string ModuleKey = "WHOLESALE";
    private readonly IReadOnlyList<IDocumentPostingRule> _rules;

    public WholesaleModule(IAccountingDataStore store, IAccountingQueryService queries, ICurrencyPolicyService currency)
    {
        _rules = new IDocumentPostingRule[] { new WholesaleCreditLimitPostingRule(store, queries, currency) };
    }

    public string Key => ModuleKey;
    public string DisplayName => "جملة";
    public IReadOnlySet<string> Features { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "units.carton", "units.pallet", "pricing.tiers", "credit.strict-limit", "customer.account"
    };
    public IReadOnlyList<ModuleScreenDefinition> Screens { get; } =
    [
        new("wholesale.sales","مبيعات الجملة",ActivityPlatform.Windows,"pricing.tiers"),
        new("wholesale.customer-credit","ائتمان العملاء",ActivityPlatform.Windows,"credit.strict-limit"),
        new("wholesale.collection.mobile","تحصيل عملاء الجملة",ActivityPlatform.Android,"customer.account")
    ];
    public IReadOnlyList<IDocumentPostingRule> PostingRules => _rules;
}

public sealed class WholesaleCreditLimitPostingRule : IDocumentPostingRule
{
    private readonly IAccountingDataStore _store;
    private readonly IAccountingQueryService _queries;
    private readonly ICurrencyPolicyService _currency;

    public WholesaleCreditLimitPostingRule(IAccountingDataStore store, IAccountingQueryService queries, ICurrencyPolicyService currency)
    {
        _store = store;
        _queries = queries;
        _currency = currency;
    }

    public string ModuleKey => WholesaleModule.ModuleKey;

    public async Task ValidateAsync(CommercialDocument document, UserContext userContext, CancellationToken cancellationToken)
    {
        if (document is not SalesInvoice invoice || !invoice.BusinessPartnerId.HasValue) return;
        var partner = await _store.GetBusinessPartnerAsync(userContext.CompanyId, userContext.BranchId, invoice.BusinessPartnerId.Value, cancellationToken).ConfigureAwait(false);
        if (invoice.SettlementAccountId != partner.ControlAccountId) return;

        var company = await _store.GetCompanyAsync(userContext.CompanyId, cancellationToken).ConfigureAwait(false);
        var policy = company.GetPolicy();
        decimal invoiceGross = 0m;
        foreach (var line in invoice.Lines)
        {
            var raw = _currency.Multiply(line.Quantity, line.UnitPrice, policy);
            decimal gross;
            if (!policy.TaxPolicy.Enabled || policy.TaxPolicy.Rate == 0m || policy.TaxPolicy.PricesIncludeTax)
                gross = raw;
            else
                gross = _currency.Add(raw, _currency.Multiply(raw, policy.TaxPolicy.Rate, policy), policy);
            invoiceGross = _currency.Add(invoiceGross, gross, policy);
        }

        var ledger = await _queries.GetAccountBalanceAsync(userContext, partner.ControlAccountId, cancellationToken).ConfigureAwait(false);
        var exposure = _currency.Add(Math.Max(0m, ledger.SignedBalance), invoiceGross, policy);
        if (exposure > partner.CreditLimit)
            throw new AccountingDomainException($"Wholesale credit limit exceeded for partner {partner.Code}. Limit={partner.CreditLimit}, exposure after invoice={exposure}.");
    }
}
