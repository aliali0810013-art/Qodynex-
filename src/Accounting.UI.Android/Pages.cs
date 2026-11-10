using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Accounting.Domain;
using Accounting.Modules;
using Accounting.SharedKernel;
using Microsoft.EntityFrameworkCore;
using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace Accounting.UI.Android;

internal static class MobileUi
{
    public static Button PrimaryButton(string text) => new()
    {
        Text = text,
        HeightRequest = 52,
        CornerRadius = 12,
        FontAttributes = FontAttributes.Bold
    };

    public static Entry Entry(string placeholder, Keyboard? keyboard = null, bool password = false) => new()
    {
        Placeholder = placeholder,
        Keyboard = keyboard ?? Keyboard.Default,
        IsPassword = password,
        HeightRequest = 48,
        ClearButtonVisibility = ClearButtonVisibility.WhileEditing
    };

    public static Label Heading(string text) => new()
    {
        Text = text,
        FontSize = 25,
        FontAttributes = FontAttributes.Bold,
        HorizontalTextAlignment = TextAlignment.Start
    };

    public static string ModuleTitle(string moduleKey) => moduleKey.ToUpperInvariant() switch
    {
        RetailModule.ModuleKey => "بيع تجزئة",
        WholesaleModule.ModuleKey => "بيع جملة",
        ServicesModule.ModuleKey => "فاتورة خدمات",
        RestaurantModule.ModuleKey => "طلب مطعم",
        _ => "فاتورة مبيعات"
    };
}

public sealed class SetupPage : ContentPage
{
    private readonly MobileSessionSettings _settings;
    private readonly MobileRuntime _runtime;
    private readonly Entry _server;
    private readonly Entry _token;
    private readonly Entry _company;
    private readonly Entry _branch;
    private readonly Entry _user;
    private readonly Entry _device;
    private readonly Entry _deviceName;
    private readonly Label _status;
    private readonly Button _save;

