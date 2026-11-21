using PerformanceTracker.Core.Models;

namespace PerformanceTracker.Core.Services;

/// <summary>
/// Plain-language descriptions for well-known processes, plus the list of Windows
/// processes that must never be ended.
/// </summary>
public static class ProcessCatalog
{
    public sealed record Entry(string DisplayName, string Explanation, AppSafety Safety, string? Caution = null);

    private const string BrowserCaution = "Closes every window and tab. Most browsers offer to restore them next time.";
    private const string ScriptCaution = "Stops it mid-run. Anything it hasn't saved yet is lost.";

    private static readonly Dictionary<string, Entry> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        // Core Windows - never end.
        ["system"] = new("Windows kernel", "The core of Windows.", AppSafety.Protected),
        ["idle"] = new("System idle", "Not a real program - it represents unused CPU time.", AppSafety.Protected),
        ["registry"] = new("Windows registry", "Holds Windows settings in memory.", AppSafety.Protected),
        ["memory compression"] = new("Memory compression", "Windows squeezing rarely-used memory so more fits in RAM. It shrinks by itself when other apps close.", AppSafety.Protected),
        ["secure system"] = new("Secure system", "Windows security isolation.", AppSafety.Protected),
        ["smss"] = new("Windows session manager", "Starts Windows sessions.", AppSafety.Protected),
        ["csrss"] = new("Windows client/server runtime", "Core Windows process. Ending it crashes Windows.", AppSafety.Protected),
        ["wininit"] = new("Windows start-up", "Core Windows process.", AppSafety.Protected),
        ["winlogon"] = new("Windows sign-in", "Handles signing in and locking the PC.", AppSafety.Protected),
        ["services"] = new("Windows services manager", "Starts and stops background services.", AppSafety.Protected),
        ["lsass"] = new("Windows security", "Handles passwords and sign-in security.", AppSafety.Protected),
        ["lsaiso"] = new("Credential guard", "Isolated sign-in security.", AppSafety.Protected),
        ["svchost"] = new("Windows service host", "Runs built-in Windows services (networking, updates, audio, ...).", AppSafety.Protected),
        ["dwm"] = new("Desktop window manager", "Draws everything on your screen.", AppSafety.Protected),
        ["explorer"] = new("Windows Explorer", "Your taskbar, Start menu and File Explorer windows.", AppSafety.Protected),
        ["fontdrvhost"] = new("Font driver", "Draws text.", AppSafety.Protected),
        ["audiodg"] = new("Windows audio", "Processes sound.", AppSafety.Protected),
        ["ctfmon"] = new("Text input", "Keyboard and language input.", AppSafety.Protected),
        ["sihost"] = new("Shell infrastructure", "Part of the Windows desktop.", AppSafety.Protected),
        ["taskhostw"] = new("Task host", "Runs scheduled Windows tasks.", AppSafety.Protected),
        ["runtimebroker"] = new("Runtime broker", "Manages app permissions for Store apps.", AppSafety.Protected),
        ["startmenuexperiencehost"] = new("Start menu", "The Start menu.", AppSafety.Protected),
        ["shellexperiencehost"] = new("Shell experience", "Taskbar pop-ups and notifications.", AppSafety.Protected),
        ["searchhost"] = new("Windows search", "The search box on the taskbar.", AppSafety.Protected),
        ["textinputhost"] = new("Touch keyboard", "On-screen keyboard and emoji picker.", AppSafety.Protected),
        ["applicationframehost"] = new("App frame host", "Draws windows for Store apps.", AppSafety.Protected),
        ["conhost"] = new("Console window host", "Belongs to an open terminal or script. Ending it can break that terminal.", AppSafety.Protected),
        ["dllhost"] = new("COM surrogate", "Helper Windows starts for thumbnails and other tasks.", AppSafety.Protected),
        ["wmiprvse"] = new("WMI provider", "Answers system-information requests.", AppSafety.Protected),
        ["spoolsv"] = new("Print spooler", "Handles printing.", AppSafety.Protected),
        ["wudfhost"] = new("Driver host", "Runs device drivers.", AppSafety.Protected),
        ["msmpeng"] = new("Microsoft Defender", "Antivirus scanning.", AppSafety.Protected),
        ["nissrv"] = new("Microsoft Defender network", "Network attack protection.", AppSafety.Protected),
        ["mpdefendercoreservice"] = new("Microsoft Defender core", "Antivirus service.", AppSafety.Protected),
        ["securityhealthservice"] = new("Windows Security", "Windows Security health checks.", AppSafety.Protected),
        ["smartscreen"] = new("SmartScreen", "Checks downloads and apps for malware.", AppSafety.Protected),
        ["sgrmbroker"] = new("System guard", "Windows integrity protection.", AppSafety.Protected),
        ["vmmem"] = new("Virtual machine memory", "Memory used by WSL (Linux), Docker or Hyper-V.", AppSafety.Protected, "Run `wsl --shutdown` or quit Docker Desktop to release it."),
        ["vmmemwsl"] = new("WSL memory", "Memory used by Linux (WSL) and Docker.", AppSafety.Protected, "Run `wsl --shutdown` or quit Docker Desktop to release it."),

        // Browsers.
        ["chrome"] = new("Google Chrome", "Web browser. Tabs, extensions, and browser features use multiple processes.", AppSafety.Caution, BrowserCaution),
        ["msedge"] = new("Microsoft Edge", "Web browser. Tabs, extensions, and browser features use multiple processes.", AppSafety.Caution, BrowserCaution),
        ["firefox"] = new("Firefox", "Web browser.", AppSafety.Caution, BrowserCaution),
        ["brave"] = new("Brave", "Web browser.", AppSafety.Caution, BrowserCaution),
        ["opera"] = new("Opera", "Web browser.", AppSafety.Caution, BrowserCaution),
        ["msedgewebview2"] = new("Edge WebView", "A browser engine other apps embed (Widgets, Teams, Outlook, many desktop apps).", AppSafety.Caution, "Apps that use it may go blank or crash until you reopen them."),

