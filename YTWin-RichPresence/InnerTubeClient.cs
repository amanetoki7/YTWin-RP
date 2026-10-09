using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace YTWin_RichPresence {

    internal sealed record Thumbnail(string Url, int Width, int Height);

    internal sealed class YouTubeSearchResult {
        public string VideoId = "";
        public string Title = "";
        public string Owner = "";
        public int? DurationSeconds;
        public string? ThumbnailUrl;
        public bool IsVerifiedArtist;

        public override string ToString() => $"{VideoId} | {Title} | {Owner} | {DurationSeconds}s";
    }

    internal sealed class YouTubeMusicSearchResult {
        public string VideoId = "";
        public string Title = "";
        public string Artist = "";
        public string? Album;
        public int? DurationSeconds;
        public string? ThumbnailUrl;
        public string? MusicVideoType;

        public override string ToString() => $"{VideoId} | {Title} | {Artist} | {Album} | {DurationSeconds}s | {MusicVideoType}";
    }

    internal sealed class VideoDetails {
        public string VideoId = "";
        public string Title = "";           // from music.youtube.com this is the song title, from www.youtube.com the video title
        public string Author = "";
        public string? ChannelId;
        public int? LengthSeconds;
        public string? MusicVideoType;      // only set when YouTube Music knows the video as music (ATV, OMV, UGC, ...)
        public string? Category;            // YouTube category, localised (e.g. "Music", "音楽")
        public string? PageOwnerName;       // uploading channel, e.g. "Rick Astley - Topic"
        public string? YouTubeTitle;        // the title as shown on www.youtube.com
        public bool IsLive;
        public List<Thumbnail> Thumbnails = new();
        public bool FromYouTubeMusic;
    }

    internal sealed class MusicNextInfo {
        public string? Artist;
        public string? Album;
        public string? ThumbnailUrl;
        public string? MusicVideoType;
    }

    /// <summary>
    /// Thin client for YouTube's internal "InnerTube" JSON API (the same API the website uses).
    /// Only metadata endpoints are used; nothing here plays or downloads media.
    /// </summary>
    internal sealed class InnerTubeClient {
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
        private const string YouTubeHost = "www.youtube.com";
        private const string MusicHost = "music.youtube.com";
        private const string WebClientName = "WEB";
        private const int WebClientId = 1;
        private const string WebClientVersion = "2.20250101.00.00";
        private const string RemixClientName = "WEB_REMIX";
        private const int RemixClientId = 67;
        private const string RemixClientVersion = "1.20250101.01.00";
        private const string SongsFilterParams = "EgWKAQIIAWoKEAoQAxAEEAkQBQ%3D%3D";
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
        private static readonly Regex GoogleUserContentSize = new(@"=w\d+-h\d+[^&]*$", RegexOptions.Compiled);
        private static readonly Regex LengthRegex = new(@"^(?:(\d+):)?(\d{1,2}):(\d{2})$", RegexOptions.Compiled);

        private readonly Logger? logger;

        /// <summary>Interface language, e.g. "ja". Titles returned by YouTube are localised with it, so it should match the browser's.</summary>
        public string Hl { get; set; } = "en";
        public string Gl { get; set; } = "US";

        public InnerTubeClient(Logger? logger = null) {
            this.logger = logger;
        }

        private object Context(string clientName, string clientVersion) {
            return new { client = new { clientName, clientVersion, hl = Hl, gl = Gl } };
        }

        private async Task<JsonDocument?> PostAsync(string host, string endpoint, object body, int clientId, string clientVersion, string caller) {
            var url = $"https://{host}/youtubei/v1/{endpoint}?prettyPrint=false";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Origin", $"https://{host}");
            request.Headers.TryAddWithoutValidation("Referer", $"https://{host}/");
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", clientId.ToString());
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", clientVersion);
            request.Headers.TryAddWithoutValidation("Accept-Language", $"{Hl},en;q=0.8");
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            var stopwatch = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(RequestTimeout);
            using var response = await Constants.HttpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            stopwatch.Stop();
            logger?.Log($"[{caller}] POST {host}/{endpoint} -> {(int)response.StatusCode} in {stopwatch.ElapsedMilliseconds}ms ({text.Length} bytes)");
            if (!response.IsSuccessStatusCode) {
                return null;
            }
            return JsonDocument.Parse(text);
        }

        // ------------------------------------------------------------------
        //  Search
        // ------------------------------------------------------------------

        public async Task<List<YouTubeSearchResult>> SearchYouTubeAsync(string query) {
            var results = new List<YouTubeSearchResult>();
            try {
                using var doc = await PostAsync(YouTubeHost, "search", new { context = Context(WebClientName, WebClientVersion), query }, WebClientId, WebClientVersion, "SearchYouTube").ConfigureAwait(false);
                if (doc == null) {
                    return results;
                }
                var sections = doc.RootElement.Get("contents").Get("twoColumnSearchResultsRenderer").Get("primaryContents").Get("sectionListRenderer").Get("contents");
                foreach (var section in sections.Arr()) {
                    foreach (var item in section.Get("itemSectionRenderer").Get("contents").Arr()) {
                        AddVideoRenderer(results, item.Get("videoRenderer"));
                        foreach (var shelfItem in item.Get("shelfRenderer").Get("content").Get("verticalListRenderer").Get("items").Arr()) {
                            AddVideoRenderer(results, shelfItem.Get("videoRenderer"));
                        }
                    }
                }
            } catch (Exception ex) {
                logger?.Log($"[SearchYouTube] An exception occurred: {ex.Message}");
            }
            return results;
        }

        private static void AddVideoRenderer(List<YouTubeSearchResult> results, JsonElement? video) {
            if (video == null) {
                return;
            }
            var videoId = video.Get("videoId").Str();
            var title = video.Get("title").Text();
            if (string.IsNullOrEmpty(videoId) || string.IsNullOrEmpty(title)) {
                return;
            }
            var owner = video.Get("ownerText").Text() ?? video.Get("longBylineText").Text() ?? video.Get("shortBylineText").Text() ?? "";
            var verifiedArtist = video.Get("ownerBadges").Arr()
                .Any(b => (b.Get("metadataBadgeRenderer").Get("style").Str() ?? "").Contains("VERIFIED_ARTIST"));
            results.Add(new YouTubeSearchResult {
                VideoId = videoId,
                Title = title,
                Owner = owner,
                DurationSeconds = ParseLength(video.Get("lengthText").Text()),
                ThumbnailUrl = video.Get("thumbnail").Get("thumbnails").LargestThumbnail()?.Url,
                IsVerifiedArtist = verifiedArtist,
            });
        }

        /// <summary>Searches the "Songs" shelf of YouTube Music. This is the only place auto-generated "Topic" uploads show up.</summary>
        public async Task<List<YouTubeMusicSearchResult>> SearchYouTubeMusicSongsAsync(string query) {
            var results = new List<YouTubeMusicSearchResult>();
            try {
                using var doc = await PostAsync(MusicHost, "search", new { context = Context(RemixClientName, RemixClientVersion), query, @params = SongsFilterParams }, RemixClientId, RemixClientVersion, "SearchYouTubeMusic").ConfigureAwait(false);
                if (doc == null) {
                    return results;
                }
                var sections = doc.RootElement.Get("contents").Get("tabbedSearchResultsRenderer").Get("tabs").At(0)
                    .Get("tabRenderer").Get("content").Get("sectionListRenderer").Get("contents");
                foreach (var section in sections.Arr()) {
                    foreach (var item in section.Get("musicShelfRenderer").Get("contents").Arr()) {
                        AddMusicListItem(results, item.Get("musicResponsiveListItemRenderer"));
                    }
                    foreach (var item in section.Get("musicCardShelfRenderer").Get("contents").Arr()) {
                        AddMusicListItem(results, item.Get("musicResponsiveListItemRenderer"));
                    }
                }
            } catch (Exception ex) {
                logger?.Log($"[SearchYouTubeMusic] An exception occurred: {ex.Message}");
            }
            return results;
        }

        private static void AddMusicListItem(List<YouTubeMusicSearchResult> results, JsonElement? item) {
            if (item == null) {
                return;
            }
            var columns = item.Get("flexColumns").Arr().Select(c => c.Get("musicResponsiveListItemFlexColumnRenderer").Get("text")).ToList();
            if (columns.Count == 0) {
                return;
            }
            var titleRun = columns[0].Get("runs").At(0);
            var videoId = item.Get("playlistItemData").Get("videoId").Str()
                ?? titleRun.Get("navigationEndpoint").Get("watchEndpoint").Get("videoId").Str();
            var title = columns[0].Text();
            if (string.IsNullOrEmpty(videoId) || string.IsNullOrEmpty(title)) {
                return;
            }
            var result = new YouTubeMusicSearchResult {
                VideoId = videoId,
                Title = title,
                MusicVideoType = titleRun.Get("navigationEndpoint").Get("watchEndpoint").Get("watchEndpointMusicSupportedConfigs").Get("watchEndpointMusicConfig").Get("musicVideoType").Str(),
                ThumbnailUrl = UpscaleGoogleUserContent(item.Get("thumbnail").Get("musicThumbnailRenderer").Get("thumbnail").Get("thumbnails").LargestThumbnail()?.Url),
            };
            if (columns.Count > 1) {
                var (artist, album, duration) = ParseByline(columns[1].Get("runs"));
                result.Artist = artist ?? "";
                result.Album = album;
                result.DurationSeconds = duration;
            }
            results.Add(result);
        }

        // "Artist • Album • 3:34" with navigation endpoints telling which run is which
        private static (string? artist, string? album, int? duration) ParseByline(JsonElement? runs) {
            var artists = new List<string>();
            string? album = null;
            int? duration = null;
            var sawSeparator = false;
            foreach (var run in runs.Arr()) {
                var text = run.Get("text").Str() ?? "";
                var pageType = run.Get("navigationEndpoint").Get("browseEndpoint").Get("browseEndpointContextSupportedConfigs").Get("browseEndpointContextMusicConfig").Get("pageType").Str();
                if (pageType == "MUSIC_PAGE_TYPE_ARTIST" || pageType == "MUSIC_PAGE_TYPE_USER_CHANNEL") {
                    artists.Add(text.Trim());
                } else if (pageType == "MUSIC_PAGE_TYPE_ALBUM") {
                    album = text.Trim();
                } else if (text.Contains('•')) {
                    sawSeparator = true;
                } else if (ParseLength(text.Trim()) is int seconds) {
                    duration = seconds;
                } else if (!sawSeparator && artists.Count == 0 && text.Trim().Length > 0 && pageType == null && !text.Contains('•')) {
                    // artist without a link (e.g. uploaded tracks)
                    artists.Add(text.Trim());
                }
            }
            return (artists.Count > 0 ? string.Join(", ", artists) : null, album, duration);
        }

        // ------------------------------------------------------------------
        //  Video details
        // ------------------------------------------------------------------

        /// <summary>
        /// Asks YouTube Music about a video. Playback is refused (no streaming URLs are requested), but the response
        /// still carries videoDetails including musicVideoType, which is only present for music.
        /// </summary>
        public async Task<VideoDetails?> GetMusicPlayerAsync(string videoId) {
            try {
                using var doc = await PostAsync(MusicHost, "player", new { context = Context(RemixClientName, RemixClientVersion), videoId }, RemixClientId, RemixClientVersion, "GetMusicPlayer").ConfigureAwait(false);
                if (doc == null) {
                    return null;
                }
                var details = ParseVideoDetails(doc.RootElement, videoId);
                if (details == null) {
                    logger?.Log($"[GetMusicPlayer] No videoDetails for {videoId}: {doc.RootElement.Get("playabilityStatus").Get("status").Str()} / {doc.RootElement.Get("playabilityStatus").Get("reason").Str()}");
                    return null;
                }
                details.FromYouTubeMusic = true;
                var microformat = doc.RootElement.Get("microformat").Get("microformatDataRenderer");
                details.Category = microformat.Get("category").Str();
                details.PageOwnerName = microformat.Get("pageOwnerDetails").Get("name").Str();
                details.YouTubeTitle = microformat.Get("linkAlternates").Arr()
                    .Select(l => l.Get("title").Str())
                    .FirstOrDefault(t => !string.IsNullOrEmpty(t));
                return details;
            } catch (Exception ex) {
                logger?.Log($"[GetMusicPlayer] An exception occurred: {ex.Message}");
                return null;
            }
        }

        /// <summary>Fallback that asks www.youtube.com instead; it cannot tell whether the video is music.</summary>
        public async Task<VideoDetails?> GetYouTubePlayerAsync(string videoId) {
            try {
                using var doc = await PostAsync(YouTubeHost, "player", new { context = Context(WebClientName, WebClientVersion), videoId }, WebClientId, WebClientVersion, "GetYouTubePlayer").ConfigureAwait(false);
                if (doc == null) {
                    return null;
                }
                var details = ParseVideoDetails(doc.RootElement, videoId);
                if (details == null) {
                    logger?.Log($"[GetYouTubePlayer] No videoDetails for {videoId}: {doc.RootElement.Get("playabilityStatus").Get("status").Str()} / {doc.RootElement.Get("playabilityStatus").Get("reason").Str()}");
                    return null;
                }
                var microformat = doc.RootElement.Get("microformat").Get("playerMicroformatRenderer");
                details.Category = microformat.Get("category").Str();
                details.PageOwnerName = microformat.Get("ownerChannelName").Str() ?? details.Author;
                details.YouTubeTitle = details.Title;
                return details;
            } catch (Exception ex) {
                logger?.Log($"[GetYouTubePlayer] An exception occurred: {ex.Message}");
                return null;
            }
        }

        private static VideoDetails? ParseVideoDetails(JsonElement root, string requestedId) {
            var vd = root.Get("videoDetails");
            if (vd == null) {
                return null;
            }
            var title = vd.Get("title").Str();
            if (string.IsNullOrEmpty(title)) {
                return null;
            }
            var details = new VideoDetails {
                VideoId = vd.Get("videoId").Str() ?? requestedId,
                Title = title,
                Author = vd.Get("author").Str() ?? "",
                ChannelId = vd.Get("channelId").Str(),
                LengthSeconds = vd.Get("lengthSeconds").Int(),
                MusicVideoType = vd.Get("musicVideoType").Str(),
                IsLive = vd.Get("isLiveContent").Bool() ?? false,
            };
            foreach (var t in vd.Get("thumbnail").Get("thumbnails").Arr()) {
                var url = t.Get("url").Str();
                if (!string.IsNullOrEmpty(url)) {
                    details.Thumbnails.Add(new Thumbnail(url, t.Get("width").Int() ?? 0, t.Get("height").Int() ?? 0));
                }
            }
            return details;
        }

        /// <summary>The "Up next" panel of YouTube Music, which is where the album name lives.</summary>
        public async Task<MusicNextInfo?> GetMusicNextAsync(string videoId) {
            try {
                using var doc = await PostAsync(MusicHost, "next",
                    new { context = Context(RemixClientName, RemixClientVersion), videoId, enablePersistentPlaylistPanel = true, isAudioOnly = true },
                    RemixClientId, RemixClientVersion, "GetMusicNext").ConfigureAwait(false);
                if (doc == null) {
                    return null;
                }
                var panel = doc.RootElement.Get("contents").Get("singleColumnMusicWatchNextResultsRenderer").Get("tabbedRenderer")
                    .Get("watchNextTabbedResultsRenderer").Get("tabs").At(0).Get("tabRenderer").Get("content")
                    .Get("musicQueueRenderer").Get("content").Get("playlistPanelRenderer").Get("contents");
                foreach (var item in panel.Arr()) {
                    var video = item.Get("playlistPanelVideoRenderer") ?? item.Get("playlistPanelVideoWrapperRenderer").Get("primaryRenderer").Get("playlistPanelVideoRenderer");
                    if (video == null || video.Get("videoId").Str() != videoId) {
                        continue;
                    }
                    var (artist, album, _) = ParseByline(video.Get("longBylineText").Get("runs"));
                    return new MusicNextInfo {
                        Artist = artist,
                        Album = album,
                        ThumbnailUrl = UpscaleGoogleUserContent(video.Get("thumbnail").Get("thumbnails").LargestThumbnail()?.Url),
                        MusicVideoType = video.Get("navigationEndpoint").Get("watchEndpoint").Get("watchEndpointMusicSupportedConfigs").Get("watchEndpointMusicConfig").Get("musicVideoType").Str(),
                    };
                }
                return null;
            } catch (Exception ex) {
                logger?.Log($"[GetMusicNext] An exception occurred: {ex.Message}");
                return null;
            }
        }

        // ------------------------------------------------------------------
        //  Helpers
        // ------------------------------------------------------------------

        /// <summary>Parses "3:34" or "1:02:03" into seconds.</summary>
        public static int? ParseLength(string? text) {
            if (string.IsNullOrWhiteSpace(text)) {
                return null;
            }
            var m = LengthRegex.Match(text.Trim());
            if (!m.Success) {
                return null;
            }
            var hours = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
            return hours * 3600 + int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value);
        }

        /// <summary>Album art on googleusercontent.com is served at the size encoded in the URL; ask for a large square.</summary>
        public static string? UpscaleGoogleUserContent(string? url) {
            if (string.IsNullOrEmpty(url) || !url.Contains("googleusercontent.com")) {
                return url;
            }
            return GoogleUserContentSize.Replace(url, "=w544-h544-l90-rj");
        }
    }

    internal static class JsonExt {
        public static JsonElement? Get(this JsonElement e, string name) {
            return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : null;
        }

        public static JsonElement? Get(this JsonElement? e, string name) => e.HasValue ? e.Value.Get(name) : null;

        public static JsonElement? At(this JsonElement? e, int index) {
            if (e is { ValueKind: JsonValueKind.Array } a && index >= 0 && index < a.GetArrayLength()) {
                return a[index];
            }
            return null;
        }

        public static IEnumerable<JsonElement> Arr(this JsonElement? e) {
            if (e is { ValueKind: JsonValueKind.Array } a) {
                foreach (var item in a.EnumerateArray()) {
                    yield return item;
                }
            }
        }

        public static string? Str(this JsonElement? e) => e is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;

        public static bool? Bool(this JsonElement? e) {
            return e switch {
                { ValueKind: JsonValueKind.True } => true,
                { ValueKind: JsonValueKind.False } => false,
                _ => null,
            };
        }

        public static int? Int(this JsonElement? e) {
            if (e is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var i)) {
                return i;
            }
            if (e is { ValueKind: JsonValueKind.String } s && int.TryParse(s.GetString(), out var j)) {
                return j;
            }
            return null;
        }

        /// <summary>Text of a YouTube "runs"/"simpleText" object.</summary>
        public static string? Text(this JsonElement? e) {
            if (e == null) {
                return null;
            }
            var simple = e.Get("simpleText").Str();
            if (simple != null) {
                return simple;
            }
            var sb = new StringBuilder();
            foreach (var run in e.Get("runs").Arr()) {
                sb.Append(run.Get("text").Str());
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        public static Thumbnail? LargestThumbnail(this JsonElement? thumbnails) {
            Thumbnail? best = null;
            foreach (var t in thumbnails.Arr()) {
                var url = t.Get("url").Str();
                if (string.IsNullOrEmpty(url)) {
                    continue;
                }
                if (url.StartsWith("//")) {
                    url = "https:" + url;
                }
                var candidate = new Thumbnail(url, t.Get("width").Int() ?? 0, t.Get("height").Int() ?? 0);
                if (best == null || candidate.Width * candidate.Height >= best.Width * best.Height) {
                    best = candidate;
                }
            }
            return best;
        }
    }
}