    public SetupPage(MobileSessionSettings settings, MobileRuntime runtime, string? initialError = null)
    {
        _settings = settings;
        _runtime = runtime;
        Title = "تهيئة الجهاز";
        FlowDirection = FlowDirection.RightToLeft;

        _server = MobileUi.Entry("https://server.example.com") ; _server.Text = settings.ServerUrl;
        _token = MobileUi.Entry("JWT Access Token", password: true);
        _company = MobileUi.Entry("CompanyId"); _company.Text = settings.CompanyId == Guid.Empty ? "" : settings.CompanyId.ToString("D");
        _branch = MobileUi.Entry("BranchId"); _branch.Text = settings.BranchId == Guid.Empty ? "" : settings.BranchId.ToString("D");
        _user = MobileUi.Entry("UserId"); _user.Text = settings.UserId == Guid.Empty ? "" : settings.UserId.ToString("D");
        _device = MobileUi.Entry("DeviceId الموجود داخل JWT"); _device.Text = settings.DeviceId == Guid.Empty ? "" : settings.DeviceId.ToString("D");
        _deviceName = MobileUi.Entry("اسم الجهاز"); _deviceName.Text = settings.DeviceName;
        _status = new Label { Text = initialError ?? "التهيئة الأولى تتطلب اتصالاً بالخادم. بعد نجاحها يعمل التطبيق دون إنترنت.", TextColor = Colors.DarkSlateGray };
        _save = MobileUi.PrimaryButton("تسجيل الجهاز وتنزيل بيانات الشركة");
        _save.Clicked += SaveAsync;

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(18, 28),
                Spacing = 10,
                Children =
                {
                    MobileUi.Heading("تهيئة Qodynex"),
                    new Label { Text = "أدخل بيانات الربط الممنوحة من الخادم. لا يتم إنشاء صلاحيات محلياً من هذه الشاشة." },
                    _server, _token, _company, _branch, _user, _device, _deviceName,
                    _save, _status
                }
            }
        };
    }

    private async void SaveAsync(object? sender, EventArgs e)
    {
        _save.IsEnabled = false;
        try
        {
            if (!Uri.TryCreate(_server.Text?.Trim(), UriKind.Absolute, out var serverUri) || serverUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("رابط الخادم يجب أن يكون HTTPS صحيحاً.");
            if (!Guid.TryParse(_company.Text, out var company) || company == Guid.Empty
                || !Guid.TryParse(_branch.Text, out var branch) || branch == Guid.Empty
                || !Guid.TryParse(_user.Text, out var user) || user == Guid.Empty
                || !Guid.TryParse(_device.Text, out var device) || device == Guid.Empty)
                throw new InvalidOperationException("CompanyId/BranchId/UserId/DeviceId يجب أن تكون GUID صحيحة.");
            if (string.IsNullOrWhiteSpace(_token.Text)) throw new InvalidOperationException("JWT Access Token مطلوب للتهيئة الأولى.");

            await _runtime.ResetAsync().ConfigureAwait(true);
            _settings.ServerUrl = serverUri.ToString().TrimEnd('/');
            _settings.CompanyId = company;
            _settings.BranchId = branch;
            _settings.UserId = user;
            _settings.DeviceId = device;
            _settings.DeviceName = string.IsNullOrWhiteSpace(_deviceName.Text) ? "Android" : _deviceName.Text!;
            _settings.MarkServerIdentity();
            await _settings.SetAccessTokenAsync(_token.Text!).ConfigureAwait(true);

            _status.Text = "جارٍ تسجيل الجهاز وتنزيل سياسة الشركة والبيانات الأساسية...";
            await _runtime.InitializeAsync().ConfigureAwait(true);
            Microsoft.Maui.Controls.Application.Current!.MainPage = new NavigationPage(new HomePage(_runtime));
            _runtime.StartSyncLoop();
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
        finally
        {
            _save.IsEnabled = true;
        }
    }
}

public sealed class HomePage : ContentPage
{
    private readonly MobileRuntime _runtime;
    private readonly Label _syncStatus = new();
    private readonly VerticalStackLayout _moduleButtons = new();

    public HomePage(MobileRuntime runtime)
    {
        _runtime = runtime;
        Title = "الرئيسية";
        FlowDirection = FlowDirection.RightToLeft;

        var collect = MobileUi.PrimaryButton("تحصيل نقدي");
        collect.Clicked += async (_, _) => await Navigation.PushAsync(new CollectionPage(_runtime));
        var statement = MobileUi.PrimaryButton("كشف حساب عميل");
        statement.Clicked += async (_, _) => await Navigation.PushAsync(new CustomerStatementPage(_runtime));
        var inventory = MobileUi.PrimaryButton("جرد / استعلام مخزون");
        inventory.Clicked += async (_, _) => await Navigation.PushAsync(new InventoryPage(_runtime));
        var conflicts = MobileUi.PrimaryButton("تعارضات المزامنة");
        conflicts.Clicked += async (_, _) => await Navigation.PushAsync(new ConflictsPage(_runtime));
        var sync = MobileUi.PrimaryButton("مزامنة الآن");
        sync.Clicked += async (_, _) => await SyncNowAsync();

        foreach (var moduleKey in runtime.EnabledModules)
        {
            var button = MobileUi.PrimaryButton(MobileUi.ModuleTitle(moduleKey));
            button.CommandParameter = moduleKey;
            button.Clicked += async (sender, _) =>
            {
                var key = (string)((Button)sender!).CommandParameter;
                await Navigation.PushAsync(new SalePage(_runtime, key));
            };
            _moduleButtons.Children.Add(button);
        }

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 20),
                Spacing = 12,
                Children =
                {
                    MobileUi.Heading(string.IsNullOrWhiteSpace(runtime.CompanyName) ? "Qodynex" : runtime.CompanyName),
                    new Label { Text = $"الفرع: {runtime.BranchName}", FontSize = 15 },
                    new Border
                    {
                        Padding = 12,
                        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
                        Content = _syncStatus
                    },
                    new Label { Text = "العمليات الميدانية", FontAttributes = FontAttributes.Bold, Margin = new Thickness(0,8,0,0) },
                    _moduleButtons,
                    collect, statement, conflicts, sync
                }
            }
        };
        if (runtime.ModuleRegistry.IsFeatureEnabled(runtime.EnabledModules, "inventory.stock"))
            ((VerticalStackLayout)((ScrollView)Content).Content).Children.Insert(8, inventory);

        Appearing += async (_, _) => await RefreshStatusAsync();
    }

    private async Task RefreshStatusAsync()
    {
        var status = await _runtime.SyncStore.GetStatusAsync(_runtime.UserContext).ConfigureAwait(true);
        _syncStatus.Text =
            $"Outbox: {status.PendingOutbox + status.RetryableFailures}   |   تعارضات: {status.UnresolvedConflicts}\n" +
            $"آخر مزامنة: {(status.LastSuccessfulSyncUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "لم تتم")}";
        _syncStatus.TextColor = status.UnresolvedConflicts > 0 ? Colors.DarkRed : Colors.DarkGreen;
    }

    private async Task SyncNowAsync()
    {
        try
        {
            var result = await _runtime.SyncNowAsync().ConfigureAwait(true);
            await RefreshStatusAsync().ConfigureAwait(true);
            await DisplayAlert("المزامنة", result is null ? "لا يوجد اتصال خادم مهيأ." :
                $"تم الإرسال: {result.PushedMessages}\nتم الاستقبال: {result.PulledChanges}\nالتعارضات الجديدة: {result.Conflicts}", "حسناً");
        }
        catch (Exception ex)
        {
            await DisplayAlert("المزامنة", ex.Message, "حسناً");
        }
    }
}

