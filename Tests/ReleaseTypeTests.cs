using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MusicPlayer;
using MusicPlayer.Controls;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.Services.Persistence;
using MusicPlayer.ViewModels;
using MusicPlayer.Views;

internal static partial class Program
{
    private static async Task CheckReleaseTypesAsync()
    {
        Track Release(string name, string? tag, string artist = "Test artist", string? title = null) => new()
        {
            FilePath = name, Title = title ?? name, Artist = artist, Album = name, ReleaseTypeTag = tag,
            ArtworkData = CreateBrowserArtwork(name), Duration = TimeSpan.FromMinutes(4)
        };
        Check(ReleaseClassification.FromTracks([Release("Partial album", "album")]).Type == ReleaseType.Album,
            "A single imported album track is still classified as an Album by its tags");
        Check(ReleaseClassification.FromTracks([Release("Untagged", null)]).Type == ReleaseType.Single,
            "An untagged release with one track matching its album title is inferred as a Single");
        Check(ReleaseClassification.FromTracks([Release("  Matching title  ", null, title: "matching TITLE")]).Type == ReleaseType.Single,
            "Single inference ignores title case and surrounding whitespace");
        Check(ReleaseClassification.FromTracks([Release("Album", null, title: "Different song")]).Type == ReleaseType.Unknown,
            "A lone track whose title differs from its album remains Unknown");
        Check(ReleaseClassification.FromTracks([Release(" ", null)]).Type == ReleaseType.Unknown,
            "Blank album metadata cannot infer a single");
        Check(ReleaseClassification.FromTracks([Release("Album", null), Release("Album", null)]).Type == ReleaseType.Unknown,
            "A release with multiple tracks cannot use single-track inference");
        Check(ReleaseClassification.FromTracks([Release("Compilation", "album; compilation; live")]).Type == ReleaseType.Album,
            "Secondary compilation/live tags preserve the primary Album type");
        Check(ReleaseClassification.FromTracks([Release("A", "single"), Release("B", "EP")]).Type == ReleaseType.Unknown,
            "Conflicting primary tags require a manual classification");
        Check(ReleaseClassification.FromTracks([Release("A", " EP "), Release("B", null)]).Type == ReleaseType.EP,
            "A recognized release tag can classify untagged tracks in the same release");
        Check(ReleaseClassification.FromTracks([Release("A", "remix", title: "Different song")]).Type == ReleaseType.Unknown,
            "A secondary tag alone does not invent a primary type");

        var overrides = new FakeReleaseTypeStore();
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer(), releaseTypeStore: overrides);
        var album = Release("Dive", "album");
        var single = Release("A Walk", "single");
        var ep = Release("Coastal Break", "ep");
        var unknown = Release("Untagged release", null, title: "Different song");
        foreach (var track in new[] { album, single, ep, unknown }) vm.Tracks.Add(track);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        vm.SelectedPage = AppPage.Albums;
        Check(vm.VisibleMusicGroups.Select(g => g.ReleaseType).Distinct().Count() == 4,
            "Gallery exposes Album, Single, EP and Unknown classifications");
        vm.SelectedReleaseFilter = vm.ReleaseFilters.Single(c => c.Type == ReleaseType.Single);
        Check(vm.VisibleMusicGroups.Single().Name == "A Walk", "Singles filter excludes albums, EPs and unknown releases");
        vm.MusicSearchText = "does not match";
        Check(vm.VisibleMusicGroups.Count == 0, "Release type filters compose with search");
        vm.MusicSearchText = "";
        vm.SelectedReleaseFilter = vm.ReleaseFilters[0];
        vm.OpenMusicGroupCommand.Execute(vm.Albums.Single(g => g.Name == unknown.Album));
        var key = vm.SelectedAlbum!.Key;
        vm.SelectedReleaseTypeChoice = vm.ReleaseTypeChoices.Single(c => c.Type == ReleaseType.Single);
        Check(vm.SelectedAlbum!.ReleaseType == ReleaseType.Single && overrides.Types[key] == ReleaseType.Single,
            "Manual classification updates the open release and persists its override");
        vm.Tracks[vm.Tracks.IndexOf(unknown)] = Release(unknown.Album!, "album");
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(vm.SelectedAlbum!.ReleaseType == ReleaseType.Single, "Manual release type wins over refreshed metadata");
        overrides.FailSave = true;
        vm.SelectedReleaseTypeChoice = vm.ReleaseTypeChoices.Single(c => c.Type == ReleaseType.EP);
        Check(vm.ReleaseTypeError is not null && vm.SelectedReleaseTypeChoice.Type == ReleaseType.Single && vm.SelectedAlbum.ReleaseType == ReleaseType.Single,
            "Failed override saves show an error and restore the saved selection");
        overrides.FailSave = false;
        vm.SelectedReleaseTypeChoice = vm.ReleaseTypeChoices[0];
        Check(vm.SelectedAlbum!.ReleaseType == ReleaseType.Album && !overrides.Types.ContainsKey(key),
            "Automatic removes the manual override and restores tag-based classification");
        vm.SelectedPage = AppPage.Artists;
        vm.OpenMusicGroupCommand.Execute(vm.Artists.Single());
        vm.SelectedReleaseFilter = vm.ReleaseFilters.Single(c => c.Type == ReleaseType.EP);
        Check(vm.VisibleMusicGroups.Single().Name == ep.Album, "Artist release galleries support the same type filters");
        vm.Queue.Clear();
        vm.QueueMusicGroupCommand.Execute(null);
        Check(vm.Queue.Count == 4, "Filtering the release gallery does not silently narrow artist playback");

