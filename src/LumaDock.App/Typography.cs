using System.Windows;
using System.Windows.Media;
using LumaDock.Core;
using LumaDock.Windows;

namespace LumaDock.App;

/// <summary>Event-driven type resources. Interface labels keep their own sizes so
/// larger desktop icon metrics cannot push navigation out of its column.</summary>
internal static class Typography
{
    private static string customColor = "";

    internal static void Apply(DesktopFontInfo font, DockSettings settings)
    {
        var resources = Application.Current.Resources;
        resources["UiTextFamily"] = new FontFamily(font.FamilyName);
        resources["UiTextWeight"] = FontWeight.FromOpenTypeWeight(font.Weight);
        resources["UiEmphasisWeight"] = FontWeight.FromOpenTypeWeight(Math.Max(600, font.Weight));
        resources["UiTextStyle"] = font.Italic ? FontStyles.Italic : FontStyles.Normal;
        customColor = settings.TextColor;
        RefreshPalette();
    }

    internal static void RefreshPalette()
    {
        var resources = Application.Current.Resources;
        var color = SystemParameters.HighContrast ? SystemColors.WindowTextColor : (Color)ColorConverter.ConvertFromString(
            string.IsNullOrEmpty(customColor) ? Theme.IsDark ? "#F0F5FC" : "#202C3B" : customColor);
        if (resources["TextBrush"] is not SolidColorBrush existing || existing.Color != color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze(); resources["TextBrush"] = brush;
        }
        if (!ReferenceEquals(resources["AppNameBrush"], resources["TextBrush"])) resources["AppNameBrush"] = resources["TextBrush"];
    }
}
