using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Accounting.Sync.Client;

namespace Accounting.UI.Windows;

public partial class MainShellWindow : Window
{
    private readonly ShellViewModel _viewModel;
    private readonly BackgroundSyncCoordinator? _syncCoordinator;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _statusTimer;

    public MainShellWindow(ShellViewModel viewModel, BackgroundSyncCoordinator? syncCoordinator)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _syncCoordinator = syncCoordinator;
        DataContext = viewModel;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += async (_, _) => await _viewModel.RefreshStatusAsync().ConfigureAwait(true);
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshStatusAsync().ConfigureAwait(true);
        _statusTimer.Start();
        if (_syncCoordinator is not null)
            _ = Task.Run(() => _syncCoordinator.RunLoopAsync(_lifetime.Token), _lifetime.Token);
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2) { e.Handled = true; _viewModel.OpenSalesCommand.Execute(null); return; }
        if (e.Key == Key.F3) { e.Handled = true; _viewModel.OpenQuickSearch(); return; }
        if (e.Key is Key.F5 or Key.F7)
        {
            e.Handled = true;
            await _viewModel.Shortcuts.HandleAsync(e.Key).ConfigureAwait(true);
            return;
        }
        if (e.Key == Key.F9)
        {
            e.Handled = true;
            await SyncNowAsync().ConfigureAwait(true);
        }
    }

    private void OnNewClick(object sender, RoutedEventArgs e) => _viewModel.OpenSalesCommand.Execute(null);

    private void OnQuickSearchClick(object sender, RoutedEventArgs e) => _viewModel.OpenQuickSearch();

    private async void OnSavePostClick(object sender, RoutedEventArgs e) =>
        await _viewModel.Shortcuts.HandleAsync(Key.F5).ConfigureAwait(true);

    private async void OnDetailsClick(object sender, RoutedEventArgs e) =>
        await _viewModel.Shortcuts.HandleAsync(Key.F7).ConfigureAwait(true);

    private async void OnSyncNowClick(object sender, RoutedEventArgs e) =>
        await SyncNowAsync().ConfigureAwait(true);

    private async Task SyncNowAsync()
    {
        if (_syncCoordinator is not null)
        {
            try { await _syncCoordinator.RunOnceAsync(_lifetime.Token).ConfigureAwait(true); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { MessageBox.Show(ex.Message, "المزامنة", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        await _viewModel.RefreshStatusAsync().ConfigureAwait(true);
    }

    private void OnCloseTabClick(object sender, RoutedEventArgs e)
    {
        _viewModel.Workspace.CloseSelected(tab =>
            MessageBox.Show($"التبويب '{tab.Title}' يحتوي تغييرات غير محفوظة. إغلاقه؟",
                "Qodynex", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_viewModel.Workspace.Tabs.Any(x => x.HasUnsavedChanges))
        {
            var answer = MessageBox.Show("توجد تبويبات بتغييرات غير محفوظة. هل تريد إغلاق النظام؟",
                "Qodynex", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }
        _statusTimer.Stop();
        _lifetime.Cancel();
    }
}
