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

## Phone remote

The launcher also serves the phone remote
([CouchControllerWeb](https://github.com/mattixone/CouchControllerWeb)) so
guests can drive the PC from their phone's browser. Settings › PHONE REMOTE
shows a QR code and the address (e.g. `http://192.168.1.20`) for them to scan
or type. ◄► on that row turns it off.

Only the page comes from here; the phone then talks straight to the Pico, so
the launcher writes the Pico's address into the page on the way out (looked up
from `couchcommander.local`, so phones that can't do `.local` names still find it).

- The first time it starts, Windows Firewall asks whether to allow Couch
  Launcher. Allow it on **private networks**, or phones can't connect. (If the
  prompt hid behind the launcher, allow it in Windows Security › Firewall ›
  Allow an app.) The Wi-Fi needs to be set to a private network in Windows.
- `config.json`: `PhoneRemote` (on/off), `PhoneRemotePort` (80; falls back to
  8080 if taken), `PicoAddress` (`couchcommander.local`, or a fixed IP).
- The page is built into the app; `release.sh` fetches the latest from
  CouchControllerWeb each release. To try changes without releasing, put an
  `index.html` in `CouchLauncherData\web\`. It's used instead, straight away.

## Where things live on the PC

- App: `%LocalAppData%\CouchLauncher` (managed by the installer; replaced on update)
- Settings, log, icons, browser sign-ins: `%LocalAppData%\CouchLauncherData`
  - `config.json`: tiles, theme, shortcuts, phone remote
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
