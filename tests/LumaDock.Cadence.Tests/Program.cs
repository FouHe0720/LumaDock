using System.Text.Json;
using LumaDock.Core;

var results = new List<object>();
var failed = 0;
void Check(string name, Action action)
{
    try { action(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; results.Add(new { name, passed = false, error = ex.Message }); }
}
void Require(bool value) { if (!value) throw new InvalidOperationException("Unexpected refresh cadence"); }
Check("Changing scenes retain the fastest sampling cadence", () =>
{
    var c = new BackdropRefreshCadence();
    for (var i = 0; i < 50; i++) { c.Observe(true); Require(c.DelayMilliseconds == 120); }
});
Check("Static scenes back off progressively with a finite maximum", () =>
{
    var c = new BackdropRefreshCadence();
    foreach (var expected in new[] { 120, 120, 250, 250, 250, 500, 500, 500, 1000, 1000 })
    { c.Observe(false); Require(c.DelayMilliseconds == expected); }
    for (var i = 0; i < 100_000; i++) c.Observe(false);
    Require(c.DelayMilliseconds == 1000);
});
Check("Any scene change immediately restores responsive sampling", () =>
{
    var c = new BackdropRefreshCadence(); for (var i = 0; i < 30; i++) c.Observe(false);
    c.Observe(true); Require(c.DelayMilliseconds == 120);
});
Check("Activation and geometry reset do not retain stale idle delays", () =>
{
    var c = new BackdropRefreshCadence(); for (var i = 0; i < 30; i++) c.Observe(false);
    c.Reset(); Require(c.DelayMilliseconds == 120); c.Observe(false); Require(c.DelayMilliseconds == 120);
});
var report = new { executedAtUtc = DateTimeOffset.UtcNow, passed = results.Count - failed, failed, cases = results };
var index = Array.IndexOf(args, "--report");
if (index >= 0 && index + 1 < args.Length)
{
    var path = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
}
Console.WriteLine($"{report.passed}/{results.Count} passed.");
return failed == 0 ? 0 : 1;
