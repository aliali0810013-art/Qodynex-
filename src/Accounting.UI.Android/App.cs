namespace Accounting.UI.Android;

public sealed class App : Microsoft.Maui.Controls.Application
{
    private readonly MobileSessionSettings _settings;
    private readonly MobileRuntime _runtime;

    public App(MobileSessionSettings settings, MobileRuntime runtime)
    {
        _settings = settings;
        _runtime = runtime;
        Resources = MobileTheme.CreateResources();
        _settings.EnsureLocalDemoIdentity();

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        MainPage = LoadingPage("تهيئة Qodynex محلياً...");
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _runtime.InitializeAsync().ConfigureAwait(false);
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                MainPage = new ProductionMobileShell(_runtime);
                _runtime.StartSyncLoop();
            });
        }
        catch (Exception ex)
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
                MainPage = new NavigationPage(ErrorPage(ex)));
        }
    }

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs args)
    {
        var error = args.ExceptionObject as Exception
                    ?? new Exception(args.ExceptionObject?.ToString() ?? "Unknown fatal error.");
        _ = MobileRuntimeSafety.ReportAsync(error, "AppDomain.UnhandledException", notify: false);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        args.SetObserved();
        _ = MobileRuntimeSafety.ReportAsync(args.Exception, "TaskScheduler.UnobservedTaskException", notify: false);
    }

    private ContentPage ErrorPage(Exception error)
    {
        var details = error.InnerException is null
            ? error.Message
            : $"{error.Message}\n\nالسبب: {error.InnerException.Message}";
        var setup = MobileUi.PrimaryButton("فتح إعدادات ربط الخادم");
        var page = new ContentPage
        {
            Title = "خطأ في التشغيل",
            FlowDirection = FlowDirection.RightToLeft
        };
        setup.Clicked += (_, _) => page.Navigation.PushAsync(new SetupPage(_settings, _runtime, details));
        page.Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(20, 36),
                Spacing = 16,
                Children =
                {
                    MobileUi.Heading("تعذر تشغيل Qodynex"),
                    new Label { Text = details, FontSize = 15 },
                    setup
                }
            }
        };
        return page;
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
