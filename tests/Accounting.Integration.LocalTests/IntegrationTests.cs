using System.Security;
using Accounting.Application;
using Accounting.Domain;
using Accounting.Infrastructure.Local;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Integration.LocalTests;

public sealed class IntegrationTests
{
    [Fact]
    public async Task Same_IdempotencyKey_Does_Not_Duplicate_Sale()
    {
        await using var t=await TestDatabase.CreateAsync();
        var p=await t.Purchase("LOC-1",10m,100m); await t.Engine.PostPurchaseInvoiceAsync(new(p.Id,t.Context("p1"))); t.Clock.Advance();
        var s=await t.Sale("LOC-2",2m,250m); var ctx=t.Context("sale-idem");
        var first=await t.Engine.PostSalesInvoiceAsync(new(s.Id,ctx)); var second=await t.Engine.PostSalesInvoiceAsync(new(s.Id,ctx));
        Assert.False(first.WasAlreadyPosted); Assert.True(second.WasAlreadyPosted); Assert.Equal(first.JournalEntryId,second.JournalEntryId);
        Assert.Equal(1,await t.Db.JournalEntries.CountAsync(x=>x.SourceDocumentId==s.Id&&x.ReversedEntryId==null));
        Assert.Equal(1,await t.Db.StockLedgerEntries.CountAsync(x=>x.SourceDocumentId==s.Id&&x.MovementType==StockMovementType.SaleIssue));
    }

    [Fact]
    public async Task Required_Weighted_Average_Scenario_Is_Exact()
    {
        await using var t=await TestDatabase.CreateAsync();
        var p1=await t.Purchase("LOC-1",10m,100m); await t.Engine.PostPurchaseInvoiceAsync(new(p1.Id,t.Context("p1"))); t.Clock.Advance();
        var p2=await t.Purchase("LOC-2",10m,200m); await t.Engine.PostPurchaseInvoiceAsync(new(p2.Id,t.Context("p2"))); t.Clock.Advance();
        var s=await t.Sale("LOC-3",5m,250m); await t.Engine.PostSalesInvoiceAsync(new(s.Id,t.Context("s1")));
        var ledger=(await t.Db.StockLedgerEntries.AsNoTracking().Where(x=>x.ItemId==t.Item.Id).ToListAsync()).OrderBy(x=>x.PostedAtUtc).ThenBy(x=>x.Id).ToList();
        var state=new InventoryValuationEngine(new CurrencyPolicyService()).CalculateState(ledger,t.Company.GetPolicy());
        var lines=await t.Db.JournalLines.AsNoTracking().ToListAsync();
        Assert.Equal(15m,state.QuantityOnHand); Assert.Equal(150m,state.AverageUnitCost);
        Assert.Equal(750m,lines.Where(x=>x.AccountId==t.Cogs.Id).Sum(x=>x.Debit));
        Assert.Equal(1250m,lines.Where(x=>x.AccountId==t.Sales.Id).Sum(x=>x.Credit));
        Assert.Equal(lines.Sum(x=>x.Debit),lines.Sum(x=>x.Credit));
    }

