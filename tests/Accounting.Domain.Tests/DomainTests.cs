using Accounting.Domain;
using Accounting.SharedKernel;

namespace Accounting.Domain.Tests;

public sealed class DomainTests
{
    private readonly CurrencyPolicyService _currency = new();
    private readonly CompanyPolicy _policy = new("USD", 2, MidpointRounding.AwayFromZero, InventoryValuationMethod.MovingWeightedAverage, false, TaxPolicy.Disabled);

    [Fact]
    public void Rejects_Unbalanced_By_001()
    {
        var c = UuidV7.NewGuid(); var b = UuidV7.NewGuid();
        var cash = new Account(c,b,"1000","Cash",AccountType.Asset,NormalBalance.Debit,true);
        var sales = new Account(c,b,"4000","Sales",AccountType.Revenue,NormalBalance.Credit,true);
        var j = new JournalEntry(c,b,DateTimeOffset.UtcNow,"test","R",UuidV7.NewGuid(),"Test",UuidV7.NewGuid(),"k1");
        j.AddLine(cash.Id,100m,0m,"",_policy,_currency); j.AddLine(sales.Id,0m,99.99m,"",_policy,_currency);
        Assert.Throws<UnbalancedJournalEntryException>(() => j.Post(new Dictionary<Guid,Account>{{cash.Id,cash},{sales.Id,sales}},_policy,_currency,DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Rejects_NonPostable_Account()
    {
        var c=UuidV7.NewGuid(); var b=UuidV7.NewGuid();
        var header=new Account(c,b,"1","Assets",AccountType.Asset,NormalBalance.Debit,false);
        var eq=new Account(c,b,"3","Equity",AccountType.Equity,NormalBalance.Credit,true);
        var j=new JournalEntry(c,b,DateTimeOffset.UtcNow,"test","R",UuidV7.NewGuid(),"Test",UuidV7.NewGuid(),"k2");
        j.AddLine(header.Id,10m,0m,"",_policy,_currency); j.AddLine(eq.Id,0m,10m,"",_policy,_currency);
        Assert.Throws<NonPostableAccountException>(() => j.Post(new Dictionary<Guid,Account>{{header.Id,header},{eq.Id,eq}},_policy,_currency,DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Weighted_Average_Is_150_And_Cogs_750()
    {
        var c=UuidV7.NewGuid(); var b=UuidV7.NewGuid(); var item=UuidV7.NewGuid(); var wh=UuidV7.NewGuid(); var t=DateTimeOffset.UtcNow;
        var entries=new List<StockLedgerEntry>{
            new(c,b,item,wh,UuidV7.NewGuid(),"P",UuidV7.NewGuid(),StockMovementType.PurchaseReceipt,10m,1000m,100m,t),
            new(c,b,item,wh,UuidV7.NewGuid(),"P",UuidV7.NewGuid(),StockMovementType.PurchaseReceipt,10m,2000m,200m,t.AddSeconds(1))
        };
        var engine=new InventoryValuationEngine(_currency); var state=engine.CalculateState(entries,_policy); var issue=engine.CalculateIssue(entries,5m,_policy,false);
        Assert.Equal(150m,state.AverageUnitCost); Assert.Equal(750m,issue.TotalCost);
    }
}
