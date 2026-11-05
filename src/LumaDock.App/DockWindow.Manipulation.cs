using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LumaDock.Core;
using LumaDock.Windows;

namespace LumaDock.App;

public sealed partial class DockWindow
{
    public const double MinimumIconSize = 16;
    public const double MaximumIconSize = 160;
    private const double GripLane = 32;
    private readonly Border resizeGrip = new() { Width = 24, Height = 28, CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Opacity = .7, Cursor = Cursors.SizeNWSE };
    private sealed record ManipulationOrigin(DockFrame Frame, string Placement, string Monitor, int BoundsVersion,
        double X, double Y, double IconSize, double Dpi, Point Pointer, Point GlassCenter, Size GlassSize);
    private ManipulationOrigin? manipulationOrigin;
    private MonitorInfo? manipulationMonitor;
    private IReadOnlyList<MonitorInfo>? manipulationMonitors;
    private Point lastManipulationPoint;
    private Point? lastAppliedManipulationPoint;
    private uint lastAppliedManipulationDpi;
    private bool resizeDragging, endingManipulation, directUpdateQueued, manipulationCancelRequested;
    private int manipulationEpoch;
    private Cursor? manipulationCursor;
    private IntPtr escapeHook;
    private ManipulationKeys.HookCallback? escapeCallback;
    private bool escapeHookFailureLogged;
    public bool IsSizeDragging => positionDragging && resizeDragging;

    private void InitializeDirectManipulation()
    {
        var mark = new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        mark.SetResourceReference(Border.BackgroundProperty, "CaptionBrush"); resizeGrip.Child = mark;
        resizeGrip.ToolTip = "拖动缩放 Dock 与图标；Esc 取消";
        AutomationProperties.SetName(resizeGrip, "缩放 Dock");
        positionGrip.Cursor = Cursors.SizeAll;
        LostMouseCapture += (_, _) => { if (positionDragging && !endingManipulation && !IsMouseCaptured) EndDirectManipulation(true); };
        PreviewKeyDown += (_, e) => { if (positionDragging && e.Key == Key.Escape) { EndDirectManipulation(true); e.Handled = true; } };
    }

    // The native adapter supplies the point carried by its mouse MSG; the same
    // entry is exercised by queued-input contracts without moving a real cursor.
    private bool ProcessClientPointerMessage(int message, Point messageScreenPoint)
    {
        if (!Finite(messageScreenPoint)) return false;
        if (message is 0x0201 or 0x0203)
        {
            if (positionDragging) return true;
            var point = PointFromScreen(messageScreenPoint); var resize = IsResizeDragPoint(point);
            if (!resize && !IsPositionDragPoint(point)) return false; // App buttons retain WPF/OLE input.
            if (message == 0x0203 && !resize) { RestoreBottomPlacement(); return true; }
            return BeginDirectManipulation(messageScreenPoint, resize);
        }
        if (!positionDragging) return false;
        if (message == 0x0200) { QueueDirectUpdate(messageScreenPoint); return true; }
        if (message == 0x0202)
        {
            // Flush the message endpoint synchronously, even if Windows coalesced
            // all intermediate moves or our render callback has not yet executed.
            UpdateDirectManipulation(messageScreenPoint);
            EndDirectManipulation(manipulationCancelRequested); return true;
        }
        return false;
    }

    private bool IsResizeDragPoint(Point point)
    {
        if (closed || tucked || positionDragging || !IsVisible || resizeGrip.ActualWidth <= 0 || resizeGrip.ActualHeight <= 0) return false;
        var origin = resizeGrip.TranslatePoint(new Point(), this);
        return new Rect(origin, resizeGrip.RenderSize).Contains(point);
    }

    private void ArrangeResizeGrip()
    {
        var cross = Math.Clamp(bodyThickness - 10, 22, 36);
        resizeGrip.Width = Vertical ? cross : 24; resizeGrip.Height = Vertical ? 24 : cross;
        resizeGrip.HorizontalAlignment = Vertical ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        resizeGrip.VerticalAlignment = Vertical ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        resizeGrip.Margin = new Thickness(body.Margin.Left + 4, body.Margin.Top + 4, body.Margin.Right + 4, body.Margin.Bottom + 4);
        resizeGrip.Visibility = tucked ? Visibility.Collapsed : Visibility.Visible;
        if (resizeGrip.Child is Border mark) { mark.Width = Vertical ? Math.Min(16, cross - 8) : 3; mark.Height = Vertical ? 3 : Math.Min(16, cross - 8); }
    }

