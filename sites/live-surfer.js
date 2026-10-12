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
//  - Keys. B: the menu (channels, guide, add/remove/rename, browse a
//    service), anywhere. Watching a channel: ◄► change channel, ▲▼ volume,
//    A what's on (A again: the TV guide). Watching anything else: A
//    play/pause, ◄► 10 s back/forward, ▲▼ volume. Anywhere else the keys
//    belong to the site, so you can browse it.
//  - What's on comes from the TV guide the launcher downloads; it sends this
//    tile's channels' shows when asked (__couchGuide).
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
  toast(`ADDED ${name} — TO CHANGE ITS NAME: B, THEN RENAME`, 8);
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

// ---------------------------------------------------------------- TV guide

let guide = null;            // { ok, channels: [{ guide, shows: [{start, stop, title, episode, desc}] }] }
let guideAskedAt = 0;

function askForGuide(force) {
  if (!force && Date.now() - guideAskedAt < 10 * 60000) return;
  guideAskedAt = Date.now();
  send({ t: 'guide' });
}

/** A channel's shows, from the guide. */
const showsFor = (index) => (guide && guide.channels[index] && guide.channels[index].shows) || [];

function showAt(index, time = Date.now()) {
  return showsFor(index).find((p) => p.start <= time && time < p.stop) || null;
}

function showAfter(index, time = Date.now()) {
  return showsFor(index).find((p) => p.start > time) || null;
}

function clock(ms) {
  return new Date(ms).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' }).replace(/\s/g, '').toUpperCase();
}

