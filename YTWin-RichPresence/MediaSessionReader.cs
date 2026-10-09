using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.Control;

namespace YTWin_RichPresence {

    /// <summary>A point-in-time copy of one Windows media session (System Media Transport Controls).</summary>
    internal sealed class MediaSessionSnapshot {
        public string AppUserModelId = "";
        public string Title = "";
        public string Artist = "";
        public string Album = "";
        public GlobalSystemMediaTransportControlsSessionPlaybackStatus Status;
        public double? PositionSeconds;
        public double? DurationSeconds;
        public bool IsCurrent;

        public bool IsPlaying => Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        public bool IsPaused => Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
        public bool IsActive => IsPlaying || IsPaused;

        public override string ToString() {
            return $"[{AppUserModelId}] {Title} | {Artist} | {Album} | {Status} | {PositionSeconds:F0}/{DurationSeconds:F0}s";
        }
    }

    /// <summary>
    /// Reads what browsers publish to the Windows media controls. Chrome, Edge and Firefox all forward the
    /// Media Session metadata that YouTube sets (title, channel/artist, playback state and position).
    /// </summary>
    internal sealed class MediaSessionReader : IDisposable {
        private readonly Logger? logger;
        private GlobalSystemMediaTransportControlsSessionManager? manager;
        private GlobalSystemMediaTransportControlsSession? tracked;

        private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, SessionsChangedEventArgs> sessionsChangedHandler;
        private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, CurrentSessionChangedEventArgs> currentSessionChangedHandler;
        private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> mediaPropertiesChangedHandler;
        private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> playbackInfoChangedHandler;

        /// <summary>Raised (on an arbitrary thread) whenever Windows reports a change worth re-reading.</summary>
        public event Action? Changed;

        public bool IsAvailable => manager != null;

        public MediaSessionReader(Logger? logger = null) {
            this.logger = logger;
            sessionsChangedHandler = (_, _) => OnChanged();
            currentSessionChangedHandler = (m, _) => { TrackCurrentSession(); OnChanged(); };
            mediaPropertiesChangedHandler = (_, _) => OnChanged();
            playbackInfoChangedHandler = (_, _) => OnChanged();
        }

        public async Task<bool> InitAsync() {
            try {
                manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                manager.SessionsChanged += sessionsChangedHandler;
                manager.CurrentSessionChanged += currentSessionChangedHandler;
                TrackCurrentSession();
                logger?.Log("Connected to Windows media session manager");
                return true;
            } catch (Exception ex) {
                logger?.Log($"Could not access Windows media sessions: {ex.Message}");
                manager = null;
                return false;
            }
        }

        private void OnChanged() {
            try {
                Changed?.Invoke();
            } catch (Exception ex) {
                logger?.Log($"Media session change handler failed: {ex.Message}");
            }
        }

        // Subscribe to the "current" session so play/pause and track changes are picked up immediately
        // instead of on the next polling tick.
        private void TrackCurrentSession() {
            GlobalSystemMediaTransportControlsSession? session = null;
            try {
                session = manager?.GetCurrentSession();
            } catch (Exception ex) {
                logger?.Log($"Could not get current media session: {ex.Message}");
            }

            lock (this) {
                if (tracked != null) {
                    try {
                        tracked.MediaPropertiesChanged -= mediaPropertiesChangedHandler;
                        tracked.PlaybackInfoChanged -= playbackInfoChangedHandler;
                    } catch { }
                    tracked = null;
                }
                if (session != null) {
                    try {
                        session.MediaPropertiesChanged += mediaPropertiesChangedHandler;
                        session.PlaybackInfoChanged += playbackInfoChangedHandler;
                        tracked = session;
                    } catch (Exception ex) {
                        logger?.Log($"Could not subscribe to media session events: {ex.Message}");
                    }
                }
            }
        }

        public async Task<List<MediaSessionSnapshot>> GetSnapshotsAsync() {
            var result = new List<MediaSessionSnapshot>();
            if (manager == null) {
                return result;
            }

            string? currentId = null;
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions;
            try {
                currentId = manager.GetCurrentSession()?.SourceAppUserModelId;
                sessions = manager.GetSessions();
            } catch (Exception ex) {
                logger?.Log($"Could not enumerate media sessions: {ex.Message}");
                return result;
            }

            foreach (var session in sessions) {
                try {
                    var snapshot = new MediaSessionSnapshot { AppUserModelId = session.SourceAppUserModelId ?? "" };
                    snapshot.IsCurrent = currentId != null && snapshot.AppUserModelId == currentId;

                    var playback = session.GetPlaybackInfo();
                    snapshot.Status = playback.PlaybackStatus;

                    var props = await session.TryGetMediaPropertiesAsync();
                    if (props != null) {
                        snapshot.Title = props.Title ?? "";
                        snapshot.Artist = props.Artist ?? "";
                        snapshot.Album = props.AlbumTitle ?? "";
                    }

                    var timeline = session.GetTimelineProperties();
                    var duration = timeline.EndTime - timeline.StartTime;
                    if (duration > TimeSpan.Zero) {
                        snapshot.DurationSeconds = duration.TotalSeconds;
                    }
                    if (duration > TimeSpan.Zero || timeline.Position > TimeSpan.Zero) {
                        // Browsers only update the position when it jumps (play, pause, seek), so extrapolate
                        // from the last update while playing.
                        var position = timeline.Position - timeline.StartTime;
                        if (snapshot.IsPlaying) {
                            var elapsed = DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
                            if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromHours(24)) {
                                var rate = playback.PlaybackRate ?? 1.0;
                                if (rate <= 0 || rate > 16) {
                                    rate = 1.0;
                                }
                                position += TimeSpan.FromSeconds(elapsed.TotalSeconds * rate);
                            }
                        }
                        var seconds = Math.Max(0, position.TotalSeconds);
                        if (snapshot.DurationSeconds != null) {
                            seconds = Math.Min(seconds, snapshot.DurationSeconds.Value);
                        }
                        snapshot.PositionSeconds = seconds;
                    }

                    result.Add(snapshot);
                } catch (Exception ex) {
                    logger?.Log($"Could not read media session: {ex.Message}");
                }
            }
            return result;
        }

        public void Dispose() {
            lock (this) {
                if (tracked != null) {
                    try {
                        tracked.MediaPropertiesChanged -= mediaPropertiesChangedHandler;
                        tracked.PlaybackInfoChanged -= playbackInfoChangedHandler;
                    } catch { }
                    tracked = null;
                }
            }
            if (manager != null) {
                try {
                    manager.SessionsChanged -= sessionsChangedHandler;
                    manager.CurrentSessionChanged -= currentSessionChangedHandler;
                } catch { }
                manager = null;
            }
        }
    }
}
