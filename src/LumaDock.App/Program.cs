using System.Diagnostics;
using System.Threading;
using System.Windows;
using LumaDock.Storage;

namespace LumaDock.App;

public static class Program
{
    internal static readonly Stopwatch Clock = Stopwatch.StartNew();
    static Program() { } // initialize before Main, rather than lazily at the first measurement
    [STAThread]
    public static void Main(string[] args)
    {
        var data = Argument(args, "--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaDock");
        Log.DirectoryPath = Path.Combine(data, "logs");
        using var single = new SingleInstance(data);
        if (!single.IsPrimary)
        {
            if (!single.RequestActivationAsync().GetAwaiter().GetResult())
                MessageBox.Show("LumaDock 正在启动，请稍后再试。", "LumaDock");
            return;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppController? controller = null;
        var ready = false;
        var activationPending = false;
        single.StartListener(() =>
        {
            if (app.Dispatcher.HasShutdownStarted) return;
            app.Dispatcher.BeginInvoke(() =>
            {
                if (ready && controller?.IsDisposed == false) controller.RevealCenter();
                else activationPending = true;
            });
        });
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Write("UI error", e.Exception); e.Handled = true;
            MessageBox.Show("操作未完成：" + e.Exception.Message + "\n详情已写入本地日志。", "LumaDock");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Fatal error", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Write("Background error", e.Exception); e.SetObserved(); };
        app.Startup += async (_, _) =>
        {
            try
            {
                controller = new AppController(data, Argument(args, "--diagnostics-dir"));
                controller.Start();
                await controller.RefreshAppsAsync();
                if (controller.IsDisposed) return;
                ready = true;
                if (activationPending || args.Contains("--center") || args.Contains("--show-launchpad")) controller.RevealCenter();
                if (args.Contains("--smoke-test"))
                {
                    if (!args.Contains("--center") && !args.Contains("--show-launchpad")) controller.RevealCenter();
                    await Task.Delay(8000); controller.Exit();
                }
            }
            catch (Exception ex) { Log.Write("Startup failed", ex); MessageBox.Show("无法启动 LumaDock：" + ex.Message, "LumaDock"); app.Shutdown(1); }
        };
        app.Exit += (_, _) => { single.Dispose(); controller?.Dispose(); };
        app.Run();
    }
    internal static string? Argument(string[] args, string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
}
