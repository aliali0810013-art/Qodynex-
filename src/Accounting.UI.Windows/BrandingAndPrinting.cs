using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps;
using System.Windows.Xps.Packaging;
using Accounting.Domain;
using Accounting.Infrastructure.Local;
using Accounting.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;

namespace Accounting.UI.Windows;

public sealed record CompanyBrandingSnapshot(
    string ArabicName, string EnglishName, string BusinessDescription, byte[]? LogoBytes,
    string City, string Address, string Phones, string Email, string TaxNumber,
    string CommercialRegistration, string FooterNotes, string CurrencyCode, string CurrencySymbol,
    bool TaxEnabled, decimal TaxRate, bool AllowNegativeStock, bool DarkThemeEnabled);

public sealed partial class WindowsAccountingFacade
{
    public async Task<CompanyBrandingSnapshot> GetBrandingAsync(CancellationToken ct = default)
    {
        var company = await _db.Companies.AsNoTracking().SingleAsync(x => x.Id == _user.CompanyId, ct).ConfigureAwait(false);
        var row = await _db.LocalCompanyBrandingSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId, ct).ConfigureAwait(false);
        if (row is null)
            return new(company.Name, "Qodynex", "", null, "", "", "", "", "", "", "",
                company.BaseCurrencyCode, company.BaseCurrencyCode, company.TaxEnabled, company.TaxRate,
                company.AllowNegativeStock, false);
        return new(row.ArabicName, row.EnglishName, row.BusinessDescription, row.LogoBytes, row.City, row.Address,
            row.Phones, row.Email, row.TaxNumber, row.CommercialRegistration, row.FooterNotes,
            row.CurrencyCode, row.CurrencySymbol, row.TaxEnabled, row.TaxRate, row.AllowNegativeStock, row.DarkThemeEnabled);
    }

    public async Task SaveBrandingAsync(CompanyBrandingSnapshot value, string? logoFileName = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(value.ArabicName)) throw new InvalidOperationException("اسم الشركة بالعربية مطلوب.");
        if (string.IsNullOrWhiteSpace(value.CurrencyCode)) throw new InvalidOperationException("العملة الأساسية مطلوبة.");
        if (value.TaxRate is < 0m or > 1m) throw new InvalidOperationException("نسبة الضريبة يجب أن تكون بين 0 و 1.");

        await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var company = await _db.Companies.SingleAsync(x => x.Id == _user.CompanyId, ct).ConfigureAwait(false);
            Guid? outputTax = company.OutputTaxAccountId, inputTax = company.InputTaxAccountId;
            if (value.TaxEnabled && value.TaxRate > 0m)
            {
                outputTax ??= await EnsureTaxAccountAsync("2130", "ضريبة مخرجات مستحقة", AccountType.Liability, NormalBalance.Credit, ct).ConfigureAwait(false);
                inputTax ??= await EnsureTaxAccountAsync("1150", "ضريبة مدخلات قابلة للاسترداد", AccountType.Asset, NormalBalance.Debit, ct).ConfigureAwait(false);
            }
            var policy = new CompanyPolicy(value.CurrencyCode, company.CurrencyScale, company.RoundingMode,
                company.InventoryValuationMethod, value.AllowNegativeStock,
                value.TaxEnabled ? new TaxPolicy(true, value.TaxRate, company.PricesIncludeTax, outputTax, inputTax) : TaxPolicy.Disabled,
                company.QuantityScale);
            company.UpdateSettings(value.ArabicName, policy);

            var row = await _db.LocalCompanyBrandingSettings.SingleOrDefaultAsync(
                x => x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId, ct).ConfigureAwait(false);
            if (row is null)
            {
                row = new LocalCompanyBrandingSettings { Id = UuidV7.NewGuid(), CompanyId = _user.CompanyId, BranchId = _user.BranchId };
                _db.LocalCompanyBrandingSettings.Add(row);
            }
            row.ArabicName=value.ArabicName.Trim(); row.EnglishName=value.EnglishName.Trim();
            row.BusinessDescription=value.BusinessDescription.Trim(); row.LogoBytes=value.LogoBytes;
            row.LogoFileName=logoFileName; row.LogoMimeType=logoFileName?.EndsWith(".png",StringComparison.OrdinalIgnoreCase)==true?"image/png":"image/jpeg";
            row.City=value.City.Trim(); row.Address=value.Address.Trim(); row.Phones=value.Phones.Trim(); row.Email=value.Email.Trim();
            row.TaxNumber=value.TaxNumber.Trim(); row.CommercialRegistration=value.CommercialRegistration.Trim();
            row.FooterNotes=value.FooterNotes.Trim(); row.CurrencyCode=value.CurrencyCode.Trim().ToUpperInvariant();
            row.CurrencySymbol=string.IsNullOrWhiteSpace(value.CurrencySymbol)?row.CurrencyCode:value.CurrencySymbol.Trim();
            row.TaxEnabled=value.TaxEnabled; row.TaxRate=value.TaxRate; row.AllowNegativeStock=value.AllowNegativeStock;
            row.DarkThemeEnabled=value.DarkThemeEnabled; row.UpdatedAtUtcTicks=DateTimeOffset.UtcNow.UtcDateTime.Ticks;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); _db.ChangeTracker.Clear(); throw; }
    }

    private async Task<Guid> EnsureTaxAccountAsync(string code, string name, AccountType type, NormalBalance normal, CancellationToken ct)
    {
        var existing=await _db.Accounts.SingleOrDefaultAsync(x=>x.CompanyId==_user.CompanyId&&x.BranchId==_user.BranchId&&x.Code==code,ct).ConfigureAwait(false);
        if(existing is not null)return existing.Id;
        var account=new Account(_user.CompanyId,_user.BranchId,code,name,type,normal,true);
        _db.Accounts.Add(account); await _db.SaveChangesAsync(ct).ConfigureAwait(false); return account.Id;
    }
}

