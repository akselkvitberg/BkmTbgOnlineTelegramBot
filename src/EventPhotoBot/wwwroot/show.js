(() => {
  'use strict';

  // The Ken Burns move. BASE is the scale both ends of the zoom stay at or above,
  // and DRIFT_PERCENT the sideways travel; BASE - 1 must stay above twice
  // DRIFT_PERCENT/100 or the drift pulls an edge of the frame into view at the small
  // end of a zoom-out (see the .slide.ken-burns rule in show.css). 1.02 is the bare
  // minimum for 0.9% of drift and leaves under a pixel of cover on a 1024px screen -
  // 1.03 keeps a margin that survives sub-pixel rounding.
  const KEN_BURNS_BASE = 1.03;
  const KEN_BURNS_ZOOM = 0.08;
  const KEN_BURNS_DRIFT_PERCENT = 0.9;

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
  const emptyEventNameEl = document.getElementById('empty-event-name');
  const offlineEl = document.getElementById('offline');
  const captionHintEl = document.getElementById('caption-hint');
  const joinEl = document.getElementById('join');
  const joinQrEl = document.getElementById('join-qr');
  const joinHandleEl = document.getElementById('join-handle');
  const joinBadgeEl = document.getElementById('join-badge');
  const emptyInviteEl = document.getElementById('empty-invite');
  const emptyQuietEl = document.getElementById('empty-quiet');

  let manifest = null;
  let etag = null;
  let playlist = [];
  let cursor = 0;
  let lastShownIndex = 0; // the index actually rendered by the most recent advance(),
                           // before cursor's post-render increment - see resumePosition()
  let activeSlot = 0;
  let advanceTimer = null;
  let failures = 0;
  let seenIds = new Set();
  let currentImageId = null;
  let currentImage = null; // last image passed to render(), for instant caption toggling
  let joinReady = false;   // the QR src is set once, not on every two-second poll

  const imageUrl = id => `/img/${encodeURIComponent(id)}/display`;

  // ---- caption visibility (a local, per-machine preference, not server state) ----

  const CAPTIONS_KEY = 'eventPhotoBot.showCaptions';

  function loadCaptionsEnabled() {
    try {
      const stored = localStorage.getItem(CAPTIONS_KEY);
      return stored === null ? false : stored === '1';
    } catch {
      // A locked-down browser profile can throw on any localStorage access.
      // The overlay stays at its default (off) there; the C key still toggles
      // it for the session, the choice just won't survive a reload.
      return false;
    }
  }

  function saveCaptionsEnabled(value) {
    try {
      localStorage.setItem(CAPTIONS_KEY, value ? '1' : '0');
    } catch {
      // Best-effort persistence only; the toggle still works for this
      // session even where it can't be saved.
    }
  }

  let captionsEnabled = loadCaptionsEnabled();

  function updateCaptionOverlay() {
    if (!currentImage) { captionEl.hidden = true; return; }
    const hasCaption = Boolean(currentImage.caption || currentImage.senderName);
    captionEl.hidden = !hasCaption || !captionsEnabled;
    captionSender.textContent = currentImage.senderName ?? '';
    captionText.textContent = currentImage.caption ?? '';
  }

  // ---- fullscreen ----------------------------------------------------------

  /// F11 covers a desktop browser, but not a tablet or a kiosk shell without a
  /// function row, and the page has no visible chrome to click (the cursor is
  /// hidden). The Fullscreen API needs a user gesture, which a keypress is.
  function toggleFullscreen() {
    const root = document.documentElement;
    const request = root.requestFullscreen ?? root.webkitRequestFullscreen;
    const exit = document.exitFullscreen ?? document.webkitExitFullscreen;
    const active = document.fullscreenElement ?? document.webkitFullscreenElement;

    // Rejects if the browser refuses (an unattended gesture, a disallowed
    // iframe); nothing to recover, and an unhandled rejection is noise.
    Promise.resolve(active ? exit?.call(document) : request?.call(root)).catch(() => {});
  }

  document.addEventListener('keydown', event => {
    // Leave browser shortcuts (Ctrl/Cmd+F, Alt+C) alone.
    if (event.ctrlKey || event.metaKey || event.altKey) return;

    const key = event.key?.toLowerCase();

    if (key === 'c') {
      captionsEnabled = !captionsEnabled;
      saveCaptionsEnabled(captionsEnabled);
      updateCaptionOverlay();
    } else if (key === 'f') {
      toggleFullscreen();
    }
  });

  // The shortcut has no other affordance, so give it a few seconds of
  // on-screen visibility once, at load, for whoever is minding the machine.
  setTimeout(() => { captionHintEl.hidden = true; }, 8000);

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
      // A 401 means the session has expired or been revoked - the manifest will
      // never succeed again until someone signs back in. Without this branch a 401
      // fell into the generic failure counter below, and the projector would sit
      // showing its last frame under a permanent "Reconnecting..." badge instead of
      // recovering, exactly like admin.js already does for its own polling.
      if (response.status === 401) { location.href = '/login'; return; }
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

  // ---- join QR -------------------------------------------------------------

  /// The QR is fixed for the life of the instance, so its src is set once rather
  /// than reassigned on every two-second poll. joinUrl is null when the bot's
  /// username could not be resolved at startup, in which case no QR is shown at
  /// all — the slideshow is not worth failing over a missing affordance.
  ///
  /// Visibility, unlike the src, is decided on every poll: showJoinInvite can be
  /// turned off mid-event, and the screen it is turned off for is one that must
  /// stop asking the room for photos within a poll, not at the next page load.
  /// When it is off nothing on screen invites anyone to send anything — no QR,
  /// no handle, and a holding card that says only that photos are coming.
  function applyJoin(next) {
    const joinUrl = next.settings.joinUrl;
    const inviting = next.settings.showJoinInvite ?? true;

    // A hidden <img> still fetches its src, so the QR is armed only once the
    // screen is actually inviting anyone — turning the setting on mid-event
    // arms it on that poll instead.
    if (joinUrl && inviting && !joinReady) {
      joinQrEl.src = '/api/join-qr.svg';
      joinBadgeEl.src = '/api/join-qr.svg';
      joinHandleEl.textContent = handleFrom(joinUrl);
      joinReady = true;
    }

    joinEl.hidden = !joinReady || !inviting;
    // Large in the empty state, small in the corner once there are photos to show.
    joinBadgeEl.hidden = !joinReady || !inviting || next.images.length === 0;
    emptyInviteEl.hidden = !inviting;
    emptyQuietEl.hidden = inviting;
  }

  function handleFrom(joinUrl) {
    try {
      return '@' + new URL(joinUrl).pathname.replace(/^\//, '');
    } catch {
      return '';
    }
  }

  // ---- reconciliation ------------------------------------------------------

  function applyManifest(next) {
    const first = manifest === null;
    manifest = next;

    document.documentElement.style.setProperty(
      '--transition', `${next.settings.transitionMs}ms`);

    const eventName = next.settings.eventName || '';
    emptyEventNameEl.textContent = eventName;
    emptyEventNameEl.hidden = eventName.length === 0;

    applyJoin(next);

    const incoming = next.images;

    if (first) {
      playlist = [...incoming];
      cursor = 0;
    } else {
      const result = reconcilePlaylist(incoming, next.settings.newestFirstBoost);
      playlist = result.playlist;
      cursor = result.cursor;
    }

    incoming.forEach(i => seenIds.add(i.id));

    // Images that vanished (hidden/rejected/deleted) are simply absent from
    // `incoming`, so they drop out of the rebuilt playlist here - but nothing above
    // forces a re-render, so whatever is already on screen stays there until the
    // advance() timer already running fires next. They disappear after the current
    // slide, never mid-slide.
    emptyEl.hidden = playlist.length > 0 || takeoverImage() !== null;

    if (first) advance();
  }

  /// <summary>
  /// The server's manifest array is authoritative for both order and multiplicity:
  /// a recurring pin sits at every Nth position, exactly as many times as
  /// ManifestBuilder placed it, and settings.order (shuffle vs newest-first) is
  /// already baked into the array's order. Reconciling by id-set instead - filtering
  /// the old playlist down to ids still present, then appending ids not already
  /// known - can never move an id the client has already seen and can never show
  /// more than one copy of it: pinning an image already in rotation had no visible
  /// effect, and an image that became approved while already pinned entered as N
  /// adjacent duplicates instead of one every N slides.
  ///
  /// Rebuilding fully from `incoming` on every change fixes both, and also makes
  /// settings.order take effect immediately instead of only on the next page load.
  /// The one thing still handled here, not by the server, is "newest first boost":
  /// a genuinely new *ordinary* (non-recurring) image is moved from its natural
  /// server position to just after the slide currently on screen, so it jumps the
  /// queue by a slide or two rather than waiting its turn. Recurring pins are
  /// excluded from that boost and left at every occurrence the server gave them -
  /// boosting only the nearest one and dropping the rest is exactly the N-duplicate
  /// bug this replaces.
  /// </summary>
  function reconcilePlaylist(incoming, newestFirstBoost) {
    const brandNewIds = newestFirstBoost
      ? new Set(incoming.filter(i => !i.recurring && !seenIds.has(i.id)).map(i => i.id))
      : new Set();

    const rebuilt = incoming.filter(i => !brandNewIds.has(i.id));
    const insertAt = Math.min(resumePosition(rebuilt), rebuilt.length);

    if (brandNewIds.size > 0) {
      rebuilt.splice(insertAt, 0, ...incoming.filter(i => brandNewIds.has(i.id)));
    }

    return { playlist: rebuilt, cursor: insertAt >= rebuilt.length ? 0 : insertAt };
  }

  /// <summary>
  /// The index, within `list`, that playback should resume from: just after the
  /// slide currently on screen. Matches against `lastShownIndex` - the index
  /// advance() actually rendered - not `cursor`, which by the time this runs has
  /// already been incremented past it (advance() renders playlist[cursor], then
  /// increments). Using `cursor` here made "at or after cursor" search for an
  /// occurrence *after* the one on screen, which for a unique id fails and falls
  /// back to occurrences[0] by accident, but for a recurring pin - an id repeated
  /// several times in the playlist - finds the *next* copy and jumps the cursor
  /// there, silently skipping every ordinary image in between on every manifest
  /// change. `cursor - 1` is not a safe substitute: once cursor has wrapped to 0
  /// (the shown image was the last in the array), cursor - 1 is -1, which would
  /// match the first occurrence and starve the head of the playlist instead.
  ///
  /// Falls back to just after `lastShownIndex`, clamped to the new length, when
  /// the current image is no longer present at all (hidden, rejected or deleted).
  /// Returns 0 when nothing has been shown yet (the playlist started empty).
  /// </summary>
  function resumePosition(list) {
    if (currentImageId === null) return 0;

    const occurrences = [];
    list.forEach((image, index) => { if (image.id === currentImageId) occurrences.push(index); });
    if (occurrences.length === 0) return Math.min(lastShownIndex + 1, list.length);

    const atOrAfter = occurrences.find(index => index >= lastShownIndex);
    return (atOrAfter !== undefined ? atOrAfter : occurrences[0]) + 1;
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
    // Caption/sender text is re-applied on every call, even when the image
    // itself hasn't changed: a takeover can run indefinitely, and an
    // organiser correcting that image's caption or sender mid-display must
    // reach the screen without waiting for the takeover to end.
    currentImage = image;
    updateCaptionOverlay();

    // A takeover re-checks every 2s for expiry (see advance()) and would
    // otherwise re-swap and re-transition the same still image on every
    // check. Skipping the DOM swap (only) when the image is unchanged keeps
    // a long takeover flicker-free without going stale.
    if (image.id === currentImageId) return;
    currentImageId = image.id;

    const slot = slots[activeSlot];
    const next = slots[1 - activeSlot];
    const url = imageUrl(image.id);

    // Before the markup, not after: the properties must be in place by the time the
    // new <img> exists, or its animation starts on the previous slide's values.
    applyKenBurns(next);

    next.innerHTML =
      `<div class="backdrop" style="background-image:url('${url}')"></div>` +
      `<img src="${url}" alt="">`;

    next.classList.add('visible');
    slot.classList.remove('visible');
    activeSlot = 1 - activeSlot;
  }

  /// Arms one slide's zoom. The direction is drawn per slide - in or out, toward one
  /// of four corners - so a run of photos doesn't drift in lockstep, which is what
  /// makes the effect look mechanical. Turning the setting off mid-event lands on the
  /// next slide; the one on screen keeps the move it started with.
  function applyKenBurns(slot) {
    const enabled = manifest?.settings?.kenBurns ?? true;
    slot.classList.toggle('ken-burns', enabled);
    if (!enabled) return;

    const near = KEN_BURNS_BASE;
    const far = KEN_BURNS_BASE + KEN_BURNS_ZOOM;
    const zoomIn = Math.random() < 0.5;
    const sign = () => (Math.random() < 0.5 ? -1 : 1);
    const seconds = manifest?.settings?.slideSeconds ?? 8;
    const transition = manifest?.settings?.transitionMs ?? 800;

    slot.style.setProperty('--kb-from', zoomIn ? near : far);
    slot.style.setProperty('--kb-to', zoomIn ? far : near);
    slot.style.setProperty('--kb-x', `${sign() * KEN_BURNS_DRIFT_PERCENT}%`);
    slot.style.setProperty('--kb-y', `${sign() * KEN_BURNS_DRIFT_PERCENT}%`);
    slot.style.setProperty('--kb-duration', `${seconds * 1000 + transition}ms`);
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
      currentImage = null;
      currentImageId = null;
      updateCaptionOverlay();
      advanceTimer = setTimeout(advance, 2000);
      return;
    }

    emptyEl.hidden = true;
    lastShownIndex = cursor % playlist.length;
    render(playlist[lastShownIndex]);
    cursor = (lastShownIndex + 1) % playlist.length;
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
