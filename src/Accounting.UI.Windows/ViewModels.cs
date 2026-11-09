using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.NetworkInformation;
using Accounting.Application;
using Accounting.Modules;
using Accounting.Sync.Client;

namespace Accounting.UI.Windows;

public sealed class InvoiceLineViewModel : ObservableObject
{
    private decimal _quantity = 1m;
    private decimal _baseUnitsPerUnit = 1m;
    private decimal _unitPrice;

    public Guid ItemId { get; init; }
    public Guid UnitOfMeasureId { get; init; }
    public string Sku { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public decimal Quantity { get => _quantity; set { if (Set(ref _quantity, value)) Raise(nameof(LineTotal)); } }
    public decimal BaseUnitsPerUnit { get => _baseUnitsPerUnit; set => Set(ref _baseUnitsPerUnit, value); }
    public decimal UnitPrice { get => _unitPrice; set { if (Set(ref _unitPrice, value)) Raise(nameof(LineTotal)); } }
    public decimal LineTotal => Quantity * UnitPrice;
}

public sealed class InvoiceEditorViewModel : ObservableObject, IWorkspaceActionTarget
{
    private readonly WindowsAccountingFacade _facade;
    private readonly Action<string, string, object> _openTab;
    private readonly bool _isSales;
    private CancellationTokenSource? _barcodeDelay;
    private string _barcodeInput = string.Empty;
    private string _status = string.Empty;
    private InvoiceLineViewModel? _selectedLine;
    private decimal _subtotal;
    private decimal _tax;
    private decimal _grandTotal;
    private CompanyPolicy? _policy;

    public InvoiceEditorViewModel(WindowsAccountingFacade facade, Action<string, string, object> openTab, bool isSales, string moduleKey)
    {
        _facade = facade;
        _openTab = openTab;
        _isSales = isSales;
        ModuleKey = moduleKey;
        if (string.Equals(moduleKey, RestaurantModule.ModuleKey, StringComparison.OrdinalIgnoreCase))
            ModuleMetadataJson = "{\"serviceType\":1,\"tableOrDeliveryReference\":null}";
        SaveCommand = new AsyncRelayCommand(SaveAndPostAsync);
        SearchCommand = new AsyncRelayCommand(QuickSearchAsync);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        DetailsCommand = new AsyncRelayCommand(OpenSelectedDetailsAsync);
        RemoveLineCommand = new RelayCommand(RemoveSelectedLine, () => SelectedLine is not null);
        _ = LoadPolicyAsync();
    }