public sealed class MobileSaleLineViewModel : BindableObject
{
    private Guid _unitOfMeasureId;
    private MobileUnitOption? _selectedUnit;
    private decimal _quantity = 1m;
    private decimal _baseUnitsPerUnit = 1m;
    private decimal _unitPrice;

    public Guid ItemId { get; init; }
    public string Sku { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public ObservableCollection<MobileUnitOption> UnitOptions { get; } = new();

    public Guid UnitOfMeasureId
    {
        get => _unitOfMeasureId;
        private set
        {
            if (_unitOfMeasureId == value) return;
            _unitOfMeasureId = value;
            OnPropertyChanged();
        }
    }

    public MobileUnitOption? SelectedUnit
    {
        get => _selectedUnit;
        set
        {
            if (Equals(_selectedUnit, value)) return;
            _selectedUnit = value;
            OnPropertyChanged();
            if (value is null) return;
            UnitOfMeasureId = value.UnitOfMeasureId;
            BaseUnitsPerUnit = value.BaseUnitsPerUnit;
        }
    }

    public decimal Quantity
    {
        get => _quantity;
        set
        {
            if (value <= 0m || _quantity == value) return;
            _quantity = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Total));
        }
    }

    public decimal BaseUnitsPerUnit
    {
        get => _baseUnitsPerUnit;
        private set
        {
            if (value <= 0m || _baseUnitsPerUnit == value) return;
            _baseUnitsPerUnit = value;
            OnPropertyChanged();
        }
    }

    public decimal UnitPrice
    {
        get => _unitPrice;
        set
        {
            if (value < 0m || _unitPrice == value) return;
            _unitPrice = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Total));
        }
    }

    public decimal Total => Quantity * UnitPrice;
}

public sealed class SalePage : ContentPage
{
    private readonly MobileRuntime _runtime;
    private readonly string _moduleKey;
    private readonly ObservableCollection<MobileSaleLineViewModel> _lines = new();
    private readonly Entry _barcode = MobileUi.Entry("امسح الباركود أو اكتب الكود");
    private readonly Picker _warehouse = new() { Title = "المستودع" };
    private readonly Picker _settlement = new() { Title = "حساب الدفع/العميل" };
    private readonly Picker _partner = new() { Title = "العميل (اختياري)" };
    private readonly Picker _serviceType = new() { Title = "نوع خدمة المطعم" };
    private readonly Label _tax = new() { FontSize = 15 };
    private readonly Label _total = new() { FontSize = 22, FontAttributes = FontAttributes.Bold };
    private CompanyPolicy? _policy;
    private readonly Label _status = new() { TextColor = Colors.DarkSlateGray };
    private CancellationTokenSource? _hidDebounce;

