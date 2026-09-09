# Artist photos

Artist tiles and artist detail headers use the resolved MusicBrainz artist ID.
Albums and playlists continue to use embedded artwork.

Identification uses embedded IDs first, then searches both MusicBrainz names and
aliases. Broad or same-name matches can be resolved by complete album searches,
or exact song titles and artist credits when album tags do not match. Recognized
featured-artist/edition suffixes may be removed for a second album query; meaningful
numbers, live/remix labels and Taylor's Version distinctions are retained.
Conflicting evidence remains ambiguous. Identity cache version 2 includes song
titles and retries old failures, but preserves unexpired successful version-1
matches for the same artist/album context without extending their lifetime.
Current-version results and embedded IDs take precedence over legacy matches.
On startup and library refresh, all available tag/cache results are published
before any network identification starts. Cached identities also bypass the
network gate while another artist is loading. No music-file edits or manual
database clearing are needed.

The automatic photo source order is TheAudioDB, Fanart.tv, Wikimedia Commons,
then Deezer. A custom photo takes precedence over all automatic sources.
TheAudioDB uses its documented public free key (`123`) and an exact MusicBrainz
artist ID lookup; mismatched IDs are rejected. Fanart.tv uses the application's
bundled public project key. No account or configuration is needed by users.
Wikimedia Commons is tried when neither has a thumbnail or is available:
MusicBrainz URL relationships identify the Wikidata item, its P18 image identifies
the Commons file, and Commons supplies a thumbnail and attribution metadata.
The app keeps photographer credits, licence information and source links with the
cached photo, without a text overlay. Hover for full credits; right-click a photo
to open its source or licence.
Deezer prefers the exact artist URL linked from MusicBrainz, reusing the same URL
relationship response as Commons. When there is no Deezer link, the artist views
pass their library song/album metadata for a verified name search: all exact-name
candidates are checked, and exactly one must have a matching album containing an
exact song title credited to that same Deezer artist. Name-only matches are never
accepted. Case, whitespace and typographic apostrophes are normalized; meaningful
album and track versions remain intact. Conflicting evidence is left unresolved.
Searches and candidate album catalogues are paginated (up to 300 results each),
with at most eight exact-name candidates and twelve relevant albums per candidate;
incomplete or over-limit evidence cannot establish uniqueness. Requests are bounded
by the existing shared rate limiter, cancellation and overall photo timeout.
The public
artist endpoint requires no key; the response ID must match, only approved Deezer
artist-image hosts/paths are downloaded, and known placeholder hashes are rejected.
Conflicting linked Deezer IDs are left unresolved. Deezer remains last because an
artist's profile image can be scenery or a logo rather than a portrait. Provider
credits link back to Deezer; API access does not establish a free image licence.
No guaranteed current Deezer quota is assumed: the client conservatively spaces
API requests by 500 ms and honours server backoff. Review Deezer's terms before
distribution; anonymous endpoint availability is not a distribution licence.

Name-verified cached photos and misses include a hash of the normalized artist and
song/album evidence, preventing an earlier miss or a different name-search context
from selecting/blocking the wrong photo. Provider revision 4 retries old misses,
while unexpired successful revision-3 photos remain available immediately. Custom
choices are unchanged. This upgrade does not require clearing the cache manually.

Right-click an artist photo or silhouette to **Choose artist photo** or **Reset
to automatic**. Choices work even without a MusicBrainz match. Supported inputs
are JPEG, PNG, BMP, GIF, and TIFF, up to 8 MiB / 40 million pixels. The first frame
is decoded off the UI thread, normalized to 96 DPI, bounded to 512 pixels, and
saved atomically as a PNG copy. Invalid/cancelled selections preserve the old choice.
The original image and audio files are never changed.

Custom photos live in `artist-photos/v1/custom` under the app's local data directory.
They are keyed by the normalized library artist name (matching the name-based
artist grouping), so they work before identification and survive later ID resolution.
They persist across restarts and source-file moves, do not expire, and are excluded
from automatic cache eviction and provider upgrades. Renaming a library artist
creates a different override key. Reset removes only that artist's copied override
and restores the usual automatic cache/provider behaviour. All visible views using
the app's shared service refresh after either action. Corrupt custom images fall
back to automatic artwork. Unresolved artists and missing photos without a custom
choice display an artist silhouette.

To override the bundled **project** API key, set the `MUSICPLAYER_FANART_API_KEY`
environment variable before starting the player (process and Windows user settings
are supported). Keys are sent only in the Fanart.tv API request header, never in
image URLs or cache files. The project key is intentionally included in source;
it identifies this distributed application and is not a personal account token.

Photos load only for visible controls. MusicBrainz photo lookups and artist searches
share the same rate limiter. TheAudioDB API requests are serialized at least 2.1
seconds apart (below its free 30 requests/minute limit); a 429 without Retry-After
backs off for one minute. Wikimedia requests are serialized at least 400 ms
apart. Requests respect HTTP 429/503 Retry-After and are cancellable.

The cache lives at `%LOCALAPPDATA%\MusicPlayer\artist-photos\v1`. Photos are cached
for 30 days, confirmed misses for seven days, and temporary failures for one minute
in memory only. Stale cached photos remain available when offline. Memory retains
up to 64 thumbnail entries; disk cleanup bounds the cache to about 256 MiB / 1,000
entries. Photos and credit metadata are saved atomically together. Provider-chain
revisions force old photos and cached misses to be refreshed on their next visible
load, while retaining old photos if offline. A fallback selected during a provider
outage is cached for only one hour, not 30 days. Audio files are not modified.
Decoded thumbnails use 96-by-96 display DPI regardless of the source metadata.
This also repairs existing cached images whose malformed DPI would stretch and
crop a real portrait into an apparently blank background or gradient.

TheAudioDB's free tier supports development projects, but its terms require a paid
subscription for publishing to an app store. Review the terms before distribution:
https://www.theaudiodb.com/docs_terms_of_use.php
API documentation: https://www.theaudiodb.com/free_music_api

Verification commands:

```powershell
dotnet run --project Tests/MusicPlayer.QueueTests.csproj -c ArtistPhotoReview -- --artist-photo-smoke
dotnet run --project Tests/MusicPlayer.QueueTests.csproj -c ArtistPhotoReview -- --artist-photo-live
dotnet run --project Tests/MusicPlayer.QueueTests.csproj -c ArtistPhotoReview -- --artist-deezer-live
dotnet run --project Tests/MusicPlayer.QueueTests.csproj -c ArtistPhotoReview -- --artist-deezer-search-smoke
dotnet run --project Tests/MusicPlayer.QueueTests.csproj -c ArtistPhotoReview -- --artist-custom-smoke
dotnet run --project Tests/MusicPlayer.QueueTests.csproj -c ArtistPhotoReview -- --artist-identity-smoke
dotnet run --project Tests/MusicPlayer.QueueTests.csproj -c ArtistPhotoReview -- --artist-matching-live
dotnet run --project Tests/MusicPlayer.QueueTests.csproj -c ArtistPhotoReview -- --artist-cache-smoke
```
