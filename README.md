# Couch Launcher

A full-screen home screen for the Beelink, in the Couch Commander CRT style.
Big tiles open apps (Stremio, Steam Big Picture) or websites in a chosen
browser (YouTube in Brave, slither.io in Chrome), each as its own fullscreen
window. Everything is driven by the Retroid through the Pico: the d-pad moves,
A (Play/Pause) opens, B (Escape) closes an open app.

## Shortcuts (set these up in the Retroid app)

| Shortcut | Does |
|---|---|
| `Ctrl+Alt+Shift+H` | Back to the tiles, from anywhere |
| `Ctrl+Alt+Shift+S` | Switch between open apps (press again to move along) |

Both can be changed in `config.json`.

## Game controller

Any Xbox-style controller works too (on by default; Settings › Advanced ›
Controller).
In the launcher the d-pad or left stick moves, A opens, B goes back. Inside
another app the controller is left alone, except:

| Hold for a second | Does |
|---|---|
| `Back` + `Start` (View + Menu) | Back to the tiles, from anywhere |

In a Live Surfer tile, X also opens the TV guide page (and X again goes back).

The combo and hold time are `ControllerHomeCombo` and `ControllerHoldMs` in
`config.json`. PlayStation and Switch pads need Steam Input or DS4Windows to
appear as Xbox controllers.

## Phone remote

The launcher also serves the phone remote
([CouchControllerWeb](https://github.com/mattixone/CouchControllerWeb)) so
guests can drive the PC from their phone's browser. Settings › PHONE REMOTE
shows a QR code and the address (e.g. `http://192.168.1.20`) for them to scan
or type. ◄► on that row turns it off.

Button presses go to the Pico when it's running the web remote (it's a real
USB keyboard, so UNLOCK works at the lock screen). Until then, or whenever
the Pico doesn't answer, the launcher presses the keys itself from Windows.
Everything works that way except UNLOCK, since Windows ignores apps' key
presses on the lock screen and admin prompts. The page's status line says
`LINKED PICO` or `LINKED PC` so you can tell which. The launcher checks for
the Pico in the background, and once it answers, pages try it first. The
Retroid's link to the Pico isn't involved either way.

- The first time it starts, Windows Firewall asks whether to allow Couch
  Launcher. Allow it on **private networks**, or phones can't connect. (If the
  prompt hid behind the launcher, allow it in Windows Security › Firewall ›
  Allow an app.) The Wi-Fi needs to be set to a private network in Windows.
- `config.json`: `PhoneRemote` (on/off), `PhoneRemotePort` (80; falls back to
  8080 if taken), `PicoAddress` (`couchcommander.local`, or a fixed IP),
  `PhoneRemotePower` (let guests sleep/restart/shut down the PC).
