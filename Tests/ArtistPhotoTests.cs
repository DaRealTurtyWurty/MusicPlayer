using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MusicPlayer.Controls;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;
using MusicPlayer.Views;

internal static partial class Program
{
    private static async Task CheckArtistPhotosAsync()
    {
        using var temporary = new TemporaryTestDirectory("MusicPlayerPhotoTests");
        using var handler = new PhotoHttpHandler(CreateBrowserArtwork("artist photo"));
        using var client = new HttpClient(handler);
        var cache = Path.Combine(temporary.Path, "cache");
        var service = new ArtistPhotoService(cache, client);
        var results = await Task.WhenAll(service.LoadAsync(ArtistA), service.LoadAsync(ArtistA));
        var photo = results[0];
        Check(photo is not null && ReferenceEquals(photo, results[1]) && handler.Requests.Count == 5,
            "Concurrent artist photos share the provider chain and image download");
        Check(photo!.Image.IsFrozen && Math.Max(photo.Image.PixelWidth, photo.Image.PixelHeight) <= 512,
            "Photos decode off the UI thread into bounded, frozen thumbnails");
        Check(photo.Attribution.Credit == "Test Photographer" && photo.Attribution.License == "CC BY 4.0" &&
              photo.Attribution.LicenseUrl == "https://creativecommons.org/licenses/by/4.0/",
            "Commons photographer and licence metadata are preserved with HTML removed");
        Check(handler.Keys.All(k => k is null), "The photo chain needs no user-supplied API key");
        handler.Offline = true;
        var restarted = await new ArtistPhotoService(cache, client).LoadAsync(ArtistA);
        Check(restarted?.Attribution == photo.Attribution && handler.Requests.Count == 5,
            "A restart loads cached photos and attribution without network access");
        var entryPath = Directory.GetFiles(cache, "*.json").Single();
        var entry = JsonNode.Parse(File.ReadAllText(entryPath))!;
        entry["ExpiresAt"] = DateTimeOffset.UtcNow.AddDays(-1).ToString("O");
        File.WriteAllText(entryPath, entry.ToJsonString());
        var stale = await new ArtistPhotoService(cache, client).LoadAsync(ArtistA);
        Check(stale?.Attribution == photo.Attribution, "Expired cached photos remain visible when offline");
        handler.Offline = false;
        File.WriteAllText(entryPath, "broken cache");
        Check(await new ArtistPhotoService(cache, client).LoadAsync(ArtistA) is not null,
            "Corrupt disk entries are replaced by a fresh photo lookup");

        using var missingHandler = new PhotoHttpHandler(CreateBrowserArtwork("missing")) { NoImage = true };
        using var missingClient = new HttpClient(missingHandler);
        var missingPath = Path.Combine(temporary.Path, "missing");
        Check(await new ArtistPhotoService(missingPath, missingClient).LoadAsync(ArtistA) is null,
            "Artists with no Wikidata photo return a placeholder result");
        var missingCount = missingHandler.Requests.Count;
        await new ArtistPhotoService(missingPath, missingClient).LoadAsync(ArtistA);
        Check(missingHandler.Requests.Count == missingCount, "Confirmed missing photos are cached across restarts");

        using var retryHandler = new PhotoHttpHandler(CreateBrowserArtwork("retry")) { Offline = true };
        using var retryClient = new HttpClient(retryHandler);
        var retryPath = Path.Combine(temporary.Path, "retry");
        Check(await new ArtistPhotoService(retryPath, retryClient).LoadAsync(ArtistA) is null && !Directory.Exists(retryPath),
            "Temporary failures are not written as persistent missing photos");
        retryHandler.Offline = false;
        Check(await new ArtistPhotoService(retryPath, retryClient).LoadAsync(ArtistA) is not null,
            "A failed lookup can recover once the provider is available");

        using var fanartHandler = new PhotoHttpHandler(CreateBrowserArtwork("fanart"));
        using var fanartClient = new HttpClient(fanartHandler);
        var fanartPath = Path.Combine(temporary.Path, "fanart");
        var fanart = await new ArtistPhotoService(fanartPath, fanartClient, "test-project-key").LoadAsync(ArtistA);
        Check(fanart?.Attribution.Provider == "Fanart.tv" && fanartHandler.Requests.Count == 3 &&
              fanartHandler.Requests[0].Host == "www.theaudiodb.com",
            "Missing AudioDB photos fall back to Fanart.tv before Commons");
        Check(fanartHandler.Keys[0] is null && fanartHandler.Keys[1] == "test-project-key" && fanartHandler.Keys[2] is null &&
              !File.ReadAllText(Directory.GetFiles(fanartPath).Single()).Contains("test-project-key"),
            "The project key is sent only to Fanart's API and never persisted with photos");
        fanartHandler.FanartUnavailable = true;
        var fallback = await new ArtistPhotoService(Path.Combine(temporary.Path, "fallback"), fanartClient, "test-project-key").LoadAsync(ArtistB);
        Check(fallback?.Attribution.Provider == "Wikimedia Commons", "Fanart failures fall back to Commons");

        using var corruptHandler = new PhotoHttpHandler([1, 2, 3]);
        using var corruptClient = new HttpClient(corruptHandler);
        var corruptPath = Path.Combine(temporary.Path, "corrupt");
        Check(await new ArtistPhotoService(corruptPath, corruptClient).LoadAsync(ArtistA) is null && !Directory.Exists(corruptPath),
            "Invalid image bytes cannot poison the persistent cache");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { await service.LoadAsync(ArtistA, cancellation.Token); throw new Exception("Photo cancellation was ignored"); }
        catch (OperationCanceledException) { }
        Check(await service.LoadAsync("../invalid") is null, "Invalid artist IDs never become cache paths or network requests");

        await CheckAudioDbPhotosAsync(temporary.Path);
        await CheckDeezerPhotosAsync(temporary.Path);
        await CheckDeezerSearchAsync(temporary.Path);
        await CheckCustomArtistPhotosAsync(temporary.Path);
        await CheckArtistPhotoDpiAsync(temporary.Path);
        await CheckArtistPhotoControlsAsync(new FixedPhotoService(photo), photo);
        Console.WriteLine("Artist photo tests passed.");
    }

