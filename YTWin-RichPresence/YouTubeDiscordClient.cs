using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DiscordRPC;
using YTWin_RichPresence;
using Localisation = YTWin_RichPresence.Properties.Localisation;

internal class YouTubeDiscordClient {
    public enum RPStatusDisplayOptions {
        Artist = 0, AppName = 1, Title = 2
    }

    public static RPStatusDisplayOptions StatusDisplayOptionFromIndex(int i) {
        return Enum.IsDefined(typeof(RPStatusDisplayOptions), i) ? (RPStatusDisplayOptions)i : RPStatusDisplayOptions.Artist;
    }

    private const int MaxFieldBytes = 128;

    public RPStatusDisplayOptions statusDisplayOptions;
    DiscordRpcClient? client;
    string? activeClientId;
    bool enabled = false;
    Logger? logger;
    volatile bool isConnected = false;
    DiscordClientType preferredClient;
    bool warnedMissingId = false;
    string? lastPresenceKey = null;

    public string ClientIdVideo { get; private set; }
    public string ClientIdMusic { get; private set; }

    public YouTubeDiscordClient(
        string clientIdVideo,
        string clientIdMusic,
        bool enabled = true,
        RPStatusDisplayOptions statusDisplayOptions = RPStatusDisplayOptions.Artist,
        Logger? logger = null,
        DiscordClientType preferredClient = DiscordClientType.Auto
    ) {
        ClientIdVideo = (clientIdVideo ?? "").Trim();
        ClientIdMusic = (clientIdMusic ?? "").Trim();
        DiscordAppInfoCache.Prefetch(ClientIdVideo, logger);
        DiscordAppInfoCache.Prefetch(ClientIdMusic, logger);
        this.enabled = enabled;
        this.statusDisplayOptions = statusDisplayOptions;
        this.logger = logger;
        this.preferredClient = preferredClient;
    }

    public bool HasClientId => ClientIdVideo.Length > 0 || ClientIdMusic.Length > 0;

    public void SetClientIds(string clientIdVideo, string clientIdMusic) {
        ClientIdVideo = (clientIdVideo ?? "").Trim();
        ClientIdMusic = (clientIdMusic ?? "").Trim();
        DiscordAppInfoCache.Prefetch(ClientIdVideo, logger);
        DiscordAppInfoCache.Prefetch(ClientIdMusic, logger);
        warnedMissingId = false;
        if (client != null && activeClientId != ClientIdVideo && activeClientId != ClientIdMusic) {
            client.ClearPresence();
            DeinitClient();
        }
    }

    /// <summary>Discord rejects fields over 128 bytes of UTF-8, which Japanese titles reach quickly.</summary>
    private static string TrimToBytes(string str, int maxBytes = MaxFieldBytes) {
        if (Encoding.UTF8.GetByteCount(str) <= maxBytes) {
            return str;
        }
        const string ellipsis = "…";
        var budget = maxBytes - Encoding.UTF8.GetByteCount(ellipsis);
        var sb = new StringBuilder();
        var bytes = 0;
        foreach (var rune in str.EnumerateRunes()) {
            var length = rune.Utf8SequenceLength;
            if (bytes + length > budget) {
                break;
            }
            sb.Append(rune.ToString());
            bytes += length;
        }
        return sb.ToString().TrimEnd() + ellipsis;
    }

    // Discord refuses strings shorter than 2 characters
    private static string? PadField(string? str) {
        if (string.IsNullOrWhiteSpace(str)) {
            return null;
        }
        var s = TrimToBytes(str.Trim());
        while (s.Length < 2) {
            s += "\u0000";
        }
        return s;
    }

