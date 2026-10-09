using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace YTWin_RichPresence {
    public static class Constants {
        public static string ProgramVersionBase {
            get {
                try {
                    var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                    if (exePath == null) {
                        return "";
                    }
                    FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(exePath);
                    return $"v{fvi.FileVersion}";
                } catch (Exception ex) {
                    new Logger().Log($"Error getting version string: {ex}");
                    return "";
                }
            }
        }
#if RELEASE
        public static string  ProgramVersion = ProgramVersionBase;
#else
        public static string  ProgramVersion = $"{ProgramVersionBase}-dev";
#endif
        public static int    MaxLogFiles                    = 10;    // files
        public static int    RefreshPeriod                  = 5;     // seconds
        public static int    NumFailedSearchesBeforeAbandon = 3;     // attempts per video before we stop looking it up
        public static int    ExtensionReportMaxAge          = 10;    // seconds before a report from the browser extension is considered stale
        public static int    DefaultExtensionBridgePort     = 48271;
        public static string ApplicationStylisedName        = "YTWin-RichPresence";
        public static string LastFMCredentialTargetName     = "YTWin-RP Last FM Password";
        public static int    LastFMTimeBeforeScrobbling     = 20;    // seconds
        public static string GithubReleasesApiUrl           = "https://api.github.com/repos/amanetoki7/YTWin-RP/releases/latest";
        public static string GithubReleasesUrl              = "https://github.com/amanetoki7/YTWin-RP/releases";
        public static string GithubRepoUrl                  = "https://github.com/amanetoki7/YTWin-RP/";
        public static string DiscordDeveloperPortalUrl      = "https://discord.com/developers/applications";

        // The Discord application whose name Discord shows as "Watching ..." / "Listening to ...".
        // Create one in the Developer Portal (New Application -> name it "YouTube" -> copy the Application ID) and
        // put the ID here once, so users of your builds need no setup. The settings window can still override it.
        // The optional second application (e.g. named "YouTube Music") is used for songs.
        public static string DefaultDiscordClientID         = "1557977339667808287";
        public static string DefaultDiscordClientIDMusic    = "1557977849258975292";

        // Images: thumbnails are sent as URLs. The small icon next to the thumbnail is the application's own icon
        // (as configured in the Developer Portal), unless the application has Rich Presence assets with these names uploaded.
        public static string DiscordServiceAssetName        = "youtube";        // small icon for videos
        public static string DiscordMusicServiceAssetName   = "youtubemusic";   // small icon for songs
        public static string DiscordPauseAssetName          = "pause";
        public static string DiscordPauseIconUrl            = "https://upload.wikimedia.org/wikipedia/commons/thumb/d/dc/Pause_Button_icon_Apple.svg/250px-Pause_Button_icon_Apple.svg.png";

        public static string WindowsStartupFolder => Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        public static string WindowsAppDataFolder => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public static string AppDataFolder => Path.Combine(WindowsAppDataFolder, ApplicationStylisedName);
        public static string ResolveCacheFile => Path.Combine(AppDataFolder, "ResolveCache.json");
        public static string AppShortcutPath => Path.Join(WindowsStartupFolder, "YTWin-RP.lnk");
        public static string? ExePath => Process.GetCurrentProcess().MainModule?.FileName;
        public static string? ExeFolder => ExePath == null ? null : Path.GetDirectoryName(ExePath);
        public static string? ExtensionFolder => ExeFolder == null ? null : Path.Combine(ExeFolder, "extension");

        public static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };

        // Media sessions whose AppUserModelId contains one of these (case-insensitive) are treated as browsers.
        // Chrome reports "Chrome", Edge "MSEdge", Firefox its install hash (e.g. 308046B0AF4A39CB), PWAs contain "_crx_".
        public static readonly string[] BrowserAppUserModelIdHints = [
            "chrome", "msedge", "edge", "firefox", "308046B0AF4A39CB", "brave", "opera", "vivaldi",
            "chromium", "thorium", "arc", "zen", "floorp", "waterfox", "librewolf", "comet", "_crx_"
        ];
    }
}
