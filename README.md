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

## Where things live on the PC

- App: `%LocalAppData%\CouchLauncher` (managed by the installer; replaced on update)
- Settings, log, icons, browser sign-ins: `%LocalAppData%\CouchLauncherData`
  - `config.json`: tiles, theme, shortcuts
  - `launcher.log`: first place to look when something misbehaves

## Releasing (on the Mac)

```bash
./release.sh
```

Builds the Windows app here, packages it with Velopack and publishes a GitHub
release. The Beelink installs it on its next start, or immediately from
Settings › Check for updates. The installer link is always
`<repo>/releases/latest/download/CouchLauncher-win-Setup.exe`.

Needs the .NET SDK in `~/.dotnet` and the GitHub CLI signed in (`gh auth login`).