        // Virtual machines and containers.
        ["vmware-vmx"] = new("VMware virtual machine", "A virtual machine that is powered on.", AppSafety.Caution, "Like pulling the power cord on that VM. Shut it down or suspend it from VMware instead if you can."),
        ["vmware"] = new("VMware Workstation", "The VMware window. Closing it does not stop running VMs.", AppSafety.Normal),
        ["virtualboxvm"] = new("VirtualBox virtual machine", "A virtual machine that is powered on.", AppSafety.Caution, "Like pulling the power cord on that VM."),
        ["docker desktop"] = new("Docker Desktop", "Runs containers.", AppSafety.Caution, "Stops all running containers."),
        ["com.docker.backend"] = new("Docker engine", "Runs containers in the background.", AppSafety.Caution, "Stops all running containers."),

        // Developer tools.
        ["python"] = new("Python", "A Python script or tool.", AppSafety.Normal, ScriptCaution),
        ["pythonw"] = new("Python (no window)", "A Python script running without a window.", AppSafety.Normal, ScriptCaution),
        ["node"] = new("Node.js", "A JavaScript tool, dev server or build.", AppSafety.Normal, ScriptCaution),
        ["java"] = new("Java", "A Java program.", AppSafety.Normal, ScriptCaution),
        ["javaw"] = new("Java (no window)", "A Java program running without a window.", AppSafety.Normal, ScriptCaution),
        ["dotnet"] = new(".NET", "A .NET program, build or language server.", AppSafety.Normal, ScriptCaution),
        ["code"] = new("Visual Studio Code", "Code editor.", AppSafety.Caution, "Unsaved files may be lost."),
        ["devenv"] = new("Visual Studio", "Code editor.", AppSafety.Caution, "Unsaved files may be lost."),
        ["claude"] = new("Claude Code", "AI coding assistant session.", AppSafety.Normal, "Ends any session in progress."),
        ["codex"] = new("Codex", "AI coding assistant session.", AppSafety.Normal, "Ends any session in progress."),
        ["postgres"] = new("PostgreSQL", "Database server.", AppSafety.Service, "Apps using the database lose their connection."),
        ["mysqld"] = new("MySQL", "Database server.", AppSafety.Service, "Apps using the database lose their connection."),
        ["httpd"] = new("Apache web server", "Local web server.", AppSafety.Service),
        ["ollama"] = new("Ollama", "Runs local AI models and keeps recent ones loaded in memory.", AppSafety.Normal),
        ["ollama app"] = new("Ollama tray app", "Ollama's tray icon.", AppSafety.Normal),
        ["cloudflared"] = new("Cloudflare tunnel", "Exposes a local service through Cloudflare.", AppSafety.Service, "Anything reached through the tunnel goes offline."),
        ["tailscaled"] = new("Tailscale", "Private network between your devices.", AppSafety.Service, "Disconnects you from your Tailscale network."),

        // Everyday apps.
        ["spotify"] = new("Spotify", "Music player.", AppSafety.Normal),
        ["spotifylauncher"] = new("Spotify launcher", "Starts Spotify with Windows.", AppSafety.Normal),
        ["discord"] = new("Discord", "Chat and voice app.", AppSafety.Normal),
        ["ms-teams"] = new("Microsoft Teams", "Chat and meetings.", AppSafety.Normal),
        ["slack"] = new("Slack", "Team chat.", AppSafety.Normal),
        ["steam"] = new("Steam", "Game launcher.", AppSafety.Normal),
        ["steamwebhelper"] = new("Steam (browser parts)", "Steam's built-in browser.", AppSafety.Normal),
        ["onedrive"] = new("OneDrive", "Syncs files with the cloud.", AppSafety.Normal, "Sync pauses until OneDrive starts again."),
        ["onedrive.sync.service"] = new("OneDrive sync service", "Syncs files with the cloud.", AppSafety.Service),
        ["bingwallpaper"] = new("Bing Wallpaper", "Changes your desktop background daily.", AppSafety.Normal),
        ["phoneexperiencehost"] = new("Phone Link", "Connects your phone to the PC.", AppSafety.Normal),
        ["crossdeviceservice"] = new("Cross-device service", "Shares things between your Windows devices.", AppSafety.Normal),
        ["widgets"] = new("Widgets", "The Windows widgets board.", AppSafety.Normal),
        ["lghub"] = new("Logitech G HUB", "Mouse and keyboard settings.", AppSafety.Normal),
        ["lghub_updater"] = new("Logitech G HUB updater", "Keeps G HUB up to date.", AppSafety.Service),
        ["armourycrate.service"] = new("Armoury Crate service", "ASUS hardware control (fans, lighting).", AppSafety.Service),
        ["asus_framework"] = new("ASUS framework", "Support process for ASUS apps.", AppSafety.Normal),
        ["nvcontainer"] = new("NVIDIA container", "Runs NVIDIA app features (overlay, updates).", AppSafety.Service),
        ["t3 code (nightly)"] = new("T3 Code (Nightly)", "Coding app.", AppSafety.Caution, "Ends any running agent sessions."),
        ["t3 code"] = new("T3 Code", "Coding app.", AppSafety.Caution, "Ends any running agent sessions.")
    };

    public static Entry? Lookup(string processName) =>
        Known.TryGetValue(processName.Trim(), out Entry? entry) ? entry : null;

    public static bool IsProtected(string processName) =>
        Lookup(processName)?.Safety == AppSafety.Protected;
}
