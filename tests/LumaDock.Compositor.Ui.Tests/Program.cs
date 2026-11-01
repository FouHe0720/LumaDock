using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using LumaDock.App;
using LumaDock.App.Center;
using LumaDock.Core;
using LumaDock.Storage;
using LumaDock.Windows;

namespace LumaDock.Compositor.Ui.Tests;

/// <summary>Real WPF composition contracts; changes only isolated settings and temporary own-process windows.</summary>
internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> Results = [];
    private static readonly List<object> ColorSteps = [];
    private static readonly List<object> MainColorSteps = [];
    private static readonly List<string> Errors = [];
    private static object? nativeClearProbe;
    private static object? mainCompositionDetails;
    private static object? mainCaptionDefaultProbe;
    private static int failed;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 1 && args[0] == "--underlay") return Underlay(args[1]);
        var report = Path.GetFullPath(Argument(args, "--report") ?? "artifacts/tests/compositor-ui-results.json");
        var profile = Path.GetFullPath(Path.Combine("work", "compositor-ui-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(profile); Log.DirectoryPath = Path.Combine(profile, "logs");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.DispatcherUnhandledException += (_, e) => { if (Errors.Count < 10) { Errors.Add(e.Exception.ToString()); Console.WriteLine(e.Exception); } e.Handled = true; };
        var exit = 1;
        application.Startup += async (_, _) =>
        {
            void Stage(string stage) { File.AppendAllText(Path.Combine(profile, "progress.txt"), stage + Environment.NewLine); Console.WriteLine(stage); }
            AppController? controller = null;
            ControlCenterWindow? center = null;
            DockWindow? dock = null;
            Process? underlay = null;
            var timer = Stopwatch.StartNew();
            try
            {
                Stage("Create controller"); controller = new AppController(profile, null);
                controller.State.Settings.Theme = "Dark";
                controller.State.Settings.LiveGlass = true;
                controller.State.Settings.LiquidGlass = true;
                controller.State.Settings.PowerSaver = false;
                controller.State.Settings.Animations = false;
                controller.State.Settings.Position = "Bottom";
                controller.State.Settings.AutoHide = false;
                controller.State.Apps = Enumerable.Range(0, 3).Select(i => new AppEntry
                {
                    Id = "compositor-fixture-" + i,
                    Name = "Composition check " + i,
                    LaunchPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")
                }).ToList();
                controller.State.PinnedIds = controller.State.Apps.Select(a => a.Id).ToList();
                Stage("Apply isolated settings"); controller.ApplySettings();
                Stage("Construct center"); center = new ControlCenterWindow(controller) { ShowActivated = false };
                center.SourceInitialized += (_, _) => EnableWindow(new WindowInteropHelper(center).Handle, false);
                Stage("Show center"); center.Show(); Stage("Wait for center frame"); await Frame(); Stage("Center frame ready");
                var surface = Field<object>(center, "_liquid");
                Check("Realtime main window keeps native non-layered composition and no captured frame", () =>
                {
                    Require(!center.AllowsTransparency, "Main window became layered.");
                    NoSnapshotWork(surface);
                    var kind = Property<string>(surface, "SourceKind");
                    Require(kind is "Native compositor" or "System fallback", "Realtime source reports a sampled frame: " + kind);
                });
                var initialStatus = WindowBackdrop.GetStatus(center);
                Check("The first clear frame has its real translucent tint without claiming Acrylic", () =>
                {
                    Require(!initialStatus.AcrylicActive, "Default clear glass is reported as Acrylic.");
                    if (initialStatus.ClearActive)
                    {
                        Require(initialStatus.AppliedBackdrop == 1, "Clear composition did not select backdrop NONE.");
                        var ncResult = DwmGetWindowAttribute(new WindowInteropHelper(center).Handle, 1, out var ncEnabled, sizeof(int));
                        Require(ncResult == 0 && ncEnabled == 0, "Clear composition retained native non-client rendering.");
                        var fill = Field<Border>(center, "_shell").Background as SolidColorBrush;
                        Require(fill?.Color.A == (byte)Math.Round(controller.State.Settings.BackdropOpacity * 255), "Initial clear tint did not reach the actual window.");
                    }
                    else Require(center.Background is SolidColorBrush brush && brush.Color.A == 255, "System fallback is not opaque.");
                });
                if (!args.Contains("--fast-probe"))
                {
                var originalWidth = center.Width; var originalHeight = center.Height;
                center.Width += 32; center.Height += 24; await Frame();
                var resizedClear = HasExpectedLiveState(center, surface);
                center.WindowState = WindowState.Maximized; await Frame();
                var maximizedClear = HasExpectedLiveState(center, surface);
                center.WindowState = WindowState.Normal; center.Width = originalWidth; center.Height = originalHeight; await Frame();
                Check("Resize, maximize and restore retain compositor transparency without snapshot work", () =>
                {
                    Require(resizedClear && maximizedClear && HasExpectedLiveState(center, surface), "A size transition restored a captured image or native non-client fill.");
                    Require(center.ResizeMode == ResizeMode.CanResize, "Clear mode removed window resizing.");
                });
                controller.State.Settings.Theme = "Light"; controller.ApplySettings(); await Frame();
                Check("Changing theme preserves realtime composition without a stale snapshot", () =>
                {
                    NoSnapshotWork(surface);
                    if (WindowBackdrop.GetStatus(center).ClearActive)
                    {
                        var ncResult = DwmGetWindowAttribute(new WindowInteropHelper(center).Handle, 1, out var ncEnabled, sizeof(int));
                        Require(ncResult == 0 && ncEnabled == 0, "Theme change restored native non-client rendering.");
                    }
                });
                center.Hide(); await Frame();
                Check("Hiding the main window releases all snapshot work", () => NoSnapshotWork(surface));
                // Maximizing/restoring earlier in this test may change activation even
                // with ShowActivated=false. Establish an explicit, own-process focus
                // anchor before testing a non-activating show; never activate user apps.
                var focusAnchor = new Window { Title = "LumaDock composition focus fixture", Width = 48, Height = 48,
                    Left = SystemParameters.WorkArea.Right - 60, Top = SystemParameters.WorkArea.Top + 12,
                    ShowInTaskbar = false, WindowStyle = WindowStyle.ToolWindow };
                try
                {
                    focusAnchor.Show(); focusAnchor.Activate(); await Frame();
                    Require(focusAnchor.IsActive, "Own-process focus fixture did not become active.");
                    center.Show(); await Frame();
                    Check("Showing an inactive main window does not freeze an old frame", () =>
                    {
                        Require(!center.IsActive && focusAnchor.IsActive, "Non-activating show changed fixture focus."); NoSnapshotWork(surface);
                    });
                }
                finally { focusAnchor.Close(); }
                controller.State.Settings.LiveGlassStyle = "Frosted"; controller.ApplySettings(); await Frame();
                Check("Frosted composition is distinct from clear composition and retains no captured frame", () =>
                {
                    var status = WindowBackdrop.GetStatus(center);
                    NoSnapshotWork(surface); Require(!status.ClearActive, "Frosted selection retained the clear flag.");
                    if (initialStatus.ClearActive) Require(status.AcrylicActive && status.AppliedBackdrop == 3, "Frosted selection did not reach native Acrylic.");
                    var ncResult = DwmGetWindowAttribute(new WindowInteropHelper(center).Handle, 1, out var ncEnabled, sizeof(int));
                    Require(ncResult == 0 && ncEnabled != 0, "Frosted composition did not restore native non-client rendering.");
                });

                controller.State.Settings.LiveGlass = false; controller.ApplySettings();
                var snapshotReady = await Until(() => NullableField(surface, "frame") != null, 4000);
                Check("Explicit static optics produce a one-shot frame on the test desktop", () =>
                {
                    Require(snapshotReady, "Static snapshot did not complete: " + Property<string>(surface, "SourceKind"));
                    Require(!SnapshotTimers(surface).Any(t => t.IsEnabled), "Static optics started a persistent sampling timer.");
                });
                controller.State.Settings.LiveGlass = true; controller.State.Settings.LiveGlassStyle = "Clear"; controller.ApplySettings(); await Frame();
                Check("Returning to realtime discards the previous static image", () => NoSnapshotWork(surface));
                controller.State.Settings.LiveGlass = false; controller.ApplySettings();
                await Task.Delay(15);
                controller.State.Settings.LiveGlass = true; controller.ApplySettings();
                await Task.Delay(300); await Frame();
                Check("A cancelled static request cannot restore an old frame after realtime is selected", () => NoSnapshotWork(surface));
                controller.State.Settings.PowerSaver = true; controller.ApplySettings(); await Frame();
                Check("Main-window power saving uses a real system fallback rather than a frozen capture", () =>
                {
                    NoSnapshotWork(surface);
                    Require(!WindowBackdrop.GetStatus(center).AcrylicActive, "Power saving retained the native effect.");
                });
                }
                center.ClosePermanently(); center = null; await Frame();
                Check("Closing the management surface leaves no snapshot dispatcher timer", () => NoSnapshotWork(surface));

                controller.State.Settings.PowerSaver = false;
                controller.State.Settings.Theme = "Dark";
                controller.State.Settings.LiveGlass = true;
                controller.ApplySettings();
                var readyPath = Path.Combine(profile, "underlay-ready.json");
                var start = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardInput = true, RedirectStandardOutput = true
                };
                if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(typeof(Program).Assembly.Location);
                start.ArgumentList.Add("--underlay"); start.ArgumentList.Add(readyPath);
                underlay = Process.Start(start) ?? throw new InvalidOperationException("Could not start known-color composition fixture.");
                Require(await Until(() => File.Exists(readyPath), 4000), "Underlay did not become ready.");
                var ready = JsonSerializer.Deserialize<Ready>(File.ReadAllText(readyPath))!;
                dock = new DockWindow(controller) { ShowActivated = false };
                dock.SourceInitialized += (_, _) => EnableWindow(new WindowInteropHelper(dock).Handle, false);
                dock.Show(); await Frame();
                var dockSurface = Field<object>(dock, "liquid");
                Check("Realtime Dock keeps a translucent plate with no background bitmap or sampling timer", () =>
                {
                    var plate = Field<object>(dock, "plate");
                    var fill = Property<Brush>(plate, "Fill") as SolidColorBrush;
                    Require(fill?.Color.A is > 0 and < 255, "Realtime Dock has an opaque plate.");
                    NoSnapshotWork(dockSurface);
                    Require(Property<string>(dockSurface, "SourceKind") == "Live transparency", "Dock reports the wrong source.");
                });
                var body = Field<Grid>(dock, "body");
                var inside = body.PointToScreen(new Point(body.ActualWidth / 2, body.ActualHeight * .90));
                var outside = new Point(ready.Left + 12, inside.Y);
                Check("Composition samples are bounded to the known test underlay", () =>
                {
                    Require(outside.Y > ready.Top && outside.Y < ready.Top + ready.Height, "Outside sample leaves the known fixture.");
                    Require(inside.X > ready.Left && inside.X < ready.Left + ready.Width && inside.Y > ready.Top && inside.Y < ready.Top + ready.Height, "Dock sample leaves the known fixture.");
                    Require(WindowFromPoint(new NativePoint((int)outside.X, (int)outside.Y)).ToInt64() == ready.Handle, "Another window obscures the outside fixture sample.");
                });
                var observedDock = await ObserveColors(underlay, inside, outside, () => !dock.IsActive);
                ColorSteps.AddRange(observedDock.Samples);
                Check("Ten changing background phases remain visible through an inactive realtime Dock", () =>
                {
                    Require(!dock.IsActive, "Dock stole foreground focus.");
                    Require(observedDock.Matched == 10, $"Only {observedDock.Matched}/10 background changes were reflected by the Dock.");
                    NoSnapshotWork(dockSurface);
                });
                dock.Hide(); await Frame();
                var scale = VisualTreeHelper.GetDpi(dock).DpiScaleX;
                center = new ControlCenterWindow(controller)
                {
                    ShowActivated = false, ShowInTaskbar = false, Topmost = true,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Width = 900, Height = 600, Left = (ready.Left + 35) / scale, Top = ready.Top / scale - 350
                };
                center.Show(); await Frame();
                var liveMain = Field<object>(center, "_liquid");
                var mainInside = center.PointToScreen(new Point(center.ActualWidth * .75, center.ActualHeight - 85));
                var mainOutside = new Point(ready.Left + 12, mainInside.Y);
                var mainStatus = WindowBackdrop.GetStatus(center);
                var mainNcResult = DwmGetWindowAttribute(new WindowInteropHelper(center).Handle, 1, out var mainNcEnabled, sizeof(int));
                var diagnosticBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)center.ActualWidth, (int)center.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                diagnosticBitmap.Render(center); var diagnosticPixel = new byte[4];
                diagnosticBitmap.CopyPixels(new Int32Rect((int)(center.ActualWidth * .75), (int)center.ActualHeight - 85, 1, 1), diagnosticPixel, 4, 0);
                GetWindowRect(new WindowInteropHelper(center).Handle, out var actualMainRect);
                center.Hide(); await Frame();
                var uncovered = await ObserveColors(underlay, mainInside, mainOutside, () => true);
                var uncoveredWindow = WindowFromPoint(new NativePoint((int)mainInside.X, (int)mainInside.Y));
                center.Show(); await Frame();
                mainCompositionDetails = new
                {
                    status = mainStatus, nonClientQueryHResult = mainNcResult, nonClientRenderingEnabled = mainNcEnabled != 0,
                    windowBackground = (center.Background as SolidColorBrush)?.Color.ToString(),
                    targetBackground = HwndSource.FromHwnd(new WindowInteropHelper(center).Handle)!.CompositionTarget.BackgroundColor.ToString(),
                    shellBackground = (Field<Border>(center, "_shell").Background as SolidColorBrush)?.Color.ToString(),
                    wpfSampleBgra = diagnosticPixel, disabledNativeInput = false,
                    nativeStyle = GetWindowLongPtr(new WindowInteropHelper(center).Handle, -16).ToInt64().ToString("X"),
                    underlayRect = ready, mainRect = new { actualMainRect.Left, actualMainRect.Top, actualMainRect.Right, actualMainRect.Bottom },
                    sample = new { mainInside.X, mainInside.Y }, uncoveredMatchingPhases = uncovered.Matched,
                    uncoveredWindowIsUnderlay = uncoveredWindow.ToInt64() == ready.Handle
                };
                Check("The actual main-window sample is bounded to the known test background", () =>
                {
                    Require(mainInside.X > ready.Left && mainInside.X < ready.Left + ready.Width && mainInside.Y > ready.Top && mainInside.Y < ready.Top + ready.Height, "Main-window sample leaves the known fixture.");
                    Require(WindowFromPoint(new NativePoint((int)mainOutside.X, (int)mainOutside.Y)).ToInt64() == ready.Handle, "Another window obscures the main outside sample.");
                    Require(!center.AllowsTransparency, "The production main window became layered."); NoSnapshotWork(liveMain);
                    Require(uncovered.Matched == 10 && uncoveredWindow.ToInt64() == ready.Handle, "The hidden main window's inner sample does not see the real fixture.");
                });
                if (mainStatus.ClearActive)
                {
                    var observedMain = await ObserveColors(underlay, mainInside, mainOutside, () => !center.IsActive);
                    MainColorSteps.AddRange(observedMain.Samples);
                    Check("Ten changing phases remain visible through the actual inactive clear management window", () =>
                    {
                        Require(!center.IsActive, "The composition test activated the management window.");
                        Require(observedMain.Matched == 10, $"Only {observedMain.Matched}/10 known phases reached the actual management surface.");
                        Require(mainStatus.AppliedBackdrop == 1 && !mainStatus.AcrylicActive, "The main surface reports a different native mode.");
                        NoSnapshotWork(liveMain);
                    });
                    if (args.Contains("--probe-native-clear"))
                    {
                        var defaultCaption = unchecked((int)0xFFFFFFFF);
                        var captionResult = DwmSetWindowAttribute(new WindowInteropHelper(center).Handle, 35, ref defaultCaption, sizeof(int));
                        var margins = new Margins(-1);
                        var extendResult = DwmExtendFrameIntoClientArea(new WindowInteropHelper(center).Handle, ref margins);
                        await Frame();
                        var captionSteps = await ObserveColors(underlay, mainInside, mainOutside, () => !center.IsActive);
                        Console.WriteLine($"EXPERIMENT Main caption COLOR_DEFAULT: {captionSteps.Matched}/10 phases follow");
                        var handle = new WindowInteropHelper(center).Handle;
                        var oldStyle = GetWindowLongPtr(handle, -16).ToInt64();
                        SetWindowLongPtr(handle, -16, new IntPtr(oldStyle & ~0x00C00000L));
                        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x0037);
                        margins = new Margins(-1); var noCaptionExtendResult = DwmExtendFrameIntoClientArea(handle, ref margins);
                        await Frame();
                        var noCaptionSteps = await ObserveColors(underlay, mainInside, mainOutside, () => !center.IsActive);
                        var disabledPolicy = 1;
                        var initialPolicy = 0; // DWMNCRP_USEWINDOWSTYLE; POLICY is a set-only attribute.
                        var ncPolicyResult = DwmSetWindowAttribute(handle, 2, ref disabledPolicy, sizeof(int));
                        margins = new Margins(-1); DwmExtendFrameIntoClientArea(handle, ref margins); await Frame();
                        var ncSteps = await ObserveColors(underlay, mainInside, mainOutside, () => !center.IsActive);
                        Console.WriteLine($"EXPERIMENT Main NC rendering disabled: {ncSteps.Matched}/10 phases follow");
                        DwmSetWindowAttribute(handle, 2, ref initialPolicy, sizeof(int));
                        center.ResizeMode = ResizeMode.NoResize;
                        margins = new Margins(-1); DwmExtendFrameIntoClientArea(handle, ref margins); await Frame();
                        var noResizeSteps = await ObserveColors(underlay, mainInside, mainOutside, () => !center.IsActive);
                        Console.WriteLine($"EXPERIMENT Main NoResize control: {noResizeSteps.Matched}/10 phases follow");
                        mainCaptionDefaultProbe = new { captionResult, extendResult, matchingPhases = captionSteps.Matched,
                            samples = captionSteps.Samples, noCaptionExtendResult, noCaptionMatchingPhases = noCaptionSteps.Matched,
                            styleBefore = oldStyle.ToString("X"), styleAfter = GetWindowLongPtr(handle, -16).ToInt64().ToString("X"), noCaptionSamples = noCaptionSteps.Samples,
                            initialPolicy, ncPolicyResult, ncDisabledMatchingPhases = ncSteps.Matched, ncDisabledSamples = ncSteps.Samples,
                            noResizeMatchingPhases = noResizeSteps.Matched, noResizeSamples = noResizeSteps.Samples,
                            scope = "Temporary own-window capability experiment; production composition phases above were measured before these overrides." };
                        Console.WriteLine($"EXPERIMENT Main without WS_CAPTION: {noCaptionSteps.Matched}/10 phases follow");
                    }
                }
                else MainColorSteps.Add(new { skipped = true, reason = mainStatus.Reason, interpretation = "System fallback has no clear transparency to sample." });
                center.ClosePermanently(); center = null; dock.Show(); await Frame();
                if (args.Contains("--probe-native-clear"))
                    nativeClearProbe = await ProbeNativeClear(underlay, ready);
                controller.State.Settings.Animations = false; dock.UpdateSettings(); await Frame();
                Check("Disabled icon animation does not restore a stale background snapshot", () => NoSnapshotWork(dockSurface));
                controller.State.Settings.PowerSaver = true; dock.UpdateSettings(); await Frame();
                Check("Dock power saving restores an opaque readable surface with no snapshot work", () =>
                {
                    Require((Property<Brush>(Field<object>(dock, "plate"), "Fill") as SolidColorBrush)?.Color.A == 255, "Power-saving plate is translucent.");
                    NoSnapshotWork(dockSurface);
                });
                dock.Close(); dock = null; await Frame();
                Check("Closing the Dock cancels its background work", () => NoSnapshotWork(dockSurface));
                exit = failed == 0 && Errors.Count == 0 ? 0 : 1;
            }
            catch (Exception ex) { Errors.Add(ex.ToString()); }
            finally
            {
                center?.ClosePermanently(); dock?.Close(); controller?.Dispose();
                if (underlay != null)
                {
                    try { underlay.StandardInput.Close(); if (!underlay.WaitForExit(2000)) underlay.Kill(); } catch (InvalidOperationException) { }
                    underlay.Dispose();
                }
                timer.Stop();
                Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                File.WriteAllText(report, JsonSerializer.Serialize(new
                {
                    passed = exit == 0, recordedAtUtc = DateTimeOffset.UtcNow, platform = Environment.OSVersion.VersionString,
                    durationMs = timer.Elapsed.TotalMilliseconds, count = Results.Count, failed, results = Results,
                    colorSteps = ColorSteps, mainColorSteps = MainColorSteps, mainCompositionDetails, mainCaptionDefaultProbe, nativeClearProbe, errors = Errors, isolatedProfile = true, controllerStartCalled = false,
                    systemSettingsChanged = false, wallpaperSettingsChanged = false, screenPixelsSaved = false,
                    interpretation = "Known-color temporary windows exercise composition and absence of application-held backdrop snapshots. Color steps are observed after the test underlay acknowledges painting; these are not end-to-end wallpaper/GPU latency or frame-rate measurements."
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Compositor checks: {Results.Count - failed}/{Results.Count}, passed={exit == 0}; report: {report}");
                application.Shutdown(exit);
            }
        };
        application.Run(); return exit;
    }

    private static async Task<(List<object> Samples, int Matched)> ObserveColors(Process underlay, Point inside, Point outside, Func<bool> inactive)
    {
        var samples = new List<object>(); var count = 0;
        for (var phase = 0; phase < 10; phase++)
        {
            var blue = phase % 2 == 0;
            await underlay.StandardInput.WriteLineAsync(blue ? "24,60,220" : "220,50,24"); await underlay.StandardInput.FlushAsync();
            Require(await underlay.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)) == "painted", "Known fixture failed to acknowledge its own color change.");
            var watch = Stopwatch.StartNew(); var matched = false; var inColor = 0u; var outColor = 0u;
            do
            {
                await Task.Delay(8); DwmFlush(); var screen = GetDC(IntPtr.Zero);
                try { outColor = GetPixel(screen, (int)outside.X, (int)outside.Y); inColor = GetPixel(screen, (int)inside.X, (int)inside.Y); }
                finally { ReleaseDC(IntPtr.Zero, screen); }
                matched = Dominates(outColor, blue, 80) && Dominates(inColor, blue, 20);
            } while (!matched && watch.ElapsedMilliseconds < 160);
            if (matched) count++;
            samples.Add(new { phase, blue, matched, observedAfterAckMs = watch.Elapsed.TotalMilliseconds, outside = Rgb(outColor), inside = Rgb(inColor), inactive = inactive() });
        }
        return (samples, count);
    }

    // Optional capability experiment. Unsupported clear-alpha composition is an
    // honest negative result, not a failure of the production Acrylic fallback.
    private static async Task<object> ProbeNativeClear(Process underlay, Ready ready)
    {
        var window = new Window
        {
            Title = "LumaDock non-layered alpha capability fixture", WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, AllowsTransparency = false, ShowActivated = false, ShowInTaskbar = false,
            Background = Brushes.Transparent, Width = 300, Height = 180, Left = ready.Left + 35, Top = ready.Top + 25,
            Content = new Border { Background = new SolidColorBrush(Color.FromArgb(96, 32, 42, 55)) }, Topmost = true
        };
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = WindowChrome.GlassFrameCompleteThickness,
            UseAeroCaptionButtons = false
        });
        var attributeResult = int.MinValue; var extensionResult = int.MinValue;
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            NativeWindowHelper.ConfigureDock(hwnd);
            HwndSource.FromHwnd(hwnd)!.CompositionTarget.BackgroundColor = Colors.Transparent;
            var noBackdrop = 1; attributeResult = DwmSetWindowAttribute(hwnd, 38, ref noBackdrop, sizeof(int));
            var margins = new Margins(-1); extensionResult = DwmExtendFrameIntoClientArea(hwnd, ref margins);
        };
        try
        {
            window.Show(); await Frame();
            var handle = new WindowInteropHelper(window).Handle;
            var isLayered = (GetWindowLongPtr(handle, -20).ToInt64() & 0x80000) != 0;
            DwmGetWindowAttribute(handle, 38, out var backdrop, sizeof(int));
            var targetAlpha = HwndSource.FromHwnd(handle)!.CompositionTarget.BackgroundColor.A;
            var inside = window.PointToScreen(new Point(window.ActualWidth / 2, window.ActualHeight / 2));
            var outside = new Point(ready.Left + 12, inside.Y);
            var samples = new List<object>(); var matched = 0;
            foreach (var blue in new[] { true, false, true, false })
            {
                await underlay.StandardInput.WriteLineAsync(blue ? "24,60,220" : "220,50,24");
                await underlay.StandardInput.FlushAsync();
                Require(await underlay.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)) == "painted", "Capability underlay acknowledgement failed.");
                await Task.Delay(70); DwmFlush();
                var screen = GetDC(IntPtr.Zero); uint inColor, outColor;
                try { inColor = GetPixel(screen, (int)inside.X, (int)inside.Y); outColor = GetPixel(screen, (int)outside.X, (int)outside.Y); }
                finally { ReleaseDC(IntPtr.Zero, screen); }
                var follows = Dominates(outColor, blue, 80) && Dominates(inColor, blue, 20);
                if (follows) matched++;
                samples.Add(new { blue, follows, outside = Rgb(outColor), inside = Rgb(inColor) });
            }
            var ncQueryResult = DwmGetWindowAttribute(handle, 1, out var ncEnabled, sizeof(int));
            var nativeStyle = GetWindowLongPtr(handle, -16).ToInt64().ToString("X");
            var supported = !isLayered && attributeResult == 0 && extensionResult == 0 && backdrop == 1 && targetAlpha == 0 && matched == 4;
            Console.WriteLine("EXPERIMENT Native clear-alpha without Acrylic: " + (supported ? "SUPPORTED on this desktop" : "NOT SUPPORTED on this desktop"));
            return new { supported, isLayered, attributeResult, extensionResult, backdrop, targetAlpha, nativeStyle, ncQueryResult, nonClientRenderingEnabled = ncEnabled != 0, matchingPhases = matched, totalPhases = 4, samples,
                scope = "This optional own-window capability probe does not change the production backdrop policy or assert compatibility with other Windows versions." };
        }
        finally { window.Close(); }
    }

    private static int Underlay(string readyPath)
    {
        var application = new Application();
        var monitor = MonitorService.GetMonitors().First(m => m.IsPrimary);
        var width = Math.Min(900, monitor.WorkWidth - 30);
        var height = Math.Min(280, monitor.WorkHeight - 30);
        var window = new Window
        {
            Title = "LumaDock known-color composition fixture", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowActivated = false, ShowInTaskbar = false, Topmost = true,
            Left = monitor.WorkLeft + (monitor.WorkWidth - width) / 2, Top = monitor.WorkTop + monitor.WorkHeight - height,
            Width = width, Height = height, Background = new SolidColorBrush(Color.FromRgb(24, 60, 220)),
            Content = new Border { Background = Brushes.Transparent }
        };
        window.SourceInitialized += (_, _) => NativeWindowHelper.ConfigureDock(new WindowInteropHelper(window).Handle);
        window.ContentRendered += async (_, _) =>
        {
            GetWindowRect(new WindowInteropHelper(window).Handle, out var rect);
            File.WriteAllText(readyPath, JsonSerializer.Serialize(new Ready(new WindowInteropHelper(window).Handle.ToInt64(), rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)));
            while (await Task.Run(Console.In.ReadLine) is { } line)
            {
                var channels = line.Split(',').Select(byte.Parse).ToArray();
                window.Background = new SolidColorBrush(Color.FromRgb(channels[0], channels[1], channels[2]));
                await Dispatcher.Yield(DispatcherPriority.ContextIdle); await Task.Delay(40); DwmFlush();
                await Console.Out.WriteLineAsync("painted"); await Console.Out.FlushAsync();
            }
            window.Close();
        };
        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(35) };
        watchdog.Tick += (_, _) => { watchdog.Stop(); window.Close(); }; watchdog.Start();
        application.Run(window); return 0;
    }

    private static bool Dominates(uint rgb, bool blue, int difference)
        => rgb != uint.MaxValue && (blue ? (int)((rgb >> 16) & 255) - (int)(rgb & 255) : (int)(rgb & 255) - (int)((rgb >> 16) & 255)) > difference;
    private static object Rgb(uint rgb) => new { r = rgb & 255, g = (rgb >> 8) & 255, b = (rgb >> 16) & 255 };
    private static T Field<T>(object instance, string name) => (T)(instance.GetType().GetField(name, Private)?.GetValue(instance) ?? throw new InvalidOperationException("Missing field: " + name));
    private static object? NullableField(object instance, string name) => instance.GetType().GetField(name, Private)?.GetValue(instance);
    private static T Property<T>(object instance, string name) => (T)(instance.GetType().GetProperty(name)?.GetValue(instance) ?? throw new InvalidOperationException("Missing property: " + name));
    private static IEnumerable<DispatcherTimer> SnapshotTimers(object surface) => surface.GetType().GetFields(Private).Select(f => f.GetValue(surface)).OfType<DispatcherTimer>();
    private static void NoSnapshotWork(object surface)
    {
        Require(NullableField(surface, "frame") == null, "A stale captured background frame remains visible.");
        Require(!SnapshotTimers(surface).Any(t => t.IsEnabled), "A snapshot dispatcher timer remains running.");
        Require(NullableField(surface, "pending") == null, "A cancelled snapshot request is retained.");
    }
    private static bool HasExpectedLiveState(ControlCenterWindow window, object surface)
    {
        NoSnapshotWork(surface);
        if (!WindowBackdrop.GetStatus(window).ClearActive) return true;
        var result = DwmGetWindowAttribute(new WindowInteropHelper(window).Handle, 1, out var enabled, sizeof(int));
        return result == 0 && enabled == 0;
    }
    private static async Task<bool> Until(Func<bool> condition, int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < milliseconds) await Task.Delay(25);
        return condition();
    }
    private static async Task Frame()
    {
        await Task.Delay(120);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task.WaitAsync(TimeSpan.FromSeconds(3));
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Check(string name, Action action)
    {
        try { action(); Results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Results.Add(new { name, passed = false, error = ex.Message }); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    private static string? Argument(string[] args, string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    private sealed record Ready(long Handle, int Left, int Top, int Width, int Height);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Margins(int all) { public int Left = all, Right = all, Top = all, Bottom = all; }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr hwnd, bool enabled);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
