using Accounting.Domain;
using Accounting.SharedKernel;

namespace Accounting.Application;

public sealed record AccountLedgerBalance(
    Guid AccountId,
    decimal DebitTotal,
    decimal CreditTotal,
    decimal SignedBalance,
    NormalBalance NormalBalance);

public sealed record StockPosition(
    Guid ItemId,
    Guid WarehouseId,
    decimal QuantityOnHand,
    decimal InventoryValue,
    decimal AverageUnitCost);

public interface IAccountingQueryService
{
    Task<AccountLedgerBalance> GetAccountBalanceAsync(UserContext userContext, Guid accountId, CancellationToken cancellationToken = default);
    Task<StockPosition> GetStockPositionAsync(UserContext userContext, Guid itemId, Guid warehouseId, CancellationToken cancellationToken = default);
}

public sealed class AccountingQueryService : IAccountingQueryService
{
    private readonly IAccountingDataStore _store;
    private readonly ICurrencyPolicyService _currency;
    private readonly InventoryValuationEngine _inventory;

    public AccountingQueryService(IAccountingDataStore store, ICurrencyPolicyService currency, InventoryValuationEngine inventory)
    {
        _store=store;
        _currency=currency;
        _inventory=inventory;
    }

    public async Task<AccountLedgerBalance> GetAccountBalanceAsync(UserContext userContext, Guid accountId, CancellationToken cancellationToken = default)
    {
        DemandAuthenticated(userContext);
        var policy=(await _store.GetCompanyAsync(userContext.CompanyId,cancellationToken).ConfigureAwait(false)).GetPolicy();
        var account=(await _store.GetAccountsAsync(userContext.CompanyId,userContext.BranchId,new[]{accountId},cancellationToken).ConfigureAwait(false)).SingleOrDefault()
            ?? throw new AccountingDomainException($"Account {accountId} was not found.");

        var lines=await _store.GetPostedJournalLinesAsync(userContext.CompanyId,userContext.BranchId,accountId,cancellationToken).ConfigureAwait(false);
        decimal debit=0m, credit=0m;
        foreach(var line in lines)
        {
            debit=_currency.Add(debit,line.Debit,policy);
            credit=_currency.Add(credit,line.Credit,policy);
        }

        var signed=account.NormalBalance==NormalBalance.Debit
            ? _currency.Subtract(debit,credit,policy)
            : _currency.Subtract(credit,debit,policy);
        return new AccountLedgerBalance(account.Id,debit,credit,signed,account.NormalBalance);
    }

    public async Task<StockPosition> GetStockPositionAsync(UserContext userContext, Guid itemId, Guid warehouseId, CancellationToken cancellationToken = default)
    {
        DemandAuthenticated(userContext);
        var policy=(await _store.GetCompanyAsync(userContext.CompanyId,cancellationToken).ConfigureAwait(false)).GetPolicy();
        _=await _store.GetItemAsync(userContext.CompanyId,userContext.BranchId,itemId,cancellationToken).ConfigureAwait(false);
        _=await _store.GetWarehouseAsync(userContext.CompanyId,userContext.BranchId,warehouseId,cancellationToken).ConfigureAwait(false);
        var ledger=await _store.GetStockLedgerAsync(userContext.CompanyId,userContext.BranchId,itemId,warehouseId,cancellationToken).ConfigureAwait(false);
        var state=_inventory.CalculateState(ledger,policy);
        return new StockPosition(itemId,warehouseId,state.QuantityOnHand,state.InventoryValue,state.AverageUnitCost);
    }

    private static void DemandAuthenticated(UserContext userContext)
    {
        if(!userContext.IsAuthenticated) throw new System.Security.SecurityException("Authenticated user context is required.");
    }
}
