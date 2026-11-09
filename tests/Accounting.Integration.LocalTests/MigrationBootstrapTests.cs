using Accounting.Infrastructure.Local;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Integration.LocalTests;

public sealed class MigrationBootstrapTests
{
    [Fact]
    public async Task Initial_Migration_Creates_Schema_And_Immutability_Triggers()
    {
        var path=Path.Combine(Path.GetTempPath(), $"qodynex-{Guid.NewGuid():N}.db");
        try
        {
            var options=new DbContextOptionsBuilder<LocalAccountingDbContext>()
                .UseSqlite($"Data Source={path}")
                .AddInterceptors(new ImmutabilityInterceptor())
                .Options;

            await using (var db=new LocalAccountingDbContext(options))
            {
                await new LocalDatabaseBootstrapper(db).InitializeAsync();
            }

            await using var connection=new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync();

            await using var tableCommand=connection.CreateCommand();
            tableCommand.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Documents','JournalEntries','StockLedgerEntries','AuditLogs','OutboxMessages');";
            var tableCount=Convert.ToInt32(await tableCommand.ExecuteScalarAsync());

            await using var triggerCommand=connection.CreateCommand();
            triggerCommand.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name IN ('document_no_update','journal_no_update','stock_no_update','audit_no_update','sales_line_no_update_posted','purchase_line_no_update_posted');";
            var triggerCount=Convert.ToInt32(await triggerCommand.ExecuteScalarAsync());

            Assert.Equal(5,tableCount);
            Assert.Equal(6,triggerCount);
        }
        finally
        {
            if(File.Exists(path)) File.Delete(path);
            if(File.Exists(path+"-wal")) File.Delete(path+"-wal");
            if(File.Exists(path+"-shm")) File.Delete(path+"-shm");
        }
    }
}