    public void SetPresence(YouTubeMediaInfo info, bool showServiceIcon, bool showThumbnail, bool showAlbumTitle) {
        if (!enabled) {
            return;
        }

        var isMusic = info.IsMusic;
        var appId = isMusic && ClientIdMusic.Length > 0 ? ClientIdMusic : (ClientIdVideo.Length > 0 ? ClientIdVideo : ClientIdMusic);
        if (appId.Length == 0) {
            if (!warnedMissingId) {
                logger?.Log("No Discord Application ID configured; open the settings window and enter one");
                warnedMissingId = true;
            }
            return;
        }
        EnsureClient(appId);

        var details = PadField(info.DisplayTitle) ?? "YouTube";
        var state = PadField(info.DisplayArtist);

        string? largeText = null;
        if (isMusic && YTWin_RichPresence.Properties.Settings.Default.EnableSyncLyrics && info.SyncedLyrics != null) {
            var currentTime = info.CurrentTime != null ? TimeSpan.FromSeconds(info.CurrentTime.Value) : (DateTime.UtcNow - (info.PlaybackStart ?? DateTime.UtcNow));
            var lyric = LRCLibClient.GetCurrentLyric(info.SyncedLyrics, currentTime);
            if (!string.IsNullOrWhiteSpace(lyric)) {
                largeText = lyric;
            }
        }
        if (largeText == null && showAlbumTitle) {
            largeText = isMusic ? (info.SongAlbum ?? info.ChannelName) : (info.ChannelName ?? info.Artist);
        }

        var isMusicService = isMusic || info.Site == YouTubeSite.YouTubeMusic;
        var serviceName = isMusicService ? Localisation.Presence_YouTubeMusic : Localisation.Presence_YouTube;
        // small icon: an uploaded asset named "youtube"/"youtubemusic" if the application has one, otherwise the application's icon
        var app = DiscordAppInfoCache.Get(appId);
        var serviceIcon = app?.ImageFor(isMusicService ? Constants.DiscordMusicServiceAssetName : Constants.DiscordServiceAssetName);
        var pauseIcon = app?.ImageFor(Constants.DiscordPauseAssetName, Constants.DiscordPauseIconUrl) is string pause && app.AssetNames.Contains(Constants.DiscordPauseAssetName)
            ? pause
            : Constants.DiscordPauseIconUrl;
        var largeImage = (showThumbnail ? info.ThumbnailUrl : null) ?? serviceIcon ?? "";

        var statusDisplay = statusDisplayOptions switch {
            RPStatusDisplayOptions.Artist => StatusDisplayType.State,
            RPStatusDisplayOptions.AppName => StatusDisplayType.Name,
            _ => StatusDisplayType.Details,
        };

        try {
            var rp = new RichPresence() {
                Details = details,
                State = state,
                Assets = new Assets() {
                    LargeImageKey = largeImage,
                    LargeImageText = PadField(largeText) ?? "",
                    SmallImageKey = "",
                    SmallImageText = ""
                },
                Type = isMusic ? ActivityType.Listening : ActivityType.Watching,
                StatusDisplay = statusDisplay,
            };

            if (info.VideoUrl != null) {
                rp.DetailsUrl = info.VideoUrl;
            }

            var buttons = new List<Button>();
            if (info.VideoUrl != null) {
                buttons.Add(new Button() { Label = Localisation.DiscordButton_WatchOnYouTube, Url = info.VideoUrl });
            }
            if (isMusic && info.MusicUrl != null) {
                buttons.Add(new Button() { Label = Localisation.DiscordButton_ListenOnYouTubeMusic, Url = info.MusicUrl });
            } else if (info.ChannelUrl != null) {
                buttons.Add(new Button() { Label = Localisation.DiscordButton_ViewArtist, Url = info.ChannelUrl });
            }
            if (buttons.Count > 0) {
                rp.Buttons = buttons.ToArray();
            }

            if (info.IsPaused) {
                rp.Assets.SmallImageKey = pauseIcon;
                rp.Assets.SmallImageText = Localisation.Presence_Paused;
            } else if (showServiceIcon && serviceIcon != null && largeImage != serviceIcon) {
                rp.Assets.SmallImageKey = serviceIcon;
                rp.Assets.SmallImageText = serviceName;
            }

            if (!info.IsPaused && info.PlaybackStart != null) {
                rp.Timestamps = info.PlaybackEnd != null
                    ? new Timestamps((DateTime)info.PlaybackStart, (DateTime)info.PlaybackEnd)
                    : new Timestamps((DateTime)info.PlaybackStart);
            }

            // Every update makes Discord re-render the activity card and resolve the external image again,
            // so only send when something actually changed.
            var key = string.Join("\u001f", appId, details, state, rp.Assets.LargeImageKey, rp.Assets.LargeImageText,
                rp.Assets.SmallImageKey, rp.Type, statusDisplay, rp.Timestamps?.Start?.ToString("o"), rp.Timestamps?.End?.ToString("o"),
                string.Join("|", buttons.Select(b => b.Url)));
            if (client == null) {
                logger?.Log("Tried to set Discord RP, but no client");
            } else if (key != lastPresenceKey) {
                client.SetPresence(rp);
                lastPresenceKey = key;
                // the IPC pipe can be down while the client keeps retrying, e.g. Discord restarting
                var what = isConnected ? "Set" : "Queued (not connected to Discord)";
                logger?.Log($"{what} Discord RP ({(isMusic ? "listening" : "watching")}): {details} / {state} [small: {(rp.Assets.SmallImageKey is { Length: > 0 } s ? s : "-")}]");
            }
        } catch (Exception ex) {
            logger?.Log($"Couldn't set Discord RP:\n{ex}");
        }
    }