    /// <summary>Own-window client capture, in physical screen pixels. No native caption loop or Shell snapping.</summary>
    public bool BeginDirectManipulation(Point screen, bool resize)
    {
        if (closed || positionDragging || dragging || tucked || !IsVisible || handle == IntPtr.Zero || !Finite(screen) || WindowFrame() is not { } frame) return false;
        var monitors = MonitorService.GetMonitors();
        if (monitors.Count == 0) return false;
        var nativeDpi = Math.Max(1u, Native.GetDpiForWindow(handle));
        var dpi = nativeDpi / 96d;
        var glass = GlassFrame(frame, dpi); var settings = controller.State.Settings;
        manipulationOrigin = new(frame, settings.DockPlacement, settings.MonitorDevice, settings.DockFreeBoundsVersion,
            settings.DockFreeX, settings.DockFreeY, settings.IconSize, dpi, screen,
            new Point(glass.X + glass.Width / 2d, glass.Y + glass.Height / 2d), new Size(glass.Width, glass.Height));
        // Geometry stays fixed for this gesture; WM_DISPLAYCHANGE cancels the
        // capture before a stale monitor snapshot could be persisted.
        manipulationMonitors = monitors;
        manipulationMonitor = MonitorForPoint(screen, monitors);
        lastAppliedManipulationPoint = screen; lastAppliedManipulationDpi = nativeDpi;
        lastManipulationPoint = screen; resizeDragging = resize; manipulationCancelRequested = false;
        manipulationCursor = Cursor; positionDragging = true; manipulationEpoch++;
        try
        {
            if (!CaptureMouse())
            {
                positionDragging = false; resizeDragging = false; manipulationOrigin = null;
                manipulationMonitor = null; manipulationMonitors = null; lastAppliedManipulationPoint = null; lastAppliedManipulationDpi = 0;
                return false;
            }
            Cursor = resize ? Cursors.SizeNWSE : Cursors.SizeAll;
            hideDelay.Stop(); StopHover(); highlight.StopMotion(); ClearInsertion();
            StartManipulationKeys(); ApplyLayer(); return true;
        }
        catch
        {
            EndDirectManipulation(true); throw;
        }
    }

