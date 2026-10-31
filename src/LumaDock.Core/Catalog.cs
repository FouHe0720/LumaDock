using System.Security.Cryptography;
using System.Text;

namespace LumaDock.Core;

public static class Catalog
{
    public static string StableId(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.ToUpperInvariant())))[..24];

    public static string SuggestCategory(string name)
    {
        var n = name.ToLowerInvariant();
        if (new[] { "visual studio", "code", "git", "python", "terminal", "docker", "idea", "rider" }.Any(n.Contains)) return "开发工具";
        if (new[] { "chrome", "edge", "firefox", "browser", "浏览器" }.Any(n.Contains)) return "浏览器";
        if (new[] { "word", "excel", "powerpoint", "office", "onenote", "notion", "学习" }.Any(n.Contains)) return "办公学习";
        if (new[] { "music", "spotify", "vlc", "音乐", "视频" }.Any(n.Contains)) return "影音娱乐";
        if (new[] { "steam", "game", "游戏", "xbox" }.Any(n.Contains)) return "游戏";
        if (new[] { "设置", "管理", "control", "settings", "explorer", "记事本", "计算器" }.Any(n.Contains)) return "系统工具";
        return "其他";
    }

    // Discovery updates launch metadata, while retaining user classification, order and tombstones.
    public static void Merge(AppState state, IEnumerable<AppEntry> discovered)
    {
        var known = state.Apps.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var app in discovered)
        {
            if (known.TryGetValue(app.Id, out var existing))
            {
                existing.Name = app.Name; existing.LaunchPath = app.LaunchPath;
                existing.Arguments = app.Arguments; existing.WorkingDirectory = app.WorkingDirectory;
                existing.ExecutablePath = app.ExecutablePath; existing.AppUserModelId = app.AppUserModelId;
                existing.Source = app.Source; existing.DesktopPaths = app.DesktopPaths;
            }
            else { app.Category = SuggestCategory(app.Name); state.Apps.Add(app); known[app.Id] = app; }
        }
    }

    public static IEnumerable<AppEntry> Search(AppState state, string query, string? category = null)
    {
        var terms = query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return state.Apps.Where(a => !a.Hidden && (string.IsNullOrEmpty(category) || a.Category == category)
            && terms.All(t => a.Name.Contains(t, StringComparison.CurrentCultureIgnoreCase) || a.Category.Contains(t, StringComparison.CurrentCultureIgnoreCase)));
    }

    public static void Normalize(AppState state)
    {
        if (state.SchemaVersion != 1) throw new InvalidDataException($"不支持的配置版本 {state.SchemaVersion}，请使用对应版本的 LumaDock。");
        state.Settings ??= new(); state.Apps ??= []; state.Categories ??= []; state.PinnedIds ??= [];
        state.Apps = state.Apps.Where(a => a != null && !string.IsNullOrWhiteSpace(a.Id) && !string.IsNullOrWhiteSpace(a.LaunchPath)).DistinctBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToList();
        state.Categories = state.Categories.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
        foreach (var app in state.Apps)
        {
            app.Name ??= Path.GetFileNameWithoutExtension(app.LaunchPath); app.Arguments ??= "";
            app.WorkingDirectory ??= ""; app.ExecutablePath ??= ""; app.AppUserModelId ??= ""; app.Source ??= "Manual";
            app.DesktopPaths ??= [];
            if (string.IsNullOrWhiteSpace(app.Category)) app.Category = "其他";
            if (!state.Categories.Contains(app.Category)) state.Categories.Add(app.Category);
        }
        if (!state.Categories.Contains("其他")) state.Categories.Add("其他");
        var ids = state.Apps.Where(a => !a.Hidden).Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        state.PinnedIds = state.PinnedIds.Where(ids.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var s = state.Settings;
        s.Opacity = double.IsFinite(s.Opacity) ? Math.Clamp(s.Opacity, .55, 1) : .88;
        s.GlassClarity = double.IsFinite(s.GlassClarity) ? Math.Clamp(s.GlassClarity, .1, 1) : .65;
        s.GlassFlowStrength = double.IsFinite(s.GlassFlowStrength) ? Math.Clamp(s.GlassFlowStrength, 0, 1) : .45;
        if (!new[] { "Lagoon", "Jade", "Amber", "Iris" }.Contains(s.Accent)) s.Accent = "Lagoon";
        s.IconSize = double.IsFinite(s.IconSize) ? Math.Clamp(s.IconSize, 32, 80) : 48;
        s.Spacing = double.IsFinite(s.Spacing) ? Math.Clamp(s.Spacing, 4, 24) : 10;
        s.CornerRadius = double.IsFinite(s.CornerRadius) ? Math.Clamp(s.CornerRadius, 0, 40) : 24;
        s.AnimationStrength = double.IsFinite(s.AnimationStrength) ? Math.Clamp(s.AnimationStrength, 0, 1) : .45;
        s.Columns = Math.Clamp(s.Columns, 3, 10);
        s.NameFontSize = double.IsFinite(s.NameFontSize) ? Math.Clamp(s.NameFontSize, 10, 40) : 15;
        s.NameFontFamily = (s.NameFontFamily ?? "").Trim();
        // Family names only: imported configuration must not turn a font choice into a URI.
        if (s.NameFontFamily.Length > 80 || s.NameFontFamily.Any(c => char.IsControl(c) || c is ':' or '/' or '\\' or '#')) s.NameFontFamily = "";
        if (!new[] { "Bottom", "Top", "Left", "Right" }.Contains(s.Position)) s.Position = "Bottom";
        if (!new[] { "System", "Light", "Dark" }.Contains(s.Theme)) s.Theme = "System";
    }
}
