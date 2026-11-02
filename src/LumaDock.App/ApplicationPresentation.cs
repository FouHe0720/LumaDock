using System.IO;
using LumaDock.Core;

namespace LumaDock.App;

/// <summary>Groups alternate launch modes for presentation without merging or rewriting their launch records.</summary>
public static class ApplicationPresentation
{
    private static readonly HashSet<string> SharedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "applicationframehost.exe", "explorer.exe", "rundll32.exe", "dllhost.exe", "svchost.exe",
        "cmd.exe", "powershell.exe", "pwsh.exe", "java.exe", "javaw.exe", "python.exe", "pythonw.exe",
        "wscript.exe", "cscript.exe", "msiexec.exe", "wwahost.exe", "runtimebroker.exe", "mmc.exe",
        "dotnet.exe", "electron.exe"
    };

    public static IReadOnlyList<ApplicationDisplayGroup> Group(IEnumerable<AppEntry> entries)
    {
        var result = new List<List<AppEntry>>();
        var groups = new Dictionary<string, List<AppEntry>>(StringComparer.OrdinalIgnoreCase);
        var existingFiles = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var executable = DirectExecutable(entry);
            if (executable.Length > 0)
            {
                // One filesystem check per unique executable, only when the caller rebuilds the catalogue view.
                if (!existingFiles.TryGetValue(executable, out var exists)) existingFiles[executable] = exists = File.Exists(executable);
                if (!exists) executable = "";
            }
            if (executable.Length == 0) { result.Add([entry]); continue; }
            var name = (entry.Name ?? "").Trim();
            var key = name.Length + ":" + name + executable;
            if (groups.TryGetValue(key, out var group)) group.Add(entry);
            else { group = [entry]; groups[key] = group; result.Add(group); }
        }
        return result.Select(group => new ApplicationDisplayGroup(group[0], group.ToArray())).ToArray();
    }

    private static string DirectExecutable(AppEntry entry)
    {
        if (entry.AppUserModelId?.Contains('!') == true) return ""; // Distinct packaged identities must not collapse into a common executable.
        var path = string.IsNullOrWhiteSpace(entry.ExecutablePath) ? entry.LaunchPath : entry.ExecutablePath;
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path)
            || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || SharedHosts.Contains(Path.GetFileName(path))) return "";
        try { return Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return ""; }
    }
}

public sealed record ApplicationDisplayGroup(AppEntry Representative, IReadOnlyList<AppEntry> Entries);
