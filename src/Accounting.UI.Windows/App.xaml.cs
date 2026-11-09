using System.Net.Http.Headers;
using System.Windows;
using Accounting.Application;
using Accounting.Domain;
using Accounting.Infrastructure.Local;
using Accounting.Modules;
using Accounting.SharedKernel;
using Accounting.Sync.Client;
using Microsoft.EntityFrameworkCore;

namespace Accounting.UI.Windows;

public partial class App : Application
{
    private LocalAccountingDbContext? _db;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var companyId = RequiredGuidEnvironment("QODYNEX_COMPANY_ID");
            var branchId = RequiredGuidEnvironment("QODYNEX_BRANCH_ID");
            var userId = RequiredGuidEnvironment("QODYNEX_USER_ID");
            var deviceId = RequiredGuidEnvironment("QODYNEX_DEVICE_ID");
            var userContext = UserContext.Authenticated(userId, companyId, branchId, deviceId);

            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Qodynex");
            Directory.CreateDirectory(root);
            var dbPath = Environment.GetEnvironmentVariable("QODYNEX_SQLITE_PATH") ?? Path.Combine(root, "accounting.db");
            var options = new DbContextOptionsBuilder<LocalAccountingDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .AddInterceptors(new ImmutabilityInterceptor())
                .Options;
            _db = new LocalAccountingDbContext(options);
            await new LocalDatabaseBootstrapper(_db).InitializeAsync().ConfigureAwait(true);

            var store = new EfAccountingDataStore(_db);
            var uow = new SqliteUnitOfWork(_db);
            var currency = new CurrencyPolicyService();
            var inventory = new InventoryValuationEngine(currency);
            var queryService = new AccountingQueryService(store, currency, inventory);
            var modules = new IActivityModule[]
            {
                new RetailModule(store),
                new WholesaleModule(store, queryService, currency),
                new ServicesModule(store),
                new RestaurantModule()
            };
            var registry = new ModuleRegistry(modules);
            var enabledModules = (Environment.GetEnvironmentVariable("QODYNEX_MODULES") ?? RetailModule.ModuleKey)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            _ = registry.ResolveEnabled(enabledModules);
            var posting = new AccountingPostingEngine(store, uow, new PermissionGuard(new EfUserAuthorizationReader(_db)),
                new DocumentPostingRuleRegistry(registry.PostingRules(enabledModules)), currency, inventory, new SystemClock());
            var facade = new WindowsAccountingFacade(_db, posting, queryService, new LocalTempNumberGenerator(_db), userContext);
            var localSync = new LocalSyncStore(_db);

            BackgroundSyncCoordinator? coordinator = null;
            var serverUrl = Environment.GetEnvironmentVariable("QODYNEX_SERVER_URL");
            var token = Environment.GetEnvironmentVariable("QODYNEX_ACCESS_TOKEN");
            if (Uri.TryCreate(serverUrl, UriKind.Absolute, out var baseUri) && !string.IsNullOrWhiteSpace(token))
            {
                var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                coordinator = new BackgroundSyncCoordinator(new HttpSyncTransport(http), localSync,
                    new SystemNetworkAvailability(), userContext, new SystemClock());
            }

            var shell = new ShellViewModel(facade, localSync, userContext, registry, enabledModules,
                Environment.GetEnvironmentVariable("QODYNEX_BRANCH_NAME") ?? branchId.ToString("N")[..8],
                Environment.GetEnvironmentVariable("QODYNEX_USER_NAME") ?? userId.ToString("N")[..8]);
            var window = new MainShellWindow(shell, coordinator);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Qodynex startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _db?.Dispose();
        base.OnExit(e);
    }

    private static Guid RequiredGuidEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!Guid.TryParse(value, out var parsed) || parsed == Guid.Empty)
            throw new InvalidOperationException($"{name} must contain a valid non-empty GUID.");
        return parsed;
    }
}