function untilText(ms) {
  const minutes = Math.round((ms - Date.now()) / 60000);
  if (minutes < 60) return `IN ${Math.max(1, minutes)} MIN`;
  return `AT ${clock(ms)}`;
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
    const panel = `background: ${rgba(BG, 0.95)}; border: 3px solid ${FG};
      box-shadow: 0 0 28px ${rgba(FG, 0.45)}, inset 0 0 40px ${rgba(FG, 0.08)};
      background-image: repeating-linear-gradient(0deg, rgba(0,0,0,0.33) 0 1px, transparent 1px 3px);`;
    shadow.innerHTML = `
      <style>
        * { box-sizing: border-box; }
        .wrap { position: fixed; inset: 0; font-family: Consolas, "Courier New", monospace; color: ${FG};
                text-shadow: 0 0 10px ${rgba(FG, 0.6)}; pointer-events: none; }
        .on { opacity: 1 !important; }
        .shown { display: flex !important; }

        .bar { position: absolute; left: 5vw; right: 5vw; bottom: 6vh; padding: 2.4vh 3vw; ${panel}
               display: flex; align-items: center; gap: 3vw; opacity: 0; transition: opacity .2s; }
        .num { font-size: 7vh; font-weight: bold; min-width: 9vh; }
        .mid { flex: 1; min-width: 0; }
        .name { font-size: 3vh; font-weight: bold; opacity: .8; text-transform: uppercase; }
        .now { font-size: 4.4vh; font-weight: bold; margin-top: .6vh; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .prog { height: .8vh; margin-top: 1.2vh; border: 1px solid ${rgba(FG, 0.6)}; }
        .prog div { height: 100%; background: ${FG}; }
        .sub { font-size: 2.4vh; opacity: .75; margin-top: 1vh; text-transform: uppercase; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .right { text-align: right; }
        .clock { font-size: 4.6vh; font-weight: bold; }
        .hintA { font-size: 2vh; opacity: .55; margin-top: 1vh; }

        .vol { position: absolute; top: 6vh; right: 5vw; padding: 1.6vh 2vw; ${panel} opacity: 0; transition: opacity .2s;
               display: flex; align-items: center; gap: 1.4vw; font-size: 3vh; font-weight: bold; }
        .vol .cells { display: flex; gap: .3vw; }
        .vol .cell { width: .9vw; height: 3vh; border: 1px solid ${rgba(FG, 0.5)}; }
        .vol .cell.lit { background: ${FG}; box-shadow: 0 0 8px ${FG}; }

        .menu { position: absolute; top: 7vh; bottom: 7vh; left: 5vw; width: 46vw; padding: 3.5vh 2vw; ${panel}
                display: none; flex-direction: column; }
        .title { font-size: 2.6vh; opacity: .6; letter-spacing: .25em; margin: 0 0 2.4vh .6vw; font-weight: bold; }
        .items { flex: 1; overflow: hidden; }
        .item { font-size: 3.3vh; padding: 1.1vh 1.2vw; margin-bottom: .8vh; border: 2px solid transparent;
                white-space: nowrap; overflow: hidden; text-overflow: ellipsis; text-transform: uppercase; }
        .item.sel { border-color: ${FG}; background: ${rgba(FG, 0.16)}; box-shadow: 0 0 20px ${rgba(FG, 0.5)}; font-weight: bold; }
        .item.extra { opacity: .75; font-size: 2.7vh; }
        .item.note { opacity: .6; font-size: 2.4vh; white-space: normal; }
        .item.gap { margin-top: 2.2vh; }
        .item .tag { opacity: .6; font-size: 2.2vh; font-weight: normal; }
        .hint { font-size: 2.2vh; opacity: .55; margin-top: 2vh; }

        .toast { position: absolute; top: 16vh; left: 50%; transform: translateX(-50%); max-width: 60vw; padding: 1.6vh 2vw; ${panel}
                 font-size: 2.8vh; opacity: 0; transition: opacity .2s; text-transform: uppercase; }

        .rename { position: absolute; top: 14vh; left: 50%; transform: translateX(-50%); width: 64vw; padding: 3.5vh 3vw; ${panel}
                  display: none; flex-direction: column; }
        .field { font-size: 4.6vh; font-weight: bold; padding: 1.4vh 1.2vw; border: 2px solid ${rgba(FG, 0.6)}; margin-bottom: 3vh; min-height: 8vh; }
        .field .caret { animation: blink 1s steps(1) infinite; }
        @keyframes blink { 50% { opacity: 0; } }
        .keys { display: grid; grid-template-columns: repeat(10, 1fr); gap: .8vh .6vw; }
        .key { font-size: 3.4vh; text-align: center; padding: 1.2vh 0; border: 2px solid ${rgba(FG, 0.3)}; }
        .key.wide { grid-column: span 4; }
        .key.mid2 { grid-column: span 2; }
        .key.sel { border-color: ${FG}; background: ${rgba(FG, 0.2)}; box-shadow: 0 0 18px ${rgba(FG, 0.5)}; font-weight: bold; }

        .guide { position: absolute; inset: 0; display: none; flex-direction: column; }
        .gtop { height: 31vh; display: flex; padding: 4vh 4vw 2vh; }
        .gdetail { width: 56vw; padding: 2.4vh 2vw; ${panel} overflow: hidden; }
        .gchan { font-size: 2.4vh; opacity: .7; letter-spacing: .1em; }
        .gtitle { font-size: 4.2vh; font-weight: bold; margin-top: .8vh; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .gwhen { font-size: 2.5vh; margin-top: .8vh; }
        .gdesc { font-size: 2.2vh; opacity: .75; margin-top: 1vh; line-height: 1.35; max-height: 9vh; overflow: hidden; }
        .ggrid { flex: 1; ${panel} border-width: 3px 0 0 0; padding: 1.6vh 2vw 2vh; display: flex; flex-direction: column; }
        .ghead { display: flex; height: 4vh; font-size: 2.3vh; opacity: .7; }
        .gday { width: 16vw; font-weight: bold; }
        .gtimes { position: relative; flex: 1; }
        .gtimes span { position: absolute; top: 0; padding-left: .4vw; border-left: 1px solid ${rgba(FG, 0.4)}; }
        .grows { position: relative; flex: 1; overflow: hidden; }
        .grow { display: flex; height: 7.4vh; margin-bottom: .6vh; }
        .gname { width: 16vw; font-size: 2.6vh; font-weight: bold; display: flex; align-items: center; padding-left: .4vw;
                 white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .gname.cur { text-decoration: underline; }
        .glane { position: relative; flex: 1; overflow: hidden; }
        .gshow { position: absolute; top: 0; bottom: 0; padding: 0 .7vw; border: 1px solid ${rgba(FG, 0.35)};
                 background: ${rgba(FG, 0.05)}; font-size: 2.4vh; display: flex; align-items: center;
                 white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .gshow.live { background: ${rgba(FG, 0.12)}; }
        .gshow.sel { border: 2px solid ${FG}; background: ${rgba(FG, 0.28)}; font-weight: bold; box-shadow: 0 0 18px ${rgba(FG, 0.5)}; z-index: 1; }
        .gnow { position: absolute; top: 0; bottom: 0; width: 2px; background: ${FG}; box-shadow: 0 0 10px ${FG}; z-index: 2; }
        .ghint { font-size: 2.1vh; opacity: .55; margin-top: 1vh; }
      </style>
      <div class="wrap">
        <div class="bar">
          <div class="num"></div>
          <div class="mid"><div class="name"></div><div class="now"></div><div class="prog"><div></div></div><div class="sub"></div></div>
          <div class="right"><div class="clock"></div><div class="hintA">A: TV GUIDE</div></div>
        </div>
        <div class="vol"><span class="vlabel">VOL</span><div class="cells"></div><span class="vnum"></span></div>
        <div class="menu"><div class="title"></div><div class="items"></div>
          <div class="hint">▲▼ MOVE &nbsp; A CHOOSE &nbsp; B CLOSE</div></div>
        <div class="rename"><div class="title rtitle"></div><div class="field"></div><div class="keys"></div>
          <div class="hint">D-PAD + A, OR TYPE ON A KEYBOARD &nbsp; B CANCEL</div></div>
        <div class="guide">
          <div class="gtop"><div class="gdetail"><div class="gchan"></div><div class="gtitle"></div><div class="gwhen"></div><div class="gdesc"></div></div></div>
          <div class="ggrid">
            <div class="ghead"><div class="gday"></div><div class="gtimes"></div></div>
            <div class="grows"></div>
            <div class="ghint">▲▼ CHANNEL &nbsp; ◄► TIME &nbsp; A WATCH &nbsp; B CLOSE</div>
          </div>
        </div>
        <div class="toast"></div>
      </div>`;
    ui.host = host;
    for (const name of ['bar', 'num', 'name', 'now', 'prog', 'sub', 'clock', 'hintA', 'vol', 'cells', 'vnum', 'vlabel',
      'menu', 'title', 'items', 'toast', 'rename', 'rtitle', 'field', 'keys',
      'guide', 'gchan', 'gtitle', 'gwhen', 'gdesc', 'gday', 'gtimes', 'grows']) {
      ui[name] = shadow.querySelector('.' + name);
    }
  }
  // On <html>, not <body>: outside the part of the page that gets hidden,
  // and out of the way of the site redrawing its own content.
  root.appendChild(ui.host);
  return true;
}

