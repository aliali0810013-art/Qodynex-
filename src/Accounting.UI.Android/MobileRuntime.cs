using System.Net.Http.Headers;
using Accounting.Application;
using Accounting.Domain;
using Accounting.Infrastructure.Local;
using Accounting.Modules;
using Accounting.SharedKernel;
using Accounting.Sync.Client;
using Microsoft.EntityFrameworkCore;

namespace Accounting.UI.Android;

public sealed class MobileSessionSettings
{
    private const string AccessTokenKey = "qodynex.access_token";

    public string ServerUrl { get => Preferences.Default.Get(nameof(ServerUrl), string.Empty); set => Preferences.Default.Set(nameof(ServerUrl), value.Trim()); }
    public Guid CompanyId { get => ReadGuid(nameof(CompanyId)); set => Preferences.Default.Set(nameof(CompanyId), value.ToString("D")); }
    public Guid BranchId { get => ReadGuid(nameof(BranchId)); set => Preferences.Default.Set(nameof(BranchId), value.ToString("D")); }
    public Guid UserId { get => ReadGuid(nameof(UserId)); set => Preferences.Default.Set(nameof(UserId), value.ToString("D")); }
    public Guid DeviceId { get => ReadGuid(nameof(DeviceId)); set => Preferences.Default.Set(nameof(DeviceId), value.ToString("D")); }
    public string DeviceName { get => Preferences.Default.Get(nameof(DeviceName), $"{DeviceInfo.Manufacturer} {DeviceInfo.Model}"); set => Preferences.Default.Set(nameof(DeviceName), value.Trim()); }

    public bool HasIdentity => CompanyId != Guid.Empty && BranchId != Guid.Empty && UserId != Guid.Empty && DeviceId != Guid.Empty;
    public bool HasServer => Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    public Task<string?> GetAccessTokenAsync() => SecureStorage.Default.GetAsync(AccessTokenKey);
    public Task SetAccessTokenAsync(string token) => SecureStorage.Default.SetAsync(AccessTokenKey, token.Trim());
    public void ClearAccessToken() => SecureStorage.Default.Remove(AccessTokenKey);

    private static Guid ReadGuid(string key) => Guid.TryParse(Preferences.Default.Get(key, string.Empty), out var id) ? id : Guid.Empty;
}

public sealed class MobileRuntime : IAsyncDisposable
{
    private readonly MobileSessionSettings _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _syncLifetime;
    private HttpClient? _http;
    private ISyncTransport? _transport;

    public LocalAccountingDbContext? Db { get; private set; }
    public UserContext UserContext { get; private set; } = null!;
    public MobileAccountingFacade Facade { get; private set; } = null!;
    public LocalSyncStore SyncStore { get; private set; } = null!;
    public BackgroundSyncCoordinator? SyncCoordinator { get; private set; }
    public ModuleRegistry ModuleRegistry { get; private set; } = null!;
    public IReadOnlyList<string> EnabledModules { get; private set; } = Array.Empty<string>();
    public string CompanyName { get; private set; } = string.Empty;
    public string BranchName { get; private set; } = string.Empty;
    public bool IsInitialized => Db is not null;

