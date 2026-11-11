using System.Windows.Media;
using System.Collections.ObjectModel;
using Accounting.Domain;
using Accounting.Infrastructure.Local;
using Accounting.SharedKernel;
using Accounting.Sync.Client;

namespace Accounting.UI.Windows;

public sealed partial class InvoiceEditorViewModel
{
    private WarehouseLookup? _selectedWarehouse;
    private AccountLookup? _selectedSettlementAccount;
    private PartnerLookup? _selectedPartner;
    private string _paymentMethod = "نقدي";
    private ItemLookup? _selectedSearchItem;

    public ObservableCollection<WarehouseLookup> Warehouses { get; } = new();
    public ObservableCollection<ItemLookup> AvailableItems { get; } = new();
    public ObservableCollection<AccountLookup> SettlementAccounts { get; } = new();
    public ObservableCollection<PartnerLookup> Partners { get; } = new();
    public ObservableCollection<string> PaymentMethods { get; } = new() { "نقدي", "آجل" };
    public string PartnerLabel => _isSales ? "العميل" : "المورد";
    public string QuickPartnerButtonLabel => _isSales ? "+ عميل جديد سريع" : "+ مورد جديد سريع";
    public ItemLookup? SelectedSearchItem { get => _selectedSearchItem; set => Set(ref _selectedSearchItem, value); }
    public RelayCommand OpenInvoiceHistoryCommand => new(() => _openTab(_isSales ? "sales-history" : "purchase-history",
        _isSales ? "فواتير المبيعات السابقة" : "فواتير المشتريات السابقة", new InvoiceHistoryViewModel(_facade, _isSales)));
    public async Task<PartnerLookup> CreateQuickPartnerAsync(string name, string? phone)
    {
        var partner = await _facade.CreateQuickPartnerAsync(_isSales, name, phone).ConfigureAwait(true);
        Partners.Add(partner); SelectedPartner = partner; Status = _isSales ? "تمت إضافة العميل واختياره." : "تمت إضافة المورد واختياره.";
        return partner;
    }
    public AsyncRelayCommand AddSelectedItemCommand => new(async () =>
    {
        if (SelectedSearchItem is null) { Status = "اختر صنفاً من قائمة البحث."; return; }
        await AddItemAsync(SelectedSearchItem).ConfigureAwait(true);
        SelectedSearchItem = null; Raise(nameof(SelectedSearchItem));
    });

    public WarehouseLookup? SelectedWarehouse
    {
        get => _selectedWarehouse;
        set { if (Set(ref _selectedWarehouse, value) && value is not null) WarehouseIdText = value.Id.ToString("D"); }
    }
    public AccountLookup? SelectedSettlementAccount
    {
        get => _selectedSettlementAccount;
        set { if (Set(ref _selectedSettlementAccount, value) && value is not null) SettlementAccountIdText = value.Id.ToString("D"); }
    }
    public PartnerLookup? SelectedPartner
    {
        get => _selectedPartner;
        set
        {
            if (!Set(ref _selectedPartner, value)) return;
            PartnerIdText = value?.Id.ToString("D") ?? string.Empty;
            if (_isSales && PaymentMethod == "آجل" && value is not null)
                SelectedSettlementAccount = SettlementAccounts.FirstOrDefault(x => x.Id == value.ControlAccountId) ?? SelectedSettlementAccount;
        }
    }
    public string PaymentMethod
    {
        get => _paymentMethod;
        set
        {
            if (!Set(ref _paymentMethod, string.IsNullOrWhiteSpace(value) ? "نقدي" : value)) return;
            if (_isSales && _paymentMethod == "آجل" && SelectedPartner is not null)
                SelectedSettlementAccount = SettlementAccounts.FirstOrDefault(x => x.Id == SelectedPartner.ControlAccountId) ?? SelectedSettlementAccount;
            if (_isSales && _paymentMethod == "نقدي")
                SelectedSettlementAccount = SettlementAccounts.FirstOrDefault(x => x.Code.StartsWith("111", StringComparison.Ordinal)) ?? SelectedSettlementAccount;
        }
    }

