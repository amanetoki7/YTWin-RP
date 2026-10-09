// YTWin-RP Connector: reports what this YouTube / YouTube Music tab is playing.
// Runs only on youtube.com and music.youtube.com; the data goes to background.js,
// which forwards it to the YTWin-RP app on 127.0.0.1.
(() => {
  const SITE = location.hostname === 'music.youtube.com' ? 'youtube_music' : 'youtube';
  const ID_RE = /^[A-Za-z0-9_-]{11}$/;
  let lastKey = '';
  let lastSentAt = 0;

  function getVideoId() {
    try {
      const url = new URL(location.href);
      const v = url.searchParams.get('v');
      if (v && ID_RE.test(v)) return v;
      const m = url.pathname.match(/^\/(?:shorts|embed|live|v)\/([A-Za-z0-9_-]{11})/);
      return m ? m[1] : null;
    } catch (e) {
      return null;
    }
  }

  function text(selector) {
    const el = document.querySelector(selector);
    return el && el.textContent ? el.textContent.trim() : '';
  }

  function collect() {
    const videoId = getVideoId();
    const video = document.querySelector('video.html5-main-video') || document.querySelector('video');
    if (!videoId || !video) return null;

    let title = '';
    let artist = '';
    let album = '';
    const metadata = navigator.mediaSession && navigator.mediaSession.metadata;
    if (metadata && metadata.title) {
      title = metadata.title;
      artist = metadata.artist || '';
      album = metadata.album || '';
    }
    if (!title) {
      if (SITE === 'youtube_music') {
        title = text('ytmusic-player-bar .title');
        const links = document.querySelectorAll('ytmusic-player-bar .byline a');
        artist = links[0] ? links[0].textContent.trim() : text('ytmusic-player-bar .byline');
        album = links[1] ? links[1].textContent.trim() : '';
      } else {
        title = text('ytd-watch-metadata h1') || document.title.replace(/\s-\sYouTube$/, '');
        artist = text('ytd-video-owner-renderer ytd-channel-name a') || text('#owner #channel-name a');
      }
    }

    return {
      site: SITE,
      videoId,
      title,
      artist,
      album,
      url: location.href,
      duration: Number.isFinite(video.duration) ? video.duration : null,
      currentTime: video.currentTime,
      paused: video.paused || video.ended,
      rate: video.playbackRate || 1,
    };
  }

  function tick() {
    const report = collect();
    if (!report) {
      lastKey = '';
      return;
    }
    const now = Date.now();
    const key = [report.videoId, report.paused, report.title].join('|');
    // send immediately on change, otherwise as a heartbeat
    const interval = report.paused ? 8000 : 4000;
    if (key !== lastKey || now - lastSentAt >= interval) {
      lastKey = key;
      lastSentAt = now;
      try {
        chrome.runtime.sendMessage({ type: 'nowplaying', report });
      } catch (e) {
        // the extension was reloaded or disabled; the page picks up the new worker after a reload
      }
    }
  }

  setInterval(tick, 1000);
  document.addEventListener('yt-navigate-finish', () => setTimeout(tick, 500));
})();
