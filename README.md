# MusicPlayer

A Windows desktop music player built with WPF and .NET 10. Import local tracks, folders and M3U/M3U8 playlists; browse your library, albums and artists; manage playlists and the playback queue; and view synchronized lyrics. Library data is stored in SQLite under your Windows user profile.

## Install and use

Use the self-contained `MusicPlayer-<version>-win-x64.zip` from a release or the Windows CI artifact. Extract the entire archive to a writable folder and run `MusicPlayer.exe`. No separate .NET installation or administrator access is needed. The current package targets Windows x64, Windows 10 version 2004 (build 19041) or newer.

See [installation, first playback, upgrades and recovery](docs/INSTALLATION.md). Keep the whole extracted folder together, including `sndfile.dll`; copying just the EXE will not work.

## Build and verify

On Windows, install the exact SDK in `global.json`, then:

```powershell
dotnet restore Tests/MusicPlayer.QueueTests.csproj --locked-mode
dotnet build Tests/MusicPlayer.QueueTests.csproj -c Release --no-restore
dotnet run --project Tests/MusicPlayer.QueueTests.csproj -c Release --no-build
dotnet run --project Tests/MusicPlayer.QueueTests.csproj -c Release --no-build -- --release-readiness-smoke
./scripts/Release.ps1 -Version 0.1.0
./scripts/VerifyRelease.ps1 -Version 0.1.0
```

The runner is a console regression program; `dotnet test` does not execute it. Default checks use fake audio/network services and do not require Discord or audio hardware. Live checks are opt-in. Windows CI runs regressions and uploads a self-contained release ZIP, per-file SHA-256 manifest, and archive checksum.

The SDK and NuGet dependency graph are pinned. Publish uses deterministic compiler output and normalized source paths. The release script cleans before compiling because WPF incremental markup generation can differ from a clean build. Windows CI compares two clean publishes byte for byte. Package a clean checkout for a release. ZIP timestamps are not reproducible; compare `files.sha256` to compare payloads. When updating packages, run restore with `--force-evaluate` and commit both lock files. The release script refuses to overwrite an existing version directory.

See [release plan and acceptance checks](docs/RELEASE_READINESS.md), [lyrics](LYRICS.md), [artist photos](ARTIST_PHOTOS.md), and [Discord presence](DISCORD_PRESENCE.md). Lucide licensing is included in `Assets/Lucide/LICENSE`; third-party package licenses remain applicable.
