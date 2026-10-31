using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using LumaDock.Core;
using LumaDock.Windows;

namespace LumaDock.App;

public sealed class DockWindow : Window
{
    private readonly AppController controller;
    private readonly Border glass = new();
    private readonly GlassRim highlight = new() { Margin = new Thickness(1) };
    private readonly LiquidGlassSurface liquid;
    private readonly StackPanel items = new();
    private readonly ScrollViewer scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false };
    private readonly List<(AppEntry App, Button Button, ScaleTransform Scale, Ellipse Dot)> tiles = [];
    private readonly DispatcherTimer hideDelay = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private bool tucked, dragging, rebuilding;
    private Point dragStart;
    private IntPtr handle;
    private HwndSource? source;
    private int generation;
    private double fullWidth, fullHeight;
    private bool Vertical => controller.State.Settings.Position is "Left" or "Right";

    public DockWindow(AppController controller)
    {
        this.controller = controller;
        liquid = new LiquidGlassSurface(this, controller);
        Title = "LumaDock"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false; Background = Brushes.Transparent; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        WindowBackdrop.Attach(this, () => Theme.IsDark, () => controller.State.Settings.PowerSaver);
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI");
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        glass.BorderThickness = new Thickness(1); glass.Padding = new Thickness(10, 7, 10, 7);
        scroll.Content = items; glass.Child = scroll;
        var surface = new Grid(); surface.Children.Add(liquid); surface.Children.Add(glass); surface.Children.Add(highlight); Content = surface;
        PreviewMouseWheel += (_, e) => { if (Vertical) scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta); else scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta); e.Handled = true; };
        AllowDrop = true;
        DragOver += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent("LumaDock.Pin")) e.Effects = DragDropEffects.Copy; e.Handled = true; Reveal(); };
        Drop += DropFiles;
        MouseMove += Magnify;
        MouseEnter += (_, _) => { hideDelay.Stop(); Reveal(); };
        MouseLeave += (_, _) => { highlight.ReleasePointer(); ResetMagnification(); if (controller.State.Settings.AutoHide) hideDelay.Start(); };
        hideDelay.Tick += HideTick;
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            NativeWindowHelper.ConfigureDock(handle);
            source = HwndSource.FromHwnd(handle); source?.AddHook(WndProc);
        };
        Loaded += (_, _) => { UpdateSettings(); if (controller.State.Settings.AutoHide) hideDelay.Start(); };
        ContentRendered += (_, _) => controller.RecordDockFrame();
        Closed += (_, _) => { highlight.StopMotion(); hideDelay.Stop(); hideDelay.Tick -= HideTick; source?.RemoveHook(WndProc); generation++; };
        ContextMenu = new ContextMenu();
        AddMenu(ContextMenu, "打开应用库", () => controller.ShowCenter());
        AddMenu(ContextMenu, "添加应用…", AddFile);
        AddMenu(ContextMenu, "偏好设置", () => controller.ShowCenter("Settings"));
        AddMenu(ContextMenu, "退出 LumaDock", controller.Exit);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == 0x2E0) Dispatcher.BeginInvoke(() => Reposition());
        return IntPtr.Zero;
    }

    public void UpdateSettings()
    {
        var acrylic = WindowBackdrop.Refresh(this, Theme.IsDark, controller.State.Settings.PowerSaver);
        var baseColor = ((SolidColorBrush)FindResource(acrylic ? "SurfaceBrush" : "OpaqueSurfaceBrush")).Color;
        var opacity = Math.Clamp((controller.State.Settings.Opacity - .55) / .45, 0, 1);
        baseColor.A = acrylic ? (byte)((.06 + .85 * opacity * opacity * opacity) * 255) : (byte)255;
        glass.Background = new SolidColorBrush(baseColor);
        if (controller.State.Settings.LiquidGlass && !controller.State.Settings.PowerSaver) glass.Background = Brushes.Transparent;
        glass.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        glass.Opacity = 1;
        glass.CornerRadius = new CornerRadius(controller.State.Settings.CornerRadius);
        highlight.Radius = Math.Max(0, controller.State.Settings.CornerRadius - 1);
        highlight.AccentColor = Theme.AccentColor;
        highlight.MotionStrength = controller.State.Settings.GlassFlowStrength;
        highlight.MotionEnabled = controller.State.Settings.GlassMotion && controller.State.Settings.LiquidGlass && !controller.ReducedMotion;
        tucked = false; items.Visibility = Visibility.Visible;
        highlight.Visibility = Visibility.Visible;
        glass.Padding = new Thickness(10, 7, 10, 7);
        Rebuild();
        if (controller.State.Settings.AutoHide && !IsMouseOver) hideDelay.Start(); else hideDelay.Stop();
        liquid.Refresh();
    }

    public void Rebuild()
    {
        if (rebuilding) return;
        rebuilding = true;
        try
        {
            var version = ++generation;
            tiles.Clear(); items.Children.Clear(); items.Orientation = Vertical ? Orientation.Vertical : Orientation.Horizontal;
            scroll.HorizontalScrollBarVisibility = Vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
            scroll.VerticalScrollBarVisibility = Vertical ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
            var s = controller.State.Settings;
            var tileSize = s.IconSize + s.Spacing;
            var launcher = new Button { Width = tileSize, Height = tileSize + 8, ToolTip = "应用库 · Ctrl+Alt+Space", Content = LaunchpadMark(s.IconSize * .67), Padding = new Thickness(0) };
            launcher.Click += (_, _) => controller.ShowCenter(); items.Children.Add(launcher);
            items.Children.Add(new Border { Width = Vertical ? tileSize - 18 : 1, Height = Vertical ? 1 : tileSize - 18, Margin = new Thickness(6), Background = (Brush)FindResource("BorderBrush") });
            foreach (var id in controller.State.PinnedIds.ToArray())
            {
                var app = controller.State.Apps.FirstOrDefault(a => a.Id == id && !a.Hidden);
                if (app == null) continue;
                var scale = new ScaleTransform(1, 1);
                var grid = new Grid { Width = tileSize, Height = tileSize + 8 };
                var image = new System.Windows.Controls.Image { Width = s.IconSize, Height = s.IconSize, Stretch = Stretch.Uniform, RenderTransformOrigin = new Point(.5, .65), RenderTransform = scale, IsHitTestVisible = false };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                var dot = new Ellipse { Width = 4, Height = 4, Fill = (Brush)FindResource("TextBrush"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 0), Opacity = 0 };
                grid.Children.Add(image); grid.Children.Add(dot);
                var button = new Button { Content = grid, Padding = new Thickness(0), ToolTip = app.Name, Tag = app, AllowDrop = true };
                button.Click += (_, _) => controller.Launch(app);
                button.PreviewMouseLeftButtonDown += (_, e) => dragStart = e.GetPosition(this);
                button.PreviewMouseMove += (_, e) => BeginReorder(button, app, e);
                button.Drop += (_, e) => Reorder(app, e);
                button.ContextMenu = new ContextMenu();
                AddMenu(button.ContextMenu, "打开 / 切换到应用", () => controller.Launch(app));
                AddMenu(button.ContextMenu, "从 Dock 移除", () => controller.Unpin(app));
                items.Children.Add(button); tiles.Add((app, button, scale, dot));
                _ = LoadIcon(image, app, version);
            }
            RefreshWindows(); Reposition();
        }
        finally { rebuilding = false; }
    }

    private async Task LoadIcon(System.Windows.Controls.Image image, AppEntry app, int version)
    {
        try { var icon = await controller.GetIconAsync(app); if (generation == version) image.Source = icon; }
        catch (Exception ex) { Storage.Log.Write("Icon failed " + app.Name, ex); }
    }

    private FrameworkElement LaunchpadMark(double size)
    {
        var grid = new UniformGrid { Rows = 2, Columns = 2, Width = size, Height = size };
        foreach (var opacity in new[] { 1.0, .72, .72, 1.0 }) grid.Children.Add(new Border { CornerRadius = new CornerRadius(size * .1), Margin = new Thickness(2.5), Background = (Brush)FindResource("AccentBrush"), Opacity = opacity });
        return grid;
    }

    public void RefreshWindows()
    {
        foreach (var tile in tiles) tile.Dot.Opacity = controller.Tracker.IsRunning(tile.App) ? .8 : 0;
        if (controller.Tracker.IsForegroundFullscreenOnMonitor(controller.State.Settings.MonitorDevice))
        {
            if (IsVisible) Hide();
        }
        else if (!IsVisible) { Show(); Reposition(); }
    }

    public void Reposition()
    {
        if (handle == IntPtr.Zero) return;
        var monitors = MonitorService.GetMonitors();
        var monitor = monitors.FirstOrDefault(m => m.DeviceName == controller.State.Settings.MonitorDevice) ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.First();
        var dpi = Native.GetDpiForWindow(handle) / 96.0; if (dpi < .5) dpi = 1;
        var s = controller.State.Settings;
        var count = tiles.Count + 1;
        var length = count * (s.IconSize + s.Spacing + 20) + 40; // includes button template padding
        var thickness = s.IconSize + s.Spacing + 36;
        fullWidth = Vertical ? thickness : Math.Min(length, monitor.WorkWidth / dpi - 24);
        fullHeight = Vertical ? Math.Min(length, monitor.WorkHeight / dpi - 24) : thickness;
        var width = fullWidth; var height = fullHeight;
        if (tucked) { if (Vertical) width = 5; else height = 5; }
        Width = Math.Max(5, width); Height = Math.Max(5, height);
        var w = (int)Math.Ceiling(Width * dpi); var h = (int)Math.Ceiling(Height * dpi); var gap = tucked ? 0 : (int)(8 * dpi);
        var x = monitor.WorkLeft + (monitor.WorkWidth - w) / 2; var y = monitor.WorkTop + monitor.WorkHeight - h - gap;
        if (s.Position == "Top") y = monitor.WorkTop + gap;
        if (s.Position == "Left") { x = monitor.WorkLeft + gap; y = monitor.WorkTop + (monitor.WorkHeight - h) / 2; }
        if (s.Position == "Right") { x = monitor.WorkLeft + monitor.WorkWidth - w - gap; y = monitor.WorkTop + (monitor.WorkHeight - h) / 2; }
        NativeWindowHelper.SetPosition(handle, x, y, w, h);
    }

    private void HideTick(object? sender, EventArgs e)
    {
        hideDelay.Stop();
        if (IsMouseOver || dragging || ContextMenu?.IsOpen == true || !controller.State.Settings.AutoHide) return;
        tucked = true; highlight.StopMotion(); items.Visibility = Visibility.Collapsed; highlight.Visibility = Visibility.Collapsed; glass.Padding = new Thickness(0); Reposition();
    }
    private void Reveal()
    {
        if (!tucked) return;
        tucked = false; items.Visibility = Visibility.Visible; highlight.Visibility = Visibility.Visible; glass.Padding = new Thickness(10, 7, 10, 7); Reposition();
    }

    private void Magnify(object sender, MouseEventArgs e)
    {
        if (!controller.ReducedMotion) highlight.TrackPointer(e.GetPosition(highlight));
        if (controller.ReducedMotion || tucked || dragging) return;
        var p = e.GetPosition(items); var strength = controller.State.Settings.AnimationStrength;
        foreach (var tile in tiles)
        {
            var center = tile.Button.TranslatePoint(new Point(tile.Button.ActualWidth / 2, tile.Button.ActualHeight / 2), items);
            var distance = Vertical ? Math.Abs(p.Y - center.Y) : Math.Abs(p.X - center.X);
            var influence = Math.Max(0, 1 - distance / (controller.State.Settings.IconSize * 1.9));
            Scale(tile.Scale, 1 + influence * influence * strength * .48);
        }
    }
    private void ResetMagnification() { foreach (var tile in tiles) Scale(tile.Scale, 1); }
    private void Scale(ScaleTransform transform, double value)
    {
        if (Math.Abs(transform.ScaleX - value) < .015) return;
        if (controller.ReducedMotion) { transform.BeginAnimation(ScaleTransform.ScaleXProperty, null); transform.BeginAnimation(ScaleTransform.ScaleYProperty, null); transform.ScaleX = transform.ScaleY = value; return; }
        var animation = new DoubleAnimation(value, TimeSpan.FromMilliseconds(115)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation, HandoffBehavior.SnapshotAndReplace); transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void BeginReorder(Button button, AppEntry app, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || dragging) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - dragStart.X) + Math.Abs(p.Y - dragStart.Y) < 10) return;
        dragging = true;
        try { System.Windows.DragDrop.DoDragDrop(button, new System.Windows.DataObject("LumaDock.Pin", app.Id), DragDropEffects.Move); }
        finally { dragging = false; }
    }
    private void Reorder(AppEntry target, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("LumaDock.Pin")) return;
        var id = e.Data.GetData("LumaDock.Pin") as string;
        var pins = controller.State.PinnedIds; var index = pins.IndexOf(target.Id);
        if (id != null && pins.Contains(id) && id != target.Id) { pins.Remove(id); pins.Insert(Math.Min(index, pins.Count), id); controller.Save(); }
        e.Handled = true;
    }
    private void DropFiles(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        foreach (var path in paths)
        {
            try { controller.AddManual(path); var app = AppDiscovery.FromPath(path); var stored = controller.State.Apps.FirstOrDefault(a => a.Id == app.Id); if (stored != null) controller.Pin(stored); }
            catch (Exception ex) { controller.Notify(ex.Message); }
        }
        e.Handled = true;
    }
    private void AddFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "添加应用到 Dock", Filter = "应用与快捷方式|*.exe;*.lnk;*.url;*.appref-ms" };
        if (dialog.ShowDialog() == true) { var path = dialog.FileName; try { controller.AddManual(path); var app = AppDiscovery.FromPath(path); var stored = controller.State.Apps.First(a => a.Id == app.Id); controller.Pin(stored); } catch (Exception ex) { controller.Notify(ex.Message); } }
    }
    private static void AddMenu(ContextMenu menu, string title, Action action) { var item = new MenuItem { Header = title }; item.Click += (_, _) => action(); menu.Items.Add(item); }
    internal void CapturePng(string path)
    {
        if (ActualWidth < 1 || ActualHeight < 1) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
    private static class Native { [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hwnd); }
}
