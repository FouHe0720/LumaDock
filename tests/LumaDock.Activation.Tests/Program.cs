using System.Diagnostics;
using System.Text.Json;
using LumaDock.App;
using LumaDock.Storage;

if (args.Length == 2 && args[0] == "--activation-client")
{
    Log.DirectoryPath = Path.Combine(args[1], "logs");
    using var client = new SingleInstance(args[1]);
    if (client.IsPrimary) return 3;
    return client.RequestActivationAsync().GetAwaiter().GetResult() ? 0 : 2;
}

var root = Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../work/activation-tests")), Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Log.DirectoryPath = Path.Combine(root, "logs");
var results = new List<object>();
var failed = 0;
void Assert(bool condition, string reason) { if (!condition) throw new Exception(reason); }
void Test(string name, Action action)
{
    var time = Stopwatch.StartNew();
    try { action(); results.Add(new { name, passed = true, elapsedMs = time.ElapsedMilliseconds }); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; results.Add(new { name, passed = false, elapsedMs = time.ElapsedMilliseconds, failure = ex.Message }); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
// A separate thread represents the second process for the thread-owned Mutex.
Task<T> OtherThread<T>(Func<T> action)
{
    var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    new Thread(() => { try { completion.SetResult(action()); } catch (Exception ex) { completion.SetException(ex); } }) { IsBackground = true }.Start();
    return completion.Task;
}

Test("A second Windows process activates the resident over a same-user pipe", () =>
{
    var data = Path.Combine(root, "cross-process");
    using var primary = new SingleInstance(data);
    var calls = 0; primary.StartListener(() => Interlocked.Increment(ref calls));
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
    if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--activation-client"); start.ArgumentList.Add(data);
    using var child = Process.Start(start) ?? throw new Exception("Client process did not start.");
    if (!child.WaitForExit(5000)) { child.Kill(); throw new Exception("Client process did not exit."); }
    Assert(child.ExitCode == 0 && calls == 1, "Cross-process activation failed or was duplicated.");
});

Test("Second instance sends exactly one activation and receives an acknowledgement", () =>
{
    var data = Path.Combine(root, "activation");
    using var primary = new SingleInstance(data);
    Assert(primary.IsPrimary, "First instance did not own its mutex.");
    var activations = 0;
    primary.StartListener(() => Interlocked.Increment(ref activations));
    for (var i = 0; i < 5; i++)
    {
        var delivered = OtherThread(() =>
        {
            using var secondary = new SingleInstance(data);
            Assert(!secondary.IsPrimary, "Duplicate acquired a resident instance mutex.");
            return secondary.RequestActivationAsync().GetAwaiter().GetResult();
        }).GetAwaiter().GetResult();
        Assert(delivered && activations == i + 1, "Activation was lost or duplicated.");
    }
});

Test("Second instance can connect while the resident process is still starting", () =>
{
    var data = Path.Combine(root, "starting");
    using var primary = new SingleInstance(data);
    var activations = 0;
    using var clientReady = new ManualResetEventSlim();
    var pending = OtherThread(() =>
    {
        using var secondary = new SingleInstance(data);
        clientReady.Set();
        return secondary.RequestActivationAsync().GetAwaiter().GetResult();
    });
    Assert(clientReady.Wait(TimeSpan.FromSeconds(1)), "Client did not start.");
    Thread.Sleep(70); // Deliberately model initialization before the pipe listener exists.
    primary.StartListener(() => Interlocked.Increment(ref activations));
    Assert(pending.GetAwaiter().GetResult() && activations == 1, "Startup activation did not wait for readiness.");
});

Test("Activation is isolated between independent data directories", () =>
{
    var firstData = Path.Combine(root, "first");
    using var first = new SingleInstance(firstData);
    using var second = new SingleInstance(Path.Combine(root, "second"));
    Assert(first.IsPrimary && second.IsPrimary, "Independent directories shared a mutex.");
    var firstCalls = 0; var secondCalls = 0;
    first.StartListener(() => Interlocked.Increment(ref firstCalls));
    second.StartListener(() => Interlocked.Increment(ref secondCalls));
    Assert(OtherThread(() => { using var client = new SingleInstance(firstData); return client.RequestActivationAsync().GetAwaiter().GetResult(); }).GetAwaiter().GetResult(), "First directory did not activate.");
    Assert(firstCalls == 1 && secondCalls == 0, "Activation crossed a directory boundary.");
});

Test("Shutdown cancels an idle pipe wait without delaying UI exit", () =>
{
    var data = Path.Combine(root, "shutdown");
    var primary = new SingleInstance(data);
    primary.StartListener(() => { });
    var time = Stopwatch.StartNew();
    primary.Dispose();
    Assert(time.ElapsedMilliseconds < 500, "Dispose blocked the caller.");
    Assert(primary.ListenerCompletion.Wait(TimeSpan.FromSeconds(1)), "Idle pipe wait did not cancel.");
    using var replacement = new SingleInstance(data);
    Assert(replacement.IsPrimary, "Mutex was not released for the next launch.");
});

Test("Unavailable resident endpoint returns within the bounded startup timeout", () =>
{
    var data = Path.Combine(root, "timeout");
    using var primary = new SingleInstance(data); // Intentionally do not start its listener.
    var time = Stopwatch.StartNew();
    var result = OtherThread(() => { using var secondary = new SingleInstance(data); return secondary.RequestActivationAsync().GetAwaiter().GetResult(); }).GetAwaiter().GetResult();
    Assert(!result && time.Elapsed < TimeSpan.FromSeconds(3), "Unavailable endpoint hung or reported successful delivery.");
});

var report = new { timestamp = DateTimeOffset.Now, platform = "Windows x64 named pipes", passed = results.Count - failed, failed, acknowledgedActivationsTested = 8, cases = results };
var reportIndex = Array.IndexOf(args, "--report");
if (reportIndex >= 0 && reportIndex + 1 < args.Length)
{
    var path = Path.GetFullPath(args[reportIndex + 1]); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
}
Console.WriteLine($"{results.Count - failed}/{results.Count} activation tests passed.");
return failed == 0 ? 0 : 1;
