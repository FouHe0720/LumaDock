using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LumaDock.Core;

var tests = new List<TestResult>();
var checker = Fixture(1024, 1024, (x, y) => ((x / 16 + y / 16) & 1) == 0 ? (0, 0, 255, 255) : (255, 0, 0, 255));
var placement = new WallpaperPlacement(0, 0, 1024, 1024, false, 0, 0, 0);
void Test(string name, Action action)
{
    try { action(); tests.Add(new(name, true, null)); Console.WriteLine("PASS " + name); }
    catch (Exception error) { tests.Add(new(name, false, error.Message)); Console.WriteLine("FAIL " + name + ": " + error.Message); }
}
void Require(bool value, string explanation) { if (!value) throw new InvalidOperationException(explanation); }
void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}

Test("The edge refracts an actual checkerboard across its original colour boundary", () =>
{
    var result = LiquidLensRenderer.Render(checker, placement, new(100, 100, 400, 400), 400, 400, 0, true, 1);
    // A plain crop at screen(101.5,300.5) is red. The lens must actually sample
    // across the x=96 source boundary, making this edge blue instead.
    var original = Pixel(checker.Bgra, checker.Width, 101, 300);
    var edge = Pixel(result, 400, 1, 200);
    var centre = Pixel(result, 400, 200, 200);
    Require(original.R > original.B && edge.B > edge.R + 30, "The edge changed shading without moving the source colour boundary.");
    Require(centre.R > centre.B + 30, "The centre no longer preserves its actual source colour.");
});
Test("The central image keeps horizontal and vertical orientation", () =>
{
    var gradient = Fixture(256, 256, (x, y) => (0, y, x, 255));
    var result = LiquidLensRenderer.Render(gradient, new(0, 0, 256, 256, false, 0, 0, 0), new(16, 16, 200, 200), 200, 200, 20, true, 1);
    var centre = Pixel(result, 200, 90, 90);
    var right = Pixel(result, 200, 110, 90);
    var below = Pixel(result, 200, 90, 110);
    Require(right.R > centre.R + 5 && right.G == centre.G, "The image's horizontal direction or channel order changed.");
    Require(below.G > centre.G + 5 && below.R == centre.R, "The image's vertical direction or channel order changed.");
});
Test("Sampling blends neighbouring pixels instead of using a nearest-pixel blit", () =>
{
    var source = Fixture(2, 2, (x, _) => x == 0 ? (0, 0, 255, 255) : (255, 0, 0, 255));
    var result = LiquidLensRenderer.Render(source, new(0, 0, 100, 100, false, 0, 0, 0), new(0, 0, 100, 100), 1, 1, 0, true, 1);
    var centre = Pixel(result, 1, 0, 0);
    Require(Math.Abs(centre.B - centre.R) <= 1 && centre.B > centre.G + 20, "The midpoint did not blend both source colours.");
});
Test("Rounded corners are transparent and all output channels are premultiplied", () =>
{
    var white = Fixture(1, 1, (_, _) => (255, 255, 255, 255));
    var result = LiquidLensRenderer.Render(white, new(0, 0, 400, 400, false, 0, 0, 0), new(0, 0, 200, 160), 200, 160, 28, false, .9);
    Require(Pixel(result, 200, 0, 0).A == 0, "The outer rounded corner was not transparent.");
    Require(Pixel(result, 200, 100, 80).A == 255, "The centre was unexpectedly transparent.");
    var antialiased = false;
    for (var index = 0; index < result.Length; index += 4)
    {
        var alpha = result[index + 3];
        Require(result[index] <= alpha && result[index + 1] <= alpha && result[index + 2] <= alpha, "A colour channel exceeds its premultiplied alpha.");
        antialiased |= alpha is > 0 and < 255;
    }
    Require(antialiased, "The curved edge lost its fractional pixel coverage.");
});
Test("Transparent PNG pixels are composited over the desktop background before tinting", () =>
{
    var transparentRed = Fixture(1, 1, (_, _) => (0, 0, 255, 0));
    var result = LiquidLensRenderer.Render(transparentRed, new(0, 0, 400, 400, false, 20, 40, 80), new(100, 100, 200, 200), 20, 20, 20, true, 1);
    var centre = Pixel(result, 20, 10, 10);
    Require(centre.B > centre.G && centre.G > centre.R, "Transparent hidden red RGB leaked into the visible wallpaper.");
});
Test("Letterbox areas use the desktop colour while tiled images repeat", () =>
{
    var red = Fixture(1, 1, (_, _) => (0, 0, 255, 255));
    var viewport = new LensViewport(-100, -100, 80, 80);
    var normal = LiquidLensRenderer.Render(red, new(100, 100, 50, 50, false, 0, 0, 160), viewport, 80, 80, 0, true, 1);
    var tiled = LiquidLensRenderer.Render(red, new(100, 100, 50, 50, true, 0, 0, 160), viewport, 80, 80, 0, true, 1);
    var background = Pixel(normal, 80, 40, 40);
    var repeated = Pixel(tiled, 80, 40, 40);
    Require(background.B > background.R + 30, "The letterbox area did not use the desktop background.");
    Require(repeated.R > repeated.B + 30, "Negative desktop coordinates did not repeat the tiled image.");
});
Test("Tiled placement is periodic across negative virtual-desktop coordinates", () =>
{
    var source = Fixture(2, 2, (x, y) => ((x + y) & 1) == 0 ? (0, 0, 255, 255) : (255, 0, 0, 255));
    var tile = new WallpaperPlacement(10, 20, 32, 32, true, 0, 0, 0);
    var first = LiquidLensRenderer.Render(source, tile, new(-102, -92, 200, 200), 200, 200, 20, true, 1);
    var shifted = LiquidLensRenderer.Render(source, tile, new(-70, -60, 200, 200), 200, 200, 20, true, 1);
    foreach (var x in new[] { 60, 100, 140 })
        foreach (var y in new[] { 60, 100, 140 })
            Require(Pixel(first, 200, x, y) == Pixel(shifted, 200, x, y), "A whole tile displacement changed the image's centre.");
});
Test("The flat-centre fast path matches unbent curved-region sampling including source alpha", () =>
{
    var source = Fixture(16, 16, (x, y) => (x * 17, y * 17, (x + y) * 8, (x * 17 + y * 11) & 255));
    var display = new WallpaperPlacement(0, 0, 400, 400, false, 20, 40, 80);
    foreach (var dark in new[] { true, false })
    {
        var flat = LiquidLensRenderer.Render(source, display, new(0, 0, 400, 400), 400, 400, 0, dark, .9);
        var curved = LiquidLensRenderer.Render(source, display, new(100, 100, 400, 400), 400, 400, 180, dark, .9);
        // Both pixels see screen(190.5,190.5). The second is in a rounded corner's
        // unbent interior, which still needs the general distance-field path.
        Require(Pixel(flat, 400, 190, 190) == Pixel(curved, 400, 90, 90), "The centre optimization changed sampling, alpha flattening or tint.");
    }
});
Test("Lower clarity actually averages source detail rather than only changing the tint", () =>
{
    var fineStripes = Fixture(256, 256, (x, _) => (x & 1) == 0 ? (0, 0, 255, 255) : (255, 0, 0, 255));
    var display = new WallpaperPlacement(0, 0, 256, 256, false, 0, 0, 0);
    var clear = LiquidLensRenderer.Render(fineStripes, display, new(16, 16, 200, 200), 200, 200, 20, true, 1);
    var soft = LiquidLensRenderer.Render(fineStripes, display, new(16, 16, 200, 200), 200, 200, 20, true, .5);
    var clearPixel = Pixel(clear, 200, 90, 90); var softPixel = Pixel(soft, 200, 90, 90);
    Require(Math.Abs(softPixel.R - softPixel.B) < Math.Abs(clearPixel.R - clearPixel.B) / 2, "Lower clarity did not soften the source detail.");
});
Test("The soft glass interior suppresses checker detail beyond the change in tint", () =>
{
    var detail = Fixture(256, 256, (x, y) => ((x / 4 + y / 4) & 1) == 0 ? (255, 255, 255, 255) : (0, 0, 0, 255));
    var display = new WallpaperPlacement(0, 0, 256, 256, false, 0, 0, 0);
    var clear = LiquidLensRenderer.Render(detail, display, new(0, 0, 256, 256), 256, 256, 24, true, 1);
    var soft = LiquidLensRenderer.Render(detail, display, new(0, 0, 256, 256), 256, 256, 24, true, .5);
    var clearVariance = Variance(clear, 256, 64, 64, 128, 128);
    var softVariance = Variance(soft, 256, 64, 64, 128, 128);
    Require(clearVariance > 100 && softVariance < clearVariance * .25, "The central detail remains legible despite the soft-glass setting.");
});
Test("Interior diffusion preserves the curved rim and its premultiplied alpha", () =>
{
    // The narrow viewport skips the diffusion pass. Both views have an identical
    // physical left perimeter, so that original refracted detail must match.
    var large = LiquidLensRenderer.Render(checker, placement, new(100, 100, 256, 256), 256, 256, 24, true, .5);
    var narrow = LiquidLensRenderer.Render(checker, placement, new(100, 100, 127, 256), 127, 256, 24, true, .5);
    foreach (var x in new[] { 0, 1, 5, 12, 20 })
        foreach (var y in new[] { 0, 5, 40, 128, 216 })
            Require(Pixel(large, 256, x, y) == Pixel(narrow, 127, x, y), "The diffusion pass altered the lens perimeter.");
    for (var offset = 0; offset < large.Length; offset += 4)
        Require(large[offset] <= large[offset + 3] && large[offset + 1] <= large[offset + 3] && large[offset + 2] <= large[offset + 3], "Interior diffusion produced invalid premultiplied alpha.");
});
Test("Thin Dock sampling matches the original scalar pixels across light, dark, rounded and vertical surfaces", () =>
{
    var source = Fixture(1024, 1024, (x, y) => (x * 17 + y * 11, x * 7 + y * 13, x * 3 + y * 23, x * 11 + y * 7));
    var display = new WallpaperPlacement(-140, -50, 2560, 1440, false, 20, 40, 80);
    // Full-output digests were recorded from the unoptimized scalar renderer,
    // including the curved caps, fractional alpha and the four softened samples.
    var references = new[] {
        (720, 64, 32d, .65, true, "97D250D6F1ADA905337EF22BDEA33097027EB28132038618D2C8520F3ABF2750"),
        (64, 720, 32d, .65, true, "6B7E788DEFC2AB84FDBB256D79FF8875CBA4B1F8C8D38030E4F7DFEB5E983516"),
        (799, 79, 24d, .3, false, "2093889C2954AD8E7B0153EF909DBD9494547F829C521B1052C4C43407358868"),
        (159, 68, 0d, .65, true, "72DE9BFA64B174E677C94A45223EBB81B46C3A9C45764385B6E434C3DA5BDB56"),
        (600, 72, 24d, .65, false, "49B982E6DA772D51FBBC06D569D301D8F08039D99D3C51B32032B60385AFC97D")
    };
    foreach (var (width, height, radius, clarity, dark, digest) in references)
    {
        var actual = LiquidLensRenderer.Render(source, display, new(400, 600, width, height), width, height, radius, dark, clarity);
        Require(Convert.ToHexString(SHA256.HashData(actual)) == digest, $"The {width}x{height} Dock's pixels changed.");
    }
    var fractional = LiquidLensRenderer.Render(source, display with { Tile = true },
        new(482, 685, 101.88495063962645, 13.174778099951697), 116, 15,
        .10301910754804844, false, .539717807685825);
    Require(Convert.ToHexString(SHA256.HashData(fractional)) == "CDEC0AD208B4377A04D7432C2B9E8A2553E90D6F27A24B1D40028739FCEF96E3",
        "Fractional scaling changed the normal chosen at the Dock's medial axis.");
});
Test("A cancelled render stops instead of returning a partial bitmap", () =>
{
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    Throws<OperationCanceledException>(() => LiquidLensRenderer.Render(checker, placement, new(0, 0, 400, 400), 400, 400, 20, true, .8, cancelled.Token));
    using var midRender = new CancellationTokenSource(); midRender.CancelAfter(1);
    Throws<OperationCanceledException>(() => LiquidLensRenderer.Render(checker, placement, new(0, 0, 1920, 1080), 1920, 1080, 24, true, .5, midRender.Token));
    using var midStrip = new CancellationTokenSource(); midStrip.CancelAfter(1);
    Throws<OperationCanceledException>(() => LiquidLensRenderer.Render(checker, placement, new(0, 0, 8192, 79), 8192, 79, 24, true, .5, midStrip.Token));
});
Test("Invalid dimensions and oversized output are rejected before allocation", () =>
{
    Throws<ArgumentException>(() => LiquidLensRenderer.Render(new(5, 5, [0, 0, 0, 0]), placement, new(0, 0, 100, 100), 100, 100, 20, true, 1));
    Throws<ArgumentOutOfRangeException>(() => LiquidLensRenderer.Render(checker, placement, new(0, 0, 100, 100), 0, 100, 20, true, 1));
    Throws<ArgumentOutOfRangeException>(() => LiquidLensRenderer.Render(checker, placement, new(0, 0, 100, 100), int.MaxValue, int.MaxValue, 20, true, 1));
    Throws<ArgumentException>(() => LiquidLensRenderer.Render(checker, placement, new(double.NaN, 0, 100, 100), 100, 100, 20, true, 1));
    Throws<ArgumentException>(() => LiquidLensRenderer.Render(checker, placement with { Width = 0 }, new(0, 0, 100, 100), 100, 100, 20, true, 1));
    Throws<ArgumentOutOfRangeException>(() => LiquidLensRenderer.Render(checker, placement, new(0, 0, 100, 100), 100, 100, double.NaN, true, 1));
    Throws<ArgumentOutOfRangeException>(() => LiquidLensRenderer.Render(checker, placement, new(0, 0, 100, 100), 100, 100, 20, true, double.NaN));
});

