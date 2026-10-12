// ---------------------------------------------------------------------------
// Live Surfer: runs inside every page a Live Surfer tile opens (ABC iview,
// SBS On Demand, 7plus, 9Now, 10…), with the tile's settings in COUCH.
//
//  - The channels are the tile's own list: pages you saved with
//    "+ ADD THIS CHANNEL". The launcher keeps the list and does the channel
//    changes; this script draws the screen and reads the keys.
//  - The TV look: when a video is playing, it fills the screen and the rest
//    of the page is hidden. The site's player still does the playing, so
//    sign-ins and DRM work as normal.
//  - Keys. B: the menu (channels, add/remove, browse a service), anywhere.
//    Watching a channel: ▲▼ change channel, ◄ last channel, A channel bar.
//    Watching anything else: A play/pause, ◄► 10 s back/forward.
//    Anywhere else the keys belong to the site, so you can browse it.
//
// The launcher re-runs this script whenever the list or the channel changes;
// the copy already running takes the new settings (see the end).
// ---------------------------------------------------------------------------

if (window.top !== window) return;

const BIND = COUCH.binding || 'couchLauncher';

function send(message) {
  try {
    const post = window[BIND];
    if (typeof post === 'function') post(JSON.stringify(message));
  } catch (e) { /* the launcher isn't listening */ }
}

let logged = 0;
function log(message) {
  // The launcher's log is how the far side of the room gets debugged; a
  // runaway loop shouldn't fill it.
  if (++logged <= 200) send({ t: 'log', msg: String(message) });
}

