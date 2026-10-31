using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LumaDock.App;

internal static class Theme
{
    internal static bool IsDark { get; private set; }
    internal static Color AccentColor { get; private set; } = Color.FromRgb(161, 211, 245);
    internal static bool ResolveDark(string mode)
    {
        if (mode == "Light") return false;
        if (mode != "System") return true;
        return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0;
    }
    internal static void Apply(string mode, string accent = "Lagoon")
    {
        IsDark = ResolveDark(mode);
        var light = !IsDark;
        var r = Application.Current.Resources;
        void Brush(string key, string value) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); brush.Freeze(); r[key] = brush; }
        Brush("SurfaceBrush", light ? "#88F5F9FF" : "#5C182536");
        Brush("PanelBrush", light ? "#64FFFFFF" : "#1AFFFFFF");
        Brush("SidebarBrush", light ? "#35FFFFFF" : "#16080F1C");
        Brush("TextBrush", light ? "#202C3B" : "#F0F5FC");
        Brush("MutedBrush", light ? "#526274" : "#B6C4D4");
        Brush("CaptionBrush", light ? "#64748A" : "#A7B8CB");
        Brush("CardBrush", light ? "#36FFFFFF" : "#10071221");
        Brush("ToggleTrackBrush", light ? "#665B7482" : "#47FFFFFF");
        Brush("BorderBrush", light ? "#A6FFFFFF" : "#38FFFFFF");
        var accentHex = (accent, light) switch
        {
            ("Jade", true) => "#28785F", ("Jade", false) => "#9CDBBE",
            ("Amber", true) => "#9B641F", ("Amber", false) => "#F2C887",
            ("Iris", true) => "#7757AB", ("Iris", false) => "#CEB8EF",
            ("Rose", true) => "#A24F68", ("Rose", false) => "#F3BDC8",
            ("Graphite", true) => "#4E6071", ("Graphite", false) => "#D2DCE3",
            (_, true) => "#3579B3", _ => "#A1D3F5"
        };
        AccentColor = (Color)ColorConverter.ConvertFromString(accentHex);
        Brush("AccentBrush", accentHex);
        var accentSoft = new SolidColorBrush(Color.FromArgb(light ? (byte)34 : (byte)42, AccentColor.R, AccentColor.G, AccentColor.B)); accentSoft.Freeze(); r["AccentSoftBrush"] = accentSoft;
        Brush("GlassHighlightBrush", light ? "#BAFFFFFF" : "#60FFFFFF");
        r["SelectionBrush"] = accentSoft;
        Brush("PopupBrush", light ? "#F2F8FBFF" : "#F2242F3C");
        Brush("OpaqueSurfaceBrush", light ? "#F1F5F9" : "#202A37");
        if (r.Contains(typeof(Button))) return;
        var button = new Style(typeof(Button));
        button.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextBrush")));
        button.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        button.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        button.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 8, 12, 8)));
        button.Setters.Add(new Setter(Control.CursorProperty, System.Windows.Input.Cursors.Hand));
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Face";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 7, 10, 7));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content); template.VisualTree = border;
        template.Triggers.Add(new Trigger { Property = UIElement.IsMouseOverProperty, Value = true, Setters = { new Setter(Border.BackgroundProperty, new DynamicResourceExtension("PanelBrush"), "Face") } });
        button.Setters.Add(new Setter(Control.TemplateProperty, template)); r[typeof(Button)] = button;
        var tooltip = new Style(typeof(ToolTip));
        tooltip.Setters.Add(new Setter(Control.FontFamilyProperty, new DynamicResourceExtension("AppNameFamily")));
        tooltip.Setters.Add(new Setter(Control.FontSizeProperty, new DynamicResourceExtension("AppNameSize")));
        tooltip.Setters.Add(new Setter(Control.FontWeightProperty, new DynamicResourceExtension("AppNameWeight")));
        tooltip.Setters.Add(new Setter(Control.FontStyleProperty, new DynamicResourceExtension("AppNameStyle")));
        tooltip.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextBrush")));
        tooltip.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("PopupBrush")));
        tooltip.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("GlassHighlightBrush")));
        tooltip.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(11, 7, 11, 7)));
        tooltip.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ToolTip">
              <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="0.8" CornerRadius="10" Padding="{TemplateBinding Padding}">
                <ContentPresenter/>
              </Border>
            </ControlTemplate>
            """))); r[typeof(ToolTip)] = tooltip;
        var menu = new Style(typeof(ContextMenu));
        menu.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("PopupBrush")));
        menu.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextBrush")));
        menu.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("BorderBrush")));
        menu.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        menu.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(5)));
        menu.Setters.Add(new Setter(Control.FontSizeProperty, 12d)); r[typeof(ContextMenu)] = menu;
    }
}
