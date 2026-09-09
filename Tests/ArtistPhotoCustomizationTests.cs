using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using MusicPlayer.Controls;
using MusicPlayer.Models;
using MusicPlayer.Services;

internal static partial class Program
{
    private static async Task CheckDeezerLiveAsync()
    {
        using var temporary = new TemporaryTestDirectory("MusicPlayerDeezerLive");
        var service = new ArtistPhotoService(temporary.Path, fanartApiKey: ArtistPhotoService.ConfiguredFanartApiKey);
        const string phoneboy = "5e428cda-a612-4789-9fb5-fabfda1df3e0";
        var photo = await service.LoadAsync(phoneboy, "Phoneboy");
        Check(photo?.Attribution.Provider == "Deezer", "Live Phoneboy photo resolves through the Deezer fallback");
        await CheckArtistPhotoControlsAsync(service, photo!, live: true, artistId: phoneboy, artistName: "Phoneboy");
        const string fanfan = "120d9bcc-4c26-43f4-aa1a-7bde7d89b77e";
        var fanPhoto = await service.LoadAsync(fanfan, "Fanfan", [new Track { FilePath = "fool.flac", Artist = "Fanfan", Album = "Fool", Title = "Fool" }]);
        Check(fanPhoto?.Attribution.SourceUrl == "https://www.deezer.com/artist/380623511",
            "Live fanfan resolves by exact album and song evidence without a MusicBrainz Deezer link");
        await CheckArtistPhotoControlsAsync(service, fanPhoto!, live: true, artistId: fanfan, artistName: "Fanfan", trackTitle: "Fool", albumTitle: "Fool");
    }

    private static async Task CheckDeezerPhotosAsync(string directory)
    {
        using var handler = new PhotoHttpHandler(CreateBrowserArtwork("Deezer"))
        {
            NoImage = true, DeezerRelation = "https://www.deezer.com/en/artist/55614432"
        };
        using var client = new HttpClient(handler);
        var cache = Path.Combine(directory, "deezer");
        var photo = await new ArtistPhotoService(cache, client).LoadAsync(ArtistA);
        Check(photo?.Attribution.Provider == "Deezer" && photo.Attribution.SourceUrl == "https://www.deezer.com/artist/55614432" &&
              handler.Requests.Count(u => u.Host == "musicbrainz.org") == 1 &&
              handler.Requests.Single(u => u.Host == "api.deezer.com").AbsolutePath == "/artist/55614432" && handler.Keys.All(k => k is null),
            "Deezer uses the linked artist ID without a key and reuses MusicBrainz relationships");
        var requestCount = handler.Requests.Count;
        handler.Offline = true;
        Check((await new ArtistPhotoService(cache, client).LoadAsync(ArtistA))?.Attribution == photo!.Attribution && handler.Requests.Count == requestCount,
            "Deezer photos and source credits survive restart without HTTP");
        handler.Offline = false;
        handler.NoImage = false;
        Check((await new ArtistPhotoService(Path.Combine(directory, "commons-before-deezer"), client).LoadAsync(ArtistA))?.Attribution.Provider == "Wikimedia Commons" &&
              handler.Requests.Count(u => u.Host == "api.deezer.com") == 1,
            "Deezer does not replace an existing Commons portrait");
        handler.NoImage = true;
        foreach (var relation in new[] { "https://deezer.com.evil.test/artist/55614432", "https://www.deezer.com/album/55614432", "https://www.deezer.com/artist/../55614432" })
        {
            handler.DeezerRelation = relation;
            var before = handler.Requests.Count(u => u.Host == "api.deezer.com");
            Check(await new ArtistPhotoService(Path.Combine(directory, Guid.NewGuid().ToString()), client).LoadAsync(ArtistA) is null &&
                  handler.Requests.Count(u => u.Host == "api.deezer.com") == before,
                "Untrusted/non-artist Deezer links cannot trigger an artist lookup");
        }
        handler.DeezerRelation = "https://www.deezer.com/artist/55614432";
        handler.DeezerResponseId = "123";
        Check(await new ArtistPhotoService(Path.Combine(directory, "deezer-wrong-id"), client).LoadAsync(ArtistA) is null,
            "A different Deezer artist ID cannot supply a photo");
        handler.DeezerResponseId = "55614432";
        var validImage = handler.DeezerImage;
        foreach (var image in new[] { validImage.Replace("dc1a56a6f11bd8dfcd5d6b843f96f8c4", "d41d8cd98f00b204e9800998ecf8427e"),
                     validImage.Replace("cdn-images.dzcdn.net", "evil.test"), validImage.Replace("/artist/", "/cover/") })
        {
            handler.DeezerImage = image;
            var before = handler.Requests.Count;
            Check(await new ArtistPhotoService(Path.Combine(directory, Guid.NewGuid().ToString()), client).LoadAsync(ArtistA) is null &&
                  handler.Requests.Skip(before).All(u => u.AbsoluteUri != image),
                "Deezer placeholders, album covers, and unexpected image hosts are rejected");
        }
        handler.DeezerImage = validImage;
        handler.DeezerError = 4;
        var failureCache = Path.Combine(directory, "deezer-error");
        Check(await new ArtistPhotoService(failureCache, client).LoadAsync(ArtistA) is null && !Directory.Exists(failureCache),
            "Deezer API errors cannot become persistent missing-photo results");
        handler.DeezerError = 800;
        var missingCache = Path.Combine(directory, "deezer-not-found");
        Check(await new ArtistPhotoService(missingCache, client).LoadAsync(ArtistA) is null && Directory.GetFiles(missingCache).Length == 1,
            "Deezer's confirmed missing-data response can be cached");
        handler.DeezerError = null;
        handler.DeezerCorruptImage = true;
        var corruptCache = Path.Combine(directory, "deezer-corrupt");
        Check(await new ArtistPhotoService(corruptCache, client).LoadAsync(ArtistA) is null && !Directory.Exists(corruptCache),
            "Corrupt Deezer image downloads cannot poison the cache");
        handler.DeezerCorruptImage = false;
        var oldMiss = JsonNode.Parse(File.ReadAllText(Directory.GetFiles(missingCache).Single()))!;
        oldMiss["ProviderRevision"] = 2;
        File.WriteAllText(Directory.GetFiles(missingCache).Single(), oldMiss.ToJsonString());
        Check((await new ArtistPhotoService(missingCache, client).LoadAsync(ArtistA))?.Attribution.Provider == "Deezer",
            "Existing cached misses refresh immediately after adding Deezer");
        Console.WriteLine("Deezer fallback tests passed.");
    }