    private static async Task CheckAudioDbPhotosAsync(string directory)
    {
        using var handler = new PhotoHttpHandler(CreateBrowserArtwork("AudioDB")) { AudioDbFound = true };
        using var client = new HttpClient(handler);
        var cache = Path.Combine(directory, "audiodb");
        var photo = await new ArtistPhotoService(cache, client, "test-project-key").LoadAsync(ArtistA);
        Check(photo?.Attribution.Provider == "TheAudioDB" && handler.Requests.Count == 2 &&
              handler.Requests[0].AbsolutePath == "/api/v1/json/123/artist-mb.php" &&
              handler.Requests[0].Query == "?i=" + ArtistA && handler.Keys.All(k => k is null),
            "TheAudioDB is first, uses the public key and exact MBID, and skips other providers on success");
        Check(photo!.Attribution.SourceUrl == "https://www.theaudiodb.com/artist/123456",
            "AudioDB source credits are preserved without inventing a Creative Commons licence");
        var entryPath = Directory.GetFiles(cache, "*.json").Single();
        var legacy = JsonNode.Parse(File.ReadAllText(entryPath))!;
        legacy.AsObject().Remove("ProviderRevision");
        legacy["Attribution"]!["Provider"] = "Wikimedia Commons";
        File.WriteAllText(entryPath, legacy.ToJsonString());
        handler.Offline = true;
        Check((await new ArtistPhotoService(cache, client, "test-project-key").LoadAsync(ArtistA))?.Attribution.Provider == "Wikimedia Commons",
            "Legacy cached photos remain available offline during the provider upgrade");
        handler.Offline = false;
        Check((await new ArtistPhotoService(cache, client, "test-project-key").LoadAsync(ArtistA))?.Attribution.Provider == "TheAudioDB",
            "Old fallback photos refresh immediately despite their unexpired 30-day cache");
        legacy["Image"] = null;
        legacy["Attribution"] = null;
        File.WriteAllText(entryPath, legacy.ToJsonString());
        Check((await new ArtistPhotoService(cache, client, "test-project-key").LoadAsync(ArtistA))?.Attribution.Provider == "TheAudioDB",
            "Old cached misses are retried with the new provider chain");
        handler.AudioDbWrongArtist = true;
        var mismatch = await new ArtistPhotoService(Path.Combine(directory, "mismatch"), client, "test-project-key").LoadAsync(ArtistA);
        Check(mismatch?.Attribution.Provider == "Fanart.tv", "A mismatched AudioDB MBID cannot supply an artist's photo");
        handler.AudioDbWrongArtist = false;
        handler.AudioDbCorruptImage = true;
        var corrupt = await new ArtistPhotoService(Path.Combine(directory, "audiodb-corrupt"), client, "test-project-key").LoadAsync(ArtistA);
        Check(corrupt?.Attribution.Provider == "Fanart.tv", "Corrupt AudioDB thumbnails fall back to Fanart.tv");
        handler.AudioDbCorruptImage = false;
        handler.AudioDbUnavailable = true;
        var outagePath = Path.Combine(directory, "audiodb-outage");
        var outage = await new ArtistPhotoService(outagePath, client, "test-project-key").LoadAsync(ArtistA);
        var outageEntry = JsonNode.Parse(File.ReadAllText(Directory.GetFiles(outagePath).Single()))!;
        Check(outage?.Attribution.Provider == "Fanart.tv" &&
              DateTimeOffset.Parse(outageEntry["ExpiresAt"]!.GetValue<string>()) < DateTimeOffset.UtcNow.AddHours(2),
            "Temporary AudioDB outages do not pin fallback photos for 30 days");
        handler.FanartUnavailable = true;
        handler.NoImage = true;
        var missingPath = Path.Combine(directory, "audiodb-outage-missing");
        Check(await new ArtistPhotoService(missingPath, client, "test-project-key").LoadAsync(ArtistA) is null && !Directory.Exists(missingPath),
            "Provider outages cannot become persistent missing-photo results");
        Check(handler.AudioDbRequests.Zip(handler.AudioDbRequests.Skip(1)).All(p => p.Second - p.First >= TimeSpan.FromSeconds(2)),
            "AudioDB requests across service instances stay below the free 30-per-minute limit");
    }

