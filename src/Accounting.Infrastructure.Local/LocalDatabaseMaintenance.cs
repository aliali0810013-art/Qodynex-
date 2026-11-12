using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Infrastructure.Local;

public sealed record LocalDatabaseIntegrityResult(
    bool IsHealthy,
    string QuickCheck,
    int ForeignKeyViolations,
    int UnbalancedPostedJournals,
    string Summary);

public sealed record LocalRuntimeConnectionSettings(string ServerUrl)
{
    public static LocalRuntimeConnectionSettings Empty { get; } = new(string.Empty);
}

public static class LocalRuntimeSettingsStore
{
    public static string SettingsPath(string databasePath) => databasePath + ".runtime.json";

    public static async Task<LocalRuntimeConnectionSettings> LoadAsync(
        string databasePath,
        CancellationToken ct = default)
    {
        var path = SettingsPath(databasePath);
        if (!File.Exists(path)) return LocalRuntimeConnectionSettings.Empty;

        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<LocalRuntimeConnectionSettings>(json)
                   ?? LocalRuntimeConnectionSettings.Empty;
        }
        catch (JsonException)
        {
            return LocalRuntimeConnectionSettings.Empty;
        }
    }

    public static async Task SaveAsync(
        string databasePath,
        LocalRuntimeConnectionSettings settings,
        CancellationToken ct = default)
    {
        var normalized = NormalizeServerUrl(settings.ServerUrl);
        var path = SettingsPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
        var temp = path + ".tmp";
        var json = JsonSerializer.Serialize(
            new LocalRuntimeConnectionSettings(normalized),
            new JsonSerializerOptions { WriteIndented = true });

        await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    public static string NormalizeServerUrl(string? value)
    {
        var serverUrl = value?.Trim() ?? string.Empty;
        if (serverUrl.Length == 0) return string.Empty;

        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(uri.Host))
            throw new InvalidOperationException("عنوان الخادم يجب أن يكون رابط HTTPS صحيحاً.");

        return uri.ToString().TrimEnd('/');
    }
}

public sealed class LocalDatabaseMaintenanceService
{
    private readonly LocalAccountingDbContext _db;
    private readonly string _databasePath;

    public LocalDatabaseMaintenanceService(LocalAccountingDbContext db)
    {
        _db = db;
        _databasePath = ResolveDatabasePath(db.Database.GetConnectionString())
            ?? throw new InvalidOperationException("قاعدة SQLite الحالية ليست ملفاً محلياً قابلاً للصيانة.");
    }

    public string DatabasePath => _databasePath;
    public string PendingRestorePath => _databasePath + ".restore.pending";