// ------------------------------------------------------------- channel bar

const BAR_SECONDS = 3;
let barTimer = 0;
let barFor = -2;          // the channel the bar is showing (-1: not a channel), -2: hidden

function barShowing() {
  return !!ui.bar && ui.bar.classList.contains('on');
}

function hideBar() {
  if (ui.bar) ui.bar.classList.remove('on');
  barFor = -2;
}

/** The channel bar: channel, what's on with how far through, what's next. */
function showBar(index, status, seconds = BAR_SECONDS) {
  if (!buildUi()) return;
  const channel = channels()[index];
  ui.num.textContent = channel ? String(index + 1).padStart(2, '0') : '--';
  ui.name.textContent = channel ? channel.name : cleanTitle();
  const show = channel ? showAt(index) : null;
  const next = channel ? showAfter(index) : null;
  ui.now.textContent = show ? show.title + (show.episode ? ` · ${show.episode}` : '') : (channel ? cleanTitle() : '');
  if (channel && ui.now.textContent.toUpperCase() === channel.name.toUpperCase()) ui.now.textContent = '';
  ui.prog.style.display = show ? '' : 'none';
  if (show) ui.prog.firstElementChild.style.width = `${Math.round(100 * (Date.now() - show.start) / (show.stop - show.start))}%`;
  const parts = [status];
  if (show) parts.push(`${clock(show.start)}–${clock(show.stop)}`);
  if (next) parts.push(`NEXT ${clock(next.start)} ${next.title}`);
  ui.sub.textContent = parts.filter(Boolean).join('  ·  ');
  ui.clock.textContent = new Date().toTimeString().slice(0, 5);
  ui.hintA.style.display = channel ? '' : 'none';
  ui.bar.classList.add('on');
  barFor = channel ? index : -1;
  clearTimeout(barTimer);
  if (seconds > 0) barTimer = setTimeout(hideBar, seconds * 1000);
}