        var root = Path.Combine(Path.GetTempPath(), "MusicPlayerReleaseTypes", Guid.NewGuid().ToString("N"));
        FixtureLibrary.Write(root);
        var audioPath = Path.Combine(root, "01.wav");
        using (var file = TagLib.File.Create(audioPath))
        {
            file.GetTag(TagLib.TagTypes.Id3v2, true).MusicBrainzReleaseType = "single";
            file.Save();
        }
        var metadata = new TagLibMetadataService();
        await CheckMediaTypeTagsAsync(root, metadata);
        var inferredPath = Path.Combine(root, "02.wav");
        var companionPath = Path.Combine(root, "03.wav");
        void SetTags(string path, string title, string albumName)
        {
            using var file = TagLib.File.Create(path);
            file.Tag.Title = title;
            file.Tag.Album = albumName;
            file.Tag.Performers = ["Rescan artist"];
            file.Save();
        }
        SetTags(inferredPath, "Inferred release", "Inferred release");
        SetTags(companionPath, "Second song", "Another release");
        var rescanOverrides = new FakeReleaseTypeStore();
        using (var rescanned = new MainViewModel(new Picker(), new Picker(), metadata, new Scanner(), new FakePlayer(),
            libraryRefreshService: new LibraryRefreshService(metadata), monitorLibrary: false, releaseTypeStore: rescanOverrides))
        {
            rescanned.Tracks.Add(metadata.ReadTrack(inferredPath));
            rescanned.Tracks.Add(metadata.ReadTrack(companionPath));
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            rescanned.SelectedPage = AppPage.Albums;
            rescanned.OpenMusicGroupCommand.Execute(rescanned.Albums.Single(g => g.Name == "Inferred release"));
            rescanned.SelectedReleaseFilter = rescanned.ReleaseFilters.Single(c => c.Type == ReleaseType.Single);
            Check(rescanned.SelectedAlbum!.ReleaseType == ReleaseType.Single, "Library groups apply single inference automatically");
            SetTags(companionPath, "Second song", "Inferred release");
            await rescanned.RescanLibraryCommand.ExecuteAsync(null);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(rescanned.SelectedAlbum!.Tracks.Count == 2 && rescanned.SelectedAlbum.ReleaseType == ReleaseType.Unknown &&
                !rescanned.VisibleMusicGroups.Any(g => g.Key == rescanned.SelectedAlbum.Key),
                "Rescan removes inferred Single status and updates the open detail/filter when a second track joins");
            SetTags(companionPath, "Second song", "Another release");
            await rescanned.RescanLibraryCommand.ExecuteAsync(null);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(rescanned.SelectedAlbum!.ReleaseType == ReleaseType.Single, "Rescan restores inferred Single status when only the matching track remains");
            SetTags(inferredPath, "Different title", "Inferred release");
            await rescanned.RescanLibraryCommand.ExecuteAsync(null);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(rescanned.SelectedAlbum!.ReleaseType == ReleaseType.Unknown && rescanOverrides.Types.Count == 0,
                "Rescan reclassifies renamed tracks without persisting inference as a manual override");
            rescanned.SelectedReleaseTypeChoice = rescanned.ReleaseTypeChoices.Single(c => c.Type == ReleaseType.Album);
            SetTags(inferredPath, "Inferred release", "Inferred release");
            await rescanned.RescanLibraryCommand.ExecuteAsync(null);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(rescanned.SelectedAlbum!.ReleaseType == ReleaseType.Album, "Manual Album classification takes priority over inference after rescan");
            rescanned.SelectedReleaseTypeChoice = rescanned.ReleaseTypeChoices[0];
            Check(rescanned.SelectedAlbum!.ReleaseType == ReleaseType.Single, "Returning to Automatic re-applies the current single-track rule");
        }
        var tagged = metadata.ReadTrack(audioPath);
        Check(tagged.ReleaseTypeTag == "single" && tagged.WithFileState(null, null, true).ReleaseTypeTag == "single",
            "Real embedded release-type tags are read and retained for missing-file metadata");
        var databasePath = Path.Combine(root, "music.db");
        using (var db = new MusicDbContext(new DbContextOptionsBuilder<MusicDbContext>().UseSqlite($"Data Source={databasePath}").Options))
        {
            db.GetService<IMigrator>().Migrate(db.Database.GetMigrations().Single(m => m.EndsWith("_TrackLibraryMembership")));
            db.StorageStates.Add(new StorageState { Key = "LegacyJsonImported" });
            db.SaveChanges();
            db.Database.ExecuteSqlInterpolated($"INSERT INTO Tracks (PathKey, FilePath, Title, Artist, Album, DurationTicks, FileSize, LastWriteTimeUtcTicks, IsMissing, ExplicitlyAddedToLibrary) VALUES ({audioPath.ToUpperInvariant()}, {audioPath}, {tagged.Title}, {tagged.Artist}, {tagged.Album}, {tagged.Duration.Ticks}, {tagged.FileSize}, {tagged.LastWriteTimeUtcTicks}, 0, 1)");
        }
        var store = new SqliteMusicStore(databasePath);
        var restored = store.LoadLibrary().Single();
        Check(restored.LastWriteTimeUtcTicks is null && restored.ReleaseTypeTag is null,
            "Upgrading existing libraries schedules a metadata refresh without losing tracks");
        var refresh = await new LibraryRefreshService(metadata).RefreshAsync([restored], [], false, CancellationToken.None);
        Check(refresh.Updates.Single().ReleaseTypeTag == "single", "Normal refresh backfills release tags in existing libraries");
        store.SaveLibrary(refresh.Updates);
        Check(new SqliteMusicStore(databasePath).LoadLibrary().Single().ReleaseTypeTag == "single",
            "Release metadata survives SQLite restart");
        var originalAudio = SHA256.HashData(File.ReadAllBytes(audioPath));
        store.SaveReleaseType(key, ReleaseType.EP);
        Check(new SqliteMusicStore(databasePath).LoadReleaseTypes()[key] == ReleaseType.EP,
            "Release overrides with normalized artist/album keys survive SQLite restart");
        store.SaveReleaseType(key, null);
        Check(!new SqliteMusicStore(databasePath).LoadReleaseTypes().ContainsKey(key) &&
            originalAudio.SequenceEqual(SHA256.HashData(File.ReadAllBytes(audioPath))),
            "Automatic deletes the override while leaving audio file bytes unchanged");