    private async Task InitializeProductionInvoiceAsync()
    {
        try
        {
            var warehousesTask = _facade.GetWarehousesAsync();
            var accountsTask = _facade.SearchAccountsAsync(string.Empty);
            var cashTask = _facade.GetCashBankAccountsAsync();
            var partnersTask = _facade.SearchPartnersByTypeAsync(_isSales ? PartnerType.Customer : PartnerType.Supplier);
            var itemsTask = _facade.SearchItemsAsync(string.Empty);
            await Task.WhenAll(warehousesTask, accountsTask, cashTask, partnersTask, itemsTask).ConfigureAwait(true);
            Warehouses.Clear(); foreach (var row in warehousesTask.Result) Warehouses.Add(row);
            Partners.Clear(); foreach (var row in partnersTask.Result) Partners.Add(row);
            AvailableItems.Clear(); foreach (var row in itemsTask.Result) AvailableItems.Add(row);
            SettlementAccounts.Clear();
            if (_isSales)
            {
                foreach (var row in cashTask.Result) SettlementAccounts.Add(row);
                var receivableIds = Partners.Select(x => x.ControlAccountId).ToHashSet();
                foreach (var row in accountsTask.Result.Where(x => receivableIds.Contains(x.Id))) SettlementAccounts.Add(row);
            }
            else
            {
                foreach (var row in cashTask.Result) SettlementAccounts.Add(row);
                var payableIds = Partners.Select(x => x.ControlAccountId).ToHashSet();
                foreach (var row in accountsTask.Result.Where(x => payableIds.Contains(x.Id))) SettlementAccounts.Add(row);
            }
            SelectedWarehouse = Warehouses.FirstOrDefault();
            SelectedSettlementAccount = SettlementAccounts.FirstOrDefault(x => x.Code.StartsWith("111", StringComparison.Ordinal) || x.Code.StartsWith("112", StringComparison.Ordinal))
                                        ?? SettlementAccounts.FirstOrDefault();
        }
        catch (Exception ex) { Status = ex.Message; }
    }
}

public sealed partial class VoucherEditorViewModel
{
    private AccountLookup? _selectedCashAccount;
    private AccountLookup? _selectedCounterAccount;
    private PartnerLookup? _selectedVoucherPartner;

    public ObservableCollection<AccountLookup> VoucherAccounts { get; } = new();
    public ObservableCollection<AccountLookup> VoucherCashAccounts { get; } = new();
    public ObservableCollection<PartnerLookup> VoucherPartners { get; } = new();
    public AccountLookup? SelectedCashAccount { get => _selectedCashAccount; set { if (Set(ref _selectedCashAccount, value) && value is not null) CashAccountIdText = value.Id.ToString("D"); } }
    public AccountLookup? SelectedCounterAccount { get => _selectedCounterAccount; set { if (Set(ref _selectedCounterAccount, value) && value is not null) CounterAccountIdText = value.Id.ToString("D"); } }
    public PartnerLookup? SelectedVoucherPartner
    {
        get => _selectedVoucherPartner;
        set
        {
            if (!Set(ref _selectedVoucherPartner, value)) return;
            PartnerIdText = value?.Id.ToString("D") ?? string.Empty;
            if (value is not null) SelectedCounterAccount = VoucherAccounts.FirstOrDefault(x => x.Id == value.ControlAccountId) ?? SelectedCounterAccount;
        }
    }

    private async Task InitializeProductionVoucherAsync()
    {
        try
        {
            var accountsTask = _facade.SearchAccountsAsync(string.Empty);
            var cashTask = _facade.GetCashBankAccountsAsync();
            var partnersTask = _facade.SearchPartnersByTypeAsync(_receipt ? PartnerType.Customer : PartnerType.Supplier);
            await Task.WhenAll(accountsTask, cashTask, partnersTask).ConfigureAwait(true);
            VoucherAccounts.Clear(); foreach (var row in accountsTask.Result) VoucherAccounts.Add(row);
            VoucherCashAccounts.Clear(); foreach (var row in cashTask.Result) VoucherCashAccounts.Add(row);
            VoucherPartners.Clear(); foreach (var row in partnersTask.Result) VoucherPartners.Add(row);
            SelectedCashAccount = VoucherCashAccounts.FirstOrDefault();
            SelectedCounterAccount = VoucherAccounts.FirstOrDefault(x => x.Id != SelectedCashAccount?.Id);
        }
        catch (Exception ex) { Status = ex.Message; }
    }
}