- The page is built into the app; `release.sh` fetches the latest from
  CouchControllerWeb each release. To try changes without releasing, put an
  `index.html` in `CouchLauncherData\web\`. It's used instead, straight away.

## Live Surfer (live TV from the streaming sites)

A **Live Surfer** tile puts live channels from ABC iview, SBS On Demand,
7plus, 9Now and 10 into one window you can flick through with the D-pad. The
channels are pages you pick yourself. Each site's own player does the
playing, so sign-ins and copy protection work as normal. Live Surfer only
changes what's on screen and what the buttons do.

**Setting one up**

1. Settings › Edit tiles › + ADD, then TYPE › LIVE SURFER. Pick a browser and
   a start page (it opens there until it has channels).
2. Open the tile. Sign in to each service you use, once. The tile keeps its
   own sign-ins. Some sites sign in through a pop-up, so turn POP-UP WINDOWS
   (under the tile's ADVANCED) to ALLOWED while you do this, then back to
   BLOCKED.
3. Find a live channel and start it playing. Press **B** and choose
   **+ ADD THIS CHANNEL**. Do that for each channel you want. B ›
   BROWSE A SERVICE… gets you to the other sites.
4. Back in Edit tiles you can rename, reorder and remove channels, or add
   one by pasting its address.

**Using it**

- **B:** the menu, from anywhere. It has your channels, ADD THIS CHANNEL or
  RENAME and REMOVE for the one you're on, TV GUIDE, LAST CHANNEL, BROWSE A
  SERVICE…, PREVIOUS PAGE, and THE SITE'S OWN SCREEN, which shows the normal
  site until you leave that page.
- **On a channel:** ◄► change channel (a few quick presses skip channels),
  ▲▼ change the PC's volume, A shows the channel bar.
- **TV guide:** **X** opens the TV guide page, Freeview's by default, and X
  again goes back to the channel. A twice, G on a keyboard, or B › TV GUIDE
  do the same. X works with an Xbox-style controller. If the Retroid's X
  doesn't, set it in the Retroid app to send G. To use a different guide
  page, open it in the tile and choose B › USE THIS PAGE AS THE TV GUIDE, or
  set it under the tile's ADVANCED › TV GUIDE PAGE.
- **On a show or film:** A plays/pauses, ◄► jump 10 seconds, ▲▼ volume.
- **Anywhere else** the keys belong to the site, so you can browse it with
  the D-pad and A.
- When something plays, the video fills the screen and the rest of the page
  is hidden. If a channel doesn't start by itself, Live Surfer presses its
  Play button. If it still isn't playing after 15 seconds, it shows you the
  page so you can start it.
- **Renaming:** B › RENAME brings up an on-screen keyboard. Use the D-pad
  and A, or type on the phone remote's keyboard, then DONE.
- The tile opens on the last channel you watched. Changing channel within a
  site is quick. Changing to another site loads that site, so allow a few
  seconds.

**Good to know**

- Save the page a channel actually plays on. If a site plays every channel
  from the same address (switching inside the page without the address
  changing), Live Surfer can't tell those channels apart. Tell me which site
  and it can get a small fix of its own.
- **How it works:** the launcher starts the tile's browser with its DevTools
  connection on (the one automation tools use) and puts Live Surfer's script
  into every page. This only works because each tile has its own browser
  profile, and the connection is only reachable from the PC itself, never
  the network.
- **When something stops working:** `launcher.log` has a line for each step
  (channel changes, Play pressed, TV look on/off, which copy
  protection the site asked for). The script is `sites/live-surfer.js` in this repo. To try
  a fix without releasing, put an edited copy in
  `CouchLauncherData\sites\live-surfer.js` and it's used from the next page
  load.
- **Settings › Advanced › DRM CHECK** shows whether WebView2 (the browser engine apps
  can build in) could play these sites. Compare it with the copy protection
  the log says each site asks for.

## Where things live on the PC

- App: `%LocalAppData%\CouchLauncher` (managed by the installer; replaced on update)
- Settings, log, icons, browser sign-ins: `%LocalAppData%\CouchLauncherData`
  - `config.json`: tiles, theme, shortcuts, phone remote
  - `sites\live-surfer.js`: optional local copy of Live Surfer's script, used instead of the built-in one
  - `launcher.log`: first place to look when something misbehaves

## Releasing

**From GitHub (no Mac needed):** on the repo's **Actions** tab (or in the
GitHub app), pick **Release** › **Run workflow**. Leave the version empty
for the next one (1.0.4 → 1.0.5), or type an exact one. It builds on
GitHub's Windows machines and publishes the release in a few minutes.

**From the Mac:**

```bash
./release.sh
```

Needs the .NET SDK in `~/.dotnet` and the GitHub CLI signed in (`gh auth login`).

Either way, it packages the app with Velopack and publishes a GitHub release,
with the latest phone remote page built in. The Beelink installs it on its
next start, or immediately from Settings › Check for updates. The installer
link is always `<repo>/releases/latest/download/CouchLauncher-win-Setup.exe`.