    public MobileRuntime(MobileSessionSettings settings) => _settings = settings;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsInitialized) return;
            if (!_settings.HasIdentity) throw new InvalidOperationException("هوية الشركة/الفرع/المستخدم/الجهاز غير مهيأة.");

            UserContext = UserContext.Authenticated(_settings.UserId, _settings.CompanyId, _settings.BranchId, _settings.DeviceId);
            var path = Path.Combine(FileSystem.AppDataDirectory, $"qodynex-{_settings.CompanyId:N}-{_settings.BranchId:N}.db");
            var options = new DbContextOptionsBuilder<LocalAccountingDbContext>()
                .UseSqlite($"Data Source={path}")
                .AddInterceptors(new ImmutabilityInterceptor())
                .Options;
            Db = new LocalAccountingDbContext(options);
            await new LocalDatabaseBootstrapper(Db).InitializeAsync(cancellationToken).ConfigureAwait(false);
            SyncStore = new LocalSyncStore(Db);

            var token = await _settings.GetAccessTokenAsync().ConfigureAwait(false);
            if (_settings.HasServer && !string.IsNullOrWhiteSpace(token))
            {
                _http = new HttpClient { BaseAddress = new Uri(_settings.ServerUrl), Timeout = TimeSpan.FromSeconds(30) };
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                _transport = new HttpSyncTransport(_http);
                try
                {
                    await _transport.RegisterDeviceAsync(new DeviceRegistrationRequest(
                        _settings.DeviceId, _settings.DeviceName, "Android", AppInfo.Current.VersionString,
                        SyncProtocol.CurrentClientSchemaVersion, SyncProtocol.CurrentProtocolVersion), cancellationToken).ConfigureAwait(false);
                    var bootstrap = await _transport.BootstrapAsync(new TenantBootstrapRequest(
                        SyncProtocol.CurrentClientSchemaVersion, SyncProtocol.CurrentProtocolVersion, _settings.DeviceId), cancellationToken).ConfigureAwait(false);
                    await SyncStore.ApplyBootstrapAsync(UserContext, bootstrap, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    if (!await Db.Companies.AsNoTracking().AnyAsync(x => x.Id == _settings.CompanyId, cancellationToken).ConfigureAwait(false))
                        throw new InvalidOperationException("يلزم اتصال أولي ناجح بالخادم لتهيئة هذا الجهاز قبل العمل دون إنترنت.", ex);
                }
            }

            if (!await Db.Companies.AsNoTracking().AnyAsync(x => x.Id == _settings.CompanyId, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("لا توجد بيانات تهيئة محلية. اتصل بالخادم ونفذ Bootstrap أولاً.");

            await BuildServicesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ForceBootstrapAsync(CancellationToken cancellationToken = default)
    {
        if (_transport is null) throw new InvalidOperationException("لا يوجد اتصال خادم مهيأ.");
        var bootstrap = await _transport.BootstrapAsync(new TenantBootstrapRequest(
            SyncProtocol.CurrentClientSchemaVersion, SyncProtocol.CurrentProtocolVersion, _settings.DeviceId), cancellationToken).ConfigureAwait(false);
        await SyncStore.ApplyBootstrapAsync(UserContext, bootstrap, cancellationToken).ConfigureAwait(false);
        await BuildServicesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SyncCycleResult?> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        if (SyncCoordinator is null) return null;
        return await SyncCoordinator.RunOnceAsync(cancellationToken).ConfigureAwait(false);
    }

    public void StartSyncLoop()
    {
        if (SyncCoordinator is null || _syncLifetime is not null) return;
        _syncLifetime = new CancellationTokenSource();
        _ = Task.Run(() => SyncCoordinator.RunLoopAsync(_syncLifetime.Token), _syncLifetime.Token);
    }

    public void StopSyncLoop()
    {
        _syncLifetime?.Cancel();
        _syncLifetime?.Dispose();
        _syncLifetime = null;
    }

    private async Task BuildServicesAsync(CancellationToken cancellationToken)
    {
        if (Db is null) throw new InvalidOperationException("Local database is not initialized.");
        var store = new EfAccountingDataStore(Db);
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
        ModuleRegistry = new ModuleRegistry(modules);

        var state = await Db.LocalSyncStates.AsNoTracking().SingleOrDefaultAsync(
            x => x.CompanyId == UserContext.CompanyId && x.BranchId == UserContext.BranchId && x.DeviceId == UserContext.DeviceId,
            cancellationToken).ConfigureAwait(false);
        EnabledModules = (state?.EnabledModulesCsv ?? "RETAIL")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToUpperInvariant()).Distinct().ToArray();
        _ = ModuleRegistry.ResolveEnabled(EnabledModules);
        CompanyName = state?.CompanyName ?? string.Empty;
        BranchName = state?.BranchName ?? string.Empty;

        var posting = new AccountingPostingEngine(store, new SqliteUnitOfWork(Db), new PermissionGuard(new EfUserAuthorizationReader(Db)),
            new DocumentPostingRuleRegistry(ModuleRegistry.PostingRules(EnabledModules)), currency, inventory, new SystemClock());
        Facade = new MobileAccountingFacade(Db, posting, queries, new LocalTempNumberGenerator(Db), UserContext);
        SyncStore = new LocalSyncStore(Db);
        SyncCoordinator = _transport is null
            ? null
            : new BackgroundSyncCoordinator(_transport, SyncStore, new SystemNetworkAvailability(), UserContext, new SystemClock());
    }

    public async ValueTask DisposeAsync()
    {
        StopSyncLoop();
        _http?.Dispose();
        if (Db is not null) await Db.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
