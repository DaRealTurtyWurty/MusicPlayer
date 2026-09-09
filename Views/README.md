# App views

`MainWindow` owns the navigation, queue, and bottom player. Its content host switches between `LibraryView`,
`PlaylistsView`, and `NowPlayingView`; playback stays in the shared `MainViewModel` so changing pages does not interrupt
it.

Now Playing displays local LRC lyrics with a persistent show/hide toggle,
line and Enhanced LRC word/syllable highlighting, click-to-seek and automatic following. Narrow layouts place
lyrics below compact track details. Turning lyrics back on rereads the matching sidecar; see
`../LYRICS.md` for discovery rules, timing behavior and focused tests.

To add a page, add a value to `Models/AppPage.cs`, create a user control here, and register its data template,
content-host trigger, and navigation button in `MainWindow.xaml`. Page-specific commands and state can live in a
separate partial view-model file, as playlist and navigation behavior does in `MainViewModel.Navigation.cs`.

The application supplies one `SqliteMusicStore` through both `IPlaylistStore` and `ILibraryStore`. EF Core 10 with the
SQLite provider saves music in `%LOCALAPPDATA%\MusicPlayer\music.db`. Tracks store file references and metadata once;
playlist entries reference tracks and preserve ordering and duplicates. Music files stay in their original locations.
Tests use isolated temporary databases or in-memory test doubles.

Each operation uses its own EF context. Playlist saves synchronize changed rows and commit new library tracks and
playlist entries in one transaction; library saves upsert imported tracks. Library entries are unique by normalized,
case-insensitive file path. The library shows tracks explicitly added through Add music or referenced by a playlist.
Playlist imports do not mark tracks as explicitly added. Removing their last playlist reference hides them from the
library unless separately added or retained with the deletion dialog's Keep option. “Add music…” imports
files or folders additively, using the shared import progress UI. Search and “Not in a playlist” filter the
library together, and “Play all” plays the visible results. Imports preserve playback and the queue.

On first use, EF migrations create/update the schema, then `library.json` and `playlists.json` in the same directory
are imported together in a transaction. Existing library metadata takes precedence over playlist copies. The original
JSON files remain untouched as backups. A database marker prevents repeat imports, including after deleting every
playlist. Missing JSON files are treated as empty; invalid JSON or failed writes leave the import incomplete and block
normal saves until repaired. UI errors report the failure, and a later load retries the import. `preferences.json`
continues to store the selected page, queue-panel visibility, volume, last audible volume, shuffle, and repeat mode. Volume changes are saved
immediately and restored before playback; zero volume restores mute, and unmute returns to the saved audible level.
Shuffle and repeat changes are also saved immediately. Startup restores these modes without reshuffling the saved
queue or starting playback. Older preferences default to shuffle off and repeat off.

SQLite also stores the current track, playback position, and ordered queue (including duplicates) through
`IPlaybackSessionStore`. The session is checkpointed every two seconds and flushed on close before the audio device
is disposed. Position-only checkpoints update a single session row; queue changes and current-track changes save
together in a transaction. Restart restores playback paused at the saved position, clamped to the track's actual
duration. Missing current files report a playback error while retaining the queue. An unreadable saved session is
preserved rather than overwritten. Existing databases receive the playback tables through an additional EF migration.

The queue panel displays played songs in playback order, a highlighted current song, then upcoming songs. Each row
represents a separate occurrence, so duplicate songs remain independently selectable. History rows can be replayed;
Clear, removal, and reordering apply to upcoming songs. Previous navigates the same playback history, and starting a
new Play all or playlist session resets it. History is saved with the playback session, including repeat-all recycling
state, and restored on restart. The queue scrolls to the current song when opened or when playback advances.
Upcoming rows can be dragged to reorder, with an insertion line and scrolling near the list edges. Right-click a row
for Play, Remove, Move up, or Move down; Enter, Delete, Alt+Up, and Alt+Down remain available from the keyboard.
Clear opens a confirmation modal showing the current upcoming-song count. Cancel or Escape leaves the queue intact;
confirming preserves the playing song and history. The modal closes if playback consumes the last upcoming song.
Deleting a playlist also opens a confirmation modal naming that playlist, with Cancel as the default, Escape to dismiss,
and a red Delete playlist action. It counts unique songs that will leave the library, excluding shared and explicitly
added songs. “Keep these songs in my library” promotes exclusive songs to explicit additions. Promotion and deletion
commit in one transaction; a failed deletion leaves the dialog and playlist intact. Current playback, queue, history,
and files on disk are retained regardless of library visibility.

`Tracks.ExplicitlyAddedToLibrary` is monotonic: metadata and session saves cannot undo a direct addition. Hidden track
records remain in the catalog for playback/history and to prevent scans from rediscovering songs removed from the
library. Re-adding one through Add music restores its visibility without duplicating the record. Existing databases
did not record origins; the membership migration treats existing playlist members as playlist-sourced and standalone
songs as explicit additions. Legacy entries in `library.json` are treated as explicit additions.

