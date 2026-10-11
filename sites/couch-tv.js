// ---------------------------------------------------------------------------
// Couch mode, the shared part. Runs inside a streaming site's pages, after the
// site's own part (SITE, above), with the launcher's settings in COUCH.
//
//  - The TV look: when a video is playing, it fills the screen and everything
//    else on the page is hidden. The site's player keeps doing the playing,
//    so DRM and sign-ins work as normal.
//  - An on-screen channel bar and channel list in the launcher's CRT style.
//  - D-pad keys. On live TV: ▲▼ change channel, ◄ last channel, A info,
//    B channel list. In the list: ▲▼ move, A watch, B close. On anything
//    else that's playing: A play/pause, ◄► 10 s back/forward, ▼ the list.
//
// Channels are the site part's own list plus any it spots in links while you
// browse the site, remembered in the site's local storage. Nothing here is
// sent anywhere except the launcher's log.
// ---------------------------------------------------------------------------

if (window.top !== window || window.__couchTv) return;
window.__couchTv = true;

const FG = COUCH.fg || '#00FF66';
const BG = COUCH.bg || '#000000';
const BIND = COUCH.binding || 'couchLauncher';
const NOW_TUNING_MS = 45000;

// ------------------------------------------------------------------ talking

let logged = 0;
function log(message) {
  // The launcher's log is how the far side of the room gets debugged; a
  // runaway loop shouldn't fill it.
  if (++logged > 200) return;
  try {
    const send = window[BIND];
    if (typeof send === 'function') send(JSON.stringify({ t: 'log', msg: String(message) }));
  } catch (e) { /* the launcher isn't listening */ }
}

// Which DRM the site asks for, and whether this browser has it. Answers
// "would this site play in a different browser engine?" from real use.
(function watchDrm() {
  const original = navigator.requestMediaKeySystemAccess;
  if (typeof original !== 'function') return;
  const seen = new Set();
  navigator.requestMediaKeySystemAccess = function (keySystem) {
    const answer = original.apply(this, arguments);
    if (!seen.has(keySystem)) {
      seen.add(keySystem);
      answer.then(
        () => log(`DRM: site asked for ${keySystem}: available`),
        (e) => log(`DRM: site asked for ${keySystem}: not available (${e && e.name})`));
    }
    return answer;
  };
})();

// ------------------------------------------------------------------ storage

function storage(area, prefix) {
  return {
    get(key, fallback) {
      try {
        const value = area().getItem(prefix + key);
        return value == null ? fallback : JSON.parse(value);
      } catch (e) { return fallback; }
    },
    set(key, value) {
      try {
        if (value == null) area().removeItem(prefix + key);
        else area().setItem(prefix + key, JSON.stringify(value));
      } catch (e) { /* storage blocked: just forget */ }
    },
  };
}
const saved = storage(() => localStorage, `couch.${SITE.id}.`);
const thisVisit = storage(() => sessionStorage, `couch.${SITE.id}.`);

function absolute(url) {
  try { return new URL(url, location.origin); } catch (e) { return null; }
}
function samePage(a, b) {
  const x = absolute(a), y = absolute(b);
  return !!x && !!y && x.origin === y.origin && x.pathname.replace(/\/$/, '') === y.pathname.replace(/\/$/, '');
}

// ----------------------------------------------------------------- channels

function channels() {
  const players = saved.get('players', {});
  const seen = new Set();
  const list = [];
  for (const channel of [...SITE.channels, ...saved.get('learned', [])]) {
    if (!channel || !channel.id || seen.has(channel.id)) continue;
    seen.add(channel.id);
    list.push({ ...channel, player: players[channel.id] });
  }
  return list;
}

function learnChannels() {
  const known = new Set(channels().map((c) => c.id));
  const learned = saved.get('learned', []);
  let added = 0;
  for (const link of document.querySelectorAll('a[href]')) {
    const label = link.getAttribute('aria-label') || link.textContent ||
      (link.querySelector('img[alt]') || {}).alt || '';
    const channel = SITE.channelFromLink(link.href, label.replace(/\s+/g, ' ').trim());
    if (!channel || known.has(channel.id)) continue;
    known.add(channel.id);
    learned.push(channel);
    added++;
    log(`found channel ${channel.name} (${channel.url})`);
  }
  if (added) saved.set('learned', learned);
}

function rememberPlayer(channel, url) {
  const players = saved.get('players', {});
  const where = absolute(url);
  if (!where || players[channel.id] === where.pathname + where.search) return;
  players[channel.id] = where.pathname + where.search;
  saved.set('players', players);
  log(`${channel.name} plays at ${players[channel.id]}`);
}

