# Local LRC lyrics

`ILocalLyricsSource` / `LocalLyricsSource` is the discovery and loading entry point:

```csharp
var result = await lyricsSource.LoadAsync(track.FilePath, track.Duration,
    cancellationToken: cancellationToken);
```

It opens a same-directory, same-basename `.lrc` (for example `01 - Song.LRC`
beside `01 - Song.flac`). Windows filename matching is case-insensitive. It does
not search recursively, guess from artist/title, read audio tags or use a network
lyrics service. Pass `lyricsFilePath` to load an explicit selection instead; an
unusable selection does not silently fall back. File selection UI and persistence
are not part of this layer.

Discovery, reading and parsing run off the caller's UI thread. Cancellation
propagates as `OperationCanceledException`. Results distinguish `Loaded`,
`NotFound`, `Invalid` (including no usable timed lyrics), and `Unavailable`
(access/I/O failures). A loaded document can contain warnings with one-based
source line numbers. UTF-8 and BOM-marked Unicode files are supported; legacy
code-page encodings require conversion. Each load rereads the file, so edits do
not require cache invalidation.

`LrcParser.Parse` is independently usable without disk access. It supports:

- `[mm:ss]`, `[mm:ss.f]`, `[mm:ss.ff]`, and `[mm:ss.fff]` timestamps.
- Multiple timestamps on one line, stable chronological sorting, simultaneous
  lines, timed blank lines, metadata tags and signed millisecond offsets.
- Enhanced LRC inline `<mm:ss.fff>` tags (the same fractional variants work).
  Text is preserved as segments, including syllables, whitespace and punctuation.
  Inline times are absolute within the track. Repeated line timestamps shift
  segments relative to the first occurrence.
- A terminal inline tag with no following text explicitly ends a line/segment.
  Otherwise ends are inferred from the next segment, next distinct line start,
  or track duration. Unknown ends remain null. Explicit overlaps are retained.
- Malformed source lines are skipped with diagnostics; malformed inline timing
  falls back to line timing. Unsynced text is not imported by this LRC parser.

All document timestamps remain in the source time coordinate system. `Offset`
is metadata and **has not been applied** to line or segment timestamps. The
future synchronizer should apply it once: `lyricsTime = playbackPosition + Offset`
(positive LRC offset advances display). A user-facing delay adjustment is separate
and has the opposite sign. Keep negative effective timestamps when applying an
offset rather than collapsing early lines onto zero. A final duration-derived
end represents the audio track boundary, not the end of a sung word.

The parsed model is independent of WPF. `MainViewModel.Lyrics` owns a
`LyricsViewModel` that loads on track changes, cancels stale loads, selects active
lines and handles click-to-seek. It uses the existing playback position updates
(250 ms); Enhanced LRC currently displays and highlights whole lines. A more
precise audible playback clock and word-level animation remain future work.

Now Playing shows lyrics beside artwork in wide windows and below compact track
details in narrow windows. The header's Show lyrics / Hide lyrics button saves
its state in `preferences.json` (on by default). Hiding cancels loading; showing
reloads from disk, picking up edits or newly added sidecars. Missing,
invalid and unreadable files have separate panel states without playback errors.
Clicking a line seeks without changing play/pause state. Following scrolls to the
active line; manual mouse/keyboard scrolling pauses following until Resume
following or a lyric click. The view releases subscriptions and animations when
unloaded, while playback and loaded lyrics stay in the shared view model.

Run the focused checks with:

```powershell
dotnet run --project Tests/MusicPlayer.QueueTests.csproj -- --lyrics-smoke
```

The same checks also run in the default test suite.

View-model and WPF rendering checks can be run with `--lyrics-view-smoke`.
They render wide, narrow, hidden and empty-state previews under
`artifacts/lyrics-previews`. If MusicPlayer is running, build into an isolated
directory first:

```powershell
dotnet build Tests/MusicPlayer.QueueTests.csproj --artifacts-path artifacts/lyrics-view
dotnet artifacts/lyrics-view/bin/MusicPlayer.QueueTests/debug_win-x64/MusicPlayer.QueueTests.dll --lyrics-view-smoke
```
