using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Accounting.Sync.Client;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Accounting.UI.Windows;

public sealed class WebErpWindow : Window
{
    private readonly WebErpServer _server;
    private readonly WebView2 _webView;
    private readonly Border _failure;
    private readonly TextBlock _failureText;
    private bool _closed;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly BackgroundSyncCoordinator? _syncCoordinator;
    private readonly CancellationTokenSource _lifetime = new();

    public WebErpWindow(WebErpServer server, BackgroundSyncCoordinator? syncCoordinator = null)
    {
        _server = server;
        _syncCoordinator = syncCoordinator;
        Title = "Qodynex ERP";
        Width = 1500;
        Height = 920;
        MinWidth = 1024;
        MinHeight = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(248, 250, 252));
        FlowDirection = FlowDirection.RightToLeft;

        var grid = new Grid();
        _webView = new WebView2();
        _failureText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromRgb(127, 29, 29)),
            Margin = new Thickness(24)
        };
        _failure = new Border
        {
            Visibility = Visibility.Collapsed,
            Background = new SolidColorBrush(Color.FromRgb(254, 242, 242)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(248, 113, 113)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(30),
            Child = _failureText,
            VerticalAlignment = VerticalAlignment.Top
        };
        grid.Children.Add(_webView);
        grid.Children.Add(_failure);
        Content = grid;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public WebView2 Browser => _webView;
    public string LocalUrl => _server.BaseUrl;
    public Task Ready => _ready.Task;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _server.StartAsync().ConfigureAwait(true);
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Qodynex", "WebView2");
            Directory.CreateDirectory(userData);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData).ConfigureAwait(true);
            await _webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.ProcessFailed += (_, args) =>
            {
                var ex = new InvalidOperationException($"WebView2 process failed: {args.ProcessFailedKind}");
                RuntimeErrorHub.Report(ex, "Web ERP browser process");
                ShowFailure(ex.Message);
                _ready.TrySetException(ex);
            };
            _webView.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess) { _ready.TrySetResult(); return; }
                var ex = new InvalidOperationException($"تعذر تحميل واجهة Qodynex المحلية: {args.WebErrorStatus}");
                RuntimeErrorHub.Report(ex, "Web ERP navigation");
                ShowFailure(ex.Message);
                _ready.TrySetException(ex);
            };
            _webView.Source = new Uri(_server.BaseUrl + "/");
            if (_syncCoordinator is not null)
                _ = Task.Run(() => _syncCoordinator.RunLoopAsync(_lifetime.Token), _lifetime.Token);
        }
        catch (Exception ex)
        {
            RuntimeErrorHub.Report(ex, "تهيئة واجهة WebView2");
            ShowFailure(ex.Message);
            _ready.TrySetException(ex);
        }
    }

    private void ShowFailure(string message)
    {
        _failureText.Text = $"تعذر تشغيل واجهة Qodynex التفاعلية.\n\n{message}\n\nتم تسجيل التفاصيل في logs/error.log.";
        _failure.Visibility = Visibility.Visible;
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (_closed) return;
        _closed = true;
        _lifetime.Cancel();
        try
        {
            await _server.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            RuntimeErrorHub.Report(ex, "إيقاف الخادم المحلي", notify: false);
        }
        finally
        {
            _lifetime.Dispose();
        }
    }
}