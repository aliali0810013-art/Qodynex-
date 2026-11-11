using Accounting.Infrastructure.Local;

namespace Accounting.UI.Windows;

public sealed partial class WindowsAccountingFacade
{
    public async Task<string> CreateLocalBackupAsync(string destinationPath, CancellationToken ct = default) =>
        await new LocalDatabaseMaintenanceService(_db)
            .CreateBackupAsync(destinationPath, ct)
            .ConfigureAwait(false);

    public async Task<string> ScheduleLocalRestoreAsync(string backupPath, CancellationToken ct = default) =>
        await new LocalDatabaseMaintenanceService(_db)
            .ScheduleRestoreAsync(backupPath, ct)
            .ConfigureAwait(false);

    public Task<LocalDatabaseIntegrityResult> CheckLocalDatabaseIntegrityAsync(CancellationToken ct = default) =>
        new LocalDatabaseMaintenanceService(_db).CheckIntegrityAsync(ct);

    public async Task<string> GetServerUrlAsync(CancellationToken ct = default)
    {
        var maintenance = new LocalDatabaseMaintenanceService(_db);
        var settings = await LocalRuntimeSettingsStore
            .LoadAsync(maintenance.DatabasePath, ct)
            .ConfigureAwait(false);
        return settings.ServerUrl;
    }

    public async Task SaveServerUrlAsync(string serverUrl, CancellationToken ct = default)
    {
        var maintenance = new LocalDatabaseMaintenanceService(_db);
        await LocalRuntimeSettingsStore
            .SaveAsync(
                maintenance.DatabasePath,
                new LocalRuntimeConnectionSettings(serverUrl),
                ct)
            .ConfigureAwait(false);
    }
}
