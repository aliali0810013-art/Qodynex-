using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Accounting.Infrastructure.Local.Migrations;

[DbContext(typeof(LocalAccountingDbContext))]
[Migration("20261110050000_UtcTicksAndOrderingIndexes")]
public sealed class UtcTicksAndOrderingIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Historical EF SQLite DateTimeOffset columns were TEXT. Migrate values
        // to UTC ticks without changing journal/stock identities or amounts.
        // SQLite 3.35+ supports DROP COLUMN (Microsoft.Data.Sqlite bundles newer).
        migrationBuilder.Sql("DROP INDEX IF EXISTS IX_StockLedgerEntries_Tenant_Item_Warehouse_Date;");
        var dateColumns = new (string Table, string Column, bool Nullable)[]
        {
            ("Documents", "DocumentDateUtc", false),
            ("Documents", "PostedAtUtc", true),
            ("JournalEntries", "EntryDateUtc", false),
            ("JournalEntries", "PostedAtUtc", true),
            ("StockLedgerEntries", "PostedAtUtc", false),
            ("AuditLogs", "TimestampUtc", false),
            ("OutboxMessages", "CreatedAtUtc", false),
            ("OutboxMessages", "LastAttemptAtUtc", true),
            ("OfficialSequenceAssignments", "AssignedAtUtc", false),
            ("DocumentReversalRecords", "CreatedAtUtc", false),
            ("LocalSyncStates", "LastSuccessfulSyncUtc", true),
            ("LocalSyncConflicts", "ServerTimestampUtc", false)
        };

        foreach (var (table, column, nullable) in dateColumns)
        {
            // strftime('%s') handles original UTC offsets. REAL is used only to
            // parse up to 7 decimal digits; ROUND recovers exact 100ns ticks.
            // TYPEOF guards databases that already contain numeric timestamps.
            var original = $"\"{column}\"";
            var fraction = $"CASE WHEN instr({original}, '.') > 0 THEN CAST(ROUND(CAST('0.' || substr({original}, instr({original}, '.') + 1) AS REAL) * 10000000) AS INTEGER) ELSE 0 END";
            var ticks = $"CASE WHEN typeof({original}) = 'integer' THEN {original} WHEN {original} IS NULL THEN NULL ELSE (CAST(strftime('%s', {original}) AS INTEGER) * 10000000 + 621355968000000000 + {fraction}) END";
            migrationBuilder.Sql($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}__UtcTicks\" INTEGER {(nullable ? "NULL" : "NOT NULL DEFAULT 0")};");
            migrationBuilder.Sql($"UPDATE \"{table}\" SET \"{column}__UtcTicks\" = {ticks};");
            migrationBuilder.Sql($"ALTER TABLE \"{table}\" DROP COLUMN \"{column}\";");
            migrationBuilder.Sql($"ALTER TABLE \"{table}\" RENAME COLUMN \"{column}__UtcTicks\" TO \"{column}\";");
        }

        migrationBuilder.Sql("CREATE INDEX IX_StockLedgerEntries_Tenant_Item_Warehouse_Date ON StockLedgerEntries (CompanyId, BranchId, ItemId, WarehouseId, PostedAtUtc, Id);");
        migrationBuilder.Sql("CREATE INDEX IX_StockLedgerEntries_Tenant_Source_Date ON StockLedgerEntries (CompanyId, BranchId, SourceDocumentId, PostedAtUtc, Id);");
        migrationBuilder.Sql("CREATE INDEX IX_JournalEntries_Tenant_Status_Date ON JournalEntries (CompanyId, BranchId, Status, EntryDateUtc, Id);");
        migrationBuilder.Sql("CREATE INDEX IX_JournalLines_Tenant_Account_Entry ON JournalLines (CompanyId, BranchId, AccountId, JournalEntryId, Id);");
        migrationBuilder.Sql("CREATE INDEX IX_OutboxMessages_Tenant_Status_Date ON OutboxMessages (CompanyId, BranchId, Status, CreatedAtUtc, Id);");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("A lossless downgrade from UTC tick storage to legacy SQLite TEXT is not supported.");
}
