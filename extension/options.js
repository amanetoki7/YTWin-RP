const DEFAULT_PORT = 48271;
const portInput = document.getElementById('port');
const status = document.getElementById('status');

chrome.storage.sync.get({ port: DEFAULT_PORT }, ({ port }) => {
  portInput.value = port;
});

document.getElementById('save').addEventListener('click', async () => {
  const port = parseInt(portInput.value, 10);
  if (!(port >= 1024 && port <= 65535)) {
    status.textContent = 'Enter a port between 1024 and 65535.';
    return;
  }
  await chrome.storage.sync.set({ port });
  try {
    const res = await fetch(`http://127.0.0.1:${port}/ping`);
    const info = await res.json();
    status.textContent = `Saved. Connected to ${info.app} ${info.version}.`;
  } catch (e) {
    status.textContent = 'Saved. YTWin-RP is not reachable on this port right now.';
  }
});
