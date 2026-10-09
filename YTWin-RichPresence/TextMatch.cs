using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace YTWin_RichPresence {
    /// <summary>Fuzzy-but-strict comparisons used to match browser metadata against YouTube search results.</summary>
    internal static class TextMatch {
        private static readonly Regex InvisibleChars = new(@"[​-‏‪-‮﻿]", RegexOptions.Compiled);
        private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex TopicSuffix = new(@"\s*-\s*TOPIC$", RegexOptions.Compiled);
        private static readonly Regex ArtistSeparators = new(@"\s*(?:,|、|/|&|×|\+|;|\s[xX]\s|\sFEAT\.?\s|\sFT\.?\s|\sFEATURING\s|\sAND\s|\sWITH\s)\s*", RegexOptions.Compiled);

        public static string Normalize(string? s) {
            if (string.IsNullOrEmpty(s)) {
                return "";
            }
            var n = s.Normalize(NormalizationForm.FormKC);
            n = InvisibleChars.Replace(n, "");
            n = Whitespace.Replace(n, " ").Trim();
            return n.ToUpperInvariant();
        }

        /// <summary>Letters and digits only, so "Title｜MV" and "Title | MV" compare equal.</summary>
        public static string LooseKey(string? s) {
            var n = Normalize(s);
            var sb = new StringBuilder(n.Length);
            foreach (var c in n) {
                if (char.IsLetterOrDigit(c)) {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        public static bool TitleEquals(string? a, string? b) {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) {
                return false;
            }
            if (Normalize(a) == Normalize(b)) {
                return true;
            }
            var la = LooseKey(a);
            return la.Length > 0 && la == LooseKey(b);
        }

        private static string StripArtistNoise(string normalized) {
            var s = TopicSuffix.Replace(normalized, "");
            s = s.Replace("VEVO", "").Replace(" OFFICIAL", "").Replace(" CHANNEL", "").Trim();
            return s;
        }

        /// <summary>
        /// True when two channel/artist strings plausibly describe the same act. YouTube reports the uploading
        /// channel in some places ("Ayase / YOASOBI") and the credited artists in others ("YOASOBI, Echoes"),
        /// so this accepts substrings and any shared artist in a list.
        /// </summary>
        public static bool ArtistMatches(string? a, string? b) {
            var na = StripArtistNoise(Normalize(a));
            var nb = StripArtistNoise(Normalize(b));
            if (na.Length == 0 || nb.Length == 0) {
                return false;
            }
            if (na == nb) {
                return true;
            }
            if ((na.Length >= 3 && nb.Contains(na)) || (nb.Length >= 3 && na.Contains(nb))) {
                return true;
            }
            var partsA = ArtistSeparators.Split(na).Select(p => p.Trim()).Where(p => p.Length >= 2).ToList();
            var partsB = ArtistSeparators.Split(nb).Select(p => p.Trim()).Where(p => p.Length >= 2).ToList();
            return partsA.Any(pa => partsB.Any(pb => pa == pb || LooseKey(pa) == LooseKey(pb)));
        }

        public static bool DurationClose(int? a, double? b, double toleranceSeconds = 3) {
            if (a == null || b == null) {
                return false;
            }
            return Math.Abs(a.Value - b.Value) <= toleranceSeconds;
        }

        public static bool DurationConflicts(int? a, double? b, double toleranceSeconds = 5) {
            if (a == null || b == null) {
                return false;
            }
            return Math.Abs(a.Value - b.Value) > toleranceSeconds;
        }

        /// <summary>First artist of a list like "A, B & C" or "A feat. B".</summary>
        public static string PrimaryArtist(string artist) {
            if (string.IsNullOrWhiteSpace(artist)) {
                return artist;
            }
            var separators = new[] { " & ", " , ", ",", "、", " feat. ", " ft. ", " feat ", " / ", " × ", " x " };
            var primary = artist;
            foreach (var sep in separators) {
                var index = primary.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
                if (index > 0) {
                    primary = primary.Substring(0, index);
                }
            }
            return primary.Trim();
        }
    }
}