    public void Enable() {
        enabled = true;
    }

    /// <summary>Hides the presence but keeps the connection to Discord, so the next update shows up immediately.</summary>
    public void ClearPresence() {
        if (client != null && lastPresenceKey != null) {
            try {
                client.ClearPresence();
                logger?.Log("Cleared Discord RP");
            } catch (Exception ex) {
                logger?.Log($"Couldn't clear Discord RP: {ex.Message}");
            }
        }
        lastPresenceKey = null;
    }

    public void Disable() {
        if (!enabled && client == null) {
            return;
        }
        enabled = false;
        client?.ClearPresence();
        DeinitClient();
    }

    public void SetPreferredClient(DiscordClientType newClient) {
        if (preferredClient == newClient) {
            return;
        }
        preferredClient = newClient;
        if (client != null) {
            client.ClearPresence();
            DeinitClient();
        }
    }

    private void EnsureClient(string appId) {
        if (client != null && activeClientId == appId) {
            return;
        }
        if (client != null) {
            client.ClearPresence();
            DeinitClient();
        }
        InitClient(appId);
    }

    private void InitClient(string appId) {
        int pipe = -1;
        if (preferredClient != DiscordClientType.Auto) {
            var resolved = DiscordPipeFinder.FindPipeForClient(preferredClient, logger);
            if (resolved != null) {
                pipe = resolved.Value;
            }
        }
        try {
            client = new DiscordRpcClient(appId, pipe: pipe, logger: logger);
            client.OnReady += (_, _) => {
                isConnected = true;
                lastPresenceKey = null;   // re-send the current presence on the next refresh in case the queued one was lost
                logger?.Log("Connected to Discord");
            };
            client.OnConnectionFailed += (_, _) => isConnected = false;
            client.OnClose += (_, _) => isConnected = false;
            client.Initialize();
            activeClientId = appId;
            lastPresenceKey = null;
            logger?.Log($"Discord RPC client started for application {appId}");
        } catch (Exception ex) {
            logger?.Log($"Could not start the Discord RPC client: {ex.Message}");
            client = null;
            activeClientId = null;
        }
    }

    private void DeinitClient() {
        isConnected = false;
        if (client != null) {
            try {
                client.Deinitialize();
                client.Dispose();
            } catch (Exception ex) {
                logger?.Log($"Could not stop the Discord RPC client: {ex.Message}");
            }
            client = null;
            activeClientId = null;
            lastPresenceKey = null;
        }
    }
}
