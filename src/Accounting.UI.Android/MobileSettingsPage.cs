namespace Accounting.UI.Android;

public sealed class MobileSettingsPage : ContentPage
{
    private readonly MobileRuntime _runtime;
    private readonly Entry _arabic = new(){Placeholder="اسم الشركة بالعربية"};
    private readonly Entry _english = new(){Placeholder="Company name"};
    private readonly Entry _city = new(){Placeholder="المدينة"};
    private readonly Entry _address = new(){Placeholder="العنوان"};
    private readonly Entry _phones = new(){Placeholder="الهاتف / واتساب"};
    private readonly Entry _email = new(){Placeholder="البريد الإلكتروني"};
    private readonly Entry _taxNo = new(){Placeholder="الرقم الضريبي"};
    private readonly Entry _cr = new(){Placeholder="السجل التجاري"};
    private readonly Entry _footer = new(){Placeholder="تذييل الفاتورة"};
    private readonly Entry _currency = new(){Placeholder="العملة"};
    private readonly Entry _symbol = new(){Placeholder="رمز العملة"};
    private readonly Entry _taxRate = new(){Placeholder="نسبة الضريبة %",Keyboard=Keyboard.Numeric};
    private readonly Switch _taxEnabled = new();
    private readonly Switch _negative = new();
    private readonly Switch _dark = new();
    private readonly Image _logo = new(){HeightRequest=110,Aspect=Aspect.AspectFit};
    private readonly Label _status = new(){TextColor=MobileTheme.Muted};
    private byte[]? _logoBytes;

    public MobileSettingsPage(MobileRuntime runtime)
    {
        _runtime=runtime; Title="الإعدادات";FlowDirection=FlowDirection.RightToLeft;BackgroundColor=MobileTheme.Background;
        var choose=MobileTheme.Primary("اختيار شعار");
        choose.Clicked+=ChooseLogoAsync;
        var save=MobileTheme.Primary("حفظ الإعدادات");
        save.Clicked+=SaveAsync;
        Content=new ScrollView{Content=new VerticalStackLayout{Padding=16,Spacing=10,Children={
            new Label{Text="الإعدادات والكليشة",FontSize=26,FontAttributes=FontAttributes.Bold},
            _logo,choose,_arabic,_english,_city,_address,_phones,_email,_taxNo,_cr,_footer,_currency,_symbol,
            new HorizontalStackLayout{Spacing=10,Children={new Label{Text="تفعيل الضريبة",VerticalTextAlignment=TextAlignment.Center},_taxEnabled,_taxRate}},
            new HorizontalStackLayout{Spacing=10,Children={new Label{Text="السماح بالمخزون السالب",VerticalTextAlignment=TextAlignment.Center},_negative}},
            new HorizontalStackLayout{Spacing=10,Children={new Label{Text="الوضع الليلي",VerticalTextAlignment=TextAlignment.Center},_dark}},
            save,_status
        }}};
        Appearing+=async(_,_)=>await LoadAsync().ConfigureAwait(true);
    }

    private async Task LoadAsync()
    {
        try
        {
            var x=await _runtime.Facade.GetBrandingAsync().ConfigureAwait(true);
            _arabic.Text=x.ArabicName;_english.Text=x.EnglishName;_city.Text=x.City;_address.Text=x.Address;_phones.Text=x.Phones;_email.Text=x.Email;
            _taxNo.Text=x.TaxNumber;_cr.Text=x.CommercialRegistration;_footer.Text=x.FooterNotes;_currency.Text=x.CurrencyCode;_symbol.Text=x.CurrencySymbol;
            _taxEnabled.IsToggled=x.TaxEnabled;_taxRate.Text=(x.TaxRate*100m).ToString("0.##");_negative.IsToggled=x.AllowNegativeStock;_dark.IsToggled=x.DarkThemeEnabled;_logoBytes=x.LogoBytes;
            if(_logoBytes is {Length:>0})_logo.Source=ImageSource.FromStream(()=>new MemoryStream(_logoBytes));
        }
        catch(Exception ex){_status.Text=ex.Message;}
    }

    private async void ChooseLogoAsync(object? sender,EventArgs e)
    {
        try
        {
            var result=await FilePicker.Default.PickAsync(new PickOptions{PickerTitle="اختيار شعار الشركة",FileTypes=FilePickerFileType.Images});
            if(result is null)return;await using var stream=await result.OpenReadAsync();using var ms=new MemoryStream();await stream.CopyToAsync(ms);_logoBytes=ms.ToArray();
            _logo.Source=ImageSource.FromStream(()=>new MemoryStream(_logoBytes));
        }
        catch(Exception ex){_status.Text=ex.Message;}
    }

    private async void SaveAsync(object? sender,EventArgs e)
    {
        try
        {
            var old=await _runtime.Facade.GetBrandingAsync().ConfigureAwait(true);
            decimal.TryParse(_taxRate.Text,out var pct);
            var updated=old with{ArabicName=_arabic.Text??"",EnglishName=_english.Text??"",City=_city.Text??"",Address=_address.Text??"",Phones=_phones.Text??"",Email=_email.Text??"",
                TaxNumber=_taxNo.Text??"",CommercialRegistration=_cr.Text??"",FooterNotes=_footer.Text??"",CurrencyCode=string.IsNullOrWhiteSpace(_currency.Text)?"YER":_currency.Text!,
                CurrencySymbol=string.IsNullOrWhiteSpace(_symbol.Text)?"YER":_symbol.Text!,TaxEnabled=_taxEnabled.IsToggled,TaxRate=Math.Clamp(pct/100m,0m,1m),
                AllowNegativeStock=_negative.IsToggled,DarkThemeEnabled=_dark.IsToggled,LogoBytes=_logoBytes};
            await _runtime.Facade.SaveBrandingAsync(updated).ConfigureAwait(true);
            MobileTheme.Apply(updated.DarkThemeEnabled);
            _status.Text="تم الحفظ. سيعاد بناء الواجهة لتطبيق المظهر.";
            Microsoft.Maui.Controls.Application.Current!.Resources=MobileTheme.CreateResources();
            Microsoft.Maui.Controls.Application.Current.MainPage=new ProductionMobileShell(_runtime);
        }
        catch(Exception ex){_status.Text=ex.Message;}
    }
}
