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

    [Theory]
    [InlineData("0.0001")]
    [InlineData("0.001")]
    [InlineData("0.0099")]
    public void Rejects_Silent_Subcent_Journal_Imbalance(string deltaText)
    {
        var delta = decimal.Parse(deltaText, System.Globalization.CultureInfo.InvariantCulture);
        var c = UuidV7.NewGuid(); var b = UuidV7.NewGuid();
        var cash = new Account(c,b,"101","Cash",AccountType.Asset,NormalBalance.Debit,true);
        var sales = new Account(c,b,"401","Revenue",AccountType.Revenue,NormalBalance.Credit,true);
        var journal = new JournalEntry(c,b,DateTimeOffset.UtcNow,"fractional imbalance","R",
            UuidV7.NewGuid(),"Test",UuidV7.NewGuid(),"fraction");
        journal.AddLine(cash.Id,100m,0m,"",_policy,_currency);
        Assert.Throws<AccountingDomainException>(() =>
            journal.AddLine(sales.Id,0m,100m - delta,"",_policy,_currency));
    }

    [Fact]
    public void Reversal_Exactly_Neutralizes_Original_Journal()
    {
        var c=UuidV7.NewGuid(); var b=UuidV7.NewGuid();
        var cash = new Account(c,b,"100","Cash",AccountType.Asset,NormalBalance.Debit,true);
        var sales = new Account(c,b,"400","Revenue",AccountType.Revenue,NormalBalance.Credit,true);
        var accounts = new Dictionary<Guid,Account>{{cash.Id,cash},{sales.Id,sales}};
        var now = DateTimeOffset.UtcNow;
        var journal = new JournalEntry(c,b,now,"Sale","S",UuidV7.NewGuid(),"Sale",UuidV7.NewGuid(),"original");
        journal.AddLine(cash.Id,250m,0m,"",_policy,_currency);
        journal.AddLine(sales.Id,0m,250m,"",_policy,_currency);
        journal.Post(accounts,_policy,_currency,now);
        var reverse = journal.CreateReversal(UuidV7.NewGuid(),"reversal",now.AddSeconds(1),accounts,_policy,_currency);
        Assert.Equal(journal.Id, reverse.ReversedEntryId);
        Assert.Equal(PostingStatus.Posted, reverse.Status);
        foreach (var account in accounts.Keys)
        {
            var originalNet = journal.Lines.Where(x=>x.AccountId==account).Sum(x=>x.Debit-x.Credit);
            var reverseNet = reverse.Lines.Where(x=>x.AccountId==account).Sum(x=>x.Debit-x.Credit);
            Assert.Equal(0m,originalNet+reverseNet);
        }
    }
}