public sealed class DashboardViewModel : ObservableObject, IWorkspaceActionTarget
{
    private readonly WindowsAccountingFacade _facade;
    private readonly Action<string,string,object> _open;
    private decimal _sales, _cash, _inventoryValue;
    private int _items, _outbox;
    private string _company = "-", _currency = "YER", _status = "جاهز";
    public DashboardViewModel(WindowsAccountingFacade facade, Action<string,string,object> open)
    {
        _facade = facade; _open = open;
        OpenSaleCommand = new RelayCommand(() => _open("sales","فاتورة مبيعات",new InvoiceEditorViewModel(_facade,_open,true,"RETAIL")));
        OpenReceiptCommand = new RelayCommand(() => _open("receipt","سند قبض",new VoucherEditorViewModel(_facade,_open,true)));
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        _ = RefreshAsync();
    }
    public decimal SalesTotal { get => _sales; private set => Set(ref _sales,value); }
    public decimal CashBalance { get => _cash; private set => Set(ref _cash,value); }
    public decimal InventoryValue { get => _inventoryValue; private set => Set(ref _inventoryValue,value); }
    public int ItemCount { get => _items; private set => Set(ref _items,value); }
    public int PendingOutbox { get => _outbox; private set => Set(ref _outbox,value); }
    public string CompanyName { get => _company; private set => Set(ref _company,value); }
    public string Currency { get => _currency; private set => Set(ref _currency,value); }
    public string Status { get => _status; private set => Set(ref _status,value); }
    public ObservableCollection<RecentJournalRow> RecentJournals { get; } = new();
    public RelayCommand OpenSaleCommand { get; }
    public RelayCommand OpenReceiptCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public async Task RefreshAsync()
    {
        try
        {
            var profileTask=_facade.GetCompanyProfileAsync(); var snapshotTask=_facade.GetDashboardAsync();
            await Task.WhenAll(profileTask,snapshotTask).ConfigureAwait(true);
            var p=profileTask.Result; var x=snapshotTask.Result;
            CompanyName=p.CompanyName; Currency=p.CurrencyCode; SalesTotal=x.SalesTotal; CashBalance=x.CashAndBankBalance;
            InventoryValue=x.InventoryValue; ItemCount=x.ItemCount; PendingOutbox=x.PendingOutbox;
            RecentJournals.Clear(); foreach(var row in x.RecentJournals) RecentJournals.Add(row);
            Status=$"آخر تحديث {DateTime.Now:HH:mm:ss}";
        } catch(Exception ex){ Status=ex.Message; }
    }
    public Task SaveAndPostAsync()=>Task.CompletedTask;
    public Task QuickSearchAsync()=>Task.CompletedTask;
    public Task OpenSelectedDetailsAsync()=>Task.CompletedTask;
}

public sealed partial class ItemsViewModel
{
    private WarehouseLookup? _selectedWarehouse;
    private string _status = "جاهز";
    public ObservableCollection<WarehouseLookup> Warehouses { get; } = new();
    public ObservableCollection<InventoryGridRow> InventoryRows { get; } = new();
    public WarehouseLookup? SelectedWarehouse { get => _selectedWarehouse; set { if (Set(ref _selectedWarehouse,value)) _=RefreshProductionAsync(); } }
    public string ProductionStatus { get => _status; private set => Set(ref _status,value); }
    public async Task RefreshProductionAsync()
    {
        try
        {
            if (Warehouses.Count==0)
            {
                foreach(var row in await _facade.GetWarehousesAsync().ConfigureAwait(true)) Warehouses.Add(row);
                if (SelectedWarehouse is null && Warehouses.Count>0) { _selectedWarehouse=Warehouses[0]; Raise(nameof(SelectedWarehouse)); }
            }
            InventoryRows.Clear();
            foreach(var row in await _facade.GetInventoryGridAsync(SelectedWarehouse?.Id).ConfigureAwait(true)) InventoryRows.Add(row);
            ProductionStatus=$"{InventoryRows.Count} صنف";
        } catch(Exception ex){ ProductionStatus=ex.Message; }
    }
}

public sealed class InventoryWorkspaceViewModel : ObservableObject, IWorkspaceActionTarget
{
    private readonly WindowsAccountingFacade _facade;
    private readonly Action<string,string,object> _open;
    private WarehouseLookup? _warehouse;
    private InventoryGridRow? _selectedItem;
    private UnitLookup? _baseUnit, _cartonUnit;
    private AccountLookup? _inventoryAccount, _salesAccount, _cogsAccount;
    private string _sku="", _name="", _barcode="", _status="جاهز";
    private decimal _cartonFactor=12m;

    public ObservableCollection<WarehouseLookup> Warehouses { get; } = new();
    public ObservableCollection<InventoryGridRow> Rows { get; } = new();
    public ObservableCollection<UnitLookup> Units { get; } = new();
    public ObservableCollection<AccountLookup> Accounts { get; } = new();
    public WarehouseLookup? SelectedWarehouse { get=>_warehouse; set { if(Set(ref _warehouse,value)) _=RefreshAsync(); } }
    public InventoryGridRow? SelectedItem { get=>_selectedItem; set=>Set(ref _selectedItem,value); }
    public UnitLookup? BaseUnit { get=>_baseUnit; set=>Set(ref _baseUnit,value); }
    public UnitLookup? CartonUnit { get=>_cartonUnit; set=>Set(ref _cartonUnit,value); }
    public AccountLookup? InventoryAccount { get=>_inventoryAccount; set=>Set(ref _inventoryAccount,value); }
    public AccountLookup? SalesAccount { get=>_salesAccount; set=>Set(ref _salesAccount,value); }
    public AccountLookup? CogsAccount { get=>_cogsAccount; set=>Set(ref _cogsAccount,value); }
    public string NewSku { get=>_sku; set=>Set(ref _sku,value); }
    public string NewName { get=>_name; set=>Set(ref _name,value); }
    public string NewBarcode { get=>_barcode; set=>Set(ref _barcode,value); }
    public decimal CartonFactor { get=>_cartonFactor; set=>Set(ref _cartonFactor,value); }
    public string Status { get=>_status; private set=>Set(ref _status,value); }
    public AsyncRelayCommand AddItemCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand OpenStockLedgerCommand { get; }