        vm.SelectedPage = AppPage.Albums;
        var window = new MainWindow(vm);
        var content = (FrameworkElement)window.Content;
        content.SetValue(Panel.BackgroundProperty, window.Background);
        async Task Capture(string name, int width = 1180, int height = 680)
        {
            void Layout()
            {
                content.Measure(new Size(width, height));
                content.Arrange(new Rect(0, 0, width, height));
                content.UpdateLayout();
            }
            Layout();
            await Task.WhenAll(Descendants(content).OfType<PlaylistCover>().Select(c => c.ArtworkReady));
            Layout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            SaveTaskbarImage(bitmap, name + ".png");
        }
        await Capture("release-type-gallery");
        var view = Descendants(content).OfType<MusicBrowserView>().Single();
        var filter = (ComboBox)view.FindName("ReleaseTypeFilter");
        filter.SelectedItem = vm.ReleaseFilters.Single(c => c.Type == ReleaseType.Single);
        Check(vm.VisibleMusicGroups.Single().ReleaseType == ReleaseType.Single, "Visible release filter updates the gallery binding");
        vm.OpenMusicGroupCommand.Execute(vm.VisibleMusicGroups.Single());
        vm.IsQueueOpen = true;
        await Capture("release-type-detail", 884, 561);
        var editor = (ComboBox)view.FindName("ReleaseTypeEditor");
        editor.SelectedItem = vm.ReleaseTypeChoices.Single(c => c.Type == ReleaseType.EP);
        Check(vm.SelectedAlbum!.ReleaseType == ReleaseType.EP, "Visible release editor updates manual classification");
        window.DataContext = null;
        window.Close();
    }

    private static async Task CheckMediaTypeTagsAsync(string root, TagLibMetadataService metadata)
    {
        var path = Path.Combine(root, "release-type.flac");
        // A metadata-only FLAC fixture: STREAMINFO for mono, 16-bit, 44.1 kHz.
        var streamInfo = new byte[34];
        streamInfo[0] = streamInfo[2] = 0x10; // Block size: 4096 samples.
        var format = (44100UL << 44) | (15UL << 36);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(streamInfo.AsSpan(10, 8), format);
        using (var output = File.Create(path))
        {
            output.Write("fLaC"u8);
            output.Write(new byte[] { 0x80, 0, 0, 34 });
            output.Write(streamInfo);
        }
        void SetTags(string[] mediaTypes, string? primary = null)
        {
            using var file = TagLib.File.Create(path);
            var comments = (TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph, true);
            comments.Title = "Different song";
            comments.Album = "Test release";
            comments.SetField("MEDIATYPE", mediaTypes);
            comments.MusicBrainzReleaseType = primary;
            file.Save();
        }

        foreach (var (value, expected) in new[]
        {
            (" AlBuM ", ReleaseType.Album), ("single", ReleaseType.Single), ("EP", ReleaseType.EP),
            ("other", ReleaseType.Other), ("broadcast", ReleaseType.Other),
            ("CD", ReleaseType.Unknown), ("vinyl", ReleaseType.Unknown), ("compilation", ReleaseType.Unknown)
        })
        {
            SetTags([value]);
            Check(ReleaseClassification.FromTracks([metadata.ReadTrack(path)]).Type == expected,
                $"FLAC MEDIATYPE '{value}' resolves to {expected}");
        }
        SetTags(["album; compilation", " Album "]);
        Check(metadata.ReadTrack(path).ReleaseTypeTag == "album", "MEDIATYPE accepts lists and ignores secondary types and duplicates");
        SetTags(["album", "single"]);
        Check(ReleaseClassification.FromTracks([metadata.ReadTrack(path)]).Type == ReleaseType.Unknown,
            "Conflicting MEDIATYPE values remain Unknown");
        SetTags(["album"], "ep");
        Check(metadata.ReadTrack(path).ReleaseTypeTag == "ep", "MusicBrainz release type takes precedence over MEDIATYPE");
        SetTags(["album"], " ");
        Check(metadata.ReadTrack(path).ReleaseTypeTag == "album", "Blank MusicBrainz release type permits MEDIATYPE fallback");
        SetTags([]);
        Check(metadata.ReadTrack(path).ReleaseTypeTag is null, "Missing MEDIATYPE remains unset");

        SetTags(["album"]);
        var imported = metadata.ReadTrack(path);
        var cachedWithoutType = new Track
        {
            FilePath = path, Title = imported.Title, Album = imported.Album,
            FileSize = imported.FileSize, LastWriteTimeUtcTicks = imported.LastWriteTimeUtcTicks
        };
        var beforeRead = SHA256.HashData(File.ReadAllBytes(path));
        var refresh = await new LibraryRefreshService(metadata).RefreshAsync([cachedWithoutType], [], true, CancellationToken.None);
        Check(refresh.Failed == 0 && refresh.Updates.Single().ReleaseTypeTag == "album",
            "Forced rescan reads MEDIATYPE for previously imported files with unchanged timestamps");
        var storePath = Path.Combine(root, "mediatype.db");
        refresh.Updates.Single().ExplicitlyAddedToLibrary = true;
        new SqliteMusicStore(storePath).SaveLibrary(refresh.Updates);
        Check(new SqliteMusicStore(storePath).LoadLibrary().Single().ReleaseTypeTag == "album",
            "Rescanned MEDIATYPE persists across library reloads");
        Check(beforeRead.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))),
            "Reading and persisting MEDIATYPE does not modify music files");
    }

    private sealed class FakeReleaseTypeStore : IReleaseTypeStore
    {
        public Dictionary<string, ReleaseType> Types { get; } = new(StringComparer.Ordinal);
        public bool FailSave { get; set; }
        public IReadOnlyDictionary<string, ReleaseType> LoadReleaseTypes() => Types;
        public void SaveReleaseType(string releaseKey, ReleaseType? type)
        {
            if (FailSave) throw new IOException("Read-only library");
            if (type is { } value) Types[releaseKey] = value;
            else Types.Remove(releaseKey);
        }
    }
}
