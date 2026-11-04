using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;

namespace LumaDock.App.Center;

public sealed partial class ControlCenterWindow
{
    private Grid BuildBrandHeader()
    {
        // Symmetric layout keeps the artwork at the actual window centre,
        // independent of the navigation width and caption-button rail.
        var header = new Grid { Background = Brushes.Transparent, Tag = "BrandHeader" };
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) ToggleMaximize();
            else if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
        var brand = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(130, 2, 130, 0) };
        var logo = new Image { Width = 46, Height = 46, Source = Icon, HorizontalAlignment = HorizontalAlignment.Center, Tag = "BrandLogo" };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        AutomationProperties.SetName(logo, "LumaDock");
        brand.Children.Add(logo);
        var name = Text("LumaDock", 13.5, FontWeights.SemiBold, true); name.Tag = "BrandName";
        name.HorizontalAlignment = HorizontalAlignment.Center; name.Margin = new Thickness(0, 2, 0, 0); brand.Children.Add(name);
        var line = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0), Tag = "BrandLine" };
        var tagline = Text("把整个桌面，变成你的主场。", 22, FontWeights.Medium); tagline.Tag = "BrandTagline";
        tagline.TextAlignment = TextAlignment.Center;
        line.Children.Add(tagline); brand.Children.Add(line); header.Children.Add(brand);

        var chrome = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 13, 16, 0) };
        chrome.Children.Add(ChromeButton("\uE921", "最小化", () => WindowState = WindowState.Minimized));
        var maximize = ChromeButton("\uE922", "最大化", ToggleMaximize); maximize.Tag = "MaximizeButton"; chrome.Children.Add(maximize);
        chrome.Children.Add(ChromeButton("\uE8BB", "关闭", () => Close())); header.Children.Add(chrome);
        StateChanged += (_, _) =>
        {
            var restored = WindowState == WindowState.Maximized;
            maximize.Content = Glyph(restored ? "\uE923" : "\uE922", 12);
            maximize.ToolTip = restored ? "还原窗口" : "最大化";
            AutomationProperties.SetName(maximize, restored ? "还原窗口" : "最大化");
        };
        var horizon = new Border { Height = .65, Margin = new Thickness(24, 0, 24, 0), VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false, Opacity = .6 };
        horizon.SetResourceReference(Border.BackgroundProperty, "GlassHighlightBrush"); header.Children.Add(horizon);
        return header;
    }

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