    public string Title => _isSales ? "فاتورة مبيعات" : "فاتورة مشتريات";
    public string ModuleKey { get; set; }
    public bool RequiresWarehouse => !string.Equals(ModuleKey, ServicesModule.ModuleKey, StringComparison.OrdinalIgnoreCase);
    public string ModuleMetadataJson { get; set; } = "{}";
    public string WarehouseIdText { get; set; } = string.Empty;
    public string SettlementAccountIdText { get; set; } = string.Empty;
    public string PartnerIdText { get; set; } = string.Empty;
    public ObservableCollection<InvoiceLineViewModel> Lines { get; } = new();
    public InvoiceLineViewModel? SelectedLine { get => _selectedLine; set { Set(ref _selectedLine, value); RemoveLineCommand.Refresh(); } }
    public decimal Subtotal { get => _subtotal; private set => Set(ref _subtotal, value); }
    public decimal Tax { get => _tax; private set => Set(ref _tax, value); }
    public decimal GrandTotal { get => _grandTotal; private set => Set(ref _grandTotal, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }

    public string BarcodeInput
    {
        get => _barcodeInput;
        set
        {
            if (!Set(ref _barcodeInput, value)) return;
            _barcodeDelay?.Cancel();
            _barcodeDelay = new CancellationTokenSource();
            _ = DebounceBarcodeAsync(value, _barcodeDelay.Token);
        }
    }

    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand SearchCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand DetailsCommand { get; }
    public RelayCommand RemoveLineCommand { get; }

    public async Task SaveAndPostAsync()
    {
        if (!Guid.TryParse(SettlementAccountIdText, out var settlement) || settlement == Guid.Empty)
        {
            Status = "حدد حساب التسوية قبل الترحيل.";
            return;
        }
        var warehouse = Guid.TryParse(WarehouseIdText, out var parsedWarehouse) ? parsedWarehouse : Guid.Empty;
        var partner = Guid.TryParse(PartnerIdText, out var parsedPartner) ? parsedPartner : (Guid?)null;
        if (Lines.Count == 0 || Lines.Any(x => x.Quantity <= 0m || x.BaseUnitsPerUnit <= 0m || x.UnitPrice <= 0m))
        {
            Status = "أضف سطراً واحداً على الأقل وتأكد أن الكمية والسعر أكبر من صفر.";
            return;
        }

        var draft = Lines.Select(x => new InvoiceDraftLine(x.ItemId, x.UnitOfMeasureId, x.Quantity, x.BaseUnitsPerUnit, x.UnitPrice)).ToArray();
        try
        {
            PostingResult result;
            if (_isSales)
                result = await _facade.PostSalesAsync(warehouse, settlement, partner, ModuleKey, ModuleMetadataJson, draft).ConfigureAwait(true);
            else
                result = await _facade.PostPurchaseAsync(warehouse, settlement, partner, ModuleKey, draft).ConfigureAwait(true);
            Status = $"تم الترحيل محلياً. Operation: {result.OperationId:N}. بانتظار الرقم الرسمي من الخادم.";
            Lines.Clear();
            Recalculate();
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    public Task QuickSearchAsync()
    {
        var vm = new QuickSearchViewModel(_facade);
        _openTab("quick-search", "بحث سريع", vm);
        return Task.CompletedTask;
    }

    public async Task RefreshAsync()
    {
        await LoadPolicyAsync().ConfigureAwait(true);
        Status = "تمت إعادة الحساب من سياسة الشركة.";
    }

    public async Task OpenSelectedDetailsAsync()
    {
        if (SelectedLine is null)
        {
            Status = "حدد سطر صنف أولاً لفتح حركة الصنف.";
            return;
        }
        if (!Guid.TryParse(WarehouseIdText, out var warehouse) || warehouse == Guid.Empty)
        {
            Status = "حدد المستودع لعرض حركة الصنف.";
            return;
        }

        var vm = new ItemMovementViewModel(_facade, SelectedLine.ItemId, warehouse, SelectedLine.Name);
        await vm.RefreshAsync().ConfigureAwait(true);
        _openTab($"item-movement:{SelectedLine.ItemId:N}:{warehouse:N}", $"حركة {SelectedLine.Name}", vm);
    }

    private async Task DebounceBarcodeAsync(string value, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            await Task.Delay(90, cancellationToken).ConfigureAwait(false);
            var item = await _facade.FindItemByBarcodeOrSkuAsync(value, cancellationToken).ConfigureAwait(false);
            if (item is null)
            {
                Status = $"لم يتم العثور على الصنف/الباركود: {value}";
                return;
            }

            var suggestedPrice = await _facade.GetSuggestedUnitPriceAsync(ModuleKey, item.Id, 1m, 0m, cancellationToken).ConfigureAwait(false);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var existing = Lines.FirstOrDefault(x => x.ItemId == item.Id);
                if (existing is not null)
                    existing.Quantity += 1m;
                else
                {
                    var line = new InvoiceLineViewModel
                    {
                        ItemId = item.Id,
                        UnitOfMeasureId = item.BaseUnitOfMeasureId,
                        Sku = item.Sku,
                        Name = item.Name,
                        Quantity = 1m,
                        BaseUnitsPerUnit = 1m,
                        UnitPrice = suggestedPrice
                    };
                    line.PropertyChanged += OnLineChanged;
                    Lines.Add(line);
                    SelectedLine = line;
                }
                BarcodeInput = string.Empty;
                Recalculate();
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    private void OnLineChanged(object? sender, PropertyChangedEventArgs e) => Recalculate();

    private async Task LoadPolicyAsync()
    {
        try
        {
            _policy = await _facade.GetPolicyAsync().ConfigureAwait(true);
            Recalculate();
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    private void Recalculate()
    {
        var subtotal = Lines.Sum(x => x.LineTotal);
        if (_policy is null)
        {
            Subtotal = subtotal;
            Tax = 0m;
            GrandTotal = subtotal;
            return;
        }

        var policy = _policy;
        Subtotal = decimal.Round(subtotal, policy.CurrencyScale, policy.RoundingMode);
        if (!policy.TaxPolicy.Enabled || policy.TaxPolicy.Rate == 0m)
            Tax = 0m;
        else if (policy.TaxPolicy.PricesIncludeTax)
        {
            var net = decimal.Round(Subtotal / (1m + policy.TaxPolicy.Rate), policy.CurrencyScale, policy.RoundingMode);
            Tax = decimal.Round(Subtotal - net, policy.CurrencyScale, policy.RoundingMode);
        }
        else
            Tax = decimal.Round(Subtotal * policy.TaxPolicy.Rate, policy.CurrencyScale, policy.RoundingMode);

        GrandTotal = policy.TaxPolicy.PricesIncludeTax
            ? Subtotal
            : decimal.Round(Subtotal + Tax, policy.CurrencyScale, policy.RoundingMode);
    }

    private void RemoveSelectedLine()
    {
        if (SelectedLine is null) return;
        SelectedLine.PropertyChanged -= OnLineChanged;
        Lines.Remove(SelectedLine);
        SelectedLine = null;
        Recalculate();
    }
}

public sealed class VoucherEditorViewModel : ObservableObject, IWorkspaceActionTarget
{
    private readonly WindowsAccountingFacade _facade;
    private readonly Action<string, string, object> _openTab;
    private readonly bool _receipt;
    private string _status = string.Empty;

    public VoucherEditorViewModel(WindowsAccountingFacade facade, Action<string, string, object> openTab, bool receipt)
    {
        _facade = facade; _openTab = openTab; _receipt = receipt;
        SaveCommand = new AsyncRelayCommand(SaveAndPostAsync);
    }

    public string Title => _receipt ? "سند قبض" : "سند صرف";
    public string CashAccountIdText { get; set; } = string.Empty;
    public string CounterAccountIdText { get; set; } = string.Empty;
    public string PartnerIdText { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public AsyncRelayCommand SaveCommand { get; }

    public async Task SaveAndPostAsync()
    {
        if (!Guid.TryParse(CashAccountIdText, out var cash) || !Guid.TryParse(CounterAccountIdText, out var counter) || Amount <= 0m)
        {
            Status = "تحقق من حساب الصندوق/البنك والحساب المقابل والمبلغ.";
            return;
        }
        var partner = Guid.TryParse(PartnerIdText, out var partnerId) ? partnerId : (Guid?)null;
        try
        {
            var result = await _facade.PostVoucherAsync(_receipt, cash, counter, partner, Amount, Description).ConfigureAwait(true);
            Status = $"تم الترحيل محلياً: {result.OperationId:N}";
            Amount = 0m; Description = string.Empty; Raise(nameof(Amount)); Raise(nameof(Description));
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    public Task QuickSearchAsync()
    {
        _openTab("quick-search", "بحث سريع", new QuickSearchViewModel(_facade));
        return Task.CompletedTask;
    }

    public Task RefreshAsync()
    {
        Status = "جاهز.";
        return Task.CompletedTask;
    }

    public Task OpenSelectedDetailsAsync()
    {
        Status = "F7: أدخل حساب الطرف المقابل ثم افتح دفتر الأستاذ من مساحة العمل.";
        return Task.CompletedTask;
    }
}

public sealed record QuickSearchResultRow(string Type, Guid Id, string Code, string Name, string Extra);

public sealed class QuickSearchViewModel : ObservableObject, IWorkspaceActionTarget
{
    private readonly WindowsAccountingFacade _facade;
    private string _query = string.Empty;
    private string _status = string.Empty;

    public QuickSearchViewModel(WindowsAccountingFacade facade)
    {
        _facade = facade;
        SearchCommand = new AsyncRelayCommand(SearchAsync);
    }

    public string Query { get => _query; set => Set(ref _query, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public ObservableCollection<QuickSearchResultRow> Results { get; } = new();
    public AsyncRelayCommand SearchCommand { get; }

    public async Task SearchAsync()
    {
        var itemsTask = _facade.SearchItemsAsync(Query);
        var partnersTask = _facade.SearchPartnersAsync(Query);
        var accountsTask = _facade.SearchAccountsAsync(Query);
        await Task.WhenAll(itemsTask, partnersTask, accountsTask).ConfigureAwait(true);
        Results.Clear();
        foreach (var x in itemsTask.Result) Results.Add(new("صنف", x.Id, x.Sku, x.Name, x.ItemKind.ToString()));
        foreach (var x in partnersTask.Result) Results.Add(new("عميل/مورد", x.Id, x.Code, x.Name, $"حد ائتماني {x.CreditLimit}"));
        foreach (var x in accountsTask.Result) Results.Add(new("حساب", x.Id, x.Code, x.Name, string.Empty));
        Status = $"{Results.Count} نتيجة";
    }

    public Task SaveAndPostAsync() { Status = "البحث لا يحتوي مستنداً للترحيل."; return Task.CompletedTask; }
    public Task QuickSearchAsync() => SearchAsync();
    public Task RefreshAsync() => SearchAsync();
    public Task OpenSelectedDetailsAsync() { Status = "انسخ المعرّف لفتح السجل المطلوب من الشاشة المختصة."; return Task.CompletedTask; }
}

public sealed class ItemsViewModel : ObservableObject, IWorkspaceActionTarget
{
    private readonly WindowsAccountingFacade _facade;
    public ObservableCollection<ItemLookup> Items { get; } = new();
    public ItemsViewModel(WindowsAccountingFacade facade) => _facade = facade;
    public Task SaveAndPostAsync() => RefreshAsync();
    public Task QuickSearchAsync() => RefreshAsync();
    public async Task RefreshAsync()
    {
        var rows = await _facade.SearchItemsAsync(string.Empty).ConfigureAwait(true);
        Items.Clear(); foreach (var x in rows) Items.Add(x);
    }
    public Task OpenSelectedDetailsAsync() => RefreshAsync();
}

public sealed class PartnersViewModel : ObservableObject, IWorkspaceActionTarget
{
    private readonly WindowsAccountingFacade _facade;
    public ObservableCollection<PartnerLookup> Partners { get; } = new();
    public PartnersViewModel(WindowsAccountingFacade facade) => _facade = facade;
    public Task SaveAndPostAsync() => RefreshAsync();
    public Task QuickSearchAsync() => RefreshAsync();
    public async Task RefreshAsync()
    {
        var rows = await _facade.SearchPartnersAsync(string.Empty).ConfigureAwait(true);
        Partners.Clear(); foreach (var x in rows) Partners.Add(x);
    }
    public Task OpenSelectedDetailsAsync() => RefreshAsync();
}

public sealed class ItemMovementViewModel : ObservableObject, IWorkspaceActionTarget
{
    private readonly WindowsAccountingFacade _facade;
    private readonly Guid _itemId;
    private readonly Guid _warehouseId;
    private string _summary = string.Empty;
    public string ItemName { get; }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public ObservableCollection<ItemMovementRow> Rows { get; } = new();

    public ItemMovementViewModel(WindowsAccountingFacade facade, Guid itemId, Guid warehouseId, string itemName)
    {
        _facade = facade; _itemId = itemId; _warehouseId = warehouseId; ItemName = itemName;
    }

    public async Task RefreshAsync()
    {
        var position = await _facade.GetStockPositionAsync(_itemId, _warehouseId).ConfigureAwait(true);
        var rows = await _facade.GetItemMovementAsync(_itemId, _warehouseId).ConfigureAwait(true);
        Rows.Clear(); foreach (var row in rows) Rows.Add(row);
        Summary = $"الكمية: {position.QuantityOnHand} | القيمة: {position.InventoryValue} | المتوسط: {position.AverageUnitCost}";
    }
    public Task SaveAndPostAsync() => RefreshAsync();
    public Task QuickSearchAsync() => RefreshAsync();
    public Task OpenSelectedDetailsAsync() => RefreshAsync();
}

public sealed class LedgerViewModel : ObservableObject, IWorkspaceActionTarget
{
    private readonly WindowsAccountingFacade _facade;
    private readonly Action<string, string, object> _openTab;
    private string _accountIdText = string.Empty;
    private string _status = string.Empty;

    public LedgerViewModel(WindowsAccountingFacade facade, Action<string, string, object> openTab)
    {
        _facade = facade; _openTab = openTab;
    }

    public string AccountIdText { get => _accountIdText; set => Set(ref _accountIdText, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public ObservableCollection<LedgerRow> Rows { get; } = new();
    public ObservableCollection<TrialBalanceRow> TrialBalance { get; } = new();

    public Task SaveAndPostAsync() => RefreshAsync();
    public Task QuickSearchAsync()
    {
        _openTab("quick-search", "بحث سريع", new QuickSearchViewModel(_facade));
        return Task.CompletedTask;
    }

    public async Task RefreshAsync()
    {
        TrialBalance.Clear();
        foreach (var row in await _facade.GetTrialBalanceAsync().ConfigureAwait(true)) TrialBalance.Add(row);
        if (Guid.TryParse(AccountIdText, out var accountId))
        {
            Rows.Clear();
            foreach (var row in await _facade.GetLedgerAsync(accountId).ConfigureAwait(true)) Rows.Add(row);
            Status = $"عدد حركات الحساب: {Rows.Count}";
        }
        else Status = "ميزان المراجعة محدث. أدخل AccountId لعرض دفتر الأستاذ.";
    }

    public Task OpenSelectedDetailsAsync() => RefreshAsync();
}

public sealed class ShellViewModel : ObservableObject
{
    private readonly WindowsAccountingFacade _facade;
    private readonly LocalSyncStore _syncStore;
    private readonly Accounting.SharedKernel.UserContext _user;
    private string _networkStatus = "Offline";
    private string _syncStatus = string.Empty;

    public ShellViewModel(WindowsAccountingFacade facade, LocalSyncStore syncStore, Accounting.SharedKernel.UserContext user,
        ModuleRegistry modules, IReadOnlyList<string> enabledModules, string branchName, string userName)
    {
        _facade = facade; _syncStore = syncStore; _user = user;
        BranchName = branchName; UserName = userName;
        Workspace = new TabWorkspaceManager();
        Shortcuts = new GlobalKeyboardShortcutHandler(Workspace);
        ModuleScreens = new ObservableCollection<ModuleScreenDefinition>(modules.VisibleScreens(enabledModules, ActivityPlatform.Windows));
        OpenSalesCommand = new RelayCommand(() => OpenTab("sales", "فاتورة مبيعات", new InvoiceEditorViewModel(_facade, OpenTab, true, enabledModules.FirstOrDefault() ?? RetailModule.ModuleKey)));
        OpenPurchaseCommand = new RelayCommand(() => OpenTab("purchase", "فاتورة مشتريات", new InvoiceEditorViewModel(_facade, OpenTab, false, enabledModules.FirstOrDefault() ?? RetailModule.ModuleKey)));
        OpenReceiptCommand = new RelayCommand(() => OpenTab("receipt", "سند قبض", new VoucherEditorViewModel(_facade, OpenTab, true)));
        OpenPaymentCommand = new RelayCommand(() => OpenTab("payment", "سند صرف", new VoucherEditorViewModel(_facade, OpenTab, false)));
        OpenItemsCommand = new RelayCommand(() => OpenTab("items", "الأصناف", new ItemsViewModel(_facade)));
        OpenPartnersCommand = new RelayCommand(() => OpenTab("partners", "العملاء والموردون", new PartnersViewModel(_facade)));
        OpenLedgerCommand = new RelayCommand(() => OpenTab("ledger", "دفتر الأستاذ وميزان المراجعة", new LedgerViewModel(_facade, OpenTab)));
        OpenModuleScreenCommand = new ParameterCommand(OpenModuleScreen);
    }

    public TabWorkspaceManager Workspace { get; }
    public GlobalKeyboardShortcutHandler Shortcuts { get; }
    public ObservableCollection<ModuleScreenDefinition> ModuleScreens { get; }
    public string BranchName { get; }
    public string UserName { get; }
    public string NetworkStatus { get => _networkStatus; private set => Set(ref _networkStatus, value); }
    public string SyncStatus { get => _syncStatus; private set => Set(ref _syncStatus, value); }
    public RelayCommand OpenSalesCommand { get; }
    public RelayCommand OpenPurchaseCommand { get; }
    public RelayCommand OpenReceiptCommand { get; }
    public RelayCommand OpenPaymentCommand { get; }
    public RelayCommand OpenItemsCommand { get; }
    public RelayCommand OpenPartnersCommand { get; }
    public RelayCommand OpenLedgerCommand { get; }
    public ParameterCommand OpenModuleScreenCommand { get; }

    public void OpenTab(string key, string title, object viewModel) => Workspace.Open(key, title, () => viewModel);

    public async Task RefreshStatusAsync()
    {
        NetworkStatus = NetworkInterface.GetIsNetworkAvailable() ? "Online" : "Offline";
        var status = await _syncStore.GetStatusAsync(_user).ConfigureAwait(true);
        SyncStatus = $"Outbox: {status.PendingOutbox + status.RetryableFailures} | تعارضات: {status.UnresolvedConflicts} | آخر مزامنة: {(status.LastSuccessfulSyncUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-")}";
    }

    private void OpenModuleScreen(object? parameter)
    {
        if (parameter is not ModuleScreenDefinition screen) return;
        var moduleKey = screen.Key.Split('.')[0].ToUpperInvariant() switch
        {
            "RETAIL" => RetailModule.ModuleKey,
            "WHOLESALE" => WholesaleModule.ModuleKey,
            "SERVICES" => ServicesModule.ModuleKey,
            "RESTAURANT" => RestaurantModule.ModuleKey,
            _ => RetailModule.ModuleKey
        };
        if (screen.Key.Contains("invoice", StringComparison.OrdinalIgnoreCase) || screen.Key.Contains("sales", StringComparison.OrdinalIgnoreCase) || screen.Key.Contains("pos", StringComparison.OrdinalIgnoreCase) || screen.Key.Contains("order", StringComparison.OrdinalIgnoreCase))
            OpenTab(screen.Key, screen.Title, new InvoiceEditorViewModel(_facade, OpenTab, true, moduleKey));
        else
            OpenTab(screen.Key, screen.Title, new QuickSearchViewModel(_facade));
    }
}
