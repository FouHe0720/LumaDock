using System.IO;
using System.Windows;
using LumaDock.App;
using LumaDock.Core;
using LumaDock.Storage;

internal static class ControllerPinChecks
{
    // Optional local integration path. Unlike the default CI projection checks,
    // constructing the controller also reads Windows font metrics. It never calls
    // Start, shows a window, registers a hotkey, discovers apps or launches a target.
    internal static void Run(Action<string, Action> test, Action<bool, string> require)
    {
        Exception? threadError = null;
        var thread = new Thread(() =>
        {
            Application? application = null;
            try
            {
                application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var directory = Path.GetFullPath(Path.Combine("work", "running-pin-checks", Guid.NewGuid().ToString("N")));
                Log.DirectoryPath = Path.Combine(directory, "logs");
                using var controller = new AppController(directory, null);
                const string identity = "Fixture.Package!Editor";
                var running = new AppEntry { Id = "running:fixture", Name = "Fixture packaged editor", Source = "Running",
                    LaunchPath = "shell:AppsFolder\\" + identity, AppUserModelId = identity, IsAvailable = true };
                test("The real controller persists an inaccessible packaged task by Shell namespace without launching it", () =>
                {
                    controller.PinRunning(running);
                    var stored = new StateStore(directory).Load();
                    var saved = stored.Apps.Single(entry => entry.AppUserModelId == identity);
                    require(saved.Source == "Manual" && saved.LaunchPath == running.LaunchPath && saved.ExecutablePath.Length == 0,
                        "Packaged task lost its usable namespace or was rejected as a nonexistent file");
                    require(stored.PinnedIds.SequenceEqual([saved.Id]) && saved.Id != running.Id, "A transient identity leaked into persisted pins");
                    require(running.Source == "Running" && running.Id == "running:fixture", "Pinning rewrote the source window projection");
                });
                test("Pinning the same packaged running task twice preserves one pin, user order and grouping", () =>
                {
                    var app = controller.State.Apps.Single(entry => entry.AppUserModelId == identity);
                    app.Category = "Fixture column"; app.CategoryIsUserAssigned = true;
                    controller.State.Categories.Add(app.Category);
                    controller.PinRunning(running);
                    var stored = new StateStore(directory).Load();
                    require(stored.Apps.Count(entry => entry.AppUserModelId == identity) == 1 && stored.PinnedIds.SequenceEqual([app.Id]),
                        "Repeated package pin duplicated a record or changed pin order");
                    require(stored.Apps.Single(entry => entry.Id == app.Id).Category == "Fixture column", "Repeated pin discarded the user column");
                });
            }
            catch (Exception error) { threadError = error; }
            finally { application?.Shutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15)))
        { test("Controller integration fixture completes within its local time budget", () => throw new TimeoutException("Controller fixture exceeded 15 seconds")); return; }
        if (threadError != null) test("Controller integration fixture initializes without a desktop window", () => throw new InvalidOperationException("Controller fixture failed", threadError));
    }
}
