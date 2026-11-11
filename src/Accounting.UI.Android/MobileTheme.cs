namespace Accounting.UI.Android;

public static class MobileTheme
{
    public static readonly Color Background = Color.FromArgb("#0B1120");
    public static readonly Color Surface = Color.FromArgb("#0F172A");
    public static readonly Color Card = Color.FromArgb("#1E293B");
    public static readonly Color BorderColor = Color.FromArgb("#334155");
    public static readonly Color Gold = Color.FromArgb("#D4AF37");
    public static readonly Color Orange = Color.FromArgb("#EA580C");
    public static readonly Color Green = Color.FromArgb("#10B981");
    public static readonly Color Red = Color.FromArgb("#EF4444");
    public static readonly Color Blue = Color.FromArgb("#3B82F6");
    public static readonly Color Text = Color.FromArgb("#F8FAFC");
    public static readonly Color Muted = Color.FromArgb("#94A3B8");

    public static ResourceDictionary CreateResources()
    {
        var resources = new ResourceDictionary();
        resources.Add("QBackground", Background); resources.Add("QSurface", Surface);
        resources.Add("QCard", Card); resources.Add("QBorder", Border); resources.Add("QGold", Gold);
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
                new Setter { Property = Entry.BackgroundColorProperty, Value = Color.FromArgb("#111827") },
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
        Text = text, BackgroundColor = Gold, TextColor = Color.FromArgb("#0B1120"),
        HeightRequest = 52, CornerRadius = 14, FontAttributes = FontAttributes.Bold
    };

    public static Border CardView(View content) => new()
    {
        BackgroundColor = Card, Stroke = BorderColor, StrokeThickness = 1, Padding = 14,
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 },
        Content = content
    };
}