public static class CompanySettingsImageHelper
{
    public static ImageSource? ToImage(byte[]? bytes)
    {
        if(bytes is null||bytes.Length==0)return null;
        var b=new BitmapImage(); using var ms=new MemoryStream(bytes);
        b.BeginInit(); b.CacheOption=BitmapCacheOption.OnLoad; b.StreamSource=ms; b.EndInit(); b.Freeze(); return b;
    }
}

public sealed class CompanySettingsViewModel : ObservableObject, IWorkspaceActionTarget
{
    private readonly WindowsAccountingFacade _facade;
    private string _arabicName="",_englishName="",_description="",_city="",_address="",_phones="",_email="",_taxNo="",_cr="",_footer="",_currency="YER",_symbol="YER",_status="";
    private bool _taxEnabled,_negativeStock,_dark; private decimal _taxRate; private byte[]? _logo; private ImageSource? _logoPreview; private string? _logoFileName;

    public CompanySettingsViewModel(WindowsAccountingFacade facade)
    {
        _facade=facade; SaveCommand=new AsyncRelayCommand(SaveAsync); ChooseLogoCommand=new RelayCommand(ChooseLogo); _=RefreshAsync();
    }
    public string ArabicName{get=>_arabicName;set=>Set(ref _arabicName,value);} public string EnglishName{get=>_englishName;set=>Set(ref _englishName,value);}
    public string BusinessDescription{get=>_description;set=>Set(ref _description,value);} public string City{get=>_city;set=>Set(ref _city,value);}
    public string Address{get=>_address;set=>Set(ref _address,value);} public string Phones{get=>_phones;set=>Set(ref _phones,value);}
    public string Email{get=>_email;set=>Set(ref _email,value);} public string TaxNumber{get=>_taxNo;set=>Set(ref _taxNo,value);}
    public string CommercialRegistration{get=>_cr;set=>Set(ref _cr,value);} public string FooterNotes{get=>_footer;set=>Set(ref _footer,value);}
    public string CurrencyCode{get=>_currency;set=>Set(ref _currency,value);} public string CurrencySymbol{get=>_symbol;set=>Set(ref _symbol,value);}
    public bool TaxEnabled{get=>_taxEnabled;set=>Set(ref _taxEnabled,value);} public decimal TaxRatePercent{get=>_taxRate*100m;set{_taxRate=Math.Clamp(value/100m,0m,1m);Raise();}}
    public bool AllowNegativeStock{get=>_negativeStock;set=>Set(ref _negativeStock,value);} public bool DarkThemeEnabled{get=>_dark;set=>Set(ref _dark,value);}
    public ImageSource? LogoPreview{get=>_logoPreview;private set=>Set(ref _logoPreview,value);} public string Status{get=>_status;private set=>Set(ref _status,value);}
    public AsyncRelayCommand SaveCommand{get;} public RelayCommand ChooseLogoCommand{get;}

