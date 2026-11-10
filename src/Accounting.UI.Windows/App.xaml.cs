using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Accounting.Application;
using Accounting.Domain;
using Accounting.Infrastructure.Local;
using Accounting.Modules;
using Accounting.SharedKernel;
using Accounting.Sync.Client;
using Microsoft.EntityFrameworkCore;

namespace Accounting.UI.Windows;

public partial class App : System.Windows.Application
{
    private LocalAccountingDbContext? _db;
    private HttpClient? _httpClient;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Qodynex");
            Directory.CreateDirectory(root);

            var (identity, localDemoMode) = await ResolveIdentityAsync(root).ConfigureAwait(true);
            var companyId = identity.CompanyId;
            var branchId = identity.BranchId;
            var userId = identity.UserId;
            var deviceId = identity.DeviceId;
            var userContext = UserContext.Authenticated(userId, companyId, branchId, deviceId);

            var configuredPath = Environment.GetEnvironmentVariable("QODYNEX_SQLITE_PATH");
            var dbPath = string.IsNullOrWhiteSpace(configuredPath)
                ? Path.Combine(root, localDemoMode
                    ? "qodynex-local.db"
                    : $"accounting-{companyId:N}-{branchId:N}.db")
                : Path.GetFullPath(configuredPath);
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath) ?? root);

            var options = new DbContextOptionsBuilder<LocalAccountingDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .AddInterceptors(new ImmutabilityInterceptor())
                .Options;

            _db = new LocalAccountingDbContext(options);
            await new LocalDatabaseBootstrapper(_db).InitializeAsync().ConfigureAwait(true);
            var localSync = new LocalSyncStore(_db);

            ISyncTransport? transport = null;
            if (localDemoMode)
            {
                await new LocalFirstRunBootstrapper(_db)
                    .EnsureDemoSeedAsync(identity)
                    .ConfigureAwait(true);
            }
            else
            {
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
                    catch (Exception ex) when ((ex is HttpRequestException && ex is not SyncTransportException)
                                                || ex is TaskCanceledException)
                    {
                        if (!await _db.Companies.AsNoTracking()
                                .AnyAsync(x => x.Id == companyId).ConfigureAwait(true))
                            throw new InvalidOperationException(
                                "تعذر الاتصال بالخادم ولا توجد تهيئة محلية محفوظة لهذا الحساب.", ex);
                    }
                }
            }

            if (!await _db.Companies.AsNoTracking()
                    .AnyAsync(x => x.Id == companyId).ConfigureAwait(true))
                throw new InvalidOperationException(
                    "تعذر العثور على بيانات الشركة محلياً. أعد تهيئة الحساب أو شغّل التطبيق بدون متغيرات ربط الخادم لاستخدام النسخة التجريبية المحلية.");

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
                state?.BranchName ?? "الفرع الرئيسي",
                localDemoMode
                    ? "مستخدم تجريبي"
                    : Environment.GetEnvironmentVariable("QODYNEX_USER_NAME") ?? userId.ToString("N")[..8]);

            var mainWindow = new MainShellWindow(shell, coordinator);
            MainWindow = mainWindow;

            // CI executes the published single-file binary with this switch to
            // prove migrations, SQLite native extraction, seed and UI composition.
            if (e.Args.Any(x => string.Equals(x, "--cold-start-check", StringComparison.OrdinalIgnoreCase)))
            {
                Shutdown(0);
                return;
            }

            mainWindow.Show();
            mainWindow.Activate();
        }
        catch (Exception ex)
        {
            if (e.Args.Any(x => string.Equals(x, "--cold-start-check", StringComparison.OrdinalIgnoreCase)))
            {
                System.Diagnostics.Debug.WriteLine(ex);
                Shutdown(1);
                return;
            }

            ShowFatal("تعذر تشغيل Qodynex", ex);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _httpClient?.Dispose();
        _db?.Dispose();
        base.OnExit(e);
    }

    private static async Task<(LocalClientIdentity Identity, bool LocalDemoMode)> ResolveIdentityAsync(string root)
    {
        var names = new[]
        {
            "QODYNEX_COMPANY_ID",
            "QODYNEX_BRANCH_ID",
            "QODYNEX_USER_ID",
            "QODYNEX_DEVICE_ID"
        };
        var values = names.Select(Environment.GetEnvironmentVariable).ToArray();
        var hasAnyServerIdentity = values.Any(x => !string.IsNullOrWhiteSpace(x));

        if (hasAnyServerIdentity)
        {
            var ids = new Guid[4];
            for (var index = 0; index < names.Length; index++)
            {
                if (!Guid.TryParse(values[index], out ids[index]) || ids[index] == Guid.Empty)
                    throw new InvalidOperationException(
                        $"عند استخدام ربط الخادم يجب ضبط {names[index]} بقيمة GUID صحيحة.");
            }

            return (new LocalClientIdentity(ids[0], ids[1], ids[2], ids[3]), false);
        }

        var identityPath = Path.Combine(root, "local-identity.json");
        if (File.Exists(identityPath))
        {
            var json = await File.ReadAllTextAsync(identityPath).ConfigureAwait(false);
            var saved = JsonSerializer.Deserialize<LocalClientIdentity>(json);
            if (saved is not null
                && saved.CompanyId != Guid.Empty
                && saved.BranchId != Guid.Empty
                && saved.UserId != Guid.Empty
                && saved.DeviceId != Guid.Empty)
                return (saved, true);
        }

        var identity = LocalClientIdentity.Create();
        await File.WriteAllTextAsync(
            identityPath,
            JsonSerializer.Serialize(identity, new JsonSerializerOptions { WriteIndented = true }))
            .ConfigureAwait(false);
        return (identity, true);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatal("خطأ غير معالج في واجهة Qodynex", e.Exception);
        e.Handled = true;
    }

    private static void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception
                        ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown fatal error.");
        ShowFatal("خطأ حرج في Qodynex", exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ShowFatal("خطأ في مهمة خلفية داخل Qodynex", e.Exception);
        e.SetObserved();
    }

    private static void ShowFatal(string title, Exception exception)
    {
        var details = exception.InnerException is null
            ? exception.Message
            : $"{exception.Message}\n\nالسبب: {exception.InnerException.Message}";
        MessageBox.Show(details, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