// One warm-JIT render records the actual renderer cost. It is a synthetic fixture
// benchmark, not a whole-app latency, GPU or idle-resource measurement.
LiquidLensRenderer.Render(checker, placement, new(0, 0, 64, 64), 64, 64, 20, true, .9);
using var process = Process.GetCurrentProcess();
var beforeCpu = process.TotalProcessorTime.TotalMilliseconds;
var beforeAllocation = GC.GetAllocatedBytesForCurrentThread();
var timer = Stopwatch.StartNew();
var benchmarkBuffer = LiquidLensRenderer.Render(checker, new(0, 0, 2560, 1440, false, 0, 0, 0), new(720, 220, 1120, 720), 1120, 720, 24, true, .9);
timer.Stop(); process.Refresh();
var benchmark = new
{
    outputWidth = 1120, outputHeight = 720, outputPixels = 1120 * 720,
    sourceWidth = checker.Width, sourceHeight = checker.Height,
    clarity = .9, dark = true, elapsedMs = timer.Elapsed.TotalMilliseconds,
    processCpuMs = process.TotalProcessorTime.TotalMilliseconds - beforeCpu,
    allocatedBytesCurrentThread = GC.GetAllocatedBytesForCurrentThread() - beforeAllocation,
    outputBytes = benchmarkBuffer.Length, warmJit = true, fixtureOnly = true
};
beforeCpu = process.TotalProcessorTime.TotalMilliseconds;
beforeAllocation = GC.GetAllocatedBytesForCurrentThread();
timer.Restart();
var runtimeBuffer = LiquidLensRenderer.Render(checker, new(0, 0, 2560, 1440, false, 0, 0, 0), new(720, 220, 1120, 720), 800, 450, 24, true, .9);
timer.Stop(); process.Refresh();
var runtimeBenchmark = new
{
    outputWidth = 800, outputHeight = 450, outputPixels = 800 * 450,
    sourceWidth = checker.Width, sourceHeight = checker.Height,
    clarity = .9, dark = true, elapsedMs = timer.Elapsed.TotalMilliseconds,
    processCpuMs = process.TotalProcessorTime.TotalMilliseconds - beforeCpu,
    allocatedBytesCurrentThread = GC.GetAllocatedBytesForCurrentThread() - beforeAllocation,
    outputBytes = runtimeBuffer.Length, warmJit = true, fixtureOnly = true
};
beforeCpu = process.TotalProcessorTime.TotalMilliseconds;
beforeAllocation = GC.GetAllocatedBytesForCurrentThread();
timer.Restart();
var softRuntimeBuffer = LiquidLensRenderer.Render(checker, new(0, 0, 2560, 1440, false, 0, 0, 0), new(720, 220, 1120, 720), 800, 450, 24, true, .65);
timer.Stop(); process.Refresh();
var softRuntimeBenchmark = new
{
    outputWidth = 800, outputHeight = 450, outputPixels = 800 * 450,
    sourceWidth = checker.Width, sourceHeight = checker.Height,
    clarity = .65, dark = true, elapsedMs = timer.Elapsed.TotalMilliseconds,
    processCpuMs = process.TotalProcessorTime.TotalMilliseconds - beforeCpu,
    allocatedBytesCurrentThread = GC.GetAllocatedBytesForCurrentThread() - beforeAllocation,
    outputBytes = softRuntimeBuffer.Length, warmJit = true, fixtureOnly = true,
    description = "Two separable running-sum box passes on the interior, with an unchanged refracting rim. Pool buffers may be cold."
};
var dockTimes = new List<double>();
long dockAllocation = 0;
for (var i = 0; i < 4; i++) LiquidLensRenderer.Render(checker, placement, new(100, 100, 720, 64), 720, 64, 32, true, .65);
for (var i = 0; i < 30; i++)
{
    beforeAllocation = GC.GetAllocatedBytesForCurrentThread(); timer.Restart();
    var dockBuffer = LiquidLensRenderer.Render(checker, placement, new(100, 100, 720, 64), 720, 64, 32, true, .65);
    timer.Stop(); dockAllocation += GC.GetAllocatedBytesForCurrentThread() - beforeAllocation;
    dockTimes.Add(timer.Elapsed.TotalMilliseconds); GC.KeepAlive(dockBuffer);
}
dockTimes.Sort();
var dockBenchmark = new
{
    outputWidth = 720, outputHeight = 64, outputPixels = 720 * 64, clarity = .65, dark = true,
    medianElapsedMs = (dockTimes[14] + dockTimes[15]) / 2, rounds = 30,
    allocatedBytesPerFrameCurrentThread = dockAllocation / 30,
    outputBytes = 720 * 64 * 4, warmJit = true, scratchPoolWarm = true, fixtureOnly = true,
    description = "Same four bilinear softened samples and scalar optical result; one bounded pooled coordinate table. Native capture and WPF excluded."
};
var reportIndex = Array.IndexOf(args, "--report");
var reportPath = Path.GetFullPath(reportIndex >= 0 && reportIndex + 1 < args.Length ? args[reportIndex + 1] : "artifacts/tests/lens-results.json");
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
var passed = tests.Count(t => t.Passed);
File.WriteAllText(reportPath, JsonSerializer.Serialize(new { recordedAtUtc = DateTimeOffset.UtcNow, passed, failed = tests.Count - passed, syntheticFixturesOnly = true, tests, benchmark, runtimeBenchmark, softRuntimeBenchmark, dockBenchmark }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{passed}/{tests.Count} lens tests passed. 806400 pixels: {benchmark.elapsedMs:F2} ms; 360000 pixels: {runtimeBenchmark.elapsedMs:F2} ms; soft .65 360000 pixels: {softRuntimeBenchmark.elapsedMs:F2} ms; 720x64 Dock median: {dockBenchmark.medianElapsedMs:F2} ms.");
return passed == tests.Count ? 0 : 1;

static LensSource Fixture(int width, int height, Func<int, int, (int B, int G, int R, int A)> colour)
{
    var buffer = new byte[width * height * 4];
    for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
    {
        var pixel = colour(x, y); var offset = (y * width + x) * 4;
        buffer[offset] = (byte)pixel.B; buffer[offset + 1] = (byte)pixel.G; buffer[offset + 2] = (byte)pixel.R; buffer[offset + 3] = (byte)pixel.A;
    }
    return new(width, height, buffer);
}
static (int B, int G, int R, int A) Pixel(byte[] buffer, int width, int x, int y)
{
    var offset = (y * width + x) * 4;
    return (buffer[offset], buffer[offset + 1], buffer[offset + 2], buffer[offset + 3]);
}
static double Variance(byte[] buffer, int strideWidth, int left, int top, int width, int height)
{
    double sum = 0, sumSquared = 0;
    for (var y = top; y < top + height; y++) for (var x = left; x < left + width; x++)
    {
        var value = buffer[(y * strideWidth + x) * 4];
        sum += value; sumSquared += value * value;
    }
    var count = width * height;
    return sumSquared / count - sum / count * (sum / count);
}
sealed record TestResult(string Name, bool Passed, string? Error);
