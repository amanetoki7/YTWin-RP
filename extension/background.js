// Forwards reports from content.js to the YTWin-RP app listening on 127.0.0.1.
const DEFAULT_PORT = 48271;

async function getPort() {
  try {
    const { port } = await chrome.storage.sync.get({ port: DEFAULT_PORT });
    const p = parseInt(port, 10);
    return p >= 1024 && p <= 65535 ? p : DEFAULT_PORT;
  } catch (e) {
    return DEFAULT_PORT;
  }
}

async function post(path, body) {
  const port = await getPort();
  try {
    await fetch(`http://127.0.0.1:${port}${path}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
  } catch (e) {
    // YTWin-RP is not running; nothing to do
  }
}

chrome.runtime.onMessage.addListener((message, sender) => {
  if (message && message.type === 'nowplaying' && message.report) {
    post('/nowplaying', { ...message.report, tabId: sender.tab ? sender.tab.id : -1 });
  }
});

chrome.tabs.onRemoved.addListener((tabId) => {
  post('/closed', { tabId });
});