    private static bool Finite(Point point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
    private static MonitorInfo? MonitorForPoint(Point point, IReadOnlyList<MonitorInfo> monitors)
    {
        MonitorInfo? closest = null; var closestDistance = double.MaxValue;
        for (var index = 0; index < monitors.Count; index++)
        {
            var monitor = monitors[index];
            var dx = point.X - Math.Clamp(point.X, monitor.Left, monitor.Left + monitor.Width);
            var dy = point.Y - Math.Clamp(point.Y, monitor.Top, monitor.Top + monitor.Height);
            var distance = dx * dx + dy * dy;
            if (distance < closestDistance || distance == closestDistance && monitor.IsPrimary && closest?.IsPrimary != true)
            { closest = monitor; closestDistance = distance; }
        }
        return closest;
    }
    private DockFrame GlassFrame(DockFrame frame, double dpi) => new(
        frame.X + (int)Math.Round(body.Margin.Left * dpi), frame.Y + (int)Math.Round(body.Margin.Top * dpi),
        Math.Max(1, frame.Width - (int)Math.Round((body.Margin.Left + body.Margin.Right) * dpi)),
        Math.Max(1, frame.Height - (int)Math.Round((body.Margin.Top + body.Margin.Bottom) * dpi)));

    private void QueueDirectUpdate(Point screen)
    {
        lastManipulationPoint = screen;
        if (!positionDragging || directUpdateQueued) return;
        directUpdateQueued = true; var epoch = manipulationEpoch;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            if (epoch != manipulationEpoch) return;
            directUpdateQueued = false; UpdateDirectManipulation(lastManipulationPoint);
        });
    }

    /// <summary>Updates retained geometry only; persistence and high-resolution icon work wait for release.</summary>
    public void UpdateDirectManipulation(Point screen)
    {
        if (!positionDragging || endingManipulation || manipulationOrigin is not { } origin || !Finite(screen)) return;
        if (manipulationCancelRequested) { EndDirectManipulation(true); return; }
        lastManipulationPoint = screen;
        var nativeDpi = Math.Max(1u, Native.GetDpiForWindow(handle));
        if (lastAppliedManipulationPoint == screen && lastAppliedManipulationDpi == nativeDpi) return;
        lastAppliedManipulationPoint = screen; lastAppliedManipulationDpi = nativeDpi;
        if (!resizeDragging && manipulationMonitors is { } monitors)
            manipulationMonitor = MonitorForPoint(screen, monitors) ?? manipulationMonitor;
        if (manipulationMonitor is not { } monitor) return;
        if (resizeDragging)
        {
            var dx = screen.X - origin.Pointer.X; var dy = screen.Y - origin.Pointer.Y;
            var along = (Vertical ? dy : dx) / Math.Max(1, Vertical ? origin.GlassSize.Height : origin.GlassSize.Width);
            var cross = (Vertical ? dx : dy) / Math.Max(1, Vertical ? origin.GlassSize.Width : origin.GlassSize.Height);
            var fraction = Math.Abs(along) >= Math.Abs(cross) ? along : cross;
            var size = Math.Clamp(Math.Round((origin.IconSize + fraction * (origin.IconSize + 42)) * 4) / 4, MinimumIconSize, MaximumIconSize);
            if (Math.Abs(controller.State.Settings.IconSize - size) < .01) return;
            ApplyRetainedIconSize(size);
            RepositionCore(monitor, origin.GlassCenter);
        }
        else
        {
            var ratio = nativeDpi / 96d / origin.Dpi;
            var proposed = new DockFrame((int)Math.Round(screen.X - (origin.Pointer.X - origin.Frame.X) * ratio),
                (int)Math.Round(screen.Y - (origin.Pointer.Y - origin.Frame.Y) * ratio),
                Math.Max(1, (int)Math.Round(origin.Frame.Width * ratio)), Math.Max(1, (int)Math.Round(origin.Frame.Height * ratio)));
            var frame = DockPlacementGeometry.Clamp(DragBounds(monitor), proposed, PlacementInset());
            if (WindowFrame() is { } current && current == frame) return;
            NativeWindowHelper.SetPosition(handle, frame.X, frame.Y, frame.Width, frame.Height);
            ApplyLayer();
        }
    }

    /// <summary>Only a completed release commits. Escape, loss of capture and hiding restore the starting state.</summary>
    public void EndDirectManipulation(bool cancel = false)
    {
        if (!positionDragging || endingManipulation || manipulationOrigin is not { } origin) return;
        endingManipulation = true; cancel |= manipulationCancelRequested;
        var changed = false; var pending = rebuildAfterDrag;
        try
        {
            StopManipulationKeys();
            var frame = WindowFrame(); var settings = controller.State.Settings;
            changed = Math.Abs(settings.IconSize - origin.IconSize) > .01 || frame is { } moved
                && (Math.Abs(moved.X - origin.Frame.X) > 2 || Math.Abs(moved.Y - origin.Frame.Y) > 2);
            if (cancel)
            {
                settings.DockPlacement = origin.Placement; settings.MonitorDevice = origin.Monitor;
                settings.DockFreeBoundsVersion = origin.BoundsVersion; settings.DockFreeX = origin.X; settings.DockFreeY = origin.Y;
                if (Math.Abs(settings.IconSize - origin.IconSize) > .01) ApplyRetainedIconSize(origin.IconSize);
            }
            else if (changed && frame is { } finalFrame && manipulationMonitor is { } monitor)
            {
                // Save while the interaction guard is still active. Its synchronous
                // rebuild is deferred, so no refresh can overwrite this final frame.
                CommitFreePlacement(finalFrame, monitor);
            }
        }
        finally
        {
            StopManipulationKeys(); positionDragging = false; resizeDragging = false;
            manipulationEpoch++; directUpdateQueued = false; manipulationCancelRequested = false;
            manipulationOrigin = null; manipulationMonitor = null; manipulationMonitors = null;
            lastAppliedManipulationPoint = null; lastAppliedManipulationDpi = 0;
            if (IsMouseCaptured) ReleaseMouseCapture();
            Cursor = manipulationCursor; endingManipulation = false;
        }
        if (closed) return;
        pending |= rebuildAfterDrag; rebuildAfterDrag = false;
        if (!IsVisible) { rebuildAfterDrag = pending; RepositionCore(); return; }
        if (!cancel && changed || pending) Rebuild();
        else
        {
            RepositionCore();
            if (MonitorService.GetMonitors().Any(monitor => monitor.DeviceName == origin.Monitor)
                || string.IsNullOrEmpty(origin.Monitor))
                NativeWindowHelper.SetPosition(handle, origin.Frame.X, origin.Frame.Y, origin.Frame.Width, origin.Frame.Height);
        }
        ApplyLayer(); liquid.Refresh(); ScheduleHide();
    }

    private void UpdateDockMetrics()
    {
        var settings = controller.State.Settings;
        var padding = Math.Min(settings.DockShape == "Rail" ? 36 : 42, 16 + Math.Max(0, settings.IconSize - 16) * 1.1);
        bodyThickness = settings.IconSize + padding;
        flight = Math.Ceiling(settings.IconSize * settings.AnimationStrength * .80) + 8;
        // The old <=80 DIP reserve clipped the halo above ~106 DIP. Bound the
        // actual retained visual and lift rather than assuming a fixed fraction.
        var maximumScale = 1 + settings.AnimationStrength * .9;
        var maximumLift = settings.IconSize * settings.AnimationStrength * .2
            + settings.IconSize * (maximumScale - 1) * .35;
        var visualHalf = (settings.IconSize + 10) * maximumScale / 2;
        flight = Math.Max(flight, Math.Ceiling(visualHalf + maximumLift - bodyThickness / 2 + 5));
        endReserve = Math.Ceiling(settings.IconSize * settings.AnimationStrength * .66) + 9;
        radius = settings.DockShape switch { "Rail" => Math.Min(17, 5 + settings.CornerRadius * .4), "Pebble" => Math.Min(bodyThickness / 2, 17 + settings.CornerRadius * .7), _ => bodyThickness / 2 };
    }

    private void ApplyRetainedIconSize(double size)
    {
        var settings = controller.State.Settings; settings.IconSize = size; UpdateDockMetrics();
        var cell = size + settings.Spacing + 12; var cross = bodyThickness + flight + 12;
        var insideAtStart = settings.Position is "Bottom" or "Right";
        var resting = (insideAtStart ? flight : 12) + bodyThickness / 2 - (insideAtStart ? 2 : -2);
        foreach (var tile in tiles)
        {
            tile.Button.Width = tile.Cell.Width = Vertical ? cross : cell;
            tile.Button.Height = tile.Cell.Height = Vertical ? cell : cross;
            tile.Visual.Width = tile.Visual.Height = size + 10;
            tile.Icon.Width = tile.Icon.Height = tile.App == null ? LauncherIconSize(size) : size;
            tile.Visual.Margin = Vertical ? new Thickness(resting - (size + 10) / 2, 0, 0, 0) : new Thickness(0, resting - (size + 10) / 2, 0, 0);
            tile.Dot.Margin = Vertical ? new Thickness(settings.Position == "Left" ? 15 : cross - 21, 0, 0, 0)
                : new Thickness(0, settings.Position == "Top" ? 15 : cross - 21, 0, 0);
            tile.Reflection.Configure(settings.Position, cell, cross, resting, size, tile.Icon.Width, tile.Icon.Height,
                settings.IconReflection && !settings.PowerSaver && !SystemParameters.HighContrast ? settings.ReflectionStrength : 0);
        }
        foreach (var separator in items.Children.OfType<Border>())
        {
            separator.Width = Vertical ? 25 : 1; separator.Height = Vertical ? 1 : 25;
            separator.Margin = Vertical ? new Thickness(resting - 12.5, 9, 0, 9) : new Thickness(9, resting - 12.5, 9, 0);
        }
    }

    private void StartManipulationKeys()
    {
        escapeCallback = (code, message, data) =>
        {
            if (code >= 0 && positionDragging && (message.ToInt32() is 0x0100 or 0x0104) && Marshal.ReadInt32(data) == 0x1B)
            {
                manipulationCancelRequested = true;
                Dispatcher.BeginInvoke(() => EndDirectManipulation(true)); return new IntPtr(1);
            }
            return ManipulationKeys.CallNextHookEx(IntPtr.Zero, code, message, data);
        };
        escapeHook = ManipulationKeys.SetWindowsHookEx(13, escapeCallback, ManipulationKeys.GetModuleHandle(null), 0);
        if (escapeHook != IntPtr.Zero || escapeHookFailureLogged) return;
        escapeHookFailureLogged = true;
        Storage.Log.Write("Dock Escape cancellation hook is unavailable; pointer manipulation remains usable.");
    }

    private void StopManipulationKeys()
    {
        var callback = escapeCallback; var hook = escapeHook; escapeHook = IntPtr.Zero;
        try { if (hook != IntPtr.Zero) ManipulationKeys.UnhookWindowsHookEx(hook); }
        finally { escapeCallback = null; GC.KeepAlive(callback); }
    }

    private static class ManipulationKeys
    {
        internal delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int id, HookCallback callback, IntPtr module, uint threadId);
        [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string? name);
    }
}
