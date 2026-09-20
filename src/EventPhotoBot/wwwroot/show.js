(() => {
  'use strict';

  const POLL_MS = 2000;
  const POLL_BACKOFF_MS = 10000;
  const FAILURES_BEFORE_BACKOFF = 3;
  const POLL_TIMEOUT_MS = 8000;

  const stage = document.getElementById('stage');
  const slots = [...stage.querySelectorAll('.slide')];
  const captionEl = document.getElementById('caption');
  const captionSender = document.getElementById('caption-sender');
  const captionText = document.getElementById('caption-text');
  const emptyEl = document.getElementById('empty');
  const offlineEl = document.getElementById('offline');

  let manifest = null;
  let etag = null;
  let playlist = [];
  let cursor = 0;
  let activeSlot = 0;
  let advanceTimer = null;
  let failures = 0;
  let seenIds = new Set();
  let currentImageId = null;

  const imageUrl = id => `/img/${encodeURIComponent(id)}/display`;

  // ---- polling -------------------------------------------------------------

  async function poll() {
    // A dropped or stalled connection at the venue must not hang the poll
    // loop forever: without a bound here, a request that never resolves
    // would never increment `failures`, so the offline indicator and the
    // backoff would never kick in.
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), POLL_TIMEOUT_MS);

    try {
      const headers = etag ? { 'If-None-Match': etag } : {};
      const response = await fetch('/api/manifest', {
        headers, cache: 'no-store', signal: controller.signal,
      });

      if (response.status === 304) { onPollSuccess(); return; }
      if (!response.ok) throw new Error(`manifest ${response.status}`);

      etag = response.headers.get('ETag');
      applyManifest(await response.json());
      onPollSuccess();
    } catch {
      failures++;
      if (failures >= FAILURES_BEFORE_BACKOFF) offlineEl.hidden = false;
    } finally {
      clearTimeout(timeout);
      // The last manifest stays in memory, so a failed poll changes nothing
      // on screen. Recovery needs no special handling: the next success carries
      // the current state.
      setTimeout(poll, failures >= FAILURES_BEFORE_BACKOFF ? POLL_BACKOFF_MS : POLL_MS);
    }
  }

  function onPollSuccess() {
    failures = 0;
    offlineEl.hidden = true;
  }

  // ---- reconciliation ------------------------------------------------------

  function applyManifest(next) {
    const first = manifest === null;
    manifest = next;

    document.documentElement.style.setProperty(
      '--transition', `${next.settings.transitionMs}ms`);

    const incoming = next.images;
    const incomingIds = new Set(incoming.map(i => i.id));

    // Images that vanished disappear after the current slide, not mid-slide.
    playlist = playlist.filter(i => incomingIds.has(i.id));

    const known = new Set(playlist.map(i => i.id));
    const fresh = incoming.filter(i => !known.has(i.id));

    if (first) {
      playlist = [...incoming];
    } else if (next.settings.newestFirstBoost) {
      // Newly approved images jump in within a slide or two, then rejoin the pool.
      const insertAt = Math.min(playlist.length, cursor + 1);
      const brandNew = fresh.filter(i => !seenIds.has(i.id));
      const rest = fresh.filter(i => seenIds.has(i.id));
      playlist.splice(insertAt, 0, ...brandNew);
      playlist.push(...rest);
    } else {
      playlist.push(...fresh);
    }

    incoming.forEach(i => seenIds.add(i.id));

    if (cursor >= playlist.length) cursor = 0;
    emptyEl.hidden = playlist.length > 0 || takeoverImage() !== null;

    if (first) advance();
  }

  function takeoverImage() {
    if (!manifest || !manifest.takeover) return null;
    const { id, until } = manifest.takeover;
    // Expiry is evaluated here: it triggers no write, so the ETag would not change.
    if (until && new Date(until) <= new Date()) return null;
    return manifest.images.find(i => i.id === id)
        ?? { id, width: 0, height: 0, caption: null, senderName: null };
  }

  // ---- rendering -----------------------------------------------------------

  function render(image) {
    // A takeover re-checks every 2s for expiry (see advance()) and would
    // otherwise re-render, and briefly cross-fade, the same still image on
    // every check. Skipping a no-op render keeps a long takeover flicker-free.
    if (image.id === currentImageId) return;
    currentImageId = image.id;

    const slot = slots[activeSlot];
    const next = slots[1 - activeSlot];
    const url = imageUrl(image.id);

    next.innerHTML =
      `<div class="backdrop" style="background-image:url('${url}')"></div>` +
      `<img src="${url}" alt="">`;

    next.classList.add('visible');
    slot.classList.remove('visible');
    activeSlot = 1 - activeSlot;

    const hasCaption = Boolean(image.caption || image.senderName);
    captionEl.hidden = !hasCaption;
    captionSender.textContent = image.senderName ?? '';
    captionText.textContent = image.caption ?? '';
  }

  function preload(count) {
    for (let i = 1; i <= count; i++) {
      const upcoming = playlist[(cursor + i) % playlist.length];
      if (upcoming) new Image().src = imageUrl(upcoming.id);
    }
  }

  function advance() {
    clearTimeout(advanceTimer);

    const takeover = takeoverImage();
    if (takeover) {
      render(takeover);
      // Re-check often so the screen restores within a slide of the takeover clearing.
      advanceTimer = setTimeout(advance, 2000);
      return;
    }

    if (playlist.length === 0) {
      emptyEl.hidden = false;
      // A caption left over from the last-shown image must not linger behind
      // the holding card once the playlist has genuinely run dry.
      captionEl.hidden = true;
      advanceTimer = setTimeout(advance, 2000);
      return;
    }

    emptyEl.hidden = true;
    render(playlist[cursor % playlist.length]);
    cursor = (cursor + 1) % playlist.length;
    preload(2);

    const seconds = manifest?.settings?.slideSeconds ?? 8;
    advanceTimer = setTimeout(advance, seconds * 1000);
  }

  // ---- keep the display awake ---------------------------------------------

  async function keepAwake() {
    try {
      if ('wakeLock' in navigator) {
        let lock = await navigator.wakeLock.request('screen');
        document.addEventListener('visibilitychange', async () => {
          if (document.visibilityState === 'visible') {
            try {
              lock = await navigator.wakeLock.request('screen');
            } catch {
              // Reacquiring on refocus can fail (e.g. permission revoked mid-event);
              // the video fallback below still guards the machine from sleeping.
            }
          }
        });
        return;
      }
    } catch { /* fall through to the video trick */ }

    const video = document.getElementById('keepawake');
    video.play().catch(() => { /* autoplay blocked; OS sleep settings are the backstop */ });
  }

  keepAwake();
  poll();
})();