    public SalePage(MobileRuntime runtime, string moduleKey)
    {
        _runtime = runtime;
        _moduleKey = moduleKey;
        Title = MobileUi.ModuleTitle(moduleKey);
        FlowDirection = FlowDirection.RightToLeft;

        var scan = MobileUi.PrimaryButton("فتح الكاميرا");
        scan.Clicked += async (_, _) => await Navigation.PushAsync(new BarcodeScannerPage(AddCodeAsync));
        _barcode.Completed += async (_, _) => await AddCodeAsync(_barcode.Text ?? string.Empty);
        _barcode.TextChanged += OnBarcodeTextChanged;

        _serviceType.ItemsSource = new[] { "محلي", "سفري", "توصيل" };
        _serviceType.SelectedIndex = 0;
        _serviceType.IsVisible = string.Equals(moduleKey, RestaurantModule.ModuleKey, StringComparison.OrdinalIgnoreCase);
        _warehouse.IsVisible = !string.Equals(moduleKey, ServicesModule.ModuleKey, StringComparison.OrdinalIgnoreCase);

        var list = new CollectionView
        {
            ItemsSource = _lines,
            SelectionMode = SelectionMode.None,
            ItemTemplate = new DataTemplate(() =>
            {
                var grid = new Grid
                {
                    Padding = new Thickness(4, 7),
                    ColumnSpacing = 6,
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(GridLength.Star),
                        new ColumnDefinition(new GridLength(105)),
                        new ColumnDefinition(new GridLength(68)),
                        new ColumnDefinition(new GridLength(92)),
                        new ColumnDefinition(new GridLength(40))
                    }
                };
                var name = new Label { VerticalTextAlignment = TextAlignment.Center };
                name.SetBinding(Label.TextProperty, nameof(MobileSaleLineViewModel.Name));
                var unit = new Picker { HorizontalOptions = LayoutOptions.Fill };
                unit.SetBinding(Picker.ItemsSourceProperty, nameof(MobileSaleLineViewModel.UnitOptions));
                unit.SetBinding(Picker.SelectedItemProperty, nameof(MobileSaleLineViewModel.SelectedUnit), BindingMode.TwoWay);
                unit.ItemDisplayBinding = new Binding(nameof(MobileUnitOption.Name));
                var qty = new Entry { Keyboard = Keyboard.Numeric, HorizontalTextAlignment = TextAlignment.Center };
                qty.SetBinding(Entry.TextProperty, nameof(MobileSaleLineViewModel.Quantity), BindingMode.TwoWay);
                var price = new Entry { Keyboard = Keyboard.Numeric, HorizontalTextAlignment = TextAlignment.Center, Placeholder = "السعر" };
                price.SetBinding(Entry.TextProperty, nameof(MobileSaleLineViewModel.UnitPrice), BindingMode.TwoWay);
                var remove = new Button { Text = "×", Padding = 0 };
                remove.SetBinding(Button.CommandParameterProperty, ".");
                remove.Clicked += (_, _) =>
                {
                    if (remove.CommandParameter is MobileSaleLineViewModel row) _lines.Remove(row);
                    Recalculate();
                };
                grid.Add(name, 0); grid.Add(unit, 1); grid.Add(qty, 2); grid.Add(price, 3); grid.Add(remove, 4);
                return grid;
            })
        };

        var post = MobileUi.PrimaryButton("حفظ وترحيل محلياً");
        post.Clicked += PostAsync;

