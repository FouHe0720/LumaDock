using System.IO;
using LumaDock.Core;

namespace LumaDock.Windows;

/// <summary>Uses positive filesystem and live Shell evidence. Unknown virtual targets are not declared missing.</summary>
public static class ApplicationAvailability
{
    private static readonly string[] Documents = [".pdf", ".txt", ".md", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".mp3", ".mp4", ".zip", ".rar", ".7z"];

    public static bool IsApplicationEntry(AppEntry app)
    {
        if (app.IsFolder) return false;
        if (!string.IsNullOrWhiteSpace(app.AppUserModelId) || app.LaunchPath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return true;
        var target = LocalPath(app.ExecutablePath);
        if (target.Length == 0) return true;
        if (Directory.Exists(target)) return false;
        return !Documents.Contains(Path.GetExtension(target), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Null means discovery could not establish whether this record is still available.</summary>
    public static bool? Evaluate(AppEntry app, AppDiscoverySnapshot snapshot)
    {
        if (app.IsFolder)
        {
            var folder = string.IsNullOrWhiteSpace(app.ExecutablePath) ? app.LaunchPath : app.ExecutablePath;
            return Directory.Exists(folder);
        }
        if (!IsApplicationEntry(app)) return false;
        var launch = LocalPath(app.LaunchPath);
        var target = LocalPath(app.ExecutablePath);
        var shellLaunch = app.LaunchPath.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase);
        var shellId = !string.IsNullOrWhiteSpace(app.AppUserModelId) ? app.AppUserModelId
            : shellLaunch ? app.LaunchPath["shell:AppsFolder\\".Length..] : "";
        // An AppsFolder launch requires its registration even if leftover program
        // files remain after uninstall. Real file-backed entries remain independent.
        if (shellLaunch && snapshot.AppsFolderComplete && !snapshot.RegisteredAppIds.Contains(shellId)) return false;
        if (target.Length > 0 && !File.Exists(target))
        {
            // A packaged app may expose an obsolete or non-public install target;
            // its current registration is stronger evidence than that path.
            if (app.AppUserModelId.Contains('!'))
            {
                if (snapshot.RegisteredAppIds.Contains(app.AppUserModelId)) return true;
                if (!snapshot.AppsFolderComplete) return null;
            }
            return false;
        }
        if (target.Length > 0 && File.Exists(target)) return true;
        if (shellId.Length > 0 && snapshot.RegisteredAppIds.Contains(shellId)) return true;
        if (shellLaunch) return snapshot.AppsFolderComplete ? false : null;
        if (launch.Length > 0) return File.Exists(launch);
        return null;
    }

    public static void Apply(AppState state, AppDiscoverySnapshot snapshot)
    {
        foreach (var app in state.Apps)
        {
            var available = Evaluate(app, snapshot);
            if (available.HasValue) app.IsAvailable = available.Value;
            // If a shortcut itself went away but its resolved program still
            // exists, preserve its arguments, working directory, ID and pin order.
            var launch = LocalPath(app.LaunchPath); var target = LocalPath(app.ExecutablePath);
            if (app.IsAvailable && launch.Length > 0 && !File.Exists(launch) && target.Length > 0 && File.Exists(target)
                && new[] { ".exe", ".com" }.Contains(Path.GetExtension(target), StringComparer.OrdinalIgnoreCase)) app.LaunchPath = target;
        }
    }

    private static string LocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return "";
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            return Path.IsPathFullyQualified(expanded) ? Path.GetFullPath(expanded) : "";
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return ""; }
    }
}

public sealed record AppDiscoverySnapshot(IReadOnlyList<AppEntry> Entries, IReadOnlySet<string> RegisteredAppIds, bool AppsFolderComplete,
    int ExcludedUnavailableCount = 0, int ExcludedNonApplicationCount = 0);
