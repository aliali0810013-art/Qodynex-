using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Accounting.UI.Windows;

public static class WindowsUiTourVerifier
{
    public static async Task RunAsync(
        MainShellWindow window,
        ShellViewModel shell,
        WindowsAccountingFacade facade,
        CancellationToken ct = default)
    {
        var runtimeErrors = new List<RuntimeErrorEventArgs>();
        void Capture(object? _, RuntimeErrorEventArgs e) => runtimeErrors.Add(e);
        RuntimeErrorHub.ErrorRaised += Capture;

        try
        {
            window.Show();
            window.Activate();
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);

            var navigation = new[]
            {
                shell.OpenDashboardCommand,
                shell.OpenSalesCommand,
                shell.OpenPurchaseCommand,
                shell.OpenVouchersWorkspaceCommand,
                shell.OpenInventoryWorkspaceCommand,
                shell.OpenAccountsWorkspaceCommand,
                shell.OpenReportsSyncCommand,
                shell.OpenSettingsCommand
            };
            foreach (var command in navigation)
            {
                ct.ThrowIfCancellationRequested();
                command.Execute(null);
                await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Background);
            }

            if (shell.Workspace.Tabs.Count < 8)
                throw new InvalidOperationException($"UI tour opened only {shell.Workspace.Tabs.Count} workspaces.");

            var opened = new List<object>();
            void OpenTab(string key, string title, object vm) => opened.Add(vm);
            var invoice = new InvoiceEditorViewModel(facade, OpenTab, true, "RETAIL");
            await WaitUntilAsync(
                () => invoice.AvailableItems.Count > 0 && invoice.Warehouses.Count > 0 && invoice.SettlementAccounts.Count > 0,
                TimeSpan.FromSeconds(5), ct).ConfigureAwait(true);

            var item = invoice.AvailableItems[0];
            await invoice.AddItemAsync(item).ConfigureAwait(true);
            var line = invoice.Lines.Single();
            line.Quantity = 2m;
            line.UnitPrice = 100m;
            line.Discount = 10m;
            if (line.LineTotal != 190m || invoice.GrandTotal != 190m)
                throw new InvalidOperationException("Invoice live recalculation failed during the real-process UI tour.");

            var originalBranding = await facade.GetBrandingAsync(ct).ConfigureAwait(true);
            var logo = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            var smokeBranding = originalBranding with
            {
                ArabicName = string.IsNullOrWhiteSpace(originalBranding.ArabicName) ? "كودينكس" : originalBranding.ArabicName,
                LogoBytes = logo,
                City = string.IsNullOrWhiteSpace(originalBranding.City) ? "صنعاء" : originalBranding.City
            };
            await facade.SaveBrandingAsync(smokeBranding, "ui-tour-logo.png", ct).ConfigureAwait(true);
            var saved = await facade.GetBrandingAsync(ct).ConfigureAwait(true);
            if (saved.LogoBytes is null || saved.LogoBytes.Length == 0)
                throw new InvalidOperationException("Company logo was not persisted during the UI tour.");

            var thermal = InvoicePrintService.Build(invoice, saved, true);
            var a4 = InvoicePrintService.Build(invoice, saved, false);
            if (thermal.PageWidth >= a4.PageWidth || thermal.Blocks.Count == 0 || a4.Blocks.Count == 0)
                throw new InvalidOperationException("80mm/A4 print document generation failed during the UI tour.");

            ThemeManager.Apply(false);
            if (!ThemeManager.PaletteHasReadableContrast(false))
                throw new InvalidOperationException("Light theme contrast verification failed.");
            ThemeManager.Apply(true);
            if (!ThemeManager.PaletteHasReadableContrast(true))
                throw new InvalidOperationException("Dark theme contrast verification failed.");
            ThemeManager.Apply(saved.DarkThemeEnabled);

            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
            var clipped = FindClippedText(window).Take(5).ToArray();
            if (clipped.Length > 0)
                throw new InvalidOperationException("Visible clipped text detected: " + string.Join(" | ", clipped));

            if (runtimeErrors.Count > 0)
                throw new InvalidOperationException(
                    $"RuntimeErrorHub captured {runtimeErrors.Count} error(s) during UI tour: {runtimeErrors[0].Context} - {runtimeErrors[0].Message}");
        }
        finally
        {
            RuntimeErrorHub.ErrorRaised -= Capture;
            if (window.IsVisible) window.Hide();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        while (!predicate())
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow - started > timeout)
                throw new TimeoutException("Timed out waiting for the production invoice editor to finish loading.");
            await Task.Delay(50, ct).ConfigureAwait(true);
        }
    }

    private static IEnumerable<string> FindClippedText(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var count = VisualTreeHelper.GetChildrenCount(current);
            for (var index = 0; index < count; index++)
                queue.Enqueue(VisualTreeHelper.GetChild(current, index));

            if (current is not TextBlock text
                || !text.IsVisible
                || string.IsNullOrWhiteSpace(text.Text)
                || text.TextWrapping != TextWrapping.NoWrap
                || text.TextTrimming != TextTrimming.None
                || text.ActualWidth <= 2
                || text.Parent is DataGridCell)
                continue;

            var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
            var formatted = new FormattedText(
                text.Text,
                CultureInfo.CurrentUICulture,
                text.FlowDirection,
                typeface,
                text.FontSize,
                text.Foreground,
                VisualTreeHelper.GetDpi(text).PixelsPerDip);

            if (formatted.WidthIncludingTrailingWhitespace > text.ActualWidth + 4)
                yield return text.Text;
        }
    }
}
