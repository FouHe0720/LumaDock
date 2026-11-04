using System.Text.RegularExpressions;

namespace LumaDock.Core;

/// <summary>Matches launch records, never just their display names. Meaningful command lines remain distinct.</summary>
public static class AppIdentity
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    // This Chromium switch records the shortcut's launch location. It does not select a profile, URL or app.
    // Only remove a complete, unquoted switch for known executables; unknown switches remain significant.
    private static readonly Regex ShortcutAttribution = new(@"^--source-shortcut-location=[a-zA-Z0-9_-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> SharedHosts = new(StringComparer.OrdinalIgnoreCase)
    { "applicationframehost.exe", "wwahost.exe", "runtimebroker.exe", "explorer.exe", "rundll32.exe", "dllhost.exe", "svchost.exe", "mmc.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "java.exe", "javaw.exe", "dotnet.exe", "python.exe", "pythonw.exe", "electron.exe", "msedge.exe", "chrome.exe", "msedge_proxy.exe", "chrome_proxy.exe" };

    public static bool AreEquivalent(AppEntry left, AppEntry right)
    {
        // Folder pins have path identity, never an Explorer process or an app identity.
        if (left.IsFolder || right.IsFolder)
            return left.IsFolder && right.IsFolder && FolderPath(left).Length > 0 && Paths.Equals(FolderPath(left), FolderPath(right));
        if (Paths.Equals(left.Id, right.Id)) return true;
        var leftLaunch = NormalizePath(left.LaunchPath); var rightLaunch = NormalizePath(right.LaunchPath);
        if (leftLaunch.Length > 0 && Paths.Equals(leftLaunch, rightLaunch)
            && new[] { ".lnk", ".url", ".appref-ms" }.Contains(Path.GetExtension(leftLaunch), StringComparer.OrdinalIgnoreCase)) return true;
        var leftDirectory = NormalizePath(left.WorkingDirectory); var rightDirectory = NormalizePath(right.WorkingDirectory);
        if (leftDirectory.Length > 0 && rightDirectory.Length > 0 && !Paths.Equals(leftDirectory, rightDirectory)) return false;
        var leftArguments = Arguments(left); var rightArguments = Arguments(right);
        if (!string.Equals(leftArguments, rightArguments, StringComparison.Ordinal)) return false;
        var leftUri = GetLaunchUri(left); var rightUri = GetLaunchUri(right);
        if (leftUri.Length > 0 || rightUri.Length > 0) return leftUri.Length > 0 && string.Equals(leftUri, rightUri, StringComparison.Ordinal);
        var leftTarget = NormalizePath(left.ExecutablePath); var rightTarget = NormalizePath(right.ExecutablePath);
        var leftId = left.AppUserModelId?.Trim() ?? ""; var rightId = right.AppUserModelId?.Trim() ?? "";
        if (leftId.Length > 0 && rightId.Length > 0 && !Paths.Equals(leftId, rightId)
            && !(leftTarget.Length > 0 && Paths.Equals(leftTarget, rightTarget) && IsDirectProgram(leftTarget) && !leftId.Contains('!') && !rightId.Contains('!'))) return false;
        if (leftLaunch.Length > 0 && Paths.Equals(leftLaunch, rightLaunch)) return true;
        if (leftTarget.Length > 0 && rightTarget.Length > 0) return Paths.Equals(leftTarget, rightTarget);
        return leftId.Length > 0 && rightId.Length > 0 && Paths.Equals(leftId, rightId);
    }

    internal static IEnumerable<string> Keys(AppEntry app)
    {
        yield return "id:" + (app.Id ?? "").ToUpperInvariant();
        if (app.IsFolder) { yield return "folder:" + FolderPath(app).ToUpperInvariant(); yield break; }
        var launch = NormalizePath(app.LaunchPath);
        if (launch.Length > 0) yield return "file:" + launch.ToUpperInvariant();
        var arguments = Arguments(app);
        var uri = GetLaunchUri(app);
        if (uri.Length > 0) yield return "uri:" + uri + "|" + arguments;
        var target = NormalizePath(app.ExecutablePath);
        if (target.Length > 0) yield return "target:" + target.ToUpperInvariant() + "|" + arguments;
        if (!string.IsNullOrWhiteSpace(app.AppUserModelId)) yield return "aumid:" + app.AppUserModelId.Trim().ToUpperInvariant() + "|" + arguments;
    }

    private static string FolderPath(AppEntry app) => NormalizePath(string.IsNullOrWhiteSpace(app.ExecutablePath) ? app.LaunchPath : app.ExecutablePath);

    private static string Arguments(AppEntry app)
    {
        var arguments = (app.Arguments ?? "").Trim();
        var target = NormalizePath(app.ExecutablePath);
        var name = target[(target.LastIndexOf('\\') + 1)..];
        if (name.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase))
            arguments = RemoveAttribution(arguments, token => ShortcutAttribution.IsMatch(token));
        else if (name.Equals("Weixin.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("WeChat.exe", StringComparison.OrdinalIgnoreCase))
            arguments = RemoveAttribution(arguments, token => token == "--scene=desktop");
        else if (name.Equals("ksolaunch.exe", StringComparison.OrdinalIgnoreCase))
            arguments = RemoveAttribution(arguments, token => token is "/from=startmenu" or "/from=desktop_shortcut");
        return arguments;
    }

    private static bool IsDirectProgram(string target) => Path.IsPathFullyQualified(target) && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && !SharedHosts.Contains(target[(target.LastIndexOf('\\') + 1)..]);

    public static string NormalizeLaunchUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || value.Any(char.IsControl)) return "";
        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && !uri.IsFile && uri.Scheme.Length > 1
            && !uri.Scheme.Equals("shell", StringComparison.OrdinalIgnoreCase) ? uri.AbsoluteUri : "";
    }

    private static string GetLaunchUri(AppEntry app) => new[] { app.LaunchUri, app.ExecutablePath, app.AppUserModelId }
        .Select(NormalizeLaunchUri).FirstOrDefault(value => value.Length > 0) ?? "";

    private static string RemoveAttribution(string arguments, Func<string, bool> isAttribution)
    {
        var kept = new List<string>(); var start = 0; var quoted = false; var slashes = 0; var removed = false;
        for (var i = 0; i <= arguments.Length; i++)
        {
            var c = i < arguments.Length ? arguments[i] : ' ';
            if (i < arguments.Length && c == '"' && slashes % 2 == 0) quoted = !quoted;
            if (i == arguments.Length || char.IsWhiteSpace(c) && !quoted)
            {
                if (i > start)
                {
                    var token = arguments[start..i];
                    if (isAttribution(token)) removed = true; else kept.Add(token);
                }
                start = i + 1;
            }
            slashes = c == '\\' ? slashes + 1 : 0;
        }
        return removed ? string.Join(' ', kept) : arguments;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')).Replace('/', '\\');
        if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return path;
        try { return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path).TrimEnd('\\') : path; }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }
}
