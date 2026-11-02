using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LumaDock.App;
using LumaDock.Storage;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [STAThread] private static int Main(string[] args)
    {
        var report = Path.GetFullPath(args.Length > 1 ? args[1] : "artifacts/tests/system-ui-0.12.0.json");
        var checks = new List<string>(); var errors = new List<string>();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; var exit = 1;
        app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception.ToString()); e.Handled = true; };
        app.Startup += async (_, _) =>
        {
            AppController? c = null;
            try
            {
                var profile = Path.GetFullPath(Path.Combine("work", "system-ui", Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(profile); Log.DirectoryPath = Path.Combine(profile, "logs");
                c = new AppController(profile, null); c.State.Settings.ShowRunningApps = false;
                foreach (var position in new[] { "Bottom", "Top", "Left", "Right" })
                {
                    c.State.Settings.Position = position;
                    foreach (var size in new[] { 12d, 24d })
                    {
                        c.State.Settings.DockStatusIconSize = size; c.State.Settings.DockStatusFontSize = size == 12 ? 10 : 18; c.State.Settings.DockStatusFontWeight = 800;
                        var strip = new DockSystemStrip(c, position is "Left" or "Right");
                        var window = new Window { Content = strip, SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false, ShowActivated = false };
                        window.Show(); await Frame();
                        Require(strip.ActualWidth > 40 && strip.ActualHeight > 40 && double.IsFinite(strip.AlongLength), "Strip has invalid layout");
                        Require(Descendants(strip).OfType<Button>().Count() == 5, "System regions were split or lost");
                        Require(Descendants(strip).OfType<TextBlock>().Where(x => x.Text == "—" || x.Text.Contains(':')).All(x => x.FontWeight.ToOpenTypeWeight() == 800), "Custom weight ignored");
                        strip.SetVisible(false); Require(!Timer(strip, "clock").IsEnabled, "Hidden clock still ticking");
                        strip.SetVisible(true); Require(Timer(strip, "clock").IsEnabled, "Visible clock not scheduled");
                        strip.Dispose(); Require(!Timer(strip, "clock").IsEnabled, "Disposed clock still ticking"); window.Close(); checks.Add(position + " size " + size + " layout, regions, weight, timer lifecycle");
                    }
                }
                c.State.Settings.Position = "Bottom";
                var dock = new DockWindow(c); dock.Show(); await Frame();
                var system = (DockSystemStrip)typeof(DockWindow).GetField("systemStrip", Private)!.GetValue(dock)!;
                var open = typeof(DockSystemStrip).GetMethod("OpenPanel", Private)!;
                foreach (var kind in new[] { "Quick", "Keyboard", "Calendar" })
                {
                    open.Invoke(system, [kind]); await Frame();
                    Require(system.IsInteracting, "Panel state lost");
                    var panel = Application.Current.Windows.Cast<Window>().Single(x => x.Title.StartsWith("LumaDock · "));
                    Require(!panel.AllowsTransparency && !panel.Topmost, "Flyout must use normal native composition");
                    Require(panel.ActualWidth >= 390 && panel.ActualHeight > 100 && panel.ActualHeight <= panel.MaxHeight + 1, "Flyout bounds invalid");
                    if (kind == "Quick") Require(Descendants(panel).OfType<Slider>().Count() == 2, "Brightness or volume slider missing");
                    if (kind == "Calendar") Require(Descendants(panel).OfType<UniformGrid>().Any(x => x.Children.Count == 49), "Calendar grid incomplete");
                    system.ClosePanel(); await Frame(); Require(!system.IsInteracting, "Panel did not close"); checks.Add(kind + " real panel, composition, bounds and lifecycle");
                }
                for (var i = 0; i < 30; i++) { dock.Rebuild(); await Frame(); } checks.Add("30 Dock rebuilds with persistent native service context");
                c.State.Settings.DockSystemEnabled = false; dock.Rebuild(); await Frame(); Require(typeof(DockWindow).GetField("systemStrip", Private)!.GetValue(dock) == null, "System region did not disable"); checks.Add("System region preference actually disables UI");
                Require(typeof(AppController).GetField("systemContext", Private)!.GetValue(c) == null, "Disabled system services stayed subscribed"); checks.Add("Disabling system region releases native subscriptions");
                dock.Close(); c.Dispose(); c = null; exit = errors.Count == 0 ? 0 : 1;
            }
            catch (Exception e) { errors.Add(e.ToString()); }
            finally { c?.Dispose(); Directory.CreateDirectory(Path.GetDirectoryName(report)!); File.WriteAllText(report, JsonSerializer.Serialize(new { passed = exit == 0, checks, errors }, new JsonSerializerOptions { WriteIndented = true })); Console.WriteLine($"System UI: {checks.Count} groups, {errors.Count} errors; {report}"); foreach (var error in errors) Console.WriteLine(error); app.Shutdown(); }
        }; app.Run(); return exit;
    }
    private static DispatcherTimer Timer(object o, string name) => (DispatcherTimer)o.GetType().GetField(name, Private)!.GetValue(o)!;
    private static async Task Frame() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(35); }
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root) { yield return root; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child; }
}
