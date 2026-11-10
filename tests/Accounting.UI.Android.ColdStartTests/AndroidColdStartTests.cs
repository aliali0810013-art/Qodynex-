using Xunit;
using System.IO;
using Accounting.Infrastructure.Local;
using Accounting.SharedKernel;
using Accounting.UI.Android;
using Microsoft.EntityFrameworkCore;

namespace Accounting.UI.Android.ColdStartTests;

public sealed class AndroidColdStartTests
{
    [Fact]
    public async Task Empty_Sqlite_Seeds_And_Builds_Actual_Mobile_Composition_Without_Server()
    {
        var path = Path.Combine(Path.GetTempPath(), $"qodynex-android-cold-{Guid.NewGuid():N}.db");
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

            var composition = await MobileColdStartComposer.BuildAsync(db, user);
            var facade = composition.Facade;

            Assert.NotNull(facade);
            Assert.Equal("YER", (await facade.GetPolicyAsync()).BaseCurrencyCode);
            Assert.Equal("الفرع الرئيسي", composition.BranchName);
            Assert.Contains("RETAIL", composition.EnabledModules);
            Assert.Single(await facade.GetWarehousesAsync());
            Assert.Single(await facade.GetCustomersAsync());

            var item = await facade.FindItemAsync("DEMO-001");
            Assert.NotNull(item);
            var units = await facade.GetItemUnitsAsync(item!.Id);
            Assert.Equal(2, units.Count);

            var inventory = await facade.GetInventoryAsync("DEMO-001", seed.WarehouseId);
            Assert.NotNull(inventory);
            Assert.NotNull(inventory!.Position);
            Assert.True(inventory.Position!.QuantityOnHand > 0m);
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
