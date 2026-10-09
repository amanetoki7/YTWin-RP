using Meziantou.Framework.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Localisation = YTWin_RichPresence.Properties.Localisation;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace YTWin_RichPresence {
    /// <summary>
    /// Interaction logic for SettingsWindow.xaml
    /// </summary>
    public partial class SettingsWindow : FluentWindow {
        private bool isLanguageSelectorInitialized;
        private bool isDiscordClientSelectorInitialized;
        private readonly DispatcherTimer statusTimer;

        private static App CurrentApp => (App)Application.Current;

        public SettingsWindow() {
            ApplicationThemeManager.ApplySystemTheme();
            SystemThemeWatcher.Watch(this);
            InitializeComponent();
            InitializeLanguageSelector();
            InitializeDiscordClientSelector();

            string imagePath = IsDarkMode()
                ? "/Resources/GitHub_Invertocat_White.png"
                : "/Resources/GitHub_Invertocat_Black.png";

            Image_GitHub.Source = new BitmapImage(new Uri(imagePath, UriKind.Relative));
            LastfmPassword.Password = GetLastFMPassword();
            TextBox_ExtensionPort.Text = Properties.Settings.Default.ExtensionBridgePort.ToString();
            // the Discord applications are built into the app; the ID fields only exist for builds without them
            Card_DiscordClientId.Visibility = string.IsNullOrWhiteSpace(Constants.DefaultDiscordClientID) ? Visibility.Visible : Visibility.Collapsed;
            ValidateDiscordClientIds();
            UpdateStatusTexts();

            statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            statusTimer.Tick += (_, _) => UpdateStatusTexts();
            statusTimer.Start();
            Closed += (_, _) => statusTimer.Stop();
        }

        private void UpdateStatusTexts() {
            try {
                TextBlock_ExtensionStatus.Text = CurrentApp.ExtensionBridgeStatus;
                TextBlock_PlaybackStatus.Text = CurrentApp.CurrentPlaybackStatus;
            } catch (Exception ex) {
                Trace.WriteLine(ex.Message);
            }
        }

        // ------------------------------------------------------------------
        //  General
        // ------------------------------------------------------------------

        private void CheckBox_RunOnStartup_Click(object sender, RoutedEventArgs e) {
            if (CheckBox_RunOnStartup.IsChecked == true) {
                AddStartupShortcut();
            } else {
                RemoveStartupShortcut();
            }
            SaveSettings();
        }

        /// <summary>Generic handler for settings that only need saving (the binding already updated the value).</summary>
        private void Setting_Click(object sender, RoutedEventArgs e) {
            SaveSettings();
            CurrentApp.UpdateDetectionPreferences();
        }

        private void InitializeLanguageSelector() {
            TextBlock_LanguageLabel.Text = GetLocalisedString("Settings_General_Language", "Language");
            TextBlock_LanguageDescription.Text = GetLocalisedString("Settings_General_Language_Description", "Restart the app after changing the language.");
            ComboBoxItem_LanguageSystem.Content = GetLocalisedString("Settings_General_Language_System", "System default (System default)");
            ComboBoxItem_LanguageEnglish.Content = GetLocalisedString("Settings_General_Language_English", "English (English)");
            ComboBoxItem_LanguageGerman.Content = GetLocalisedString("Settings_General_Language_German", "German (Deutsch)");
            ComboBoxItem_LanguageTurkish.Content = GetLocalisedString("Settings_General_Language_Turkish", "Turkce (Turkce)");
            ComboBoxItem_LanguageKorean.Content = GetLocalisedString("Settings_General_Language_Korean", "Korean (한국어)");
            ComboBoxItem_LanguageJapanese.Content = GetLocalisedString("Settings_General_Language_Japanese", "Japanese (日本語)");
            ComboBoxItem_LanguageRussian.Content = GetLocalisedString("Settings_General_Language_Russian", "Russian (Русский)");
            ComboBoxItem_LanguageSpanish.Content = GetLocalisedString("Settings_General_Language_Spanish", "Spanish - Spain (Español de España)");
            ComboBoxItem_LanguageLatam.Content = GetLocalisedString("Settings_General_Language_Latam", "Spanish - Latin America (Español de Latinoamérica)");

            var selectedLanguage = App.NormalizeLanguageCode(Properties.Settings.Default.Language);
            if (!String.Equals(selectedLanguage, Properties.Settings.Default.Language, StringComparison.Ordinal)) {
                Properties.Settings.Default.Language = selectedLanguage;
                SaveSettings();
            }

            ComboBox_Language.SelectedItem = selectedLanguage switch {
                "en" => ComboBoxItem_LanguageEnglish,
                "de" => ComboBoxItem_LanguageGerman,
                "tr" => ComboBoxItem_LanguageTurkish,
                "ko" => ComboBoxItem_LanguageKorean,
                "ja" => ComboBoxItem_LanguageJapanese,
                "ru" => ComboBoxItem_LanguageRussian,
                "es" => ComboBoxItem_LanguageSpanish,
                "es-MX" => ComboBoxItem_LanguageLatam,
                _ => ComboBoxItem_LanguageSystem
            };

            isLanguageSelectorInitialized = true;
        }

        private static string GetLocalisedString(string key, string fallback) {
            return Localisation.ResourceManager.GetString(key, Localisation.Culture) ?? fallback;
        }

        private async void ComboBox_Language_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            if (!isLanguageSelectorInitialized) {
                return;
            }

            if (ComboBox_Language.SelectedItem is not ComboBoxItem selectedItem || selectedItem.Tag is not string selectedLanguage) {
                return;
            }

            var normalizedLanguage = App.NormalizeLanguageCode(selectedLanguage);
            if (normalizedLanguage == App.NormalizeLanguageCode(Properties.Settings.Default.Language)) {
                return;
            }

            Properties.Settings.Default.Language = normalizedLanguage;
            SaveSettings();
            App.ApplyLanguagePreference();

            var result = await new MessageBox {
                Title = GetLocalisedString("Message_RestartRequired_Title", "Restart Required"),
                Content = GetLocalisedString("Message_RestartRequired_Content", "Restart now to apply the language change?"),
                IsCloseButtonEnabled = false,
                PrimaryButtonText = Localisation.Message_Yes,
                SecondaryButtonText = Localisation.Message_No,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            }.ShowDialogAsync();

            if (result == MessageBoxResult.Primary) {
                App.RestartApplication(openSettingsWindow: true);
            }
        }

        // ------------------------------------------------------------------
        //  Detection
        // ------------------------------------------------------------------

        private void CheckBox_Detection_Click(object sender, RoutedEventArgs e) {
            SaveSettings();
            CurrentApp.UpdateDetectionPreferences();
        }

        private void Toggle_ExtensionBridge_Click(object sender, RoutedEventArgs e) {
            SaveSettings();
            CurrentApp.UpdateExtensionBridge();
            UpdateStatusTexts();
        }

        private void TextBox_ExtensionPort_LostFocus(object sender, RoutedEventArgs e) {
            ApplyExtensionPort();
        }

        private void TextBox_ExtensionPort_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) {
            if (e.Key == System.Windows.Input.Key.Enter) {
                ApplyExtensionPort();
            }
        }

        private void ApplyExtensionPort() {
            if (int.TryParse(TextBox_ExtensionPort.Text.Trim(), out var port) && port >= 1024 && port <= 65535) {
                if (port != Properties.Settings.Default.ExtensionBridgePort) {
                    Properties.Settings.Default.ExtensionBridgePort = port;
                    SaveSettings();
                    CurrentApp.UpdateExtensionBridge();
                }
            } else {
                TextBox_ExtensionPort.Text = Properties.Settings.Default.ExtensionBridgePort.ToString();
            }
            UpdateStatusTexts();
        }

        private void Button_OpenExtensionFolder_Click(object sender, RoutedEventArgs e) {
            var path = Constants.ExtensionFolder;
            if (path == null || !Directory.Exists(path)) {
                OpenUrl(Constants.GithubRepoUrl + "tree/master/extension");
                return;
            }
            Process.Start(new ProcessStartInfo {
                FileName = path,
                UseShellExecute = true
            });
        }

        // ------------------------------------------------------------------
        //  Discord
        // ------------------------------------------------------------------

        private void TextBox_DiscordClientID_LostFocus(object sender, RoutedEventArgs e) {
            ApplyDiscordClientIds();
        }

        private void TextBox_DiscordClientID_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) {
            if (e.Key == System.Windows.Input.Key.Enter) {
                ApplyDiscordClientIds();
            }
        }

        private static bool IsValidClientId(string value) {
            return value.Length == 0 || (value.Length >= 15 && value.Length <= 22 && value.All(char.IsDigit));
        }

        private bool ValidateDiscordClientIds() {
            var video = Properties.Settings.Default.DiscordClientID.Trim();
            var music = Properties.Settings.Default.DiscordClientIDMusic.Trim();
            var valid = IsValidClientId(video) && IsValidClientId(music);
            Panel_DiscordClientIDWarning.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
            return valid;
        }

        private void ApplyDiscordClientIds() {
            Properties.Settings.Default.DiscordClientID = Properties.Settings.Default.DiscordClientID.Trim();
            Properties.Settings.Default.DiscordClientIDMusic = Properties.Settings.Default.DiscordClientIDMusic.Trim();
            SaveSettings();
            if (ValidateDiscordClientIds()) {
                CurrentApp.UpdateDiscordClientIds();
            }
        }

        private void Button_OpenDeveloperPortal_Click(object sender, RoutedEventArgs e) {
            OpenUrl(Constants.DiscordDeveloperPortalUrl);
        }

        private void ComboBox_RPDisplayChoice_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            var newOption = YouTubeDiscordClient.StatusDisplayOptionFromIndex(ComboBox_RPDisplayChoice.SelectedIndex);
            CurrentApp.UpdateRPStatusDisplay(newOption);
            SaveSettings();
        }

        private void InitializeDiscordClientSelector() {
            TextBlock_DiscordClientLabel.Text = GetLocalisedString("Settings_Discord_ClientChoice", "Send Rich Presence to");
            TextBlock_DiscordClientDescription.Text = GetLocalisedString("Settings_Discord_ClientChoice_Description", "Choose which Discord app receives Rich Presence when you have more than one running (e.g. Stable and Canary). Leave on Automatic if you only use one.");
            ComboBoxItem_DiscordClientAuto.Content = GetLocalisedString("Settings_Discord_ClientChoice_Auto", "Automatic (any running client)");
            ComboBoxItem_DiscordClientStable.Content = GetLocalisedString("Settings_Discord_ClientChoice_Stable", "Discord (Stable)");
            ComboBoxItem_DiscordClientPTB.Content = GetLocalisedString("Settings_Discord_ClientChoice_PTB", "Discord PTB");
            ComboBoxItem_DiscordClientCanary.Content = GetLocalisedString("Settings_Discord_ClientChoice_Canary", "Discord Canary");

            var selectedIndex = Properties.Settings.Default.DiscordClientPreference;
            if (selectedIndex < 0 || selectedIndex >= ComboBox_DiscordClient.Items.Count) {
                selectedIndex = 0;
            }
            ComboBox_DiscordClient.SelectedIndex = selectedIndex;

            isDiscordClientSelectorInitialized = true;
        }

        private void ComboBox_DiscordClient_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            if (!isDiscordClientSelectorInitialized) {
                return;
            }
            var selectedIndex = ComboBox_DiscordClient.SelectedIndex;
            Properties.Settings.Default.DiscordClientPreference = selectedIndex;
            SaveSettings();
            CurrentApp.UpdateDiscordClientPreference((DiscordClientType)selectedIndex);
        }

        private void Button_OpenLyricCache_Click(object sender, RoutedEventArgs e) {
            var path = Path.Combine(Constants.AppDataFolder, "LyricCache");
            if (!Directory.Exists(path)) {
                Directory.CreateDirectory(path);
            }
            Process.Start(new ProcessStartInfo {
                FileName = path,
                UseShellExecute = true
            });
        }

        private async void Button_DeleteLyricCache_Click(object sender, RoutedEventArgs e) {
            var path = Path.Combine(Constants.AppDataFolder, "LyricCache");
            if (Directory.Exists(path)) {
                var result = await new MessageBox {
                    Title = Localisation.Message_ClearLyricCache_Title,
                    Content = Localisation.Message_ClearLyricCache,
                    IsCloseButtonEnabled = false,
                    PrimaryButtonText = Localisation.Message_Yes,
                    SecondaryButtonText = Localisation.Message_No,
                    Owner = this,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                }.ShowDialogAsync();

                if (result == MessageBoxResult.Primary) {
                    try {
                        foreach (var file in Directory.GetFiles(path)) {
                            File.Delete(file);
                        }
                        await new MessageBox {
                            Title = Localisation.Message_ClearedLyricCache_Title,
                            Content = Localisation.Message_ClearedLyricCache,
                            IsPrimaryButtonEnabled = false,
                            IsSecondaryButtonEnabled = false,
                            Owner = this,
                            WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        }.ShowDialogAsync();
                    } catch (Exception ex) {
                        await new MessageBox {
                            Title = Localisation.Message_Error,
                            Content = Localisation.Message_ClearLyricCache_Fail + ex.Message,
                            IsPrimaryButtonEnabled = false,
                            IsSecondaryButtonEnabled = false,
                            Owner = this,
                            WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        }.ShowDialogAsync();
                    }
                }
            } else {
                await new MessageBox {
                    Title = Localisation.Message_Information,
                    Content = Localisation.Message_ClearLyricCache_FailNotFound,
                    IsPrimaryButtonEnabled = false,
                    IsSecondaryButtonEnabled = false,
                    Owner = this,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                }.ShowDialogAsync();
            }
        }

        // ------------------------------------------------------------------
        //  Scrobbling
        // ------------------------------------------------------------------

        private void ScrobbleMaxTime_TextChanged(object sender, TextChangedEventArgs e) {
            try {
                int.Parse(ScrobbleMaxTime.Text);
            } catch {
                ScrobbleMaxTime.Text = $"{Properties.Settings.Default.ScrobbleMaxWait}";
            } finally {
                SaveSettings();
            }
        }

        private async void SaveLastFMCreds_Click(object sender, RoutedEventArgs e) {
            // Store the password in the Windows Credential Manager rather than in the plain-text settings file.
            try {
                CredentialManager.WriteCredential(
                    applicationName: Constants.LastFMCredentialTargetName,
                    userName: "",
                    secret: LastfmPassword.Password,
                    persistence: CredentialPersistence.LocalMachine);
            } catch (Exception ex) {
                Trace.WriteLine(ex.Message);
                Trace.WriteLine(ex.StackTrace);
            }
            SaveSettings(); // The other three values are just stored in Settings

            if (Properties.Settings.Default.LastfmEnable) {
                var result = await CurrentApp.UpdateLastfmCreds();
                await new MessageBox {
                    Title = Localisation.Message_LastFM_Authentication_Title,
                    Content = result ? Localisation.Message_LastFM_Authentication_Success : Localisation.Message_LastFM_Authentication_Fail,
                    IsPrimaryButtonEnabled = false,
                    IsSecondaryButtonEnabled = false,
                    Owner = this,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                }.ShowDialogAsync();
            }
        }

        private async void SaveListenBrainzCreds_Click(object sender, RoutedEventArgs e) {
            SaveSettings();
            if (Properties.Settings.Default.ListenBrainzEnable) {
                var result = await CurrentApp.UpdateListenBrainzCreds();
                await new MessageBox {
                    Title = Localisation.Message_ListenBrainz_Authentication_Title,
                    Content = result ? Localisation.Message_ListenBrainz_Authentication_Success : Localisation.Message_ListenBrainz_Authentication_Fail,
                    IsPrimaryButtonEnabled = false,
                    IsSecondaryButtonEnabled = false,
                    Owner = this,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                }.ShowDialogAsync();
            }
        }

        public static string GetLastFMPassword() {
            var cred = CredentialManager.ReadCredential(applicationName: Constants.LastFMCredentialTargetName);
            return cred?.Password ?? String.Empty;
        }

        // ------------------------------------------------------------------
        //  Helpers
        // ------------------------------------------------------------------

        private static void AddStartupShortcut() {
            // from https://stackoverflow.com/questions/234231/creating-application-shortcut-in-a-directory
            var t = Type.GetTypeFromCLSID(new Guid("72C24DD5-D70A-438B-8A42-98424B88AFB8")); // Windows Script Host Shell Object
            dynamic shell = Activator.CreateInstance(t!)!;
            try {
                var lnk = shell.CreateShortcut(Constants.AppShortcutPath);
                try {
                    lnk.TargetPath = Constants.ExePath;
                    lnk.IconLocation = $"{Constants.ExePath}, 0";
                    lnk.Save();
                } finally {
                    Marshal.FinalReleaseComObject(lnk);
                }
            } finally {
                Marshal.FinalReleaseComObject(shell);
            }
        }

        private static void RemoveStartupShortcut() {
            File.Delete(Constants.AppShortcutPath);
        }

        private static void SaveSettings() {
            Properties.Settings.Default.Save();
        }

        private static void OpenUrl(string url) {
            Process.Start(new ProcessStartInfo {
                FileName = url,
                UseShellExecute = true
            });
        }

        private void GitHubButton_Click(object sender, RoutedEventArgs e) {
            OpenUrl(Constants.GithubRepoUrl);
        }

        private bool IsDarkMode() {
            var lightTheme = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);

            return lightTheme is int value && value == 0;
        }

        private void ScrollTo(FrameworkElement element) {
            ScrollViewerSettings.ScrollToVerticalOffset(
                element.TranslatePoint(new Point(0, 0), ScrollViewerTop).Y
            );
        }

        private void NavItemGeneral_Click(object sender, RoutedEventArgs e) => ScrollTo(SectionTitleGeneral);

        private void NavItemDetection_Click(object sender, RoutedEventArgs e) => ScrollTo(SectionTitleDetection);

        private void NavItemDiscord_Click(object sender, RoutedEventArgs e) => ScrollTo(SectionTitleDiscord);

        private void NavItemScrobbling_Click(object sender, RoutedEventArgs e) => ScrollTo(SectionTitleScrobbling);
    }
}
