using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace YTWin_RichPresence {

    internal sealed class YouTubeWindow {
        public YouTubeSite Site;
        public string PageTitle = "";     // the document title without the " - YouTube" suffix
        public string WindowTitle = "";
        public int ProcessId;

        public override string ToString() => $"[{Site}] {PageTitle} ({WindowTitle})";
    }

    /// <summary>
    /// Finds browser windows whose active tab is YouTube or YouTube Music by looking at top-level window titles
    /// (e.g. "(3) Video title - YouTube - Google Chrome"). This is cheap and never touches the browser's
    /// accessibility tree, at the cost of only seeing the foreground tab of each window.
    /// </summary>
    internal static class BrowserWindows {
        // "(11) Title - YouTube - Google Chrome", "Title - YouTube Music — Mozilla Firefox", "Title - YouTube - Microsoft​ Edge"
        private static readonly Regex TitleRegex = new(
            @"^(?:\(\d+\)\s*)?(?<title>.+?)\s+[-–—]\s+(?<site>YouTube Music|YouTube)(?:\s+[-–—]\s+.+)?$",
            RegexOptions.Compiled);

        public static List<YouTubeWindow> FindYouTubeWindows() {
            var found = new List<YouTubeWindow>();
            foreach (var (pid, title) in Win32.GetVisibleWindowTitles()) {
                var match = TitleRegex.Match(title);
                if (!match.Success) {
                    continue;
                }
                found.Add(new YouTubeWindow {
                    Site = match.Groups["site"].Value == "YouTube Music" ? YouTubeSite.YouTubeMusic : YouTubeSite.YouTube,
                    PageTitle = match.Groups["title"].Value.Trim(),
                    WindowTitle = title,
                    ProcessId = pid,
                });
            }
            return found;
        }

        public static string? GetProcessName(int pid) {
            try {
                using var p = Process.GetProcessById(pid);
                return p.ProcessName;
            } catch {
                return null;
            }
        }
    }

    internal static class Win32 {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLengthW(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

        public static List<(int pid, string title)> GetVisibleWindowTitles() {
            var titles = new List<(int, string)>();
            EnumWindows((hWnd, _) => {
                if (!IsWindowVisible(hWnd)) {
                    return true;
                }
                var length = GetWindowTextLengthW(hWnd);
                if (length <= 0) {
                    return true;
                }
                var sb = new StringBuilder(length + 1);
                if (GetWindowTextW(hWnd, sb, sb.Capacity) > 0) {
                    GetWindowThreadProcessId(hWnd, out var pid);
                    titles.Add(((int)pid, sb.ToString()));
                }
                return true;
            }, IntPtr.Zero);
            return titles;
        }
    }
}
