// ---------------------------------------------------------------------------
// Couch mode for ABC iview (iview.abc.net.au).
//
// Each live channel is a "show" on iview — /show/abc-live-stream is ABC TV —
// whose page has a button into the player. The first time a channel is
// watched, its page is opened and the button pressed; where that lands is
// remembered, so after that a channel change goes straight to the player.
//
// Written without access to iview (it only works from Australia), so the
// guesses about its pages are kept here, small and in one place, and every
// step is written to the launcher's log. If iview changes and something stops
// working, this is the file to fix.
// ---------------------------------------------------------------------------

const STATE_COPIES = /^abc-(act|nsw|nt|qld|sa|tas|vic|wa)-live-stream$/;

function channelName(label, id) {
  const cleaned = (label || '')
    .replace(/\b(watch|live\s*stream|live|now|play)\b/gi, ' ')
    .replace(/[^\w&+' -]/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
  if (cleaned.length >= 2 && cleaned.length <= 30) return cleaned.toUpperCase();
  return id.replace(/-?live-stream$/, '').replace(/-/g, ' ').toUpperCase();
}

function visible(el) {
  const r = el.getBoundingClientRect();
  return r.width > 0 && r.height > 0;
}

const SITE = {
  id: 'iview',
  name: 'ABC IVIEW',

  // In channel-number order. These two are known for certain; ABC NEWS,
  // ABC KIDS and ABC FAMILY are picked up from iview's own links (see
  // directory) under whatever names iview gives them.
  channels: [
    { id: 'abc-live-stream', name: 'ABC TV', url: '/show/abc-live-stream' },
    { id: 'abc3-live-stream', name: 'ABC ENTERTAINS', url: '/show/abc3-live-stream' },
  ],

  // iview's page of live channels, opened by FIND MORE CHANNELS.
  directory: '/category/watch-live',

  /** A link to a live channel's page, as a channel; null for anything else. */
  channelFromLink(href, label) {
    const match = /\/show\/([a-z0-9-]*live-stream)\/?(?:[?#]|$)/.exec(href);
    if (!match) return null;
    const id = match[1];
    // ABC TV in each state. ABC TV itself follows where you are.
    if (STATE_COPIES.test(id)) return null;
    return { id, name: channelName(label, id), url: `/show/${id}` };
  },

  /** Pages where iview's player is the point of the page. */
  isPlayer(where) {
    return /^\/(video|live)\//.test(where.pathname);
  },

  /** On a channel's page: the link or button that starts it playing. */
  findPlay() {
    const main = document.querySelector('main') || document.body;
    const toPlayer = [...main.querySelectorAll('a[href*="/video/"], a[href*="/live/"]')].filter(visible);
    if (toPlayer.length) return toPlayer[0];
    const buttons = [...main.querySelectorAll('button, a[role="button"], [role="button"]')].filter(visible);
    return buttons.find((b) => /^\s*(watch|play)\b/i.test(b.textContent || '') ||
                               /^\s*(watch|play)\b/i.test(b.getAttribute('aria-label') || '')) || null;
  },

  /** The "are you still watching?" button, if one's up. */
  stillWatching() {
    for (const b of document.querySelectorAll('button, [role="button"]')) {
      const text = (b.textContent || b.getAttribute('aria-label') || '').trim();
      if (/^(continue( watching)?|keep watching|yes,? (i'm|i am) (still )?(here|watching)|i'm (still )?here)$/i.test(text)) return b;
    }
    return null;
  },

  /** What's on, for the channel bar: the page title without iview's name. */
  nowShowing() {
    return (document.title || '')
      .replace(/\s*[|:–-]\s*ABC iview\s*$/i, '')
      .replace(/\blive\s*stream\b/i, '')
      .trim()
      .slice(0, 60);
  },
};