    private void ChooseLogo()
    {
        var d=new OpenFileDialog{Filter="Image files|*.png;*.jpg;*.jpeg",Title="اختيار شعار الشركة"};
        if(d.ShowDialog()!=true)return;
        _logo=File.ReadAllBytes(d.FileName); _logoFileName=Path.GetFileName(d.FileName); LogoPreview=ToImage(_logo);
    }
    public async Task RefreshAsync()
    {
        var x=await _facade.GetBrandingAsync().ConfigureAwait(true);
        ArabicName=x.ArabicName;EnglishName=x.EnglishName;BusinessDescription=x.BusinessDescription;City=x.City;Address=x.Address;Phones=x.Phones;Email=x.Email;
        TaxNumber=x.TaxNumber;CommercialRegistration=x.CommercialRegistration;FooterNotes=x.FooterNotes;CurrencyCode=x.CurrencyCode;CurrencySymbol=x.CurrencySymbol;
        TaxEnabled=x.TaxEnabled;_taxRate=x.TaxRate;Raise(nameof(TaxRatePercent));AllowNegativeStock=x.AllowNegativeStock;DarkThemeEnabled=x.DarkThemeEnabled;
        _logo=x.LogoBytes;LogoPreview=ToImage(_logo);Status="تم تحميل إعدادات الكليشة.";
    }
    private async Task SaveAsync()
    {
        try{
            await _facade.SaveBrandingAsync(new(ArabicName,EnglishName,BusinessDescription,_logo,City,Address,Phones,Email,TaxNumber,CommercialRegistration,FooterNotes,
                CurrencyCode,CurrencySymbol,TaxEnabled,_taxRate,AllowNegativeStock,DarkThemeEnabled),_logoFileName).ConfigureAwait(true);
            ThemeManager.Apply(DarkThemeEnabled); Status="تم حفظ الإعدادات والكليشة في SQLite.";
        }catch(Exception ex){Status=ex.Message;}
    }
    private static ImageSource? ToImage(byte[]? bytes)
    {
        if(bytes is null||bytes.Length==0)return null;
        var b=new BitmapImage(); using var ms=new MemoryStream(bytes); b.BeginInit();b.CacheOption=BitmapCacheOption.OnLoad;b.StreamSource=ms;b.EndInit();b.Freeze();return b;
    }
    public Task SaveAndPostAsync()=>SaveAsync(); public Task QuickSearchAsync()=>RefreshAsync(); public Task OpenSelectedDetailsAsync()=>RefreshAsync();
}