        Content = new Grid
        {
            Padding = 12,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto)
            },
            Children =
            {
                new VerticalStackLayout
                {
                    Spacing = 8,
                    Children = { _barcode, scan }
                },
                new Grid
                {
                    RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto) },
                    ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) },
                    Children =
                    {
                        _warehouse.Position(0,0),
                        _settlement.Position(1,0),
                        _partner.Position(0,1),
                        _serviceType.Position(1,1)
                    }
                }.Position(0,1),
                new Label { Text = "الصنف | الوحدة | الكمية | السعر", FontAttributes = FontAttributes.Bold }.Position(0,2),
                list.Position(0,3),
                new VerticalStackLayout { Spacing = 6, Children = { _tax, _total, post, _status } }.Position(0,4)
            }
        };

        _lines.CollectionChanged += (_, _) => Recalculate();
        Appearing += async (_, _) => await LoadPickersAsync();
    }

    private async Task LoadPickersAsync()
    {
        if (_settlement.ItemsSource is not null) return;
        _policy = await _runtime.Facade.GetPolicyAsync().ConfigureAwait(true);
        var accounts = await _runtime.Facade.GetPostableAccountsAsync().ConfigureAwait(true);
        var partners = await _runtime.Facade.GetCustomersAsync().ConfigureAwait(true);
        var warehouses = await _runtime.Facade.GetWarehousesAsync().ConfigureAwait(true);
        _settlement.ItemsSource = accounts;
        _settlement.ItemDisplayBinding = new Binding(nameof(MobileAccount.Name));
        _partner.ItemsSource = partners;
        _partner.ItemDisplayBinding = new Binding(nameof(MobilePartner.Name));
        _warehouse.ItemsSource = warehouses;
        _warehouse.ItemDisplayBinding = new Binding(nameof(MobileWarehouse.Name));
        if (accounts.Count > 0) _settlement.SelectedIndex = 0;
        if (warehouses.Count > 0) _warehouse.SelectedIndex = 0;
        Recalculate();
    }

    private async void OnBarcodeTextChanged(object? sender, TextChangedEventArgs e)
    {
        _hidDebounce?.Cancel();
        if (string.IsNullOrWhiteSpace(e.NewTextValue)) return;
        _hidDebounce = new CancellationTokenSource();
        try
        {
            await Task.Delay(100, _hidDebounce.Token).ConfigureAwait(true);
            if ((e.NewTextValue?.Length ?? 0) >= 5) await AddCodeAsync(e.NewTextValue!).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
    }

    private async Task AddCodeAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        var item = await _runtime.Facade.FindItemAsync(code).ConfigureAwait(true);
        if (item is null)
        {
            _status.Text = $"لم يتم العثور على: {code}";
            return;
        }

        var existing = _lines.FirstOrDefault(x => x.ItemId == item.Id);
        if (existing is not null)
            existing.Quantity += 1m;
        else
        {
            var unitOptions = await _runtime.Facade.GetItemUnitsAsync(item.Id).ConfigureAwait(true);
            if (unitOptions.Count == 0) throw new InvalidOperationException("لا توجد وحدة قياس معرفة للصنف.");
            var defaultUnit = unitOptions[0];
            var suggestedPrice = await _runtime.Facade.GetSuggestedUnitPriceAsync(
                _moduleKey, item.Id, defaultUnit.BaseUnitsPerUnit, 0m).ConfigureAwait(true);
            var row = new MobileSaleLineViewModel
            {
                ItemId = item.Id,
                Sku = item.Sku,
                Name = item.Name,
                Quantity = 1m,
                UnitPrice = suggestedPrice
            };
            foreach (var option in unitOptions) row.UnitOptions.Add(option);
            row.SelectedUnit = defaultUnit;
            row.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(MobileSaleLineViewModel.Quantity) or nameof(MobileSaleLineViewModel.UnitPrice) or nameof(MobileSaleLineViewModel.Total))
                    Recalculate();
            };
            _lines.Add(row);
        }

        _barcode.Text = string.Empty;
        _status.Text = $"تمت إضافة {item.Name}";
        Recalculate();
    }

    private void Recalculate()
    {
        var subtotal = _lines.Sum(x => x.Total);
        if (_policy is null)
        {
            _tax.Text = "الضريبة: 0.00";
            _total.Text = $"الإجمالي: {subtotal:N2}";
            return;
        }

        var policy = _policy;
        subtotal = decimal.Round(subtotal, policy.CurrencyScale, policy.RoundingMode);
        decimal tax;
        decimal total;
        if (!policy.TaxPolicy.Enabled || policy.TaxPolicy.Rate == 0m)
        {
            tax = 0m;
            total = subtotal;
        }
        else if (policy.TaxPolicy.PricesIncludeTax)
        {
            var net = decimal.Round(subtotal / (1m + policy.TaxPolicy.Rate), policy.CurrencyScale, policy.RoundingMode);
            tax = decimal.Round(subtotal - net, policy.CurrencyScale, policy.RoundingMode);
            total = subtotal;
        }
        else
        {
            tax = decimal.Round(subtotal * policy.TaxPolicy.Rate, policy.CurrencyScale, policy.RoundingMode);
            total = decimal.Round(subtotal + tax, policy.CurrencyScale, policy.RoundingMode);
        }

        _tax.Text = $"الضريبة: {tax:N2}";
        _total.Text = $"الإجمالي: {total:N2}";
    }

    private async void PostAsync(object? sender, EventArgs e)
    {
        try
        {
            if (_settlement.SelectedItem is not MobileAccount account) throw new InvalidOperationException("حدد حساب الدفع/العميل.");
            var warehouseId = _warehouse.SelectedItem is MobileWarehouse warehouse ? warehouse.Id : Guid.Empty;
            if (_warehouse.IsVisible && warehouseId == Guid.Empty) throw new InvalidOperationException("حدد المستودع.");
            var partnerId = (_partner.SelectedItem as MobilePartner)?.Id;
            if (_lines.Count == 0 || _lines.Any(x => x.Quantity <= 0m || x.UnitPrice <= 0m))
                throw new InvalidOperationException("أضف أصنافاً وتأكد أن الكمية والسعر أكبر من صفر.");

            var metadata = "{}";
            if (string.Equals(_moduleKey, RestaurantModule.ModuleKey, StringComparison.OrdinalIgnoreCase))
            {
                var serviceType = _serviceType.SelectedIndex switch { 1 => 2, 2 => 3, _ => 1 };
                metadata = JsonSerializer.Serialize(new { serviceType, tableOrDeliveryReference = (string?)null });
            }

            var result = await _runtime.Facade.PostSaleAsync(_moduleKey, metadata, warehouseId, account.Id, partnerId,
                _lines.Select(x => new MobileSaleLine(x.ItemId, x.UnitOfMeasureId, x.Quantity, x.BaseUnitsPerUnit, x.UnitPrice)).ToArray()).ConfigureAwait(true);
            _lines.Clear();
            Recalculate();
            _status.Text = $"تم الترحيل أوفلاين بنجاح. {result.OperationId:N}\nسيمنح الخادم الرقم الرسمي عند المزامنة.";
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }
}