// These two have to be in place before the site's own code runs, so they're
// set up straight away, once per page, and hand over to whichever copy of
// the script is in charge.
if (!window.__couchEarly) {
  window.__couchEarly = true;

  // Which DRM the site asks for, and whether this browser has it.
  const original = navigator.requestMediaKeySystemAccess;
  if (typeof original === 'function') {
    const seen = new Set();
    navigator.requestMediaKeySystemAccess = function (keySystem) {
      const answer = original.apply(this, arguments);
      if (!seen.has(keySystem)) {
        seen.add(keySystem);
        answer.then(
          () => log(`DRM: ${location.host} asked for ${keySystem}: available`),
          (e) => log(`DRM: ${location.host} asked for ${keySystem}: not available (${e && e.name})`));
      }
      return answer;
    };
  }

  // Keys: first in line, ahead of the site's own handlers.
  const swallowed = new Set();
  window.addEventListener('keydown', (e) => {
    if (e.ctrlKey || e.altKey || e.metaKey || !window.__couchKey) return;
    let handled = false;
    try { handled = window.__couchKey(e); } catch (err) { log('key: ' + err); }
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
}

// More than one copy can be registered for a page while the launcher swaps
// in new settings: only the newest starts, once the others have had their go.
window.__couchVersion = Math.max(window.__couchVersion || 0, COUCH.version || 0);
setTimeout(() => {
  if (window.__couchVersion !== (COUCH.version || 0)) return;
  if (window.__couchUpdate) window.__couchUpdate(COUCH);
  else start(COUCH);
}, 0);

function start(settings) {

let S = settings;
const FG = S.fg || '#00FF66';
const BG = S.bg || '#000000';
const TUNING_MS = 60000;
const START_GIVE_UP_MS = 15000;

// ------------------------------------------------------------- addresses

function absolute(url) {
  try { return new URL(url, location.href); } catch (e) { return null; }
}

/** Same page: same site and path, and every part of the saved address's
 *  query (7plus-style ?channel=…) still there. */
function isPage(saved, here = location.href) {
  const a = absolute(saved), b = absolute(here);
  if (!a || !b || a.host.replace(/^www\./, '') !== b.host.replace(/^www\./, '')) return false;
  if (a.pathname.replace(/\/$/, '') !== b.pathname.replace(/\/$/, '')) return false;
  for (const [key, value] of a.searchParams) if (b.searchParams.get(key) !== value) return false;
  return true;
}

// ---------------------------------------------------------------- channels

const channels = () => S.channels || [];

/** Index of the channel on screen, or -1. */
function current() {
  const byPage = channels().findIndex((c) => isPage(c.url));
  if (byPage >= 0) return byPage;
  // Just changed channel, and the site moved the address on (a redirect, a
  // player page): still that channel, as long as it's the same site.
  const tuned = channels()[S.current];
  if (tuned && Date.now() - (S.tunedAt || 0) < TUNING_MS &&
      absolute(tuned.url) && absolute(tuned.url).host === location.host) return S.current;
  return -1;
}

function tune(index) {
  const channel = channels()[index];
  if (!channel) return;
  showBar(index, 'TUNING…', 0);
  log(`change to ${channel.name}`);
  send({ t: 'tune', index });
}

function cleanTitle() {
  const parts = (document.title || '').split(/\s+[|–—:-]\s+/).map((p) => p.trim()).filter(Boolean);
  const site = location.host.replace(/^www\./, '').split('.')[0];
  const useful = parts.filter((p) => !p.toLowerCase().replace(/\s/g, '').includes(site.toLowerCase()));
  return (useful[0] || parts[0] || location.host)
    .replace(/\b(watch|live\s*stream|stream(ing)?|online|for free|free|now)\b/gi, ' ')
    .replace(/\blive\b/gi, ' ')
    .replace(/\s+/g, ' ')
    .trim()
    .toUpperCase()
    .slice(0, 28) || location.host.toUpperCase();
}

function addThis() {
  const at = channels().findIndex((c) => isPage(c.url));
  if (at >= 0) {
    toast(`ALREADY CHANNEL ${String(at + 1).padStart(2, '0')}`);
    return;
  }
  const name = cleanTitle();
  send({ t: 'add', name, url: location.href });
  toast(`ADDED ${name} — RENAME IT IN SETTINGS › EDIT TILES`, 8);
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

const isPlaying = (v) => !!v && !v.paused && !v.ended && v.readyState >= 2;
const isLive = (v) => current() >= 0 || (!!v && v.duration === Infinity);

function bigOnScreen(v) {
  const r = v.getBoundingClientRect();
  return r.width * r.height > 0.5 * innerWidth * innerHeight;
}

function visible(el) {
  const r = el.getBoundingClientRect();
  return r.width > 0 && r.height > 0;
}

/** A play / watch button the site wants pressed before it plays. */
function findPlay() {
  const words = /\b(play|watch live|watch now|start watching|resume|watch)\b/i;
  const candidates = [...document.querySelectorAll('button, [role="button"], a')].filter(visible);
  return candidates.find((el) =>
    words.test(el.getAttribute('aria-label') || '') ||
    words.test(el.getAttribute('title') || '') ||
    words.test((el.textContent || '').trim().slice(0, 30))) ||
    candidates.find((el) => /play/i.test(String(el.className || ''))) || null;
}

/** The "are you still watching?" button, if one's up. */
function stillWatching() {
  for (const b of document.querySelectorAll('button, [role="button"]')) {
    const text = (b.textContent || b.getAttribute('aria-label') || '').trim();
    if (/^(continue( watching)?|keep watching|yes,? (i'm|i am) (still )?(here|watching)|i'm (still )?here|still watching)$/i.test(text)) return b;
  }
  return null;
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
    if (!on || (old !== v && !(v && old.contains(v)))) old.classList.remove('couch-tv-video', 'couch-tv-path');
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
    log(`TV look ${on ? 'on' : 'off'}: ${location.host}${location.pathname}`);
  }
}

let ownScreen = '';     // the page the site's own screen was asked for on

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
    const panel = `background: ${rgba(BG, 0.95)}; border: 3px solid ${FG};
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
        .menu { position: absolute; top: 7vh; bottom: 7vh; left: 5vw; width: 42vw; padding: 3.5vh 2vw; ${panel}
                display: none; flex-direction: column; }
        .menu.on { display: flex; }
        .title { font-size: 2.6vh; opacity: .6; letter-spacing: .25em; margin: 0 0 2.4vh .6vw; font-weight: bold; }
        .items { flex: 1; overflow: hidden; }
        .item { font-size: 3.3vh; padding: 1.1vh 1.2vw; margin-bottom: .8vh; border: 2px solid transparent;
                white-space: nowrap; overflow: hidden; text-overflow: ellipsis; text-transform: uppercase; }
        .item.sel { border-color: ${FG}; background: ${rgba(FG, 0.16)}; box-shadow: 0 0 20px ${rgba(FG, 0.5)}; font-weight: bold; }
        .item.extra { opacity: .75; font-size: 2.7vh; }
        .item.note { opacity: .6; font-size: 2.4vh; white-space: normal; }
        .item.gap { margin-top: 2.2vh; }
        .item .now { opacity: .6; font-size: 2.2vh; }
        .hint { font-size: 2.2vh; opacity: .55; margin-top: 2vh; }
        .toast { position: absolute; top: 6vh; right: 5vw; max-width: 46vw; padding: 1.6vh 2vw; ${panel}
                 font-size: 2.8vh; opacity: 0; transition: opacity .2s; text-transform: uppercase; }
        .toast.on { opacity: 1; }
      </style>
      <div class="wrap">
        <div class="bar"><div class="num"></div><div><div class="name"></div><div class="sub"></div></div><div class="clock"></div></div>
        <div class="menu"><div class="title"></div><div class="items"></div>
          <div class="hint">▲▼ MOVE &nbsp; A CHOOSE &nbsp; B CLOSE</div></div>
        <div class="toast"></div>
      </div>`;
    ui.host = host;
    for (const name of ['bar', 'num', 'name', 'sub', 'clock', 'menu', 'title', 'items', 'toast']) {
      ui[name] = shadow.querySelector('.' + name);
    }
  }
  // On <html>, not <body>: outside the part of the page that gets hidden,
  // and out of the way of the site redrawing its own content.
  root.appendChild(ui.host);
  return true;
}

let barTimer = 0;
function showBar(index, status, seconds = 5) {
  if (!buildUi()) return;
  const channel = channels()[index];
  ui.num.textContent = channel ? String(index + 1).padStart(2, '0') : '--';
  ui.name.textContent = channel ? channel.name : cleanTitle();
  const title = cleanTitle();
  const extra = channel && title !== channel.name.toUpperCase() ? title : '';
  ui.sub.textContent = [status, extra].filter(Boolean).join('  ·  ');
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

// -------------------------------------------------------------------- menu

const menu = { open: false, entries: [], at: 0, title: '' };

function mainMenu() {
  const now = current();
  const entries = channels().map((c, i) => ({
    label: `${String(i + 1).padStart(2, '0')}  ${c.name}`, now: i === now, run: () => tune(i),
  }));
  if (!entries.length) {
    entries.push({ note: true, label: 'NO CHANNELS YET. BROWSE A SERVICE, START A LIVE CHANNEL PLAYING, THEN PRESS B AND ADD IT.' });
  }
  const extras = [];
  if (now >= 0) {
    extras.push({ label: `✕ REMOVE ${channels()[now].name}`, run: () => confirmRemove(now) });
  } else {
    extras.push({ label: '+ ADD THIS CHANNEL', run: addThis });
  }
  extras.push({ label: 'BROWSE A SERVICE…', run: () => openMenu(servicesMenu(), 'BROWSE') });
  if (history.length > 1) extras.push({ label: '◄ PREVIOUS PAGE', run: () => history.back() });
  if (tvOn) extras.push({ label: "THE SITE'S OWN SCREEN", run: useOwnScreen });
  extras[0].gap = true;
  for (const e of extras) e.extra = true;
  return [...entries, ...extras];
}

function servicesMenu() {
  return [
    ...(S.services || []).map((s) => ({ label: s.name, run: () => { log(`browse ${s.name}`); location.assign(s.url); } })),
    { label: '◄ BACK', extra: true, gap: true, keepOpen: true, run: () => openMenu(mainMenu(), S.tile || 'LIVE SURFER') },
  ];
}

function confirmRemove(index) {
  const channel = channels()[index];
  openMenu([
    { label: `YES, REMOVE ${channel.name}`, run: () => { send({ t: 'remove', index }); toast(`REMOVED ${channel.name}`); } },
    { label: 'NO', run: () => {} },
  ], `REMOVE CHANNEL ${String(index + 1).padStart(2, '0')}?`);
}

function openMenu(entries, title) {
  if (!buildUi()) return;
  menu.entries = entries;
  menu.title = title;
  menu.at = Math.max(0, entries.findIndex((e) => e.now));
  while (menu.entries[menu.at] && menu.entries[menu.at].note) menu.at++;
  menu.open = true;
  ui.title.textContent = title;
  ui.menu.classList.add('on');
  ui.bar.classList.remove('on');
  paintMenu();
}

function closeMenu() {
  menu.open = false;
  if (ui.menu) ui.menu.classList.remove('on');
}

function paintMenu() {
  ui.items.textContent = '';
  menu.entries.forEach((entry, i) => {
    const row = document.createElement('div');
    row.className = 'item' + (i === menu.at ? ' sel' : '') + (entry.extra ? ' extra' : '') +
      (entry.note ? ' note' : '') + (entry.gap ? ' gap' : '');
    row.textContent = entry.label;
    if (entry.now) {
      const now = document.createElement('span');
      now.className = 'now';
      now.textContent = '   ◄ ON NOW';
      row.appendChild(now);
    }
    ui.items.appendChild(row);
    if (i === menu.at) requestAnimationFrame(() => row.scrollIntoView({ block: 'nearest' }));
  });
}

function moveMenu(step) {
  let at = menu.at;
  do { at += step; } while (menu.entries[at] && menu.entries[at].note);
  if (at >= 0 && at < menu.entries.length) menu.at = at;
  paintMenu();
}

function chooseFromMenu() {
  const entry = menu.entries[menu.at];
  if (!entry || entry.note) return;
  if (!entry.keepOpen) closeMenu();
  entry.run();
}

/** The site's own page, untouched, until you leave it. */
function useOwnScreen() {
  ownScreen = location.href;
  setTv(false);
  toast("THE SITE'S OWN SCREEN, UNTIL YOU LEAVE THIS PAGE");
}

// --------------------------------------------------------------- the keys

const zap = { index: -1, timer: 0 };

function zapBy(step) {
  const count = channels().length;
  if (!count) return;
  if (!zap.timer) zap.index = current();
  zap.index = zap.index < 0 ? (step > 0 ? 0 : count - 1) : (zap.index + step + count) % count;
  const target = zap.index;
  showBar(target, 'CHANGING…', 0);
  clearTimeout(zap.timer);
  // A short wait, so a few quick presses skip channels instead of loading each.
  zap.timer = setTimeout(() => {
    zap.timer = 0;
    tune(target);
  }, 900);
}

function lastChannel() {
  if (channels()[S.previous] && S.previous !== current()) tune(S.previous);
  else toast('NO LAST CHANNEL YET');
}

function typing() {
  const el = document.activeElement;
  return !!el && (el.isContentEditable || /^(input|textarea|select)$/i.test(el.tagName));
}

/** What a key does right now; false = leave it to the site. */
window.__couchKey = (e) => {
  const key = e.key;
  if (menu.open) {
    switch (key) {
      case 'ArrowUp': moveMenu(-1); return true;
      case 'ArrowDown': moveMenu(1); return true;
      case 'Enter': case ' ': chooseFromMenu(); return true;
      case 'Escape': case 'Backspace': closeMenu(); return true;
      case 'ArrowLeft': case 'ArrowRight': return true;
    }
    return false;
  }
  if (key === 'Escape' && !typing()) {
    openMenu(mainMenu(), S.tile || 'LIVE SURFER');
    return true;
  }
  if (!tvOn) return false;
  const v = pickVideo();
  if (isLive(v)) {
    const now = current();
    switch (key) {
      case 'ArrowUp': zapBy(-1); return channels().length > 0;
      case 'ArrowDown': zapBy(1); return channels().length > 0;
      case 'ArrowLeft': lastChannel(); return true;
      case 'ArrowRight': showBar(now, 'LIVE'); return true;
      case 'Enter': case ' ':
        if (v && v.paused) v.play().catch(() => {});
        showBar(now, 'LIVE');
        return true;
    }
    return false;
  }
  switch (key) {
    case 'Enter': case ' ':
      if (v) {
        if (v.paused) v.play().catch(() => {});
        else v.pause();
        showBar(-1, v.paused ? 'PAUSED' : 'PLAYING', v.paused ? 0 : 2);
      }
      return true;
    case 'ArrowLeft': if (v) v.currentTime = Math.max(0, v.currentTime - 10); return true;
    case 'ArrowRight': if (v) v.currentTime = v.currentTime + 10; return true;
    case 'ArrowUp': showBar(-1, ''); return true;
  }
  return false;
};

// ----------------------------------------------------------- every half second

let lastHref = '';
let pageAt = Date.now();
let startTries = 0;
let announced = false;

function tick() {
  if (location.href !== lastHref) {
    lastHref = location.href;
    pageAt = Date.now();
    startTries = 0;
    announced = false;
  }
  if (!document.body) return;
  buildUi();

  const v = pickVideo();
  if (v !== video) {
    video = v;
    if (v) log(`video found on ${location.host}${location.pathname} (duration ${v.duration})`);
  }
  const now = current();
  const onChannel = now >= 0;
  const starting = onChannel && Date.now() - pageAt < START_GIVE_UP_MS;
  const wanted = !!v && ownScreen !== location.href &&
    (isPlaying(v) && (bigOnScreen(v) || onChannel) || starting);
  setTv(wanted, v);

  if (onChannel) {
    if (isPlaying(v)) {
      if (!announced) {
        announced = true;
        showBar(now, 'LIVE');
        log(`${channels()[now].name} is playing`);
      }
    } else if (starting) {
      if (!announced) showBar(now, 'TUNING…', 0);
      startChannel(v, now);
    } else if (startTries < 100) {
      startTries = 100;
      log(`${channels()[now].name} didn't start playing on ${location.host}${location.pathname}`);
      if (ui.bar) ui.bar.classList.remove('on');
      toast('NOT PLAYING YET — START IT ON SCREEN, OR PRESS B FOR THE MENU', 10);
    }
  }

  if (tvOn) {
    const button = stillWatching();
    if (button) {
      log('answered "still watching?"');
      button.click();
    }
  }
}

/** On a channel that isn't playing: give it a nudge, every 1.5 s or so. */
function startChannel(v, index) {
  if (++startTries % 3 !== 0) return;
  if (v && v.readyState >= 2 && v.paused) {
    v.play().catch(() => {});
    return;
  }
  if (Date.now() - pageAt < 4000) return;      // let the site start it itself first
  const button = findPlay();
  if (button) {
    log(`starting ${channels()[index].name}: pressed ${button.tagName} "${(button.getAttribute('aria-label') || button.textContent || '').trim().slice(0, 30)}"`);
    button.click();
  }
}

/** New settings from the launcher (a channel added, removed or changed). */
window.__couchUpdate = (settings) => {
  S = settings;
  if (menu.open && menu.title === (S.tile || 'LIVE SURFER')) openMenu(mainMenu(), menu.title);
};

setInterval(() => {
  try { tick(); } catch (e) { log('tick: ' + ((e && e.stack) || e)); }
}, 500);
log(`ready on ${location.host}${location.pathname}`);
}
