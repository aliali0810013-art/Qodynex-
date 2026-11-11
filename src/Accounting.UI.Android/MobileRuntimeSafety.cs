namespace Accounting.UI.Android;

public static class MobileRuntimeSafety
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _lastFingerprint;
    private static DateTimeOffset _lastUtc;

    public static async Task ReportAsync(Exception exception, string context, bool notify = false)
    {
        try
        {
            var directory = Path.Combine(FileSystem.AppDataDirectory, "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "error.log");
            if (File.Exists(path) && new FileInfo(path).Length > 5 * 1024 * 1024)
                File.Move(path, Path.Combine(directory, $"error-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log"), overwrite: true);

            await File.AppendAllTextAsync(path,
                $"[{DateTimeOffset.UtcNow:O}] {context}{Environment.NewLine}{exception}{Environment.NewLine}{new string('-', 72)}{Environment.NewLine}")
                .ConfigureAwait(false);
        }
        catch
        {
            // Error logging must never become an application error.
        }

        if (!notify) return;

        var fingerprint = $"{context}|{exception.GetType().FullName}|{exception.Message}";
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (string.Equals(_lastFingerprint, fingerprint, StringComparison.Ordinal)
                && now - _lastUtc < TimeSpan.FromSeconds(5))
                return;
            _lastFingerprint = fingerprint;
            _lastUtc = now;
        }
        finally { Gate.Release(); }

        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = Microsoft.Maui.Controls.Application.Current?.MainPage;
            if (page is not null)
                await page.DisplayAlert("تنبيه Qodynex", exception.Message, "حسناً");
        });
    }
}