public sealed class CollectionPage : ContentPage
{
    private readonly MobileRuntime _runtime;
    private readonly Picker _customer = new() { Title = "العميل" };
    private readonly Picker _cash = new() { Title = "الصندوق/البنك" };
    private readonly Entry _amount = MobileUi.Entry("المبلغ", Keyboard.Numeric);
    private readonly Entry _description = MobileUi.Entry("البيان");
    private readonly Label _status = new();

    public CollectionPage(MobileRuntime runtime)
    {
        _runtime = runtime;
        Title = "تحصيل نقدي";
        FlowDirection = FlowDirection.RightToLeft;
        var post = MobileUi.PrimaryButton("تسجيل سند القبض أوفلاين");
        post.Clicked += PostAsync;
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 18, Spacing = 12,
                Children = { MobileUi.Heading("تحصيل عميل"), _customer, _cash, _amount, _description, post, _status }
            }
        };
        Appearing += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_customer.ItemsSource is not null) return;
        var customers = await _runtime.Facade.GetCustomersAsync().ConfigureAwait(true);
        var accounts = await _runtime.Facade.GetPostableAccountsAsync().ConfigureAwait(true);
        _customer.ItemsSource = customers; _customer.ItemDisplayBinding = new Binding(nameof(MobilePartner.Name));
        _cash.ItemsSource = accounts; _cash.ItemDisplayBinding = new Binding(nameof(MobileAccount.Name));
    }

    private async void PostAsync(object? sender, EventArgs e)
    {
        try
        {
            if (_customer.SelectedItem is not MobilePartner customer) throw new InvalidOperationException("حدد العميل.");
            if (_cash.SelectedItem is not MobileAccount cash) throw new InvalidOperationException("حدد حساب الصندوق/البنك.");
            if (!decimal.TryParse(_amount.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
                && !decimal.TryParse(_amount.Text, out amount))
                throw new InvalidOperationException("المبلغ غير صحيح.");
            var result = await _runtime.Facade.CollectAsync(customer.Id, cash.Id, amount, _description.Text ?? string.Empty).ConfigureAwait(true);
            _amount.Text = string.Empty; _description.Text = string.Empty;
            _status.Text = $"تم إنشاء سند القبض والقيد والـOutbox محلياً: {result.OperationId:N}";
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
}

public sealed class CustomerStatementPage : ContentPage
{
    private readonly MobileRuntime _runtime;
    private readonly Picker _customer = new() { Title = "العميل" };
    private readonly Label _balance = new() { FontSize = 20, FontAttributes = FontAttributes.Bold };
    private readonly ObservableCollection<MobileStatementRow> _rows = new();

    public CustomerStatementPage(MobileRuntime runtime)
    {
        _runtime = runtime;
        Title = "كشف حساب";
        FlowDirection = FlowDirection.RightToLeft;
        var load = MobileUi.PrimaryButton("عرض كشف الحساب");
        load.Clicked += LoadStatementAsync;
        var list = new CollectionView
        {
            ItemsSource = _rows,
            ItemTemplate = new DataTemplate(() =>
            {
                var grid = new Grid
                {
                    Padding = 7,
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(new GridLength(95)),
                        new ColumnDefinition(GridLength.Star),
                        new ColumnDefinition(new GridLength(90))
                    }
                };
                var date = new Label(); date.SetBinding(Label.TextProperty, new Binding(nameof(MobileStatementRow.DateUtc), stringFormat: "{0:yyyy-MM-dd}"));
                var desc = new Label(); desc.SetBinding(Label.TextProperty, nameof(MobileStatementRow.Description));
                var balance = new Label { HorizontalTextAlignment = TextAlignment.End }; balance.SetBinding(Label.TextProperty, new Binding(nameof(MobileStatementRow.RunningBalance), stringFormat: "{0:N2}"));
                grid.Add(date, 0); grid.Add(desc, 1); grid.Add(balance, 2); return grid;
            })
        };
        Content = new Grid
        {
            Padding = 14,
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) },
            Children = { _customer, load.Position(0,1), _balance.Position(0,2), list.Position(0,3) }
        };
        Appearing += async (_, _) =>
        {
            if (_customer.ItemsSource is null)
            {
                var rows = await _runtime.Facade.GetCustomersAsync().ConfigureAwait(true);
                _customer.ItemsSource = rows; _customer.ItemDisplayBinding = new Binding(nameof(MobilePartner.Name));
            }
        };
    }

    private async void LoadStatementAsync(object? sender, EventArgs e)
    {
        if (_customer.SelectedItem is not MobilePartner customer)
        {
            await DisplayAlert("كشف الحساب", "حدد العميل أولاً.", "حسناً"); return;
        }
        var result = await _runtime.Facade.GetCustomerStatementAsync(customer.Id).ConfigureAwait(true);
        _rows.Clear(); foreach (var row in result.Rows) _rows.Add(row);
        _balance.Text = $"الرصيد المشتق: {result.Balance:N2}";
    }
}

