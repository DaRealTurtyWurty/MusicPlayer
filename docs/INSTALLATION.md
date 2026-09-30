# Installation and library recovery

## Install, import and play

1. Download the Windows x64 ZIP and its `.sha256` file. Compare `Get-FileHash .\MusicPlayer-<version>-win-x64.zip -Algorithm SHA256` with the supplied checksum.
2. Extract the entire ZIP to a user-writable location, such as `%LOCALAPPDATA%\Programs`. Open the extracted MusicPlayer folder and run `MusicPlayer.exe`. You can create a desktop shortcut to that file. Packages are currently unsigned; Windows may show a publisher warning. Only run a package from a source you trust.
3. Open Library and add music files or a folder. Use Playlists to import an M3U/M3U8 playlist or folder. Relative playlist paths are resolved against the playlist's location.
4. Select a song and press Enter to play. Choose an output device in Settings if needed. Importing retains file references; keep the original music files available. Use Locate for moved/missing songs and Rescan after changing tags or files.

## Data and upgrades

All app versions share `%LOCALAPPDATA%\MusicPlayer` for the current Windows user. `music.db` contains tracks, playlists, queue/history, playback session, watched folders and cached identities. JSON preferences, artwork and other caches live separately in this directory. Music files remain at their original locations.

Before upgrading, choose **Settings → Back up library now**, close MusicPlayer, and copy the entire data folder to a safe location. Extract the new version into a separate application folder, then launch it. SQLite schema migrations run automatically, with a validated database snapshot saved before upgrades. Legacy JSON import leaves its source files unchanged. The app allows one running instance per user data directory across Windows sessions to prevent recovery racing normal playback.

To downgrade, close the app and restore the pre-upgrade database before starting the older binary. Newer schemas may not work with older versions. Uninstall by deleting the extracted application folder and shortcut. Retain the data folder to keep your library.

## Backups and recovery

Snapshots live in `%LOCALAPPDATA%\MusicPlayer\backups`. Seven routine startup snapshots are kept; manual and pre-upgrade snapshots are retained until you remove them. Startup snapshots capture the library at launch; use the backup button after important edits. They use SQLite's online backup API, including committed WAL data. A backup contains database state, not your music, JSON preferences or custom artwork. Copy those separately for complete recovery or another machine.

If startup fails or the library is damaged:

1. Close every MusicPlayer instance. Preserve a copy of the entire data folder first.
2. Select a known-good `music-*.db` snapshot from `backups`.
3. In PowerShell, from the extracted app directory, run:

   ```powershell
   .\MusicPlayer.exe --restore-library "$env:LOCALAPPDATA\MusicPlayer\backups\<backup-file>.db"
   ```

4. Wait for the recovery message, then start MusicPlayer normally. Verify tracks, playlists, queue and playback. Changes after the snapshot are not recovered.

Recovery validates integrity, references and library tables, stages a consistent copy, and preserves the previous database and SQLite sidecars in `%LOCALAPPDATA%\MusicPlayer\recovery\<timestamp-id>`. Invalid backups are rejected before replacing the current library. A failure does not silently reset the library. If an interruption occurs during file replacement, the previous files remain in that recovery folder: with the app closed, preserve all files and copy the archived `music.db` and any sidecars back together. Do not mix files from different snapshots. Restore only MusicPlayer backups you trust.

For a manual backup with the app closed, run `MusicPlayer.exe --backup-library`; the dialog reports the snapshot path. The Settings backup button works while the app is open.

## Diagnostics and keyboard use

Logs are in `%LOCALAPPDATA%\MusicPlayer\logs`: ten session files, approximately 2 MiB each maximum. They include version/runtime information, playback/import/storage failures and existing subsystem warnings. They can contain music paths, metadata and exception details; review them before sharing. If logging is unavailable, app operations continue. Delete logs with the app closed to clear diagnostics.

Use Tab/Shift+Tab between controls, arrow keys within lists, Enter to play a selected track, and Shift+F10 for track menus. In Queue, Delete removes an entry and Alt+Up/Down reorders it. Sliders support arrow keys; on the volume button, Up/Down opens and focuses the volume slider, and Escape closes it. Confirmation dialogs cycle focus, default to Cancel, and support Escape. Settings scrolls to reach controls in shorter windows.
