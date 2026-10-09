## How does it work?

The original AMWin-RP scraped the Apple Music window through UI Automation. YouTube runs in a browser, where walking the accessibility tree is slow and switches the browser into accessibility mode, so YTWin-RP uses a different set of sources.

### 1. What is playing: Windows media sessions (`MediaSessionReader.cs`)

Chrome, Edge and Firefox forward the page's [Media Session](https://developer.mozilla.org/docs/Web/API/Media_Session_API) metadata to the Windows System Media Transport Controls. YouTube sets the title and the channel/artist, the playback state and (on play, pause and seek) the position and duration. `Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager` exposes all of that to any app.

Observed values:

| Browser / site | `SourceAppUserModelId` | Title | Artist | Album |
| --- | --- | --- | --- | --- |
| Chrome, youtube.com | `Chrome` | video title | channel or credited artist (e.g. `YOASOBI` for a video on the *Ayase / YOASOBI* channel) | empty |
| Chrome, music.youtube.com | `Chrome` | song title | artist | album |

Position is only updated by the browser when it jumps, so the reader extrapolates from `TimelineProperties.LastUpdatedTime` while the status is *Playing*.

The media session does not say which website is playing. A session is accepted when
- a visible browser window has the title `Video title - YouTube - Browser` or `... - YouTube Music ...` (`BrowserWindows.cs`, plain Win32 `EnumWindows`; a leading `(3) ` notification count is ignored), or
- the app ID looks like a browser (`Constants.BrowserAppUserModelIdHints`) and a YouTube / YouTube Music search finds the title. Until one of these succeeds the item is *unconfirmed* and nothing is sent to Discord.

### 2. Which video: YouTube's InnerTube API (`InnerTubeClient.cs`, `YouTubeResolver.cs`)

The website's own JSON API (`https://www.youtube.com/youtubei/v1/...`) is used for metadata only; nothing is streamed and no API key is required. Requests send the `WEB` (youtube.com) or `WEB_REMIX` (music.youtube.com) client context with the user's `hl`/`gl` so titles match what the browser shows.

- `search` on youtube.com: `videoRenderer` entries give `videoId`, title, owner, length and thumbnails. Candidates whose title equals the media-session title (after NFKC normalisation) are verified through the player endpoint below. Auto-generated "Topic" uploads do not appear here.
- `search` on music.youtube.com with the *Songs* filter (`params=EgWKAQIIAWoKEAoQAxAEEAkQBQ%3D%3D`): `musicResponsiveListItemRenderer` entries give the song, artist, album, length, `musicVideoType` and square art. This is how Topic uploads and YouTube Music web playback are identified.
- `player` on music.youtube.com: playback is refused (`UNPLAYABLE`), but `videoDetails` is returned with `musicVideoType`, the YouTube Music title/author, thumbnails, `microformat.category` and `linkAlternates[].title` (the youtube.com title). One request answers "is it music?" and "what should be shown?".
- `next` on music.youtube.com: the "Up next" panel, whose byline runs are tagged `MUSIC_PAGE_TYPE_ARTIST` / `MUSIC_PAGE_TYPE_ALBUM`; this is where the album name comes from.
- `player` on youtube.com: fallback when YouTube Music cannot be reached (`microformat.playerMicroformatRenderer.category`).

Results are cached in memory and in `%localappdata%\YTWin-RichPresence\ResolveCache.json`; failures are cached for 10 minutes.

### 3. Video or music (`YouTubeResolver.Classify`)

| `musicVideoType` | Verdict |
| --- | --- |
| `MUSIC_VIDEO_TYPE_ATV`, `OFFICIAL_SOURCE_MUSIC`, `PRIVATELY_OWNED_TRACK`, `OMV` | music |
| `MUSIC_VIDEO_TYPE_UGC` | music if the YouTube category is *Music*, otherwise video |
| `MUSIC_VIDEO_TYPE_PODCAST_EPISODE` | video |
| absent | video (the category is only trusted when YouTube Music itself was unreachable) |

Anything played on music.youtube.com is music.

### 4. Optional extension (`ExtensionBridge.cs`, `extension/`)

An MV3 extension posts `{site, videoId, title, artist, album, duration, currentTime, paused}` from YouTube tabs to `http://127.0.0.1:48271/nowplaying` (an `HttpListener` that non-admin users can bind). Reports younger than 10 seconds take precedence over the media session; the video ID skips the search step.

### 5. Discord (`YouTubeDiscordClient.cs`)

Music is sent as `ActivityType.Listening` (song / artist / album art), video as `ActivityType.Watching` (title / channel / thumbnail). Thumbnails are plain URLs. The small icon next to the thumbnail is the application's own icon, read through Discord's public `oauth2/applications/{id}/rpc` endpoint (`DiscordAppInfo.cs`); Rich Presence assets named `youtube`, `youtubemusic` or `pause` uploaded to the application take precedence. Fields are trimmed to 128 UTF-8 bytes (Japanese titles exceed this quickly). Because Discord shows the application's name, a separate application ID can be configured for music so it reads "Listening to YouTube Music".
