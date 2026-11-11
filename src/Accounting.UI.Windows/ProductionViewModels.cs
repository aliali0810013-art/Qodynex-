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

    public ObservableCollection<WarehouseLookup> Warehouses { get; } = new();
    public ObservableCollection<AccountLookup> SettlementAccounts { get; } = new();
    public ObservableCollection<PartnerLookup> Partners { get; } = new();
    public ObservableCollection<string> PaymentMethods { get; } = new() { "نقدي", "آجل" };

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
            var partnersTask = _facade.SearchPartnersAsync(string.Empty);
            await Task.WhenAll(warehousesTask, accountsTask, partnersTask).ConfigureAwait(true);
            Warehouses.Clear(); foreach (var row in warehousesTask.Result) Warehouses.Add(row);
            SettlementAccounts.Clear(); foreach (var row in accountsTask.Result) SettlementAccounts.Add(row);
            Partners.Clear(); foreach (var row in partnersTask.Result) Partners.Add(row);
            SelectedWarehouse = Warehouses.FirstOrDefault();
            SelectedSettlementAccount = SettlementAccounts.FirstOrDefault(x => x.Code.StartsWith(_isSales ? "111" : "211", StringComparison.Ordinal))
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
            var partnersTask = _facade.SearchPartnersAsync(string.Empty);
            await Task.WhenAll(accountsTask, partnersTask).ConfigureAwait(true);
            VoucherAccounts.Clear(); foreach (var row in accountsTask.Result) VoucherAccounts.Add(row);
            VoucherPartners.Clear(); foreach (var row in partnersTask.Result) VoucherPartners.Add(row);
            SelectedCashAccount = VoucherAccounts.FirstOrDefault(x => x.Code.StartsWith("111", StringComparison.Ordinal)) ?? VoucherAccounts.FirstOrDefault();
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

