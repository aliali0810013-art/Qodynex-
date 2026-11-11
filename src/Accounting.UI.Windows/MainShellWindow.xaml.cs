using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

    private async void OnThemeToggleClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.ToggleThemeAsync().ConfigureAwait(true);
            if (sender is Button button)
                button.Content = ThemeManager.IsDark ? "☀️ وضع نهاري" : "🌙 وضع ليلي";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "المظهر", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnQuickPartnerClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not InvoiceEditorViewModel vm) return;
        var name = new TextBox { Margin = new Thickness(8), MinWidth = 300 };
        var phone = new TextBox { Margin = new Thickness(8), MinWidth = 300 };
        var ok = new Button { Content = "إضافة واختيار", Margin = new Thickness(8), IsDefault = true, MinWidth = 120 };
        var cancel = new Button { Content = "إلغاء", Margin = new Thickness(8), IsCancel = true, MinWidth = 90 };
        var window = new Window
        {
            Title = vm.PartnerLabel == "العميل" ? "إضافة عميل سريع" : "إضافة مورد سريع",
            Width = 430, Height = 270, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            ResizeMode = ResizeMode.NoResize
        };
        var root = new StackPanel { Margin = new Thickness(18), FlowDirection = FlowDirection.RightToLeft };
        root.Children.Add(new TextBlock { Text = $"اسم {vm.PartnerLabel}", FontWeight = FontWeights.SemiBold });
        root.Children.Add(name); root.Children.Add(new TextBlock { Text = "الهاتف / الجوال" }); root.Children.Add(phone);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        actions.Children.Add(ok); actions.Children.Add(cancel); root.Children.Add(actions); window.Content = root;
        ok.Click += async (_, _) =>
        {
            try { await vm.CreateQuickPartnerAsync(name.Text, phone.Text).ConfigureAwait(true); window.DialogResult = true; }
            catch (Exception ex) { MessageBox.Show(ex.Message, window.Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        window.ShowDialog();
    }

    private async void OnInvoiceLineItemChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.DataContext is not InvoiceLineViewModel line || combo.SelectedItem is not ItemLookup item)
            return;
        var current = combo as DependencyObject;
        while (current is not null && current is not DataGrid)
            current = VisualTreeHelper.GetParent(current);
        if (current is DataGrid grid && grid.DataContext is InvoiceEditorViewModel vm)
        {
            try { await vm.ChangeLineItemAsync(line, item).ConfigureAwait(true); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "تغيير الصنف", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
    }

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
