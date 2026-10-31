using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Automation;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using LumaDock.Core;
using LumaDock.Windows;

namespace LumaDock.App;

public sealed class DockWindow : Window
{
    private readonly AppController controller;
    private readonly Grid body = new();
    private readonly Border tint = new() { IsHitTestVisible = false };
    private readonly DockPlate plate = new();
    private readonly DockPlate finish = new() { FinishOnly = true };
    private readonly GlassRim highlight = new() { Margin = new Thickness(1), DrawStaticEdge = false };
    private readonly LiquidGlassSurface liquid;
    private readonly StackPanel items = new();
    private readonly ScrollViewer scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false };
    private sealed class Tile
    {
        public AppEntry? App; public required Button Button; public required FrameworkElement Visual;
        public required ScaleTransform Scale; public required TranslateTransform Move;
        public required Border Halo; public required Ellipse Dot;
        public double TargetScale = 1, TargetAlong, TargetLift, TargetHalo;
    }
    private readonly List<Tile> tiles = [];
    private readonly DispatcherTimer hover = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private long hoverTick;
    private Point lastPointer = new(double.NaN, double.NaN);
    private double bodyThickness, flight, endReserve, radius;
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
        // Only the small Dock is per-pixel transparent: magnified icons can leave
        // the plate without painting an Acrylic rectangle in their flight area.
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI");
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        scroll.Content = items;
        body.Children.Add(liquid); body.Children.Add(tint); body.Children.Add(highlight);
        var surface = new Grid(); surface.Children.Add(plate); surface.Children.Add(body); surface.Children.Add(finish); surface.Children.Add(scroll); Content = surface;
        body.SizeChanged += (_, _) => UpdateContour();
        hover.Tick += HoverTick;
        PreviewMouseWheel += (_, e) => { if (Vertical) scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta); else scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta); e.Handled = true; };
        AllowDrop = true;
        DragOver += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent("LumaDock.Pin")) e.Effects = DragDropEffects.Copy; e.Handled = true; Reveal(); };
        Drop += DropFiles;
        MouseMove += Magnify;
        MouseEnter += (_, _) => { hideDelay.Stop(); Reveal(); };
        MouseLeave += (_, _) => { lastPointer = new(double.NaN, double.NaN); highlight.ReleasePointer(); ResetMagnification(); if (controller.State.Settings.AutoHide) hideDelay.Start(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) StopHover(); };
        hideDelay.Tick += HideTick;
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            NativeWindowHelper.ConfigureDock(handle);
            source = HwndSource.FromHwnd(handle); source?.AddHook(WndProc);
        };
        Loaded += (_, _) => { UpdateSettings(); if (controller.State.Settings.AutoHide) hideDelay.Start(); };
        ContentRendered += (_, _) => controller.RecordDockFrame();
        Closed += (_, _) => { StopHover(); hover.Tick -= HoverTick; highlight.StopMotion(); hideDelay.Stop(); hideDelay.Tick -= HideTick; source?.RemoveHook(WndProc); generation++; };
        ContextMenu = new ContextMenu();
        ContextMenu.Closed += (_, _) => ScheduleHide();
        AddMenu(ContextMenu, "打开应用库", () => controller.ShowCenter());
        AddMenu(ContextMenu, "添加应用…", AddFile);
        AddMenu(ContextMenu, "偏好设置", () => controller.ShowCenter("Settings"));
        AddMenu(ContextMenu, "退出 LumaDock", controller.Exit);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == 0x2E0) Dispatcher.BeginInvoke(() => { Rebuild(); liquid.Refresh(); });
        return IntPtr.Zero;
    }

    public void UpdateSettings()
    {
        var s = controller.State.Settings;
        var optical = s.LiquidGlass && !s.PowerSaver && !SystemParameters.HighContrast;
        var baseColor = ((SolidColorBrush)FindResource("OpaqueSurfaceBrush")).Color;
        var opacity = Math.Clamp((controller.State.Settings.Opacity - .55) / .45, 0, 1);
        // Opaque underlay is always present while the first asynchronous sample
        // is loading. The sampled frame itself supplies translucency and optics.
        baseColor.A = 255;
        if (SystemParameters.HighContrast) baseColor = SystemColors.WindowColor;
        plate.Fill = new SolidColorBrush(baseColor);
        tint.Background = optical ? new SolidColorBrush(Color.FromArgb((byte)(5 + 72 * opacity * opacity * opacity), baseColor.R, baseColor.G, baseColor.B)) : Brushes.Transparent;
        highlight.AccentColor = Theme.AccentColor;
        highlight.MotionStrength = controller.State.Settings.GlassFlowStrength;
        highlight.MotionEnabled = controller.State.Settings.GlassMotion && controller.State.Settings.LiquidGlass && !controller.ReducedMotion;
        tucked = false; scroll.Visibility = Visibility.Visible; body.Visibility = Visibility.Visible; finish.Visibility = Visibility.Visible;
        highlight.Visibility = Visibility.Visible;
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
            StopHover();
            var version = ++generation;
            tiles.Clear(); items.Children.Clear(); items.Orientation = Vertical ? Orientation.Vertical : Orientation.Horizontal;
            scroll.HorizontalScrollBarVisibility = Vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
            scroll.VerticalScrollBarVisibility = Vertical ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
            var s = controller.State.Settings;
            bodyThickness = s.IconSize + (s.DockShape == "Rail" ? 24 : 30);
            flight = Math.Ceiling(s.IconSize * s.AnimationStrength * .76) + 8;
            endReserve = Math.Ceiling(s.IconSize * s.AnimationStrength * .66) + 9;
            radius = s.DockShape switch { "Rail" => Math.Min(17, 5 + s.CornerRadius * .4), "Pebble" => Math.Min(bodyThickness / 2, 17 + s.CornerRadius * .7), _ => bodyThickness / 2 };
            var launcher = AddTile(null, LaunchpadMark(s.IconSize * .69));
            launcher.Button.Click += (_, _) => controller.ShowCenter();
            var resting = (s.Position is "Bottom" or "Right" ? flight : 12) + bodyThickness / 2 - (s.Position is "Bottom" or "Right" ? 2 : -2);
            if (controller.State.PinnedIds.Any(id => controller.State.Apps.Any(a => a.Id == id && !a.Hidden)))
                items.Children.Add(new Border { Width = Vertical ? 25 : 1, Height = Vertical ? 1 : 25, Margin = Vertical ? new Thickness(resting - 12.5, 9, 0, 9) : new Thickness(9, resting - 12.5, 9, 0), VerticalAlignment = Vertical ? VerticalAlignment.Center : VerticalAlignment.Top, HorizontalAlignment = Vertical ? HorizontalAlignment.Left : HorizontalAlignment.Center, Background = (Brush)FindResource("BorderBrush") });
            foreach (var id in controller.State.PinnedIds.ToArray())
            {
                var app = controller.State.Apps.FirstOrDefault(a => a.Id == id && !a.Hidden);
                if (app == null) continue;
                var image = new System.Windows.Controls.Image { Width = s.IconSize, Height = s.IconSize, Stretch = Stretch.Uniform, IsHitTestVisible = false };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                var tile = AddTile(app, image); var button = tile.Button;
                button.Click += (_, _) => controller.Launch(app);
                button.PreviewMouseLeftButtonDown += (_, e) => dragStart = e.GetPosition(this);
                button.PreviewMouseMove += (_, e) => BeginReorder(button, app, e);
                button.Drop += (_, e) => Reorder(app, e);
                button.ContextMenu = new ContextMenu();
                button.ContextMenu.Closed += (_, _) => ScheduleHide();
                AddMenu(button.ContextMenu, "打开 / 切换到应用", () => controller.Launch(app));
                AddMenu(button.ContextMenu, "从 Dock 移除", () => controller.Unpin(app));
                _ = LoadIcon(image, app, version);
            }
            RefreshWindows(); Reposition();
        }
        finally { rebuilding = false; }
    }

    private async Task LoadIcon(System.Windows.Controls.Image image, AppEntry app, int version)
    {
        try
        {
            var s = controller.State.Settings;
            var pixels = (int)Math.Ceiling(s.IconSize * (1 + s.AnimationStrength * .8) * VisualTreeHelper.GetDpi(this).DpiScaleX / 16) * 16;
            var icon = await controller.GetIconAsync(app, Math.Clamp(pixels, 32, 256));
            if (generation == version) image.Source = icon;
        }
        catch (Exception ex) { Storage.Log.Write("Icon failed " + app.Name, ex); }
    }

    private static readonly ControlTemplate TileTemplate = (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
          <Grid ClipToBounds="False" Background="{TemplateBinding Background}">
            <ContentPresenter HorizontalAlignment="Stretch" VerticalAlignment="Stretch"/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsPressed" Value="True"><Setter Property="Opacity" Value="0.72"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);

    private Tile AddTile(AppEntry? app, FrameworkElement icon)
    {
        var s = controller.State.Settings;
        var cell = s.IconSize + s.Spacing + 12;
        var cross = bodyThickness + flight + 12;
        var grid = new Grid { Width = Vertical ? cross : cell, Height = Vertical ? cell : cross, ClipToBounds = false };
        var halo = new Border { CornerRadius = s.DockShape == "Rail" ? new CornerRadius(10) : new CornerRadius(13, 19, 13, 19), Background = (Brush)FindResource("AccentSoftBrush"), BorderBrush = (Brush)FindResource("BorderBrush"), BorderThickness = new Thickness(.7), Opacity = 0 };
        var visual = new Grid { Width = s.IconSize + 10, Height = s.IconSize + 10, RenderTransformOrigin = new Point(.5, .5) };
        visual.Children.Add(halo); visual.Children.Add(icon);
        var scale = new ScaleTransform(1, 1); var move = new TranslateTransform();
        var transforms = new TransformGroup(); transforms.Children.Add(scale); transforms.Children.Add(move); visual.RenderTransform = transforms;
        var insideAtStart = s.Position is "Bottom" or "Right";
        var resting = (insideAtStart ? flight : 12) + bodyThickness / 2 - (insideAtStart ? 2 : -2);
        if (Vertical) { visual.HorizontalAlignment = HorizontalAlignment.Left; visual.Margin = new Thickness(resting - visual.Width / 2, 0, 0, 0); }
        else { visual.VerticalAlignment = VerticalAlignment.Top; visual.Margin = new Thickness(0, resting - visual.Height / 2, 0, 0); }
        var dot = new Ellipse { Width = 4, Height = 4, Fill = (Brush)FindResource("AccentBrush"), Opacity = 0, IsHitTestVisible = false };
        if (Vertical)
        {
            dot.HorizontalAlignment = HorizontalAlignment.Left;
            dot.Margin = new Thickness(s.Position == "Left" ? 19 : cross - 19, 0, 0, 0);
        }
        else
        {
            dot.VerticalAlignment = VerticalAlignment.Top;
            dot.Margin = new Thickness(0, s.Position == "Top" ? 19 : cross - 19, 0, 0);
        }
        grid.Children.Add(visual); grid.Children.Add(dot);
        var name = app?.Name ?? "应用库 · Ctrl+Alt+Space";
        var text = new TextBlock { Text = name };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "AppNameFamily");
        text.SetResourceReference(TextBlock.FontSizeProperty, "AppNameSize");
        text.SetResourceReference(TextBlock.FontWeightProperty, "AppNameWeight");
        text.SetResourceReference(TextBlock.FontStyleProperty, "AppNameStyle");
        var button = new Button { Content = grid, Width = grid.Width, Height = grid.Height, Padding = new Thickness(0), Margin = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, Template = TileTemplate, ToolTip = new ToolTip { Content = text, Placement = s.Position switch { "Top" => PlacementMode.Bottom, "Left" => PlacementMode.Right, "Right" => PlacementMode.Left, _ => PlacementMode.Top } }, Tag = app, AllowDrop = app != null, Cursor = Cursors.Hand };
        AutomationProperties.SetName(button, app?.Name ?? "应用库");
        ToolTipService.SetInitialShowDelay(button, 480); ToolTipService.SetBetweenShowDelay(button, 100);
        var tile = new Tile { App = app, Button = button, Visual = visual, Scale = scale, Move = move, Halo = halo, Dot = dot };
        button.GotKeyboardFocus += (_, _) => FocusTile(tile);
        button.LostKeyboardFocus += (_, _) => { if (!IsMouseOver) ResetMagnification(); };
        items.Children.Add(button); tiles.Add(tile); return tile;
    }

    private FrameworkElement LaunchpadMark(double size)
    {
        var frame = new Border { Width = size + 9, Height = size + 9, CornerRadius = new CornerRadius(13, 19, 13, 19), Background = (Brush)FindResource("AccentSoftBrush"), BorderBrush = (Brush)FindResource("BorderBrush"), BorderThickness = new Thickness(.7), Padding = new Thickness(7) };
        var grid = new UniformGrid { Rows = 2, Columns = 2 };
        foreach (var opacity in new[] { 1.0, .6, .6, 1.0 }) grid.Children.Add(new Border { CornerRadius = new CornerRadius(3), Margin = new Thickness(2), Background = (Brush)FindResource("AccentBrush"), Opacity = opacity });
        frame.Child = grid; return frame;
    }

    public void RefreshWindows()
    {
        foreach (var tile in tiles) tile.Dot.Opacity = tile.App != null && controller.Tracker.IsRunning(tile.App) ? .9 : 0;
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
        var length = tiles.Count * (s.IconSize + s.Spacing + 12) + (tiles.Count > 1 ? 19 : 0) + 28 + endReserve * 2;
        var thickness = bodyThickness + flight + 12;
        fullWidth = Vertical ? thickness : Math.Min(length, monitor.WorkWidth / dpi - 16);
        fullHeight = Vertical ? Math.Min(length, monitor.WorkHeight / dpi - 16) : thickness;
        var width = fullWidth; var height = fullHeight;
        if (tucked) { if (Vertical) width = 5; else height = 5; }
        Width = Math.Max(5, width); Height = Math.Max(5, height);
        var margin = tucked ? new Thickness(0) : s.Position switch
        {
            "Top" => new Thickness(endReserve, 12, endReserve, flight),
            "Left" => new Thickness(12, endReserve, flight, endReserve),
            "Right" => new Thickness(flight, endReserve, 12, endReserve),
            _ => new Thickness(endReserve, flight, endReserve, 12)
        };
        body.Margin = plate.Margin = finish.Margin = margin;
        items.Margin = tucked ? new Thickness(0) : Vertical ? new Thickness(0, endReserve + 14, 0, endReserve + 14) : new Thickness(endReserve + 14, 0, endReserve + 14, 0);
        foreach (var layer in new[] { plate, finish })
        {
            layer.Shape = s.DockShape; layer.Radius = radius; layer.Accent = Theme.AccentColor; layer.Dark = Theme.IsDark; layer.Vertical = Vertical; layer.InvalidateVisual();
        }
        body.Clip = DockPlate.Contour(new Size(Math.Max(0, Width - margin.Left - margin.Right), Math.Max(0, Height - margin.Top - margin.Bottom)), s.DockShape, radius);
        liquid.CornerRadiusOverride = radius;
        highlight.Radius = Math.Max(0, radius - 1);
        UpdateContour();
        var w = (int)Math.Ceiling(Width * dpi); var h = (int)Math.Ceiling(Height * dpi); var gap = tucked ? 0 : (int)(2 * dpi);
        var x = monitor.WorkLeft + (monitor.WorkWidth - w) / 2; var y = monitor.WorkTop + monitor.WorkHeight - h - gap;
        if (s.Position == "Top") y = monitor.WorkTop + gap;
        if (s.Position == "Left") { x = monitor.WorkLeft + gap; y = monitor.WorkTop + (monitor.WorkHeight - h) / 2; }
        if (s.Position == "Right") { x = monitor.WorkLeft + monitor.WorkWidth - w - gap; y = monitor.WorkTop + (monitor.WorkHeight - h) / 2; }
        NativeWindowHelper.SetPosition(handle, x, y, w, h);
    }

    private void UpdateContour()
    {
        var shape = controller.State.Settings.DockShape;
        body.Clip = DockPlate.Contour(body.RenderSize, shape, radius);
        var size = new Size(Math.Max(0, body.ActualWidth - 2), Math.Max(0, body.ActualHeight - 2));
        highlight.SetMotionContours(DockPlate.Contour(size, shape, Math.Max(0, radius - 1), .7), DockPlate.Contour(size, shape, Math.Max(0, radius - 1), 4.5));
    }

    private void HideTick(object? sender, EventArgs e)
    {
        hideDelay.Stop();
        if (IsMouseOver || dragging || ContextMenu?.IsOpen == true || tiles.Any(t => t.Button.ContextMenu?.IsOpen == true) || !controller.State.Settings.AutoHide) return;
        tucked = true; StopHover(); highlight.StopMotion(); scroll.Visibility = Visibility.Collapsed; body.Visibility = Visibility.Collapsed; finish.Visibility = Visibility.Collapsed; Reposition();
    }
    private void ScheduleHide() { if (controller.State.Settings.AutoHide && !IsMouseOver) hideDelay.Start(); }
    private void Reveal()
    {
        if (!tucked) return;
        tucked = false; scroll.Visibility = Visibility.Visible; body.Visibility = Visibility.Visible; finish.Visibility = Visibility.Visible; Reposition(); liquid.Refresh();
    }

    private void Magnify(object sender, MouseEventArgs e)
    {
        if (!controller.ReducedMotion) highlight.TrackPointer(e.GetPosition(highlight));
        if (controller.ReducedMotion || tucked || dragging) return;
        var p = e.GetPosition(items);
        if (double.IsFinite(lastPointer.X) && Math.Abs(p.X - lastPointer.X) < .25 && Math.Abs(p.Y - lastPointer.Y) < .25) return;
        lastPointer = p; SetTargets(p);
    }
    private void SetTargets(Point p)
    {
        var s = controller.State.Settings; var cell = s.IconSize + s.Spacing + 12;
        foreach (var tile in tiles)
        {
            var center = tile.Button.TranslatePoint(new Point(tile.Button.ActualWidth / 2, tile.Button.ActualHeight / 2), items);
            var signed = Vertical ? center.Y - p.Y : center.X - p.X;
            var distance = Math.Min(1, Math.Abs(signed) / (cell * 1.55));
            var influence = (1 + Math.Cos(distance * Math.PI)) / 2;
            tile.TargetScale = 1 + influence * s.AnimationStrength * .8;
            tile.TargetAlong = Math.Tanh(signed / cell * 1.8) * s.IconSize * s.AnimationStrength * .26 * influence;
            tile.TargetLift = (s.IconSize * s.AnimationStrength * .2 + s.IconSize * (tile.TargetScale - 1) * .35) * influence;
            tile.TargetHalo = influence * .76;
            Panel.SetZIndex(tile.Button, (int)(influence * 100));
        }
        StartHover();
    }
    private void FocusTile(Tile tile)
    {
        if (controller.ReducedMotion) { tile.Halo.Opacity = .76; return; }
        SetTargets(tile.Button.TranslatePoint(new Point(tile.Button.ActualWidth / 2, tile.Button.ActualHeight / 2), items));
    }
    private void ResetMagnification()
    {
        foreach (var tile in tiles) { tile.TargetScale = 1; tile.TargetAlong = tile.TargetLift = tile.TargetHalo = 0; }
        if (controller.ReducedMotion) StopHover(); else StartHover();
    }
    private void StartHover()
    {
        if (hover.IsEnabled || !IsVisible) return;
        if (!tiles.Any(t => Math.Abs(t.Scale.ScaleX - t.TargetScale) > .001 || Math.Abs(t.Move.X) + Math.Abs(t.Move.Y) > .01 || Math.Abs(t.Halo.Opacity - t.TargetHalo) > .002)) return;
        hoverTick = Environment.TickCount64; hover.Start();
    }
    private void HoverTick(object? sender, EventArgs e)
    {
        if (controller.ReducedMotion || !IsVisible || tucked || dragging) { StopHover(); return; }
        var now = Environment.TickCount64; var delta = Math.Clamp(now - hoverTick, 1, 64); hoverTick = now;
        var factor = 1 - Math.Exp(-delta / 42d); var moving = false;
        foreach (var tile in tiles)
        {
            var scale = Approach(tile.Scale.ScaleX, tile.TargetScale, factor, .001);
            tile.Scale.ScaleX = tile.Scale.ScaleY = scale;
            var lift = tile.TargetLift * (controller.State.Settings.Position is "Bottom" or "Right" ? -1 : 1);
            var x = Vertical ? lift : tile.TargetAlong; var y = Vertical ? tile.TargetAlong : lift;
            tile.Move.X = Approach(tile.Move.X, x, factor, .015); tile.Move.Y = Approach(tile.Move.Y, y, factor, .015);
            tile.Halo.Opacity = Approach(tile.Halo.Opacity, tile.TargetHalo, factor, .002);
            moving |= scale != tile.TargetScale || tile.Move.X != x || tile.Move.Y != y || tile.Halo.Opacity != tile.TargetHalo;
        }
        if (!moving) hover.Stop();
    }
    private static double Approach(double value, double target, double factor, double threshold) => Math.Abs(target - value) < threshold ? target : value + (target - value) * factor;
    private void StopHover()
    {
        hover.Stop(); lastPointer = new(double.NaN, double.NaN);
        foreach (var tile in tiles) { tile.Scale.ScaleX = tile.Scale.ScaleY = tile.TargetScale = 1; tile.Move.X = tile.Move.Y = tile.TargetAlong = tile.TargetLift = tile.TargetHalo = tile.Halo.Opacity = 0; }
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