    public InventoryWorkspaceViewModel(WindowsAccountingFacade facade, Action<string,string,object> open)
    {
        _facade=facade; _open=open;
        AddItemCommand=new AsyncRelayCommand(AddItemAsync);
        RefreshCommand=new AsyncRelayCommand(RefreshAsync);
        OpenStockLedgerCommand=new AsyncRelayCommand(OpenStockLedgerAsync);
        _=InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var w=_facade.GetWarehousesAsync(); var u=_facade.GetUnitsAsync(); var a=_facade.SearchAccountsAsync("");
            await Task.WhenAll(w,u,a).ConfigureAwait(true);
            Warehouses.Clear(); foreach(var x in w.Result)Warehouses.Add(x);
            Units.Clear(); foreach(var x in u.Result)Units.Add(x);
            Accounts.Clear(); foreach(var x in a.Result)Accounts.Add(x);
            _warehouse=Warehouses.FirstOrDefault(); Raise(nameof(SelectedWarehouse));
            BaseUnit=Units.FirstOrDefault(x=>x.Code=="EA")??Units.FirstOrDefault();
            CartonUnit=Units.FirstOrDefault(x=>x.Code=="CTN")??Units.Skip(1).FirstOrDefault();
            InventoryAccount=Accounts.FirstOrDefault(x=>x.Code=="1140")??Accounts.FirstOrDefault();
            SalesAccount=Accounts.FirstOrDefault(x=>x.Code=="4100")??Accounts.FirstOrDefault();
            CogsAccount=Accounts.FirstOrDefault(x=>x.Code=="5100")??Accounts.FirstOrDefault();
            await RefreshAsync().ConfigureAwait(true);
        } catch(Exception ex){Status=ex.Message;}
    }

    public async Task RefreshAsync()
    {
        try
        {
            if(SelectedWarehouse is null)return;
            Rows.Clear(); foreach(var x in await _facade.GetInventoryGridAsync(SelectedWarehouse.Id).ConfigureAwait(true))Rows.Add(x);
            Status=$"{Rows.Count} صنف في {SelectedWarehouse.Name}";
        }catch(Exception ex){Status=ex.Message;}
    }

    private async Task AddItemAsync()
    {
        try
        {
            if(BaseUnit is null||InventoryAccount is null||SalesAccount is null||CogsAccount is null)throw new InvalidOperationException("أكمل الوحدة والحسابات.");
            var id=await _facade.CreateLocalItemAsync(NewSku,NewName,NewBarcode,BaseUnit.Id,InventoryAccount.Id,SalesAccount.Id,CogsAccount.Id,CartonUnit?.Id,CartonFactor).ConfigureAwait(true);
            Status=$"تمت إضافة الصنف إلى SQLite: {id:N}";
            NewSku=NewName=NewBarcode=""; Raise(nameof(NewSku));Raise(nameof(NewName));Raise(nameof(NewBarcode));
            await RefreshAsync().ConfigureAwait(true);
        }catch(Exception ex){Status=ex.Message;}
    }

    private async Task OpenStockLedgerAsync()
    {
        if(SelectedItem is null||SelectedWarehouse is null){Status="حدد صنفاً ومستودعاً.";return;}
        var vm=new ItemMovementViewModel(_facade,SelectedItem.ItemId,SelectedWarehouse.Id,SelectedItem.Name);
        await vm.RefreshAsync().ConfigureAwait(true);
        _open($"stock:{SelectedItem.ItemId:N}:{SelectedWarehouse.Id:N}",$"كرت الصنف - {SelectedItem.Name}",vm);
    }
    public Task SaveAndPostAsync()=>AddItemAsync();
    public Task QuickSearchAsync()=>RefreshAsync();
    public Task OpenSelectedDetailsAsync()=>OpenStockLedgerAsync();
}