let toastTimer = 0;
function toast(text, seconds = 6) {
  if (!buildUi()) return;
  ui.toast.textContent = text;
  ui.toast.classList.add('on');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => ui.toast.classList.remove('on'), seconds * 1000);
}

// ------------------------------------------------------------------ volume

let volTimer = 0;
function changeVolume(step) {
  send({ t: 'volume', step });
}

/** The launcher's answer: the PC's volume now. */
window.__couchVolume = (percent, muted) => {
  if (!buildUi()) return;
  ui.cells.textContent = '';
  const lit = muted ? 0 : Math.round(percent / 5);
  for (let i = 0; i < 20; i++) {
    const cell = document.createElement('div');
    cell.className = 'cell' + (i < lit ? ' lit' : '');
    ui.cells.appendChild(cell);
  }
  ui.vlabel.textContent = muted ? 'MUTED' : 'VOL';
  ui.vnum.textContent = String(percent);
  ui.vol.classList.add('on');
  clearTimeout(volTimer);
  volTimer = setTimeout(() => ui.vol.classList.remove('on'), 2000);
};

// -------------------------------------------------------------------- menu

const menu = { open: false, entries: [], at: 0, title: '' };

function mainMenu() {
  const now = current();
  const entries = channels().map((c, i) => {
    const show = showAt(i);
    return { label: `${String(i + 1).padStart(2, '0')}  ${c.name}`, tag: show ? show.title : '', now: i === now, run: () => tune(i) };
  });
  if (!entries.length) {
    entries.push({ note: true, label: 'NO CHANNELS YET. BROWSE A SERVICE, START A LIVE CHANNEL PLAYING, THEN PRESS B AND ADD IT.' });
  }
  const extras = [];
  if (channels().length) extras.push({ label: 'TV GUIDE', run: openGuide });
  if (now >= 0) {
    extras.push({ label: `✎ RENAME ${channels()[now].name}`, run: () => openRename(now) });
    extras.push({ label: `✕ REMOVE ${channels()[now].name}`, run: () => confirmRemove(now) });
  } else {
    extras.push({ label: '+ ADD THIS CHANNEL', run: addThis });
  }
  const last = channels()[S.previous];
  if (last && S.previous !== now) extras.push({ label: `↺ LAST CHANNEL: ${last.name}`, run: () => tune(S.previous) });
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
  askForGuide(false);
  menu.entries = entries;
  menu.title = title;
  menu.at = Math.max(0, entries.findIndex((e) => e.now));
  while (menu.entries[menu.at] && menu.entries[menu.at].note) menu.at++;
  menu.open = true;
  ui.title.textContent = title;
  ui.menu.classList.add('shown');
  hideBar();
  paintMenu();
}

function closeMenu() {
  menu.open = false;
  if (ui.menu) ui.menu.classList.remove('shown');
}

