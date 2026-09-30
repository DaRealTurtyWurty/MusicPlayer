using System.IO;
using System.Windows.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.Services.Persistence;
using MusicPlayer.ViewModels;

internal static partial class Program
{
    private static async Task CheckTrackMetadataAsync()
    {
        using var temporary = new TemporaryTestDirectory("MusicPlayerTrackMetadata");
        var directory = temporary.Path;
        FixtureLibrary.Write(directory);
        var path = Path.Combine(directory, "01.wav");
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Disc = 2;
            file.Tag.Track = 12;
            file.Tag.Year = 2024;
            file.Tag.Genres = ["Rock", "Electronic"];
            file.Save();
        }
        var metadata = new TagLibMetadataService();
        var tagged = metadata.ReadTrack(path);
        bool HasTags(Track t) => t.DiscNumber == 2 && t.TrackNumber == 12 && t.Year == 2024 &&
            t.Genre == "Rock; Electronic" && t.MetadataVersion == MusicPlayer.Models.Track.CurrentMetadataVersion;
        Check(HasTags(tagged) && HasTags(tagged.WithFileState(1, 2, true)),
            "Audio metadata and missing-file copies retain disc, track, year, and genres");
        var library = new JsonLibraryStore(Path.Combine(directory, "roundtrip-library.json"));
        var playlists = new JsonPlaylistStore(Path.Combine(directory, "roundtrip-playlists.json"));
        library.Save([tagged]);
        playlists.Save([new Playlist([tagged]) { Name = "Metadata" }]);
        Check(HasTags(library.Load().Single()) && HasTags(playlists.Load().Single().Tracks.Single()),
            "JSON library and playlist metadata roundtrip");

        var database = Path.Combine(directory, "music.db");
        var legacyKey = MusicGroup.LegacyAlbumKey(tagged);
        using (var db = new MusicDbContext(new DbContextOptionsBuilder<MusicDbContext>().UseSqlite($"Data Source={database}").Options))
        {
            db.GetService<IMigrator>().Migrate(db.Database.GetMigrations().Single(m => m.EndsWith("_AlbumArtworkMetadata")));
            db.Database.ExecuteSqlRaw("INSERT INTO StorageStates (Key) VALUES ('LegacyJsonImported')");
            var key = Path.GetFullPath(path).ToUpperInvariant();
            db.Database.ExecuteSqlInterpolated($"INSERT INTO Tracks (PathKey, FilePath, Title, Artist, Album, DurationTicks, IsMissing, ExplicitlyAddedToLibrary, MetadataVersion, FileSize, LastWriteTimeUtcTicks) VALUES ({key}, {path}, {tagged.Title}, {tagged.Artist}, {tagged.Album}, {tagged.Duration.Ticks}, 0, 1, 2, {tagged.FileSize}, {tagged.LastWriteTimeUtcTicks})");
            db.Database.ExecuteSqlInterpolated($"INSERT INTO ReleaseTypes (ReleaseKey, Type) VALUES ({legacyKey}, {(int)ReleaseType.EP})");
        }
        var store = new SqliteMusicStore(database);
        var old = store.LoadLibrary().Single();
        Check(old.DiscNumber == 0 && old.TrackNumber == 0 && old.Year == 0 && old.Genre is null &&
            store.LoadReleaseTypes()[legacyKey] == ReleaseType.EP,
            "Schema migration leaves missing tags unset and preserves existing manual release types");
        var refresh = new LibraryRefreshService(metadata);
        var updated = (await refresh.RefreshAsync([old], [], false, default)).Updates.Single();
        Check(HasTags(updated), "Unchanged version-two tracks backfill new metadata");
        store.SaveLibrary([updated]);
        store.SavePlaylists([new Playlist([updated]) { Name = "Metadata" }]);
        store.SaveSession(new PlaybackSession(updated, TimeSpan.Zero, [updated]));
        store = new SqliteMusicStore(database);
        Check(HasTags(store.LoadLibrary().Single()) && HasTags(store.LoadPlaylists().Single().Tracks.Single()) &&
            HasTags(store.LoadSession().CurrentTrack!) && HasTags(store.LoadSession().Queue.Single()),
            "SQLite library, playlist, and playback session retain track metadata");
        Check((await refresh.RefreshAsync(store.LoadLibrary(), [], false, default)).Updates.Count == 0,
            "Backfilled metadata is not reread repeatedly");
        using (var db = new MusicDbContext(new DbContextOptionsBuilder<MusicDbContext>().UseSqlite($"Data Source={database}").Options))
            Check(!db.Database.HasPendingModelChanges(), "Track metadata migration matches the model");

        var player = new FakePlayer();
        using (var persistedVm = new MainViewModel(new Picker(), new Picker(), metadata, new Scanner(), player,
                   libraryStore: store, releaseTypeStore: store))
        {
            Check(persistedVm.Albums.Single().ReleaseType == ReleaseType.EP &&
                store.LoadReleaseTypes()[persistedVm.Albums.Single().Key] == ReleaseType.EP,
                "Legacy override migrates durably to the new album identity");
            persistedVm.OpenMusicGroupCommand.Execute(persistedVm.Albums.Single());
            persistedVm.SelectedReleaseTypeChoice = persistedVm.ReleaseTypeChoices[0];
        }
        using (var restarted = new MainViewModel(new Picker(), new Picker(), metadata, new Scanner(), player,
                   libraryStore: new SqliteMusicStore(database), releaseTypeStore: new SqliteMusicStore(database)))
            Check(restarted.Albums.Single().ReleaseType != ReleaseType.EP && store.LoadReleaseTypes().Count == 0,
                "Automatic classification remains automatic after restarting a migrated library");

