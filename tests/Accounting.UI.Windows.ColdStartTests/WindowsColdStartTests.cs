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
            var units = await facade.GetUnitsAsync();
            var accounts = await facade.SearchAccountsAsync("");
            var baseUnit = units.First();
            var cartonUnit = units.Skip(1).FirstOrDefault();
            var inventoryAccount = accounts.First(x => x.Code.StartsWith("114", StringComparison.Ordinal));
            var salesAccount = accounts.First(x => x.Code.StartsWith("4", StringComparison.Ordinal));
            var cogsAccount = accounts.First(x => x.Code.StartsWith("5", StringComparison.Ordinal));
            var commercialItemId = await facade.CreateLocalItemAsync(
                "TEST-PRICE", "صنف سعر تجريبي", "990000001", baseUnit.Id,
                inventoryAccount.Id, salesAccount.Id, cogsAccount.Id, cartonUnit?.Id, 12m,
                125m, 80m, "990000012", 1350m);
            Assert.Equal(125m, await facade.GetSuggestedUnitPriceAsync("RETAIL", commercialItemId, 1m, 0m));
            if (cartonUnit is not null)
            {
                Assert.Equal(1350m, await facade.GetSuggestedUnitPriceAsync("RETAIL", commercialItemId, 12m, 0m));
                var cartonScan = await facade.FindItemScanAsync("990000012");
                Assert.NotNull(cartonScan);
                Assert.Equal(cartonUnit.Id, cartonScan!.PreferredUnitOfMeasureId);
            }
            Assert.Equal(2, (await facade.SearchPartnersAsync(string.Empty)).Count);
            Assert.Equal("YER", (await facade.GetPolicyAsync()).BaseCurrencyCode);
            Assert.True((await facade.GetStockPositionAsync(seed.ItemIds[0], seed.WarehouseId)).QuantityOnHand > 0m);
            Assert.NotEmpty(await facade.GetTrialBalanceAsync());

            // Production UI/UX facade coverage for the seven Windows workspaces.
            var dashboard = await facade.GetDashboardAsync();
            Assert.Equal(6, dashboard.ItemCount);
            Assert.True(dashboard.PurchaseTotal >= 0m);
            Assert.True(dashboard.CustomerReceivables >= 0m);
            Assert.True(dashboard.RecentJournals.Count <= 15);
            Assert.NotEmpty(dashboard.RecentJournals);

            var warehouses = await facade.GetWarehousesAsync();
            Assert.Single(warehouses);
            Assert.Equal(6, (await facade.GetInventoryGridAsync(seed.WarehouseId)).Count);
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


    [Fact]
    public async Task Branding_Settings_Theme_And_Printing_Are_Production_Ready()
    {
        Assert.True(ThemeManager.PaletteHasReadableContrast(false));
        Assert.True(ThemeManager.PaletteHasReadableContrast(true));

        var path = Path.Combine(Path.GetTempPath(), $"qodynex-windows-branding-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<LocalAccountingDbContext>()
                .UseSqlite($"Data Source={path}")
                .AddInterceptors(new ImmutabilityInterceptor())
                .Options;
            await using var db = new LocalAccountingDbContext(options);
            await new LocalDatabaseBootstrapper(db).InitializeAsync();
            var identity = LocalClientIdentity.Create();
            await new LocalFirstRunBootstrapper(db).EnsureDemoSeedAsync(identity);
            var user = UserContext.Authenticated(identity.UserId, identity.CompanyId, identity.BranchId, identity.DeviceId);
            var store = new EfAccountingDataStore(db);
            var currency = new CurrencyPolicyService();
            var inventory = new InventoryValuationEngine(currency);
            var queries = new AccountingQueryService(store, currency, inventory);
            var registry = new ModuleRegistry(new IActivityModule[] { new RetailModule(store) });
            var posting = new AccountingPostingEngine(store,new SqliteUnitOfWork(db),
                new PermissionGuard(new EfUserAuthorizationReader(db)),
                new DocumentPostingRuleRegistry(registry.PostingRules(new[]{RetailModule.ModuleKey})),
                currency,inventory,new SystemClock());
            var facade = new WindowsAccountingFacade(db,posting,queries,new LocalTempNumberGenerator(db),user);

            var logo = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            var branding = new CompanyBrandingSnapshot("شركة اختبار","Test Co","تجارة",logo,"صنعاء","شارع حدة","777000000",
                "test@example.com","TX-1","CR-1","شكراً لزيارتكم","YER","ر.ي",false,0m,false,false);
            await facade.SaveBrandingAsync(branding,"logo.png");
            var saved = await facade.GetBrandingAsync();
            Assert.Equal("شركة اختبار", saved.ArabicName);
            Assert.Equal(logo, saved.LogoBytes);
            Assert.Equal("صنعاء", saved.City);

            var opened = new List<object>();
            void OpenTab(string key,string title,object vm)=>opened.Add(vm);
            var vm = new InvoiceEditorViewModel(facade,OpenTab,true,RetailModule.ModuleKey);
            await Task.Delay(100);
            var item=(await facade.SearchItemsAsync(""))[0];
            await vm.AddItemAsync(item);
            var line=Assert.Single(vm.Lines);
            line.Quantity=2m; line.UnitPrice=100m; line.Discount=10m;
            Assert.Equal(190m,line.LineTotal);
            Assert.Equal(190m,vm.GrandTotal);

            Exception? printError = null;
            var printValidated = false;
            var sta = new Thread(() =>
            {
                try
                {
                    var thermal=InvoicePrintService.Build(vm,saved,true);
                    var a4=InvoicePrintService.Build(vm,saved,false);
                    Assert.NotEmpty(thermal.Blocks);
                    Assert.NotEmpty(a4.Blocks);
                    Assert.True(thermal.PageWidth < a4.PageWidth);
                    printValidated = true;
                }
                catch (Exception ex) { printError = ex; }
            });
            sta.SetApartmentState(ApartmentState.STA);
            sta.Start();
            sta.Join();
            if (printError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(printError).Throw();
            Assert.True(printValidated);
        }
        finally { DeleteSqlite(path); }
    }
    [Fact]
    public async Task Applied_Migration_Column_Drift_Is_Self_Healed_Without_Data_Loss()
    {
        var path = Path.Combine(Path.GetTempPath(), $"qodynex-heal-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<LocalAccountingDbContext>()
                .UseSqlite($"Data Source={path}")
                .AddInterceptors(new ImmutabilityInterceptor())
                .Options;
            await using var db = new LocalAccountingDbContext(options);
            var bootstrapper = new LocalDatabaseBootstrapper(db);
            await bootstrapper.InitializeAsync();

            var identity = LocalClientIdentity.Create();
            await new LocalFirstRunBootstrapper(db).EnsureDemoSeedAsync(identity);
            var before = await db.Companies.CountAsync();

            await db.Database.ExecuteSqlRawAsync("ALTER TABLE LocalSyncStates DROP COLUMN CompanyName;");
            var healed = await bootstrapper.InitializeAsync();

            Assert.False(healed.RecreatedDatabase);
            Assert.Contains(healed.Repairs, x => x.Contains("LocalSyncStates.CompanyName", StringComparison.Ordinal));
            Assert.Equal(before, await db.Companies.CountAsync());

            await using var command = db.Database.GetDbConnection().CreateCommand();
            await db.Database.OpenConnectionAsync();
            command.CommandText = "SELECT CompanyName FROM LocalSyncStates LIMIT 1;";
            _ = await command.ExecuteScalarAsync();
            await db.Database.CloseConnectionAsync();
        }
        finally { DeleteSqlite(path); }
    }

    [Fact]
    public async Task Incompatible_Legacy_Database_Is_Backed_Up_And_Recreated_Once()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"qodynex-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "qodynex-local.db");
        try
        {
            await LegacyDatabaseSmokeFixture.CreateAsync(path);
            var options = new DbContextOptionsBuilder<LocalAccountingDbContext>()
                .UseSqlite($"Data Source={path}")
                .AddInterceptors(new ImmutabilityInterceptor())
                .Options;
            await using var db = new LocalAccountingDbContext(options);
            var bootstrapper = new LocalDatabaseBootstrapper(db);
            var result = await bootstrapper.InitializeAsync();

            Assert.True(result.RecreatedDatabase);
            Assert.False(string.IsNullOrWhiteSpace(result.BackupPath));
            Assert.True(File.Exists(result.BackupPath));

            var identity = LocalClientIdentity.Create();
            await new LocalFirstRunBootstrapper(db).EnsureDemoSeedAsync(identity);
            Assert.Single(await db.Companies.ToListAsync());
            Assert.Equal(5, await db.Items.CountAsync());
        }
        finally
        {
            DeleteSqlite(path);
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Backup_Restore_Integrity_And_ServerUrl_Are_Operational()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"qodynex-maint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "live.db");
        var backupPath = Path.Combine(directory, "backup.db");

        try
        {
            var options = new DbContextOptionsBuilder<LocalAccountingDbContext>()
                .UseSqlite($"Data Source={path}")
                .AddInterceptors(new ImmutabilityInterceptor())
                .Options;
            await using var db = new LocalAccountingDbContext(options);
            await new LocalDatabaseBootstrapper(db).InitializeAsync();

            var identity = LocalClientIdentity.Create();
            await new LocalFirstRunBootstrapper(db).EnsureDemoSeedAsync(identity);
            var user = UserContext.Authenticated(identity.UserId, identity.CompanyId, identity.BranchId, identity.DeviceId);
            var store = new EfAccountingDataStore(db);
            var currency = new CurrencyPolicyService();
            var inventory = new InventoryValuationEngine(currency);
            var queries = new AccountingQueryService(store, currency, inventory);
            var registry = new ModuleRegistry(new IActivityModule[] { new RetailModule(store) });
            var posting = new AccountingPostingEngine(
                store,
                new SqliteUnitOfWork(db),
                new PermissionGuard(new EfUserAuthorizationReader(db)),
                new DocumentPostingRuleRegistry(registry.PostingRules(new[] { RetailModule.ModuleKey })),
                currency,
                inventory,
                new SystemClock());
            var facade = new WindowsAccountingFacade(
                db, posting, queries, new LocalTempNumberGenerator(db), user);

            var integrity = await facade.CheckLocalDatabaseIntegrityAsync();
            Assert.True(integrity.IsHealthy, integrity.Summary);

            var created = await facade.CreateLocalBackupAsync(backupPath);
            Assert.Equal(Path.GetFullPath(backupPath), created);
            Assert.True(File.Exists(backupPath));
            Assert.True((await LocalDatabaseMaintenanceService.ValidateSqliteFileAsync(backupPath, true)).IsHealthy);

            await facade.SaveServerUrlAsync("https://sync.qodynex.test/");
            Assert.Equal("https://sync.qodynex.test", await facade.GetServerUrlAsync());

            var pending = await facade.ScheduleLocalRestoreAsync(backupPath);
            Assert.True(File.Exists(pending));

            await db.DisposeAsync();
            LocalDatabaseMaintenanceService.ApplyPendingRestoreIfAny(path);
            Assert.False(File.Exists(path + ".restore.pending"));
            Assert.True((await LocalDatabaseMaintenanceService.ValidateSqliteFileAsync(path, true)).IsHealthy);
            Assert.NotEmpty(Directory.GetFiles(directory, "qodynex_pre_restore_*.db"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
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
