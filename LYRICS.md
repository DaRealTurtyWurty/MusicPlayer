# Local lyrics: sidecars and embedded tags

`ILocalLyricsSource` / `LocalLyricsSource` is the discovery and loading entry point:

```csharp
var result = await lyricsSource.LoadAsync(track.FilePath, track.Duration,
    cancellationToken: cancellationToken);
```

It opens same-directory, same-basename sidecars in this order: `.ttml`,
`.lyricsfile.yaml`, then `.lrc` (for example `01 - Song.lyricsfile.yaml` beside
`01 - Song.flac`). Only a missing file falls back to the next format. An invalid
or inaccessible preferred file is reported so another version is not silently substituted.
When all three sidecars are absent, it reads embedded lyrics from the audio file.
Windows filename matching is case-insensitive. It does not search recursively,
guess from artist/title or use a network lyrics service.
Pass `lyricsFilePath` to load an explicit selection instead; an
unusable selection does not silently fall back. File selection UI and persistence
are not part of this layer.

`EmbeddedLyricsReader` uses TagLibSharp with a read-only file abstraction and
shared read access, so lyrics can load during playback and from read-only files.
Loading does not save tags, extract sidecars, or modify the audio. Tags are reread
on each lyrics load, including after toggling lyrics off and on.

Supported embedded fields include:

- MP3/ID3v2 `SYLT` (synchronized lyrics) and `USLT` (plain text or embedded LRC).
  SYLT accepts absolute millisecond timing and lyric content frames. LRCGET's
  complete-line entries become line lyrics; entries with newline boundaries
  become word/syllable groups. Spaces remain part of their original text.
  Empty cues end the preceding sung text. Missing ends use the next distinct
  line/word onset or available audio duration. SYLT does not provide explicit
  end times or singer roles; they are not reconstructed from the audio.
- FLAC/Ogg Vorbis comment fields `LYRICS` and `UNSYNCEDLYRICS`, including
  LRCGET's synced/plain pairing. `SYNCEDLYRICS`, `SYNCHRONIZEDLYRICS`,
  `UNSYNCED LYRICS`, `TTML`, and `LYRICSFILE` are also recognized.
  The same names are supported in ID3v2 `TXXX` and APEv2 tags.
- Standard TagLib lyric properties, including M4A/MP4 `©lyr` and ASF `WM/Lyrics`.

Text fields containing LRC, Enhanced LRC, TTML or Lyricsfile use the corresponding
parser. Other text displays as plain lyrics without highlighting or seeking.
The embedded `[au: instrumental]` marker is recognized. Within audio tags,
instrumental declarations take priority, followed by usable word timing, line
timing, then plain lyrics. Equivalent candidates keep tag enumeration order;
different languages or duplicate renditions are not merged. A damaged timed
candidate can fall back to another usable embedded tag with a warning. Sidecars
still override all embedded versions, including when a sidecar is unusable.

Successfully read tags set `LocalLyricsResult.IsEmbedded`; `FilePath` is the
audio path. Loaded documents also include `source=embedded`, `embedded_tag`,
and the ID3 language when available. Unsupported SYLT MPEG-frame timing units
are skipped with a diagnostic, rather than interpreted as milliseconds.

A small SYLT frame decoder is registered once at assembly initialization to
correct TagLibSharp 2.3.0's dropped final empty cue. It retains TagLib's frame
header and encoding handling, preserves UTF-8/UTF-16 text, and rejects truncated
timestamps. It changes no on-disk data.

