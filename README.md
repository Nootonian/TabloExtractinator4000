# Tablo Extractinator 4000

A personal Windows utility for extracting and archiving recordings from a **Tablo 4th-generation** network DVR. Browse your library, schedule new recordings via the built-in TV guide, and pull recordings off the device as standard MP4 files.

---

## Features

- **Recordings browser** — grouped list of all episodes, movies, and sports on your Tablo, with right-click details and batch selection
- **One-click extraction** — downloads and re-muxes recordings to MP4 using ffmpeg (no re-encoding by default — fast and lossless)
- **TV Guide** — 48-hour scrollable program grid with channel list; schedule or cancel individual recordings
- **Live TV** — launch any channel's live stream directly from the guide into mpv or VLC
- **Settings** — configurable output folders, filename templates, ffmpeg encoding options, and media player path

---

## Requirements

### .NET 10 Runtime (required)
The app is framework-dependent — it does **not** bundle the .NET runtime.

Download: https://dotnet.microsoft.com/en-us/download/dotnet/10.0
→ Install **.NET Desktop Runtime 10.x (Windows x64)**

### ffmpeg (required for extraction)
Download the Windows build from https://www.gyan.dev/ffmpeg/builds/
→ Get `ffmpeg-release-essentials.zip`
→ Extract `ffmpeg.exe` and `ffprobe.exe` into the `tools\` folder next to the EXE

```
TabloExtractinator4000.exe
tools\
    ffmpeg.exe
    ffprobe.exe
```

### mpv (recommended media player)
mpv is the recommended player for both **live TV** and **recording playback**.

Download: https://mpv.io/installation/
→ Windows builds at https://sourceforge.net/projects/mpv-player-windows/files/

**Why mpv over VLC:**
- Hardware-accelerated HEVC/H.265 decoding (what Tablo 4th-gen records in)
- Lower latency for live streams
- Clean keyboard controls: `Space` pause, `←`/`→` seek 5s, `9`/`0` volume, `q` quit

The app launches mpv with `--volume=25` so it starts at a sane volume level. You can adjust this — the path is set in **Settings → Media Player**.

VLC also works and can be set the same way.

---

## Setup

1. Copy `TabloExtractinator4000.exe` and the `tools\` folder to a permanent location
2. Launch the app
3. Go to **Settings**, enter your Tablo account email and password, and click **Reconnect to Tablo**
4. Set your output folders and media player path
5. Click **Save Settings**

Your password is stored encrypted using Windows DPAPI — it is tied to your Windows account and cannot be read on another machine.

---

## Extraction

- Switch to the **Recordings** tab (loads automatically on connect)
- Check the boxes next to recordings you want to extract, or use the group checkbox to select a whole series
- Click **Extract Selected**
- Files are saved to the output folder with the configured filename template

**Default filename templates:**
- Episodes: `{SeriesTitle} - S{Season:00}E{Episode:00} - {EpisodeTitle}`
- Movies: `{Title} ({Year})`

**Default encoding:** copy-mux only (`-c copy`) — no re-encoding, very fast. Change in **Settings → ffmpeg Encoding Options** if you want to transcode.

Output format is **MP4**, which is the correct container for the HEVC video and AAC audio that Tablo 4th-gen produces.

---

## TV Guide

- Switch to the **Guide** tab — it loads the first time you open it
- **Double-click** an airing to see details, schedule a recording, or watch live
- **Right-click** for quick access: Watch Live, Schedule Recording, Cancel Recording, Details
- Click **↺ Load Guide** to refresh
- Click **⊙ Now** to scroll the grid back to the current time

---

## License & Disclaimer

This software is provided free of charge for personal, non-commercial use. You may use, copy, and modify it for your own purposes. No warranty is provided, express or implied. The author is not responsible for any data loss, device damage, or terms-of-service violations that may result from use of this software.

This tool accesses your Tablo device on your local network using the device's own API. It does not circumvent copy protection or DRM. Use it only with recordings you are legally entitled to access and archive.
