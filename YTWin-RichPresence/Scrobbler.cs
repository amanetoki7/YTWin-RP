using IF.Lastfm.Core.Api;
using IF.Lastfm.Core.Objects;
using IF.Lastfm.Core.Scrobblers;
using MetaBrainz.ListenBrainz;
using System;
using System.Threading.Tasks;

namespace YTWin_RichPresence {
    internal interface IScrobblerCredentials { }

    public struct LastFmCredentials : IScrobblerCredentials {
        public string apiKey;
        public string apiSecret;
        public string username;
        public string password;
    }

    public struct ListenBrainzCredentials : IScrobblerCredentials {
        public string userToken;
    }

    internal abstract class YouTubeScrobbler<C> where C : IScrobblerCredentials {
        protected int elapsedSeconds;
        protected string? lastSongID;
        protected bool hasScrobbled;
        protected bool nowPlayingSent;
        protected double lastSongProgress;
        protected Logger? logger;
        protected string serviceName;
        protected bool scrobbleInProgress;

        public YouTubeScrobbler(string serviceName, Logger? logger = null) {
            this.serviceName = serviceName;
            this.logger = logger;
        }

        protected bool IsTimeToScrobble(YouTubeMediaInfo info) {
            if (info.DurationSeconds.HasValue && info.DurationSeconds.Value >= 30) { // only scrobble tracks longer than 30 seconds
                double halfSongDuration = info.DurationSeconds.Value / 2.0;
                return elapsedSeconds >= halfSongDuration || elapsedSeconds >= Properties.Settings.Default.ScrobbleMaxWait;
            }
            return elapsedSeconds > Constants.LastFMTimeBeforeScrobbling;
        }

        protected bool IsRepeating(YouTubeMediaInfo info) {
            if (info.CurrentTime.HasValue && info.DurationSeconds.HasValue) {
                double currentTime = info.CurrentTime.Value;
                double songDuration = info.DurationSeconds.Value;
                double repeatThreshold = 1.5 * Constants.RefreshPeriod;
                return currentTime <= repeatThreshold && lastSongProgress >= (songDuration - repeatThreshold);
            }
            return false;
        }

        public abstract Task<bool> init(C credentials);

        public abstract Task<bool> UpdateCredsAsync(C credentials);

        protected abstract Task<bool> UpdateNowPlaying(string artist, string album, string song);

        protected abstract Task ScrobbleSong(string artist, string album, string song);

        /// <summary>Artist / album / track to submit: YouTube Music's data for songs, a cleaned-up video title otherwise.</summary>
        public static (string artist, string album, string song) GetScrobbleFields(YouTubeMediaInfo info) {
            var settings = Properties.Settings.Default;
            string song;
            string artist;
            var album = info.SongAlbum ?? "";

            if (info.IsMusic && !string.IsNullOrWhiteSpace(info.SongTitle)) {
                song = info.SongTitle!;
                artist = info.SongArtist ?? info.ChannelName ?? info.Artist;
            } else if (settings.CleanTitles) {
                var (parsedArtist, parsedTitle) = TitleCleaner.SplitArtistTitle(TitleCleaner.Clean(info.Title));
                song = parsedTitle;
                artist = parsedArtist ?? info.ChannelName ?? info.Artist;
            } else {
                song = info.Title;
                artist = info.ChannelName ?? info.Artist;
            }

            if (settings.LastfmScrobblePrimaryArtist) {
                artist = TextMatch.PrimaryArtist(artist);
            }
            return (artist, album, song);
        }

        public async void Scrobbleit(YouTubeMediaInfo info) {
            // Called every Constants.RefreshPeriod seconds while something is playing. A song is scrobbled once half of it
            // (or ScrobbleMaxWait seconds) has been listened to. There is no offline queue.
            if (!info.IsMusic && !Properties.Settings.Default.ScrobbleVideos) {
                return;
            }
            if (!info.IsResolved && info.Kind == MediaKind.Unknown && !info.ResolveGaveUp) {
                return; // wait until we know whether it is music
            }

            try {
                var thisSongID = info.VideoId ?? $"{info.Site}|{info.Title}|{info.Artist}";
                var (artist, album, song) = GetScrobbleFields(info);

                if (thisSongID != lastSongID) {
                    lastSongID = thisSongID;
                    elapsedSeconds = 0;
                    scrobbleInProgress = false;
                    hasScrobbled = false;
                    nowPlayingSent = false;
                    logger?.Log($"[{serviceName} scrobbler] New Song: {lastSongID}");
                } else {
                    elapsedSeconds += Constants.RefreshPeriod;

                    if (hasScrobbled && IsRepeating(info)) {
                        hasScrobbled = false;
                        elapsedSeconds = 0;
                        logger?.Log($"[{serviceName} scrobbler] Repeating Song: {lastSongID}");
                    }

                    if (IsTimeToScrobble(info) && !hasScrobbled && !scrobbleInProgress) {
                        logger?.Log($"[{serviceName} scrobbler] Scrobbling: {artist} - {song} ({album})");
                        try {
                            scrobbleInProgress = true;
                            await ScrobbleSong(artist, album, song);
                            hasScrobbled = true;
                        } finally {
                            scrobbleInProgress = false;
                        }
                    }

                    lastSongProgress = info.CurrentTime ?? 0.0;
                }

                if (!nowPlayingSent) {
                    nowPlayingSent = await UpdateNowPlaying(artist, album, song);
                    if (nowPlayingSent) {
                        logger?.Log($"[{serviceName} scrobbler] Updated now playing: {artist} - {song}");
                    }
                }
            } catch (Exception ex) {
                logger?.Log($"[{serviceName} scrobbler] An error occurred while scrobbling: {ex}");
            }
        }
    }

