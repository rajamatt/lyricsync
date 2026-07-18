# LyricSync

Time-synced karaoke lyrics for Spotify (or any media player), floating in a
click-through overlay that stays visible over fullscreen games and follows you across
virtual desktops. No API keys, no login. Built with [Uno Platform](https://platform.uno)
and MVUX.

## Features

- **Works with anything that plays music** — reads the Windows media session (the same
  source as the volume flyout); Spotify is preferred when several players are open
- **Synced lyrics** from [LRCLIB](https://lrclib.net), fetched automatically per track
  and cached
- **Game-ready overlay** — always click-through, topmost (re-asserted so games can't
  steal it), hidden from Alt-Tab, never takes focus, moves across virtual desktops
- **Customizable** — lyric color presets, font size, background panel opacity
  (0 = floating outlined text), drag to position; everything persists
- **Lives in the tray** — minimize hides the control window; the overlay keeps running

## Getting started

```powershell
cd LyricSync
dotnet run -f net10.0-desktop
```

Requires the .NET 10 SDK on Windows 10 1809+. To build a self-contained,
share-anywhere exe (no .NET needed on the target machine):

```powershell
dotnet publish -f net10.0-desktop -c Release -p:PublishProfile=win-x64
```

## Notes

- Games in true *exclusive fullscreen* bypass the compositor and can't be overlaid —
  use borderless/windowed fullscreen (the default in most modern games).
- Lyrics availability depends on LRCLIB's community database.
- Settings and the lyrics cache live in `%LOCALAPPDATA%\LyricSync`.
