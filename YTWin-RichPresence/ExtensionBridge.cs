using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace YTWin_RichPresence {

    /// <summary>One "now playing" report from a browser tab running the optional extension.</summary>
    internal sealed class ExtensionReport {
        public YouTubeSite Site;
        public string? VideoId;
        public string Title = "";
        public string Artist = "";
        public string? Album;
        public string? Url;
        public double? Duration;
        public double? CurrentTime;
        public bool Paused = true;
        public double Rate = 1.0;
        public int TabId = -1;
        public DateTime ReceivedUtc = DateTime.UtcNow;

        public override string ToString() => $"[{Site}] {VideoId} {Title} | {Artist} | {(Paused ? "paused" : "playing")} {CurrentTime:F0}/{Duration:F0}s (tab {TabId})";
    }

    /// <summary>
    /// Tiny HTTP endpoint on 127.0.0.1 that the optional browser extension posts to. Everything works without it;
    /// it only adds exact video IDs (unlisted videos, background tabs) and precise timestamps.
    /// </summary>
    internal sealed class ExtensionBridge : IDisposable {
        private static readonly Regex VideoIdRegex = new(@"^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);
        private const int MaxBodyBytes = 64 * 1024;

        private readonly Logger? logger;
        private readonly ConcurrentDictionary<int, ExtensionReport> reports = new();
        private HttpListener? listener;

        /// <summary>Raised (on a thread pool thread) when a report arrives.</summary>
        public event Action? ReportReceived;

        public bool IsListening => listener?.IsListening == true;
        public int Port { get; private set; }
        public DateTime? LastReportUtc { get; private set; }
        public string? LastError { get; private set; }

        public ExtensionBridge(Logger? logger = null) {
            this.logger = logger;
        }

        public bool Start(int port) {
            Stop();
            try {
                var l = new HttpListener();
                l.Prefixes.Add($"http://127.0.0.1:{port}/");
                l.Start();
                listener = l;
                Port = port;
                LastError = null;
                _ = AcceptLoopAsync(l);
                logger?.Log($"[ExtensionBridge] Listening on 127.0.0.1:{port}");
                return true;
            } catch (Exception ex) {
                LastError = ex.Message;
                logger?.Log($"[ExtensionBridge] Could not listen on 127.0.0.1:{port}: {ex.Message}");
                listener = null;
                return false;
            }
        }

        public void Stop() {
            var l = listener;
            listener = null;
            if (l != null) {
                try {
                    l.Stop();
                    l.Close();
                } catch { }
                logger?.Log("[ExtensionBridge] Stopped");
            }
            reports.Clear();
        }

        private async Task AcceptLoopAsync(HttpListener l) {
            while (l.IsListening) {
                HttpListenerContext context;
                try {
                    context = await l.GetContextAsync().ConfigureAwait(false);
                } catch (Exception) when (!l.IsListening) {
                    break;
                } catch (Exception ex) {
                    logger?.Log($"[ExtensionBridge] Accept failed: {ex.Message}");
                    continue;
                }
                _ = Task.Run(() => HandleAsync(context));
            }
        }

        private async Task HandleAsync(HttpListenerContext context) {
            var request = context.Request;
            var response = context.Response;
            try {
                response.Headers["Access-Control-Allow-Origin"] = "*";
                response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
                response.Headers["Access-Control-Allow-Private-Network"] = "true";

                var path = request.Url?.AbsolutePath ?? "/";
                if (request.HttpMethod == "OPTIONS") {
                    response.StatusCode = 204;
                } else if (request.HttpMethod == "GET" && path == "/ping") {
                    await WriteJsonAsync(response, $"{{\"app\":\"YTWin-RP\",\"version\":{JsonSerializer.Serialize(Constants.ProgramVersion)}}}").ConfigureAwait(false);
                } else if (request.HttpMethod == "POST" && path == "/nowplaying") {
                    var body = await ReadBodyAsync(request).ConfigureAwait(false);
                    var report = ParseReport(body);
                    if (report == null) {
                        response.StatusCode = 400;
                    } else {
                        reports[report.TabId] = report;
                        LastReportUtc = report.ReceivedUtc;
                        response.StatusCode = 204;
                        ReportReceived?.Invoke();
                    }
                } else if (request.HttpMethod == "POST" && path == "/closed") {
                    var body = await ReadBodyAsync(request).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(body);
                    var tabId = doc.RootElement.Get("tabId").Int() ?? -1;
                    if (reports.TryRemove(tabId, out _)) {
                        logger?.Log($"[ExtensionBridge] Tab {tabId} closed");
                        ReportReceived?.Invoke();
                    }
                    response.StatusCode = 204;
                } else {
                    response.StatusCode = 404;
                }
            } catch (Exception ex) {
                logger?.Log($"[ExtensionBridge] Bad request: {ex.Message}");
                try {
                    response.StatusCode = 400;
                } catch { }
            } finally {
                try {
                    response.Close();
                } catch { }
            }
        }

        private static async Task<string> ReadBodyAsync(HttpListenerRequest request) {
            if (request.ContentLength64 > MaxBodyBytes) {
                throw new InvalidDataException("Request body too large");
            }
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);
            if (body.Length > MaxBodyBytes) {
                throw new InvalidDataException("Request body too large");
            }
            return body;
        }

        private static async Task WriteJsonAsync(HttpListenerResponse response, string json) {
            var bytes = Encoding.UTF8.GetBytes(json);
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }

        private ExtensionReport? ParseReport(string json) {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var videoId = r.Get("videoId").Str();
            if (videoId != null && !VideoIdRegex.IsMatch(videoId)) {
                videoId = null;
            }
            var report = new ExtensionReport {
                Site = r.Get("site").Str() == "youtube_music" ? YouTubeSite.YouTubeMusic : YouTubeSite.YouTube,
                VideoId = videoId,
                Title = r.Get("title").Str() ?? "",
                Artist = r.Get("artist").Str() ?? "",
                Album = r.Get("album").Str(),
                Url = r.Get("url").Str(),
                Duration = Number(r.Get("duration")),
                CurrentTime = Number(r.Get("currentTime")),
                Paused = r.Get("paused").Bool() ?? true,
                Rate = Number(r.Get("rate")) ?? 1.0,
                TabId = r.Get("tabId").Int() ?? -1,
            };
            if (videoId == null && string.IsNullOrWhiteSpace(report.Title)) {
                return null;
            }
            if (string.IsNullOrWhiteSpace(report.Album)) {
                report.Album = null;
            }
            return report;
        }

        private static double? Number(JsonElement? e) {
            if (e is { ValueKind: JsonValueKind.Number } n && n.TryGetDouble(out var d) && !double.IsNaN(d) && !double.IsInfinity(d)) {
                return d;
            }
            return null;
        }

        /// <summary>The most useful fresh report: a playing tab wins over a paused one, newer wins over older.</summary>
        public ExtensionReport? GetLatest(TimeSpan maxAge) {
            var cutoff = DateTime.UtcNow - maxAge;
            return reports.Values
                .Where(r => r.ReceivedUtc >= cutoff)
                .OrderByDescending(r => !r.Paused)
                .ThenByDescending(r => r.ReceivedUtc)
                .FirstOrDefault();
        }

        public void Dispose() {
            Stop();
        }
    }
}