    private static async Task CheckCustomArtistPhotosAsync(string directory)
    {
        var input = Path.Combine(directory, "chosen.png");
        var bytes = CreateBrowserArtwork("Chosen portrait");
        File.WriteAllBytes(input, bytes);
        using var handler = new PhotoHttpHandler(CreateBrowserArtwork("Automatic")) { AudioDbFound = true };
        using var client = new HttpClient(handler);
        var cache = Path.Combine(directory, "custom-photos");
        var service = new ArtistPhotoService(cache, client);
        var changes = 0;
        service.PhotoChanged += (_, _) => changes++;
        await service.SetCustomPhotoAsync("Unmatched artist", input);
        var custom = await service.LoadAsync(null, "Unmatched artist");
        Check(custom is { IsCustom: true } && custom.Image.IsFrozen && handler.Requests.Count == 0 && changes == 1,
            "Unidentified artists can have custom photos with no network requests");
        File.Move(input, input + ".moved");
        Check((await new ArtistPhotoService(cache, client).LoadAsync(ArtistA, " unmatched   ARTIST ")) is { IsCustom: true },
            "Custom photos survive restart, source-file moves, and later MusicBrainz identification");
        input += ".moved";
        var invalid = Path.Combine(directory, "invalid-image.png");
        File.WriteAllBytes(invalid, [1, 2, 3]);
        try { await service.SetCustomPhotoAsync("Unmatched artist", invalid); throw new Exception("Invalid image was accepted"); }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidDataException) { }
        Check((await service.LoadAsync(null, "Unmatched artist")) is { IsCustom: true } && changes == 1,
            "Invalid image selection preserves the previous custom photo");
        var oversized = Path.Combine(directory, "oversized-image.png");
        using (var stream = File.Create(oversized)) stream.SetLength(8 * 1024 * 1024 + 1);
        try { await service.SetCustomPhotoAsync("Unmatched artist", oversized); throw new Exception("Oversized image accepted"); }
        catch (InvalidDataException) { }
        Check((await service.LoadAsync(null, "Unmatched artist")) is { IsCustom: true } && changes == 1,
            "Oversized images are rejected before replacing the previous photo");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await service.SetCustomPhotoAsync("Unmatched artist", input, cancelled.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { }
        }
        Check((await service.LoadAsync(null, "Another artist")) is null, "Custom artwork is isolated by artist");
        await service.ResetCustomPhotoAsync("Unmatched artist");
        var automatic = await service.LoadAsync(ArtistA, "Unmatched artist");
        Check(automatic is { IsCustom: false } && automatic.Attribution.Provider == "TheAudioDB" && changes == 2,
            "Reset removes the override and restores automatic providers");
        await new ArtistPhotoService(Path.Combine(directory, "never-custom"), client).ResetCustomPhotoAsync("Artist");
        Check((await new ArtistPhotoService(cache, client).LoadAsync(ArtistA, "Unmatched artist")) is { IsCustom: false },
            "Reset remains effective across restart and is safe without an existing override");
        await service.SetCustomPhotoAsync("../unsafe artist/name", input);
        Check(Directory.GetFiles(Path.Combine(cache, "custom")).All(p => Path.GetFileNameWithoutExtension(p).Length == 64),
            "Custom filenames are hashed, not artist-controlled filesystem paths");
        var customFile = Directory.GetFiles(Path.Combine(cache, "custom")).Single();
        File.WriteAllText(customFile, "broken");
        Check((await service.LoadAsync(ArtistA, "../unsafe artist/name")) is { IsCustom: false },
            "A damaged custom photo falls back safely to automatic artwork");