public sealed class AccountsWorkspaceViewModel : ObservableObject, IWorkspaceActionTarget
{
    private readonly WindowsAccountingFacade _facade; private JournalHeaderRow? _selectedJournal; private AccountLookup? _manualDebit,_manualCredit;
    private decimal _manualAmount; private string _manualDescription="",_manualReference="",_status="جاهز";
    public ObservableCollection<AccountTreeRow> Accounts { get; }=new();
    public ObservableCollection<JournalHeaderRow> Journals { get; }=new();
    public ObservableCollection<JournalDetailRow> JournalLines { get; }=new();
    public ObservableCollection<TrialBalanceRow> TrialBalance { get; }=new();
    public ObservableCollection<AccountLookup> PostableAccounts { get; }=new();
    public JournalHeaderRow? SelectedJournal { get=>_selectedJournal; set { if(Set(ref _selectedJournal,value)) _=LoadJournalLinesAsync(); } }
    public AccountLookup? ManualDebitAccount { get=>_manualDebit; set=>Set(ref _manualDebit,value); }
    public AccountLookup? ManualCreditAccount { get=>_manualCredit; set=>Set(ref _manualCredit,value); }
    public decimal ManualAmount { get=>_manualAmount; set=>Set(ref _manualAmount,value); }
    public string ManualDescription { get=>_manualDescription; set=>Set(ref _manualDescription,value); }
    public string ManualReference { get=>_manualReference; set=>Set(ref _manualReference,value); }
    public string Status { get=>_status; private set=>Set(ref _status,value); }
    public AsyncRelayCommand PostManualCommand { get; } public AsyncRelayCommand ReverseSelectedCommand { get; }
    public AccountsWorkspaceViewModel(WindowsAccountingFacade facade)
    {
        _facade=facade; PostManualCommand=new AsyncRelayCommand(PostManualAsync); ReverseSelectedCommand=new AsyncRelayCommand(ReverseSelectedAsync); _=RefreshAsync();
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
            Status="تم تحديث الحسابات والقيود وميزان المراجعة.";
        }catch(Exception ex){Status=ex.Message;}
    }
    private async Task LoadJournalLinesAsync(){JournalLines.Clear();if(SelectedJournal is null)return;foreach(var x in await _facade.GetJournalDetailsAsync(SelectedJournal.Id).ConfigureAwait(true))JournalLines.Add(x);}
    private async Task PostManualAsync()
    {
        try
        {
            if(ManualDebitAccount is null||ManualCreditAccount is null||ManualAmount<=0)throw new InvalidOperationException("حدد حسابي المدين والدائن والمبلغ.");
            var id=await _facade.CreateManualJournalAsync(ManualDescription,ManualReference,new[]{new ManualJournalLine(ManualDebitAccount.Id,ManualAmount,0,"قيد يدوي"),new ManualJournalLine(ManualCreditAccount.Id,0,ManualAmount,"قيد يدوي")}).ConfigureAwait(true);
            Status=$"تم ترحيل القيد اليدوي محلياً: {id:N}"; ManualAmount=0; Raise(nameof(ManualAmount)); await RefreshAsync().ConfigureAwait(true);
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
    public PartnerLookup? SelectedPartner { get=>_selectedPartner; set=>Set(ref _selectedPartner,value); }
    public string PartnerBalance { get=>_partnerBalance; private set=>Set(ref _partnerBalance,value); }
    public string SyncSummary { get=>_syncSummary; private set=>Set(ref _syncSummary,value); }
    public string Status { get=>_status; private set=>Set(ref _status,value); }
    public AsyncRelayCommand LoadStatementCommand { get; } public AsyncRelayCommand RefreshCommand { get; }
    public ReportsSyncViewModel(WindowsAccountingFacade facade,LocalSyncStore syncStore,UserContext user)
    { _facade=facade;_syncStore=syncStore;_user=user;LoadStatementCommand=new AsyncRelayCommand(LoadStatementAsync);RefreshCommand=new AsyncRelayCommand(RefreshAsync);_=RefreshAsync();}
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
    public Task SaveAndPostAsync()=>RefreshAsync(); public Task QuickSearchAsync()=>LoadStatementAsync(); public Task OpenSelectedDetailsAsync()=>LoadStatementAsync();
}

public sealed partial class ShellViewModel
{
    public RelayCommand OpenDashboardCommand { get; private set; } = null!;
    public RelayCommand OpenVouchersWorkspaceCommand { get; private set; } = null!;
    public RelayCommand OpenInventoryWorkspaceCommand { get; private set; } = null!;
    public RelayCommand OpenAccountsWorkspaceCommand { get; private set; } = null!;
    public RelayCommand OpenReportsSyncCommand { get; private set; } = null!;

    private void InitializeProductionNavigation(WindowsAccountingFacade facade, LocalSyncStore syncStore, UserContext user, IReadOnlyList<string> enabledModules)
    {
        var moduleKey=enabledModules.FirstOrDefault() ?? "RETAIL";
        OpenDashboardCommand=new RelayCommand(()=>OpenTab("dashboard","لوحة القيادة",new DashboardViewModel(facade,OpenTab)));
        OpenVouchersWorkspaceCommand=new RelayCommand(()=>OpenTab("vouchers","السندات المالية",new VouchersWorkspaceViewModel(facade,OpenTab)));
        OpenInventoryWorkspaceCommand=new RelayCommand(()=>{var vm=new ItemsViewModel(facade);_=vm.RefreshProductionAsync();OpenTab("inventory","المخزون والأصناف",vm);});
        OpenAccountsWorkspaceCommand=new RelayCommand(()=>OpenTab("accounts","الحسابات ودفتر الأستاذ",new AccountsWorkspaceViewModel(facade)));
        OpenReportsSyncCommand=new RelayCommand(()=>OpenTab("reports-sync","التقارير والمزامنة",new ReportsSyncViewModel(facade,syncStore,user)));
        OpenTab("dashboard","لوحة القيادة",new DashboardViewModel(facade,OpenTab));
    }
}
