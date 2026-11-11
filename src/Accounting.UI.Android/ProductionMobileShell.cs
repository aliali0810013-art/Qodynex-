using System.Collections.ObjectModel;
using Accounting.Sync.Client;

namespace Accounting.UI.Android;

public sealed class ProductionMobileShell : TabbedPage
{
    public ProductionMobileShell(MobileRuntime runtime)
    {
        FlowDirection = FlowDirection.RightToLeft;
        BarBackgroundColor = MobileTheme.Surface;
        BarTextColor = MobileTheme.Text;
        SelectedTabColor = MobileTheme.Gold;
        UnselectedTabColor = MobileTheme.Muted;
        BackgroundColor = MobileTheme.Background;

        var moduleKey = runtime.EnabledModules.FirstOrDefault() ?? "RETAIL";
        Children.Add(Wrap(new SalePage(runtime, moduleKey), "البيع"));
        Children.Add(Wrap(new CollectionPage(runtime), "التحصيل"));
        Children.Add(Wrap(new InventoryPage(runtime), "المخزون"));
        Children.Add(Wrap(new StatementSyncPage(runtime), "الحساب/المزامنة"));
    }

    private static NavigationPage Wrap(Page page, string title)
    {
        page.Title = title;
        var nav = new NavigationPage(page)
        {
            Title = title,
            BarBackgroundColor = MobileTheme.Surface,
            BarTextColor = MobileTheme.Text
        };
        return nav;
    }
}

public sealed class StatementSyncPage : ContentPage
{
    private readonly MobileRuntime _runtime;
    private readonly Picker _customer = new() { Title = "اختر العميل" };
    private readonly Label _balance = new() { FontSize = 22, FontAttributes = FontAttributes.Bold, TextColor = MobileTheme.Gold };
    private readonly Label _sync = new() { TextColor = MobileTheme.Muted };
    private readonly Label _status = new() { TextColor = MobileTheme.Muted };
    private readonly ObservableCollection<MobileStatementRow> _rows = new();

    public StatementSyncPage(MobileRuntime runtime)
    {
        _runtime = runtime;
        FlowDirection = FlowDirection.RightToLeft;
        BackgroundColor = MobileTheme.Background;

        var statementButton = MobileTheme.Primary("عرض كشف الحساب");
        statementButton.Clicked += async (_, _) => await LoadStatementAsync();
        var syncButton = MobileTheme.Primary("مزامنة الآن");
        syncButton.Clicked += async (_, _) => await SyncNowAsync();
        var conflictsButton = new Button { Text = "عرض تعارضات المزامنة" };
        conflictsButton.Clicked += async (_, _) => await Navigation.PushAsync(new ConflictsPage(_runtime));

        var list = new CollectionView
        {
            ItemsSource = _rows,
            SelectionMode = SelectionMode.None,
            ItemTemplate = new DataTemplate(() =>
            {
                var desc = new Label { FontSize = 14 };
                desc.SetBinding(Label.TextProperty, nameof(MobileStatementRow.Description));
                var date = new Label { FontSize = 11, TextColor = MobileTheme.Muted };
                date.SetBinding(Label.TextProperty, new Binding(nameof(MobileStatementRow.DateUtc), stringFormat: "{0:yyyy-MM-dd}"));
                var amount = new Label { FontSize = 16, FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.End };
                amount.SetBinding(Label.TextProperty, new Binding(nameof(MobileStatementRow.RunningBalance), stringFormat: "{0:N2}"));
                return MobileTheme.CardView(new Grid
                {
                    ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(new GridLength(100)) },
                    Children = { new VerticalStackLayout { Spacing = 3, Children = { desc, date } }, amount.Position(1,0) }
                });
            })
        };

        Content = new Grid
        {
            Padding = new Thickness(14,18),
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto)
            },
            Children =
            {
                new VerticalStackLayout { Spacing = 4, Children = { new Label { Text = "كشف الحساب والمزامنة", FontSize = 25, FontAttributes = FontAttributes.Bold }, new Label { Text = "الحسابات المحلية وOutbox من SQLite", TextColor = MobileTheme.Muted } } },
                MobileTheme.CardView(new VerticalStackLayout { Spacing = 10, Children = { _customer, statementButton, _balance } }).Position(0,1),
                MobileTheme.CardView(new VerticalStackLayout { Spacing = 8, Children = { new Label { Text = "حالة المزامنة", FontAttributes = FontAttributes.Bold }, _sync, new HorizontalStackLayout { Children = { syncButton, conflictsButton } } } }).Position(0,2),
                new Label { Text = "الحركات", FontAttributes = FontAttributes.Bold, Margin = new Thickness(4,10,4,4) }.Position(0,3),
                list.Position(0,4),
                _status.Position(0,5)
            }
        };
        Appearing += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (_customer.ItemsSource is null)
            {
                var customers = await _runtime.Facade.GetCustomersAsync().ConfigureAwait(true);
                _customer.ItemsSource = customers;
                _customer.ItemDisplayBinding = new Binding(nameof(MobilePartner.Name));
                if (customers.Count > 0) _customer.SelectedIndex = 0;
            }
            var s = await _runtime.SyncStore.GetStatusAsync(_runtime.UserContext).ConfigureAwait(true);
            _sync.Text = $"Outbox: {s.PendingOutbox + s.RetryableFailures}  •  تعارضات: {s.UnresolvedConflicts}\nآخر مزامنة: {(s.LastSuccessfulSyncUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "لم تتم")}";
            _sync.TextColor = s.UnresolvedConflicts > 0 ? MobileTheme.Red : MobileTheme.Green;
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    private async Task LoadStatementAsync()
    {
        try
        {
            if (_customer.SelectedItem is not MobilePartner customer) throw new InvalidOperationException("حدد العميل.");
            var result = await _runtime.Facade.GetCustomerStatementAsync(customer.Id).ConfigureAwait(true);
            _rows.Clear(); foreach (var row in result.Rows) _rows.Add(row);
            _balance.Text = $"الرصيد: {result.Balance:N2}";
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    private async Task SyncNowAsync()
    {
        try
        {
            var result = await _runtime.SyncNowAsync().ConfigureAwait(true);
            _status.Text = result is null ? "لا يوجد خادم مهيأ؛ البيانات محفوظة محلياً." : $"تم الإرسال {result.PushedMessages} والاستقبال {result.PulledChanges}.";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
}
