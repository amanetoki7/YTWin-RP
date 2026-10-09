using Hardcodet.Wpf.TaskbarNotification;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Localisation = YTWin_RichPresence.Properties.Localisation;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace YTWin_RichPresence {
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application {
        private static readonly CultureInfo InitialUICulture = CultureInfo.CurrentUICulture;
        private const string OpenSettingsWindowArg = "--open-settings-window";

        private TaskbarIcon? taskbarIcon;
        private readonly Logger? logger;
        private readonly InnerTubeClient innerTube;
        private readonly YouTubeResolver resolver;
        private readonly MediaSessionReader mediaSessionReader;
        private readonly ExtensionBridge extensionBridge;
        private readonly LRCLibClient lrclibClient;
        private readonly YouTubeTracker tracker;
        private readonly YouTubeDiscordClient discordClient;
        private readonly LastFmScrobbler lastFmScrobblerClient;
        private readonly ListenBrainzScrobbler listenBrainzScrobblerClient;

        internal static string NormalizeLanguageCode(string? languageCode) {
            return languageCode?.Trim() switch {
                "en" => "en",
                "de" => "de",
                "tr" => "tr",
                "ko" => "ko",
                "ja" => "ja",
                "ru" => "ru",
                "es" => "es",
                "es-MX" => "es-MX",
                _ => ""
            };
        }

        internal static void ApplyLanguagePreference() {
            var languageCode = NormalizeLanguageCode(YTWin_RichPresence.Properties.Settings.Default.Language);
            var culture = languageCode == ""
                ? InitialUICulture
                : CultureInfo.GetCultureInfo(languageCode);

            Thread.CurrentThread.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            Localisation.Culture = culture;
        }

        internal static void RestartApplication(bool openSettingsWindow = false) {
            var exePath = Constants.ExePath;
            if (!string.IsNullOrWhiteSpace(exePath)) {
                Process.Start(new ProcessStartInfo {
                    FileName = exePath,
                    Arguments = openSettingsWindow ? OpenSettingsWindowArg : "",
                    UseShellExecute = true
                });
            }
            Application.Current.Shutdown();
        }

        private static string FirstNonEmpty(string? preferred, string? fallback) {
            return !string.IsNullOrWhiteSpace(preferred) ? preferred!.Trim() : (fallback ?? "").Trim();
        }

        /// <summary>The user's override from the settings, otherwise the application built into this build.</summary>
        internal static string EffectiveDiscordClientID => FirstNonEmpty(YTWin_RichPresence.Properties.Settings.Default.DiscordClientID, Constants.DefaultDiscordClientID);

        internal static string EffectiveDiscordClientIDMusic => FirstNonEmpty(YTWin_RichPresence.Properties.Settings.Default.DiscordClientIDMusic, Constants.DefaultDiscordClientIDMusic);

        public LastFmCredentials lastFmCredentials {
            get {
                var creds = new LastFmCredentials();
                creds.apiKey = YTWin_RichPresence.Properties.Settings.Default.LastfmAPIKey;
                creds.apiSecret = YTWin_RichPresence.Properties.Settings.Default.LastfmSecret;
                creds.username = YTWin_RichPresence.Properties.Settings.Default.LastfmUsername;
                creds.password = SettingsWindow.GetLastFMPassword();
                return creds;
            }
        }

        public ListenBrainzCredentials listenBrainzCredentials {
            get {
                var creds = new ListenBrainzCredentials();
                creds.userToken = YTWin_RichPresence.Properties.Settings.Default.ListenBrainzUserToken;
                return creds;
            }
        }

        public App() {
            ApplyLanguagePreference();
            var settings = YTWin_RichPresence.Properties.Settings.Default;

            // make logger
            try {
                logger = new Logger();
                logger.Log("Application started");
                logger.Log($"{Environment.OSVersion}");
                logger.Log($"Using UI language: {CultureInfo.CurrentUICulture.Name}");
            } catch {
                logger = null;
            }

            // check for updates
            if (settings.CheckForUpdatesOnStartup) {
                _ = CheckForUpdates();
            }

            // Discord RPC (connects lazily once something is playing)
            var statusDisplayOptions = YouTubeDiscordClient.StatusDisplayOptionFromIndex(settings.RPDisplayChoice);
            var preferredDiscordClient = (DiscordClientType)settings.DiscordClientPreference;
            discordClient = new(EffectiveDiscordClientID, EffectiveDiscordClientIDMusic, enabled: false, statusDisplayOptions: statusDisplayOptions, logger: logger, preferredClient: preferredDiscordClient);
            if (!discordClient.HasClientId) {
                logger?.Log("No Discord Application ID available: set Constants.DefaultDiscordClientID or enter one in the settings");
            }

            // scrobblers
            lastFmScrobblerClient = new LastFmScrobbler(logger);
            if (settings.LastfmEnable) {
                _ = lastFmScrobblerClient.init(lastFmCredentials);
            }
            listenBrainzScrobblerClient = new ListenBrainzScrobbler(logger);
            if (settings.ListenBrainzEnable) {
                _ = listenBrainzScrobblerClient.init(listenBrainzCredentials);
            }

            // YouTube lookups
            innerTube = new InnerTubeClient(logger);
            ApplyInnerTubeLocale();
            resolver = new YouTubeResolver(innerTube, logger) { DetectMusic = settings.DetectMusic };
            lrclibClient = new LRCLibClient(logger);

            // playback sources
            mediaSessionReader = new MediaSessionReader(logger);
            extensionBridge = new ExtensionBridge(logger);
            if (settings.ExtensionBridgeEnabled) {
                extensionBridge.Start(settings.ExtensionBridgePort);
            }

            tracker = new YouTubeTracker(mediaSessionReader, extensionBridge, resolver, lrclibClient, OnPlaybackRefresh, logger) {
                RequireYouTubeWindow = settings.RequireYouTubeWindow
            };
            _ = tracker.StartAsync();
        }

        private void ApplyInnerTubeLocale() {
            var language = NormalizeLanguageCode(YTWin_RichPresence.Properties.Settings.Default.Language);
            var culture = language == "" ? InitialUICulture : CultureInfo.GetCultureInfo(language);
            innerTube.Hl = culture.TwoLetterISOLanguageName;
            try {
                innerTube.Gl = RegionInfo.CurrentRegion.TwoLetterISORegionName;
            } catch {
                innerTube.Gl = "US";
            }
            logger?.Log($"YouTube lookups use hl={innerTube.Hl} gl={innerTube.Gl}");
        }

        private void OnPlaybackRefresh(YouTubeMediaInfo? info) {
            var settings = YTWin_RichPresence.Properties.Settings.Default;

            var kindAllowed = info != null && (info.IsMusic ? settings.ShowMusic : settings.ShowVideos);
            var show = info != null
                && info.IsConfirmedYouTube
                && kindAllowed
                && (settings.ShowRPWhenPaused || !info.IsPaused);

            if (!show) {
                if (settings.EnableDiscordRP) {
                    discordClient.ClearPresence();   // paused / nothing playing: hide, but stay connected
                } else {
                    discordClient.Disable();
                }
                return;
            }

            if (settings.EnableDiscordRP) {
                discordClient.Enable();
                discordClient.SetPresence(info!, settings.ShowServiceIcon, settings.EnableRPCoverImages, settings.ShowAlbumTitle);
            } else {
                discordClient.Disable();
            }

            if (!info!.IsPaused) {
                if (settings.LastfmEnable) {
                    lastFmScrobblerClient.Scrobbleit(info);
                }
                if (settings.ListenBrainzEnable) {
                    listenBrainzScrobblerClient.Scrobbleit(info);
                }
            }
        }

        protected override void OnStartup(StartupEventArgs e) {
            base.OnStartup(e);
            taskbarIcon = (TaskbarIcon)FindResource("TaskbarIcon");
            if (e.Args.Contains(OpenSettingsWindowArg, StringComparer.OrdinalIgnoreCase)) {
                Current.Dispatcher.BeginInvoke(new Action(() => {
                    var settingsWindow = new SettingsWindow();
                    settingsWindow.Show();
                    settingsWindow.Focus();
                }));
            } else if (!discordClient.HasClientId) {
                // first run: point the user at the one setting that cannot have a default
                var reminder = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                reminder.Tick += (_, _) => {
                    reminder.Stop();
                    try {
                        taskbarIcon?.ShowBalloonTip(Localisation.Message_DiscordClientIdMissing_Title, Localisation.Message_DiscordClientIdMissing, BalloonIcon.Info);
                    } catch (Exception ex) {
                        logger?.Log($"Could not show balloon tip: {ex.Message}");
                    }
                };
                reminder.Start();
            }
        }

        private void Application_Exit(object sender, ExitEventArgs e) {
            taskbarIcon?.Dispose();
            tracker.Dispose();
            extensionBridge.Dispose();
            mediaSessionReader.Dispose();
            discordClient.Disable();
            logger?.Log("Application finished");
        }

        // ------------------------------------------------------------------
        //  Called from the settings window
        // ------------------------------------------------------------------

        internal void UpdateRPStatusDisplay(YouTubeDiscordClient.RPStatusDisplayOptions newVal) {
            discordClient.statusDisplayOptions = newVal;
            tracker.RequestRefresh();
        }

        internal void UpdateDiscordClientPreference(DiscordClientType newClient) {
            discordClient.SetPreferredClient(newClient);
            tracker.RequestRefresh();
        }

        internal void UpdateDiscordClientIds() {
            var settings = YTWin_RichPresence.Properties.Settings.Default;
            discordClient.SetClientIds(EffectiveDiscordClientID, EffectiveDiscordClientIDMusic);
            logger?.Log($"Discord Application IDs updated (video: {(EffectiveDiscordClientID.Length > 0 ? "set" : "empty")}, music: {(EffectiveDiscordClientIDMusic.Length > 0 ? "set" : "empty")})");
            tracker.RequestRefresh();
        }

        internal void UpdateDetectionPreferences() {
            var settings = YTWin_RichPresence.Properties.Settings.Default;
            resolver.DetectMusic = settings.DetectMusic;
            tracker.RequireYouTubeWindow = settings.RequireYouTubeWindow;
            tracker.RequestRefresh();
        }

        internal void UpdateExtensionBridge() {
            var settings = YTWin_RichPresence.Properties.Settings.Default;
            if (settings.ExtensionBridgeEnabled) {
                extensionBridge.Start(settings.ExtensionBridgePort);
            } else {
                extensionBridge.Stop();
            }
            tracker.RequestRefresh();
        }

        internal string ExtensionBridgeStatus {
            get {
                if (!extensionBridge.IsListening) {
                    var text = Localisation.Settings_Detection_Extension_Status_NotListening;
                    return extensionBridge.LastError == null ? text : $"{text} ({extensionBridge.LastError})";
                }
                var status = string.Format(Localisation.Settings_Detection_Extension_Status_Listening, extensionBridge.Port);
                if (extensionBridge.LastReportUtc is DateTime last) {
                    var age = (int)(DateTime.UtcNow - last).TotalSeconds;
                    status += "\n" + string.Format(Localisation.Settings_Detection_Extension_Status_LastReport, age);
                } else {
                    status += "\n" + Localisation.Settings_Detection_Extension_Status_NoReport;
                }
                return status;
            }
        }

        internal string CurrentPlaybackStatus {
            get {
                var info = tracker.Current;
                if (info == null) {
                    return Localisation.Settings_Detection_Status_Nothing;
                }
                var kind = info.Kind switch {
                    MediaKind.Music => Localisation.Settings_Detection_Status_Music,
                    MediaKind.Video => Localisation.Settings_Detection_Status_Video,
                    _ => Localisation.Settings_Detection_Status_Unknown,
                };
                var source = info.Source == MediaSource.Extension ? Localisation.Settings_Detection_Status_SourceExtension : Localisation.Settings_Detection_Status_SourceMediaSession;
                var confirmed = info.IsConfirmedYouTube ? "" : $" ({Localisation.Settings_Detection_Status_Unconfirmed})";
                return $"{info.DisplayTitle} — {info.DisplayArtist}\n{kind} · {source}{confirmed}{(info.IsPaused ? " · " + Localisation.Presence_Paused : "")}";
            }
        }

        internal async Task<bool> UpdateLastfmCreds() {
            return await lastFmScrobblerClient.UpdateCredsAsync(lastFmCredentials);
        }

        internal async Task<bool> UpdateListenBrainzCreds() {
            return await listenBrainzScrobblerClient.UpdateCredsAsync(listenBrainzCredentials);
        }

        internal void ClearLookupCache() {
            resolver.ClearCache();
            tracker.RequestRefresh();
        }

        internal async Task CheckForUpdates() {
            try {
                static int StringVerToInt(string v) {
                    var verStr = v[1..].Split("b")[0].Replace(".", "").PadRight(4, '0');
                    return int.Parse(verStr);
                }

                if (!Constants.HttpClient.DefaultRequestHeaders.UserAgent.Any()) {
                    Constants.HttpClient.DefaultRequestHeaders.Add("User-Agent", "YTWin-RP");
                }

                var result = await Constants.HttpClient.GetStringAsync(Constants.GithubReleasesApiUrl);
                using var json = JsonDocument.Parse(result);

                var verLocal = Constants.ProgramVersionBase;
                var verRemote = json.RootElement.GetProperty("name").GetString()!;

                var numverLocal = StringVerToInt(verLocal);
                var numverRemote = StringVerToInt(verRemote);

                // TODO add support for multiple beta versions (i.e. b1 and b2)
                if (numverRemote > numverLocal || (numverRemote == numverLocal && verLocal.Contains('b') && !verRemote.Contains('b'))) {
                    Application.Current.Dispatcher.Invoke((Action)async delegate {
                        var result = await new MessageBox {
                            Title = Localisation.Message_AppUpdate_Title,
                            Content = Localisation.Message_AppUpdate,
                            IsCloseButtonEnabled = false,
                            PrimaryButtonText = Localisation.Message_Yes,
                            SecondaryButtonText = Localisation.Message_No
                        }.ShowDialogAsync();

                        if (result == MessageBoxResult.Primary) {
                            Process.Start(new ProcessStartInfo {
                                FileName = Constants.GithubReleasesUrl,
                                UseShellExecute = true
                            });
                        }
                    });
                } else {
                    logger?.Log("No YTWin-RP updates available.");
                }
            } catch (Exception e) {
                logger?.Log($"Could not check for YTWin-RP updates: {e.Message}");
            }
        }
    }
}
