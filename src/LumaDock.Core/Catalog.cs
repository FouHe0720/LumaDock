using System.Security.Cryptography;
using System.Text;

namespace LumaDock.Core;

public static class Catalog
{
    public const int CategoryRuleVersion = 1;
    public static string StableId(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.ToUpperInvariant())))[..24];

    public static string SuggestCategory(string name) => AppCategories.Suggest(name);
    public static string SuggestCategory(AppEntry app) => AppCategories.Suggest(app.Name, app.ExecutablePath);
    public static void AssignCategory(AppEntry app, string category)
    { app.Category = category; app.CategoryIsUserAssigned = true; app.CategoryRuleVersion = CategoryRuleVersion; }
    public static AppEntry? FindEquivalent(AppState state, AppEntry app) => state.Apps.FirstOrDefault(existing => AppIdentity.AreEquivalent(existing, app));

    // Discovery updates launch metadata, while retaining user classification, order and tombstones.
    public static void Merge(AppState state, IEnumerable<AppEntry> discovered)
    {
        foreach (var app in state.Apps) UpdateAutomaticCategory(app);
        var known = state.Apps.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var app in discovered)
        {
            var exact = known.TryGetValue(app.Id, out var existing);
            existing ??= FindEquivalent(state, app);
            if (existing != null)
            {
                MergeMetadata(existing, app, exact || LaunchRank(app) < LaunchRank(existing));
                if (!existing.CategoryIsUserAssigned) existing.Category = SuggestCategory(existing);
            }
            else
            {
                app.Category = SuggestCategory(app); app.CategoryRuleVersion = CategoryRuleVersion;
                state.Apps.Add(app); known[app.Id] = app;
            }
        }
        Deduplicate(state);
        foreach (var category in state.Apps.Select(app => app.Category).Distinct()) if (!state.Categories.Contains(category)) state.Categories.Add(category);
    }

    // Canonical IDs and the first user order survive an upgrade. Duplicate pins map to that same ID.
    // A hidden record remains a tombstone even if another discovery source lists the same launch action.
    public static void Deduplicate(AppState state)
    {
        // A late Shell record can connect a file target with an earlier AUMID-only record.
        // Repeat only while a record was actually removed; no polling or background work.
        while (DeduplicatePass(state)) { }
    }

    private static bool DeduplicatePass(AppState state)
    {
        var originalCount = state.Apps.Count;
        var result = new List<AppEntry>(); var index = new Dictionary<string, List<AppEntry>>(StringComparer.Ordinal);
        var redirects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in state.Apps)
        {
            var keys = AppIdentity.Keys(app).ToArray();
            var existing = keys.Where(index.ContainsKey).SelectMany(key => index[key]).Distinct()
                .FirstOrDefault(candidate => AppIdentity.AreEquivalent(candidate, app));
            if (existing == null) { existing = app; result.Add(app); }
            else
            {
                redirects[app.Id] = existing.Id;
                MergeMetadata(existing, app, LaunchRank(app) < LaunchRank(existing));
                if (!existing.CategoryIsUserAssigned && app.CategoryIsUserAssigned)
                { existing.Category = app.Category; existing.CategoryIsUserAssigned = true; existing.CategoryRuleVersion = app.CategoryRuleVersion; }
                existing.Hidden |= app.Hidden;
            }
            foreach (var key in keys.Concat(AppIdentity.Keys(existing)).Distinct())
            {
                if (!index.TryGetValue(key, out var matches)) index[key] = matches = [];
                if (!matches.Contains(existing)) matches.Add(existing);
            }
        }
        state.Apps = result;
        state.PinnedIds = state.PinnedIds.Select(id => redirects.GetValueOrDefault(id, id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return result.Count < originalCount;
    }

    private static void MergeMetadata(AppEntry existing, AppEntry incoming, bool replaceLaunch)
    {
        var aumid = string.IsNullOrWhiteSpace(incoming.AppUserModelId) ? existing.AppUserModelId : incoming.AppUserModelId;
        if (replaceLaunch)
        {
            existing.Name = incoming.Name; existing.LaunchPath = incoming.LaunchPath;
            existing.Arguments = incoming.Arguments; existing.WorkingDirectory = incoming.WorkingDirectory;
            existing.ExecutablePath = incoming.ExecutablePath; existing.Source = incoming.Source;
        }
        if (replaceLaunch || string.IsNullOrWhiteSpace(existing.AppUserModelId)) existing.AppUserModelId = aumid;
        existing.DesktopPaths = (existing.DesktopPaths ?? []).Concat(incoming.DesktopPaths ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (string.IsNullOrEmpty(existing.CustomIconPath)) existing.CustomIconPath = incoming.CustomIconPath;
    }

    private static int LaunchRank(AppEntry app) => app.Source switch { "StartMenu" => 0, "Desktop" => 1, "Manual" => 2, "AppsFolder" => 4, _ => 3 };

    private static void UpdateAutomaticCategory(AppEntry app)
    {
        if (app.CategoryRuleVersion == 0 && !app.CategoryIsUserAssigned)
            app.CategoryIsUserAssigned = !AppCategories.WasAutomatic(app.Category ?? "", app.Name ?? "");
        if (!app.CategoryIsUserAssigned && app.CategoryRuleVersion < CategoryRuleVersion) app.Category = SuggestCategory(app);
        app.CategoryRuleVersion = CategoryRuleVersion;
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
            app.CustomIconPath = NormalizeIconPath(app.CustomIconPath);
            if (string.IsNullOrWhiteSpace(app.Category)) app.Category = "其他";
            UpdateAutomaticCategory(app);
        }
        Deduplicate(state);
        foreach (var app in state.Apps) if (!state.Categories.Contains(app.Category)) state.Categories.Add(app.Category);
        if (!state.Categories.Contains("其他")) state.Categories.Add("其他");
        var ids = state.Apps.Where(a => !a.Hidden).Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        state.PinnedIds = state.PinnedIds.Where(ids.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var s = state.Settings;
        if (!new[] { "Clear", "Frosted" }.Contains(s.LiveGlassStyle)) s.LiveGlassStyle = "Clear";
        s.BackdropOpacity = double.IsFinite(s.BackdropOpacity) ? Math.Clamp(s.BackdropOpacity, .25, .9) : .65;
        s.Opacity = double.IsFinite(s.Opacity) ? Math.Clamp(s.Opacity, .55, 1) : .88;
        s.GlassClarity = double.IsFinite(s.GlassClarity) ? Math.Clamp(s.GlassClarity, .1, 1) : .65;
        s.GlassFlowStrength = double.IsFinite(s.GlassFlowStrength) ? Math.Clamp(s.GlassFlowStrength, 0, 1) : .45;
        if (!new[] { "Lagoon", "Jade", "Amber", "Iris", "Rose", "Graphite" }.Contains(s.Accent)) s.Accent = "Lagoon";
        if (!new[] { "Capsule", "Pebble", "Rail" }.Contains(s.DockShape)) s.DockShape = "Capsule";
        s.IconSize = double.IsFinite(s.IconSize) ? Math.Clamp(s.IconSize, 32, 80) : 48;
        s.Spacing = double.IsFinite(s.Spacing) ? Math.Clamp(s.Spacing, 4, 24) : 10;
        s.CornerRadius = double.IsFinite(s.CornerRadius) ? Math.Clamp(s.CornerRadius, 0, 40) : 24;
        s.AnimationStrength = double.IsFinite(s.AnimationStrength) ? Math.Clamp(s.AnimationStrength, 0, 1) : .45;
        s.Columns = Math.Clamp(s.Columns, 3, 10);
        s.NameFontSize = double.IsFinite(s.NameFontSize) ? Math.Clamp(s.NameFontSize, 10, 40) : 15;
        s.NameFontFamily = (s.NameFontFamily ?? "").Trim();
        s.TextColor = NormalizeTextColor(s.TextColor);
        // Family names only: imported configuration must not turn a font choice into a URI.
        if (s.NameFontFamily.Length > 80 || s.NameFontFamily.Any(c => char.IsControl(c) || c is ':' or '/' or '\\' or '#')) s.NameFontFamily = "";
        if (!new[] { "Bottom", "Top", "Left", "Right" }.Contains(s.Position)) s.Position = "Bottom";
        if (!new[] { "System", "Light", "Dark" }.Contains(s.Theme)) s.Theme = "System";
    }

    public static string NormalizeTextColor(string? value)
    {
        value = (value ?? "").Trim();
        return value.Length == 7 && value[0] == '#' && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF".AsSpan()) < 0 ? value.ToUpperInvariant() : "";
    }

    public static string NormalizeIconPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl)
            || value.StartsWith("\\\\", StringComparison.Ordinal) || value.Contains("://", StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(value)) return "";
        try
        {
            var path = Path.GetFullPath(value);
            if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.IndexOf(':', 2) >= 0) return "";
            return new[] { ".ico", ".png", ".jpg", ".jpeg", ".exe", ".dll", ".lnk" }.Contains(Path.GetExtension(path).ToLowerInvariant()) ? path : "";
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return ""; }
    }
}