public sealed class VouchersWorkspaceViewModel : ObservableObject, IWorkspaceActionTarget
{
    private readonly WindowsAccountingFacade _facade; private readonly Action<string,string,object> _open; private string _status="جاهز";
    public ObservableCollection<VoucherHistoryRow> Rows { get; }=new();
    public string Status { get=>_status; private set=>Set(ref _status,value); }
    public RelayCommand NewReceiptCommand { get; } public RelayCommand NewPaymentCommand { get; }
    public VouchersWorkspaceViewModel(WindowsAccountingFacade facade,Action<string,string,object> open)
    {
        _facade=facade; _open=open;
        NewReceiptCommand=new RelayCommand(()=>_open("receipt","سند قبض",new VoucherEditorViewModel(_facade,_open,true)));
        NewPaymentCommand=new RelayCommand(()=>_open("payment","سند صرف",new VoucherEditorViewModel(_facade,_open,false)));
        _=RefreshAsync();
    }
    public async Task RefreshAsync(){try{Rows.Clear();foreach(var x in await _facade.GetVoucherHistoryAsync().ConfigureAwait(true))Rows.Add(x);Status=$"{Rows.Count} سند مرحّل";}catch(Exception ex){Status=ex.Message;}}
    public Task SaveAndPostAsync()=>RefreshAsync(); public Task QuickSearchAsync()=>RefreshAsync(); public Task OpenSelectedDetailsAsync()=>RefreshAsync();
}

public sealed class ManualJournalLineViewModel : ObservableObject
{
    private AccountLookup? _account;
    private decimal _debit,_credit;
    private string _memo="";
    public AccountLookup? Account { get=>_account; set=>Set(ref _account,value); }
    public decimal Debit { get=>_debit; set { if(value<0)return; if(Set(ref _debit,value)&&value>0&&_credit!=0){_credit=0;Raise(nameof(Credit));} } }
    public decimal Credit { get=>_credit; set { if(value<0)return; if(Set(ref _credit,value)&&value>0&&_debit!=0){_debit=0;Raise(nameof(Debit));} } }
    public string Memo { get=>_memo; set=>Set(ref _memo,value); }
}

public sealed class AccountsWorkspaceViewModel : ObservableObject, IWorkspaceActionTarget
{
    private readonly WindowsAccountingFacade _facade; private JournalHeaderRow? _selectedJournal; private AccountLookup? _manualDebit,_manualCredit;
    private decimal _manualAmount; private string _manualDescription="",_manualReference="",_status="جاهز";
    public ObservableCollection<AccountTreeRow> Accounts { get; }=new();
    public ObservableCollection<JournalHeaderRow> Journals { get; }=new();
    public ObservableCollection<JournalDetailRow> JournalLines { get; }=new();
    public ObservableCollection<TrialBalanceRow> TrialBalance { get; }=new();
    public ObservableCollection<AccountLookup> PostableAccounts { get; }=new();
    public ObservableCollection<ManualJournalLineViewModel> ManualLines { get; }=new();
    private ManualJournalLineViewModel? _selectedManualLine;
    public ManualJournalLineViewModel? SelectedManualLine { get=>_selectedManualLine; set=>Set(ref _selectedManualLine,value); }
    public JournalHeaderRow? SelectedJournal { get=>_selectedJournal; set { if(Set(ref _selectedJournal,value)) _=LoadJournalLinesAsync(); } }
    public AccountLookup? ManualDebitAccount { get=>_manualDebit; set=>Set(ref _manualDebit,value); }
    public AccountLookup? ManualCreditAccount { get=>_manualCredit; set=>Set(ref _manualCredit,value); }
    public decimal ManualAmount { get=>_manualAmount; set=>Set(ref _manualAmount,value); }
    public string ManualDescription { get=>_manualDescription; set=>Set(ref _manualDescription,value); }
    public string ManualReference { get=>_manualReference; set=>Set(ref _manualReference,value); }
    public string Status { get=>_status; private set=>Set(ref _status,value); }
    public AsyncRelayCommand PostManualCommand { get; } public AsyncRelayCommand ReverseSelectedCommand { get; }
    public RelayCommand AddManualLineCommand { get; } public RelayCommand RemoveManualLineCommand { get; }
    public AccountsWorkspaceViewModel(WindowsAccountingFacade facade)
    {
        _facade=facade; PostManualCommand=new AsyncRelayCommand(PostManualAsync); ReverseSelectedCommand=new AsyncRelayCommand(ReverseSelectedAsync);
        AddManualLineCommand=new RelayCommand(AddManualLine); RemoveManualLineCommand=new RelayCommand(RemoveManualLine,()=>SelectedManualLine is not null);
        _=RefreshAsync();
    }
    public async Task RefreshAsync()
    {
        try
        {
            var a=_facade.GetAccountTreeAsync(); var j=_facade.GetRecentJournalsAsync(); var t=_facade.GetTrialBalanceAsync(); var p=_facade.SearchAccountsAsync("");
            await Task.WhenAll(a,j,t,p).ConfigureAwait(true);
            Accounts.Clear();foreach(var x in a.Result)Accounts.Add(x); Journals.Clear();foreach(var x in j.Result)Journals.Add(x);
            TrialBalance.Clear();foreach(var x in t.Result)TrialBalance.Add(x); PostableAccounts.Clear();foreach(var x in p.Result)PostableAccounts.Add(x);
            ManualDebitAccount??=PostableAccounts.FirstOrDefault(); ManualCreditAccount??=PostableAccounts.Skip(1).FirstOrDefault();
            if(ManualLines.Count==0)
            {
                ManualLines.Add(new ManualJournalLineViewModel{Account=PostableAccounts.FirstOrDefault(),Debit=0m,Memo=""});
                ManualLines.Add(new ManualJournalLineViewModel{Account=PostableAccounts.Skip(1).FirstOrDefault(),Credit=0m,Memo=""});
            }
            Status="تم تحديث الحسابات والقيود وميزان المراجعة.";
        }catch(Exception ex){Status=ex.Message;}
    }
    private async Task LoadJournalLinesAsync(){JournalLines.Clear();if(SelectedJournal is null)return;foreach(var x in await _facade.GetJournalDetailsAsync(SelectedJournal.Id).ConfigureAwait(true))JournalLines.Add(x);}
    private void AddManualLine()
    {
        var line=new ManualJournalLineViewModel{Account=PostableAccounts.FirstOrDefault(),Memo=ManualDescription};
        ManualLines.Add(line); SelectedManualLine=line; RemoveManualLineCommand.Refresh();
    }
    private void RemoveManualLine()
    {
        if(SelectedManualLine is null||ManualLines.Count<=2)return;
        ManualLines.Remove(SelectedManualLine); SelectedManualLine=ManualLines.LastOrDefault(); RemoveManualLineCommand.Refresh();
    }

