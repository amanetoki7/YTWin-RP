# YTWin-RP Connector (optional browser extension)

YTWin-RP works without this extension. Install it only if you want:

- exact video IDs for **unlisted** videos (they cannot be found by searching YouTube),
- tracking of YouTube tabs that are **not in the foreground** without relying on a YouTube search,
- **precise timestamps** straight from the player.

The extension only talks to `http://127.0.0.1:<port>/` on your own PC. Nothing is sent anywhere else.

## Install (Chrome, Edge, Brave, Vivaldi, Opera, ...)

1. Open `chrome://extensions` (or `edge://extensions`) and enable **Developer mode**.
2. Click **Load unpacked** and pick this `extension` folder. It is shipped next to `YTWin-RichPresence.exe`; the settings window has a button that opens it.
3. In the YTWin-RP settings, make sure **Browser extension** is switched on. The status line shows when reports arrive.

## Install (Firefox)

Open `about:debugging#/runtime/this-firefox`, choose **Load Temporary Add-on...** and pick `manifest.json`. Temporary add-ons are removed when Firefox exits.

## Port

The app listens on `127.0.0.1:48271` by default. If you change the port in the YTWin-RP settings, change it on the extension's options page as well.
