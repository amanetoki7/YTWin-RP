using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace YTWin_RichPresence {

    public enum YouTubeSite {
        YouTube = 0,
        YouTubeMusic = 1
    }

    public enum MediaKind {
        Unknown = 0,
        Video = 1,
        Music = 2
    }

    public enum MediaSource {
        MediaSession = 0,   // Windows media controls (SMTC), fed by the browser
        Extension = 1       // optional browser extension talking to ExtensionBridge
    }

    /// <summary>
    /// What is currently playing in the browser, plus everything we managed to look up about it.
    /// Two instances are "the same item" when they carry the same video ID, or (if no ID is known)
    /// the same title and artist from the same site.
    /// </summary>
    internal sealed class YouTubeMediaInfo : IEquatable<YouTubeMediaInfo> {
        private static readonly Regex InvisibleChars = new(@"[​-‏‪-‮﻿]", RegexOptions.Compiled);

        // --- reported by the browser ---
        public string Title { get; }
        public string Artist { get; }          // channel name on YouTube, artist on YouTube Music
        public string? Album { get; }          // only YouTube Music sets this
        public YouTubeSite Site { get; set; }
        public MediaSource Source { get; set; }
        public string? SourceApp { get; set; }  // AppUserModelId of the media session
        public bool IsConfirmedYouTube { get; set; }   // a window title, the extension or a successful lookup confirmed this is YouTube
        public bool IsPaused { get; set; } = true;
        public double? CurrentTime { get; set; }        // seconds
        public int? DurationSeconds { get; set; }
        public DateTime? PlaybackStart { get; set; }    // UTC
        public DateTime? PlaybackEnd { get; set; }      // UTC
        public DateTime FirstSeenUtc { get; } = DateTime.UtcNow;

        // --- resolved through YouTube / YouTube Music ---
        public string? VideoId { get; set; }
        public MediaKind Kind { get; set; } = MediaKind.Unknown;
        public string? MusicVideoType { get; set; }
        public string? Category { get; set; }
        public string? ChannelName { get; set; }
        public string? ChannelId { get; set; }
        public string? SongTitle { get; set; }
        public string? SongArtist { get; set; }
        public string? SongAlbum { get; set; }
        public string? ThumbnailUrl { get; set; }
        public bool IsResolved { get; set; }
        public bool ResolveGaveUp { get; set; }
        public int ResolveFailures { get; set; }
        public Task? EnrichTask { get; set; }

        public List<LyricLine>? SyncedLyrics { get; set; }
        public bool LyricsSearched { get; set; }

        public YouTubeMediaInfo(string title, string artist, string? album, YouTubeSite site, MediaSource source) {
            Title = Sanitize(title);
            Artist = Sanitize(artist);
            Album = string.IsNullOrWhiteSpace(album) ? null : Sanitize(album!);
            Site = site;
            Source = source;
        }

        private static string Sanitize(string s) {
            if (string.IsNullOrEmpty(s)) {
                return "";
            }
            return InvisibleChars.Replace(s, "").Trim();
        }

        public bool IsMusic => Kind == MediaKind.Music;

        public string? VideoUrl => VideoId == null ? null : $"https://www.youtube.com/watch?v={VideoId}";
        public string? MusicUrl => VideoId == null ? null : $"https://music.youtube.com/watch?v={VideoId}";
        public string? ChannelUrl => ChannelId == null ? null : $"https://www.youtube.com/channel/{ChannelId}";

        /// <summary>Title to show: the song title for music, the video title otherwise.</summary>
        public string DisplayTitle => IsMusic ? (SongTitle ?? Title) : Title;

        /// <summary>Subtitle to show: the artist for music, the channel otherwise.</summary>
        public string DisplayArtist => IsMusic ? (SongArtist ?? ChannelName ?? Artist) : (ChannelName ?? Artist);

        /// <summary>Copy the playback state from a freshly observed instance of the same item.</summary>
        public void UpdatePlaybackFrom(YouTubeMediaInfo other) {
            IsPaused = other.IsPaused;
            CurrentTime = other.CurrentTime;
            // keep the timestamps stable while playback simply continues; only follow real jumps (seeks)
            if (PlaybackStart != null && other.PlaybackStart != null && Math.Abs((other.PlaybackStart.Value - PlaybackStart.Value).TotalSeconds) < 3) {
                if (other.PlaybackEnd != null) {
                    PlaybackEnd = PlaybackStart + (other.PlaybackEnd.Value - other.PlaybackStart.Value);
                }
            } else {
                PlaybackStart = other.PlaybackStart;
                PlaybackEnd = other.PlaybackEnd;
            }
            Source = other.Source;
            SourceApp = other.SourceApp ?? SourceApp;
            if (other.DurationSeconds != null) {
                DurationSeconds = other.DurationSeconds;
            }
            if (other.IsConfirmedYouTube) {
                IsConfirmedYouTube = true;
                Site = other.Site;
            }
            VideoId ??= other.VideoId;
        }

        public void ApplyResolved(ResolvedMedia r) {
            VideoId ??= r.VideoId;
            Kind = r.Kind;
            MusicVideoType = r.MusicVideoType;
            Category = r.Category;
            ChannelName = r.ChannelName ?? ChannelName;
            ChannelId = r.ChannelId ?? ChannelId;
            SongTitle = r.SongTitle;
            SongArtist = r.SongArtist;
            SongAlbum = r.SongAlbum ?? Album;
            ThumbnailUrl = r.ThumbnailUrl ?? ThumbnailUrl;
            DurationSeconds ??= r.DurationSeconds;
            IsConfirmedYouTube = true;
            IsResolved = !r.IsPartial;
        }

        public override string ToString() {
            var str = $"[YouTubeMediaInfo] {Title} by {Artist} ({Site}, {Source}, {(IsPaused ? "paused" : "playing")})";
            if (VideoId != null) {
                str += $"\n| Video:      {VideoId} kind={Kind} type={MusicVideoType ?? "-"} category={Category ?? "-"}";
            }
            if (IsMusic) {
                str += $"\n| Song:       {SongTitle} by {SongArtist} on {SongAlbum ?? "-"}";
            }
            if (DurationSeconds != null) {
                str += $"\n| Time:       {(CurrentTime == null ? "?" : ((int)CurrentTime).ToString())}/{DurationSeconds} sec";
            }
            if (ThumbnailUrl != null) {
                str += $"\n| Thumbnail:  {ThumbnailUrl}";
            }
            return str;
        }

        public bool Equals(YouTubeMediaInfo? other) {
            if (other is null) {
                return false;
            }
            if (VideoId != null && other.VideoId != null) {
                return VideoId == other.VideoId;
            }
            return Site == other.Site && Title == other.Title && Artist == other.Artist;
        }

        public override bool Equals(object? obj) => Equals(obj as YouTubeMediaInfo);

        public override int GetHashCode() => HashCode.Combine(Site, Title, Artist);

        public static bool operator ==(YouTubeMediaInfo? a, YouTubeMediaInfo? b) {
            if (a is null && b is null) {
                return true;
            }
            if (a is null || b is null) {
                return false;
            }
            return a.Equals(b);
        }

        public static bool operator !=(YouTubeMediaInfo? a, YouTubeMediaInfo? b) => !(a == b);
    }
}
