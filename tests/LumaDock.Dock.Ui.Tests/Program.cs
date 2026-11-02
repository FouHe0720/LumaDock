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
    private static bool interactiveLayerCheck, layerForegroundVerified;
    private static string layerReadyPath = "";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [STAThread]
    private static int Main(string[] args)
    {
        var report = Path.GetFullPath(args.Length > 1 && args[0] == "--report" ? args[1] : "artifacts/tests/dock-ui-results.json");
        interactiveLayerCheck = args.Contains("--interactive-layer-check", StringComparer.Ordinal);
        layerReadyPath = report + ".layer-ready.json";
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
                    Require(buttons.Select(System.Windows.Automation.AutomationProperties.GetName).SequenceEqual(new[] { "应用" }.Concat(controller.State.Apps.Select(a => a.Name))), "Dock accessibility names do not match records.");
                });
                Check("Dock hover is dormant at rest", () => Require(!Field<DispatcherTimer>(dock, "hover").IsEnabled, "Hover timer is running without input."));
                Check("Icon color enhancement preserves the original at zero or an invalid amount", () =>
                {
                    var source = ColorSample();
                    foreach (var amount in new[] { 0d, -1d, double.NaN, double.PositiveInfinity })
                        Require(ReferenceEquals(source, EnhanceColor(source, amount)), "An original-color request changed the image.");
                });
                Check("Icon color enhancement increases real color separation without tinting grayscale", () =>
                {
                    var source = ColorSample(); var before = IconPixels(source);
                    var result = (BitmapSource)EnhanceColor(source, 1); var after = IconPixels(result);
                    Require(after.Take(3).Max() - after.Take(3).Min() > before.Take(3).Max() - before.Take(3).Min(), "The color setting did not increase chroma.");
                    Require(after[4] == after[5] && after[5] == after[6] && after[4] >= before[4], "A neutral icon acquired a color cast or became darker.");
                    Require(IconPixels(source).SequenceEqual(before), "The source icon was modified.");
                });
                Check("Color adjustment keeps transparent and translucent icon alpha byte-identical", () =>
                {
                    var source = ColorSample(); var before = IconPixels(source); var after = IconPixels((BitmapSource)EnhanceColor(source, .7));
                    Require(Enumerable.Range(0, before.Length / 4).All(i => before[i * 4 + 3] == after[i * 4 + 3]), "The color control changed alpha.");
                    Require(before.AsSpan(12, 4).SequenceEqual(after.AsSpan(12, 4)), "A transparent pixel was given invented content.");
                });
                Check("Premultiplied icon edges receive the same straight-color adjustment", () =>
                {
                    var source = ColorSample(); var premultiplied = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0); premultiplied.Freeze();
                    var straight = IconPixels((BitmapSource)EnhanceColor(source, .6)); var converted = IconPixels((BitmapSource)EnhanceColor(premultiplied, .6));
                    for (var i = 0; i < straight.Length; i++)
                        if (straight[i / 4 * 4 + 3] != 0) Require(Math.Abs(straight[i] - converted[i]) <= 2, "Premultiplied edges changed hue or became dark.");
                });
                Check("Enhanced icons keep non-square dimensions, DPI and frozen cross-thread ownership", () =>
                {
                    var source = ColorSample(); var result = (BitmapSource)Task.Run(() => EnhanceColor(source, .5)).GetAwaiter().GetResult();
                    Require(result.PixelWidth == 3 && result.PixelHeight == 2 && Math.Abs(result.DpiX - 144) < .001 && Math.Abs(result.DpiY - 120) < .001, "Color adjustment changed dimensions or DPI.");
                    Require(result.IsFrozen && result.CanFreeze && result.Format == PixelFormats.Bgra32, "Enhanced image cannot be shared as a frozen source.");
                });
                Check("Icon enhancement caches one latest adjustment and shares concurrent requests", () =>
                {
                    var source = ColorSample(); var first = EnhanceColor(source, .5);
                    Require(ReferenceEquals(first, EnhanceColor(source, .50001)), "Equivalent slider amounts allocated another image.");
                    var results = Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => EnhanceColor(source, .8)))).GetAwaiter().GetResult();
                    Require(results.All(result => ReferenceEquals(result, results[0])), "Concurrent loads duplicated enhanced images.");
                    Require(!ReferenceEquals(first, EnhanceColor(source, .5)), "The color cache retained an old adjustment instead of only the latest.");
                    Require(ReferenceEquals(EnhanceColor(source, 1), EnhanceColor(source, 2)), "The enhancement upper bound was not normalized.");
                });
                Check("Unsupported, mutable and oversized artwork stays at its real original", () =>
                {
                    var drawing = new DrawingImage(new GeometryDrawing(Brushes.Red, null, new RectangleGeometry(new Rect(0, 0, 16, 16)))); drawing.Freeze();
                    var mutable = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 20, 40, 80, 255 }, 4);
                    var oversized = BitmapSource.Create(513, 1, 96, 96, PixelFormats.Bgra32, null, new byte[513 * 4], 513 * 4); oversized.Freeze();
                    foreach (var source in new ImageSource[] { drawing, mutable, oversized })
                        Require(ReferenceEquals(source, EnhanceColor(source, .5)), "Unsupported artwork was replaced or decoded without bounds.");
                });
                Check("Fully transparent artwork creates neither a color layer nor another image", () =>
                {
                    var source = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, new byte[8], 8); source.Freeze();
                    Require(ReferenceEquals(source, EnhanceColor(source, 1)), "A transparent icon allocated colored artwork.");
                });
                Check("Launcher reflection reuses its actual frozen artwork without another bitmap", () =>
                {
                    var tile = Tiles(dock)[0];
                    var image = ((Grid)Field<FrameworkElement>(tile, "Visual")).Children.OfType<Image>().Single();
                    var reflection = Field<FrameworkElement>(tile, "Reflection");
                    Require(ReferenceEquals(image.Source, reflection.GetType().GetProperty("Source")!.GetValue(reflection)), "The reflection is not sharing the icon's real source.");
                    Require(image.Source.IsFrozen && !reflection.IsHitTestVisible, "Reflection artwork is mutable or intercepts input.");
                });
                foreach (var direction in new[] { "Bottom", "Top", "Left", "Right" })
                {
                    Check($"{direction} reflection mirrors actual icon pixels toward the outer edge and fades away", () => CheckReflectionPixels(direction));
                }
                Check("The stronger mirror setting exceeds the previous limit without inventing pixels", () =>
                {
                    var source = ColorIcon(); var half = CreateReflection("Bottom", .5); var strong = CreateReflection("Bottom", .85);
                    SetReflectionSource(half, source); SetReflectionSource(strong, source);
                    var halfPixels = RenderPixels(half); var strongPixels = RenderPixels(strong);
                    var halfAlpha = halfPixels.Where((_, index) => index % 4 == 3).Sum(value => (int)value);
                    var strongAlpha = strongPixels.Where((_, index) => index % 4 == 3).Sum(value => (int)value);
                    Require(strongAlpha > halfAlpha * 1.5, "The mirror slider still clamps at the former 50% limit.");
                    Require(ReferenceEquals(source, strong.GetType().GetProperty("Source")!.GetValue(strong)), "The stronger mirror copied or substituted the real source.");
                    var shelf = (Rect)strong.GetType().GetProperty("ShelfBounds", Private)!.GetValue(strong)!;
                    Require(shelf.Height > 24 && shelf.Height <= 28, "The mirror did not gain its bounded depth.");
                });
                Check("Missing or disabled reflection paints no invented fallback", () =>
                {
                    var reflection = CreateReflection("Bottom", 0);
                    SetReflectionSource(reflection, ColorIcon());
                    Require(RenderPixels(reflection).All(value => value == 0), "Disabled reflection drew content.");
                    Call(reflection, "Configure", "Bottom", 88d, 160d, 70d, 64d, 64d, 64d, .4);
                    SetReflectionSource(reflection, null);
                    Require(RenderPixels(reflection).All(value => value == 0), "A missing icon acquired fallback artwork.");
                });
                Check("A changed cached icon source refreshes the mirror, including a non-square source", () =>
                {
                    var reflection = CreateReflection("Bottom", .4); var source = ColorIcon();
                    SetReflectionSource(reflection, source); var first = RenderPixels(reflection);
                    var replacement = BitmapSource.Create(32, 16, 96, 96, PixelFormats.Bgra32, null,
                        Enumerable.Range(0, 32 * 16).SelectMany(_ => new byte[] { 255, 0, 255, 255 }).ToArray(), 32 * 4);
                    replacement.Freeze(); SetReflectionSource(reflection, replacement); var second = RenderPixels(reflection);
                    Require(!first.SequenceEqual(second) && ReferenceEquals(reflection.GetType().GetProperty("Source")!.GetValue(reflection), replacement), "Mirror did not update its real source or made a private bitmap copy.");
                });
                var temporarilyMissing = controller.State.Apps[2]; var preservedPins = controller.State.PinnedIds.ToArray();
                temporarilyMissing.IsAvailable = false; dock.Rebuild(); await Frame();
                Check("An unavailable application leaves the Dock without losing its saved pin", () =>
                {
                    Require(Tiles(dock).All(tile => Field<AppEntry?>(tile, "App")?.Id != temporarilyMissing.Id), "An unavailable application is still shown.");
                    Require(controller.State.PinnedIds.SequenceEqual(preservedPins), "Filtering an unavailable application deleted or reordered its saved pin.");
                });
                temporarilyMissing.IsAvailable = true; dock.Rebuild(); await Frame();
                Check("A restored application reappears at its saved Dock position", () =>
                    Require(Tiles(dock).Skip(1).Select(tile => Field<AppEntry>(tile, "App").Id).SequenceEqual(preservedPins), "Restored application did not recover its saved position."));
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
                var scenarioApps = controller.State.Apps.ToArray();
                var modePath = Path.Combine(profile, "launch-modes.exe"); File.Copy(scenarioApps[0].ExecutablePath, modePath);
                var modes = Enumerable.Range(1, 3).Select(index => new AppEntry { Id = "mode" + index, Name = "Launch mode fixture", LaunchPath = modePath, ExecutablePath = modePath, Arguments = "--profile=" + index }).ToArray();
                var missingHead = new AppEntry { Id = "unavailable-head", Name = "Temporarily unavailable head", LaunchPath = scenarioApps[0].LaunchPath, ExecutablePath = scenarioApps[0].ExecutablePath, Arguments = "--offline-head", IsAvailable = false };
                var missingTail = new AppEntry { Id = "unavailable-tail", Name = "Temporarily unavailable tail", LaunchPath = scenarioApps[0].LaunchPath, ExecutablePath = scenarioApps[0].ExecutablePath, Arguments = "--offline-tail", IsAvailable = false };
                controller.State.Apps = scenarioApps.Concat(modes).Concat([missingHead, missingTail]).ToList();
                var mixedPins = new[] { missingHead.Id, originalPins[0], modes[0].Id, modes[1].Id }.Concat(originalPins.Skip(1)).Concat([missingTail.Id]).ToArray();
                controller.State.PinnedIds = mixedPins.ToList(); dock.UpdateSettings(); await Frame();
                Check("Alternate saved launch modes occupy one Dock icon while all mode records and pins survive", () =>
                {
                    var grouped = Tiles(dock).Where(tile => Field<AppEntry?>(tile, "App")?.Name == modes[0].Name).ToArray();
                    Require(grouped.Length == 1 && Field<AppEntry>(grouped[0], "App").Id == modes[0].Id, "Dock duplicated a program or changed its first saved launch mode.");
                    Require(Field<IReadOnlyList<AppEntry>>(grouped[0], "Variants").Count == 3, "The Dock lost an unpinned alternate launch mode.");
                    var submenu = Field<Button>(grouped[0], "Button").ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "启动方式"));
                    Require(submenu.Items.Count == 3 && submenu.Items.OfType<MenuItem>().Select(item => item.ToolTip!.ToString()).All(tip => tip!.Contains("--profile=")), "Real mode arguments are not available in the context menu.");
                    Require(controller.State.PinnedIds.SequenceEqual(mixedPins) && controller.State.Apps.Count == scenarioApps.Length + 5, "Presentation grouping destructively changed saved records or pins.");
                });
                foreach (var position in new[] { "Bottom", "Top", "Left", "Right" })
                {
                    controller.State.Settings.Position = position; dock.UpdateSettings(); await Frame();
                    Check($"{position} visible insertion gaps map through unavailable and grouped saved pins", () =>
                    {
                        var displayedApps = Tiles(dock).Skip(1).Select(tile => Field<AppEntry>(tile, "App")).ToArray();
                        for (var gap = 0; gap < displayedApps.Length; gap++)
                            Require(Invoke<int>(dock, "SavedInsertionGap", gap) == Array.IndexOf(mixedPins, displayedApps[gap].Id), "A visible gap was treated as a raw saved index.");
                        Require(Invoke<int>(dock, "SavedInsertionGap", displayedApps.Length) == Array.IndexOf(mixedPins, displayedApps[^1].Id) + 1, "The final gap crossed an unrelated latent trailing pin.");
                    });
                }
                foreach (var (moving, gap) in new[] { (scenarioApps[2], 1), (scenarioApps[0], 3) })
                {
                    controller.State.PinnedIds = mixedPins.ToList(); dock.UpdateSettings(); await Frame();
                    var savedGap = Invoke<int>(dock, "SavedInsertionGap", gap);
                    var expectedPins = mixedPins.ToList(); DockOrder.InsertAtGap(expectedPins, [moving.Id], savedGap);
                    controller.PinAt(moving, savedGap); dock.Rebuild(); await Frame();
                    Check($"A real pin command at visible gap {gap} preserves latent pins and saves the intended raw order", () =>
                    {
                        Require(controller.State.PinnedIds.SequenceEqual(expectedPins), "The visible insertion command moved to a different saved slot.");
                        Require(new StateStore(profile).Load().PinnedIds.SequenceEqual(expectedPins), "The mapped order did not survive persistence.");
                        Require(controller.State.PinnedIds[0] == missingHead.Id && controller.State.PinnedIds[^1] == missingTail.Id, "An unrelated unavailable pin moved or disappeared.");
                    });
                }
                controller.State.PinnedIds = mixedPins.ToList(); missingHead.IsAvailable = missingTail.IsAvailable = true; dock.UpdateSettings(); await Frame();
                Check("Previously unavailable pins recover their saved positions around a grouped application", () =>
                {
                    var expectedVisible = mixedPins.Where(id => id != modes[1].Id);
                    Require(Tiles(dock).Skip(1).Select(tile => Field<AppEntry>(tile, "App").Id).SequenceEqual(expectedVisible), "Restoring availability reordered a latent pin or exposed duplicate modes.");
                });
                controller.Unpin(modes[1]); dock.Rebuild(); await Frame();
                Check("Removing a grouped Dock application unpins its modes without deleting metadata or source files", () =>
                {
                    Require(!controller.State.PinnedIds.Any(id => modes.Select(mode => mode.Id).Contains(id)), "A second saved mode reappeared after removing the application.");
                    Require(modes.All(mode => controller.State.Apps.Contains(mode)) && File.Exists(modePath), "Unpinning removed a mode record or real source.");
                });
                controller.State.Apps = scenarioApps.ToList(); controller.State.PinnedIds = originalPins.ToList(); dock.UpdateSettings(); await Frame();
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
                        // Expose only this owned fixture while checking native input geometry.
                        // Product defaults and the separate foreground-layer checks stay non-floating.
                        Require(SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0013), "The owned geometry fixture could not be exposed.");
                        var blank = dock.PointToScreen(new Point(2, 2));
                        var body = Field<Grid>(dock, "body");
                        var painted = body.PointToScreen(new Point(body.ActualWidth / 2, body.ActualHeight / 2));
                        Require(WindowFromPoint(new NativePoint((int)blank.X, (int)blank.Y)) != hwnd, "Transparent flight corner still blocks native hit testing.");
                        Require(WindowFromPoint(new NativePoint((int)painted.X, (int)painted.Y)) == hwnd, "The actual plate is not hit-testable.");
                    }
                    finally
                    {
                        SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0, 0x0013);
                        EnableWindow(hwnd, false); dock.Reposition();
                    }
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
                            CheckReflectionBounds(dock);
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
                        CheckReflectionBounds(dock);
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
                    Require(Tiles(dock).All(tile => RenderPixels(Field<FrameworkElement>(tile, "Reflection")).All(value => value == 0)), "Power saving retained icon reflections.");
                });
                controller.State.Settings.PowerSaver = false; controller.State.Settings.LiveGlass = true; controller.State.Settings.LiquidGlass = true;
                controller.State.Settings.Opacity = .7; dock.UpdateSettings(); await Frame();
                Check("Default live glass keeps a clear low-alpha plate and no duplicate tint", () =>
                {
                    var fill = (SolidColorBrush)Field<object>(dock, "plate").GetType().GetProperty("Fill")!.GetValue(Field<object>(dock, "plate"))!;
                    Require(fill.Color.A is >= 80 and <= 110, "The default plate is too opaque to reveal the desktop.");
                    Require(((SolidColorBrush)Field<Border>(dock, "tint").Background).Color.A == 0, "Live glass adds a second tint layer.");
                });
                controller.State.Settings.IconReflection = false; dock.UpdateSettings(); await Frame();
                Check("Reflection toggle removes mirror content without an idle frame loop", () =>
                {
                    Require(Tiles(dock).All(tile => RenderPixels(Field<FrameworkElement>(tile, "Reflection")).All(value => value == 0)), "Reflection toggle did not clear the artwork.");
                    Require(!Field<DispatcherTimer>(dock, "hover").IsEnabled, "Reflection toggle left a hover timer running.");
                });
                controller.State.Settings.IconReflection = true;
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
                await CheckWindowLayering(profile);
                exit = Errors.Count == 0 ? 0 : 1;
            }
            catch (Exception ex) { Errors.Add(ex.ToString()); }
            finally
            {
                if (dock?.IsVisible == true) dock.Close(); controller?.Dispose();
                Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                File.WriteAllText(report, JsonSerializer.Serialize(new { measuredAt = DateTimeOffset.Now, passed = Results.Count - Errors.Count(e => e.StartsWith("CHECK:")), failed = Errors.Count, total = Results.Count, interactiveLayerRequested = interactiveLayerCheck, layerForegroundVerified, results = Results, layouts = Layouts, errors = Errors, notes = "Actual WPF Dock with isolated app records; no software was launched and no system settings were written. Native input geometry exposes only its owned test Dock temporarily and restores its normal layer immediately. Foreground Z-order acceptance is reported separately and requires the owned cover to be truly foreground. This is not a physical multi-monitor or frame-rate benchmark." }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Dock UI: {Results.Count - Errors.Count(e => e.StartsWith("CHECK:"))}/{Results.Count}; {report}");
                foreach (var error in Errors) Console.WriteLine(error);
                app.Shutdown();
            }
        };
        app.Run(); return exit;
    }
    private static async Task CheckWindowLayering(string profile)
    {
        using var controller = new AppController(Path.Combine(profile, "window-layers"), null);
        controller.State.Settings.AutoHide = false;
        controller.State.Settings.Animations = false;
        var dock = new DockWindow(controller) { ShowActivated = false };
        var cover = new Window
        {
            Title = "LumaDock · 层级验收", Width = 240, Height = 130,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = interactiveLayerCheck, ShowActivated = false, Background = Brushes.DimGray
        };
        try
        {
            dock.SourceInitialized += (_, _) => EnableWindow(new WindowInteropHelper(dock).Handle, false);
            dock.Show(); await Frame();
            var dockHandle = new WindowInteropHelper(dock).Handle;
            Check("A default Dock has neither managed nor native topmost state", () =>
                Require(!dock.Topmost && !IsTopmost(dockHandle), "The default Dock still occupies the native topmost band."));
            cover.Left = dock.Left + 15; cover.Top = dock.Top + 15;
            cover.Show(); await Frame();
            var coverHandle = new WindowInteropHelper(cover).Handle;
            if (interactiveLayerCheck && GetForegroundWindow() != coverHandle)
            {
                var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Activated(object? sender, EventArgs e) { if (GetForegroundWindow() == coverHandle) activated.TrySetResult(); }
                cover.Activated += Activated;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(layerReadyPath)!);
                    File.WriteAllText(layerReadyPath, JsonSerializer.Serialize(new { processId = Environment.ProcessId, windowTitle = cover.Title, waitingForOwnedWindowActivation = true }));
                    Console.WriteLine("Layer fixture ready: activate its owned window '" + cover.Title + "'.");
                    if (GetForegroundWindow() != coverHandle) await Task.WhenAny(activated.Task, Task.Delay(TimeSpan.FromSeconds(90)));
                }
                finally { cover.Activated -= Activated; }
            }
            var fixtureIsForeground = GetForegroundWindow() == coverHandle;
            if (interactiveLayerCheck)
                Check("The owned ordinary cover is genuinely foreground before checking Z-order", () =>
                    Require(fixtureIsForeground && !dock.IsActive, "The owned cover was not activated; foreground-layer acceptance cannot be claimed."));
            layerForegroundVerified = fixtureIsForeground;
            foreach (var position in new[] { "Bottom", "Top", "Left", "Right" })
            {
                controller.State.Settings.Position = position;
                dock.UpdateSettings(); await Frame();
                Check(position + " relayout preserves the ordinary Dock layer", () =>
                {
                    Require(!dock.Topmost && !IsTopmost(dockHandle), "Relayout silently enabled topmost state.");
                    if (fixtureIsForeground)
                    {
                        Require(IsAbove(coverHandle, dockHandle), "The Dock moved above the ordinary foreground cover.");
                        Require(GetForegroundWindow() == coverHandle, "Relayout stole keyboard focus.");
                    }
                });
            }
            dock.RefreshWindows(); await Frame();
            Check("Refreshing application state does not raise the Dock or take focus", () =>
            {
                Require(!dock.Topmost && !IsTopmost(dockHandle), "Application refresh silently enabled topmost state.");
                if (fixtureIsForeground) Require(IsAbove(coverHandle, dockHandle) && GetForegroundWindow() == coverHandle,
                    "Application refresh covered the foreground window or took focus.");
            });
            dock.Rebuild(); await Frame();
            Check("Rebuilding the Dock retains its ordinary native layer", () =>
            {
                Require(!dock.Topmost && !IsTopmost(dockHandle), "Rebuild silently enabled topmost state.");
                if (fixtureIsForeground) Require(IsAbove(coverHandle, dockHandle) && GetForegroundWindow() == coverHandle,
                    "Rebuild covered the foreground window or took focus.");
            });
            SetWindowPos(dockHandle, new IntPtr(-1), 0, 0, 0, 0, 0x0013);
            var wasTopmost = IsTopmost(dockHandle);
            dock.Reposition(); await Frame();
            Check("Relayout clears a stale native topmost bit instead of keeping it", () =>
            {
                Require(wasTopmost && !dock.Topmost && !IsTopmost(dockHandle), "The ordinary layer did not clear stale native topmost state.");
                if (fixtureIsForeground) Require(IsAbove(coverHandle, dockHandle) && GetForegroundWindow() == coverHandle,
                    "Restoring the normal layer covered the foreground window or took focus.");
            });
            dock.Close(); await Frame();
            Check("The layer-only update adds no clock that survives closing", () =>
                Require(!Field<DispatcherTimer>(dock, "hover").IsEnabled && !Field<DispatcherTimer>(dock, "hideDelay").IsEnabled,
                    "Closing the normal-layer Dock retained a timer."));
        }
        finally { cover.Close(); if (dock.IsVisible) dock.Close(); await Frame(); }
    }
    private static bool IsTopmost(IntPtr handle) => (GetWindowLongPtr(handle, -20).ToInt64() & 8) != 0;
    private static bool IsAbove(IntPtr upper, IntPtr lower)
    {
        var seen = new HashSet<IntPtr>();
        for (var window = GetTopWindow(IntPtr.Zero); window != IntPtr.Zero && seen.Add(window); window = GetWindow(window, 2))
        {
            if (window == upper) return true;
            if (window == lower) return false;
        }
        return false;
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
    private static FrameworkElement CreateReflection(string position, double strength)
    {
        var type = typeof(DockWindow).Assembly.GetType("LumaDock.App.DockReflection")!;
        var reflection = (FrameworkElement)Activator.CreateInstance(type)!;
        Call(reflection, "Configure", position, 88d, 160d, 70d, 64d, 64d, 64d, strength);
        return reflection;
    }
    private static ImageSource EnhanceColor(ImageSource source, double amount) => (ImageSource)typeof(DockWindow).Assembly
        .GetType("LumaDock.App.DockIconColor")!.GetMethod("Enhance", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [source, amount])!;
    private static BitmapSource ColorSample()
    {
        var source = BitmapSource.Create(3, 2, 144, 120, PixelFormats.Bgra32, null, new byte[]
        {
            90, 120, 180, 255, 96, 96, 96, 255, 160, 80, 40, 128,
            17, 9, 4, 0, 32, 64, 96, 64, 0, 0, 0, 255
        }, 12);
        source.Freeze(); return source;
    }
    private static byte[] IconPixels(BitmapSource source)
    {
        BitmapSource straight = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[straight.PixelWidth * straight.PixelHeight * 4]; straight.CopyPixels(pixels, straight.PixelWidth * 4, 0); return pixels;
    }
    private static void SetReflectionSource(FrameworkElement reflection, ImageSource? source) => reflection.GetType().GetProperty("Source")!.SetValue(reflection, source);
    private static BitmapSource ColorIcon()
    {
        var pixels = new byte[64 * 64 * 4];
        for (var y = 0; y < 64; y++) for (var x = 0; x < 64; x++)
        {
            var color = (x < 32, y < 32) switch { (true, true) => Colors.Red, (false, true) => Colors.Lime, (true, false) => Colors.Blue, _ => Colors.Yellow };
            var i = (y * 64 + x) * 4; pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255;
        }
        var image = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4); image.Freeze(); return image;
    }
    private static byte[] RenderPixels(FrameworkElement element)
    {
        element.Measure(new Size(element.Width, element.Height)); element.Arrange(new Rect(0, 0, element.Width, element.Height)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.Width), (int)Math.Ceiling(element.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels;
    }
    private static void CheckReflectionPixels(string position)
    {
        var reflection = CreateReflection(position, .4); SetReflectionSource(reflection, ColorIcon());
        var pixels = RenderPixels(reflection); var width = (int)reflection.Width;
        var outerEnd = position is "Bottom" or "Right"; var vertical = position is "Left" or "Right";
        var plane = 70 + (outerEnd ? 33.5 : -33.5);
        var cross = (int)Math.Round(plane + (outerEnd ? 5 : -5));
        var point = vertical ? new Point(cross, 29) : new Point(29, cross);
        var index = ((int)point.Y * width + (int)point.X) * 4;
        var expected = position switch { "Bottom" => Colors.Blue, "Top" or "Left" => Colors.Red, _ => Colors.Lime };
        Require(pixels[index + 3] > 10, "Mirror has no visible real source pixels.");
        Require(expected.B == 0 ? pixels[index] < 3 : pixels[index] > 10, "Mirrored blue channel does not match the outer source edge.");
        Require(expected.G == 0 ? pixels[index + 1] < 3 : pixels[index + 1] > 10, "Mirrored green channel does not match the outer source edge.");
        Require(expected.R == 0 ? pixels[index + 2] < 3 : pixels[index + 2] > 10, "Mirrored red channel does not match the outer source edge.");
        var far = (int)Math.Round(plane + (outerEnd ? 18 : -18));
        var farIndex = vertical ? (29 * width + far) * 4 : (far * width + 29) * 4;
        Require(pixels[farIndex + 3] < pixels[index + 3] / 2, "The reflection does not fade away from its glass plane.");
    }
    private static void CheckReflectionBounds(DockWindow dock)
    {
        foreach (var tile in Tiles(dock))
        {
            var reflection = Field<FrameworkElement>(tile, "Reflection");
            var type = reflection.GetType();
            var shelf = (Rect)type.GetProperty("ShelfBounds", Private)!.GetValue(reflection)!;
            var mirror = (Rect)type.GetProperty("MirroredImageBounds", Private)!.GetValue(reflection)!;
            mirror.Intersect(shelf);
            if (mirror.IsEmpty) continue;
            var bounds = reflection.TransformToAncestor(dock).TransformBounds(mirror);
            Require(bounds.Left >= -.5 && bounds.Top >= -.5 && bounds.Right <= dock.ActualWidth + .5 && bounds.Bottom <= dock.ActualHeight + .5, "A reflected icon leaves the Dock window.");
        }
    }
    private static object Snapshot(DockWindow dock, string shape, string position) => new { shape, position, width = dock.ActualWidth, height = dock.ActualHeight, scales = Tiles(dock).Select(t => Field<ScaleTransform>(t, "Scale").ScaleX).ToArray(), timerRunning = Field<DispatcherTimer>(dock, "hover").IsEnabled };
    private static async Task Frame() { await Dispatcher.Yield(DispatcherPriority.ContextIdle); await Task.Delay(70); await Dispatcher.Yield(DispatcherPriority.ContextIdle); }
    private static async Task Settle() { await Task.Delay(600); await Dispatcher.Yield(DispatcherPriority.ContextIdle); }
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr hwnd, bool enabled);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(IntPtr drop, uint index, System.Text.StringBuilder? path, uint size);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref System.Runtime.InteropServices.ComTypes.STGMEDIUM medium);
}