The production view model refreshes the library in the background shortly after startup. File-system events are
debounced for 1.5 seconds; a one-minute incremental reconciliation covers missed events and returning drives.
File size and UTC modification time are persisted, so unchanged files do not need their tags reread. Imported
folder roots are saved and watched recursively; folders containing individually imported tracks are watched directly.
Folders registered through Add music discover new library songs; playlist-only folder watches refresh known tracks
without independently importing more songs. Previously configured folder discovery is preserved during migration.
New discoveries enter the library without changing playback or the queue. Add music → Rescan library forces a tag
refresh and shows a five-second, dismissible completion toast. Automatic scans do not show completion notifications.

Missing tracks keep their metadata and all playlist/queue/history references. Their context menus in the library,
playlists, and queue offer Locate → Choose file or Search folder. Folder search includes subfolders and matches both
title and artist, ignoring case and redundant whitespace. Multiple matches require an explicit choice; no match,
cancel, and unreadable files preserve the original reference. The file option supports songs without artist tags.
Relinking retains the database track ID; an already-imported destination is merged transactionally without dropping
duplicate playlist or queue occurrences. Metadata and availability updates propagate to every in-memory occurrence.
Playback continues during scans and relinking; a relocated current song reloads at its previous position on resume.

The EF context, design-time factory, migrations, and model snapshot are in `Services/Persistence`. To add a schema change:

```powershell
dotnet tool restore
dotnet ef migrations add DescribeChange --output-dir Services/Persistence/Migrations --context MusicDbContext
```

The design-time factory defaults to `music.design.db`, keeping tooling away from the user's database. If intentionally
applying migrations to a specific database, pass `-- --database-path <path>` to the EF command. Runtime initialization
uses `Database.Migrate()` rather than `EnsureCreated()`. Run the integration and UI regression suite with
`dotnet run --project Tests/MusicPlayer.QueueTests.csproj`.

Windows media integration uses a window-scoped System Media Transport Controls (SMTC) session, created when the
main window receives its HWND and cleared on close. Keyboard/headset media buttons and Windows media controls use
the existing Play, Pause, Next, Previous, and Stop commands, including queue, shuffle, and repeat behavior. They
remain available when the app is minimized or unfocused. Windows chooses which active media session receives keys.
Title, artist, album, embedded artwork, playback state, duration, and position are published to Windows' media
surfaces (the lock screen and media/volume flyout, where supported by the Windows version/settings). System seeking
uses the same position control as the app. Closing the app ends the session; it does not run a background process.

The app and tests target `net10.0-windows10.0.19041.0` (Windows 10 version 2004 or later) to use the Windows SDK's
[desktop SMTC interop](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-com-interop-csharp).
`WindowsSystemMediaControls` publishes metadata and owns artwork streams; `SystemMediaControlsBinding` dispatches
system requests to the WPF thread and observes playback/command changes. Shell failures are traced without failing
audio loads. Explicit Play and Pause requests are idempotent, and pending requests are ignored after shutdown.

The regression suite includes fake-transport behavior tests and native HWND/SMTC metadata checks. Run
`dotnet run --project Tests/MusicPlayer.QueueTests.csproj -- --system-media-smoke` in an interactive Windows session
to additionally verify registration and command delivery through Windows' actual session manager, using a hidden
test window and fake audio. For hardware QA, play a song, minimize the app, and use keyboard/headset Play/Pause and
Next/Previous; check metadata and artwork on the Windows media surface and after changing to a song without artwork.

Hovering the app's taskbar button shows a compact now-playing thumbnail: artwork, title, artist, album, playback
state, elapsed/total time, and a progress bar. The native thumbnail toolbar provides Previous, Play/Pause, Next,
and Stop without activating the main window or dismissing the popup. Buttons follow the same command availability
as the player, and the window/taskbar title follows the current song. This is separate from SMTC:
`TaskbarPreviewService` uses WPF's `TaskbarItemInfo` toolbar and
[DWM iconic thumbnails](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmseticonicthumbnail).
The shell's frame, button appearance, and any Share this window action are provided by Windows.

Artwork loads through the existing thumbnail cache. DWM requests an image at its allowed pixel dimensions; the
renderer preserves its aspect ratio, truncates long text, and falls back to a music placeholder for absent/broken
artwork. Position invalidates the preview at most once per second, including while minimized. Each native bitmap
is freed after DWM copies it. Peek uses a snapshot of the full application; close removes the toolbar and hooks.
`dotnet run --project Tests/MusicPlayer.QueueTests.csproj -- --taskbar-smoke` checks toolbar behavior, native setup,
bitmap sizing, and disposal, and writes preview PNGs into the test output's `screenshots` folder. To check the shell
popup itself, launch the updated app, play a track, minimize it, and hover its taskbar button.

