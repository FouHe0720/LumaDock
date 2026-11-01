using System.Text.RegularExpressions;

namespace LumaDock.Core;

internal static class AppCategories
{
    private static readonly Regex Words = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static string Suggest(string name, string executablePath = "")
    {
        var text = (name ?? "").ToLowerInvariant();
        var executable = Path.GetFileNameWithoutExtension((executablePath ?? "").Replace('\\', '/')).ToLowerInvariant();
        var words = Words.Matches(text).Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
        bool Has(params string[] fragments) => fragments.Any(text.Contains);
        bool Word(params string[] values) => values.Any(words.Contains);
        bool Exe(params string[] values) => values.Contains(executable, StringComparer.Ordinal);
        bool NameIs(params string[] values) => values.Contains(text.Trim(), StringComparer.Ordinal);

        // Maintenance/help shortcuts are intentionally separate launch actions, rather than duplicate applications.
        if (Has("卸载", "解除安装", "uninstall", "unins000", "获取帮助", "反馈中心", "快速助手", "单击以执行", "安全中心") || Exe("uninstall", "unins000")) return "系统工具";
        if (Has("visual studio", "visualstudio", "android studio", "intellij", "pycharm", "webstorm", "phpstorm", "datagrip", "goland", "clion", "rubymine", "vscodium", "github desktop", "git bash", "git gui", "sublime text", "jupyter", "node.js", "nodejs", "windows terminal", "jetbrains", "开发工具", "开发者", "终端", "命令提示符")
            || Has("cc switch", "opencode", "monkeycode", "qoder", "dev home", "miniforge", "ubuntu-")
            || Word("code", "vscode", "codex", "cursor", "windsurf", "trae", "git", "python", "python3", "docker", "rider", "postman", "insomnia", "powershell", "cmake", "rustup", "dbeaver", "anaconda", "miniconda", "heidisql", "navicat", "unity", "unreal", "wsl")
            || Exe("code", "codex", "cursor", "devenv", "idea64", "pycharm64", "rider64", "docker desktop", "windowsterminal", "wt", "powershell", "pwsh")) return "开发工具";
        if (Has("microsoft edge", "google chrome", "firefox", "vivaldi", "brave", "chromium", "waterfox", "librewolf", "浏览器")
            || Word("chrome", "edge", "msedge", "opera", "browser") || Exe("msedge", "chrome", "firefox", "opera", "brave", "vivaldi")) return "浏览器";
        if (Has("无畏契约", "英雄联盟", "原神", "崩坏", "星穹铁道", "绝区零", "永劫无间", "黑神话", "我的世界", "守望先锋", "魔兽", "炉石", "地下城", "穿越火线", "绝地求生", "逆战", "王者荣耀", "蛋仔", "三角洲行动", "燕云", "战网", "暴雪", "雷神加速器", "uu加速器", "迅游", "奇游", "epic games", "riot client", "riot games", "ea app", "ubisoft connect", "游戏")
            || Word("steam", "xbox", "valorant", "minecraft", "genshin", "overwatch", "battle.net", "wegame", "playnite", "game", "games")
            || Exe("steam", "wegame", "valorant", "minecraftlauncher", "epicgameslauncher", "riotclientservices", "leigod")) return "游戏";
        if (Has("adobe", "photoshop", "illustrator", "after effects", "premiere", "lightroom", "indesign", "figma", "sketchbook", "blender", "cinema 4d", "davinci resolve", "autodesk", "fusion", "solidworks", "autocad", "rhino", "krita", "inkscape", "gimp", "clipchamp", "剪映", "万兴喵影", "醒图", "美图秀秀", "绘画", "建模")
            || Exe("photoshop", "illustrator", "blender", "figma", "fusion360", "acad", "sldworks", "krita", "inkscape")) return "创作设计";
        if (Has("微信", "企业微信", "飞书", "钉钉", "腾讯会议", "腾讯qq", "腾讯 qq", "skype", "discord", "telegram", "whatsapp", "slack", "microsoft teams", "zoom", "thunderbird", "outlook", "localsend", "邮件")
            || Word("qq", "wechat", "wecom", "lark", "feishu", "dingtalk", "teams")
            || Exe("wechat", "weixin", "qq", "qqnt", "lark", "feishu", "dingtalk", "wemeetapp", "teams", "ms-teams", "outlook")) return "社交通讯";
        if (Has("microsoft word", "microsoft excel", "powerpoint", "onenote", "office", "notion", "obsidian", "typora", "wps", "有道", "印象笔记", "文档", "笔记", "学习", "翻译", "词典", "便笺", "sticky notes", "acrobat", "foxit", "sumatrapdf", "calibre", "zotero", "anki", "chatgpt", "豆包", "microsoft to do") || NameIs("claude")
            || Word("word", "excel", "access", "publisher", "pdf", "todo", "to-do", "onenote", "libreoffice")
            || Exe("winword", "excel", "powerpnt", "onenote", "wps", "et", "wpp", "acrord32", "acrobat", "sumatrapdf")) return "办公学习";
        if (Has("music", "spotify", "foobar2000", "potplayer", "mediaplayer", "media player", "windows media", "netflix", "disney+", "bilibili", "哔哩哔哩", "音乐", "视频", "影视", "电影", "照片", "播放器", "爱奇艺", "优酷", "酷狗", "酷我", "网易云", "汽水", "喜马拉雅", "wallpaper engine", "lively wallpaper", "microsoft 资讯")
            || Word("video", "player", "vlc", "mpv", "itunes", "groove", "plex", "kodi", "obs", "audacity")
            || Exe("vlc", "mpv", "potplayermini64", "potplayermini", "cloudmusic", "qqmusic", "kugou", "wmplayer", "obs64", "wallpaper64")) return "影音娱乐";
        if (Has("设置", "控制面板", "管理器", "系统", "工具", "记事本", "计算器", "计算机", "资源管理", "注册表", "磁盘", "任务计划", "远程桌面", "截图工具", "截图和草图", "画图", "时钟", "闹钟", "天气", "地图", "相机", "录音机", "设备", "打印", "文件恢复", "字符映射", "放大镜", "屏幕键盘", "讲述人", "辅助", "system font size", "font size changer", "odbc data sources", "lenovo ", "atrust", "windows 备份", "windows defender firewall")
            || NameIs("入门", "手机连接", "运行", "amd software", "character map", "command prompt", "component services", "computer management", "console rar manual", "dfrgui", "disk cleanup", "event viewer", "family", "flclash", "iscsi initiator", "livecaptions", "magnify", "memory diagnostics tool", "microsoft store", "narrator", "nvidia app", "on-screen keyboard", "performance monitor", "power automate", "recoverydrive", "remote desktop connection", "resource monitor", "run", "services", "steps recorder", "system configuration", "system information", "task scheduler", "translucenttb", "voiceaccess", "lumadock", "uu远程")
            || Word("settings", "calculator", "notepad", "control", "explorer", "paint", "snipping", "7zip", "7-zip", "winrar", "everything", "powertoys", "rufus", "hwinfo", "cpu-z", "gpu-z", "windirstat", "treesize", "changesize")
            || Exe("notepad", "calc", "calculatorapp", "mspaint", "snippingtool", "control", "explorer", "regedit", "taskmgr", "7zfm", "winrar", "everything", "powertoys", "changesize", "geek")) return "系统工具";
        return "其他";
    }