    [Fact]
    public async Task Posted_Journal_And_Audit_Cannot_Be_Changed()
    {
        await using var t=await TestDatabase.CreateAsync();
        var p=await t.Purchase("LOC-1",10m,100m); var r=await t.Engine.PostPurchaseInvoiceAsync(new(p.Id,t.Context("immut")));
        var journal=await t.Db.JournalEntries.SingleAsync(x=>x.Id==r.JournalEntryId); t.Db.Entry(journal).Property(x=>x.Description).CurrentValue="tamper";
        await Assert.ThrowsAsync<SecurityException>(()=>t.Db.SaveChangesAsync()); t.Db.ChangeTracker.Clear();
        var audit=await t.Db.AuditLogs.SingleAsync(x=>x.EntityId==p.Id); t.Db.Remove(audit);
        await Assert.ThrowsAsync<SecurityException>(()=>t.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task Raw_SQL_Trigger_Blocks_Posted_Journal_Update()
    {
        await using var t=await TestDatabase.CreateAsync();
        var p=await t.Purchase("LOC-1",10m,100m); var r=await t.Engine.PostPurchaseInvoiceAsync(new(p.Id,t.Context("raw")));
        t.Db.ChangeTracker.Clear();
        var ex=await Assert.ThrowsAsync<SqliteException>(()=>t.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE JournalEntries SET Description = {"tamper"} WHERE Id = {r.JournalEntryId}"));
        Assert.Contains("SECURITY",ex.Message,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Raw_SQL_Trigger_Blocks_Changing_Line_Of_Posted_Invoice()
    {
        await using var t=await TestDatabase.CreateAsync();
        var p=await t.Purchase("LOC-1",10m,100m);
        await t.Engine.PostPurchaseInvoiceAsync(new(p.Id,t.Context("posted-line-guard")));
        t.Db.ChangeTracker.Clear();
        var line=await t.Db.PurchaseInvoiceLines.AsNoTracking().SingleAsync(x=>x.PurchaseInvoiceId==p.Id);
        var ex=await Assert.ThrowsAsync<SqliteException>(()=>t.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE PurchaseInvoiceLines SET Quantity = {11m} WHERE Id = {line.Id}"));
        Assert.Contains("SECURITY",ex.Message,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reversal_Restores_Stock_Without_Deleting_Original()
    {
        await using var t=await TestDatabase.CreateAsync();
        var p=await t.Purchase("LOC-1",10m,100m); await t.Engine.PostPurchaseInvoiceAsync(new(p.Id,t.Context("p"))); t.Clock.Advance();
        var s=await t.Sale("LOC-2",5m,250m); var posted=await t.Engine.PostSalesInvoiceAsync(new(s.Id,t.Context("s"))); t.Clock.Advance();
        var reversed=await t.Engine.ReversePostedDocumentAsync(new(s.Id,t.Context("r")));
        var journals=await t.Db.JournalEntries.AsNoTracking().Where(x=>x.SourceDocumentId==s.Id).ToListAsync();
        var stock=await t.Db.StockLedgerEntries.AsNoTracking().Where(x=>x.ItemId==t.Item.Id).ToListAsync();
        Assert.Equal(2,journals.Count); Assert.Equal(posted.JournalEntryId,journals.Single(x=>x.Id==reversed.JournalEntryId).ReversedEntryId);
        Assert.Equal(10m,stock.Sum(x=>x.QuantityDelta)); Assert.True(await t.Db.DocumentReversalRecords.AnyAsync(x=>x.SourceDocumentId==s.Id));
    }

    [Fact]
    public async Task Transaction_Rolls_Back_Audit_And_Outbox_On_Failure()
    {
        await using var t=await TestDatabase.CreateAsync(); var uow=new SqliteUnitOfWork(t.Db); var entityId=Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await uow.ExecuteInTransactionAsync<int>(async ct=>{
            t.Db.Add(new AuditLog(t.Company.Id,t.Branch.Id,t.User.Id,t.UserContext.DeviceId,"Probe",entityId,"Probe","{}","{}",t.Clock.UtcNow));
            t.Db.Add(new OutboxMessage(t.Company.Id,t.Branch.Id,Accounting.SharedKernel.UuidV7.NewGuid(),"probe","{}","Probe",t.Clock.UtcNow));
            await t.Db.SaveChangesAsync(ct); throw new InvalidOperationException("rollback");
        }));
        t.Db.ChangeTracker.Clear(); Assert.False(await t.Db.AuditLogs.AnyAsync(x=>x.EntityId==entityId)); Assert.False(await t.Db.OutboxMessages.AnyAsync(x=>x.IdempotencyKey=="probe"));
    }
}