    private async Task PostManualAsync()
    {
        try
        {
            var valid=ManualLines.Where(x=>x.Account is not null&&(x.Debit>0m||x.Credit>0m))
                .Select(x=>new ManualJournalLine(x.Account!.Id,x.Debit,x.Credit,string.IsNullOrWhiteSpace(x.Memo)?ManualDescription:x.Memo)).ToArray();
            if(valid.Length<2)throw new InvalidOperationException("أدخل سطرين محاسبيين على الأقل.");
            var debit=valid.Sum(x=>x.Debit);var credit=valid.Sum(x=>x.Credit);
            if(debit<=0m||debit!=credit)throw new InvalidOperationException($"القيد غير متوازن: مدين {debit:N2} / دائن {credit:N2}.");
            var id=await _facade.CreateManualJournalAsync(ManualDescription,ManualReference,valid).ConfigureAwait(true);
            Status=$"تم ترحيل القيد اليدوي محلياً: {id:N}";
            ManualLines.Clear();
            ManualLines.Add(new ManualJournalLineViewModel{Account=PostableAccounts.FirstOrDefault(),Memo=""});
            ManualLines.Add(new ManualJournalLineViewModel{Account=PostableAccounts.Skip(1).FirstOrDefault(),Memo=""});
            await RefreshAsync().ConfigureAwait(true);
        }catch(Exception ex){Status=ex.Message;}
    }
    private async Task ReverseSelectedAsync()
    {
        try
        {
            if(SelectedJournal is null)throw new InvalidOperationException("حدد قيداً أولاً.");
            if(string.Equals(SelectedJournal.SourceType,"ManualJournal",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("القيد اليدوي المحلي لا يرسل للخادم حالياً؛ أنشئ قيداً معاكساً متوازناً من نفس الشاشة.");
            var r=await _facade.ReverseDocumentAsync(SelectedJournal.SourceDocumentId).ConfigureAwait(true); Status=$"تم إنشاء القيد العكسي: {r.OperationId:N}"; await RefreshAsync().ConfigureAwait(true);
        }catch(Exception ex){Status=ex.Message;}
    }
    public Task SaveAndPostAsync()=>PostManualAsync(); public Task QuickSearchAsync()=>RefreshAsync(); public Task OpenSelectedDetailsAsync()=>LoadJournalLinesAsync();
}

public sealed class ReportsSyncViewModel : ObservableObject, IWorkspaceActionTarget
{
    private readonly WindowsAccountingFacade _facade; private readonly LocalSyncStore _syncStore; private readonly UserContext _user;
    private PartnerLookup? _selectedPartner; private string _partnerBalance="-",_syncSummary="-",_status="جاهز";
    public ObservableCollection<PartnerLookup> Partners { get; }=new(); public ObservableCollection<LedgerRow> StatementRows { get; }=new();
    public ObservableCollection<TrialBalanceRow> TrialBalance { get; }=new(); public ObservableCollection<OutboxUiRow> Outbox { get; }=new(); public ObservableCollection<ConflictUiRow> Conflicts { get; }=new();
    private ConflictUiRow? _selectedConflict;
    public ConflictUiRow? SelectedConflict { get=>_selectedConflict; set=>Set(ref _selectedConflict,value); }
    public PartnerLookup? SelectedPartner { get=>_selectedPartner; set=>Set(ref _selectedPartner,value); }
    public string PartnerBalance { get=>_partnerBalance; private set=>Set(ref _partnerBalance,value); }
    public string SyncSummary { get=>_syncSummary; private set=>Set(ref _syncSummary,value); }
    public string Status { get=>_status; private set=>Set(ref _status,value); }
    public AsyncRelayCommand LoadStatementCommand { get; } public AsyncRelayCommand RefreshCommand { get; } public AsyncRelayCommand ResolveConflictCommand { get; }
    public ReportsSyncViewModel(WindowsAccountingFacade facade,LocalSyncStore syncStore,UserContext user)
    { _facade=facade;_syncStore=syncStore;_user=user;LoadStatementCommand=new AsyncRelayCommand(LoadStatementAsync);RefreshCommand=new AsyncRelayCommand(RefreshAsync);ResolveConflictCommand=new AsyncRelayCommand(ResolveConflictAsync);_=RefreshAsync();}
    public async Task RefreshAsync()
    {
        try
        {
            var p=_facade.SearchPartnersAsync("");var t=_facade.GetTrialBalanceAsync();var s=_facade.GetSyncCenterAsync();await Task.WhenAll(p,t,s).ConfigureAwait(true);
            Partners.Clear();foreach(var x in p.Result)Partners.Add(x);if(SelectedPartner is null)SelectedPartner=Partners.FirstOrDefault();
            TrialBalance.Clear();foreach(var x in t.Result)TrialBalance.Add(x); Outbox.Clear();foreach(var x in s.Result.Outbox)Outbox.Add(x); Conflicts.Clear();foreach(var x in s.Result.Conflicts)Conflicts.Add(x);
            var ls=await _syncStore.GetStatusAsync(_user).ConfigureAwait(true);
            SyncSummary=$"Outbox {ls.PendingOutbox+ls.RetryableFailures} | تعارضات {ls.UnresolvedConflicts} | آخر مزامنة {(ls.LastSuccessfulSyncUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm")??"-")}";
            Status="تم تحديث التقارير ومركز المزامنة.";
        }catch(Exception ex){Status=ex.Message;}
    }
    private async Task LoadStatementAsync(){try{if(SelectedPartner is null)throw new InvalidOperationException("حدد العميل/المورد.");var x=await _facade.GetPartnerStatementAsync(SelectedPartner.Id).ConfigureAwait(true);StatementRows.Clear();foreach(var row in x.Rows)StatementRows.Add(row);PartnerBalance=$"{x.PartnerName} — الرصيد {x.Balance:N2}";}catch(Exception ex){Status=ex.Message;}}
    private async Task ResolveConflictAsync(){try{if(SelectedConflict is null)throw new InvalidOperationException("حدد تعارضاً.");await _facade.ResolveConflictLocallyAsync(SelectedConflict.Id).ConfigureAwait(true);Status="تم تعليم التعارض كمحلول محلياً بعد المعالجة.";await RefreshAsync().ConfigureAwait(true);}catch(Exception ex){Status=ex.Message;}}
    public Task SaveAndPostAsync()=>RefreshAsync(); public Task QuickSearchAsync()=>LoadStatementAsync(); public Task OpenSelectedDetailsAsync()=>LoadStatementAsync();
}

