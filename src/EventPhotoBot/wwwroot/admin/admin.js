(() => {
  'use strict';

  const POLL_MS = 2000;
  let etag = null;
  let manifest = null;
  const listeners = [];

  window.Admin = {
    onManifest(callback) { listeners.push(callback); if (manifest) callback(manifest); },
    get manifest() { return manifest; },
    api,
    imageUrl: (id, size) => `/img/${encodeURIComponent(id)}/${size}`,
  };

  async function api(method, path, body) {
    const options = { method, headers: {} };
    if (body instanceof FormData) {
      options.body = body;
    } else if (body !== undefined) {
      options.headers['Content-Type'] = 'application/json';
      options.body = JSON.stringify(body);
    }

    const response = await fetch(path, options);
    if (response.status === 401) { location.href = '/login'; return null; }
    if (!response.ok) {
      const detail = await response.text();
      alert(`That did not work (${response.status}). ${detail}`);
      return null;
    }
    // Refresh straight away rather than waiting for the next poll.
    await pollOnce(true);
    return response;
  }

  async function pollOnce(force) {
    const headers = force || !etag ? {} : { 'If-None-Match': etag };
    const response = await fetch('/api/manifest', { headers, cache: 'no-store' });
    if (response.status === 304) return;
    if (!response.ok) return;

    etag = response.headers.get('ETag');
    manifest = await response.json();

    for (const badge of document.querySelectorAll('.badge')) {
      badge.textContent = manifest.pendingCount || '';
      badge.dataset.count = manifest.pendingCount;
    }
    renderTakeoverBanner();
    // The manifest's generation advances on every settings change too (see
    // AdminApiTests), so a fresh (non-304) manifest is exactly the signal that
    // pairing mode might have flipped. Piggybacking here keeps the pairing
    // check free the rest of the time, without adding a field to the manifest
    // payload itself.
    await refreshPairingBanner();
    listeners.forEach(fn => fn(manifest));
  }

  function renderTakeoverBanner() {
    const banner = document.getElementById('takeover-banner');
    if (!banner) return;

    const takeover = manifest.takeover;
    const active = takeover && (!takeover.until || new Date(takeover.until) > new Date());
    banner.hidden = !active;
    if (!active) return;

    const until = takeover.until
      ? `until ${new Date(takeover.until).toLocaleTimeString()}`
      : 'until you clear it';
    banner.innerHTML =
      `<strong>One image is holding the screen</strong><span class="muted">${until}</span>`;

    const clear = document.createElement('button');
    clear.className = 'danger';
    clear.textContent = 'Clear takeover';
    clear.onclick = () => api('DELETE', '/api/takeover');
    banner.appendChild(clear);
  }

  async function refreshPairingBanner() {
    const banner = document.getElementById('pairing-banner');
    if (!banner) return;
    try {
      const response = await fetch('/api/settings', { cache: 'no-store' });
      if (!response.ok) return;
      const settings = await response.json();
      banner.hidden = !settings.pairingMode;
    } catch {
      // Leave the banner as it was rather than guessing.
    }
  }

  async function loop() {
    try { await pollOnce(false); } catch { /* keep polling */ }
    setTimeout(loop, POLL_MS);
  }

  for (const link of document.querySelectorAll('nav a')) {
    if (link.getAttribute('href') === location.pathname) link.classList.add('active');
  }

  loop();
})();
