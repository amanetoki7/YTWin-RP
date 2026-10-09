using System.Text.RegularExpressions;

namespace YTWin_RichPresence {
    /// <summary>
    /// Turns YouTube video titles like "Artist - Song (Official Music Video)" into something scrobblers accept.
    /// Only used when YouTube Music does not know the video as a song.
    /// </summary>
    internal static class TitleCleaner {
        private const string TagWords =
            @"official\s*(?:music\s*)?video|official\s*audio|official\s*lyrics?\s*video|official\s*visuali[sz]er|official\s*mv|official" +
            @"|lyrics?\s*video|lyrics?|audio|visuali[sz]er|mv|m/v|pv|music\s*video|hd|hq|4k|remaster(?:ed)?(?:\s*\d{4})?" +
            @"|full\s*(?:album|version|ver\.?)|公式|ミュージックビデオ|歌詞付き?|歌詞|フル|高音質";

        private static readonly Regex BracketTag = new(
            @"\s*[\(\[【『「〈《]\s*(?:" + TagWords + @")\s*[\)\]】』」〉》]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TrailingTag = new(
            @"\s*(?:[-|–—/]\s*)?(?:" + TagWords + @")\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TrailingSeparator = new(@"[\s\-|–—/:]+$", RegexOptions.Compiled);
        private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

        private static readonly Regex DashSplit = new(@"^(?<a>.+?)\s+[-–—]\s+(?<t>.+)$", RegexOptions.Compiled);
        private static readonly Regex JapaneseQuoteSplit = new(@"^(?<a>[^「『]+?)\s*[「『](?<t>[^」』]+)[」』]\s*$", RegexOptions.Compiled);

        public static string Clean(string title) {
            if (string.IsNullOrWhiteSpace(title)) {
                return title;
            }
            var s = title;
            for (var i = 0; i < 4; i++) {
                var next = BracketTag.Replace(s, "");
                next = TrailingTag.Replace(next, "");
                if (next == s) {
                    break;
                }
                s = next;
            }
            s = TrailingSeparator.Replace(s, "");
            s = Whitespace.Replace(s, " ").Trim();
            return s.Length == 0 ? title.Trim() : s;
        }

        /// <summary>Splits "Artist - Title" or "Artist「Title」" into its parts; artist is null when the title has no such shape.</summary>
        public static (string? Artist, string Title) SplitArtistTitle(string cleanedTitle) {
            var m = DashSplit.Match(cleanedTitle);
            if (m.Success) {
                return (m.Groups["a"].Value.Trim(), m.Groups["t"].Value.Trim());
            }
            m = JapaneseQuoteSplit.Match(cleanedTitle);
            if (m.Success) {
                return (m.Groups["a"].Value.Trim(), m.Groups["t"].Value.Trim());
            }
            return (null, cleanedTitle);
        }
    }
}