/** The channel on screen now, if this page is one. */
function currentChannel() {
  const list = channels();
  const byPage = list.find((c) => (c.player && samePage(c.player, location.href)) || samePage(c.url, location.href));
  if (byPage) return byPage;
  const tuning = tuningTo();
  return tuning ? list.find((c) => c.id === tuning) || null : null;
}

/** Set while a channel change is under way: across the page loads it takes. */
function tuningTo() {
  const tuning = thisVisit.get('tuning', null);
  return tuning && Date.now() - tuning.at < NOW_TUNING_MS ? tuning.id : null;
}

function tune(channel) {
  const now = currentChannel();
  if (now && now.id !== channel.id) thisVisit.set('previous', now.id);
  thisVisit.set('tuning', { id: channel.id, at: Date.now() });
  thisVisit.set('ownScreen', null);
  showBar(channel, 'TUNING…', 0);
  const target = absolute(channel.player || channel.url);
  if (!target) return;
  if (target.href === location.href) {
    // Already here (it never got going?) — start over.
    location.reload();
  } else {
    location.assign(target.href);
  }
}

// --------------------------------------------------------------- the video

let video = null;

function pickVideo() {
  if (video && video.isConnected) return video;
  let best = null, bestArea = 0;
  for (const v of document.getElementsByTagName('video')) {
    const r = v.getBoundingClientRect();
    const area = Math.max(r.width * r.height, v.videoWidth * v.videoHeight) + (v.readyState > 0 ? 1e9 : 0);
    if (area > bestArea) { best = v; bestArea = area; }
  }
  return best;
}

function isPlaying(v) {
  return !!v && !v.paused && !v.ended && v.readyState >= 2;
}

function isLive(v) {
  return !!currentChannel() || (!!v && v.duration === Infinity);
}

function bigOnScreen(v) {
  const r = v.getBoundingClientRect();
  return r.width * r.height > 0.5 * innerWidth * innerHeight;
}

// ------------------------------------------------------------------ TV look

const CSS = `
html.couch-tv, html.couch-tv body { background: #000 !important; overflow: hidden !important; cursor: none !important; }
html.couch-tv body * { visibility: hidden !important; }
html.couch-tv .couch-tv-path {
  visibility: hidden !important; transform: none !important; filter: none !important;
  perspective: none !important; contain: none !important; will-change: auto !important;
  clip-path: none !important; mask: none !important; opacity: 1 !important; z-index: auto !important;
}
html.couch-tv video.couch-tv-video {
  visibility: visible !important; position: fixed !important; inset: 0 !important;
  width: 100vw !important; height: 100vh !important; max-width: none !important; max-height: none !important;
  margin: 0 !important; padding: 0 !important; transform: none !important; object-fit: contain !important;
  background: #000 !important; z-index: 2147483646 !important; opacity: 1 !important;
}`;

let tvOn = false;

function setTv(on, v) {
  const root = document.documentElement;
  if (!root) return;
  if (!document.getElementById('couch-tv-style')) {
    const style = document.createElement('style');
    style.id = 'couch-tv-style';
    style.textContent = CSS;
    (document.head || root).appendChild(style);
  }
  for (const old of document.querySelectorAll('.couch-tv-video, .couch-tv-path')) {
    if (!on || old !== v && !(v && old.contains(v))) old.classList.remove('couch-tv-video', 'couch-tv-path');
  }
  if (on && v) {
    // Every box the video sits in: anything that would trap it in a corner
    // of the page (transforms, stacking, clipping) is switched off. Done
    // every tick, since the site may redraw them.
    v.classList.add('couch-tv-video');
    for (let el = v.parentElement; el && el !== document.body && el !== root; el = el.parentElement) {
      el.classList.add('couch-tv-path');
    }
  }
  root.classList.toggle('couch-tv', on);
  if (on !== tvOn) {
    tvOn = on;
    log(on ? `TV look on: ${location.pathname}` : `TV look off: ${location.pathname}`);
  }
}

// ---------------------------------------------------------------- on screen

const ui = {};

function rgba(hex, alpha) {
  const h = hex.replace('#', '');
  const n = parseInt(h.length === 3 ? h.split('').map((c) => c + c).join('') : h.slice(0, 6), 16);
  return `rgba(${(n >> 16) & 255}, ${(n >> 8) & 255}, ${n & 255}, ${alpha})`;
}