Focused verification: `--embedded-lyrics-smoke` exercises raw ID3 frames matching
[LRCGET's export layout](https://github.com/tranxuanthang/lrcget/blob/main/src-tauri/src/export.rs),
FLAC, Ogg, M4A and APE tags, source priority, unchanged audio bytes, shared/read-only
access, reloads, error handling, and Now Playing synchronization. All audio
fixtures are synthetic and tests write tags only to temporary copies.

`LyricsfileParser.Parse` reads the [Lyricsfile 1.0 draft](https://github.com/tranxuanthang/lyricsfile/blob/main/SPECIFICATION.md)
used by LRCGET/LRCLIB. The compound `.lyricsfile.yaml` extension is required for
discovery and explicit loading; unrelated `.yaml` files are not treated as lyrics.
Line and word timestamps are absolute integer milliseconds. Explicit overlaps
remain intact, and words retain their whitespace and syllable boundaries. If
word text differs from the fallback line text, timed word text takes precedence
with a diagnostic. Invalid word timing falls back to line highlighting; invalid
lines are skipped. Structural YAML errors and unsupported versions reject the file.

Missing word ends use the next distinct word start or the line end. A missing
line end uses the latest word end when every word has one. Otherwise, presentation
lasts five seconds beyond the final word start (or line start), capped by the
available track duration without cutting off explicit word intervals. Another
line's onset never truncates these intervals. Inferred ends are marked in memory;
the source file is never rewritten.

Plain-only files display preserved line breaks without timed highlighting or
seeking. Timed lyrics take precedence over the separate `plain` version.
Instrumental files show an instrumental status. `metadata.offset_ms` is retained
but not applied because the draft leaves its meaning undefined; nonzero values
produce a visible warning. Singer/background-role fields are not yet standardized
by Lyricsfile, so overlapping lines do not receive invented singer labels.

YamlDotNet handles YAML syntax, including flow collections, escaped strings and
block text. Loading rejects duplicate keys, multiple documents, custom tags,
anchors/aliases, more than 64 nested collections, and more than 4,194,304 UTF-16
code units. Unknown fields with supported YAML value types are ignored.

Example `Song.lyricsfile.yaml`:

```yaml
version: '1.0'
metadata:
  title: Two voices
  artist: Maya & Alex
lines:
  - text: 'Stay with me'
    start_ms: 2000
    end_ms: 8000
    words:
      - {text: 'Stay ', start_ms: 2000, end_ms: 4000}
      - {text: 'with me', start_ms: 4000, end_ms: 8000}
  - {text: 'Through the night', start_ms: 4000, end_ms: 10000}
```

Focused verification: run the queue-test executable with `--lyricsfile-smoke`.
It exercises parsing, discovery, overlapping playback, plain/instrumental states,
and actual Now Playing rendering.

`TtmlParser.Parse` imports lyrics in the `http://www.w3.org/ns/ttml` namespace:

- Paragraphs (`p`) become lyric groups. Timed `span` elements provide word or
  syllable ranges. Nested containers support `begin`, `end`, `dur`, and parallel
  or sequential timing; ancestor intervals limit their children's presentation.
- Standard `HH:MM:SS.fraction` clock expressions and `h`, `m`, `s`, `ms`, `f`,
  and `t` offsets are supported, including frame-rate multipliers and subframes.
  Standard TTML child times are relative to the parent (or preceding sibling
  for sequential containers).
- Files declaring Apple's `http://itunes.apple.com/lyric-ttml-extensions`
  namespace, or using its two-field `MM:SS.fraction` shorthand, use absolute
  song-time clock expressions. Duration values remain durations. This explicit
  convention detection avoids adding the paragraph start to every Apple word.
- `ttm:agent` references and `ttm:name` metadata identify singers. Agent changes
  in spans produce separate vocal parts; nested `ttm:role="x-bg"` spans produce
  independently timed backing vocals. Backing text is excluded from the lead.
  Translation and romanization roles are not treated as sung text.
- Text preserves Unicode, punctuation and adjacent syllables. Default XML
  whitespace collapses across spans; `xml:space="preserve"` and `br` retain
  intentional spacing and line breaks.
- Malformed intervals produce source-line diagnostics while other paragraphs
  remain usable. XML DTDs and external entities are prohibited. Documents are
  limited to 4 MiB of XML characters and 128 levels of timed content nesting.

This is a media-time lyrics importer, not a complete TTML subtitle compositor.
Clock/SMPTE time bases and drop-frame timing are rejected; subtitle regions,
styling, ruby layout and animation are not implemented. TTML files without any
timing do not become synchronized merely because an audio duration is supplied.

For example, save this as `Song.ttml` beside `Song.flac`. These standard TTML
word offsets are relative to their paragraph:

```xml
<tt xmlns="http://www.w3.org/ns/ttml"
    xmlns:ttm="http://www.w3.org/ns/ttml#metadata">
  <head><metadata>
    <ttm:agent xml:id="lead"><ttm:name>Maya</ttm:name></ttm:agent>
    <ttm:agent xml:id="duet"><ttm:name>Alex</ttm:name></ttm:agent>
  </metadata></head>
  <body><div>
    <p begin="2s" end="8s" ttm:agent="lead"><span begin="0s" end="2s">Stay </span><span begin="2s" end="6s">with me</span><span ttm:role="x-bg" begin="1s" end="5s">(Stay with me)</span></p>
    <p begin="4s" end="10s" ttm:agent="duet"><span begin="0s" end="2s">Through </span><span begin="2s" end="6s">the night</span></p>
  </div></body>
</tt>
```

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
synchronizer applies it once: `lyricsTime = playbackPosition + Offset`
(positive LRC offset advances display). A user-facing delay adjustment is separate
and has the opposite sign. Keep negative effective timestamps when applying an
offset rather than collapsing early lines onto zero. A final duration-derived
end represents the audio track boundary, not the end of a sung word.

The parsed model is independent of WPF. `MainViewModel.Lyrics` owns a
`LyricsViewModel` that loads on track changes, cancels stale loads, selects active
lines and handles click-to-seek. Enhanced LRC words and syllables fill continuously
between their start and end timestamps; completed segments stay bright until the
line ends. Ordinary LRC still highlights the whole active line. Missing or
zero-length segment ends highlight at onset instead of inventing a duration.
TTML segments use the same renderer. Each vocal part keeps its own interval;
another singer's onset never truncates it. `ActiveLines` exposes all active
parts, while `ActiveLine` selects a lead-first scroll anchor. The view keeps all
active parts visible when they fit. Singers alternate alignment, and backing
vocals are labelled and inset with smaller text. If a group is taller than the
viewport, following prioritizes the lead; manual scrolling remains available.

`KaraokeLine` shapes the entire line with WPF, preserving spaces, punctuation,
Unicode text and wrapping. Cached text-range geometry clips a bright copy over
the dim text. Segment progress can span several visual rows, with right-to-left
segments filling in their reading direction. Layout is rebuilt only for text,
width, font, DPI or brush changes, not on each frame.

The visible Now Playing view samples `IAudioPlayer.PresentationPosition` on WPF
rendering frames while playing. NAudio derives that position from the output
device's rendered-byte counter rather than the decoder's buffered read position.
Seeking discards queued audio and resets the device origin; pause freezes the
clock. Hidden, unloaded and paused views unsubscribe from frame callbacks. The
existing 250 ms timer still maintains lyrics state outside the active view.

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

`--lyrics-enhanced-smoke` tests segment timing, visible fill progression,
backward seeks, wrapping, right-to-left text and the native output clock with a
silent WAV. The native checks report a skip if no output device is installed.

`--ttml-smoke` checks standard and Apple timing, vocal separation, whitespace,
frames/ticks, invalid XML, sidecar precedence and overlapping-vocal rendering.
It writes `ttml-duet-wide.png` and `ttml-duet-narrow.png` previews. The default
test suite also includes TTML parser and discovery checks.

Timing conventions follow the [W3C TTML2 specification](https://www.w3.org/TR/ttml2/)
and lyric extensions/examples in the [Apple asset guide](https://help.apple.com/itc/videoaudioassetguide/en.lproj/static.html).