        static Track Song(string path, string artist = "Guest", uint disc = 0, uint number = 0,
            string title = "Song", string? release = null, string? group = null, uint year = 2024) => new()
        {
            FilePath = path, Title = title, Artist = artist, AlbumArtist = "Various Artists", Album = "Collection",
            DiscNumber = disc, TrackNumber = number, Year = year,
            MusicBrainzReleaseId = release, MusicBrainzReleaseGroupId = group
        };
        const string release1 = "11111111-1111-1111-1111-111111111111";
        const string release2 = "22222222-2222-2222-2222-222222222222";
        const string group1 = "33333333-3333-3333-3333-333333333333";
        var a = Song("a", number: 2, title: "Z");
        var b = Song("b", artist: "Other guest", disc: 1, number: 1, title: "Y");
        var c = Song("c", disc: 2, number: 1, title: "A");
        var d = Song("d", title: "B");
        var e = Song("e", title: "B");
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer());
        foreach (var track in new[] { c, e, a, d, b }) vm.Tracks.Add(track);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var album = vm.Albums.Single();
        Check(album.Artist == "Various Artists" && album.Tracks.SequenceEqual(new[] { b, a, d, e, c }),
            "Compilation groups by album artist; absent discs use disc one, absent track numbers follow numbered tracks, with title/path ties");
        vm.OpenMusicGroupCommand.Execute(album);
        vm.PlayMusicGroupCommand.Execute(null);
        Check(vm.CurrentTrack == b && vm.Queue.SequenceEqual(new[] { a, d, e, c }), "Album playback follows disc/track order");
        vm.Queue.Clear();
        vm.QueueMusicGroupCommand.Execute(null);
        Check(vm.Queue.SequenceEqual(album.Tracks), "Album queue follows the same order");
        vm.Tracks.Add(Song("release1", release: release1, group: group1));
        vm.Tracks.Add(Song("release2", release: release2, group: group1));
        vm.Tracks.Add(Song("group1", group: group1));
        vm.Tracks.Add(Song("older", year: 2000));
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(vm.Albums.Count == 5 && vm.Albums.Select(g => g.Key).Distinct().Count() == 5,
            "Distinct release IDs take priority over shared release-group IDs; untagged releases fall back to title/year");
        Check(MusicGroup.AlbumKey(Song("normalized", release: release1.ToUpperInvariant())) ==
            MusicGroup.AlbumKey(Song("release", release: release1)), "Release IDs normalize consistently");

        var overrides = new FakeReleaseTypeStore();
        overrides.Types[MusicGroup.LegacyAlbumKey(a)] = ReleaseType.Single;
        using var migrating = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer(),
            releaseTypeStore: overrides);
        migrating.Tracks.Add(a);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        migrating.OpenMusicGroupCommand.Execute(migrating.Albums.Single());
        var identified = Song("a", release: release1);
        migrating.Tracks[0] = identified;
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(migrating.SelectedAlbum?.Key == MusicGroup.AlbumKey(identified) &&
            migrating.SelectedAlbum.ReleaseType == ReleaseType.Single && overrides.Types.Count == 1,
            "Metadata backfill carries overrides and selected album across identity changes");

        var splitOverrides = new FakeReleaseTypeStore();
        splitOverrides.Types[MusicGroup.LegacyAlbumKey(a)] = ReleaseType.EP;
        var firstRelease = Song("first-release", release: release1);
        var secondRelease = Song("second-release", release: release2);
        splitOverrides.Types[MusicGroup.AlbumKey(secondRelease)] = ReleaseType.Album;
        using var split = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer(),
            releaseTypeStore: splitOverrides);
        split.Tracks.Add(firstRelease);
        split.Tracks.Add(secondRelease);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(split.Albums.Single(g => g.Key == MusicGroup.AlbumKey(firstRelease)).ReleaseType == ReleaseType.EP &&
            split.Albums.Single(g => g.Key == MusicGroup.AlbumKey(secondRelease)).ReleaseType == ReleaseType.Album &&
            !splitOverrides.Types.ContainsKey(MusicGroup.LegacyAlbumKey(a)),
            "Legacy overrides follow split releases while existing release-specific overrides win");

        var failingOverrides = new FakeReleaseTypeStore { FailSave = true };
        failingOverrides.Types[MusicGroup.LegacyAlbumKey(a)] = ReleaseType.EP;
        using var failing = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer(),
            releaseTypeStore: failingOverrides);
        failing.Tracks.Add(firstRelease);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(failing.ReleaseTypeError is not null && !failing.CanEditReleaseType &&
            failingOverrides.Types[MusicGroup.LegacyAlbumKey(a)] == ReleaseType.EP,
            "Failed key migration reports the error and retains the original override");
    }
}
