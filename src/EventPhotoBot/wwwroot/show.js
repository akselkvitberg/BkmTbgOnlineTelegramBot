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

  /// <summary>
  /// How each layout arranges the stage. The server sends a name; everything the
  /// screen actually does with it is decided here, in one table, rather than being
  /// spread across the stylesheet and half a dozen branches.
  ///
  /// `slots` is both how many photos are on the wall at once and the ceiling on how
  /// many <img> elements the layout may ever hold. Nothing below mounts an element
  /// per slide: an evening is eight hours and a thousand slides, and a screen whose
  /// node count tracks that is a projector that gets slower as the party goes on.
  ///
  /// The counts are all small on purpose. Twelve is the largest, which on a 1080p
  /// projector is already about 480x270 per photo before the gap - roughly the point
  /// at which a face stops being a face from five metres.
  ///
  /// `enters` is whether a photo taking a slot's turn animates in. The filmstrip is
  /// the one that says no: its whole band shifts by one frame per slide, so every
  /// frame's contents change at once and fading them all in would destroy the
  /// illusion that the strip is simply sliding.
  ///
  /// `thumbs` sends a layout to /thumb rather than /display. Only the collage does.
  /// Twelve display copies (2560px on the long edge) is something like 100 megapixels
  /// of decoded bitmap held at once, which is real memory on the mini PC under the
  /// projector; at a twelfth of the screen the 480px thumb is the right size anyway.
  /// The mosaic's largest tile is half the screen, where a thumb would visibly soften,
  /// so six display copies is the trade there.
  /// </summary>
  const LAYOUTS = {
    single:    { slots: 1,  cell: null,    window: false, caption: false, enters: false, tilt: false, thumbs: false },
    mosaic:    { slots: 6,  cell: 'tile',  window: false, caption: false, enters: true,  tilt: false, thumbs: false },
    polaroid:  { slots: 3,  cell: 'print', window: true,  caption: true,  enters: true,  tilt: true,  thumbs: false },
    filmstrip: { slots: 5,  cell: 'frame', window: false, caption: false, enters: false, tilt: false, thumbs: false },
    collage:   { slots: 12, cell: 'cell',  window: false, caption: false, enters: true,  tilt: false, thumbs: true  },
    split:     { slots: 2,  cell: 'pane',  window: true,  caption: true,  enters: true,  tilt: false, thumbs: false },
  };

  const DEFAULT_LAYOUT = 'single';

  // How far a polaroid print is turned on the pile, in whole degrees: the range is
  // TILT_RANGE values centred on zero, so ±4 at nine. Further and a print reads as
  // fallen over rather than as placed by hand.
  const TILT_RANGE = 9;
  const FNV_OFFSET_BASIS = 2166136261;
  const FNV_PRIME = 16777619;

  const stage = document.getElementById('stage');
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

  let stageLayout = null;  // the layout the stage's DOM is currently built for
  let cells = [];          // the stage's slot elements, in slot order
  let cellIds = [];        // which image each of those slots is holding
  let track = null;        // the filmstrip's sliding band, null in every other layout
  let slidesShown = 0;     // advances this page has rendered - see the collage

  const imageUrl = (id, thumb) =>
    `/img/${encodeURIComponent(id)}/${thumb ? 'thumb' : 'display'}`;

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

  /// The C key governs captions everywhere, but the two layouts that carry a caption
  /// per photo - the polaroid's mat and the split's pane - already have their text in
  /// the DOM, so the key only has to flip a class on the stage. Re-rendering those
  /// slots to hide a line of text would restart their entrance animations, which is a
  /// visible jolt across the whole wall for a keypress that changed nothing about
  /// which photos are up.
  function updateCaptionOverlay() {
    stage.classList.toggle('captions-off', !captionsEnabled);

    // The bar belongs to the single layout alone: one line across the bottom of a
    // six-tile mosaic names none of the six.
    if (!currentImage || stageLayout !== 'single') { captionEl.hidden = true; return; }

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
    // The polaroid and split layouts keep their captions clear of the corner badge,
    // but only while it is actually up - reserving the corner permanently would cost
    // them a slice of the screen on a machine that never shows one.
    document.body.classList.toggle('has-badge', !joinBadgeEl.hidden);
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
    const previousLayout = manifest?.settings?.layout;
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

    // A layout change redraws the entire screen, so it is not something to leave
    // sitting until the current slide runs out: an organiser who picks a layout and
    // looks up at the projector must see it within a poll, not up to two minutes
    // later at the longest slide length this app allows. Unlike the Ken Burns
    // toggle, which deliberately lands on the next slide, this restarts the slide
    // clock — what it is drawing is a different screen, so there is no slide left
    // to finish.
    if (first || next.settings.layout !== previousLayout) advance();
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

  // ---- the stage -----------------------------------------------------------

  /// The layout the screen should be running right now.
  ///
  /// A takeover overrides whatever the organiser picked. A takeover exists to put one
  /// image - the programme, a word to the room - in front of everybody, and a
  /// takeover tiled into a twelfth of a collage is not a takeover.
  ///
  /// An unrecognised name falls back rather than failing, so a screen left open
  /// across a deploy that renames or adds a layout keeps showing photos in the
  /// original one instead of going black in front of the room.
  function activeLayout() {
    if (takeoverImage() !== null) return DEFAULT_LAYOUT;
    const name = manifest?.settings?.layout;
    return Object.prototype.hasOwnProperty.call(LAYOUTS, name) ? name : DEFAULT_LAYOUT;
  }

  /// Builds the stage for a layout, discarding whatever the previous one had on it.
  /// Called when the layout changes and on the first render, never per slide: the
  /// element count per layout is fixed, and an evening of a thousand slides must
  /// leave the DOM exactly the size it started at.
  function buildStage(name) {
    const spec = LAYOUTS[name];

    stage.className = `layout-${name}`;
    stage.replaceChildren();
    cells = [];
    cellIds = [];
    track = null;
    activeSlot = 0;
    // Nothing on the old stage survives, so the "same image, skip the swap"
    // short-circuit in renderSingle must not match against what used to be there.
    currentImageId = null;

    if (name === 'single') {
      for (const which of ['a', 'b']) {
        const slide = document.createElement('div');
        slide.className = 'slide';
        slide.dataset.slot = which;
        stage.append(slide);
        cells.push(slide);
      }
      stageLayout = name;
      updateCaptionOverlay();
      return;
    }

    // The filmstrip's frames ride on a band that slides as one; every other layout
    // places its cells on the stage directly.
    let parent = stage;
    if (name === 'filmstrip') {
      track = document.createElement('div');
      track.className = 'track';
      stage.append(track);
      parent = track;
    }

    // One frame more than the filmstrip shows: the photo waiting off the right-hand
    // edge, which one slide's worth of drift brings into view.
    const count = name === 'filmstrip' ? spec.slots + 1 : spec.slots;

    for (let slot = 0; slot < count; slot++) {
      const figure = document.createElement('figure');
      figure.className = spec.cell;
      // The mosaic's cells are placed into named grid areas rather than flowing, so
      // that a photo arriving at 23:00 never reshuffles the wall.
      if (name === 'mosaic') figure.classList.add(`area-${slot}`);

      const image = document.createElement('img');
      image.alt = '';

      if (spec.window) {
        const windowEl = document.createElement('span');
        windowEl.className = 'window';
        windowEl.append(image);
        figure.append(windowEl);
      } else {
        figure.append(image);
      }

      if (spec.caption) {
        const caption = document.createElement('figcaption');
        // Two spans, matching the caption bar: a sender's name set apart from their
        // words. Both are filled with textContent in fillCell - a caption and a
        // Telegram display name are attacker-controlled and never become markup.
        caption.append(document.createElement('span'), document.createElement('span'));
        figure.append(caption);
      }

      parent.append(figure);
      cells.push(figure);
      cellIds.push(null);
    }

    stageLayout = name;
    updateCaptionOverlay();
  }

  /// Empties the stage without tearing it down.
  ///
  /// The holding card is drawn over the stage and has no background of its own, so
  /// whatever the stage is still holding shows through it. Before the layouts that
  /// meant one stale photo behind "Send us your photos" when the last image was
  /// hidden or rejected mid-event; with a mosaic it would be six.
  function clearStage() {
    for (const cell of cells) {
      if (stageLayout === 'single') {
        cell.classList.remove('visible');
        cell.replaceChildren();
      } else {
        cell.hidden = true;
      }
    }
    cellIds = cellIds.map(() => null);
    activeSlot = 0;
  }

  /// Re-arms a CSS animation on an element that is staying in the DOM. Reading a
  /// layout property between the two assignments is what forces the reflow that makes
  /// the browser treat the re-declared animation as a new one; without the read the
  /// assignments coalesce and nothing restarts.
  function restartAnimation(element) {
    element.style.animation = 'none';
    void element.offsetWidth;
    element.style.animation = '';
  }

  // ---- filling the slots ---------------------------------------------------

  /// <summary>
  /// Which photos a rotating layout is holding at slide `index`.
  ///
  /// One slot changes per slide, in turn, rather than all of them at once: a
  /// whole-wall refresh every eight seconds reads as a fault from across the room,
  /// and taking turns is what gives each photo `count` slides on screen, which is the
  /// reason to pick a multi-slot layout for a busy hour in the first place. Derived
  /// from the index alone, so two screens on the same event agree slot for slot.
  ///
  /// The result is as short as the playlist when the playlist is shorter than the
  /// grid. Repeating a photo to fill the holes is worse: the same face twice on one
  /// screen reads as a fault, and it would happen ten minutes after the doors open,
  /// when the organiser is watching the screen hardest.
  /// </summary>
  function rotatingSlots(items, index, count) {
    const visible = Math.min(count, items.length);
    const chosen = [];

    for (let slot = 0; slot < visible; slot++) {
      const sinceItsTurn = (((index - slot) % count) + count) % count;
      const turn = index - sinceItsTurn;
      // Before a slot's first turn it simply shows the photo at its own position, so
      // the screen is full from the first frame instead of filling in over a minute.
      chosen.push(items[(turn < 0 ? slot : turn) % items.length]);
    }

    return chosen;
  }

  /// A consecutive run of the playlist from `from`, wrapping past the end. The
  /// filmstrip's shape rather than the mosaic's: the whole band moves by one photo
  /// per slide, so what a frame holds is its distance from the cursor and not a turn
  /// of its own.
  function playlistWindow(items, from, count) {
    const visible = Math.min(count, items.length);
    const start = ((from % items.length) + items.length) % items.length;
    return Array.from({ length: visible }, (_, step) => items[(start + step) % items.length]);
  }

  /// <summary>
  /// The two halves of the split at slide `index`.
  ///
  /// The panes take their turn alternately - the same derivation the mosaic uses,
  /// with two slots - so one half is always still while the other changes. A screen
  /// where both halves cut at once is two slideshows rather than a pairing, and it
  /// leaves the eye nowhere to rest.
  ///
  /// The left pane reaches half a playlist back for an older photo, which is the
  /// pairing this layout exists for; the right stays with the cursor, so the newest
  /// photo reaches the screen within a slide of arriving. Under four photos neither
  /// looks back: a lag on a three-photo playlist puts the same face in both halves.
  /// </summary>
  function splitPanes(items, index) {
    const visible = Math.min(LAYOUTS.split.slots, items.length);
    const lag = items.length < 4 ? 0 : Math.floor(items.length / 2);
    const panes = [];

    for (let pane = 0; pane < visible; pane++) {
      const sinceItsTurn = (((index - pane) % 2) + 2) % 2;
      const turn = index - sinceItsTurn;
      const base = turn < 0 ? pane : turn;
      panes.push(items[(base + (pane === 0 ? lag : 0)) % items.length]);
    }

    return panes;
  }

  /// How far a print is turned on the pile, in degrees. Derived from the photo's id
  /// rather than drawn at random, so a print keeps its angle for as long as it is on
  /// the pile and two screens in one room turn it the same way. FNV-1a over the id,
  /// folded into the allowed range.
  function tiltFor(id) {
    let hash = FNV_OFFSET_BASIS;
    for (let at = 0; at < id.length; at++) {
      hash = Math.imul(hash ^ id.charCodeAt(at), FNV_PRIME) >>> 0;
    }
    return (hash % TILT_RANGE) - (TILT_RANGE - 1) / 2;
  }

  /// Puts `image` in slot `slot`, or empties the slot when there is no image for it
  /// (a playlist shorter than the grid). A slot whose photo has not changed is left
  /// entirely alone: re-setting an unchanged src would restart the entrance animation
  /// of every cell on the screen on every slide, which is the whole-wall refresh
  /// rotatingSlots exists to avoid.
  function fillCell(slot, image, spec) {
    const figure = cells[slot];

    if (!image) {
      figure.hidden = true;
      cellIds[slot] = null;
      return;
    }

    figure.hidden = false;
    if (cellIds[slot] === image.id) return;
    cellIds[slot] = image.id;

    figure.querySelector('img').src = imageUrl(image.id, spec.thumbs);

    if (spec.caption) {
      const caption = figure.querySelector('figcaption');
      const [sender, text] = caption.children;
      sender.textContent = image.senderName ?? '';
      text.textContent = image.caption ?? '';
      // A photo sent with neither a caption nor a name would otherwise leave an
      // empty line of leading under the print, making one mat taller than its
      // neighbours for no visible reason.
      caption.hidden = !image.senderName && !image.caption;
    }

    if (spec.tilt) figure.style.setProperty('--tilt', `${tiltFor(image.id)}deg`);
    if (spec.enters) restartAnimation(figure);
  }

  // ---- rendering -----------------------------------------------------------

  /// One photo, whole, on its own blurred backdrop. The original screen, the default,
  /// and what a takeover always gets.
  function renderSingle(image) {
    if (stageLayout !== 'single') buildStage('single');

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

    const slot = cells[activeSlot];
    const next = cells[1 - activeSlot];
    const url = imageUrl(image.id, false);

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

  /// <summary>
  /// The band, at playlist position `index`.
  ///
  /// The window it shows advances by one photo per slide while the band itself
  /// travels exactly one frame to the left over the same interval. The two cancel, so
  /// the strip appears to move continuously while the DOM holds the same six frames
  /// all evening, and there is no moment for a latecomer to miss.
  ///
  /// The ordinary slide moves the leftmost frame to the right-hand end and gives it
  /// the one new photo, rather than reassigning all six sources. Not for the saved
  /// request - the five that stay would all be cache hits - but because those five
  /// keep the exact <img> elements they already had, so no decode can open a gap in
  /// the middle of the band. Anything that is not a shift by exactly one (a
  /// reconciled playlist, a switch into this layout, the first render) refills.
  ///
  /// The drift is timed from the slide clock itself, so it can neither outlast its
  /// slide nor finish early and freeze mid-travel. A playlist that does not fill the
  /// band does not drift at all: there is nothing to drift towards, and rotating five
  /// photos through five frames would move faces around the screen for no reason.
  /// </summary>
  function renderFilmstrip(index, spec) {
    const scrolling = playlist.length > spec.slots;
    const wanted = playlistWindow(playlist, scrolling ? index : 0, cells.length);

    // Whether the band overflows, which is what decides where it is anchored. Not the
    // same question as whether it is moving: a full band under reduced motion still
    // overflows and must still be anchored at its start edge, because a centred flex
    // container splits its overflow across both edges and would leave a blank strip
    // down one side of the screen.
    track.dataset.strip = scrolling ? 'scrolling' : 'short';

    const shiftedByOne = wanted.length > 1
      && cellIds.length === wanted.length
      && cellIds.slice(1).every((id, at) => id !== null && id === wanted[at].id);

    if (shiftedByOne) {
      const head = cells.shift();
      cellIds.shift();
      track.append(head);
      cells.push(head);
      cellIds.push(null);
      fillCell(cells.length - 1, wanted[wanted.length - 1], spec);
    } else {
      cells.forEach((_, slot) => fillCell(slot, wanted[slot], spec));
    }

    const seconds = manifest?.settings?.slideSeconds ?? 8;
    track.style.setProperty('--drift-duration', `${seconds * 1000}ms`);
    track.dataset.motion = scrolling ? 'drift' : 'still';
    // Restarted rather than left running: the band's contents have just shifted under
    // it, and an animation that kept its old phase would creep out of alignment with
    // them over an evening.
    if (scrolling) restartAnimation(track);
  }

  /// Every layout but the single one. Builds the stage on a change of layout, then
  /// hands each slot the photo it should be holding at this slide.
  function renderLayout(name, index) {
    const spec = LAYOUTS[name];
    if (stageLayout !== name) buildStage(name);

    // The playlist cursor still means what it always did, whatever is on screen:
    // reconcilePlaylist resumes from the photo at `index` regardless of how many of
    // its neighbours happen to be visible beside it.
    currentImage = playlist[index] ?? null;
    currentImageId = currentImage?.id ?? null;

    if (name === 'filmstrip') { renderFilmstrip(index, spec); return; }

    const wanted = name === 'split'
      ? splitPanes(playlist, index)
      : rotatingSlots(playlist, index, spec.slots);

    // The collage is the one layout that deliberately shows less than it could at the
    // start of the night: it opens on a single photo and gains a cell per slide, so
    // the room watches the screen compose itself. How full it is comes from how many
    // slides this page has rendered, not from the playlist position - the position
    // wraps, and the grid would shrink again every time the show came round. That
    // makes it a per-screen number, and it is meant to be: a machine that reloads at
    // 23:00 drops back to one cell and refills over the next twelve slides. Switching
    // into the collage from another layout does not, because the count has been
    // running all along - the organiser who picks it two hours in gets a full grid
    // rather than a minute of the room watching it fill again.
    const filled = name === 'collage'
      ? Math.min(slidesShown + 1, wanted.length)
      : wanted.length;

    cells.forEach((_, slot) => fillCell(slot, slot < filled ? wanted[slot] : undefined, spec));
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

  /// Whichever layout is running, the next photo to appear anywhere on the screen is
  /// the one at the cursor, so the same two-ahead preload serves all six.
  function preload(count, thumbs) {
    for (let i = 1; i <= count; i++) {
      const upcoming = playlist[(cursor + i) % playlist.length];
      if (upcoming) new Image().src = imageUrl(upcoming.id, thumbs);
    }
  }

  function advance() {
    clearTimeout(advanceTimer);

    const takeover = takeoverImage();
    if (takeover) {
      renderSingle(takeover);
      // Re-check often so the screen restores within a slide of the takeover clearing.
      advanceTimer = setTimeout(advance, 2000);
      return;
    }

    if (playlist.length === 0) {
      emptyEl.hidden = false;
      // A caption left over from the last-shown image must not linger behind
      // the holding card once the playlist has genuinely run dry, and nor must
      // the photos themselves - the card is transparent.
      currentImage = null;
      currentImageId = null;
      clearStage();
      updateCaptionOverlay();
      advanceTimer = setTimeout(advance, 2000);
      return;
    }

    emptyEl.hidden = true;
    lastShownIndex = cursor % playlist.length;

    const layout = activeLayout();
    if (layout === 'single') renderSingle(playlist[lastShownIndex]);
    else renderLayout(layout, lastShownIndex);

    cursor = (lastShownIndex + 1) % playlist.length;
    // Counted after the render, so the collage's first slide shows one cell.
    slidesShown++;
    preload(2, LAYOUTS[layout].thumbs);

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