    // Version 0 had two slightly different rules, depending on whether discovery or Catalog assigned the value.
    internal static bool WasAutomatic(string category, string name)
    {
        if (string.IsNullOrWhiteSpace(category) || category == "其他") return true;
        var n = name.ToLowerInvariant();
        string Old(bool discovery)
        {
            if ((discovery ? new[] { "visual", "code", "terminal", "python", "git", "docker", "开发", "powershell" } : ["visual studio", "code", "git", "python", "terminal", "docker", "idea", "rider"]).Any(n.Contains)) return "开发工具";
            if ((discovery ? new[] { "edge", "chrome", "firefox", "browser", "浏览" } : ["chrome", "edge", "firefox", "browser", "浏览器"]).Any(n.Contains)) return "浏览器";
            if ((discovery ? new[] { "word", "excel", "office", "note", "pdf", "文档", "笔记", "学习" } : ["word", "excel", "powerpoint", "office", "onenote", "notion", "学习"]).Any(n.Contains)) return "办公学习";
            if ((discovery ? new[] { "music", "video", "player", "音乐", "视频", "照片" } : ["music", "spotify", "vlc", "音乐", "视频"]).Any(n.Contains)) return "影音娱乐";
            if (new[] { "steam", "game", "游戏", "xbox" }.Any(n.Contains)) return "游戏";
            if ((discovery ? new[] { "设置", "管理", "工具", "控制", "system", "windows", "计算器" } : ["设置", "管理", "control", "settings", "explorer", "记事本", "计算器"]).Any(n.Contains)) return "系统工具";
            return "其他";
        }
        return category == Old(false) || category == Old(true);
    }
}
