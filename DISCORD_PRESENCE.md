# Discord presence

Open **Discord** in the top bar, enable **Show what I’m listening to**, and save.
The built-in application ID is `1549010425205497987`; the ID field allows an
override. Sharing is disabled by default.
Only an application ID is needed; do not supply a bot token or client secret.
The Discord desktop client must be running with activity sharing enabled.

While playing, the app publishes a Listening activity with the song title,
artist and album, plus timestamps based on the audio player's actual position.
Pause, stop, queue exhaustion, failed track loads, disabling presence, and
closing the app clear the activity. Resume, seek and repeat recalculate the
timestamps. Local file paths, audio files and embedded artwork are never uploaded.

**Look up album covers online** is enabled by default within the opt-in presence
feature and can be switched off independently in the Discord dialog. Text publishes
immediately; a cover is added once found, without restarting the playback timer.
The lookup uses tagged MusicBrainz release IDs first, then release-group artwork.
When no release IDs exist, it searches MusicBrainz using album title and album artist
(or track artist if album artist is absent). Only one exact title/artist match from a
complete result page is accepted. Ambiguous matches and missing covers remain text-only.
An online cover can differ from your embedded cover or edition.

URLs and confirmed misses are cached under
`%LOCALAPPDATA%\MusicPlayer\cache\album-covers`, keyed by release identity or normalized
album/artist. Hits expire after 30 days and misses after one day. Outages and invalid
responses use a one-minute memory-only backoff, not a persistent missing result.
Current-track misses are reconsidered once per minute, using this cache. Requests
share the existing MusicBrainz rate limiter and identify the app with a User-Agent.
Changing tracks, pausing, disabling the feature, or exiting cancels outstanding work;
late results cannot replace another song's artwork.

The metadata migration preserves album artist, release ID and release-group ID in
SQLite, playlists and sessions. Existing files refresh once to recover these tags.
Cover images use the public [Cover Art Archive API](https://musicbrainz.org/doc/Cover_Art_Archive/API)
`front-500` endpoints, which Discord fetches directly; they require no application
asset uploads, API keys, or image-hosting account.

Settings persist in `%LOCALAPPDATA%\MusicPlayer\preferences.json` alongside the
existing player preferences. Changing the application ID replaces the connection.
The dialog shows the connection status; saving or cancelling closes it.

`DiscordPresenceBinding` observes completed playback changes on the WPF dispatcher,
coalesces updates and limits playing updates to one per second. Normal position
ticks do not send RPC updates. `DiscordPresenceClient` wraps DiscordRichPresence
1.6.1.70, uses its background IPC connection and backoff, and invokes callbacks on
the dispatcher. It retains the latest desired activity while disconnected and
publishes it on Ready. Unexpected integration failures retry after ten seconds
without affecting audio. `DiscordPresencePipe` works around the library's shutdown
queue behaviour by writing a final clear before closing IPC on a worker. A replacement
connection waits for that closure so an old connection cannot clear the new activity.

The wrapper also skips unused asset metadata in Discord's acknowledgements to avoid
the library's null-reference bug when an optional image is absent
([upstream issue #284](https://github.com/Lachee/discord-rpc-csharp/issues/284)).
Outgoing cover URLs remain intact. If Discord rejects an update containing artwork,
the latest song is retried as text-only without disconnecting. That URL is suppressed
for the client lifetime so subsequent seeks do not repeat the error; other covers
can still be published. The settings status indicates when a cover is unavailable.

Run the focused checks with:

```powershell
dotnet run --project Tests/MusicPlayer.QueueTests.csproj -c Release -- --discord-smoke
dotnet run --project Tests/MusicPlayer.QueueTests.csproj -c Release -- --discord-artwork-recovery-smoke
dotnet run --project Tests/MusicPlayer.QueueTests.csproj -c Release -- --album-artwork-smoke
```

The automated tests use a fake client and clock, plus the real RPC wrapper with a
simulated pipe for handshake, wire payload, reconnect, and shutdown checks. No test
publishes to a Discord account. A live acceptance check additionally
requires a valid application ID: play, seek, pause/resume, restart Discord during
playback, change tracks while Discord is closed, and quit the player. Inspect the
activity from another Discord account to confirm rendering and removal.
