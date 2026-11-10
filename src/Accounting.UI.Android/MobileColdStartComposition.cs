using Accounting.Application;
using Accounting.Domain;
using Accounting.Infrastructure.Local;
using Accounting.Modules;
using Accounting.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Accounting.UI.Android;

public sealed record MobileColdStartComposition(
    MobileAccountingFacade Facade,
    ModuleRegistry ModuleRegistry,
    IReadOnlyList<string> EnabledModules,
    string CompanyName,
    string BranchName);

public static class MobileColdStartComposer
{
    public static async Task<MobileColdStartComposition> BuildAsync(
        LocalAccountingDbContext db,
        UserContext userContext,
        CancellationToken cancellationToken = default)
    {
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

        var state = await db.LocalSyncStates.AsNoTracking().SingleOrDefaultAsync(
            x => x.CompanyId == userContext.CompanyId
                 && x.BranchId == userContext.BranchId
                 && x.DeviceId == userContext.DeviceId,
            cancellationToken).ConfigureAwait(false);
        var enabled = (state?.EnabledModulesCsv ?? RetailModule.ModuleKey)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _ = registry.ResolveEnabled(enabled);

        var posting = new AccountingPostingEngine(
            store,
            new SqliteUnitOfWork(db),
            new PermissionGuard(new EfUserAuthorizationReader(db)),
            new DocumentPostingRuleRegistry(registry.PostingRules(enabled)),
            currency,
            inventory,
            new SystemClock());
        var facade = new MobileAccountingFacade(
            db,
            posting,
            queries,
            new LocalTempNumberGenerator(db),
            userContext);

        return new MobileColdStartComposition(
            facade,
            registry,
            enabled,
            state?.CompanyName ?? string.Empty,
            state?.BranchName ?? string.Empty);
    }
}