function paintMenu() {
  ui.items.textContent = '';
  menu.entries.forEach((entry, i) => {
    const row = document.createElement('div');
    row.className = 'item' + (i === menu.at ? ' sel' : '') + (entry.extra ? ' extra' : '') +
      (entry.note ? ' note' : '') + (entry.gap ? ' gap' : '');
    row.textContent = entry.label;
    const tags = [entry.now ? '◄ ON NOW' : '', entry.tag || ''].filter(Boolean).join('  ·  ');
    if (tags) {
      const tag = document.createElement('span');
      tag.className = 'tag';
      tag.textContent = '   ' + tags;
      row.appendChild(tag);
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

// ------------------------------------------------------------------ rename

const KEYS = [
  ...'ABCDEFGHIJKLMNOPQRSTUVWXYZ'.split(''), '&', '+', '-', "'",
  ...'1234567890'.split(''),
  { label: 'SPACE', put: ' ', cls: 'wide' }, { label: '⌫', del: true, cls: 'mid2' },
  { label: 'CLEAR', clear: true, cls: 'mid2' }, { label: 'DONE', done: true, cls: 'mid2' },
].map((k) => (typeof k === 'string' ? { label: k, put: k } : k));
// Where each key sits on the 10-wide grid, for moving with the D-pad.
const KEY_SPOTS = (() => {
  let col = 0, row = 0;
  return KEYS.map((k) => {
    const width = k.cls === 'wide' ? 4 : k.cls === 'mid2' ? 2 : 1;
    if (col + width > 10) { col = 0; row++; }
    const spot = { row, col, width };
    col += width;
    return spot;
  });
})();

const rename = { open: false, index: -1, text: '', at: KEYS.length - 1 };

function openRename(index) {
  if (!buildUi() || !channels()[index]) return;
  rename.open = true;
  rename.index = index;
  rename.text = channels()[index].name;
  rename.at = KEYS.length - 1;      // DONE: typing on a keyboard then A/Enter saves
  ui.rtitle.textContent = `RENAME CHANNEL ${String(index + 1).padStart(2, '0')}`;
  ui.rename.classList.add('shown');
  hideBar();
  paintRename();
}

function closeRename() {
  rename.open = false;
  if (ui.rename) ui.rename.classList.remove('shown');
}

function paintRename() {
  ui.field.textContent = rename.text;
  const caret = document.createElement('span');
  caret.className = 'caret';
  caret.textContent = '█';
  ui.field.appendChild(caret);
  ui.keys.textContent = '';
  KEYS.forEach((k, i) => {
    const key = document.createElement('div');
    key.className = 'key' + (k.cls ? ' ' + k.cls : '') + (i === rename.at ? ' sel' : '');
    key.textContent = k.label;
    ui.keys.appendChild(key);
  });
}

function moveRename(dRow, dCol) {
  const here = KEY_SPOTS[rename.at];
  if (dCol) {
    const next = rename.at + dCol;
    if (next >= 0 && next < KEYS.length && KEY_SPOTS[next].row === here.row) rename.at = next;
  } else {
    const row = here.row + dRow;
    const middle = here.col + here.width / 2;
    let best = -1, bestGap = Infinity;
    KEY_SPOTS.forEach((spot, i) => {
      if (spot.row !== row) return;
      const gap = Math.abs(spot.col + spot.width / 2 - middle);
      if (gap < bestGap) { best = i; bestGap = gap; }
    });
    if (best >= 0) rename.at = best;
  }
  paintRename();
}

function pressRenameKey(key) {
  if (key.done) return saveRename();
  if (key.del) rename.text = rename.text.slice(0, -1);
  else if (key.clear) rename.text = '';
  else if (rename.text.length < 30) rename.text += key.put;
  paintRename();
}

function saveRename() {
  const name = rename.text.replace(/\s+/g, ' ').trim().toUpperCase();
  closeRename();
  if (!name) return toast('A CHANNEL NEEDS A NAME — NOT CHANGED');
  send({ t: 'rename', index: rename.index, name });
  toast(`RENAMED TO ${name}`, 3);
}

function renameKey(e) {
  switch (e.key) {
    case 'ArrowUp': moveRename(-1, 0); return true;
    case 'ArrowDown': moveRename(1, 0); return true;
    case 'ArrowLeft': moveRename(0, -1); return true;
    case 'ArrowRight': moveRename(0, 1); return true;
    case 'Enter': pressRenameKey(KEYS[rename.at]); return true;
    case 'Escape': closeRename(); return true;
    case 'Backspace': rename.text = rename.text.slice(0, -1); paintRename(); return true;
  }
  if (e.key.length === 1) {
    if (rename.text.length < 30) rename.text += e.key.toUpperCase();
    paintRename();
    return true;
  }
  return true;     // nothing reaches the site while renaming
}

// ---------------------------------------------------------------- TV guide

const SLOT = 30 * 60000;          // the grid's columns: half hours
const SPAN = 4 * SLOT;            // two hours across the screen
const ROWS = 6;                   // channel rows on screen at once
const g = { open: false, row: 0, show: 0, from: 0, top: 0 };

const floorSlot = (ms) => Math.floor(ms / SLOT) * SLOT;

/** A row's shows, or one "no guide" block so it can still be picked. */
function laneFor(row) {
  const shows = showsFor(row);
  if (shows.length) return shows;
  return [{ start: Date.now() - 12 * 3600e3, stop: Date.now() + 36 * 3600e3, title: guide ? 'NO GUIDE FOR THIS CHANNEL' : 'LOADING THE GUIDE…', none: true }];
}

function openGuide() {
  if (!buildUi() || !channels().length) return;
  askForGuide(false);
  closeMenu();
  hideBar();
  g.open = true;
  g.row = Math.max(0, current());
  g.from = floorSlot(Date.now());
  pickShowAt(Math.max(Date.now(), g.from));
  ui.guide.classList.add('shown');
  paintGuide();
}

function closeGuide() {
  g.open = false;
  if (ui.guide) ui.guide.classList.remove('shown');
}

/** On the current row, select the show running at (or next after) this time. */
function pickShowAt(time) {
  const lane = laneFor(g.row);
  let at = lane.findIndex((p) => p.start <= time && time < p.stop);
  if (at < 0) at = lane.findIndex((p) => p.start > time);
  if (at < 0) at = lane.length - 1;
  g.show = Math.max(0, at);
}

function selected() {
  const lane = laneFor(g.row);
  return lane[Math.min(g.show, lane.length - 1)];
}

function guideKey(e) {
  const lane = laneFor(g.row);
  switch (e.key) {
    case 'ArrowUp':
    case 'ArrowDown': {
      const step = e.key === 'ArrowUp' ? -1 : 1;
      const row = g.row + step;
      if (row < 0 || row >= channels().length) return true;
      const focus = Math.max(selected().start, g.from);
      g.row = row;
      pickShowAt(focus);
      break;
    }
    case 'ArrowLeft': {
      const earliest = floorSlot(Date.now()) - 2 * SLOT;    // an hour back at most
      if (g.show > 0 && lane[g.show - 1].stop > earliest) {
        g.show--;
        if (selected().start < g.from) g.from = Math.max(earliest, floorSlot(selected().start));
      } else if (g.from > earliest) {
        g.from -= SLOT;
      }
      break;
    }
    case 'ArrowRight':
      if (g.show < lane.length - 1) {
        g.show++;
        if (selected().start >= g.from + SPAN - SLOT / 2) g.from = floorSlot(selected().start);
      } else if (g.from < Date.now() + 20 * 3600e3) {
        g.from += SLOT;
      }
      break;
    case 'Enter': case ' ': {
      const show = selected();
      if (show.none || (show.start <= Date.now() && Date.now() < show.stop)) {
        closeGuide();
        if (g.row !== current()) tune(g.row);
        else showBar(g.row, 'LIVE');
      } else {
        toast(`${show.title} IS ON ${untilText(show.start)}`, 4);
      }
      return true;
    }
    case 'Escape': case 'Backspace': closeGuide(); return true;
    default: return true;
  }
  paintGuide();
  return true;
}

function paintGuide() {
  const now = Date.now();
  const show = selected();
  const channel = channels()[g.row];
  ui.gchan.textContent = `${String(g.row + 1).padStart(2, '0')}  ${channel.name}` +
    (guide && guide.channels[g.row] && guide.channels[g.row].guide ? `   ·   GUIDE: ${guide.channels[g.row].guide.toUpperCase()}` : '');
  ui.gtitle.textContent = show.title + (show.episode ? ` · ${show.episode}` : '');
  ui.gwhen.textContent = show.none ? (guide && !guide.ok ? "THE GUIDE COULDN'T BE DOWNLOADED — SEE SETTINGS › EDIT TILES" : '')
    : `${clock(show.start)} – ${clock(show.stop)}   ·   ` +
      (show.start <= now && now < show.stop ? `ON NOW, ${Math.round((show.stop - now) / 60000)} MIN LEFT`
        : show.stop <= now ? 'FINISHED' : `STARTS ${untilText(show.start)}`);
  ui.gdesc.textContent = show.desc || '';

  ui.gday.textContent = new Date(g.from).toLocaleDateString([], { weekday: 'short', day: 'numeric', month: 'short' }).toUpperCase();
  ui.gtimes.textContent = '';
  for (let i = 0; i < SPAN / SLOT; i++) {
    const label = document.createElement('span');
    label.style.left = `${(100 * i * SLOT) / SPAN}%`;
    label.textContent = clock(g.from + i * SLOT);
    ui.gtimes.appendChild(label);
  }

  if (g.row < g.top) g.top = g.row;
  if (g.row >= g.top + ROWS) g.top = g.row - ROWS + 1;
  const playing = current();
  ui.grows.textContent = '';
  for (let row = g.top; row < Math.min(channels().length, g.top + ROWS); row++) {
    const line = document.createElement('div');
    line.className = 'grow';
    const name = document.createElement('div');
    name.className = 'gname' + (row === playing ? ' cur' : '');
    name.textContent = `${String(row + 1).padStart(2, '0')} ${channels()[row].name}`;
    line.appendChild(name);
    const lane = document.createElement('div');
    lane.className = 'glane';
    laneFor(row).forEach((p, i) => {
      if (p.stop <= g.from || p.start >= g.from + SPAN) return;
      const left = Math.max(0, (p.start - g.from) / SPAN);
      const right = Math.min(1, (p.stop - g.from) / SPAN);
      const block = document.createElement('div');
      block.className = 'gshow' + (p.start <= now && now < p.stop ? ' live' : '') + (row === g.row && i === g.show ? ' sel' : '');
      block.style.left = `${left * 100}%`;
      block.style.width = `calc(${(right - left) * 100}% - 2px)`;
      block.textContent = p.title;
      lane.appendChild(block);
    });
    if (now >= g.from && now < g.from + SPAN) {
      const mark = document.createElement('div');
      mark.className = 'gnow';
      mark.style.left = `${(100 * (now - g.from)) / SPAN}%`;
      lane.appendChild(mark);
    }
    line.appendChild(lane);
    ui.grows.appendChild(line);
  }
}

/** The launcher's answer to askForGuide. */
window.__couchGuide = (data) => {
  const focus = g.open ? Math.max(selected().start, g.from) : 0;
  guide = data;
  if (g.open) pickShowAt(focus);
  log(`guide: ${data.ok ? data.channels.filter((c) => c.shows.length).length + ' of ' + data.channels.length + ' channels have shows' : 'none'}`);
  if (g.open) paintGuide();
  if (menu.open && menu.title === (S.tile || 'LIVE SURFER')) { const at = menu.at; openMenu(mainMenu(), menu.title); menu.at = at; paintMenu(); }
  // Arrived just after the channel came on: show what's on now.
  const now = current();
  if (now >= 0 && Date.now() - announcedAt < 8000 && !menu.open && !g.open && !rename.open) showBar(now, 'LIVE');
};

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

function typing() {
  const el = document.activeElement;
  return !!el && (el.isContentEditable || /^(input|textarea|select)$/i.test(el.tagName));
}

/** What a key does right now; false = leave it to the site. */
window.__couchKey = (e) => {
  const key = e.key;
  if (rename.open) return renameKey(e);
  if (g.open) return guideKey(e);
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
  if (key === 'ArrowUp' || key === 'ArrowDown') {
    changeVolume(key === 'ArrowUp' ? 1 : -1);
    return true;
  }
  if (isLive(v)) {
    const now = current();
    switch (key) {
      case 'ArrowLeft': zapBy(-1); return channels().length > 0;
      case 'ArrowRight': zapBy(1); return channels().length > 0;
      case 'Enter': case ' ':
        if (v && v.paused) {
          v.play().catch(() => {});
          showBar(now, 'LIVE');
        } else if (now >= 0 && barShowing() && barFor === now) {
          openGuide();
        } else {
          showBar(now, 'LIVE');
          askForGuide(false);
        }
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
  }
  return false;
};

// ----------------------------------------------------------- every half second

let lastHref = '';
let pageAt = Date.now();
let startTries = 0;
let announced = false;
let announcedAt = 0;

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
        announcedAt = Date.now();
        // What's on, for a few seconds, as the channel comes on.
        if (!menu.open && !g.open && !rename.open) showBar(now, 'LIVE');
        log(`${channels()[now].name} is playing`);
      }
    } else if (starting) {
      if (!announced && !menu.open && !g.open && !rename.open && barFor !== now) showBar(now, 'TUNING…', 0);
      startChannel(v, now);
    } else if (startTries < 100) {
      startTries = 100;
      log(`${channels()[now].name} didn't start playing on ${location.host}${location.pathname}`);
      hideBar();
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
  if (g.open && Date.now() % 30000 < 500) paintGuide();      // keep the "now" line moving
  if (channels().length) askForGuide(false);
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

/** New settings from the launcher (a channel added, removed, renamed or changed). */
window.__couchUpdate = (settings) => {
  S = settings;
  if (menu.open && menu.title === (S.tile || 'LIVE SURFER')) openMenu(mainMenu(), menu.title);
  // A renamed or new channel can match a different guide channel.
  askForGuide(true);
};

setInterval(() => {
  try { tick(); } catch (e) { log('tick: ' + ((e && e.stack) || e)); }
}, 500);
log(`ready on ${location.host}${location.pathname}`);
}
