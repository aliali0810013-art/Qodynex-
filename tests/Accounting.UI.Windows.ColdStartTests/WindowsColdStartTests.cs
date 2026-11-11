using Xunit;
using System.IO;
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

            // Production UI/UX facade coverage for the seven Windows workspaces.
            var dashboard = await facade.GetDashboardAsync();
            Assert.Equal(5, dashboard.ItemCount);
            Assert.NotEmpty(dashboard.RecentJournals);

            var warehouses = await facade.GetWarehousesAsync();
            Assert.Single(warehouses);
            Assert.Equal(5, (await facade.GetInventoryGridAsync(seed.WarehouseId)).Count);
            Assert.NotEmpty(await facade.GetAccountTreeAsync());
            Assert.NotEmpty(await facade.GetRecentJournalsAsync());
            Assert.Empty(await facade.GetVoucherHistoryAsync());

            var syncCenter = await facade.GetSyncCenterAsync();
            Assert.NotNull(syncCenter);

            // ViewModel construction/refresh smoke coverage uses the real SQLite facade.
            var opened = new List<object>();
            void OpenTab(string key, string title, object vm) => opened.Add(vm);

            var dashboardVm = new DashboardViewModel(facade, OpenTab);
            await dashboardVm.RefreshAsync();
            Assert.Equal(5, dashboardVm.ItemCount);

            var vouchersVm = new VouchersWorkspaceViewModel(facade, OpenTab);
            await vouchersVm.RefreshAsync();
            Assert.Empty(vouchersVm.Rows);

            var accountsVm = new AccountsWorkspaceViewModel(facade);
            await accountsVm.RefreshAsync();
            Assert.NotEmpty(accountsVm.Accounts);
            Assert.NotEmpty(accountsVm.TrialBalance);

            var reportsVm = new ReportsSyncViewModel(facade, syncStore, user);
            await reportsVm.RefreshAsync();
            Assert.NotEmpty(reportsVm.TrialBalance);

            var inventoryVm = new InventoryWorkspaceViewModel(facade, OpenTab);
            await Task.Delay(100);
            await inventoryVm.RefreshAsync();
            Assert.Equal(5, inventoryVm.Rows.Count);

            var salesVm = new InvoiceEditorViewModel(facade, OpenTab, true, RetailModule.ModuleKey);
            var purchaseVm = new InvoiceEditorViewModel(facade, OpenTab, false, RetailModule.ModuleKey);
            var receiptVm = new VoucherEditorViewModel(facade, OpenTab, true);
            await Task.Delay(100);
            Assert.NotEmpty(salesVm.Warehouses);
            Assert.NotEmpty(salesVm.SettlementAccounts);
            Assert.NotEmpty(purchaseVm.Warehouses);
            Assert.NotEmpty(receiptVm.VoucherAccounts);
        }
        finally
        {
            DeleteSqlite(path);
        }
    }

    private static void DeleteSqlite(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch (IOException) { }
        }
    }
}