    public async Task<string> CreateBackupAsync(string destinationPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("مسار النسخة الاحتياطية مطلوب.", nameof(destinationPath));

        destinationPath = Path.GetFullPath(destinationPath);
        if (string.Equals(destinationPath, _databasePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("لا يمكن حفظ النسخة الاحتياطية فوق قاعدة البيانات الحالية.");

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? Environment.CurrentDirectory);
        if (File.Exists(destinationPath)) File.Delete(destinationPath);

        await _db.Database.CloseConnectionAsync().ConfigureAwait(false);
        SqliteConnection.ClearAllPools();

        await using var source = new SqliteConnection($"Data Source={_databasePath};Mode=ReadOnly");
        await using var destination = new SqliteConnection($"Data Source={destinationPath}");
        await source.OpenAsync(ct).ConfigureAwait(false);
        await destination.OpenAsync(ct).ConfigureAwait(false);
        source.BackupDatabase(destination);
        await destination.CloseAsync().ConfigureAwait(false);
        await source.CloseAsync().ConfigureAwait(false);

        var validation = await ValidateSqliteFileAsync(destinationPath, requireAccountingSchema: true, ct).ConfigureAwait(false);
        if (!validation.IsHealthy)
        {
            try { File.Delete(destinationPath); } catch (Exception ignoredError) { System.Diagnostics.Debug.WriteLine(ignoredError); }
            throw new InvalidOperationException($"فشل التحقق من النسخة الاحتياطية: {validation.Summary}");
        }

        return destinationPath;
    }

    public async Task<string> ScheduleRestoreAsync(string backupPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
            throw new ArgumentException("ملف النسخة الاحتياطية مطلوب.", nameof(backupPath));

        backupPath = Path.GetFullPath(backupPath);
        if (!File.Exists(backupPath))
            throw new FileNotFoundException("ملف النسخة الاحتياطية غير موجود.", backupPath);
        if (string.Equals(backupPath, _databasePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("الملف المحدد هو قاعدة البيانات الحالية.");

        var validation = await ValidateSqliteFileAsync(backupPath, requireAccountingSchema: true, ct).ConfigureAwait(false);
        if (!validation.IsHealthy)
            throw new InvalidOperationException($"النسخة المحددة غير صالحة للاستعادة: {validation.Summary}");

        var temp = PendingRestorePath + ".tmp";
        File.Copy(backupPath, temp, overwrite: true);
        SqliteConnection.ClearAllPools();

        var copied = await ValidateSqliteFileAsync(temp, requireAccountingSchema: true, ct).ConfigureAwait(false);
        if (!copied.IsHealthy)
        {
            try { File.Delete(temp); } catch (Exception ignoredError) { System.Diagnostics.Debug.WriteLine(ignoredError); }
            throw new InvalidOperationException($"فشل التحقق من ملف الاستعادة المؤقت: {copied.Summary}");
        }

        SqliteConnection.ClearAllPools();
        await MoveWithRetryAsync(temp, PendingRestorePath, ct).ConfigureAwait(false);
        return PendingRestorePath;
    }

    private static async Task MoveWithRetryAsync(string source, string destination, CancellationToken ct)
    {
        IOException? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (IOException ex)
            {
                last = ex;
                SqliteConnection.ClearAllPools();
                await Task.Delay(50 * (attempt + 1), ct).ConfigureAwait(false);
            }
        }

        throw last ?? new IOException("تعذر تثبيت ملف الاستعادة المؤقت.");
    }

    public async Task<LocalDatabaseIntegrityResult> CheckIntegrityAsync(CancellationToken ct = default)
    {
        var quick = await ScalarTextAsync(_db, "PRAGMA quick_check;", ct).ConfigureAwait(false);
        var foreignKeys = await CountRowsAsync(_db, "PRAGMA foreign_key_check;", ct).ConfigureAwait(false);

        var unbalanced = 0;
        if (string.Equals(quick, "ok", StringComparison.OrdinalIgnoreCase))
        {
            var sql = """
                SELECT COUNT(*)
                FROM (
                    SELECT je.Id,
                           COALESCE(SUM(CAST(jl.Debit AS REAL)), 0.0) AS DebitTotal,
                           COALESCE(SUM(CAST(jl.Credit AS REAL)), 0.0) AS CreditTotal
                    FROM JournalEntries je
                    JOIN JournalLines jl ON jl.JournalEntryId = je.Id
                    WHERE je.Status = 1
                    GROUP BY je.Id
                    HAVING ABS(
                        COALESCE(SUM(CAST(jl.Debit AS REAL)), 0.0) -
                        COALESCE(SUM(CAST(jl.Credit AS REAL)), 0.0)
                    ) > 0.000001
                );
                """;
            await using var command = _db.Database.GetDbConnection().CreateCommand();
            if (command.Connection!.State != System.Data.ConnectionState.Open)
                await _db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            try
            {
                command.CommandText = sql;
                unbalanced = Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }
            finally
            {
                await _db.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }

        var healthy = string.Equals(quick, "ok", StringComparison.OrdinalIgnoreCase)
                      && foreignKeys == 0
                      && unbalanced == 0;
        var summary = healthy
            ? "SQLite سليم، العلاقات سليمة، وجميع القيود المرحلة متوازنة."
            : $"QuickCheck={quick}; ForeignKeys={foreignKeys}; UnbalancedJournals={unbalanced}";

        return new LocalDatabaseIntegrityResult(healthy, quick, foreignKeys, unbalanced, summary);
    }

    public static void ApplyPendingRestoreIfAny(string databasePath)
    {
        databasePath = Path.GetFullPath(databasePath);
        var pending = databasePath + ".restore.pending";
        if (!File.Exists(pending)) return;

        var directory = Path.GetDirectoryName(databasePath) ?? Environment.CurrentDirectory;
        Directory.CreateDirectory(directory);
        SqliteConnection.ClearAllPools();

        if (File.Exists(databasePath))
        {
            var backup = Path.Combine(
                directory,
                $"qodynex_pre_restore_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.db");
            File.Copy(databasePath, backup, overwrite: false);
        }

        DeleteIfExists(databasePath + "-wal");
        DeleteIfExists(databasePath + "-shm");
        File.Move(pending, databasePath, overwrite: true);
        SqliteConnection.ClearAllPools();
    }

    public static async Task<LocalDatabaseIntegrityResult> ValidateSqliteFileAsync(
        string path,
        bool requireAccountingSchema,
        CancellationToken ct = default)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            return new(false, "missing", 0, 0, "الملف غير موجود أو فارغ.");

        try
        {
            await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var quickCommand = connection.CreateCommand();
            quickCommand.CommandText = "PRAGMA quick_check;";
            var quick = Convert.ToString(await quickCommand.ExecuteScalarAsync(ct).ConfigureAwait(false)) ?? "unknown";
            if (!string.Equals(quick, "ok", StringComparison.OrdinalIgnoreCase))
                return new(false, quick, 0, 0, $"SQLite quick_check: {quick}");

            if (requireAccountingSchema)
            {
                await using var schemaCommand = connection.CreateCommand();
                schemaCommand.CommandText = """
                    SELECT COUNT(*)
                    FROM sqlite_master
                    WHERE type='table'
                      AND name IN ('Companies','Accounts','JournalEntries','JournalLines','Documents','__EFMigrationsHistory');
                    """;
                var count = Convert.ToInt32(await schemaCommand.ExecuteScalarAsync(ct).ConfigureAwait(false));
                if (count != 6)
                    return new(false, quick, 0, 0, "الملف SQLite سليم لكنه ليس قاعدة Qodynex محاسبية مكتملة.");
            }

            await using var fkCommand = connection.CreateCommand();
            fkCommand.CommandText = "PRAGMA foreign_key_check;";
            var fkCount = 0;
            await using (var reader = await fkCommand.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while (await reader.ReadAsync(ct).ConfigureAwait(false)) fkCount++;

            var healthy = fkCount == 0;
            return new(healthy, quick, fkCount, 0,
                healthy ? "النسخة صالحة." : $"يوجد {fkCount} تعارض علاقات داخل النسخة.");
        }
        catch (SqliteException ex)
        {
            return new(false, "error", 0, 0, ex.Message);
        }
    }

    private static async Task<string> ScalarTextAsync(
        LocalAccountingDbContext db,
        string sql,
        CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) ?? "unknown";
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static async Task<int> CountRowsAsync(
        LocalAccountingDbContext db,
        string sql,
        CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            var count = 0;
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) count++;
            return count;
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static string? ResolveDatabasePath(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource)
            || builder.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
            return null;
        return Path.GetFullPath(builder.DataSource);
    }

    private static void DeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException ioError) { System.Diagnostics.Debug.WriteLine(ioError); }
    }
}
