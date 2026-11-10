using Xunit;
using Accounting.Application;
using Accounting.Domain;
using Accounting.Infrastructure.Local;
using Accounting.Modules;
using Accounting.SharedKernel;
using Accounting.Sync.Client;
using Accounting.UI.Windows;
using Microsoft.EntityFrameworkCore;

namespace Accounting.UI.Windows.ColdStartTests;

public sealed class WindowsColdStartTests
{
    [Fact]
    public async Task Empty_Sqlite_Seeds_And_Builds_Facade_And_Shell_Without_Server()
    {
        var path = Path.Combine(Path.GetTempPath(), $"qodynex-windows-cold-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<LocalAccountingDbContext>()
                .UseSqlite($"Data Source={path}")
                .AddInterceptors(new ImmutabilityInterceptor())
                .Options;
            await using var db = new LocalAccountingDbContext(options);
            await new LocalDatabaseBootstrapper(db).InitializeAsync();

            var identity = LocalClientIdentity.Create();
            var seed = await new LocalFirstRunBootstrapper(db).EnsureDemoSeedAsync(identity);
            var user = UserContext.Authenticated(identity.UserId, identity.CompanyId, identity.BranchId, identity.DeviceId);

            var store = new EfAccountingDataStore(db);
            var currency = new CurrencyPolicyService();
            var inventory = new InventoryValuationEngine(currency);
            var queries = new AccountingQueryService(store, currency, inventory);
            var modules = new IActivityModule[]
            {
                new RetailModule(store),
                new WholesaleModule(store, queries, currency),
                new ServicesModule(store),
                new RestaurantModule()
            };
            var registry = new ModuleRegistry(modules);
            var enabled = new[] { RetailModule.ModuleKey };
            var posting = new AccountingPostingEngine(
                store,
                new SqliteUnitOfWork(db),
                new PermissionGuard(new EfUserAuthorizationReader(db)),
                new DocumentPostingRuleRegistry(registry.PostingRules(enabled)),
                currency,
                inventory,
                new SystemClock());
            var facade = new WindowsAccountingFacade(
                db, posting, queries, new LocalTempNumberGenerator(db), user);
            var syncStore = new LocalSyncStore(db);
            var shell = new ShellViewModel(
                facade, syncStore, user, registry, enabled, "الفرع الرئيسي", "مستخدم تجريبي");

            Assert.NotNull(shell);
            Assert.NotEmpty(shell.ModuleScreens);
            Assert.Equal(5, (await facade.SearchItemsAsync(string.Empty)).Count);
            Assert.Equal(2, (await facade.SearchPartnersAsync(string.Empty)).Count);
            Assert.Equal("YER", (await facade.GetPolicyAsync()).BaseCurrencyCode);
            Assert.True((await facade.GetStockPositionAsync(seed.ItemIds[0], seed.WarehouseId)).QuantityOnHand > 0m);
            Assert.NotEmpty(await facade.GetTrialBalanceAsync());
        }
        finally
        {
            DeleteSqlite(path);
        }
    }

    private static void DeleteSqlite(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(file)) File.Delete(file);
    }
}
