using System.Windows;
using System.Windows.Controls;
using MusicPlayer.Models;
using MusicPlayer.Services;

namespace MusicPlayer.Controls;

public partial class PlaylistCover : UserControl
{
    public static readonly DependencyProperty TracksProperty = DependencyProperty.Register(
        nameof(Tracks), typeof(IReadOnlyList<Track>), typeof(PlaylistCover),
        new PropertyMetadata(null, (control, _) => ((PlaylistCover)control).RefreshCover()));

    public IReadOnlyList<Track>? Tracks
    {
        get => (IReadOnlyList<Track>?)GetValue(TracksProperty);
        set => SetValue(TracksProperty, value);
    }

    private int _generation;
    private CancellationTokenSource? _artworkCancellation;
    private IReadOnlyList<Track>? _requestedTracks;
    public Task ArtworkReady { get; private set; } = Task.CompletedTask;

    public PlaylistCover()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshCover();
        Unloaded += (_, _) =>
        {
            _generation++;
            _artworkCancellation?.Cancel();
        };
    }

    private void RefreshCover()
    {
        // Binding and Loaded both fire for a new card. Keep its existing request/result.
        if (ReferenceEquals(_requestedTracks, Tracks) && _artworkCancellation is { IsCancellationRequested: false }) return;
        _requestedTracks = Tracks;
        ArtworkReady = LoadCoverAsync();
    }

    private async Task LoadCoverAsync()
    {
        if (CoverImages is null) return;
        var generation = ++_generation;
        _artworkCancellation?.Cancel();
        _artworkCancellation?.Dispose();
        _artworkCancellation = new CancellationTokenSource();
        var cancellationToken = _artworkCancellation.Token;
        CoverImages.ItemsSource = null;
        var tracks = Tracks;
        if (tracks is null || tracks.Count == 0) return;
        try
        {
            // Cancellation stops this recycled control waiting; shared cache generation continues for other tiles.
            var covers = await PlaylistArtworkService.LoadCachedAsync(tracks, cancellationToken).ConfigureAwait(false);
            if (Dispatcher.HasShutdownStarted) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (generation == _generation) CoverImages.ItemsSource = covers;
            });
        }
        catch (OperationCanceledException)
        {
        }
    }
}

public sealed class PlaylistCoverPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(double.IsInfinity(availableSize.Width) ? 150 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 150 : availableSize.Height);
        for (var i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Measure(GetCell(size, i).Size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Arrange(GetCell(finalSize, i));
        return finalSize;
    }

    private Rect GetCell(Size size, int i) => InternalChildren.Count switch
    {
        1 => new Rect(0, 0, size.Width, size.Height),
        2 => new Rect(i * size.Width / 2, 0, size.Width / 2, size.Height),
        3 when i == 0 => new Rect(0, 0, size.Width / 2, size.Height),
        3 => new Rect(size.Width / 2, (i - 1) * size.Height / 2, size.Width / 2, size.Height / 2),
        _ => new Rect(i % 2 * size.Width / 2, i / 2 * size.Height / 2, size.Width / 2, size.Height / 2)
    };
}
