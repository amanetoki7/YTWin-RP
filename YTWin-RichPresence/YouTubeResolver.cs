using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace YTWin_RichPresence {

    /// <summary>Everything we know about a video once it has been identified.</summary>
    internal sealed class ResolvedMedia {
        public string VideoId { get; set; } = "";
        public MediaKind Kind { get; set; }
        public string? MusicVideoType { get; set; }
        public string? Category { get; set; }
        public string? YouTubeTitle { get; set; }
        public string? ChannelName { get; set; }
        public string? ChannelId { get; set; }
        public string? SongTitle { get; set; }
        public string? SongArtist { get; set; }
        public string? SongAlbum { get; set; }
        public string? ThumbnailUrl { get; set; }
        public int? DurationSeconds { get; set; }
        public DateTime ResolvedAtUtc { get; set; } = DateTime.UtcNow;
        /// <summary>Only search data is available (details could not be fetched); retried after a short while.</summary>
        public bool IsPartial { get; set; }
    }

    /// <summary>
    /// Turns "title + channel" (what the browser tells Windows) or a video ID (what the extension tells us)
    /// into a video ID, a thumbnail and a verdict on whether it is music.
    ///
    /// Music detection follows the idea of checking whether the same video exists on YouTube Music:
    /// YouTube Music's player endpoint returns <c>musicVideoType</c> only for videos in its catalogue
    /// (ATV = auto-generated song, OMV = official music video, UGC = user upload in the Music category, ...).
    /// </summary>
    internal sealed class YouTubeResolver {
        private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan PartialTtl = TimeSpan.FromMinutes(2);
        private const int MaxPersistedEntries = 500;
        private const int CacheVersion = 2;   // bump when the shape or meaning of ResolvedMedia changes

        private sealed class CacheFile {
            public int Version { get; set; }
            public Dictionary<string, ResolvedMedia>? Entries { get; set; }
        }
        private static readonly Regex TopicSuffix = new(@"\s*-\s*Topic$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly string[] MusicCategoryNames = [
            "Music", "音楽", "음악", "Musik", "Música", "Musique", "Музыка", "Müzik", "音乐", "音樂", "Musica", "Muziek", "Muzyka"
        ];
        private static readonly HashSet<string> SongTypes = [
            "MUSIC_VIDEO_TYPE_ATV", "MUSIC_VIDEO_TYPE_OMV", "MUSIC_VIDEO_TYPE_OFFICIAL_SOURCE_MUSIC", "MUSIC_VIDEO_TYPE_PRIVATELY_OWNED_TRACK"
        ];

        private readonly InnerTubeClient client;
        private readonly Logger? logger;
        private readonly ConcurrentDictionary<string, ResolvedMedia> cache = new();
        private readonly ConcurrentDictionary<string, DateTime> negativeUntil = new();
        private readonly object saveLock = new();
        private Timer? saveTimer;

        /// <summary>When false everything is reported as a video (lookups still happen for the thumbnail and links).</summary>
        public bool DetectMusic { get; set; } = true;

        public YouTubeResolver(InnerTubeClient client, Logger? logger = null) {
            this.client = client;
            this.logger = logger;
            LoadCache();
        }

        private static string IdKey(string videoId) => "id:" + videoId;
        private static string QueryKey(YouTubeSite site, string title, string artist) => $"q:{site}:{TextMatch.Normalize(title)}|{TextMatch.Normalize(artist)}";

        private ResolvedMedia? Cached(string key) {
            if (!cache.TryGetValue(key, out var hit)) {
                return null;
            }
            if (hit.IsPartial && DateTime.UtcNow - hit.ResolvedAtUtc > PartialTtl) {
                cache.TryRemove(key, out _);
                return null;
            }
            return hit;
        }

        private bool IsNegative(string key) => negativeUntil.TryGetValue(key, out var until) && until > DateTime.UtcNow;

        private void MarkNegative(string key) => negativeUntil[key] = DateTime.UtcNow + NegativeTtl;

        private void Store(string key, ResolvedMedia result) {
            cache[key] = result;
            if (!result.IsPartial || !cache.ContainsKey(IdKey(result.VideoId))) {
                cache[IdKey(result.VideoId)] = result;
            }
            if (!result.IsPartial) {
                ScheduleSave();
            }
        }

        // ------------------------------------------------------------------
        //  By video ID (browser extension)
        // ------------------------------------------------------------------

        public async Task<ResolvedMedia?> ResolveByIdAsync(string videoId, YouTubeSite site) {
            var key = IdKey(videoId);
            var hit = Cached(key);
            if (hit != null && !hit.IsPartial) {
                return hit;
            }
            if (IsNegative(key)) {
                return hit;
            }
            try {
                var result = await FetchDetailsAsync(videoId, site).ConfigureAwait(false);
                if (result == null) {
                    MarkNegative(key);
                    return hit;
                }
                logger?.Log($"[Resolver] {videoId} -> {result.Kind} (type={result.MusicVideoType ?? "-"}, category={result.Category ?? "-"})");
                Store(key, result);
                return result;
            } catch (Exception ex) {
                logger?.Log($"[Resolver] Lookup failed for {videoId}: {ex.Message}");
                return hit;
            }
        }

        private async Task<ResolvedMedia?> FetchDetailsAsync(string videoId, YouTubeSite site) {
            var details = await client.GetMusicPlayerAsync(videoId).ConfigureAwait(false)
                ?? await client.GetYouTubePlayerAsync(videoId).ConfigureAwait(false);
            if (details == null) {
                return null;
            }
            var kind = Classify(details, site);
            MusicNextInfo? next = null;
            if (kind == MediaKind.Music && details.FromYouTubeMusic) {
                next = await client.GetMusicNextAsync(videoId).ConfigureAwait(false);
            }
            return Build(details, next, kind);
        }

        private MediaKind Classify(VideoDetails d, YouTubeSite site) {
            if (!DetectMusic) {
                return MediaKind.Video;
            }
            if (site == YouTubeSite.YouTubeMusic) {
                return MediaKind.Music;
            }
            if (d.MusicVideoType != null) {
                if (SongTypes.Contains(d.MusicVideoType)) {
                    return MediaKind.Music;
                }
                if (d.MusicVideoType == "MUSIC_VIDEO_TYPE_UGC") {
                    // fan uploads, covers, lyric videos: YouTube Music lists them, so trust the uploader's category
                    return IsMusicCategory(d.Category) ? MediaKind.Music : MediaKind.Video;
                }
                if (d.MusicVideoType == "MUSIC_VIDEO_TYPE_PODCAST_EPISODE") {
                    return MediaKind.Video;
                }
                return MediaKind.Music;
            }
            // Not in YouTube Music's catalogue. The category is only trusted when YouTube Music could not be asked at all.
            return !d.FromYouTubeMusic && IsMusicCategory(d.Category) ? MediaKind.Music : MediaKind.Video;
        }

        private static bool IsMusicCategory(string? category) {
            return category != null && MusicCategoryNames.Any(n => string.Equals(n, category.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private static ResolvedMedia Build(VideoDetails d, MusicNextInfo? next, MediaKind kind) {
            var isMusic = kind == MediaKind.Music;
            string? songTitle = null;
            string? songArtist = null;
            if (isMusic) {
                if (d.FromYouTubeMusic && d.MusicVideoType != null) {
                    songTitle = d.Title;
                    // the channel/author name is canonical (and what scrobblers expect); the "next" byline is localised
                    songArtist = !string.IsNullOrWhiteSpace(d.Author) ? d.Author : next?.Artist;
                } else {
                    var (artist, title) = TitleCleaner.SplitArtistTitle(TitleCleaner.Clean(d.Title));
                    songTitle = title;
                    songArtist = artist ?? d.Author;
                }
            }

            // square album art (googleusercontent) beats a 16:9 video frame for songs
            // prefer JPEG over WebP (some Discord clients do not render WebP), then the largest
            var thumbnail = d.Thumbnails
                .OrderBy(t => t.Url.Contains("webp", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenByDescending(t => t.Width * t.Height)
                .FirstOrDefault()?.Url;
            if (isMusic && next?.ThumbnailUrl != null && next.ThumbnailUrl.Contains("googleusercontent.com") && !(thumbnail?.Contains("googleusercontent.com") ?? false)) {
                thumbnail = next.ThumbnailUrl;
            }

            return new ResolvedMedia {
                VideoId = d.VideoId,
                Kind = kind,
                MusicVideoType = d.MusicVideoType,
                Category = d.Category,
                YouTubeTitle = d.YouTubeTitle ?? d.Title,
                ChannelName = d.PageOwnerName ?? d.Author,
                ChannelId = d.ChannelId,
                SongTitle = songTitle,
                SongArtist = songArtist,
                SongAlbum = isMusic ? next?.Album : null,
                ThumbnailUrl = thumbnail,
                DurationSeconds = d.LengthSeconds,
            };
        }

        // ------------------------------------------------------------------
        //  By title + channel/artist (Windows media session)
        // ------------------------------------------------------------------

        public async Task<ResolvedMedia?> ResolveByMetadataAsync(YouTubeSite site, string title, string artist, string? album, double? durationSeconds) {
            if (string.IsNullOrWhiteSpace(title)) {
                return null;
            }
            var key = QueryKey(site, title, artist);
            var hit = Cached(key);
            if (hit != null) {
                return hit;
            }
            if (IsNegative(key)) {
                return null;
            }

            ResolvedMedia? result = null;
            try {
                // YouTube Music search is the only place auto-generated "Topic" songs show up; YouTube search finds everything else
                var musicFirst = site == YouTubeSite.YouTubeMusic || !string.IsNullOrWhiteSpace(album);
                if (musicFirst) {
                    result = await ResolveViaMusicSearchAsync(title, artist, album, durationSeconds, site).ConfigureAwait(false);
                }
                result ??= await ResolveViaYouTubeSearchAsync(title, artist, durationSeconds, site).ConfigureAwait(false);
                if (!musicFirst) {
                    result ??= await ResolveViaMusicSearchAsync(title, artist, album, durationSeconds, site).ConfigureAwait(false);
                }
            } catch (Exception ex) {
                logger?.Log($"[Resolver] Lookup failed for '{title}' by '{artist}': {ex.Message}");
            }

            if (result == null) {
                logger?.Log($"[Resolver] Could not identify '{title}' by '{artist}' ({site})");
                MarkNegative(key);
                return null;
            }
            logger?.Log($"[Resolver] '{title}' by '{artist}' -> {result.VideoId} ({result.Kind}, type={result.MusicVideoType ?? "-"}{(result.IsPartial ? ", partial" : "")})");
            Store(key, result);
            return result;
        }

        private static string BuildQuery(string title, string artist) {
            var query = $"{title} {TopicSuffix.Replace(artist, "")}".Trim();
            while (query.Length > 120 && query.Contains(' ')) {
                query = query.Substring(0, query.LastIndexOf(' '));
            }
            return query;
        }

        private async Task<ResolvedMedia?> ResolveViaYouTubeSearchAsync(string title, string artist, double? duration, YouTubeSite site) {
            var results = await client.SearchYouTubeAsync(BuildQuery(title, artist)).ConfigureAwait(false);
            var exact = results
                .Where(r => TextMatch.TitleEquals(r.Title, title) && !TextMatch.DurationConflicts(r.DurationSeconds, duration))
                .ToList();
            if (exact.Count == 0) {
                logger?.Log($"[Resolver] None of {results.Count} YouTube results match the title '{title}'");
                return null;
            }

            int Score(YouTubeSearchResult r) {
                var score = 0;
                if (TextMatch.ArtistMatches(r.Owner, artist)) score += 2;
                if (TextMatch.DurationClose(r.DurationSeconds, duration)) score += 1;
                return score;
            }
            var ranked = exact.OrderByDescending(Score).ToList();

            YouTubeSearchResult? unverified = null;
            foreach (var candidate in ranked.Take(3)) {
                var details = await client.GetMusicPlayerAsync(candidate.VideoId).ConfigureAwait(false)
                    ?? await client.GetYouTubePlayerAsync(candidate.VideoId).ConfigureAwait(false);
                if (details == null) {
                    unverified ??= candidate;
                    continue;
                }
                var titleOk = TextMatch.TitleEquals(details.YouTubeTitle ?? details.Title, title) || TextMatch.TitleEquals(candidate.Title, title);
                var artistOk = string.IsNullOrWhiteSpace(artist)
                    || TextMatch.ArtistMatches(details.PageOwnerName, artist)
                    || TextMatch.ArtistMatches(details.Author, artist)
                    || TextMatch.ArtistMatches(candidate.Owner, artist);
                if (titleOk && (artistOk || ranked.Count == 1)) {
                    var kind = Classify(details, site);
                    var next = kind == MediaKind.Music && details.FromYouTubeMusic ? await client.GetMusicNextAsync(candidate.VideoId).ConfigureAwait(false) : null;
                    return Build(details, next, kind);
                }
                logger?.Log($"[Resolver] Rejected candidate {candidate} (title ok: {titleOk}, artist ok: {artistOk})");
            }

            if (unverified != null && (string.IsNullOrWhiteSpace(artist) || TextMatch.ArtistMatches(unverified.Owner, artist))) {
                // details could not be fetched; use the search result for now and retry later
                return new ResolvedMedia {
                    VideoId = unverified.VideoId,
                    Kind = MediaKind.Video,
                    YouTubeTitle = unverified.Title,
                    ChannelName = unverified.Owner,
                    ThumbnailUrl = unverified.ThumbnailUrl,
                    DurationSeconds = unverified.DurationSeconds,
                    IsPartial = true,
                };
            }
            return null;
        }

        private async Task<ResolvedMedia?> ResolveViaMusicSearchAsync(string title, string artist, string? album, double? duration, YouTubeSite site) {
            var results = await client.SearchYouTubeMusicSongsAsync(BuildQuery(title, artist)).ConfigureAwait(false);
            var titled = results
                .Where(r => TextMatch.TitleEquals(r.Title, title) && !TextMatch.DurationConflicts(r.DurationSeconds, duration))
                .ToList();
            if (titled.Count == 0) {
                logger?.Log($"[Resolver] None of {results.Count} YouTube Music results match the title '{title}'");
                return null;
            }

            var match = titled.FirstOrDefault(r => TextMatch.ArtistMatches(r.Artist, artist) && (album == null || r.Album == null || TextMatch.TitleEquals(r.Album, album)))
                ?? titled.FirstOrDefault(r => TextMatch.ArtistMatches(r.Artist, artist))
                ?? (string.IsNullOrWhiteSpace(artist) || TextMatch.DurationClose(titled[0].DurationSeconds, duration) ? titled[0] : null);
            if (match == null) {
                logger?.Log($"[Resolver] YouTube Music has '{title}' but not by '{artist}' (found: {string.Join("; ", titled.Select(t => t.Artist))})");
                return null;
            }

            var kind = DetectMusic ? MediaKind.Music : MediaKind.Video;
            var details = await client.GetMusicPlayerAsync(match.VideoId).ConfigureAwait(false);
            if (details != null) {
                var next = kind == MediaKind.Music ? await client.GetMusicNextAsync(match.VideoId).ConfigureAwait(false) : null;
                var built = Build(details, next, kind);
                built.SongArtist ??= match.Artist;
                built.SongAlbum ??= match.Album;
                built.ThumbnailUrl ??= match.ThumbnailUrl;
                return built;
            }
            return new ResolvedMedia {
                VideoId = match.VideoId,
                Kind = kind,
                MusicVideoType = match.MusicVideoType,
                YouTubeTitle = match.Title,
                ChannelName = match.Artist,
                SongTitle = match.Title,
                SongArtist = match.Artist,
                SongAlbum = match.Album,
                ThumbnailUrl = match.ThumbnailUrl,
                DurationSeconds = match.DurationSeconds,
                IsPartial = true,
            };
        }

        // ------------------------------------------------------------------
        //  Persistence (so the same videos are not looked up again after a restart)
        // ------------------------------------------------------------------

        private void LoadCache() {
            try {
                if (!File.Exists(Constants.ResolveCacheFile)) {
                    return;
                }
                var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(Constants.ResolveCacheFile));
                if (file == null || file.Version != CacheVersion || file.Entries == null) {
                    logger?.Log("[Resolver] Ignoring lookup cache written by an older version");
                    return;
                }
                var entries = file.Entries;
                foreach (var (key, value) in entries) {
                    if (!string.IsNullOrEmpty(value?.VideoId)) {
                        cache[key] = value!;
                    }
                }
                logger?.Log($"[Resolver] Loaded {cache.Count} cached lookups");
            } catch (Exception ex) {
                logger?.Log($"[Resolver] Could not read the lookup cache: {ex.Message}");
            }
        }

        private void ScheduleSave() {
            lock (saveLock) {
                saveTimer ??= new Timer(_ => SaveCache(), null, Timeout.Infinite, Timeout.Infinite);
                saveTimer.Change(5000, Timeout.Infinite);
            }
        }

        private void SaveCache() {
            try {
                var snapshot = cache
                    .Where(kv => !kv.Value.IsPartial)
                    .OrderByDescending(kv => kv.Value.ResolvedAtUtc)
                    .Take(MaxPersistedEntries)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                Directory.CreateDirectory(Constants.AppDataFolder);
                File.WriteAllText(Constants.ResolveCacheFile, JsonSerializer.Serialize(new CacheFile { Version = CacheVersion, Entries = snapshot }));
            } catch (Exception ex) {
                logger?.Log($"[Resolver] Could not write the lookup cache: {ex.Message}");
            }
        }

        public void ClearCache() {
            cache.Clear();
            negativeUntil.Clear();
            try {
                File.Delete(Constants.ResolveCacheFile);
            } catch { }
        }
    }
}
