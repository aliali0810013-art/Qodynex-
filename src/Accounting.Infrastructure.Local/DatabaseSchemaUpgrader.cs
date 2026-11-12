using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Infrastructure.Local;

public sealed record DatabaseUpgradeResult(
    bool RecreatedDatabase,
    string? BackupPath,
    IReadOnlyList<string> Repairs)
{
    public static DatabaseUpgradeResult None { get; } =
        new(false, null, Array.Empty<string>());
}

/// <summary>
/// Repairs additive schema drift in databases whose EF migration history says
/// the change is already applied. Truly incompatible/corrupt legacy databases
/// are backed up and recreated once, instead of crashing the UI during startup.
/// </summary>
public sealed class DatabaseSchemaUpgrader
{
    public const string InitialMigration = "20261110004000_InitialLocalAccounting";
    public const string ActivityFieldsMigration = "20261110013000_ActivityModuleFields";
    public const string LocalSyncMigration = "20261110021500_LocalSyncState";
    public const string DocumentMetadataMigration = "20261110024500_DocumentModuleMetadata";
    public const string LocalSequenceMigration = "20261110030000_LocalDocumentSequences";
    public const string TenantProjectionMigration = "20261110033000_LocalTenantProjection";
    public const string ItemBarcodeMigration = "20261110040000_ItemBarcode";
    public const string ModuleConfigurationMigration = "20261110043000_ModuleConfiguration";
    public const string UtcTicksMigration = "20261110050000_UtcTicksAndOrderingIndexes";
    public const string BrandingMigration = "20261111193000_CompanyBrandingSettings";
    public const string ItemCommercialSettingsMigration = "20261111210000_ItemCommercialSettings";

    private readonly LocalAccountingDbContext _db;
    private readonly string? _databasePath;

    public DatabaseSchemaUpgrader(LocalAccountingDbContext db)
    {
        _db = db;
        _databasePath = ResolveDatabasePath(db.Database.GetConnectionString());
    }

    public bool CanRecoverFile => !string.IsNullOrWhiteSpace(_databasePath);

    public async Task<DatabaseUpgradeResult> PrepareAsync(CancellationToken ct = default)
    {
        if (_databasePath is null || !File.Exists(_databasePath) || new FileInfo(_databasePath).Length == 0)
            return DatabaseUpgradeResult.None;

        var repairs = new List<string>();
        var connectionString = _db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
            return DatabaseUpgradeResult.None;

        var incompatibleReason = string.Empty;
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA busy_timeout=5000;", ct).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA wal_checkpoint(FULL);", ct).ConfigureAwait(false);

            var integrity = Convert.ToString(await ScalarAsync(connection, "PRAGMA quick_check;", ct).ConfigureAwait(false));
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            {
                incompatibleReason = $"SQLite quick_check failed: {integrity}";
            }
            else
            {
                var tables = await ReadTablesAsync(connection, ct).ConfigureAwait(false);
                if (!tables.Contains("__EFMigrationsHistory"))
                {
                    if (tables.Count > 0)
                        incompatibleReason = "Legacy database has data tables but no EF migration history.";
                }
                else
                {
                    var applied = await ReadAppliedMigrationsAsync(connection, ct).ConfigureAwait(false);
                    if (applied.Contains(InitialMigration))
                    {
                        var requiredCore = new[]
                        {
                            "Companies","Branches","Accounts","BusinessPartners","Items","Warehouses",
                            "Documents","JournalEntries","JournalLines","StockLedgerEntries","OutboxMessages"
                        };
                        var missingCore = requiredCore.Where(x => !tables.Contains(x)).ToArray();
                        if (missingCore.Length > 0)
                            incompatibleReason = $"Applied initial migration is missing core tables: {string.Join(", ", missingCore)}";
                    }
                    else if (tables.Any(x => x is "Companies" or "Documents" or "JournalEntries"))
                    {
                        incompatibleReason = "Core accounting tables exist while the initial migration is not recorded.";
                    }

                    if (string.IsNullOrEmpty(incompatibleReason))
                    {
                        await RepairAppliedAdditiveMigrationsAsync(connection, applied, repairs, ct).ConfigureAwait(false);
                        var refreshedTables = await ReadTablesAsync(connection, ct).ConfigureAwait(false);

                        if (applied.Contains(UtcTicksMigration))
                        {
                            incompatibleReason = await ValidateUtcTickSchemaAsync(connection, refreshedTables, ct).ConfigureAwait(false)
                                ?? string.Empty;
                        }

                        if (string.IsNullOrEmpty(incompatibleReason))
                        {
                            var foreignKeyIssue = await FirstForeignKeyIssueAsync(connection, ct).ConfigureAwait(false);
                            if (foreignKeyIssue is not null)
                                incompatibleReason = $"Foreign-key integrity failure: {foreignKeyIssue}";
                        }
                    }
                }
            }