function buildUi() {
  if (ui.host && ui.host.isConnected) return true;
  const root = document.documentElement;
  if (!root || !document.body) return false;
  if (!ui.host) {
    const host = document.createElement('couch-tv');
    host.style.cssText = 'all: initial; position: fixed; inset: 0; z-index: 2147483647; pointer-events: none;';
    const shadow = host.attachShadow({ mode: 'open' });
    const panel = `background: ${rgba(BG, 0.86)}; border: 3px solid ${FG};
      box-shadow: 0 0 28px ${rgba(FG, 0.45)}, inset 0 0 40px ${rgba(FG, 0.08)};
      background-image: repeating-linear-gradient(0deg, rgba(0,0,0,0.33) 0 1px, transparent 1px 3px);`;
    shadow.innerHTML = `
      <style>
        * { box-sizing: border-box; }
        .wrap { position: fixed; inset: 0; font-family: Consolas, "Courier New", monospace; color: ${FG};
                text-shadow: 0 0 10px ${rgba(FG, 0.6)}; pointer-events: none; }
        .bar { position: absolute; left: 5vw; right: 5vw; bottom: 6vh; padding: 2.4vh 3vw; ${panel}
               display: flex; align-items: center; gap: 3vw; opacity: 0; transition: opacity .2s; }
        .bar.on { opacity: 1; }
        .num { font-size: 7vh; font-weight: bold; min-width: 9vh; }
        .name { font-size: 4.6vh; font-weight: bold; text-transform: uppercase; }
        .sub { font-size: 2.6vh; opacity: .75; margin-top: .8vh; text-transform: uppercase; }
        .clock { margin-left: auto; font-size: 4.6vh; font-weight: bold; }
        .list { position: absolute; top: 8vh; bottom: 8vh; left: 5vw; width: 40vw; padding: 3.5vh 2vw; ${panel}
                display: none; flex-direction: column; }
        .list.on { display: flex; }
        .title { font-size: 2.6vh; opacity: .6; letter-spacing: .25em; margin: 0 0 2.4vh .6vw; font-weight: bold; }
        .items { flex: 1; overflow: hidden; }
        .item { font-size: 3.4vh; padding: 1.2vh 1.2vw; margin-bottom: .9vh; border: 2px solid transparent;
                white-space: nowrap; overflow: hidden; text-overflow: ellipsis; text-transform: uppercase; }
        .item.sel { border-color: ${FG}; background: ${rgba(FG, 0.16)}; box-shadow: 0 0 20px ${rgba(FG, 0.5)}; font-weight: bold; }
        .item.extra { opacity: .7; font-size: 2.8vh; }
        .item .now { opacity: .6; font-size: 2.2vh; }
        .hint { font-size: 2.2vh; opacity: .55; margin-top: 2vh; }
        .toast { position: absolute; top: 6vh; right: 5vw; max-width: 50vw; padding: 1.6vh 2vw; ${panel}
                 font-size: 2.8vh; opacity: 0; transition: opacity .2s; text-transform: uppercase; }
        .toast.on { opacity: 1; }
      </style>
      <div class="wrap">
        <div class="bar"><div class="num"></div><div><div class="name"></div><div class="sub"></div></div><div class="clock"></div></div>
        <div class="list"><div class="title"></div><div class="items"></div>
          <div class="hint">▲▼ MOVE &nbsp; A WATCH &nbsp; B CLOSE</div></div>
        <div class="toast"></div>
      </div>`;
    ui.host = host;
    for (const name of ['bar', 'num', 'name', 'sub', 'clock', 'list', 'title', 'items', 'toast']) {
      ui[name] = shadow.querySelector('.' + name);
    }
  }
  // On <html>, not <body>: outside the part of the page that gets hidden,
  // and out of the way of the site redrawing its own content.
  root.appendChild(ui.host);
  return true;
}

let barTimer = 0;
function showBar(channel, status, seconds = 5) {
  if (!buildUi()) return;
  const list = channels();
  const index = channel ? list.findIndex((c) => c.id === channel.id) : -1;
  ui.num.textContent = index >= 0 ? String(index + 1).padStart(2, '0') : '--';
  ui.name.textContent = channel ? channel.name : SITE.name;
  const title = SITE.nowShowing ? SITE.nowShowing() : '';
  const sameAsName = title && title.toUpperCase() === ui.name.textContent.toUpperCase();
  ui.sub.textContent = [status, sameAsName ? '' : title].filter(Boolean).join('  ·  ');
  ui.clock.textContent = new Date().toTimeString().slice(0, 5);
  ui.bar.classList.add('on');
  clearTimeout(barTimer);
  if (seconds > 0) barTimer = setTimeout(() => ui.bar.classList.remove('on'), seconds * 1000);
}