    internal class LastFmScrobbler : YouTubeScrobbler<LastFmCredentials> {
        private LastAuth? lastfmAuth;
        private IScrobbler? lastFmScrobbler;
        private ITrackApi? trackApi;

        public LastFmScrobbler(Logger? logger = null) : base("Last.FM", logger) { }

        public async override Task<bool> init(LastFmCredentials credentials) {
            if (string.IsNullOrEmpty(credentials.apiKey)
                || string.IsNullOrEmpty(credentials.apiSecret)
                || string.IsNullOrEmpty(credentials.username)) {
                return false;
            }
            lastfmAuth = new LastAuth(credentials.apiKey, credentials.apiSecret);
            await lastfmAuth.GetSessionTokenAsync(credentials.username, credentials.password);

            lastFmScrobbler = new MemoryScrobbler(lastfmAuth, Constants.HttpClient);
            trackApi = new TrackApi(lastfmAuth, Constants.HttpClient);

            logger?.Log(lastfmAuth.Authenticated ? "Last.FM authentication succeeded" : "Last.FM authentication failed");
            return lastfmAuth.Authenticated;
        }

        public async override Task<bool> UpdateCredsAsync(LastFmCredentials credentials) {
            logger?.Log("[Last.FM scrobbler] Updating credentials");
            lastfmAuth = null;
            lastFmScrobbler = null;
            trackApi = null;
            return await init(credentials);
        }

        protected async override Task ScrobbleSong(string artist, string album, string song) {
            if (lastFmScrobbler == null || lastfmAuth?.Authenticated != true) {
                return;
            }
            var scrobble = new Scrobble(artist, album, song, DateTime.UtcNow);
            await lastFmScrobbler.ScrobbleAsync(scrobble);
        }

        protected async override Task<bool> UpdateNowPlaying(string artist, string album, string song) {
            if (trackApi == null || lastfmAuth?.Authenticated != true) {
                return false;
            }
            var scrobble = new Scrobble(artist, album, song, DateTime.UtcNow);
            await trackApi.UpdateNowPlayingAsync(scrobble);
            return true;
        }
    }

    internal class ListenBrainzScrobbler : YouTubeScrobbler<ListenBrainzCredentials> {
        private ListenBrainz? listenBrainzClient;

        public ListenBrainzScrobbler(Logger? logger = null) : base("ListenBrainz", logger) { }

        public async override Task<bool> init(ListenBrainzCredentials credentials) {
            listenBrainzClient = new();

            if (string.IsNullOrEmpty(credentials.userToken)) {
                logger?.Log("No ListenBrainz user token found");
                return false;
            }

            var tokenValidation = await listenBrainzClient.ValidateTokenAsync(credentials.userToken);
            if (tokenValidation.Valid == true) {
                logger?.Log("ListenBrainz authentication succeeded");
                listenBrainzClient.UserToken = credentials.userToken;
            } else {
                logger?.Log("ListenBrainz authentication failed");
                listenBrainzClient.UserToken = null;
            }
            return tokenValidation.Valid ?? false;
        }

        public async override Task<bool> UpdateCredsAsync(ListenBrainzCredentials credentials) {
            logger?.Log("[ListenBrainz] Updating credentials");
            return await init(credentials);
        }

        protected async override Task ScrobbleSong(string artist, string album, string song) {
            if (string.IsNullOrEmpty(listenBrainzClient?.UserToken)) {
                return;
            }
            await listenBrainzClient.SubmitSingleListenAsync(song, artist, album);
        }

        protected async override Task<bool> UpdateNowPlaying(string artist, string album, string song) {
            if (string.IsNullOrEmpty(listenBrainzClient?.UserToken)) {
                return false;
            }
            await listenBrainzClient.SetNowPlayingAsync(song, artist, album);
            return true;
        }
    }
}
