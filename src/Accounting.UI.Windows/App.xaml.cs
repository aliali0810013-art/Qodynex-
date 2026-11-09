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
    private HttpClient? _httpClient;

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
            var dbPath = Environment.GetEnvironmentVariable("QODYNEX_SQLITE_PATH")
                         ?? Path.Combine(root, $"accounting-{companyId:N}-{branchId:N}.db");
            var options = new DbContextOptionsBuilder<LocalAccountingDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .AddInterceptors(new ImmutabilityInterceptor())
                .Options;
            _db = new LocalAccountingDbContext(options);
            await new LocalDatabaseBootstrapper(_db).InitializeAsync().ConfigureAwait(true);
            var localSync = new LocalSyncStore(_db);

            ISyncTransport? transport = null;
            var serverUrl = Environment.GetEnvironmentVariable("QODYNEX_SERVER_URL");
            var token = Environment.GetEnvironmentVariable("QODYNEX_ACCESS_TOKEN");
            if (Uri.TryCreate(serverUrl, UriKind.Absolute, out var baseUri)
                && baseUri.Scheme == Uri.UriSchemeHttps
                && !string.IsNullOrWhiteSpace(token))
            {
                _httpClient = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                transport = new HttpSyncTransport(_httpClient);
                try
                {
                    await transport.RegisterDeviceAsync(new DeviceRegistrationRequest(
                        deviceId,
                        Environment.GetEnvironmentVariable("QODYNEX_DEVICE_NAME") ?? Environment.MachineName,
                        "Windows",
                        typeof(App).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                        SyncProtocol.CurrentClientSchemaVersion,
                        SyncProtocol.CurrentProtocolVersion)).ConfigureAwait(true);

                    var bootstrap = await transport.BootstrapAsync(new TenantBootstrapRequest(
                        SyncProtocol.CurrentClientSchemaVersion,
                        SyncProtocol.CurrentProtocolVersion,
                        deviceId)).ConfigureAwait(true);
                    await localSync.ApplyBootstrapAsync(userContext, bootstrap).ConfigureAwait(true);
                }
                catch (Exception ex) when ((ex is HttpRequestException && ex is not SyncTransportException) || ex is TaskCanceledException)
                {
                    if (!await _db.Companies.AsNoTracking().AnyAsync(x => x.Id == companyId).ConfigureAwait(true))
                        throw new InvalidOperationException("يلزم اتصال أولي ناجح بالخادم لتهيئة Windows قبل العمل دون إنترنت.", ex);
                }
            }

            if (!await _db.Companies.AsNoTracking().AnyAsync(x => x.Id == companyId).ConfigureAwait(true))
                throw new InvalidOperationException("لا توجد تهيئة محلية. وفر HTTPS server + JWT صالحاً لإجراء Bootstrap أول مرة.");

            var store = new EfAccountingDataStore(_db);
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

            var state = await _db.LocalSyncStates.AsNoTracking().SingleOrDefaultAsync(
                x => x.CompanyId == companyId && x.BranchId == branchId && x.DeviceId == deviceId).ConfigureAwait(true);
            var enabledModules = (state?.EnabledModulesCsv ?? RetailModule.ModuleKey)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _ = registry.ResolveEnabled(enabledModules);

            var posting = new AccountingPostingEngine(
                store,
                new SqliteUnitOfWork(_db),
                new PermissionGuard(new EfUserAuthorizationReader(_db)),
                new DocumentPostingRuleRegistry(registry.PostingRules(enabledModules)),
                currency,
                inventory,
                new SystemClock());
            var facade = new WindowsAccountingFacade(
                _db,
                posting,
                queryService,
                new LocalTempNumberGenerator(_db),
                userContext);

            BackgroundSyncCoordinator? coordinator = transport is null
                ? null
                : new BackgroundSyncCoordinator(
                    transport,
                    localSync,
                    new SystemNetworkAvailability(),
                    userContext,
                    new SystemClock());

            var shell = new ShellViewModel(
                facade,
                localSync,
                userContext,
                registry,
                enabledModules,
                state?.BranchName ?? branchId.ToString("N")[..8],
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
        _httpClient?.Dispose();
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