public sealed class InventoryPage : ContentPage
{
    private readonly MobileRuntime _runtime;
    private readonly Picker _warehouse = new() { Title = "المستودع" };
    private readonly Entry _code = MobileUi.Entry("باركود / SKU");
    private readonly Label _summary = new() { FontSize = 19, FontAttributes = FontAttributes.Bold };
    private readonly ObservableCollection<ItemMovement> _rows = new();

    public InventoryPage(MobileRuntime runtime)
    {
        _runtime = runtime;
        Title = "المخزون والجرد";
        FlowDirection = FlowDirection.RightToLeft;
        var scan = MobileUi.PrimaryButton("مسح بالكاميرا");
        scan.Clicked += async (_, _) => await Navigation.PushAsync(new BarcodeScannerPage(async code => { _code.Text = code; await QueryAsync(); }));
        var query = MobileUi.PrimaryButton("استعلام");
        query.Clicked += async (_, _) => await QueryAsync();
        _code.Completed += async (_, _) => await QueryAsync();

        var list = new CollectionView
        {
            ItemsSource = _rows,
            ItemTemplate = new DataTemplate(() =>
            {
                var grid = new Grid { Padding = 6, ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(new GridLength(95)), new ColumnDefinition(new GridLength(100)) } };
                var source = new Label(); source.SetBinding(Label.TextProperty, nameof(ItemMovement.Source));
                var qty = new Label(); qty.SetBinding(Label.TextProperty, nameof(ItemMovement.QuantityDelta));
                var cost = new Label(); cost.SetBinding(Label.TextProperty, nameof(ItemMovement.UnitCost));
                grid.Add(source,0); grid.Add(qty,1); grid.Add(cost,2); return grid;
            })
        };
        Content = new Grid
        {
            Padding = 14,
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) },
            Children = { _warehouse, _code.Position(0,1), new HorizontalStackLayout { Children = { scan, query } }.Position(0,2), _summary.Position(0,3), list.Position(0,4) }
        };
        Appearing += async (_, _) =>
        {
            if (_warehouse.ItemsSource is null)
            {
                var rows = await _runtime.Facade.GetWarehousesAsync().ConfigureAwait(true);
                _warehouse.ItemsSource = rows; _warehouse.ItemDisplayBinding = new Binding(nameof(MobileWarehouse.Name));
                if (rows.Count > 0) _warehouse.SelectedIndex = 0;
            }
        };
    }

    private async Task QueryAsync()
    {
        if (_warehouse.SelectedItem is not MobileWarehouse warehouse)
        {
            _summary.Text = "حدد المستودع."; return;
        }
        var result = await _runtime.Facade.GetInventoryAsync(_code.Text ?? string.Empty, warehouse.Id).ConfigureAwait(true);
        _rows.Clear();
        if (result is null) { _summary.Text = "الصنف غير موجود."; return; }
        if (result.Position is null)
        {
            _summary.Text = $"{result.Item.Name} — بند خدمي غير مخزني";
            return;
        }
        _summary.Text = $"{result.Item.Name}\nالكمية {result.Position.QuantityOnHand:N3} | المتوسط {result.Position.AverageUnitCost:N2}";
        foreach (var row in result.Movements) _rows.Add(row);
    }
}

