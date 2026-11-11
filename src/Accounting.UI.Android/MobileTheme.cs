namespace Accounting.UI.Android;

public static class MobileTheme
{
    public static Color Background { get; private set; } = Color.FromArgb("#F8FAFC");
    public static Color Surface { get; private set; } = Color.FromArgb("#FFFFFF");
    public static Color Card { get; private set; } = Color.FromArgb("#FFFFFF");
    public static Color BorderColor { get; private set; } = Color.FromArgb("#CBD5E1");
    public static Color Gold { get; private set; } = Color.FromArgb("#B7791F");
    public static Color Orange { get; private set; } = Color.FromArgb("#C2410C");
    public static Color Green { get; private set; } = Color.FromArgb("#047857");
    public static Color Red { get; private set; } = Color.FromArgb("#B91C1C");
    public static Color Blue { get; private set; } = Color.FromArgb("#1D4ED8");
    public static Color Text { get; private set; } = Color.FromArgb("#0F172A");
    public static Color Muted { get; private set; } = Color.FromArgb("#475569");

    public static void Apply(bool dark)
    {
        Background=Color.FromArgb(dark?"#0B1120":"#F8FAFC");
        Surface=Color.FromArgb(dark?"#0F172A":"#FFFFFF");
        Card=Color.FromArgb(dark?"#1E293B":"#FFFFFF");
        BorderColor=Color.FromArgb(dark?"#334155":"#CBD5E1");
        Gold=Color.FromArgb(dark?"#D4AF37":"#B7791F");
        Orange=Color.FromArgb(dark?"#EA580C":"#C2410C");
        Green=Color.FromArgb(dark?"#10B981":"#047857");
        Red=Color.FromArgb(dark?"#EF4444":"#B91C1C");
        Blue=Color.FromArgb(dark?"#3B82F6":"#1D4ED8");
        Text=Color.FromArgb(dark?"#F8FAFC":"#0F172A");
        Muted=Color.FromArgb(dark?"#CBD5E1":"#475569");
    }

    public static ResourceDictionary CreateResources()
    {
        var resources = new ResourceDictionary();
        resources.Add("QBackground", Background); resources.Add("QSurface", Surface);
        resources.Add("QCard", Card); resources.Add("QBorder", BorderColor); resources.Add("QGold", Gold);
        resources.Add("QOrange", Orange); resources.Add("QGreen", Green); resources.Add("QRed", Red);
        resources.Add("QBlue", Blue); resources.Add("QText", Text); resources.Add("QMuted", Muted);

        resources.Add(new Style(typeof(ContentPage))
        {
            Setters = { new Setter { Property = VisualElement.BackgroundColorProperty, Value = Background } }
        });
        resources.Add(new Style(typeof(Label))
        {
            Setters = { new Setter { Property = Label.TextColorProperty, Value = Text } }
        });
        resources.Add(new Style(typeof(Button))
        {
            Setters =
            {
                new Setter { Property = Button.BackgroundColorProperty, Value = Card },
                new Setter { Property = Button.TextColorProperty, Value = Text },
                new Setter { Property = Button.BorderColorProperty, Value = BorderColor },
                new Setter { Property = Button.BorderWidthProperty, Value = 1d },
                new Setter { Property = Button.CornerRadiusProperty, Value = 12 },
                new Setter { Property = Button.PaddingProperty, Value = new Thickness(14,10) },
                new Setter { Property = Button.FontAttributesProperty, Value = FontAttributes.Bold }
            }
        });
        resources.Add(new Style(typeof(Entry))
        {
            Setters =
            {
                new Setter { Property = Entry.BackgroundColorProperty, Value = Color.FromArgb("#FFFFFF") },
                new Setter { Property = Entry.TextColorProperty, Value = Text },
                new Setter { Property = Entry.PlaceholderColorProperty, Value = Muted }
            }
        });
        resources.Add(new Style(typeof(Picker))
        {
            Setters =
            {
                new Setter { Property = Picker.BackgroundColorProperty, Value = Color.FromArgb("#111827") },
                new Setter { Property = Picker.TextColorProperty, Value = Text },
                new Setter { Property = Picker.TitleColorProperty, Value = Muted }
            }
        });
        resources.Add(new Style(typeof(Border))
        {
            Setters =
            {
                new Setter { Property = Border.BackgroundColorProperty, Value = Card },
                new Setter { Property = Border.StrokeProperty, Value = BorderColor },
                new Setter { Property = Border.StrokeThicknessProperty, Value = 1d },
                new Setter { Property = Border.PaddingProperty, Value = new Thickness(14) }
            }
        });
        return resources;
    }

    public static Button Primary(string text) => new()
    {
        Text = text, BackgroundColor = Blue, TextColor = Colors.White,
        HeightRequest = 52, CornerRadius = 14, FontAttributes = FontAttributes.Bold
    };

    public static Border CardView(View content) => new()
    {
        BackgroundColor = Card, Stroke = BorderColor, StrokeThickness = 1, Padding = 14,
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 },
        Content = content
    };
}