public sealed partial class ShellViewModel
{
    private string _productionCompanyName = "Qodynex";
    private string _productionCurrency = "YER";
    private ImageSource? _productionLogo;
    public string ProductionCompanyName { get => _productionCompanyName; private set => Set(ref _productionCompanyName, value); }
    public string ProductionCurrency { get => _productionCurrency; private set => Set(ref _productionCurrency, value); }
    public ImageSource? ProductionLogo { get => _productionLogo; private set => Set(ref _productionLogo, value); }
    public void OpenQuickSearch() => OpenTab("quick-search", "بحث سريع", new QuickSearchViewModel(_facade));
    public RelayCommand OpenDashboardCommand { get; private set; } = null!;
    public RelayCommand OpenVouchersWorkspaceCommand { get; private set; } = null!;
    public RelayCommand OpenInventoryWorkspaceCommand { get; private set; } = null!;
    public RelayCommand OpenAccountsWorkspaceCommand { get; private set; } = null!;
    public RelayCommand OpenReportsSyncCommand { get; private set; } = null!;
    public RelayCommand OpenSettingsCommand { get; private set; } = null!;

    private void InitializeProductionNavigation(WindowsAccountingFacade facade, LocalSyncStore syncStore, UserContext user, IReadOnlyList<string> enabledModules)
    {
        var moduleKey=enabledModules.FirstOrDefault() ?? "RETAIL";
        _ = LoadProductionHeaderAsync();
        OpenDashboardCommand=new RelayCommand(()=>OpenTab("dashboard","لوحة القيادة",new DashboardViewModel(facade,OpenTab)));
        OpenVouchersWorkspaceCommand=new RelayCommand(()=>OpenTab("vouchers","السندات المالية",new VouchersWorkspaceViewModel(facade,OpenTab)));
        OpenInventoryWorkspaceCommand=new RelayCommand(()=>OpenTab("inventory","المخزون والأصناف",new InventoryWorkspaceViewModel(facade,OpenTab)));
        OpenAccountsWorkspaceCommand=new RelayCommand(()=>OpenTab("accounts","الحسابات ودفتر الأستاذ",new AccountsWorkspaceViewModel(facade)));
        OpenReportsSyncCommand=new RelayCommand(()=>OpenTab("reports-sync","التقارير والمزامنة",new ReportsSyncViewModel(facade,syncStore,user)));
        OpenSettingsCommand=new RelayCommand(()=>OpenTab("settings","الإعدادات والكليشة",new CompanySettingsViewModel(facade)));
        OpenTab("dashboard","لوحة القيادة",new DashboardViewModel(facade,OpenTab));
    }

