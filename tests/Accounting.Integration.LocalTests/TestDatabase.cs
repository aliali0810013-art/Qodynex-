using Accounting.Application;
using Accounting.Domain;
using Accounting.Infrastructure.Local;
using Accounting.SharedKernel;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Integration.LocalTests;

internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    public LocalAccountingDbContext Db { get; }
    public TestClock Clock { get; }
    public AccountingPostingEngine Engine { get; }
    public Company Company { get; private set; } = null!;
    public Branch Branch { get; private set; } = null!;
    public AppUser User { get; private set; } = null!;
    public UserContext UserContext { get; private set; } = null!;
    public Warehouse Warehouse { get; private set; } = null!;
    public UnitOfMeasure Each { get; private set; } = null!;
    public Account Cash { get; private set; } = null!;
    public Account Inventory { get; private set; } = null!;
    public Account Sales { get; private set; } = null!;
    public Account Cogs { get; private set; } = null!;
    public Item Item { get; private set; } = null!;

    private TestDatabase(SqliteConnection connection, LocalAccountingDbContext db, TestClock clock, AccountingPostingEngine engine)
    { _connection=connection; Db=db; Clock=clock; Engine=engine; }

    public static async Task<TestDatabase> CreateAsync(bool allowNegativeStock=false)
    {
        var connection=new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var options=new DbContextOptionsBuilder<LocalAccountingDbContext>().UseSqlite(connection).AddInterceptors(new ImmutabilityInterceptor()).Options;
        var db=new LocalAccountingDbContext(options); await SqliteSchemaGuard.ApplyPragmasAsync(db); await db.Database.EnsureCreatedAsync(); await SqliteSchemaGuard.ApplyTriggersAsync(db);
        var clock=new TestClock(new DateTimeOffset(2026,11,10,0,0,0,TimeSpan.Zero)); var store=new EfAccountingDataStore(db); var uow=new SqliteUnitOfWork(db);
        var currency=new CurrencyPolicyService(); var engine=new AccountingPostingEngine(store,uow,new PermissionGuard(new EfUserAuthorizationReader(db)),new DocumentPostingRuleRegistry(),currency,new InventoryValuationEngine(currency),clock);
        var test=new TestDatabase(connection,db,clock,engine); await test.SeedAsync(allowNegativeStock); return test;
    }

    public PostingCommandContext Context(string key)=>new(UserContext,UuidV7.NewGuid(),key);
    public async Task<PurchaseInvoice> Purchase(string number,decimal qty,decimal price)
    { var x=new PurchaseInvoice(Company.Id,Branch.Id,number,"CORE",Clock.UtcNow,Warehouse.Id,Cash.Id); x.AddLine(Item.Id,Each.Id,qty,1m,price); Db.Add(x); await Db.SaveChangesAsync(); return x; }
    public async Task<SalesInvoice> Sale(string number,decimal qty,decimal price)
    { var x=new SalesInvoice(Company.Id,Branch.Id,number,"CORE",Clock.UtcNow,Warehouse.Id,Cash.Id); x.AddLine(Item.Id,Each.Id,qty,1m,price); Db.Add(x); await Db.SaveChangesAsync(); return x; }

    private async Task SeedAsync(bool allowNegativeStock)
    {
        Company=new Company("Qodynex",new CompanyPolicy("USD",2,MidpointRounding.AwayFromZero,InventoryValuationMethod.MovingWeightedAverage,allowNegativeStock,TaxPolicy.Disabled));
        Branch=new Branch(Company.Id,"B1","Main"); User=new AppUser(Company.Id,Branch.Id,"tester","Tester");
        var role=new Role(Company.Id,Branch.Id,"Accountant"); var p1=new Permission(Company.Id,Branch.Id,AccountingPostingEngine.PostPermission,"Post"); var p2=new Permission(Company.Id,Branch.Id,AccountingPostingEngine.ReversePermission,"Reverse");
        Cash=new Account(Company.Id,Branch.Id,"1100","Cash",AccountType.Asset,NormalBalance.Debit,true);
        Inventory=new Account(Company.Id,Branch.Id,"1200","Inventory",AccountType.Asset,NormalBalance.Debit,true);
        Sales=new Account(Company.Id,Branch.Id,"4100","Sales",AccountType.Revenue,NormalBalance.Credit,true);
        Cogs=new Account(Company.Id,Branch.Id,"5100","COGS",AccountType.Expense,NormalBalance.Debit,true);
        Each=new UnitOfMeasure(Company.Id,Branch.Id,"EA","Each"); Warehouse=new Warehouse(Company.Id,Branch.Id,"WH","Main");
        Item=new Item(Company.Id,Branch.Id,"ITEM","Item",Each.Id,Inventory.Id,Sales.Id,Cogs.Id);
        Db.AddRange(Company,Branch,User,role,p1,p2,new AppUserRole(Company.Id,Branch.Id,User.Id,role.Id),
            new RolePermissionAssignment(Company.Id,Branch.Id,role.Id,p1.Id),new RolePermissionAssignment(Company.Id,Branch.Id,role.Id,p2.Id),
            Cash,Inventory,Sales,Cogs,Each,Warehouse,Item);
        await Db.SaveChangesAsync(); UserContext=Accounting.SharedKernel.UserContext.Authenticated(User.Id,Company.Id,Branch.Id,UuidV7.NewGuid());
    }

    public async ValueTask DisposeAsync(){ await Db.DisposeAsync(); await _connection.DisposeAsync(); }
}

internal sealed class TestClock : IClock
{
    public TestClock(DateTimeOffset now)=>UtcNow=now;
    public DateTimeOffset UtcNow { get; private set; }
    public void Advance()=>UtcNow=UtcNow.AddSeconds(1);
}
