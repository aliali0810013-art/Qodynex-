using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Accounting.UI.Windows;

public sealed record RuntimeErrorEventArgs(string Context, string Message, DateTimeOffset Timestamp);

public static class RuntimeErrorHub
{
    private static readonly object Gate = new();
    private static string? _lastFingerprint;
    private static DateTimeOffset _lastNotificationUtc;

    public static event EventHandler<RuntimeErrorEventArgs>? ErrorRaised;

    public static void Report(Exception exception, string context, bool notify = true)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var fingerprint = $"{context}|{exception.GetType().FullName}|{exception.Message}";
        WriteLog(exception, context, timestamp);

        if (!notify) return;

        lock (Gate)
        {
            if (string.Equals(_lastFingerprint, fingerprint, StringComparison.Ordinal)
                && timestamp - _lastNotificationUtc < TimeSpan.FromSeconds(3))
                return;
            _lastFingerprint = fingerprint;
            _lastNotificationUtc = timestamp;
        }

        var args = new RuntimeErrorEventArgs(
            context,
            exception.InnerException is null
                ? exception.Message
                : $"{exception.Message} — {exception.InnerException.Message}",
            timestamp);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            ErrorRaised?.Invoke(null, args);
            return;
        }

        if (dispatcher.CheckAccess())
            ErrorRaised?.Invoke(null, args);
        else
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => ErrorRaised?.Invoke(null, args)));
    }

    private static void WriteLog(Exception exception, string context, DateTimeOffset timestamp)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Qodynex", "logs");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "error.log");
            RotateIfNeeded(path);

            var entry =
                $"[{timestamp:O}] {context}{Environment.NewLine}" +
                $"{exception}{Environment.NewLine}" +
                $"{new string('-', 80)}{Environment.NewLine}";
            File.AppendAllText(path, entry);
        }
        catch
        {
            // Error reporting must never become another application error.
        }
    }

    private static void RotateIfNeeded(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < 5 * 1024 * 1024) return;
        var archive = Path.Combine(
            Path.GetDirectoryName(path)!,
            $"error-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        File.Move(path, archive, overwrite: true);
    }
}
