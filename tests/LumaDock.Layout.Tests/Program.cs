using System.Text.Json;
using LumaDock.Core;

var tests = new List<TestResult>();
void Test(string name, Action test)
{
    try { test(); tests.Add(new(name, true, null)); Console.WriteLine("PASS " + name); }
    catch (Exception error) { tests.Add(new(name, false, error.Message)); Console.WriteLine("FAIL " + name + ": " + error.Message); }
}
void Require(bool value, string explanation) { if (!value) throw new InvalidOperationException(explanation); }

Test("Larger desktop fonts reserve more label space and reduce page density", () =>
{
    var small = AppGridLayout.Calculate(850, 450, 6, 11);
    var large = AppGridLayout.Calculate(850, 450, 6, 32);
    Require(large.LineHeight > small.LineHeight && large.NameHeight > small.NameHeight, "Larger names must have more vertical space.");
    Require(large.Columns * large.Rows < small.Columns * small.Rows, "Large names must not remain squeezed into the old grid density.");
});
Test("Supported font sizes retain two name lines and fit complete visible rows", () =>
{
    foreach (var size in new[] { 11d, 15d, 24d, 32d, 48d })
    {
        var layout = AppGridLayout.Calculate(850, 600, 10, size);
        Require(layout.NameHeight >= layout.LineHeight * 2, "Both name lines must fit.");
        Require(layout.TileHeight > 48 + layout.NameHeight, "The icon and label need distinct vertical space.");
        Require(layout.Rows * (layout.TileHeight + 10) <= 600, "Visible rows must fit their viewport.");
        Require(layout.Columns <= 10 && layout.Columns >= 1 && layout.Rows >= 1, "The grid needs usable nonzero dimensions.");
    }
});
Test("Requested column count is an upper bound rather than a forced density", () =>
{
    var restrained = AppGridLayout.Calculate(1200, 600, 4, 15);
    var narrow = AppGridLayout.Calculate(320, 600, 10, 24);
    Require(restrained.Columns <= 4, "The user's maximum column count was ignored.");
    Require(narrow.Columns < restrained.Columns, "A narrow viewport should reduce columns instead of shrinking names.");
});
Test("Shrinking the viewport never increases column or row density", () =>
{
    var roomy = AppGridLayout.Calculate(1100, 650, 8, 24);
    var smaller = AppGridLayout.Calculate(650, 350, 8, 24);
    Require(smaller.Columns <= roomy.Columns && smaller.Rows <= roomy.Rows, "Shrinking a viewport increased its grid dimensions.");
});
Test("Very small windows retain one scrollable tile", () =>
{
    var layout = AppGridLayout.Calculate(1, 1, 6, 48);
    Require(layout.Columns == 1 && layout.Rows == 1, "A small viewport must not create a zero-size page.");
    Require(layout.NameHeight >= layout.LineHeight * 2, "Small windows must not reduce the font's label height.");
});
Test("A taller font face receives extra line spacing", () =>
{
    var normal = AppGridLayout.Calculate(850, 450, 6, 24, 1.2);
    var tall = AppGridLayout.Calculate(850, 450, 6, 24, 1.8);
    Require(tall.NameHeight > normal.NameHeight && tall.TileHeight > normal.TileHeight, "Font face line spacing was ignored.");
});
Test("Invalid or extreme input never creates nonfinite or zero geometry", () =>
{
    foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -10d, 0d, double.MaxValue })
    {
        var layout = AppGridLayout.Calculate(invalid, invalid, int.MaxValue, invalid, invalid);
        Require(layout.Columns is >= 1 and <= 10 && layout.Rows is >= 1 and <= 4, "Invalid grid dimensions escaped normalization.");
        Require(double.IsFinite(layout.NameHeight) && double.IsFinite(layout.TileHeight) && double.IsFinite(layout.LineHeight), "Nonfinite tile geometry escaped normalization.");
        Require(layout.NameHeight > 0 && layout.TileHeight > 0 && layout.LineHeight > 0, "Invalid input produced empty labels.");
    }
    Require(AppGridLayout.Calculate(850, 450, int.MinValue, 14).Columns == 1, "Invalid requested columns must still produce one column.");
});

var reportIndex = Array.IndexOf(args, "--report");
var reportPath = Path.GetFullPath(reportIndex >= 0 && reportIndex + 1 < args.Length ? args[reportIndex + 1] : "artifacts/tests/layout-results.json");
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
var passed = tests.Count(t => t.Passed);
File.WriteAllText(reportPath, JsonSerializer.Serialize(new { recordedAtUtc = DateTimeOffset.UtcNow, passed, failed = tests.Count - passed, tests }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{passed}/{tests.Count} layout tests passed.");
return passed == tests.Count ? 0 : 1;

sealed record TestResult(string Name, bool Passed, string? Error);