let toastTimer = 0;
function toast(text, seconds = 6) {
  if (!buildUi()) return;
  ui.toast.textContent = text;
  ui.toast.classList.add('on');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => ui.toast.classList.remove('on'), seconds * 1000);
}

// ------------------------------------------------------------ channel list

const list = { open: false, entries: [], at: 0 };

function openList() {
  if (!buildUi()) return;
  const now = currentChannel();
  list.entries = channels().map((channel) => ({ channel, label: channel.name, now: now && now.id === channel.id }));
  if (SITE.directory) list.entries.push({ extra: 'more', label: 'FIND MORE CHANNELS…' });
  list.entries.push({ extra: 'own', label: `${SITE.name}'S OWN SCREEN` });
  list.at = Math.max(0, list.entries.findIndex((e) => e.now));
  list.open = true;
  ui.title.textContent = `${SITE.name} — LIVE`;
  ui.list.classList.add('on');
  ui.bar.classList.remove('on');
  paintList();
}

function closeList() {
  list.open = false;
  if (ui.list) ui.list.classList.remove('on');
}

function paintList() {
  ui.items.textContent = '';
  list.entries.forEach((entry, i) => {
    const row = document.createElement('div');
    row.className = 'item' + (i === list.at ? ' sel' : '') + (entry.extra ? ' extra' : '');
    row.textContent = entry.channel ? `${String(i + 1).padStart(2, '0')}  ${entry.label}` : entry.label;
    if (entry.now) {
      const now = document.createElement('span');
      now.className = 'now';
      now.textContent = '   ◄ ON NOW';
      row.appendChild(now);
    }
    ui.items.appendChild(row);
    if (i === list.at) requestAnimationFrame(() => row.scrollIntoView({ block: 'nearest' }));
  });
}

function chooseFromList() {
  const entry = list.entries[list.at];
  closeList();
  if (!entry) return;
  if (entry.channel) {
    tune(entry.channel);
  } else if (entry.extra === 'own') {
    useOwnScreen();
  } else if (entry.extra === 'more') {
    // Left alone so channels can be picked out of it; picking one puts
    // couch mode back on.
    thisVisit.set('ownScreen', absolute(SITE.directory).pathname);
    thisVisit.set('tuning', null);
    toast(`CHANNELS YOU PASS ON ${SITE.name} ARE ADDED TO THE LIST`, 10);
    location.assign(absolute(SITE.directory).href);
  }
}

/** The site's own page, untouched, until you leave it. */
function useOwnScreen() {
  thisVisit.set('ownScreen', location.pathname);
  thisVisit.set('tuning', null);
  setTv(false);
  toast(`${SITE.name}'S OWN SCREEN, UNTIL YOU LEAVE THIS PAGE`);
}

function ownScreen() {
  return thisVisit.get('ownScreen', null) === location.pathname;
}

// --------------------------------------------------------------- the keys

let zap = { index: -1, timer: 0 };

function zapBy(step) {
  const all = channels();
  if (!all.length) return;
  const now = currentChannel();
  if (!zap.timer) zap.index = now ? all.findIndex((c) => c.id === now.id) : -1;
  zap.index = ((zap.index < 0 && step < 0 ? 0 : zap.index) + step + all.length) % all.length;
  const target = all[zap.index];
  showBar(target, 'CHANGING…', 0);
  clearTimeout(zap.timer);
  // A short wait, so a few quick presses skip channels instead of loading each.
  zap.timer = setTimeout(() => {
    zap.timer = 0;
    tune(target);
  }, 900);
}

function lastChannel() {
  const id = thisVisit.get('previous', null);
  const channel = channels().find((c) => c.id === id);
  if (channel) tune(channel);
  else toast('NO LAST CHANNEL YET');
}

/** What a key does right now; false = leave it to the site. */
function onKey(key) {
  if (list.open) {
    switch (key) {
      case 'ArrowUp': list.at = Math.max(0, list.at - 1); paintList(); return true;
      case 'ArrowDown': list.at = Math.min(list.entries.length - 1, list.at + 1); paintList(); return true;
      case 'Enter': case ' ': chooseFromList(); return true;
      case 'Escape': case 'ArrowLeft': case 'Backspace': closeList(); return true;
      case 'ArrowRight': return true;
    }
    return false;
  }
  if (!tvOn) return false;
  const v = pickVideo();
  if (isLive(v)) {
    switch (key) {
      case 'ArrowUp': zapBy(-1); return true;
      case 'ArrowDown': zapBy(1); return true;
      case 'ArrowLeft': lastChannel(); return true;
      case 'ArrowRight': showBar(currentChannel(), 'LIVE'); return true;
      case 'Enter': case ' ':
        if (v && v.paused) v.play().catch(() => {});
        showBar(currentChannel(), 'LIVE');
        return true;
      case 'Escape': openList(); return true;
    }
    return false;
  }
  switch (key) {
    case 'Enter': case ' ':
      if (v) {
        if (v.paused) v.play().catch(() => {});
        else v.pause();
        showBar(null, v.paused ? 'PLAY' : 'PAUSED', v.paused ? 2 : 0);
      }
      return true;
    case 'ArrowLeft': if (v) v.currentTime = Math.max(0, v.currentTime - 10); return true;
    case 'ArrowRight': if (v) v.currentTime = v.currentTime + 10; return true;
    case 'ArrowUp': showBar(null, ''); return true;
    case 'ArrowDown': openList(); return true;
  }
  return false;   // B leaves the player the site's own way
}