Albums and Artists are derived from the visible source library, independently of the Library page's text search and
Not in a playlist filter. Each has a searchable cover gallery. Albums open to a track list; artists open to their
albums and can switch to all tracks. Back from an artist's album returns to that artist. Play replaces the queue
with the group's tracks; Add to queue appends without interrupting playback. Track rows support double-click/Enter
to play and an actions menu for Play next, Add to queue, Add to playlist, Show album, Show artist, and Open file
location. These actions are also available on Library, playlist, and queue rows, including queue history and the
current song. Actions target the row that opened the menu. Play next inserts at the front of the upcoming queue;
Add to queue appends, preserving duplicates and current playback. Add to playlist opens the existing destination
picker. Show album/artist opens the corresponding detail and clears browser filters. Open file location selects
the audio file in Windows Explorer; missing files report an error and retain the Locate submenu for recovery.

`MusicGroup` groups the existing artist/album tags case-insensitively, ignoring surrounding whitespace. Album titles
are scoped to the tagged artist; missing tags appear under Unknown artist/Unknown album. Tracks sort by title, then
path; artist playback groups those tracks by album. The current metadata model does not store album-artist or disc/
track numbers, so compilation performers appear separately and ordering is alphabetical. Artwork uses the existing
asynchronous cover loader, including artwork read from saved file paths. Library changes are coalesced on the UI
dispatcher, update open groups, and preserve track selection by path. Browsing never edits the queue or library.
The new navigation values are appended to `AppPage` so older saved preferences retain their meaning.

Album cards and details display Album, Single, EP, Other, or Unknown type badges. A Release type filter is available
in the Albums gallery and an artist's release gallery. `TagLibMetadataService` reads the embedded MusicBrainz release
type through TagLib's format-specific mappings (for example, ID3's MusicBrainz Album Type and Vorbis RELEASETYPE;
see the [Picard tag mapping](https://picard-docs.musicbrainz.org/en/latest/appendices/tag_mapping.html)). Primary types
are used; secondary tags such as compilation, live, and remix do not replace an Album/Single/EP classification.
When the MusicBrainz release-type tag is absent or blank, FLAC/Vorbis `MEDIATYPE` is also read for explicit
Album, Single, EP, Other, or Broadcast values (case-insensitive). Physical formats such as CD or vinyl are ignored.
Rescan the library once after upgrading to read this alternative tag from already imported files.
When no primary tag is available, a release is inferred as a Single if it has exactly one track and that track's
title matches its nonblank album name, ignoring case and surrounding whitespace. Its tooltip explains the inference.
Other untagged releases and conflicting primary tags show Unknown type. Inference is recalculated whenever the
library changes or is rescanned, including when another track joins the album or its title/album tags change; it
is never saved as a manual override. A release that stops matching returns to Unknown unless tags classify it.

Open a release and use its type selector to set a manual classification, or select Automatic to remove it.
Overrides are scoped to the normalized artist/album key, saved in SQLite, and take precedence over refreshed tags
and the single-track inference rule. Explicit release-type tags take precedence over inference.
The music files are never modified. The ReleaseTypes migration adds the metadata column and override table and
invalidates old file timestamps once so the usual background library refresh can read tags for existing imports.
Run `dotnet run --project Tests/MusicPlayer.QueueTests.csproj -- --release-type-smoke` for classification, filtering,
override persistence/failures, embedded-tag extraction, upgrade/backfill, file preservation, and UI binding checks.

Run `dotnet run --project Tests/MusicPlayer.QueueTests.csproj -- --music-browser-smoke` for grouping, search, playback,
live-update, navigation, and layout checks. Gallery and detail previews are saved in the test output's screenshots
folder, including the artist track view at minimum width with the queue open.

The playlist view starts with a cover gallery. `OpenPlaylistCommand` opens the selected playlist's detail page;
`ClosePlaylistCommand` returns to the gallery without changing playback. `PlaylistCover` searches songs on a background
thread for up to four distinct covers, including embedded artwork from saved playlists. `PlaylistArtworkService` filters
repeated albums and compares encoded image hashes and normalized decoded pixels. One, two, three, and four covers each
have a layout that displays every image once; missing artwork uses the standard music placeholder. Changes to the
playlist refresh the cover and cancel obsolete artwork work.

The gallery can import M3U8/M3U files or a folder into a new saved playlist. `PlaylistImportService` reads local UTF-8
playlist entries in order, including duplicates, resolves relative paths beside the playlist file, and supports absolute
paths and file URIs. Folder imports include subfolders in filename order. Missing, unsupported, and unreadable entries
are counted in the result; remote URLs are skipped and HLS manifests are rejected. Imports run off the UI thread and
leave playback and the queue intact.

Import progress is reported during file discovery and track reading, at most every 100 ms plus stage transitions and
completion. The shared window shows discovery activity, then a determinate progress bar with the processed count, total,
current file, and skipped count. Progress remains visible when switching pages and disappears on completion or failure.
