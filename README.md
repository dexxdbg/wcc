# wcc — Windows Context Converter

A lightweight Windows CLI tool that adds a **"Convert with WCC"** cascading submenu to Explorer's right-click context menu for media files. Conversions are powered by FFmpeg.

## Features

- One-click media conversion directly from Explorer
- 13 video targets: MP4, MKV, WebM, MOV, AVI, FLV, WMV, M4V, MPG, TS, 3GP, OGV, GIF
- 11 audio targets: MP3, WAV, FLAC, OGG, Opus, M4A, AAC, WMA, AC3, AIFF, MKA
- Auto-downloads FFmpeg on first use (parallel chunked download with progress bar)
- No admin required — installs to HKCU registry only
- Single self-contained `.exe`, no installer

## Requirements

- Windows 10/11
- .NET 10 runtime (or use the self-contained publish)

## Usage

```
wcc install                    Register context menu entries in Explorer
wcc uninstall                  Remove context menu entries
wcc ensure-ffmpeg              Pre-download FFmpeg (otherwise done on first convert)
wcc set-ffmpeg <path>          Use an existing ffmpeg.exe instead of downloading
wcc formats                    List all supported target formats
wcc convert <input> <format>   Convert a file directly from the command line
```

### Quick start

```cmd
wcc install
```

Then right-click any supported media file in Explorer → **Convert with WCC** → pick a format.

> On Windows 11, the menu lives under **Show more options** (or press Shift+F10).

## Supported source formats

| Type  | Extensions |
|-------|-----------|
| Video | `.mp4` `.mkv` `.avi` `.mov` `.webm` `.wmv` `.flv` `.m4v` `.mpeg` `.mpg` `.ts` `.3gp` `.ogv` `.gif` |
| Audio | `.mp3` `.wav` `.flac` `.ogg` `.m4a` `.wma` `.aac` `.opus` `.ac3` `.aiff` `.aif` `.mka` |

## FFmpeg

WCC downloads the [BtbN GPL static build](https://github.com/BtbN/FFmpeg-Builds) to `%LOCALAPPDATA%\WCC\ffmpeg.exe` on first use. The download uses 8 parallel HTTP range requests for faster speeds. If you already have FFmpeg, skip the download:

```cmd
wcc set-ffmpeg C:\tools\ffmpeg.exe
```

## Build

```cmd
dotnet publish ConsoleApp1/ConsoleApp1/ConsoleApp1.csproj -c Release
```

Output lands in `dist/wcc.exe`.
