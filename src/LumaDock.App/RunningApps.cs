using System.Globalization;
using System.IO;
using LumaDock.Core;
using LumaDock.Windows;

namespace LumaDock.App;

/// <summary>A transient projection of actual windows. No catalogue writes, scans or process polling.</summary>
public static class RunningApps
{
    private static readonly HashSet<string> SharedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "applicationframehost.exe", "rundll32.exe", "dllhost.exe", "svchost.exe", "cmd.exe", "powershell.exe",
        "pwsh.exe", "java.exe", "javaw.exe", "python.exe", "pythonw.exe", "wscript.exe", "cscript.exe",
        "msiexec.exe", "wwahost.exe", "runtimebroker.exe", "mmc.exe", "dotnet.exe", "electron.exe"
    };

    public static bool CanPin(AppEntry app) => !string.IsNullOrWhiteSpace(app.LaunchPath)
        && (!string.Equals(app.Source, "Running", StringComparison.Ordinal)
            || !SharedHosts.Contains(Path.GetFileName(app.ExecutablePath)));

    public static IReadOnlyList<RunningAppGroup> Project(AppState state, IReadOnlyList<RunningWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(state); ArgumentNullException.ThrowIfNull(windows);
        var pinIds = new HashSet<string>(state.PinnedIds, StringComparer.OrdinalIgnoreCase);
        var pinned = state.Apps.Where(app => pinIds.Contains(app.Id) && !app.Hidden && app.IsAvailable).ToArray();
        var identities = new Dictionary<string, List<RunningWindow>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var handles = new HashSet<IntPtr>();
        foreach (var window in windows)
        {
            if (window.Handle == IntPtr.Zero || window.ProcessId == 0 || string.IsNullOrWhiteSpace(window.Title)
                || !handles.Add(window.Handle) || pinned.Any(app => WindowTracker.Matches(app, window))) continue;
            var key = Identity(window);
            if (!identities.TryGetValue(key, out var group)) { group = []; identities.Add(key, group); order.Add(key); }
            group.Add(window);
        }
        return order.Select(key =>
        {
            var group = identities[key];
            var known = state.Apps.FirstOrDefault(app => !string.IsNullOrWhiteSpace(app.AppUserModelId)
                && group.Any(window => string.Equals(app.AppUserModelId, window.AppUserModelId, StringComparison.OrdinalIgnoreCase)))
                ?? state.Apps.FirstOrDefault(app => string.IsNullOrWhiteSpace(app.Arguments) && group.Any(window => WindowTracker.Matches(app, window)))
                ?? state.Apps.FirstOrDefault(app => group.Any(window => WindowTracker.Matches(app, window)));
            return new RunningAppGroup(known ?? Temporary(group[0], key), group.AsReadOnly()) { IdentityKey = key };
        }).ToArray();
    }

    private static string Identity(RunningWindow window)
    {
        if (!string.IsNullOrWhiteSpace(window.AppUserModelId)) return "aumid:" + window.AppUserModelId.Trim().ToUpperInvariant();
        var path = Executable(window.ExecutablePath);
        if (path.Length > 0)
        {
            // Shared runtime hosts can contain unrelated applications. A process is
            // their narrowest reliable shared identity without inspecting commands.
            return SharedHosts.Contains(Path.GetFileName(path))
                ? "process:" + window.ProcessId.ToString(CultureInfo.InvariantCulture) + ":" + path.ToUpperInvariant() : "exe:" + path.ToUpperInvariant();
        }
        return "window:" + window.ProcessId.ToString(CultureInfo.InvariantCulture) + ":" + window.Handle.ToInt64().ToString("X", CultureInfo.InvariantCulture);
    }

    private static AppEntry Temporary(RunningWindow window, string key)
    {
        var path = Executable(window.ExecutablePath);
        var aumid = window.AppUserModelId?.Trim() ?? "";
        // A live packaged identity has a Shell launch namespace. An inaccessible
        // window without an executable or packaged identity remains switch-only.
        var launch = path.Length > 0 ? path : aumid.Contains('!') ? "shell:AppsFolder\\" + aumid : "";
        var name = path.Length > 0 ? Path.GetFileNameWithoutExtension(path) : window.Title.Trim();
        return new AppEntry
        {
            Id = "running:" + key.ToUpperInvariant(), Name = name, Source = "Running", LaunchPath = launch,
            ExecutablePath = path, AppUserModelId = aumid, IsAvailable = true
        };
    }

    private static string Executable(string? value)
    {
        try
        {
            var path = Environment.ExpandEnvironmentVariables((value ?? "").Trim().Trim('"'));
            if (path.StartsWith("\\\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path)
                || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return "";
            return Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return ""; }
    }
}

public sealed record RunningAppGroup(AppEntry App, IReadOnlyList<RunningWindow> Windows)
{
    public string IdentityKey { get; init; } = "";
    public bool IsEphemeral => string.Equals(App.Source, "Running", StringComparison.Ordinal);
}
