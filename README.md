# YTWin-RP
([日本語](README-JA.md))

A Discord Rich Presence client for **YouTube and YouTube Music playing in your browser** on Windows.  
Shows *Watching* for videos and *Listening to* (with song title, artist and album art) for music, and can scrobble music to Last.FM and ListenBrainz.

YTWin-RP is a fork of [AMWin-RP](https://github.com/PKBeam/AMWin-RP) (Apple Music) rebuilt for YouTube.

## How it works (no browser extension needed)

1. Browsers publish what they play to the Windows media controls (the same data the media overlay shows). YTWin-RP reads the title, channel, play/pause state and playback position from there.
2. It confirms the session is YouTube from the browser window title (`Video title - YouTube - Google Chrome`) or, for background tabs, by searching YouTube for the title and channel. That search also yields the video ID, which gives the thumbnail and the "Watch on YouTube" button.
3. **Video or music?** YTWin-RP asks YouTube Music whether it knows the same video ID. YouTube Music tags its catalogue (`MUSIC_VIDEO_TYPE_ATV` for songs, `OMV` for official music videos, `UGC` for user uploads in the Music category); videos outside the catalogue carry no tag and are treated as videos. For music, the song title, artist, album and square album art come from YouTube Music as well.

Chrome, Edge, Firefox, Brave, Vivaldi, Opera and installed YouTube PWAs are recognised. Other apps publishing media (Spotify desktop, etc.) are ignored.

### Optional browser extension

An unpacked extension in the [`extension`](extension/) folder sends the exact video ID and playback position to the app over `127.0.0.1`. It is only needed for unlisted videos, YouTube in a background tab when the search fallback cannot identify it, or more precise timestamps. See [extension/README.md](extension/README.md).

## Installation
YTWin-RP requires Windows 11 24H2 or later and a Discord desktop client.

Builds can be found [here](https://github.com/amanetoki7/YTWin-RP/releases). Pick x64 or ARM64; the `NoRuntime` variant needs the [.NET 10 desktop runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) installed.

### Discord application

Discord shows the *name of the application* that sets the presence ("Watching **YouTube**", "Listening to **YouTube Music**"). Both applications are built into YTWin-RP, so there is nothing to set up. Thumbnails are sent as URLs; the small icon next to them is the application's icon from the Developer Portal (Rich Presence assets named `youtube`, `youtubemusic` and `pause` override it if uploaded). Builds without built-in IDs show Application ID fields in the settings instead.

## Usage
- Open the .exe to start the app. It runs in the system tray; double-click the icon for settings, right-click → Exit to quit.
- Play something on youtube.com or music.youtube.com in your browser. Rich Presence appears a few seconds later (while playing; "Show when paused" is optional).
- The *Detection* page of the settings shows what is currently detected and how.

Settings worth knowing:
- **Detect music using YouTube Music** – turn off to show everything as a video.
- **Only track YouTube while its tab is in the foreground** – stricter mode for people who also use other web players (e.g. Spotify Web) in the same browser.
- **Show presence for videos / music** – hide one of the two.

## Scrobbling
Only items detected as music are scrobbled (there is an option to scrobble everything). Song, artist and album come from YouTube Music; otherwise the video title is cleaned up (`Artist - Title (Official Video)` → *Artist* / *Title*).

The scrobbler has no offline queue; listens while offline are lost.

### Last.FM
You need your own API key and secret from https://www.last.fm/api ("Get an API Account"). Enter them with your Last.FM username and password in the settings. The password is stored in the Windows Credential Manager.

### ListenBrainz
Enter your user token in the settings.

## Building
```
dotnet build YTWin-RichPresence.sln -c Release
```
Before publishing a build, create a Discord application named `YouTube` (and optionally `YouTube Music`) in the Developer Portal and put the Application IDs into `DefaultDiscordClientID` / `DefaultDiscordClientIDMusic` in `YTWin-RichPresence/Constants.cs`. Without them, users have to enter an ID themselves.

After editing `Properties/Localisation.resx` with the dotnet CLI only, run `GenerateLocalisation.ps1` to regenerate the designer file (Visual Studio does this automatically).

## Reporting bugs
Attach the relevant `.log` files from `%localappdata%\YTWin-RichPresence`. They show which media sessions were seen, how the video was identified and what was sent to Discord.

Before posting, please check that Activity Status is enabled in Discord (Settings > Activity Privacy > Share your activity status).