    public async Task ToggleThemeAsync()
    {
        var current = await _facade.GetBrandingAsync().ConfigureAwait(true);
        var updated = current with { DarkThemeEnabled = !ThemeManager.IsDark };
        await _facade.SaveBrandingAsync(updated).ConfigureAwait(true);
        ThemeManager.Apply(updated.DarkThemeEnabled);
    }

    private async Task LoadProductionHeaderAsync()
    {
        try
        {
            var profile = await _facade.GetCompanyProfileAsync().ConfigureAwait(true);
            var branding = await _facade.GetBrandingAsync().ConfigureAwait(true);
            ProductionCompanyName = string.IsNullOrWhiteSpace(branding.ArabicName) ? profile.CompanyName : branding.ArabicName;
            ProductionCurrency = branding.CurrencyCode;
            ProductionLogo = branding.LogoBytes is { Length: > 0 } ? CompanySettingsImageHelper.ToImage(branding.LogoBytes) : null;
            ThemeManager.Apply(branding.DarkThemeEnabled);
        }
        catch { }
    }
}


public sealed class InvoiceHistoryViewModel : ObservableObject, IWorkspaceActionTarget
{
    private readonly WindowsAccountingFacade _facade;
    private readonly bool _sales;
    private InvoiceHistoryRow? _selected;
    private string _status="جاهز";
    public ObservableCollection<InvoiceHistoryRow> Rows { get; } = new();
    public ObservableCollection<InvoiceHistoryLineRow> Lines { get; } = new();
    public InvoiceHistoryRow? Selected
    {
        get=>_selected;
        set { if(Set(ref _selected,value)) _=LoadLinesAsync(); }
    }
    public string Status { get=>_status; private set=>Set(ref _status,value); }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ReverseCommand { get; }

    public InvoiceHistoryViewModel(WindowsAccountingFacade facade,bool sales)
    {
        _facade=facade;_sales=sales;
        RefreshCommand=new AsyncRelayCommand(RefreshAsync);
        ReverseCommand=new AsyncRelayCommand(ReverseAsync,()=>Selected is not null&&!Selected.IsReversed);
        _=RefreshAsync();
    }
    public async Task RefreshAsync()
    {
        try
        {
            Rows.Clear(); foreach(var x in await _facade.GetInvoiceHistoryAsync(_sales).ConfigureAwait(true)) Rows.Add(x);
            if(Selected is null) Selected=Rows.FirstOrDefault();
            Status=$"{Rows.Count} فاتورة مرحّلة";
        }
        catch(Exception ex){Status=ex.Message;}
    }
    private async Task LoadLinesAsync()
    {
        Lines.Clear(); if(Selected is null)return;
        foreach(var x in await _facade.GetInvoiceHistoryLinesAsync(_sales,Selected.Id).ConfigureAwait(true))Lines.Add(x);
        ReverseCommand.Refresh();
    }
    private async Task ReverseAsync()
    {
        try
        {
            if(Selected is null)throw new InvalidOperationException("حدد فاتورة أولاً.");
            if(Selected.IsReversed)throw new InvalidOperationException("الفاتورة معكوسة مسبقاً.");
            var result=await _facade.ReverseDocumentAsync(Selected.Id).ConfigureAwait(true);
            Status=$"تم إنشاء مرتجع/عكس محاسبي للعملية {result.OperationId:N}.";
            Selected=null; await RefreshAsync().ConfigureAwait(true);
        }
        catch(Exception ex){Status=ex.Message;}
    }
    public Task SaveAndPostAsync()=>Task.CompletedTask;
    public Task QuickSearchAsync()=>RefreshAsync();
    public Task OpenSelectedDetailsAsync()=>LoadLinesAsync();
}