    private static async Task CheckArtistPhotoDpiAsync(string directory)
    {
        var pixels = new byte[64 * 64 * 4];
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            var offset = (y * 64 + x) * 4;
            pixels[offset] = (byte)(x * 4);
            pixels[offset + 1] = (byte)(y * 4);
            pixels[offset + 2] = 180;
            pixels[offset + 3] = 255;
        }
        var source = BitmapSource.Create(64, 64, 96, 1, PixelFormats.Bgra32, null, pixels, 64 * 4);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        var raw = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Check(raw.DpiX / raw.DpiY > 50, "Regression fixture reproduces the 96-by-1 DPI artist JPEG metadata");
        using var handler = new PhotoHttpHandler(stream.ToArray()) { AudioDbFound = true };
        using var client = new HttpClient(handler);
        var cache = Path.Combine(directory, "dpi");
        var photo = await new ArtistPhotoService(cache, client).LoadAsync(ArtistA);
        Check(photo is not null && photo.Image.DpiX == 96 && photo.Image.DpiY == 96 &&
              photo.Image.Width == photo.Image.PixelWidth && photo.Image.Height == photo.Image.PixelHeight,
            "Malformed image DPI is normalized so square photos cannot stretch into cropped gradients");
        handler.Offline = true;
        var cached = await new ArtistPhotoService(cache, client).LoadAsync(ArtistA);
        Check(cached?.Image.DpiY == 96 && cached.Image.Width == cached.Image.Height,
            "Existing disk-cached photo bytes also receive the DPI fix without a fresh download");
        await CheckArtistPhotoControlsAsync(new FixedPhotoService(photo!), photo!);
    }

    private static async Task CheckArtistPhotoControlsAsync(IArtistPhotoService photos, ArtistPhoto expected, bool live = false,
        string artistId = ArtistA, string artistName = "Radiohead", string trackTitle = "Test track", string? albumTitle = null)
    {
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), new FakePlayer(),
            artistIdentityService: new MusicBrainzArtistService(), artistPhotoService: photos);
        vm.Tracks.Add(new Track { FilePath = "artist-test.wav", Title = trackTitle, Artist = artistName,
            Album = albumTitle ?? (artistName == "Radiohead" ? "OK Computer" : "Test album"), MusicBrainzArtistId = artistId });
        vm.SelectedPage = AppPage.Artists;
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await vm.ArtistIdentificationReady;
        var view = new MusicBrowserView { DataContext = vm, Background = new SolidColorBrush(Color.FromRgb(30, 32, 36)) };
        using var source = new HwndSource(new HwndSourceParameters("Artist photo verification")
            { Width = 900, Height = 600, WindowStyle = unchecked((int)0x80000000) });
        source.RootVisual = view;
        async Task Layout()
        {
            view.Measure(new Size(900, 600));
            view.Arrange(new Rect(0, 0, 900, 600));
            view.UpdateLayout();
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
        await Layout();
        var artwork = Descendants(view).OfType<ArtistArtwork>().Single(a => a.IsVisible);
        await artwork.ArtworkReady;
        await Layout();
        Check(artwork.Photo?.Attribution == expected.Attribution, "Artist gallery controls bind the resolved ID and load the photo");
        Check(!Descendants(view).OfType<PlaylistCover>().Any(c => c.IsVisible), "Artist cards display photos rather than album collages");
        Check(artwork.ContextMenu?.Items.OfType<MenuItem>().Any(i => Equals(i.Header, "Photo source and credits")) == true && artwork.ToolTip?.ToString()?.Contains(expected.Attribution.Credit) == true,
            "Photo credits, source and licence are accessible from the artist artwork");
        Check(!Descendants(artwork).OfType<TextBlock>().Any(t => t.IsVisible),
            "Artist gallery photos have no text overlay");
        void Capture(string name)
        {
            var bitmap = new RenderTargetBitmap(900, 600, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            SaveTaskbarImage(bitmap, name);
        }
        if (live) Capture($"artist-photos-{artistName.ToLowerInvariant().Replace(' ', '-')}-gallery.png");
        vm.OpenMusicGroupCommand.Execute(vm.Artists.Single());
        await Layout();
        var header = (Grid)view.FindName("MusicGroupHeader");
        var detail = Descendants(header).OfType<ArtistArtwork>().Single();
        await detail.ArtworkReady;
        await Layout();
        Check(detail.Photo?.Attribution == expected.Attribution, "The artist detail header displays the same photo and credits");
        Check(!Descendants(detail).OfType<TextBlock>().Any(t => t.IsVisible) &&
              detail.ToolTip?.ToString() == expected.Attribution.Description && detail.ContextMenu is not null,
            "Artist detail photos keep credits in the tooltip and context menu without an overlay");
        if (live) Capture($"artist-photos-{artistName.ToLowerInvariant().Replace(' ', '-')}-detail.png");
        vm.SelectedPage = AppPage.Albums;
        await Layout();
        Check(Descendants(view).OfType<PlaylistCover>().Any(c => c.IsVisible) &&
              !Descendants(view).OfType<ArtistArtwork>().Any(c => c.IsVisible), "Album views retain embedded cover artwork");
        source.RootVisual = null;
        view.DataContext = null;

        var deferred = new DeferredPhotoService();
        var recycled = new ArtistArtwork { ArtistId = ArtistA, Service = deferred, Width = 150, Height = 150 };
        source.RootVisual = recycled;
        recycled.Measure(new Size(150, 150));
        recycled.Arrange(new Rect(0, 0, 150, 150));
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await deferred.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = recycled.ArtworkReady;
        recycled.ArtistId = ArtistB;
        await recycled.ArtworkReady;
        deferred.Complete.TrySetResult(expected);
        await pending;
        Check(recycled.Photo is null && deferred.Cancelled, "Recycled artist controls cancel old requests and reject stale photos");
        recycled.Visibility = Visibility.Collapsed;
        var calls = deferred.Calls;
        recycled.ArtistId = ArtistA;
        Check(deferred.Calls == calls, "Hidden artist controls do not request photos");
        source.RootVisual = null;
    }

    private static async Task CheckArtistPhotoLiveAsync()
    {
        using var temporary = new TemporaryTestDirectory("MusicPlayerPhotoLive");
        var service = new ArtistPhotoService(temporary.Path, fanartApiKey: ArtistPhotoService.ConfiguredFanartApiKey);
        var photo = await service.LoadAsync(ArtistA);
        Check(photo is not null, "Live Radiohead artist photo resolves and downloads");
        Check(photo!.Attribution.Provider == "TheAudioDB", "The public key retrieves a real AudioDB artist photo");
        Console.WriteLine(photo!.Attribution.Description);
        SaveTaskbarImage(photo.Image, "artist-photo-radiohead.png");
        await CheckArtistPhotoControlsAsync(service, photo, live: true);
        foreach (var (name, id) in new[]
                 {
                     ("Lola Young", "43b896c5-5652-4b04-bde1-2e4224bc99b3"),
                     ("Benson Boone", "62b914a7-d775-4bb4-bb5e-d46e7df115b5"),
                     ("Alex Warren", "609a2ea3-f446-4e31-8eef-9f7591afb55a"),
                     ("Addison Rae", "610b71d9-fa78-47d6-9073-083922e73840")
                 })
        {
            var artistPhoto = await service.LoadAsync(id);
            Check(artistPhoto?.Attribution.Provider == "TheAudioDB", $"Live AudioDB photo downloads for {name}, previously missing from Fanart");
        }
        foreach (var (name, id) in new[]
                 {
                     ("Taylor Swift", "20244d07-534f-4eff-b4d4-930878889970"),
                     ("Kenya Grace", "94c4459d-4c35-4bc6-8bca-c75107515c1c")
                 })
        {
            var artistPhoto = await service.LoadAsync(id);
            Check(artistPhoto is not null && artistPhoto.Image.Width == artistPhoto.Image.PixelWidth &&
                  artistPhoto.Image.Height == artistPhoto.Image.PixelHeight, $"Live {name} portrait has correct display dimensions");
            await CheckArtistPhotoControlsAsync(service, artistPhoto!, live: true, artistId: id, artistName: name);
        }
    }

    private sealed class FixedPhotoService(ArtistPhoto photo) : IArtistPhotoService
    {
        public Task<ArtistPhoto?> LoadAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<ArtistPhoto?>(photo);
    }
    private sealed class DeferredPhotoService : IArtistPhotoService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ArtistPhoto?> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public int Calls { get; private set; }
        public Task<ArtistPhoto?> LoadAsync(string id, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (id != ArtistA) return Task.FromResult<ArtistPhoto?>(null);
            cancellationToken.Register(() => Cancelled = true);
            Started.TrySetResult();
            return Complete.Task; // Deliberately ignores cancellation to exercise the stale-result guard.
        }
    }

    private sealed class PhotoHttpHandler(byte[] bytes) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public List<string?> Keys { get; } = [];
        public bool Offline { get; set; }
        public bool NoImage { get; set; }
        public bool FanartUnavailable { get; set; }
        public bool AudioDbFound { get; set; }
        public bool AudioDbWrongArtist { get; set; }
        public bool AudioDbUnavailable { get; set; }
        public bool AudioDbCorruptImage { get; set; }
        public string? DeezerRelation { get; set; }
        public string DeezerResponseId { get; set; } = "55614432";
        public string DeezerImage { get; set; } = "https://cdn-images.dzcdn.net/images/artist/dc1a56a6f11bd8dfcd5d6b843f96f8c4/500x500-000000-80-0-0.jpg";
        public int? DeezerError { get; set; }
        public bool DeezerCorruptImage { get; set; }
        public List<DateTimeOffset> AudioDbRequests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            Keys.Add(request.Headers.TryGetValues("api-key", out var keys) ? keys.Single() : null);
            if (uri.Host == "www.theaudiodb.com") AudioDbRequests.Add(DateTimeOffset.UtcNow);
            if (Offline) throw new HttpRequestException("Offline");
            if (uri.Host == "www.theaudiodb.com" && AudioDbUnavailable) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            if (uri.Host == "r2.theaudiodb.com" && AudioDbCorruptImage)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
            if (uri.Host == "webservice.fanart.tv" && FanartUnavailable) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            if (uri.Host == "cdn-images.dzcdn.net" && DeezerCorruptImage)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
            var json = uri.Host switch
            {
                "www.theaudiodb.com" when AudioDbFound => System.Text.Json.JsonSerializer.Serialize(new
                {
                    artists = new[] { new { idArtist = "123456", strArtist = "Artist", strMusicBrainzID = AudioDbWrongArtist ? ArtistB : uri.Query[3..],
                        strArtistThumb = "https://r2.theaudiodb.com/images/media/artist/thumb/test.jpg" } }
                }),
                "www.theaudiodb.com" => """{"artists":null}""",
                "musicbrainz.org" => System.Text.Json.JsonSerializer.Serialize(new { relations = new[] {
                    new { type = "wikidata", url = new { resource = "https://www.wikidata.org/wiki/Q1" } },
                    new { type = "streaming", url = new { resource = DeezerRelation ?? "" } } } }),
                "api.deezer.com" when DeezerError is not null => System.Text.Json.JsonSerializer.Serialize(new { error = new { code = DeezerError } }),
                "api.deezer.com" => System.Text.Json.JsonSerializer.Serialize(new { id = long.Parse(DeezerResponseId), type = "artist", name = "Phoneboy", picture_big = DeezerImage }),
                "www.wikidata.org" when NoImage => """{"entities":{"Q1":{"claims":{}}}}""",
                "www.wikidata.org" => """{"entities":{"Q1":{"claims":{"P18":[{"rank":"normal","mainsnak":{"datavalue":{"value":"Artist photo.jpg"}}}]}}}}""",
                "commons.wikimedia.org" => """{"query":{"pages":{"1":{"imageinfo":[{"mime":"image/jpeg","thumburl":"https://upload.wikimedia.org/test/photo.jpg","descriptionurl":"https://commons.wikimedia.org/wiki/File:Artist_photo.jpg","extmetadata":{"Artist":{"value":"<a href='test'>Test Photographer</a>"},"LicenseShortName":{"value":"CC BY 4.0"},"LicenseUrl":{"value":"https://creativecommons.org/licenses/by/4.0/"},"ObjectName":{"value":"Artist photo"}}}]}}}}""",
                "webservice.fanart.tv" => """{"name":"Artist","artistthumb":[{"url":"https://assets.fanart.tv/test/photo.jpg","likes":"3"}],"albums":[]}""",
                _ => null
            };
            return Task.FromResult(json is not null ? ArtistJson(json) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