            await connection.CloseAsync().ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(incompatibleReason))
            return await BackupAndResetAsync(incompatibleReason, ct).ConfigureAwait(false);

        return new DatabaseUpgradeResult(false, null, repairs);
    }

    public async Task<DatabaseUpgradeResult> BackupAndResetAsync(
        string reason,
        CancellationToken ct = default)
    {
        if (_databasePath is null)
            throw new InvalidOperationException("The SQLite database is not file-backed and cannot be recovered automatically.");

        try { await _db.Database.CloseConnectionAsync().ConfigureAwait(false); } catch (Exception ignoredError) { System.Diagnostics.Debug.WriteLine(ignoredError); }
        SqliteConnection.ClearAllPools();

        if (!File.Exists(_databasePath))
            return DatabaseUpgradeResult.None;

        var directory = Path.GetDirectoryName(_databasePath) ?? Environment.CurrentDirectory;
        Directory.CreateDirectory(directory);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        var backup = Path.Combine(directory, $"qodynex_legacy_backup_{stamp}.db");
        File.Copy(_databasePath, backup, overwrite: false);

        var note = backup + ".txt";
        await File.WriteAllTextAsync(
            note,
            $"Qodynex automatic SQLite recovery{Environment.NewLine}UTC: {DateTimeOffset.UtcNow:O}{Environment.NewLine}Reason: {reason}",
            ct).ConfigureAwait(false);

        DeleteIfExists(_databasePath + "-wal");
        DeleteIfExists(_databasePath + "-shm");
        DeleteIfExists(_databasePath);

        return new DatabaseUpgradeResult(true, backup, new[] { $"Legacy database backed up: {backup}" });
    }

    public static bool IsRecoverableSchemaFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite && sqlite.SqliteErrorCode is 1 or 11 or 26)
                return true;

            var message = current.Message;
            if (message.Contains("no such table", StringComparison.OrdinalIgnoreCase)
                || message.Contains("no such column", StringComparison.OrdinalIgnoreCase)
                || message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)
                || message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                || message.Contains("malformed", StringComparison.OrdinalIgnoreCase)
                || message.Contains("file is not a database", StringComparison.OrdinalIgnoreCase)
                || message.Contains("database disk image is malformed", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static async Task RepairAppliedAdditiveMigrationsAsync(
        SqliteConnection connection,
        HashSet<string> applied,
        List<string> repairs,
        CancellationToken ct)
    {
        if (applied.Contains(ActivityFieldsMigration))
        {
            await EnsureColumnAsync(connection, "BusinessPartners", "CreditLimit", "TEXT NOT NULL DEFAULT '0'", repairs, ct).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "Items", "ItemKind", "INTEGER NOT NULL DEFAULT 1", repairs, ct).ConfigureAwait(false);
        }

        if (applied.Contains(DocumentMetadataMigration))
            await EnsureColumnAsync(connection, "Documents", "ModuleMetadataJson", "TEXT NOT NULL DEFAULT '{}'", repairs, ct).ConfigureAwait(false);

        if (applied.Contains(ItemBarcodeMigration))
        {
            await EnsureColumnAsync(connection, "Items", "Barcode", "TEXT NULL", repairs, ct).ConfigureAwait(false);
            await ExecuteAsync(connection,
                "CREATE INDEX IF NOT EXISTS IX_Items_CompanyId_BranchId_Barcode ON Items (CompanyId, BranchId, Barcode);", ct).ConfigureAwait(false);
        }

        if (applied.Contains(LocalSyncMigration))
            await EnsureLocalSyncTablesAsync(connection, applied, repairs, ct).ConfigureAwait(false);

        if (applied.Contains(TenantProjectionMigration))
        {
            await EnsureColumnAsync(connection, "LocalSyncStates", "CompanyName", "TEXT NOT NULL DEFAULT ''", repairs, ct).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "LocalSyncStates", "BranchName", "TEXT NOT NULL DEFAULT ''", repairs, ct).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "LocalSyncStates", "EnabledModulesCsv", "TEXT NOT NULL DEFAULT 'RETAIL'", repairs, ct).ConfigureAwait(false);
        }

        if (applied.Contains(LocalSequenceMigration))
        {
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS LocalSequenceCounters (
                    Id TEXT NOT NULL PRIMARY KEY,
                    CompanyId TEXT NOT NULL,
                    BranchId TEXT NOT NULL,
                    DeviceId TEXT NOT NULL,
                    DocumentType TEXT NOT NULL,
                    CurrentValue INTEGER NOT NULL
                );
                """, ct).ConfigureAwait(false);
            await ExecuteAsync(connection, """
                CREATE UNIQUE INDEX IF NOT EXISTS IX_LocalSequenceCounters_Tenant_Device_Document
                ON LocalSequenceCounters (CompanyId, BranchId, DeviceId, DocumentType);
                """, ct).ConfigureAwait(false);
        }

        if (applied.Contains(ModuleConfigurationMigration))
        {
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS LocalModuleConfigurations (
                    Id TEXT NOT NULL PRIMARY KEY,
                    CompanyId TEXT NOT NULL,
                    BranchId TEXT NOT NULL,
                    ModuleKey TEXT NOT NULL,
                    ConfigurationJson TEXT NOT NULL
                );
                """, ct).ConfigureAwait(false);
            await ExecuteAsync(connection, """
                CREATE UNIQUE INDEX IF NOT EXISTS IX_LocalModuleConfigurations_Tenant_Module
                ON LocalModuleConfigurations (CompanyId, BranchId, ModuleKey);
                """, ct).ConfigureAwait(false);
        }

        if (applied.Contains(BrandingMigration))
        {
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS LocalCompanyBrandingSettings (
                    Id TEXT NOT NULL PRIMARY KEY,
                    CompanyId TEXT NOT NULL,
                    BranchId TEXT NOT NULL,
                    ArabicName TEXT NOT NULL DEFAULT '',
                    EnglishName TEXT NOT NULL DEFAULT '',
                    BusinessDescription TEXT NOT NULL DEFAULT '',
                    LogoBytes BLOB NULL,
                    LogoFileName TEXT NULL,
                    LogoMimeType TEXT NULL,
                    City TEXT NOT NULL DEFAULT '',
                    Address TEXT NOT NULL DEFAULT '',
                    Phones TEXT NOT NULL DEFAULT '',
                    Email TEXT NOT NULL DEFAULT '',
                    TaxNumber TEXT NOT NULL DEFAULT '',
                    CommercialRegistration TEXT NOT NULL DEFAULT '',
                    FooterNotes TEXT NOT NULL DEFAULT '',
                    CurrencyCode TEXT NOT NULL DEFAULT 'YER',
                    CurrencySymbol TEXT NOT NULL DEFAULT 'YER',
                    TaxEnabled INTEGER NOT NULL DEFAULT 0,
                    TaxRate TEXT NOT NULL DEFAULT '0',
                    AllowNegativeStock INTEGER NOT NULL DEFAULT 0,
                    DarkThemeEnabled INTEGER NOT NULL DEFAULT 0,
                    UpdatedAtUtcTicks INTEGER NOT NULL DEFAULT 0
                );
                """, ct).ConfigureAwait(false);
            await ExecuteAsync(connection, """
                CREATE UNIQUE INDEX IF NOT EXISTS IX_LocalCompanyBrandingSettings_Tenant
                ON LocalCompanyBrandingSettings (CompanyId, BranchId);
                """, ct).ConfigureAwait(false);
        }

        if (applied.Contains(ItemCommercialSettingsMigration))
        {
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS LocalItemCommercialSettings (
                    Id TEXT NOT NULL PRIMARY KEY,
                    CompanyId TEXT NOT NULL,
                    BranchId TEXT NOT NULL,
                    ItemId TEXT NOT NULL,
                    BaseSalesPrice TEXT NOT NULL DEFAULT '0',
                    OpeningUnitCost TEXT NOT NULL DEFAULT '0',
                    CartonUnitId TEXT NULL,
                    CartonBarcode TEXT NULL,
                    CartonSalesPrice TEXT NOT NULL DEFAULT '0',
                    UpdatedAtUtcTicks INTEGER NOT NULL DEFAULT 0
                );
                """, ct).ConfigureAwait(false);
            await ExecuteAsync(connection, """
                CREATE UNIQUE INDEX IF NOT EXISTS IX_LocalItemCommercialSettings_Tenant_Item
                ON LocalItemCommercialSettings (CompanyId, BranchId, ItemId);
                """, ct).ConfigureAwait(false);
            await ExecuteAsync(connection, """
                CREATE INDEX IF NOT EXISTS IX_LocalItemCommercialSettings_Tenant_CartonBarcode
                ON LocalItemCommercialSettings (CompanyId, BranchId, CartonBarcode);
                """, ct).ConfigureAwait(false);
        }
    }

    private static async Task EnsureLocalSyncTablesAsync(
        SqliteConnection connection,
        HashSet<string> applied,
        List<string> repairs,
        CancellationToken ct)
    {
        var useTicks = applied.Contains(UtcTicksMigration);
        var lastSyncType = useTicks ? "INTEGER NULL" : "TEXT NULL";
        var serverTimestampType = useTicks ? "INTEGER NOT NULL" : "TEXT NOT NULL";

        await ExecuteAsync(connection, $"""
            CREATE TABLE IF NOT EXISTS LocalSyncStates (
                Id TEXT NOT NULL PRIMARY KEY,
                CompanyId TEXT NOT NULL,
                BranchId TEXT NOT NULL,
                DeviceId TEXT NOT NULL,
                LastPulledWatermark INTEGER NOT NULL DEFAULT 0,
                LastSuccessfulSyncUtc {lastSyncType},
                LastError TEXT NULL,
                CompanyName TEXT NOT NULL DEFAULT '',
                BranchName TEXT NOT NULL DEFAULT '',
                EnabledModulesCsv TEXT NOT NULL DEFAULT 'RETAIL'
            );
            """, ct).ConfigureAwait(false);
        await ExecuteAsync(connection, $"""
            CREATE TABLE IF NOT EXISTS LocalSyncConflicts (
                Id TEXT NOT NULL PRIMARY KEY,
                CompanyId TEXT NOT NULL,
                BranchId TEXT NOT NULL,
                OperationId TEXT NOT NULL,
                DocumentId TEXT NOT NULL,
                ConflictCode TEXT NOT NULL,
                Message TEXT NOT NULL,
                ServerPayloadJson TEXT NOT NULL,
                ServerTimestampUtc {serverTimestampType},
                IsResolved INTEGER NOT NULL DEFAULT 0
            );
            """, ct).ConfigureAwait(false);

        await ExecuteAsync(connection, """
            CREATE UNIQUE INDEX IF NOT EXISTS IX_LocalSyncStates_CompanyId_BranchId_DeviceId
            ON LocalSyncStates (CompanyId, BranchId, DeviceId);
            """, ct).ConfigureAwait(false);
        await ExecuteAsync(connection, """
            CREATE UNIQUE INDEX IF NOT EXISTS IX_LocalSyncConflicts_CompanyId_BranchId_OperationId
            ON LocalSyncConflicts (CompanyId, BranchId, OperationId);
            """, ct).ConfigureAwait(false);

        repairs.Add("Verified LocalSyncStates/LocalSyncConflicts.");
    }

    private static async Task<string?> ValidateUtcTickSchemaAsync(
        SqliteConnection connection,
        HashSet<string> tables,
        CancellationToken ct)
    {
        var dateColumns = new (string Table, string Column)[]
        {
            ("Documents", "DocumentDateUtc"),
            ("Documents", "PostedAtUtc"),
            ("JournalEntries", "EntryDateUtc"),
            ("JournalEntries", "PostedAtUtc"),
            ("StockLedgerEntries", "PostedAtUtc"),
            ("AuditLogs", "TimestampUtc"),
            ("OutboxMessages", "CreatedAtUtc"),
            ("OutboxMessages", "LastAttemptAtUtc"),
            ("OfficialSequenceAssignments", "AssignedAtUtc"),
            ("DocumentReversalRecords", "CreatedAtUtc"),
            ("LocalSyncStates", "LastSuccessfulSyncUtc"),
            ("LocalSyncConflicts", "ServerTimestampUtc")
        };

        foreach (var (table, column) in dateColumns)
        {
            if (!tables.Contains(table))
                return $"UTC-ticks migration is recorded but table {table} is missing.";

            var columns = await ReadColumnsAsync(connection, table, ct).ConfigureAwait(false);
            if (!columns.TryGetValue(column, out var type))
                return $"UTC-ticks migration is recorded but {table}.{column} is missing.";
            if (!type.Contains("INT", StringComparison.OrdinalIgnoreCase))
                return $"UTC-ticks migration is recorded but {table}.{column} has legacy type {type}.";
        }

        return null;
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string definition,
        List<string> repairs,
        CancellationToken ct)
    {
        var tables = await ReadTablesAsync(connection, ct).ConfigureAwait(false);
        if (!tables.Contains(table))
            throw new InvalidOperationException($"Cannot repair missing column because table {table} is absent.");

        var columns = await ReadColumnsAsync(connection, table, ct).ConfigureAwait(false);
        if (columns.ContainsKey(column)) return;

        await ExecuteAsync(connection,
            $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition};", ct).ConfigureAwait(false);
        repairs.Add($"Added {table}.{column}.");
    }

    private static async Task<HashSet<string>> ReadTablesAsync(SqliteConnection connection, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<Dictionary<string,string>> ReadColumnsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken ct)
    {
        var result = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result[reader.GetString(1)] = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
        return result;
    }

    private static async Task<HashSet<string>> ReadAppliedMigrationsAsync(SqliteConnection connection, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<string?> FirstForeignKeyIssueAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return $"{reader.GetValue(0)} row {reader.GetValue(1)}";
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private static string? ResolveDatabasePath(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        var builder = new SqliteConnectionStringBuilder(connectionString);
        var source = builder.DataSource;
        if (string.IsNullOrWhiteSpace(source)
            || source.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("file::memory:", StringComparison.OrdinalIgnoreCase))
            return null;
        return Path.GetFullPath(source);
    }

    private static void DeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException ioError) { System.Diagnostics.Debug.WriteLine(ioError); }
    }
}

/// <summary>CI-only fixture used by the published EXE smoke test.</summary>
public static class LegacyDatabaseSmokeFixture
{
    public static async Task CreateAsync(string path, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
        SqliteConnection.ClearAllPools();
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(candidate)) File.Delete(candidate);

        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE __EFMigrationsHistory (
                MigrationId TEXT NOT NULL PRIMARY KEY,
                ProductVersion TEXT NOT NULL
            );
            INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion)
            VALUES ('{DatabaseSchemaUpgrader.InitialMigration}', '8.0.0');
            CREATE TABLE LegacyMarker (Id INTEGER NOT NULL PRIMARY KEY, Note TEXT NULL);
            INSERT INTO LegacyMarker(Note) VALUES ('incompatible-smoke-fixture');
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