public sealed class ConflictsPage : ContentPage
{
    private readonly MobileRuntime _runtime;
    private readonly ObservableCollection<LocalConflictRow> _rows = new();

    private sealed record LocalConflictRow(string Code, string Message, DateTimeOffset DateUtc);

    public ConflictsPage(MobileRuntime runtime)
    {
        _runtime = runtime;
        Title = "تعارضات المزامنة";
        FlowDirection = FlowDirection.RightToLeft;
        Content = new CollectionView
        {
            ItemsSource = _rows,
            ItemTemplate = new DataTemplate(() =>
            {
                var stack = new VerticalStackLayout { Padding = 10, Spacing = 3 };
                var code = new Label { FontAttributes = FontAttributes.Bold, TextColor = Colors.DarkRed };
                code.SetBinding(Label.TextProperty, nameof(LocalConflictRow.Code));
                var message = new Label(); message.SetBinding(Label.TextProperty, nameof(LocalConflictRow.Message));
                var date = new Label { FontSize = 12 }; date.SetBinding(Label.TextProperty, new Binding(nameof(LocalConflictRow.DateUtc), stringFormat: "{0:yyyy-MM-dd HH:mm}"));
                stack.Children.Add(code); stack.Children.Add(message); stack.Children.Add(date); return stack;
            })
        };
        Appearing += async (_, _) =>
        {
            var rows = await _runtime.Db!.LocalSyncConflicts.AsNoTracking()
                .Where(x => x.CompanyId == _runtime.UserContext.CompanyId && x.BranchId == _runtime.UserContext.BranchId && !x.IsResolved)
                .OrderByDescending(x => x.ServerTimestampUtc)
                .Select(x => new LocalConflictRow(x.ConflictCode, x.Message, x.ServerTimestampUtc))
                .ToListAsync().ConfigureAwait(true);
            _rows.Clear(); foreach (var row in rows) _rows.Add(row);
        };
    }
}

public sealed class BarcodeScannerPage : ContentPage
{
    private readonly Func<string, Task> _onScanned;
    private readonly CameraBarcodeReaderView _scanner;
    private int _handled;

    public BarcodeScannerPage(Func<string, Task> onScanned)
    {
        _onScanned = onScanned;
        Title = "مسح الباركود";
        FlowDirection = FlowDirection.RightToLeft;
        _scanner = new CameraBarcodeReaderView
        {
            Options = new BarcodeReaderOptions
            {
                Formats = BarcodeFormats.All,
                AutoRotate = true,
                Multiple = false
            },
            IsDetecting = false
        };
        _scanner.BarcodesDetected += OnBarcodesDetected;
        var torch = MobileUi.PrimaryButton("إضاءة");
        torch.Clicked += (_, _) => _scanner.IsTorchOn = !_scanner.IsTorchOn;
        Content = new Grid
        {
            RowDefinitions = { new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) },
            Children = { _scanner, torch.Position(0,1) }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted) status = await Permissions.RequestAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
        {
            await DisplayAlert("الكاميرا", "صلاحية الكاميرا مطلوبة لمسح الباركود.", "حسناً");
            await Navigation.PopAsync();
            return;
        }
        Interlocked.Exchange(ref _handled, 0);
        _scanner.IsDetecting = true;
    }

    protected override void OnDisappearing()
    {
        _scanner.IsDetecting = false;
        base.OnDisappearing();
    }

    private void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        var value = e.Results.FirstOrDefault()?.Value;
        if (string.IsNullOrWhiteSpace(value) || Interlocked.Exchange(ref _handled, 1) != 0) return;
        _scanner.IsDetecting = false;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await _onScanned(value).ConfigureAwait(true);
                await Navigation.PopAsync().ConfigureAwait(true);
            }
            catch
            {
                Interlocked.Exchange(ref _handled, 0);
                _scanner.IsDetecting = true;
            }
        });
    }
}

internal static class ViewPositionExtensions
{
    public static T Position<T>(this T view, int column, int row) where T : View
    {
        Grid.SetColumn(view, column);
        Grid.SetRow(view, row);
        return view;
    }
}
