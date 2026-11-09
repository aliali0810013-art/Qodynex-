namespace Accounting.UI.Android;

public sealed class App : Application
{
    private readonly MobileSessionSettings _settings;
    private readonly MobileRuntime _runtime;

    public App(MobileSessionSettings settings, MobileRuntime runtime)
    {
        _settings = settings;
        _runtime = runtime;
        MainPage = LoadingPage("تهيئة Qodynex...");
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        if (!_settings.HasIdentity)
        {
            await MainThread.InvokeOnMainThreadAsync(() => MainPage = new NavigationPage(new SetupPage(_settings, _runtime)));
            return;
        }

        try
        {
            await _runtime.InitializeAsync().ConfigureAwait(false);
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                MainPage = new NavigationPage(new HomePage(_runtime));
                _runtime.StartSyncLoop();
            });
        }
        catch (Exception ex)
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
                MainPage = new NavigationPage(new SetupPage(_settings, _runtime, ex.Message)));
        }
    }

    private static ContentPage LoadingPage(string text) => new()
    {
        FlowDirection = FlowDirection.RightToLeft,
        Content = new Grid
        {
            Children =
            {
                new VerticalStackLayout
                {
                    Spacing = 18,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    Children =
                    {
                        new ActivityIndicator { IsRunning = true, WidthRequest = 44, HeightRequest = 44 },
                        new Label { Text = text, FontSize = 18, HorizontalTextAlignment = TextAlignment.Center }
                    }
                }
            }
        }
    };
}