public static class InvoicePrintService
{
    public static FlowDocument Build(InvoiceEditorViewModel vm, CompanyBrandingSnapshot brand, bool thermal)
    {
        var doc=new FlowDocument{FlowDirection=FlowDirection.RightToLeft,FontFamily=new FontFamily("Segoe UI"),Foreground=Brushes.Black,Background=Brushes.White,
            PagePadding=thermal?new Thickness(14):new Thickness(42),ColumnGap=0};
        if(thermal){doc.PageWidth=302;doc.PageHeight=1120;doc.ColumnWidth=274;}
        else{doc.PageWidth=794;doc.PageHeight=1123;doc.ColumnWidth=710;}

        if(thermal)
        {
            var title=new Paragraph{TextAlignment=TextAlignment.Center,Margin=new Thickness(0,0,0,8)};
            if(brand.LogoBytes is {Length:>0}){var img=new Image{Source=ToImage(brand.LogoBytes),Height=54,Stretch=Stretch.Uniform};title.Inlines.Add(new InlineUIContainer(img));title.Inlines.Add(new LineBreak());}
            title.Inlines.Add(new Bold(new Run(brand.ArabicName)){FontSize=18});
            title.Inlines.Add(new LineBreak()); if(!string.IsNullOrWhiteSpace(brand.BusinessDescription))title.Inlines.Add(new Run(brand.BusinessDescription));
            doc.Blocks.Add(title);
            doc.Blocks.Add(new Paragraph(new Run($"{brand.City} • {brand.Phones}")){TextAlignment=TextAlignment.Center,FontSize=10});
        }
        else
        {
            var header=new Grid{Margin=new Thickness(0,0,0,12)};
            header.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Star});
            header.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(170)});
            header.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var company=new TextBlock{Text=$"{brand.ArabicName}\n{brand.BusinessDescription}",FontSize=15,FontWeight=FontWeights.Bold,TextAlignment=TextAlignment.Right,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};
            Grid.SetColumn(company,0);header.Children.Add(company);
            if(brand.LogoBytes is {Length:>0}){var img=new Image{Source=ToImage(brand.LogoBytes),Height=80,Stretch=Stretch.Uniform};Grid.SetColumn(img,1);header.Children.Add(img);}
            var info=new TextBlock{Text=$"{brand.City}\n{brand.Address}\n{brand.Phones}\n{brand.Email}\nالرقم الضريبي: {brand.TaxNumber}\nالسجل: {brand.CommercialRegistration}",FontSize=10,TextAlignment=TextAlignment.Left,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center,FlowDirection=FlowDirection.RightToLeft};
            Grid.SetColumn(info,2);header.Children.Add(info);doc.Blocks.Add(new BlockUIContainer(header));
        }
        var docTitle=new Paragraph{TextAlignment=TextAlignment.Center,Margin=new Thickness(0,6,0,10)};
        docTitle.Inlines.Add(new Bold(new Run(vm.Title)){FontSize=thermal?16:22});docTitle.Inlines.Add(new LineBreak());
        docTitle.Inlines.Add(new Run($"رقم: {vm.DocumentNumber}   |   التاريخ: {vm.DocumentDate:yyyy-MM-dd HH:mm}"){FontSize=thermal?9:11});
        doc.Blocks.Add(docTitle);
        var table=new Table{CellSpacing=0}; table.Columns.Add(new TableColumn{Width=new GridLength(thermal?130:300)});table.Columns.Add(new TableColumn{Width=new GridLength(thermal?48:90)});table.Columns.Add(new TableColumn{Width=new GridLength(thermal?70:120)});
        var rg=new TableRowGroup();table.RowGroups.Add(rg); var h=new TableRow();h.Cells.Add(Cell("الصنف",true));h.Cells.Add(Cell("الكمية",true));h.Cells.Add(Cell("الإجمالي",true));rg.Rows.Add(h);
        foreach(var l in vm.Lines){var r=new TableRow();r.Cells.Add(Cell(l.Name));r.Cells.Add(Cell(l.Quantity.ToString("N3")));r.Cells.Add(Cell(l.LineTotal.ToString("N2")));rg.Rows.Add(r);} doc.Blocks.Add(table);
        doc.Blocks.Add(new Paragraph(new Bold(new Run($"قبل الضريبة: {vm.Subtotal:N2}\nالضريبة: {vm.Tax:N2}\nالصافي: {vm.GrandTotal:N2} {brand.CurrencySymbol}"))){TextAlignment=TextAlignment.Left});
        if(!thermal)doc.Blocks.Add(new Paragraph(new Run($"الرقم الضريبي: {brand.TaxNumber}     السجل التجاري: {brand.CommercialRegistration}\nتوقيع المحاسب: ______________     توقيع المستلم: ______________")){Margin=new Thickness(0,24,0,10)});
        doc.Blocks.Add(BuildQrBlock($"{brand.ArabicName}|{vm.Title}|{vm.DocumentNumber}|{vm.DocumentDate:O}|{vm.GrandTotal:N2}|{brand.TaxNumber}",thermal));
        if(!string.IsNullOrWhiteSpace(brand.FooterNotes))doc.Blocks.Add(new Paragraph(new Run(brand.FooterNotes)){TextAlignment=TextAlignment.Center,FontSize=10});
        return doc;
    }
    public static FlowDocument BuildVoucher(VoucherEditorViewModel vm, CompanyBrandingSnapshot brand, bool thermal)
    {
        var doc=new FlowDocument{FlowDirection=FlowDirection.RightToLeft,FontFamily=new FontFamily("Segoe UI"),Foreground=Brushes.Black,Background=Brushes.White,
            PagePadding=thermal?new Thickness(14):new Thickness(42),ColumnGap=0};
        if(thermal){doc.PageWidth=302;doc.PageHeight=900;doc.ColumnWidth=274;}
        else{doc.PageWidth=794;doc.PageHeight=1123;doc.ColumnWidth=710;}

        if(thermal)
        {
            var h=new Paragraph{TextAlignment=TextAlignment.Center};
            if(brand.LogoBytes is {Length:>0}){h.Inlines.Add(new InlineUIContainer(new Image{Source=ToImage(brand.LogoBytes),Height=54,Stretch=Stretch.Uniform}));h.Inlines.Add(new LineBreak());}
            h.Inlines.Add(new Bold(new Run(brand.ArabicName)){FontSize=18});h.Inlines.Add(new LineBreak());h.Inlines.Add(new Run($"{brand.City} • {brand.Phones}"){FontSize=10});doc.Blocks.Add(h);
        }
        else
        {
            var header=new Grid{Margin=new Thickness(0,0,0,14)};
            header.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Star});header.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(170)});header.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Star});
            var company=new TextBlock{Text=$"{brand.ArabicName}\n{brand.BusinessDescription}",FontSize=15,FontWeight=FontWeights.Bold,TextAlignment=TextAlignment.Right,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(company,0);header.Children.Add(company);
            if(brand.LogoBytes is {Length:>0}){var img=new Image{Source=ToImage(brand.LogoBytes),Height=80,Stretch=Stretch.Uniform};Grid.SetColumn(img,1);header.Children.Add(img);}
            var info=new TextBlock{Text=$"{brand.City}\n{brand.Address}\n{brand.Phones}\n{brand.Email}\nالرقم الضريبي: {brand.TaxNumber}",FontSize=10,TextAlignment=TextAlignment.Left,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(info,2);header.Children.Add(info);
            doc.Blocks.Add(new BlockUIContainer(header));
        }
        var title=new Paragraph{TextAlignment=TextAlignment.Center,Margin=new Thickness(0,8,0,16)};
        title.Inlines.Add(new Bold(new Run(vm.Title)){FontSize=thermal?18:24});title.Inlines.Add(new LineBreak());
        title.Inlines.Add(new Run($"رقم: {vm.VoucherDocumentNumber}   |   التاريخ: {vm.VoucherDocumentDate:yyyy-MM-dd HH:mm}"){FontSize=thermal?9:11});doc.Blocks.Add(title);
        var party=vm.SelectedVoucherPartner?.Name ?? "-";
        doc.Blocks.Add(new Paragraph(new Run($"الطرف: {party}\nالبيان: {vm.Description}\nالمبلغ: {vm.Amount:N2} {brand.CurrencySymbol}")){FontSize=thermal?12:16,Margin=new Thickness(0,12,0,12)});
        if(!thermal)doc.Blocks.Add(new Paragraph(new Run("توقيع المحاسب: ____________________     توقيع المستلم/المستفيد: ____________________")){Margin=new Thickness(0,28,0,12)});
        doc.Blocks.Add(BuildQrBlock($"{brand.ArabicName}|{vm.Title}|{vm.VoucherDocumentNumber}|{vm.VoucherDocumentDate:O}|{vm.Amount:N2}|{brand.TaxNumber}",thermal));
        if(!string.IsNullOrWhiteSpace(brand.FooterNotes))doc.Blocks.Add(new Paragraph(new Run(brand.FooterNotes)){TextAlignment=TextAlignment.Center,FontSize=10});
        return doc;
    }

    private static TableCell Cell(string s,bool bold=false){var p=new Paragraph(new Run(s)){Margin=new Thickness(3),Padding=new Thickness(2)};if(bold)p.FontWeight=FontWeights.Bold;return new TableCell(p){BorderBrush=Brushes.Gray,BorderThickness=new Thickness(.5)};}
    private static Block BuildQrBlock(string payload,bool thermal)
    {
        var size=thermal?116:140;
        var matrix=new ZXing.MultiFormatWriter().encode(payload,ZXing.BarcodeFormat.QR_CODE,29,29,
            new Dictionary<ZXing.EncodeHintType,object>{{ZXing.EncodeHintType.MARGIN,1},{ZXing.EncodeHintType.CHARACTER_SET,"UTF-8"}});
        var g=new Grid{Width=size,Height=size,Background=Brushes.White};
        var module=Math.Min((double)size/matrix.Width,(double)size/matrix.Height);
        for(int y=0;y<matrix.Height;y++)for(int x=0;x<matrix.Width;x++)
        {
            if(!matrix[x,y])continue;
            g.Children.Add(new Border{Background=Brushes.Black,Width=module+0.2,Height=module+0.2,
                HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,
                Margin=new Thickness(x*module,y*module,0,0)});
        }
        return new BlockUIContainer(g){TextAlignment=TextAlignment.Center};
    }
    private static ImageSource ToImage(byte[] bytes){var b=new BitmapImage();using var ms=new MemoryStream(bytes);b.BeginInit();b.CacheOption=BitmapCacheOption.OnLoad;b.StreamSource=ms;b.EndInit();b.Freeze();return b;}

    public static void Preview(FlowDocument document,string title)
    {
        var copy=((IDocumentPaginatorSource)document).DocumentPaginator;
        var viewer=new DocumentViewer{Document=document};
        var print=new Button{Content="طباعة مباشرة",Margin=new Thickness(4),Padding=new Thickness(16,8,16,8)};
        print.Click+=(_,_)=>{var d=new PrintDialog();if(d.ShowDialog()==true)d.PrintDocument(copy,"Qodynex invoice");};
        var save=new Button{Content="حفظ XPS قابل للطباعة",Margin=new Thickness(4),Padding=new Thickness(16,8,16,8)};
        save.Click+=(_,_)=>{var d=new SaveFileDialog{Filter="XPS Document|*.xps",FileName="Qodynex-Invoice.xps"};if(d.ShowDialog()!=true)return;using var xps=new XpsDocument(d.FileName,FileAccess.ReadWrite);XpsDocument.CreateXpsDocumentWriter(xps).Write(copy);};
        var panel=new DockPanel();var bar=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Left};bar.Children.Add(print);bar.Children.Add(save);DockPanel.SetDock(bar,Dock.Top);panel.Children.Add(bar);panel.Children.Add(viewer);
        new Window{Title=title,Width=1000,Height=800,WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=panel,Owner=System.Windows.Application.Current.MainWindow}.ShowDialog();
    }
}
