using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LumaDock.App;
using LumaDock.Core;
using LumaDock.Storage;

namespace LumaDock.Dock.Ui.Tests;

/// <summary>Tests the real WPF Dock with isolated records, without launching other applications or changing Windows preferences.</summary>
internal static class Program
{
    private static readonly List<object> Results = [];
    private static readonly List<object> Layouts = [];
    private static readonly List<string> Errors = [];
    private static int exit = 1;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [STAThread]
    private static int Main(string[] args)
    {
        var report = Path.GetFullPath(args.Length > 1 && args[0] == "--report" ? args[1] : "artifacts/tests/dock-ui-results.json");
        var profile = Path.GetFullPath(Path.Combine("work", "dock-ui-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(profile); Log.DirectoryPath = Path.Combine(profile, "logs");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { Errors.Add(e.Exception.ToString()); e.Handled = true; };
        app.Startup += async (_, _) =>
        {
            AppController? controller = null; DockWindow? dock = null;
            try
            {
                controller = new AppController(profile, null);
                controller.State.Settings.LiveGlass = false;
                controller.State.Settings.LiquidGlass = false;
                controller.State.Settings.IconSize = 48;
                controller.State.Settings.AnimationStrength = .45;
                controller.State.Apps = Enumerable.Range(1, 7).Select(i => new AppEntry { Id = "fixture" + i, Name = "Dock test " + i, LaunchPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), ExecutablePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), Arguments = "--fixture " + i }).ToList();
                controller.State.PinnedIds = controller.State.Apps.Select(x => x.Id).ToList();
                dock = new DockWindow(controller) { ShowActivated = false };
                dock.SourceInitialized += (_, _) => EnableWindow(new WindowInteropHelper(dock).Handle, false);
                dock.Show(); await Frame();
                Check("Dock uses one transparent tool window with no rectangular flight-area backdrop", () =>
                {
                    Require(dock.AllowsTransparency, "Dock is not per-pixel transparent.");
                    Require((GetWindowLongPtr(new WindowInteropHelper(dock).Handle, -20).ToInt64() & 0x80000) != 0, "WS_EX_LAYERED is missing.");
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(dock.ActualWidth), (int)Math.Ceiling(dock.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(dock);
                    var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                    Require(pixels[(2 * bitmap.PixelWidth + 2) * 4 + 3] == 0, "A flight-area corner was painted.");
                });
                Check("All Dock actions keep accessible application names", () =>
                {
                    var buttons = Tiles(dock).Select(t => Field<Button>(t, "Button")).ToArray();
                    Require(buttons.Length == 8, "The actual pin/launcher action count changed.");
                    Require(buttons.Select(System.Windows.Automation.AutomationProperties.GetName).SequenceEqual(new[] { "应用库" }.Concat(controller.State.Apps.Select(a => a.Name))), "Dock accessibility names do not match records.");
                });
                Check("Dock hover is dormant at rest", () => Require(!Field<DispatcherTimer>(dock, "hover").IsEnabled, "Hover timer is running without input."));
                Check("Drag identity and native format queries start without creating a transfer file", () =>
                {
                    var created = 0;
                    var shortcut = Path.Combine(profile, "delayed-transfer.url");
                    var transfer = new DockTransferData("fixture1", () => { created++; File.WriteAllText(shortcut, "[InternetShortcut]\r\nURL=https://example.invalid/fixture\r\n"); return [shortcut]; }, error => throw error);
                    var wrapped = new DataObject(transfer);
                    Require((string?)wrapped.GetData("LumaDock.Pin") == "fixture1" && wrapped.GetFormats().Contains(DataFormats.FileDrop) && created == 0 && !File.Exists(shortcut), "Drag startup eagerly performed file I/O.");
                    var native = (System.Runtime.InteropServices.ComTypes.IDataObject)wrapped;
                    var format = new System.Runtime.InteropServices.ComTypes.FORMATETC { cfFormat = (short)DataFormats.GetDataFormat(DataFormats.FileDrop).Id,
                        dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL };
                    Require(native.QueryGetData(ref format) == 0 && created == 0, "Native format probing created a file before it was requested.");
                    native.GetData(ref format, out var medium);
                    try
                    {
                        var path = new System.Text.StringBuilder(4096);
                        Require(DragQueryFile(medium.unionmember, uint.MaxValue, null, 0) == 1, "Shell did not receive one real CF_HDROP file.");
                        DragQueryFile(medium.unionmember, 0, path, (uint)path.Capacity);
                        Require(path.ToString() == shortcut && File.Exists(shortcut) && created == 1, "Delayed Shell transfer did not materialize exactly once.");
                        var retrieved = wrapped.GetData(DataFormats.FileDrop) as string[] ?? throw new InvalidOperationException("Shell file data was missing.");
                        Require(retrieved.Length == 1 && retrieved[0] == shortcut && created == 1, "Repeated retrieval recreated a transfer file.");
                    }
                    finally { ReleaseStgMedium(ref medium); }
                });
                Check("A failed Shell export keeps internal reordering available and reports failure once", () =>
                {
                    var attempts = 0; var failures = 0;
                    var transfer = new DockTransferData("fixture1", () => { attempts++; throw new IOException("Fixture failure"); }, _ => failures++);
                    var wrapped = new DataObject(transfer);
                    var first = wrapped.GetData(DataFormats.FileDrop) as string[] ?? throw new InvalidOperationException("Failed export omitted its empty file result.");
                    var second = wrapped.GetData(DataFormats.FileDrop) as string[] ?? throw new InvalidOperationException("Repeated export omitted its empty file result.");
                    Require(first.Length == 0 && second.Length == 0, "Failed export returned nonexistent files.");
                    Require(attempts == 1 && failures == 1 && !wrapped.GetDataPresent(DataFormats.FileDrop) && (string?)wrapped.GetData("LumaDock.Pin") == "fixture1", "Export failure disabled internal identity or repeatedly performed work.");
                });
                Check("Insertion gaps preserve exact before/after order without duplicate pins", () =>
                {
                    var pins = new List<string> { "a", "b", "c", "d" };
                    DockOrder.InsertAtGap(pins, ["a"], 3); Require(pins.SequenceEqual(new[] { "b", "c", "a", "d" }), "Forward insertion chose the wrong side.");
                    DockOrder.InsertAtGap(pins, ["d"], 1); Require(pins.SequenceEqual(new[] { "b", "d", "c", "a" }), "Backward insertion chose the wrong side.");
                    DockOrder.InsertAtGap(pins, ["x", "y", "x"], 2); Require(pins.SequenceEqual(new[] { "b", "d", "x", "y", "c", "a" }), "Batch drop did not keep incoming order.");
                    DockOrder.InsertAtGap(pins, ["c"], pins.Count); Require(pins[^1] == "c" && pins.Count == pins.Distinct().Count(), "An end drop duplicated a pin.");
                });
                var originalPins = controller.State.PinnedIds.ToArray();
                foreach (var position in new[] { "Bottom", "Top", "Left", "Right" })
                {
                    controller.State.Settings.Position = position; dock.UpdateSettings(); await Frame();
                    Check($"{position} Dock maps every visual half-cell to its exact insertion gap", () =>
                    {
                        var appTiles = Tiles(dock).Skip(1).ToArray();
                        for (var i = 0; i < appTiles.Length; i++)
                        {
                            var button = Field<Button>(appTiles[i], "Button");
                            var center = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), dock);
                            var before = position is "Left" or "Right" ? new Point(center.X, center.Y - 1) : new Point(center.X - 1, center.Y);
                            var after = position is "Left" or "Right" ? new Point(center.X, center.Y + 1) : new Point(center.X + 1, center.Y);
                            Require(Invoke<int>(dock, "InsertionGap", before) == i && Invoke<int>(dock, "InsertionGap", after) == i + 1, "A cell half maps to the wrong insertion slot.");
                            Call(dock, "ShowInsertionAt", i);
                            Require(Field<Border>(dock, "insertionCue").Visibility == Visibility.Visible, "Insertion position has no visible cue.");
                        }
                        Call(dock, "ClearInsertion");
                        Require(Field<Border>(dock, "insertionCue").Visibility == Visibility.Collapsed && !Field<DispatcherTimer>(dock, "hover").IsEnabled, "Drag feedback retained a cue or animation after cancellation.");
                    });
                }
                Check("Pinning between two icons saves the exact order and preserves the original application", () =>
                {
                    var moving = controller.State.Apps[0]; var path = moving.LaunchPath;
                    controller.PinAt(moving, 3);
                    Require(controller.State.PinnedIds.SequenceEqual(originalPins.Skip(1).Take(2).Concat([originalPins[0]]).Concat(originalPins.Skip(3))), "Controller inserted at the wrong gap.");
                    var loaded = new StateStore(profile).Load();
                    Require(loaded.PinnedIds.SequenceEqual(controller.State.PinnedIds) && File.Exists(path), "Pin order was not persisted or a launch file changed.");
                });
                controller.State.PinnedIds = originalPins.ToList(); controller.State.Settings.Position = "Bottom"; dock.UpdateSettings(); await Frame();
                Check("Icon centres hit their actual application buttons", () =>
                {
                    foreach (var tile in Tiles(dock))
                    {
                        var button = Field<Button>(tile, "Button"); var visual = Field<FrameworkElement>(tile, "Visual");
                        var point = visual.TranslatePoint(new Point(visual.ActualWidth / 2, visual.ActualHeight / 2), dock);
                        var hit = dock.InputHitTest(point) as DependencyObject;
                        while (hit != null && !ReferenceEquals(hit, button)) hit = VisualTreeHelper.GetParent(hit);
                        Require(ReferenceEquals(hit, button), "Visible icon does not route input to its application action.");
                    }
                });
                Check("Native hit testing passes through the unpainted flight corner", () =>
                {
                    var hwnd = new WindowInteropHelper(dock).Handle;
                    EnableWindow(hwnd, true);
                    try
                    {
                        var blank = dock.PointToScreen(new Point(2, 2));
                        var body = Field<Grid>(dock, "body");
                        var painted = body.PointToScreen(new Point(body.ActualWidth / 2, body.ActualHeight / 2));
                        Require(WindowFromPoint(new NativePoint((int)blank.X, (int)blank.Y)) != hwnd, "Transparent flight corner still blocks native hit testing.");
                        Require(WindowFromPoint(new NativePoint((int)painted.X, (int)painted.Y)) == hwnd, "The actual plate is not hit-testable.");
                    }
                    finally { EnableWindow(hwnd, false); }
                });

                foreach (var shape in new[] { "Capsule", "Pebble", "Rail" })
                foreach (var position in new[] { "Bottom", "Top", "Left", "Right" })
                {
                    controller.State.Settings.DockShape = shape; controller.State.Settings.Position = position;
                    dock.UpdateSettings(); await Frame();
                    var first = Tiles(dock)[0]; var last = Tiles(dock)[^1];
                    foreach (var tile in new[] { first, last })
                    {
                        MoveTo(dock, tile); await Settle();
                        var info = Snapshot(dock, shape, position);
                        Layouts.Add(info);
                        Check($"{shape}/{position} {(ReferenceEquals(tile, first) ? "leading" : "trailing")} icon has room to rise without clipping", () =>
                        {
                            Require(!controller.ReducedMotion, "Animation coverage requires AC power and enabled Windows client animations.");
                            var scale = Field<ScaleTransform>(tile, "Scale");
                            Require(scale.ScaleX > 1.34, "Default hover does not visibly magnify the focused icon.");
                            CheckBounds(dock);
                            Require(!Field<DispatcherTimer>(dock, "hover").IsEnabled, "The hover timer did not stop after settling.");
                            var transform = Field<TranslateTransform>(tile, "Move");
                            var lift = position is "Bottom" or "Top" ? transform.Y : transform.X;
                            Require(position is "Bottom" or "Right" ? lift < -5 : lift > 5, "The icon does not rise toward the work area.");
                            var rim = Field<FrameworkElement>(dock, "highlight");
                            Require(!(bool)rim.GetType().GetProperty("DrawStaticEdge")!.GetValue(rim)!, "A second static rounded edge is drawn over the Dock contour.");
                            var contour = Field<object>(rim, "outerContour");
                            var start = (Point)contour.GetType().GetMethod("At")!.Invoke(contour, [0d])!;
                            var body = Field<Grid>(dock, "body");
                            var effective = Math.Min(Field<double>(dock, "radius") - 1.7, Math.Min(body.ActualWidth - 3.4, body.ActualHeight - 3.4) / 2);
                            Require(Math.Abs(start.X - (.7 + effective * (shape == "Pebble" ? .68 : 1))) < .05, "Motion outline does not follow the selected Dock corner geometry.");
                        });
                    }
                    Call(dock, "ResetMagnification"); await Settle();
                }
                controller.State.Settings.IconSize = 80; controller.State.Settings.AnimationStrength = 1;
                foreach (var position in new[] { "Bottom", "Top", "Left", "Right" })
                {
                    controller.State.Settings.Position = position; dock.UpdateSettings(); await Frame();
                    MoveTo(dock, Tiles(dock)[4]); await Settle();
                    Check($"Maximum-size {position} hover is bounded and influences neighbouring icons", () =>
                    {
                        CheckBounds(dock);
                        Require(Field<ScaleTransform>(Tiles(dock)[4], "Scale").ScaleX > 1.79, "Maximum hover scale was not applied.");
                        Require(Field<ScaleTransform>(Tiles(dock)[3], "Scale").ScaleX > 1.1, "Neighbouring icons did not respond.");
                        Require(!Field<DispatcherTimer>(dock, "hover").IsEnabled, "Maximum hover did not settle.");
                        var target = Tiles(dock)[4]; var visual = Field<FrameworkElement>(target, "Visual");
                        var bounds = visual.TransformToAncestor(dock).TransformBounds(new Rect(visual.RenderSize));
                        var point = position is "Bottom" or "Top" ? new Point(bounds.Left + 4, bounds.Top + bounds.Height / 2) : new Point(bounds.Left + bounds.Width / 2, bounds.Top + 4);
                        var hit = dock.InputHitTest(point) as DependencyObject; var button = Field<Button>(target, "Button");
                        while (hit != null && !ReferenceEquals(hit, button)) hit = VisualTreeHelper.GetParent(hit);
                        Require(ReferenceEquals(hit, button), "The expanded icon's outer area routes input to a neighbouring application.");
                    });
                }
                controller.State.Settings.Animations = false; dock.UpdateSettings(); await Frame();
                MoveTo(dock, Tiles(dock)[3]); await Settle();
                Check("Disabled animations leave no hover clock or transformed icons", () =>
                {
                    Require(!Field<DispatcherTimer>(dock, "hover").IsEnabled, "Disabled animations started the hover clock.");
                    Require(Tiles(dock).All(t => Field<ScaleTransform>(t, "Scale").ScaleX == 1), "A reduced-motion icon stayed magnified.");
                });
                controller.State.Settings.Animations = true;
                var original = controller.State.Apps.ToList();
                controller.State.Apps.AddRange(Enumerable.Range(8, 32).Select(i => new AppEntry { Id = "fixture" + i, Name = "Dock test " + i, LaunchPath = original[0].LaunchPath, ExecutablePath = original[0].ExecutablePath }));
                controller.State.PinnedIds = controller.State.Apps.Select(a => a.Id).ToList();
                foreach (var position in new[] { "Bottom", "Left" })
                {
                    controller.State.Settings.Position = position; dock.UpdateSettings(); await Frame();
                    var scroller = Field<ScrollViewer>(dock, "scroll");
                    if (position == "Left") scroller.ScrollToBottom(); else scroller.ScrollToRightEnd();
                    await Frame(); var tile = Tiles(dock)[^1]; MoveTo(dock, tile); await Settle();
                    Check($"Overfull {position} Dock keeps its final magnified icon inside the scroll viewport", () =>
                    {
                        Require(position == "Left" ? scroller.ScrollableHeight > 0 : scroller.ScrollableWidth > 0, "Overfull Dock did not expose a scroll range.");
                        var visual = Field<FrameworkElement>(tile, "Visual");
                        var rect = visual.TransformToAncestor(dock).TransformBounds(new Rect(visual.RenderSize));
                        Require(rect.Left >= -.5 && rect.Top >= -.5 && rect.Right <= dock.ActualWidth + .5 && rect.Bottom <= dock.ActualHeight + .5, "Final enlarged icon was clipped by its window.");
                        Require(!Field<DispatcherTimer>(dock, "hover").IsEnabled, "Scrolled hover kept a motion clock alive.");
                    });
                }
                controller.State.Apps = original; controller.State.PinnedIds = original.Select(a => a.Id).ToList();
                controller.State.Settings.LiquidGlass = true;
                controller.State.Settings.Opacity = .55; dock.UpdateSettings(); await Frame();
                var lowTint = ((SolidColorBrush)Field<Border>(dock, "tint").Background).Color.A;
                controller.State.Settings.Opacity = 1; dock.UpdateSettings(); await Frame();
                Check("Opacity adds one monotonic tint rather than fading the sampled background twice", () =>
                {
                    var highTint = ((SolidColorBrush)Field<Border>(dock, "tint").Background).Color.A;
                    Require(highTint > lowTint, "Higher opacity does not add a stronger tint.");
                    Require(Field<FrameworkElement>(dock, "liquid").Opacity == 1, "Backdrop is independently faded over an opaque plate.");
                });
                controller.State.Settings.LiquidGlass = false;
                controller.State.PinnedIds.Clear(); dock.UpdateSettings(); await Frame();
                Check("An empty pin list leaves a usable launcher without a stray separator", () =>
                {
                    Require(Tiles(dock).Length == 1, "An empty Dock lost its launcher.");
                    Require(Field<StackPanel>(dock, "items").Children.Count == 1, "An empty Dock retained a separator.");
                    CheckBounds(dock);
                });
                controller.State.PinnedIds = original.Select(a => a.Id).ToList();
                controller.State.Settings.Animations = true; controller.State.Settings.PowerSaver = true; dock.UpdateSettings(); await Frame();
                Check("Power saving uses an opaque plate and no hover clock", () =>
                {
                    Require(!Field<DispatcherTimer>(dock, "hover").IsEnabled, "Power saving retained a hover clock.");
                    var fill = Field<object>(dock, "plate").GetType().GetProperty("Fill")!.GetValue(Field<object>(dock, "plate")) as SolidColorBrush;
                    Require(fill?.Color.A == 255, "Power saving does not retain an opaque plate.");
                });
                controller.State.Settings.PowerSaver = false; controller.State.Settings.AutoHide = true; dock.UpdateSettings(); await Frame();
                MoveTo(dock, Tiles(dock)[2]); await Settle();
                Call(dock, "HideTick", null, EventArgs.Empty); await Frame();
                Check("Auto-hide releases magnification and the custom glass surface", () =>
                {
                    Require(Field<bool>(dock, "tucked"), "Dock did not tuck.");
                    Require(!Field<DispatcherTimer>(dock, "hover").IsEnabled, "Tucked Dock has a hover clock.");
                    Require(!Field<Grid>(dock, "body").IsVisible, "Tucked Dock retained the glass surface.");
                });
                Call(dock, "Reveal"); await Frame();
                Check("Revealing the Dock restores bounds with resting icons", () =>
                {
                    Require(!Field<bool>(dock, "tucked"), "Dock remained tucked.");
                    Require(Tiles(dock).All(t => Field<ScaleTransform>(t, "Scale").ScaleX == 1), "Auto-hide restored stale magnification.");
                    CheckBounds(dock);
                });
                dock.Close(); await Frame();
                Check("Closing the Dock detaches its motion and auto-hide clocks", () =>
                {
                    Call(dock, "ScheduleHide");
                    Require(!Field<DispatcherTimer>(dock, "hover").IsEnabled && !Field<DispatcherTimer>(dock, "hideDelay").IsEnabled, "A closed Dock retains a clock.");
                });
                exit = Errors.Count == 0 ? 0 : 1;
            }
            catch (Exception ex) { Errors.Add(ex.ToString()); }
            finally
            {
                if (dock?.IsVisible == true) dock.Close(); controller?.Dispose();
                Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                File.WriteAllText(report, JsonSerializer.Serialize(new { measuredAt = DateTimeOffset.Now, passed = Results.Count - Errors.Count(e => e.StartsWith("CHECK:")), total = Results.Count, results = Results, layouts = Layouts, errors = Errors, notes = "Actual WPF Dock with isolated app records; no software was launched and no system settings were written. Four placements, three contours, two hover extremes. This is a geometry/motion lifecycle check, not a physical multi-monitor or frame-rate benchmark." }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Dock UI: {Results.Count - Errors.Count(e => e.StartsWith("CHECK:"))}/{Results.Count}; {report}");
                foreach (var error in Errors) Console.WriteLine(error);
                app.Shutdown();
            }
        };
        app.Run(); return exit;
    }
    private static void Check(string name, Action action)
    {
        try { action(); Results.Add(new { name, passed = true }); }
        catch (Exception e) { Errors.Add("CHECK: " + name + ": " + e.Message); Results.Add(new { name, passed = false, error = e.Message }); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static object[] Tiles(DockWindow dock) => ((IEnumerable)Field<object>(dock, "tiles")).Cast<object>().ToArray();
    private static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Private | BindingFlags.Public)!.GetValue(o)!;
    private static void Call(object o, string name, params object?[] args) => o.GetType().GetMethod(name, Private)!.Invoke(o, args);
    private static T Invoke<T>(object o, string name, params object?[] args) => (T)o.GetType().GetMethod(name, Private)!.Invoke(o, args)!;
    private static void MoveTo(DockWindow dock, object tile)
    {
        if (Field<AppController>(dock, "controller").ReducedMotion) return;
        var button = Field<Button>(tile, "Button");
        Call(dock, "SetTargets", button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), Field<StackPanel>(dock, "items")));
    }
    private static void CheckBounds(DockWindow dock)
    {
        foreach (var tile in Tiles(dock))
        {
            var visual = Field<FrameworkElement>(tile, "Visual");
            var rect = visual.TransformToAncestor(dock).TransformBounds(new Rect(visual.RenderSize));
            Require(rect.Left >= -.5 && rect.Top >= -.5 && rect.Right <= dock.ActualWidth + .5 && rect.Bottom <= dock.ActualHeight + .5, $"Transformed icon leaves its window: {rect} vs {dock.ActualWidth}x{dock.ActualHeight}.");
        }
    }
    private static object Snapshot(DockWindow dock, string shape, string position) => new { shape, position, width = dock.ActualWidth, height = dock.ActualHeight, scales = Tiles(dock).Select(t => Field<ScaleTransform>(t, "Scale").ScaleX).ToArray(), timerRunning = Field<DispatcherTimer>(dock, "hover").IsEnabled };
    private static async Task Frame() { await Dispatcher.Yield(DispatcherPriority.ContextIdle); await Task.Delay(70); await Dispatcher.Yield(DispatcherPriority.ContextIdle); }
    private static async Task Settle() { await Task.Delay(600); await Dispatcher.Yield(DispatcherPriority.ContextIdle); }
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr hwnd, bool enabled);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(IntPtr drop, uint index, System.Text.StringBuilder? path, uint size);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref System.Runtime.InteropServices.ComTypes.STGMEDIUM medium);
}
