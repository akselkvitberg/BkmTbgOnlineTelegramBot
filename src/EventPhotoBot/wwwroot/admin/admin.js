(() => {
  'use strict';

  const POLL_MS = 2000;
  let etag = null;
  let manifest = null;
  const listeners = [];

  // ---- event context -------------------------------------------------------
  // The event a page is about lives in ?event=; absent means the default event,
  // which is what every page and bookmark from before events already meant.
  const eventId = new URLSearchParams(location.search).get('event');
  let eventsPromise = null;
  const PHASES = { open: 'åpent', scheduled: 'planlagt', closed: 'avsluttet' };

  function events(fresh = false) {
    if (fresh || !eventsPromise) {
      eventsPromise = fetch('/api/events', { cache: 'no-store' })
        .then(response => response.ok ? response.json() : [])
        .catch(() => []);
    }
    return eventsPromise;
  }

  async function currentEvent() {
    const list = await events();
    return list.find(e => e.id === eventId) ?? list.find(e => e.isDefault) ?? null;
  }

  function withEvent(path) {
    if (!eventId) return path;
    return `${path}${path.includes('?') ? '&' : '?'}event=${encodeURIComponent(eventId)}`;
  }

  async function renderEventPicker() {
    const nav = document.querySelector('nav');
    if (!nav) return;
    const list = await events();
    if (list.length < 2) return;   // only the daily event: nothing to choose between
    const current = await currentEvent();

    const select = document.createElement('select');
    select.className = 'event-picker';
    select.setAttribute('aria-label', 'Arrangement');
    for (const ev of list) {
      const option = document.createElement('option');
      option.value = ev.id;
      // textContent, not innerHTML: an event name is typed by an organiser, but it
      // is still text, and this keeps it that way.
      option.textContent = ev.phase === 'open' ? ev.name : `${ev.name} (${PHASES[ev.phase]})`;
      option.selected = ev.id === current?.id;
      select.appendChild(option);
    }
    select.onchange = () => {
      const next = new URL(location.href);
      const chosen = list.find(e => e.id === select.value);
      if (chosen?.isDefault) next.searchParams.delete('event');
      else next.searchParams.set('event', select.value);
      location.href = next.toString();
    };
    nav.insertBefore(select, nav.querySelector('.nav-logout'));
  }

  window.Admin = {
    onManifest(callback) { listeners.push(callback); if (manifest) callback(manifest); },
    get manifest() { return manifest; },
    api,
    imageUrl: (id, size) => `/img/${encodeURIComponent(id)}/${size}`,
    escapeHtml,
    toast,
    dialog,
    confirm: ({ title, body, confirmLabel, danger = false }) => dialog({
      title, body,
      actions: [
        { label: 'Avbryt', value: false },
        { label: confirmLabel, value: true, kind: danger ? 'danger' : 'primary' },
      ],
    }).then(Boolean),
    timeAgo,
    events,
    currentEvent,
    withEvent,
    eventId,
  };

  /** A short message at the bottom of the screen, instead of a blocking alert(). */
  function toast(text, kind = 'info') {
    let host = document.querySelector('.toasts');
    if (!host) {
      host = document.createElement('div');
      host.className = 'toasts';
      host.setAttribute('role', 'status');
      host.setAttribute('aria-live', 'polite');
      document.body.appendChild(host);
    }
    const item = document.createElement('div');
    item.className = `toast ${kind}`;
    item.textContent = text;
    host.appendChild(item);
    setTimeout(() => item.remove(), kind === 'error' ? 6000 : 2500);
  }

  /**
   * An in-page modal with a title, a line of text and a row of buttons. Resolves to
   * the chosen action's value, or null when dismissed with Esc or the backdrop.
   * Built on <dialog> so focus, Esc and the backdrop behave natively.
   */
  function dialog({ title, body, actions }) {
    return new Promise(resolve => {
      const element = document.createElement('dialog');
      element.className = 'admin-dialog';

      const heading = document.createElement('h2');
      heading.textContent = title;
      element.appendChild(heading);
      if (body) {
        const text = document.createElement('p');
        text.textContent = body;
        element.appendChild(text);
      }

      const row = document.createElement('div');
      row.className = 'dialog-actions';
      for (const action of actions) {
        const button = document.createElement('button');
        button.type = 'button';
        button.textContent = action.label;
        if (action.kind) button.className = action.kind;
        button.onclick = () => { element.close(); resolve(action.value); };
        row.appendChild(button);
      }
      element.appendChild(row);

      element.addEventListener('cancel', () => resolve(null));
      element.addEventListener('click', event => {
        if (event.target === element) { element.close(); resolve(null); }
      });
      element.addEventListener('close', () => element.remove());

      document.body.appendChild(element);
      element.showModal();
      // Land on the last (the affirmative) button, so Enter confirms and Esc cancels.
      row.lastElementChild?.focus();
    });
  }

  function timeAgo(value) {
    const seconds = Math.max(0, Math.round((Date.now() - new Date(value)) / 1000));
    if (seconds < 60) return 'akkurat nå';
    const minutes = Math.round(seconds / 60);
    if (minutes < 60) return `${minutes} min siden`;
    return new Date(value).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }

  // Every one of these characters can be attacker-controlled: a Telegram
  // display name or a photo caption ends up here. Any admin page that
  // interpolates such a string into innerHTML must run it through this
  // first - see the pages' own render functions for where that applies.
  function escapeHtml(value) {
    return String(value ?? '').replace(/[&<>"']/g, ch => ({
      '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
    })[ch]);
  }

  async function api(method, path, body) {
    const options = { method, headers: {} };
    if (body instanceof FormData) {
      options.body = body;
    } else if (body !== undefined) {
      options.headers['Content-Type'] = 'application/json';
      options.body = JSON.stringify(body);
    }

    let response;
    try {
      response = await fetch(path, options);
    } catch {
      toast('Fikk ikke kontakt med serveren. Sjekk nettet og prøv igjen.', 'error');
      return null;
    }
    if (response.status === 401) { location.href = '/login'; return null; }
    if (!response.ok) {
      // The API answers errors as {"error": "..."}; show that sentence, not the JSON.
      const detail = await response.text();
      let message = detail.trim();
      try { message = JSON.parse(detail).error || message; } catch { /* plain text */ }
      toast(message || `Det gikk ikke (${response.status}).`, 'error');
      return null;
    }
    // Refresh straight away rather than waiting for the next poll. The write above
    // already succeeded, so a dropped connection here - the common case on a phone -
    // must not make the caller look like the action itself failed: pollOnce rejecting
    // would otherwise propagate out of api() and skip the caller's own .then(load),
    // leaving the page showing no sign of a change the server did make. The next
    // scheduled poll (loop(), below) picks it up regardless.
    try { await pollOnce(true); } catch { /* stale UI for one tick; not a failure */ }
    return response;
  }

  async function pollOnce(force) {
    const headers = force || !etag ? {} : { 'If-None-Match': etag };
    const response = await fetch(withEvent('/api/manifest'), { headers, cache: 'no-store' });
    if (response.status === 304) return;
    if (!response.ok) return;

    etag = response.headers.get('ETag');
    manifest = await response.json();

    // The badge counts everything waiting, across events, because the queue shows
    // all of it.
    for (const badge of document.querySelectorAll('.badge')) {
      badge.textContent = manifest.pendingTotal || '';
      badge.dataset.count = manifest.pendingTotal;
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

    // Name the image, not just "one image" - a takeover set to "until I
    // clear it" and forgotten is the likeliest way to leave one photo on
    // the wall for an hour, and the admin needs to know which photo to go
    // look for. The takeover image is always approved (the server enforces
    // that), so it is always present in manifest.images; the fallback here
    // is defensive only.
    const image = manifest.images.find(i => i.id === takeover.id);
    const sender = escapeHtml(image?.senderName ?? (image ? 'Lastet opp av arrangør' : 'Ukjent'));
    const caption = image?.caption ? escapeHtml(image.caption) : '';
    const until = takeover.until
      ? `til ${new Date(takeover.until).toLocaleTimeString()}`
      : 'til du avslutter';

    banner.innerHTML = `
      <img class="takeover-thumb" src="${Admin.imageUrl(takeover.id, 'thumb')}" alt="">
      <span class="takeover-info">
        <strong>Holder skjermen</strong>
        <span>${sender}</span>
        ${caption ? `<span class="muted">${caption}</span>` : ''}
        <span class="muted">${until}</span>
      </span>`;

    const clear = document.createElement('button');
    clear.className = 'accent';
    clear.textContent = 'Avslutt overtakelse';
    clear.onclick = () => api('DELETE', withEvent('/api/takeover'));
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

  // Pages about one event carry the choice along; Telegram and the events list are
  // about every event, so their links stay plain.
  const GLOBAL_PAGES = ['/admin/telegram', '/admin/events', '/dev'];
  for (const link of document.querySelectorAll('nav a')) {
    const href = link.getAttribute('href');
    if (href === location.pathname) link.classList.add('active');
    if (!GLOBAL_PAGES.includes(href)) link.href = withEvent(href);
  }

  renderEventPicker();

  loop();
})();
