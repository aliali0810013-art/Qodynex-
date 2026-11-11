using System.Windows;
using System.Windows.Media;

namespace Accounting.UI.Windows;

public static class ThemeManager
{
    public static bool IsDark { get; private set; }

    private static readonly IReadOnlyDictionary<string, string> Light = new Dictionary<string, string>
    {
        ["Bg0Brush"]="#F8FAFC", ["Bg1Brush"]="#FFFFFF", ["CardBrush"]="#FFFFFF",
        ["BorderBrushQ"]="#CBD5E1", ["PrimaryBrush"]="#1D4ED8", ["GoldBrush"]="#B7791F",
        ["OrangeBrush"]="#C2410C", ["GreenBrush"]="#047857", ["RedBrush"]="#B91C1C",
        ["BlueBrush"]="#1D4ED8", ["TextBrush"]="#0F172A", ["MutedBrush"]="#475569",
        ["InputBrush"]="#FFFFFF", ["HeaderBrush"]="#FFFFFF", ["SidebarBrush"]="#F1F5F9",
        ["SelectionBrush"]="#DBEAFE", ["SelectionTextBrush"]="#0F172A"
    };

    private static readonly IReadOnlyDictionary<string, string> Dark = new Dictionary<string, string>
    {
        ["Bg0Brush"]="#0B1120", ["Bg1Brush"]="#0F172A", ["CardBrush"]="#1E293B",
        ["BorderBrushQ"]="#334155", ["PrimaryBrush"]="#2563EB", ["GoldBrush"]="#D4AF37",
        ["OrangeBrush"]="#EA580C", ["GreenBrush"]="#10B981", ["RedBrush"]="#EF4444",
        ["BlueBrush"]="#3B82F6", ["TextBrush"]="#F8FAFC", ["MutedBrush"]="#CBD5E1",
        ["InputBrush"]="#111827", ["HeaderBrush"]="#080D18", ["SidebarBrush"]="#0F172A",
        ["SelectionBrush"]="#1E3A8A", ["SelectionTextBrush"]="#FFFFFF"
    };

    public static void Apply(bool dark)
    {
        IsDark = dark;
        var palette = dark ? Dark : Light;
        foreach (var pair in palette)
        {
            if (Application.Current.TryFindResource(pair.Key) is SolidColorBrush brush)
                brush.Color = (Color)ColorConverter.ConvertFromString(pair.Value);
        }
    }

    public static bool PaletteHasReadableContrast(bool dark)
    {
        var p = dark ? Dark : Light;
        return Contrast(p["TextBrush"], p["InputBrush"]) >= 7d
            && Contrast(p["TextBrush"], p["CardBrush"]) >= 7d
            && Contrast(p["SelectionTextBrush"], p["SelectionBrush"]) >= 4.5d;
    }

    private static double Contrast(string a, string b)
    {
        static double L(string hex)
        {
            var c=(Color)ColorConverter.ConvertFromString(hex);
            static double F(byte v){var s=v/255d;return s<=0.03928?s/12.92:Math.Pow((s+0.055)/1.055,2.4);}
            return 0.2126*F(c.R)+0.7152*F(c.G)+0.0722*F(c.B);
        }
        var l1=L(a);var l2=L(b);if(l1<l2)(l1,l2)=(l2,l1);return (l1+0.05)/(l2+0.05);
    }

    public static bool IsLightBackground(string resourceKey)
    {
        if (Application.Current.TryFindResource(resourceKey) is not SolidColorBrush brush) return false;
        var c = brush.Color;
        var luminance = (0.2126*c.R + 0.7152*c.G + 0.0722*c.B)/255d;
        return luminance > 0.72;
    }
}