const swallowed = new Set();
window.addEventListener('keydown', (e) => {
  if (e.ctrlKey || e.altKey || e.metaKey) return;
  let handled = false;
  try { handled = onKey(e.key); } catch (err) { log('key: ' + err); }
  if (!handled) return;
  e.preventDefault();
  e.stopImmediatePropagation();
  swallowed.add(e.code);
}, true);
for (const type of ['keypress', 'keyup']) {
  window.addEventListener(type, (e) => {
    if (!swallowed.has(e.code)) return;
    e.preventDefault();
    e.stopImmediatePropagation();
    if (type === 'keyup') swallowed.delete(e.code);
  }, true);
}

// ----------------------------------------------------------- every half second

let lastHref = '';
let startTries = 0;
let ticks = 0;

function tick() {
  ticks++;
  if (location.href !== lastHref) {
    lastHref = location.href;
    startTries = 0;
    closeList();
  }
  if (!document.body) return;
  buildUi();
  if (ticks % 4 === 1) learnChannels();

  let tuning = tuningTo();
  const v = pickVideo();
  if (!tuning && !ownScreen() && !isPlaying(v)) {
    // Landed on a channel's page (the tile's own address, say): put it on.
    const here = channels().find((c) => samePage(c.url, location.href));
    if (here) {
      log(`starting ${here.name}, its page was opened`);
      thisVisit.set('tuning', { id: here.id, at: Date.now() });
      tuning = here.id;
    }
  }
  const channel = tuning ? channels().find((c) => c.id === tuning) : null;
  if (v !== video) {
    video = v;
    if (v) log(`video found on ${location.pathname} (duration ${v.duration})`);
  }

  const wanted = !!v && !ownScreen() &&
    (SITE.isPlayer(location) || !!channel || (isPlaying(v) && bigOnScreen(v)));
  setTv(wanted, v);

  if (channel) {
    if (isPlaying(v)) {
      // It's on: this page is where the channel plays, so next time go
      // straight here and skip the channel's page.
      rememberPlayer(channel, location.href);
      thisVisit.set('tuning', null);
      showBar(channel, 'LIVE');
    } else if (!SITE.isPlayer(location) && ticks % 3 === 0) {
      startChannel(channel);
    } else if (v && v.paused && v.readyState >= 2 && ticks % 6 === 0) {
      // Loaded but waiting for a click that the remote can't give.
      v.play().catch(() => {});
    }
  }

  if (tvOn) {
    const button = SITE.stillWatching && SITE.stillWatching();
    if (button) {
      log('answered "still watching?"');
      button.click();
    }
  }
}

/** On a channel's page: press its play/watch button. */
function startChannel(channel) {
  if (startTries >= 10) return;
  startTries++;
  const button = SITE.findPlay();
  if (button) {
    const href = button.href || button.getAttribute && button.getAttribute('href');
    log(`starting ${channel.name}: ${href || button.tagName + ' "' + (button.textContent || '').trim().slice(0, 30) + '"'}`);
    if (href && absolute(href) && absolute(href).origin === location.origin) location.assign(absolute(href).href);
    else button.click();
  } else if (startTries === 10) {
    log(`couldn't find how to start ${channel.name} on ${location.pathname}`);
    thisVisit.set('tuning', null);
    toast(`COULDN'T START ${channel.name} — PICK IT ON SCREEN`, 10);
    if (ui.bar) ui.bar.classList.remove('on');
  }
}

const tuningNow = tuningTo();
if (tuningNow) {
  const channel = channels().find((c) => c.id === tuningNow);
  if (channel) document.addEventListener('DOMContentLoaded', () => showBar(channel, 'TUNING…', 0));
}
setInterval(() => {
  try { tick(); } catch (e) { log('tick: ' + (e && e.stack || e)); }
}, 500);
log(`ready on ${location.pathname}`);
