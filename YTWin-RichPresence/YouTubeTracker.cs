using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace YTWin_RichPresence {

    /// <summary>
    /// Periodically works out what YouTube content the browser is playing and enriches it with lookups.
    /// Sources, in order of preference: the optional browser extension (exact video ID), then the Windows
    /// media session published by the browser (title + channel, confirmed through the browser window title
    /// or a YouTube search).
    /// </summary>
    internal sealed class YouTubeTracker : IDisposable {
        public delegate void RefreshHandler(YouTubeMediaInfo? info);

        private readonly MediaSessionReader reader;
        private readonly ExtensionBridge bridge;
        private readonly YouTubeResolver resolver;
        private readonly LRCLibClient lrclib;
        private readonly RefreshHandler handler;
        private readonly Logger? logger;
        private readonly System.Timers.Timer timer;
        private readonly Timer debounce;
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly System.Collections.Generic.HashSet<string> ignoredApps = new(StringComparer.OrdinalIgnoreCase);
        private YouTubeMediaInfo? current;
        private bool loggedNothing;

        /// <summary>Only accept media sessions whose YouTube tab is visible in a browser window title.</summary>
        public bool RequireYouTubeWindow { get; set; }

        public YouTubeMediaInfo? Current => current;

        public YouTubeTracker(MediaSessionReader reader, ExtensionBridge bridge, YouTubeResolver resolver, LRCLibClient lrclib, RefreshHandler handler, Logger? logger = null) {
            this.reader = reader;
            this.bridge = bridge;
            this.resolver = resolver;
            this.lrclib = lrclib;
            this.handler = handler;
            this.logger = logger;

            timer = new System.Timers.Timer(Constants.RefreshPeriod * 1000) { AutoReset = true };
            timer.Elapsed += (_, _) => _ = RefreshAsync();
            debounce = new Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);

            reader.Changed += RequestRefresh;
            bridge.ReportReceived += RequestRefresh;
        }

        public async Task StartAsync() {
            await Task.Run(reader.InitAsync).ConfigureAwait(false);
            timer.Start();
            await RefreshAsync().ConfigureAwait(false);
        }

        /// <summary>Re-read soon (debounced), e.g. after a play/pause event.</summary>
        public void RequestRefresh() {
            try {
                debounce.Change(700, Timeout.Infinite);
            } catch (ObjectDisposedException) { }
        }

        private async Task RefreshAsync() {
            if (!await gate.WaitAsync(0).ConfigureAwait(false)) {
                return; // a refresh is still running
            }
            try {
                try {
                    await UpdateAsync().ConfigureAwait(false);
                } catch (Exception ex) {
                    logger?.Log($"Something went wrong while tracking playback: {ex}");
                }
                try {
                    handler(current);
                } catch (Exception ex) {
                    logger?.Log($"Refresh handler failed: {ex}");
                }
            } finally {
                gate.Release();
            }
        }

        private async Task UpdateAsync() {
            var candidate = BuildFromExtension() ?? await BuildFromMediaSessionAsync().ConfigureAwait(false);
            if (candidate == null) {
                if (current != null || !loggedNothing) {
                    logger?.Log("No YouTube playback found");
                    loggedNothing = true;
                }
                current = null;
                return;
            }
            loggedNothing = false;

            if (current == null || current != candidate) {
                current = candidate;
                logger?.Log($"Now playing: {candidate.Title} by {candidate.Artist} ({candidate.Site}, {candidate.Source}{(candidate.VideoId != null ? ", id " + candidate.VideoId : "")})");
            } else {
                current.UpdatePlaybackFrom(candidate);
            }

            // lookups can be slow; give them a moment, otherwise let them finish in the background
            var enrich = EnrichAsync(current);
            await Task.WhenAny(enrich, Task.Delay(2500)).ConfigureAwait(false);
        }

        private Task EnrichAsync(YouTubeMediaInfo info) {
            if (info.EnrichTask is { IsCompleted: false }) {
                return info.EnrichTask;
            }
            info.EnrichTask = DoEnrichAsync(info);
            return info.EnrichTask;
        }

        private async Task DoEnrichAsync(YouTubeMediaInfo info) {
            try {
                if (!info.IsResolved && !info.ResolveGaveUp) {
                    var resolved = info.VideoId != null
                        ? await resolver.ResolveByIdAsync(info.VideoId, info.Site).ConfigureAwait(false)
                        : await resolver.ResolveByMetadataAsync(info.Site, info.Title, info.Artist, info.Album, info.CurrentTime == null ? info.DurationSeconds : info.DurationSeconds).ConfigureAwait(false);
                    if (resolved != null) {
                        info.ApplyResolved(resolved);
                        logger?.Log(info.ToString());
                    } else {
                        info.ResolveFailures++;
                        if (info.ResolveFailures >= Constants.NumFailedSearchesBeforeAbandon) {
                            info.ResolveGaveUp = true;
                            logger?.Log($"Giving up identifying '{info.Title}'");
                        }
                    }
                }

                if (info.IsMusic && !info.LyricsSearched && Properties.Settings.Default.EnableSyncLyrics) {
                    info.LyricsSearched = true;
                    var result = await lrclib.GetSyncedLyrics(info.DisplayTitle, info.DisplayArtist, info.DurationSeconds).ConfigureAwait(false);
                    if (result != null) {
                        info.SyncedLyrics = result.Lyrics;
                    }
                }
            } catch (Exception ex) {
                logger?.Log($"Lookup failed: {ex}");
            }
        }

        // ------------------------------------------------------------------
        //  Sources
        // ------------------------------------------------------------------

        private YouTubeMediaInfo? BuildFromExtension() {
            var report = bridge.GetLatest(TimeSpan.FromSeconds(Constants.ExtensionReportMaxAge));
            if (report == null) {
                return null;
            }
            var info = new YouTubeMediaInfo(report.Title, report.Artist, report.Album, report.Site, MediaSource.Extension) {
                VideoId = report.VideoId,
                IsConfirmedYouTube = true,
                IsPaused = report.Paused,
                SourceApp = "extension",
            };
            if (report.Duration != null) {
                info.DurationSeconds = (int)Math.Round(report.Duration.Value);
            }
            if (report.CurrentTime != null) {
                var position = report.CurrentTime.Value;
                if (!report.Paused) {
                    position += (DateTime.UtcNow - report.ReceivedUtc).TotalSeconds * (report.Rate > 0 ? report.Rate : 1.0);
                }
                info.CurrentTime = Math.Max(0, position);
            }
            SetTimestamps(info);
            return info;
        }

        private async Task<YouTubeMediaInfo?> BuildFromMediaSessionAsync() {
            var snapshots = await reader.GetSnapshotsAsync().ConfigureAwait(false);
            if (snapshots.Count == 0) {
                return null;
            }
            var windows = BrowserWindows.FindYouTubeWindows();

            var ordered = snapshots
                .Where(s => s.IsActive && !string.IsNullOrWhiteSpace(s.Title))
                .OrderByDescending(s => s.IsPlaying)
                .ThenByDescending(s => s.IsCurrent);

            foreach (var session in ordered) {
                var window = windows.FirstOrDefault(w => TextMatch.TitleEquals(w.PageTitle, session.Title));
                var isBrowser = IsBrowserApp(session.AppUserModelId);
                if (window == null && (!isBrowser || RequireYouTubeWindow)) {
                    if (!isBrowser && ignoredApps.Add(session.AppUserModelId)) {
                        logger?.Log($"Ignoring media session from '{session.AppUserModelId}' (not a known browser and no YouTube window)");
                    }
                    continue;
                }

                // YouTube never reports an album; YouTube Music does
                var site = window?.Site ?? (session.Album.Length > 0 ? YouTubeSite.YouTubeMusic : YouTubeSite.YouTube);
                var info = new YouTubeMediaInfo(session.Title, session.Artist, session.Album, site, MediaSource.MediaSession) {
                    IsConfirmedYouTube = window != null,
                    IsPaused = !session.IsPlaying,
                    CurrentTime = session.PositionSeconds,
                    SourceApp = session.AppUserModelId,
                };
                if (session.DurationSeconds != null) {
                    info.DurationSeconds = (int)Math.Round(session.DurationSeconds.Value);
                }
                SetTimestamps(info);
                return info;
            }
            return null;
        }

        private static bool IsBrowserApp(string appUserModelId) {
            return Constants.BrowserAppUserModelIdHints.Any(h => appUserModelId.Contains(h, StringComparison.OrdinalIgnoreCase));
        }

        private static void SetTimestamps(YouTubeMediaInfo info) {
            if (info.CurrentTime == null) {
                return;
            }
            // whole seconds, so consecutive refreshes produce identical timestamps while playback continues
            var now = DateTime.UtcNow;
            now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            var position = Math.Round(info.CurrentTime.Value);
            info.PlaybackStart = now - TimeSpan.FromSeconds(position);
            if (info.DurationSeconds != null && info.DurationSeconds.Value >= position) {
                info.PlaybackEnd = now + TimeSpan.FromSeconds(info.DurationSeconds.Value - position);
            }
        }

        public void Dispose() {
            timer.Stop();
            timer.Dispose();
            debounce.Dispose();
            reader.Changed -= RequestRefresh;
            bridge.ReportReceived -= RequestRefresh;
        }
    }
}
