using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using LumaDock.App;
using LumaDock.Core;
using LumaDock.Storage;
using LumaDock.Windows;

namespace LumaDock.Workspace.Ui.Tests;

internal static partial class Program
{
    // Calls the exact screen-coordinate entry points used by the client mouse
    // handlers. Capture and HWND bounds are real; input is not physical injection.
    private static async Task DirectManipulationChecks(AppController controller, DockWindow dock, string data,
        MonitorInfo monitor, Action<string, Action> check, Dictionary<string, object> observations)
    {
        var handle = new WindowInteropHelper(dock).Handle;
        var bounds = new DockWorkArea(monitor.Left, monitor.Top, monitor.Width, monitor.Height);
        var configPath = Path.Combine(data, "config.json");
        controller.State.Settings.AutoHide = false; controller.State.Settings.IconSize = 48;
        dock.UpdateSettings(); controller.Save(); await Frame();
        var commits = 0; EventHandler observeSave = (_, _) => commits++;
        controller.Changed += observeSave;
        try
        {
            var before = NativeFrame(handle); var start = GripPoint(dock, "positionGrip");
            var bytes = File.ReadAllBytes(configPath); var written = File.GetLastWriteTimeUtc(configPath);
            var tiles = DockTileButtons(dock); var images = DockIconImages(dock); var generation = Field<int>(dock, "generation");
            check("The production client move entry acquires real capture without entering a native move loop", () =>
            {
                Require(dock.BeginDirectManipulation(start, false), "The shown Dock could not begin its client drag.");
                Require(dock.IsPositionDragging && !dock.IsSizeDragging && ReferenceEquals(Mouse.Captured, dock) && GetCapture() == handle,
                    "The direct move did not own both WPF and native mouse capture.");
                Require(!Field<DispatcherTimer>(dock, "hover").IsEnabled && !Field<DispatcherTimer>(dock, "hideDelay").IsEnabled,
                    "Direct manipulation left hover or auto-hide work active.");
            });
            if (!dock.IsPositionDragging) return;
            dock.UpdateDirectManipulation(new Point(start.X + 90, start.Y - 75)); dock.UpdateLayout();
            check("Client pointer movement follows physical deltas without persisting every update", () =>
            {
                var current = NativeFrame(handle);
                Require(Math.Abs(current.X - before.X - 90) <= 2 && Math.Abs(current.Y - before.Y + 75) <= 2,
                    "The client pointer delta was replaced by a work-area snap or a relative-coordinate feedback loop.");
                Require(commits == 0 && bytes.SequenceEqual(File.ReadAllBytes(configPath)) && File.GetLastWriteTimeUtc(configPath) == written,
                    "The pointer-move preview wrote the configuration before release.");
                Require(generation == Field<int>(dock, "generation") && SameObjects(tiles, DockTileButtons(dock)) && SameObjects(images, DockIconImages(dock)),
                    "Moving the Dock recreated its application buttons or images.");
            });
            var moved = NativeFrame(handle);
            dock.Rebuild(); dock.Reposition(); dock.RefreshWindows(); dock.UpdateLayout();
            check("Refresh and relayout requests cannot override an in-progress client drag", () =>
            {
                Require(dock.IsPositionDragging && SameFrame(moved, NativeFrame(handle)) && generation == Field<int>(dock, "generation"),
                    "A refresh reset the live gesture to its saved anchor.");
                Require(commits == 0, "A deferred refresh saved the transient pointer position.");
            });
            dock.EndDirectManipulation(); await Frame();
            check("Releasing the client move commits once and releases capture at the actual drop", () =>
            {
                Require(!dock.IsPositionDragging && !ReferenceEquals(Mouse.Captured, dock) && GetCapture() != handle && commits == 1,
                    "The client release leaked capture or saved more than once.");
                Require(SameFrame(moved, NativeFrame(handle)), "Releasing the client drag bounced back to another position.");
                var saved = new StateStore(data).Load().Settings;
                Require(saved.DockPlacement == "Free" && saved.DockFreeBoundsVersion == 1, "The pointer release did not persist free positioning.");
            });
            dock.Rebuild(); dock.Reposition(); await Frame();
            check("The released move remains at its drop after another queued refresh", () =>
                Require(SameFrame(moved, NativeFrame(handle)), "A post-release refresh reverted the actual client drop."));

            // Drive the real client-message adapter continuously, before the
            // dispatcher can execute its coalesced move. The system cursor is
            // deliberately untouched: queued message points own the trajectory.
            before = NativeFrame(handle); start = GripPoint(dock, "positionGrip"); commits = 0;
            Require(GetCursorPos(out var cursorBefore), "Cannot read the stationary cursor used by the message contract.");
            var down = ClientPointer(dock, 0x0201, start);
            var move = ClientPointer(dock, 0x0200, new Point(start.X - 40, start.Y - 30));
            var up = ClientPointer(dock, 0x0202, new Point(start.X - 90, start.Y - 70));
            Require(GetCursorPos(out var cursorAfter), "Cannot re-read the cursor after the message contract.");
            await Frame();
            check("Queued client down move and up use their own screen points and flush the final point", () =>
            {
                var actual = NativeFrame(handle);
                Require(down && move && up && !dock.IsPositionDragging && commits == 1,
                    "The client-message adapter lost a pointer phase or saved more than once.");
                Require(cursorBefore.X == cursorAfter.X && cursorBefore.Y == cursorAfter.Y,
                    "The independent cursor moved during the queued message contract; rerun without pointer input.");
                Require(Math.Abs(actual.X - before.X + 90) <= 2 && Math.Abs(actual.Y - before.Y + 70) <= 2,
                    "Queued input used the current cursor or a stale coalesced move instead of the final mouse-up point.");
                observations["queuedClientMessageDrop"] = new { before, actual, commits, events = new[] { 0x0201, 0x0200, 0x0202 },
                    stationaryCursor = new { cursorBefore.X, cursorBefore.Y } };
            });
            before = NativeFrame(handle); start = GripPoint(dock, "positionGrip"); commits = 0;
            Require(GetCursorPos(out cursorBefore), "Cannot read the cursor before the fast message contract.");
            down = ClientPointer(dock, 0x0201, start);
            up = ClientPointer(dock, 0x0202, new Point(start.X + 65, start.Y + 40));
            Require(GetCursorPos(out cursorAfter), "Cannot read the cursor after the fast message contract.");
            await Frame();
            check("A fast client down and up without a move message still commits its final drop once", () =>
            {
                var actual = NativeFrame(handle);
                Require(down && up && !dock.IsPositionDragging && commits == 1,
                    "A fast pointer release was treated as an unchanged click or saved more than once.");
                Require(cursorBefore.X == cursorAfter.X && cursorBefore.Y == cursorAfter.Y,
                    "The independent cursor moved during the fast message contract; rerun without pointer input.");
                Require(Math.Abs(actual.X - before.X - 65) <= 2 && Math.Abs(actual.Y - before.Y - 40) <= 2,
                    "Without WM_MOUSEMOVE, the client mouse-up point did not move the Dock to the requested final drop.");
                observations["fastClientMessageDrop"] = new { before, actual, commits, events = new[] { 0x0201, 0x0202 },
                    stationaryCursor = new { cursorBefore.X, cursorBefore.Y } };
            });

            foreach (var edge in new[] { "Top", "Bottom" })
            {
                var glass = DockGlassBounds(dock); var inset = Math.Max(2, (int)Math.Ceiling(4 * Dpi(handle)));
                var target = new Point(bounds.Left + (bounds.Width - glass.Width) / 2,
                    edge == "Top" ? bounds.Top + inset : bounds.Top + bounds.Height - glass.Height - inset);
                start = GripPoint(dock, "positionGrip"); commits = 0;
                Require(dock.BeginDirectManipulation(start, false), "Could not begin the owned edge drag.");
                dock.UpdateDirectManipulation(new Point(start.X + target.X - glass.X, start.Y + target.Y - glass.Y));
                dock.EndDirectManipulation(); await Frame();
                var atDrop = NativeFrame(handle); dock.Reposition(); dock.Rebuild(); await Frame();
                check($"The {edge.ToLowerInvariant()} screen-edge client drop survives pointer release and relayout", () =>
                {
                    var actual = DockGlassBounds(dock);
                    Require(Math.Abs(actual.X - target.X) <= 2 && Math.Abs(actual.Y - target.Y) <= 2 && SameFrame(atDrop, NativeFrame(handle)),
                        "A screen-edge pointer drop was constrained by the native taskbar work area or bounced after release.");
                    Require(commits == 1, "The edge drop did not save exactly once.");
                    CheckFreeDockBounds(dock, bounds);
                    observations["clientDrop" + edge] = new { glass = new { actual.X, actual.Y, actual.Width, actual.Height }, atDrop };
                });
            }
            // Put the visible centre in the monitor centre before resizing, so
            // clamping at a screen edge is not mistaken for a resize jump.
            var centered = DockGlassBounds(dock); start = GripPoint(dock, "positionGrip");
            Require(dock.BeginDirectManipulation(start, false), "Could not begin the central test placement.");
            dock.UpdateDirectManipulation(new Point(start.X + bounds.Left + bounds.Width / 2d - (centered.Left + centered.Width / 2),
                start.Y + bounds.Top + bounds.Height / 2d - (centered.Top + centered.Height / 2)));
            dock.EndDirectManipulation(); await Frame();
            var initialSize = controller.State.Settings.IconSize; var initialGlass = DockGlassBounds(dock);
            bytes = File.ReadAllBytes(configPath); written = File.GetLastWriteTimeUtc(configPath);
            tiles = DockTileButtons(dock); images = DockIconImages(dock); generation = Field<int>(dock, "generation");
            var cachedIcons = IconCacheCount(controller); commits = 0; start = GripPoint(dock, "resizeGrip");
            check("The visible resize handle starts the same captured production gesture", () =>
            {
                Require(dock.BeginDirectManipulation(start, true) && dock.IsPositionDragging && dock.IsSizeDragging
                    && ReferenceEquals(Mouse.Captured, dock), "The resize handle did not enter its captured resize state.");
            });
            if (!dock.IsPositionDragging) return;
            for (var step = 1; step <= 8; step++) dock.UpdateDirectManipulation(new Point(start.X + step * 24, start.Y + step * 10));
            dock.UpdateLayout();
            check("Continuous resizing enlarges icons and the glass while reusing the current visuals", () =>
            {
                var glass = DockGlassBounds(dock);
                Require(controller.State.Settings.IconSize > initialSize + 1 && glass.Width > initialGlass.Width + 5 && glass.Height > initialGlass.Height + 1,
                    "Resizing changed only a setting or an empty window instead of the live icons and glass.");
                Require(generation == Field<int>(dock, "generation") && SameObjects(tiles, DockTileButtons(dock)) && SameObjects(images, DockIconImages(dock)),
                    "Resize preview rebuilt application tiles or image controls on each pointer update.");
                Require(IconCacheCount(controller) <= cachedIcons, "Resize preview requested another set of icon bitmaps before release.");
                Require(commits == 0 && bytes.SequenceEqual(File.ReadAllBytes(configPath)) && File.GetLastWriteTimeUtc(configPath) == written,
                    "Continuous resize persisted transient sizes or raised change notifications before release.");
            });
            var previewSize = controller.State.Settings.IconSize; var previewGlass = DockGlassBounds(dock);
            dock.EndDirectManipulation(); await Frame();
            check("Resize release persists the selected size once and retains the visible placement", () =>
            {
                var glass = DockGlassBounds(dock); var saved = new StateStore(data).Load().Settings;
                Require(commits == 1 && !dock.IsPositionDragging && !dock.IsSizeDragging && GetCapture() != handle,
                    "Resize release leaked capture or wrote more than once.");
                Require(Math.Abs(saved.IconSize - previewSize) < .01 && Math.Abs(glass.X - previewGlass.X) <= 2 && Math.Abs(glass.Y - previewGlass.Y) <= 2
                    && Math.Abs(glass.Width - previewGlass.Width) <= 2 && Math.Abs(glass.Height - previewGlass.Height) <= 2,
                    "The committed resize did not match its last visible preview.");
                CheckFreeDockBounds(dock, bounds);
            });

            var cancelledFrame = NativeFrame(handle); var cancelledSize = controller.State.Settings.IconSize;
            bytes = File.ReadAllBytes(configPath); commits = 0; start = GripPoint(dock, "resizeGrip");
            Require(dock.BeginDirectManipulation(start, true), "Could not begin the cancellable resize.");
            dock.UpdateDirectManipulation(new Point(start.X + 160, start.Y + 60));
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dock)!, Environment.TickCount, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            dock.RaiseEvent(escape); await Frame();
            check("Escape cancels the live size and restores the previous frame without saving", () =>
            {
                Require(!dock.IsPositionDragging && !dock.IsSizeDragging && GetCapture() != handle && commits == 0
                    && Math.Abs(controller.State.Settings.IconSize - cancelledSize) < .01 && SameFrame(cancelledFrame, NativeFrame(handle))
                    && bytes.SequenceEqual(File.ReadAllBytes(configPath)), "Escape left a partial size, saved a cancelled drag, or retained capture.");
            });
            start = GripPoint(dock, "positionGrip");
            Require(dock.BeginDirectManipulation(start, false), "Could not begin the capture-loss check.");
            dock.UpdateDirectManipulation(new Point(start.X + 100, start.Y - 100)); Mouse.Capture(null); await Frame();
            check("Unexpected capture loss retires the gesture and restores its saved position", () =>
            {
                Require(!dock.IsPositionDragging && GetCapture() != handle && commits == 0 && SameFrame(cancelledFrame, NativeFrame(handle))
                    && bytes.SequenceEqual(File.ReadAllBytes(configPath)), "Capture loss left an active drag, moved the saved Dock, or wrote a cancelled position.");
            });
            start = GripPoint(dock, "positionGrip"); commits = 0;
            Require(dock.BeginDirectManipulation(start, false), "Could not begin a stationary pointer gesture.");
            dock.EndDirectManipulation(); await Frame();
            check("A click without a meaningful drag does not save or change placement", () =>
                Require(commits == 0 && SameFrame(cancelledFrame, NativeFrame(handle)) && bytes.SequenceEqual(File.ReadAllBytes(configPath)),
                    "A click-only gesture rewrote the configuration or moved the Dock."));

            using var reload = new AppController(data, null);
            var reopened = new DockWindow(reload) { ShowActivated = false };
            var reloadDock = typeof(AppController).GetField("dock", Private)!; reloadDock.SetValue(reload, reopened);
            try
            {
                reopened.Show(); await Frame();
                check("A new Dock reloads the pointer-selected position and size from storage", () =>
                {
                    Require(Math.Abs(reload.State.Settings.IconSize - cancelledSize) < .01 && SameFrame(cancelledFrame, NativeFrame(new WindowInteropHelper(reopened).Handle)),
                        "A newly created Dock did not retain the saved direct-manipulation result.");
                    Require(!reopened.Topmost && (GetWindowLongPtr(new WindowInteropHelper(reopened).Handle, -20).ToInt64() & 8) == 0,
                        "A reloaded resized Dock became topmost.");
                });
            }
            finally { reloadDock.SetValue(reload, null); reopened.Close(); }
        }
        finally
        {
            if (dock.IsPositionDragging) dock.EndDirectManipulation(true);
            controller.Changed -= observeSave;
            controller.State.Settings.IconSize = 48; controller.State.Settings.AutoHide = false; dock.UpdateSettings();
        }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static DockFrame NativeFrame(IntPtr handle) { Require(GetWindowRect(handle, out var r), "No native Dock frame."); return new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top); }
    private static bool SameFrame(DockFrame a, DockFrame b) => Math.Abs(a.X - b.X) <= 2 && Math.Abs(a.Y - b.Y) <= 2 && Math.Abs(a.Width - b.Width) <= 2 && Math.Abs(a.Height - b.Height) <= 2;
    private static Point GripPoint(DockWindow dock, string field) { var grip = Field<FrameworkElement>(dock, field); return grip.PointToScreen(new Point(grip.ActualWidth / 2, grip.ActualHeight / 2)); }
    private static Button[] DockTileButtons(DockWindow dock) => Field<System.Collections.IEnumerable>(dock, "tiles").Cast<object>().Select(tile => Field<Button>(tile, "Button")).ToArray();
    private static Image[] DockIconImages(DockWindow dock) => DockTileButtons(dock).SelectMany(button => Descendants(button).OfType<Image>()).ToArray();
    private static bool SameObjects<T>(T[] a, T[] b) where T : class => a.Length == b.Length && a.Zip(b).All(pair => ReferenceEquals(pair.First, pair.Second));
    private static int IconCacheCount(AppController controller) { var cache = Field<object>(Field<object>(controller, "icons"), "cache"); return (int)cache.GetType().GetProperty("Count")!.GetValue(cache)!; }
    private static bool ClientPointer(DockWindow dock, int message, Point screen) =>
        (bool)typeof(DockWindow).GetMethod("ProcessClientPointerMessage", Private)!.Invoke(dock, [message, screen])!;
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
}