        var picker = new TestArtistPhotoPicker();
        var first = new ArtistArtwork(picker) { ArtistName = "Menu artist", Service = service, Width = 150, Height = 150 };
        var second = new ArtistArtwork { ArtistName = "Menu artist", Service = service, Width = 150, Height = 150 };
        var panel = new StackPanel();
        panel.Children.Add(first);
        panel.Children.Add(second);
        using var source = new HwndSource(new HwndSourceParameters("Custom artist photo verification")
            { Width = 350, Height = 400, WindowStyle = unchecked((int)0x80000000) });
        source.RootVisual = panel;
        async Task Settle()
        {
            panel.Measure(new Size(350, 400));
            panel.Arrange(new Rect(0, 0, 350, 400));
            panel.UpdateLayout();
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.WhenAll(first.ArtworkReady, second.ArtworkReady);
        }
        await Settle();
        MenuItem Item(string label) => first.ContextMenu!.Items.OfType<MenuItem>().Single(i => Equals(i.Header, label));
        Check(first.Photo is null && Item("Choose artist photo").IsEnabled && Item("Reset to automatic").IsEnabled,
            "Missing and unidentified artist tiles expose both photo actions");
        Item("Choose artist photo").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await first.PhotoEditReady;
        Check(first.Photo is null, "Cancelling the image picker leaves artwork unchanged");
        picker.Path = input;
        Item("Choose artist photo").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await first.PhotoEditReady;
        await Settle();
        Check(first.Photo is { IsCustom: true } && second.Photo is { IsCustom: true },
            "Choosing a photo through the menu updates all visible views of that artist");
        first.ArtistId = ArtistA;
        await Settle();
        Check(first.Photo is { IsCustom: true }, "A newly resolved ID does not overwrite a manual choice");
        Item("Reset to automatic").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await first.PhotoEditReady;
        await Settle();
        Check(first.Photo is { IsCustom: false } && second.Photo is null,
            "The reset menu restores automatic artwork in every visible view");
        picker.OnPick = () => first.ArtistName = "Recycled artist";
        Item("Choose artist photo").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await first.PhotoEditReady;
        await Settle();
        Check(first.Photo is { IsCustom: false } && second.Photo is { IsCustom: true },
            "A tile recycled while the picker is open still saves to the originally selected artist");
        source.RootVisual = null;

        using var blocked = new BlockingAutomaticPhotoHandler(bytes);
        using var blockedClient = new HttpClient(blocked);
        var racingService = new ArtistPhotoService(Path.Combine(directory, "custom-race"), blockedClient);
        var pending = racingService.LoadAsync(ArtistA, "Slow artist");
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await racingService.SetCustomPhotoAsync("Slow artist", input).WaitAsync(TimeSpan.FromSeconds(5));
            Check((await racingService.LoadAsync(ArtistA, "Slow artist").WaitAsync(TimeSpan.FromSeconds(5))) is { IsCustom: true } && !pending.IsCompleted,
                "Custom photos bypass an already-stalled automatic lookup");
        }
        finally { blocked.Resume.TrySetResult(); }
        Check((await pending) is { IsCustom: true }, "A late provider result cannot overwrite the new custom photo");
        Console.WriteLine("Custom artist photo tests passed.");
    }

    private sealed class TestArtistPhotoPicker : IArtistPhotoFilePicker
    {
        public string? Path { get; set; }
        public Action? OnPick { get; set; }
        public string? PickPhoto(string artistName, Window? owner) { OnPick?.Invoke(); return Path; }
    }

    private sealed class BlockingAutomaticPhotoHandler(byte[] bytes) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.Host == "www.theaudiodb.com")
            {
                Started.TrySetResult();
                await Resume.Task.WaitAsync(token);
                return ArtistJson(System.Text.Json.JsonSerializer.Serialize(new { artists = new[] { new {
                    idArtist = "123", strArtist = "Slow artist", strMusicBrainzID = ArtistA,
                    strArtistThumb = "https://r2.theaudiodb.com/images/media/artist/thumb/test.jpg" } } }));
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }
}
