using ZXing.Net.Maui.Controls;

namespace Accounting.UI.Android;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseBarcodeReader();

        builder.Services.AddSingleton<MobileSessionSettings>();
        builder.Services.AddSingleton<MobileRuntime>();
        return builder.Build();
    }
}
