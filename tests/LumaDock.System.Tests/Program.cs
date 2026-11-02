using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Threading;
using LumaDock.Windows;

namespace LumaDock.System.Tests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var results = new List<object>(); var failed = 0;
        var observations = new Dictionary<string, object>();
        void Check(string name, Action run)
        {
            var watch = Stopwatch.StartNew();
            try { run(); results.Add(new { name, passed = true, durationMs = watch.Elapsed.TotalMilliseconds }); Console.WriteLine("PASS " + name); }
            catch (Exception error)
            {
                failed++; results.Add(new { name, passed = false, durationMs = watch.Elapsed.TotalMilliseconds, error = error.GetType().Name });
                Console.WriteLine("FAIL " + name + " (" + error.GetType().Name + ")");
            }
        }
        static void Require(bool condition) { if (!condition) throw new InvalidOperationException("The system integration condition did not pass."); }
        Check("Unknown battery flags and percentage never become invented zero or a missing battery", () =>
        {
            var unknown = SystemStatusService.DescribeBattery(255, 255, 255);
            Require(!unknown.Available && unknown.Percent == null && unknown.IsCharging == null && unknown.Text.Contains("未知"));
            var missingPercent = SystemStatusService.DescribeBattery(8, 255, 1);
            Require(missingPercent.HasBattery && missingPercent.Percent == null && missingPercent.IsCharging == true);
        });
        Check("Real zero percent, charging, external power and absent battery remain distinct", () =>
        {
            var empty = SystemStatusService.DescribeBattery(4, 0, 0);
            Require(empty.HasBattery && empty.Percent == 0 && empty.IsCharging == false);
            var charge = SystemStatusService.DescribeBattery(8, 54, 1);
            Require(charge.HasBattery && charge.Percent == 54 && charge.IsCharging == true);
            var power = SystemStatusService.DescribeBattery(0, 100, 1);
            Require(power.HasBattery && power.IsCharging == false && power.Percent == 100);
            var desktop = SystemStatusService.DescribeBattery(128, 255, 1);
            Require(desktop.Available && !desktop.HasBattery && desktop.Percent == null);
        });
        Check("Live status reads only usable interface, battery and foreground-language descriptions", () =>
        {
            using var service = new SystemStatusService(); service.Refresh();
            var current = service.Current;
            Require(current.Battery.Percent == null || current.Battery.Percent is >= 0 and <= 100);
            Require(current.Network.Text.Length > 0 && current.Keyboard.Text.Length > 0);
            Require(!current.Network.Text.Contains("Internet", StringComparison.OrdinalIgnoreCase));
            observations["batteryPresent"] = current.Battery.HasBattery;
            observations["batteryPercentageKnown"] = current.Battery.Percent != null;
            observations["usableNetworkInterfacePresent"] = current.Network.Connected;
        });
        Check("Disposed status unsubscribes native foreground, network and power events", () =>
        {
            var weak = CreateDisposedStatus(); Pump();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Require(!weak.IsAlive);
        });
        foreach (var action in Enum.GetValues<SystemAction>())
        {
            Check($"{action} constructs a balanced native key sequence without sending it", () =>
            {
                IReadOnlyList<SystemKeyInput>? seen = null;
                var result = SystemActions.Execute(action, packets => { seen = packets; return true; }, 26300);
                Require(result.Succeeded && seen != null && seen.Count >= 2 && seen.Count % 2 == 0);
                var half = seen!.Count / 2;
                Require(seen.Take(half).All(packet => !packet.KeyUp) && seen.Skip(half).All(packet => packet.KeyUp));
                Require(seen.Take(half).Select(packet => packet.VirtualKey).SequenceEqual(seen.Skip(half).Reverse().Select(packet => packet.VirtualKey)));
            });
        }
        Check("Windows 10 and Windows 11 notification entries select their documented native shortcuts", () =>
        {
            var oldKeys = new List<SystemKeyInput>(); var newKeys = new List<SystemKeyInput>();
            SystemActions.Execute(SystemAction.NotificationCenter, packets => { oldKeys.AddRange(packets); return true; }, 19045);
            SystemActions.Execute(SystemAction.NotificationCenter, packets => { newKeys.AddRange(packets); return true; }, 26300);
            Require(oldKeys[1].VirtualKey == 0x41 && newKeys[1].VirtualKey == 0x4E);
        });
        Check("An invalid or rejected native shortcut produces a visible failure result", () =>
        {
            Require(!SystemActions.Execute((SystemAction)900, _ => throw new Exception("Unexpected sender call"), 26300).Succeeded);
            var rejected = SystemActions.Execute(SystemAction.StartMenu, _ => false, 26300);
            Require(!rejected.Succeeded && !string.IsNullOrWhiteSpace(rejected.Error));
        });
        Check("Every settings URI is a fixed documented Windows entry without arbitrary parameters", () =>
        {
            foreach (var page in Enum.GetValues<SystemSettingsPage>())
            {
                var uri = SystemActions.SettingsUri(page); Require(uri != null && (uri.StartsWith("ms-settings:") || uri == "ms-availablenetworks:") && !uri.Any(char.IsWhiteSpace));
            }
            Require(SystemActions.SettingsUri((SystemSettingsPage)999) == null);
            Require(SystemActions.SettingsUri(SystemSettingsPage.Taskbar) == "ms-settings:taskbar");
            Require(SystemActions.SettingsUri(SystemSettingsPage.Keyboard) == "ms-settings:typing");
        });
        Check("Partial keyboard submission releases only injected keys still held and never releases a rejected key", () =>
        {
            SystemKeyInput[] packets = [new(0x5B, false), new(0x4E, false), new(0x4E, true), new(0x5B, true)];
            var method = typeof(SystemActions).GetMethod("PendingReleases", BindingFlags.Static | BindingFlags.NonPublic)!;
            SystemKeyInput[] Pending(int count) => (SystemKeyInput[])method.Invoke(null, [packets, count])!;
            Require(Pending(0).Length == 0 && Pending(4).Length == 0);
            Require(Pending(1).SequenceEqual([new SystemKeyInput(0x5B, true)]));
            Require(Pending(2).SequenceEqual([new SystemKeyInput(0x4E, true), new SystemKeyInput(0x5B, true)]));
            Require(Pending(3).SequenceEqual([new SystemKeyInput(0x5B, true)]));
        });
        Check("Real Core Audio default render endpoint reads bounded volume without changing it", () =>
        {
            using var audio = new AudioEndpointService(); audio.Refresh();
            observations["audioDeviceAvailable"] = audio.Current.Available;
            Require(!audio.Current.Available || (double.IsFinite(audio.Current.Volume) && audio.Current.Volume is >= 0 and <= 1 && audio.Current.DeviceName.Length > 0));
            if (audio.Current.Available)
            {
                Require(Field<bool>(audio, "notificationsRegistered") && Field<bool>(audio, "volumeRegistered"));
                observations["nativeAudioCallbacksRegistered"] = true;
            }
        });
        Check("Audio notification marshalling queues a UI refresh instead of making a native audio change", () =>
        {
            using var audio = new AudioEndpointService();
            var before = audio.Current;
            var callback = Field<AudioEndpointService.IAudioEndpointVolumeCallback>(audio, "volumeCallback");
            Require(Task.Run(() => callback.OnNotify(IntPtr.Zero)).GetAwaiter().GetResult() == 0);
            Pump();
            Require(before == audio.Current);
            Require(!audio.SetVolume(double.NaN).Succeeded);
        });
        Check("Default endpoint device notifications rebind and retain live callback registration", () =>
        {
            using var audio = new AudioEndpointService();
            var before = audio.Current;
            var callback = Field<AudioEndpointService.IMMNotificationClient>(audio, "deviceCallback");
            Require(Task.Run(() => callback.OnDefaultDeviceChanged(0, 1, null)).GetAwaiter().GetResult() == 0);
            Pump();
            Require(before == audio.Current);
            if (audio.Current.Available) Require(Field<bool>(audio, "notificationsRegistered") && Field<bool>(audio, "volumeRegistered"));
        });
        Check("Disposed audio unregisters native endpoint callbacks and rejects subsequent writes", () =>
        {
            var weak = CreateDisposedAudio(); Pump();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Require(!weak.IsAlive);
        });
        Check("Brightness service starts without WMI traffic and rejects non-finite writes", () =>
        {
            using var brightness = new BrightnessService();
            Require(!brightness.Current.Available && brightness.Current.Reason.Length > 0);
            Require(!brightness.SetBrightnessAsync(double.NaN).GetAwaiter().GetResult().Succeeded);
            Require(!brightness.SetBrightnessAsync(double.PositiveInfinity).GetAwaiter().GetResult().Succeeded);
        });
        Check("Read-only WMI brightness query returns real supported range or an explicit unavailable state", () =>
        {
            using var brightness = new BrightnessService();
            var before = brightness.Current;
            var wrongThread = false;
            var ownerThread = Environment.CurrentManagedThreadId;
            brightness.Changed += (_, _) => wrongThread |= Environment.CurrentManagedThreadId != ownerThread;
            var refresh = brightness.RefreshAsync();
            Require(ReferenceEquals(refresh, brightness.RefreshAsync()));
            var until = Stopwatch.StartNew();
            while (!refresh.IsCompleted && until.Elapsed < TimeSpan.FromSeconds(15)) { Pump(); Thread.Sleep(10); }
            Require(refresh.IsCompleted); refresh.GetAwaiter().GetResult();
            Pump();
            var value = brightness.Current;
            Require(!wrongThread);
            observations["brightnessInterfaceAvailable"] = value.Available;
            if (value.Available) observations["brightnessReadPercent"] = value.Percent;
            Require(value.Available ? double.IsFinite(value.Percent) && value.Percent is >= 0 and <= 100 : value.Reason.Length > 0);
        });
        Check("Disposed brightness service rejects writes and does not open WMI for refresh", () =>
        {
            var brightness = new BrightnessService(); brightness.Dispose(); brightness.Dispose();
            Require(!brightness.SetBrightnessAsync(.5).GetAwaiter().GetResult().Succeeded);
            brightness.RefreshAsync().GetAwaiter().GetResult();
            Require(!brightness.Current.Available);
        });
        Check("Native background-app entry is probed read-only with an explicit capability result", () =>
        {
            var probe = TrayIntegration.ProbeOverflowAsync();
            var until = Stopwatch.StartNew();
            while (!probe.IsCompleted && until.Elapsed < TimeSpan.FromSeconds(5)) { Pump(); Thread.Sleep(10); }
            Require(probe.IsCompleted);
            var value = probe.GetAwaiter().GetResult();
            observations["nativeTrayOverflowEntryFound"] = value.Succeeded;
            if (!value.Succeeded) observations["nativeTrayOverflowUnavailableReason"] = value.Error ?? "";
            Require(value.Succeeded || !string.IsNullOrWhiteSpace(value.Error));
        });
        var report = new { version = "0.12.0", measuredAt = DateTimeOffset.Now, passed = results.Count - failed, failed,
            observations, cases = results, actualKeyboardInputsSent = 0, actualSettingsOpened = 0,
            audioVolumeChangedByTest = false, brightnessChangedByTest = false, systemSettingsChanged = false,
            scope = "Real status/default-audio reads and callback registration/disposal; injected-key tests do not invoke Windows UI; callback delivery uses a synthetic notification, not a system volume change." };
        var index = Array.IndexOf(args, "--report"); var path = index >= 0 && index + 1 < args.Length ? args[index + 1] : "artifacts/tests/system-results-0.12.json";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"System services: {results.Count - failed}/{results.Count} passed."); return failed == 0 ? 0 : 1;
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    [MethodImpl(MethodImplOptions.NoInlining)] private static WeakReference CreateDisposedStatus()
    {
        var service = new SystemStatusService(); service.Dispose(); service.Dispose();
        if (service.Refresh()) throw new InvalidOperationException(); return new(service);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private static WeakReference CreateDisposedAudio()
    {
        var service = new AudioEndpointService(); service.Dispose(); service.Dispose();
        if (service.Refresh() || service.SetMuted(false).Succeeded || service.SetVolume(.5).Succeeded) throw new InvalidOperationException();
        return new(service);
    }
}
