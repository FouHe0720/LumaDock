namespace LumaDock.Core;

public sealed class AppEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string LaunchPath { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string AppUserModelId { get; set; } = "";
    public string LaunchUri { get; set; } = "";
    public string CustomIconPath { get; set; } = "";
    public string Category { get; set; } = "";
    public bool CategoryIsUserAssigned { get; set; }
    public int CategoryRuleVersion { get; set; }
    public string Source { get; set; } = "Manual";
    public List<string> DesktopPaths { get; set; } = [];
    public bool Hidden { get; set; }
    public bool IsAvailable { get; set; } = true;
}

public sealed class DockSettings
{
    public string Theme { get; set; } = "System";
    public double Opacity { get; set; } = .70;
    public int DockMaterialVersion { get; set; }
    public bool IconReflection { get; set; } = true;
    public double ReflectionStrength { get; set; } = .24;
    public double DockIconVibrancy { get; set; } = .28;
    public bool LiquidGlass { get; set; } = true;
    public bool LiveGlass { get; set; } = true;
    public string LiveGlassStyle { get; set; } = "Clear";
    public double BackdropOpacity { get; set; } = .65;
    public double GlassClarity { get; set; } = .65;
    public bool GlassMotion { get; set; } = true;
    public double GlassFlowStrength { get; set; } = .45;
    public string Accent { get; set; } = "Lagoon";
    public double CornerRadius { get; set; } = 24;
    public double IconSize { get; set; } = 48;
    public double Spacing { get; set; } = 10;
    public string DockShape { get; set; } = "Capsule";
    public string Position { get; set; } = "Bottom";
    public bool AutoHide { get; set; }
    public bool DockSystemEnabled { get; set; } = true;
    public bool ShowRunningApps { get; set; } = true;
    public double DockStatusIconSize { get; set; } = 16;
    public double DockStatusFontSize { get; set; } = 12;
    public int DockStatusFontWeight { get; set; } = 600;
    public double AnimationStrength { get; set; } = .45;
    public bool Animations { get; set; } = true;
    public bool PowerSaver { get; set; }
    public string MonitorDevice { get; set; } = "";
    public int Columns { get; set; } = 6;
    public bool FollowSystemIconFont { get; set; } = true;
    public double NameFontSize { get; set; } = 15;
    public bool NameFontBold { get; set; }
    public string NameFontFamily { get; set; } = "";
    public bool NameFontItalic { get; set; }
    public string TextColor { get; set; } = "";
    public bool StartWithWindows { get; set; }
    public bool CloseToTray { get; set; } = true;
}

public sealed class AppState
{
    public int SchemaVersion { get; set; } = 2;
    public int OrganizationVersion { get; set; }
    public List<AppEntry> Apps { get; set; } = [];
    public List<string> PinnedIds { get; set; } = [];
    public List<string> Categories { get; set; } = [];
    public DockSettings Settings { get; set; } = new();
}
