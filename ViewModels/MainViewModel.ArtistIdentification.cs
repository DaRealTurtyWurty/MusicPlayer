using System.Diagnostics;
using System.Windows.Threading;
using MusicPlayer.Models;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

public partial class MainViewModel
{
    private IArtistIdentityService? _artistIdentityService;
    private CancellationTokenSource? _artistIdentificationCancellation;
    private DispatcherTimer? _artistIdentificationRetry;
    public Task ArtistIdentificationReady { get; private set; } = Task.CompletedTask;

    private void InitializeArtistIdentification()
    {
        if (_artistIdentityService is null) return;
        _artistIdentificationRetry = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _artistIdentificationRetry.Tick += OnArtistIdentificationRetry;
        _artistIdentificationRetry.Start();
    }

    private void OnArtistIdentificationRetry(object? sender, EventArgs e)
    {
        if (ArtistIdentificationReady.IsCompleted && Artists.Any(g => g.Identification.Result?.Status == ArtistIdentityStatus.Unavailable))
            IdentifyArtists();
    }

    private void IdentifyArtists()
    {
        _artistIdentificationCancellation?.Cancel();
        _artistIdentificationCancellation?.Dispose();
        if (_artistIdentityService is not { } service) return;
        _artistIdentificationCancellation = new CancellationTokenSource();
        var token = _artistIdentificationCancellation.Token;
        // Known tagged IDs should not wait behind network searches for other artists.
        var groups = Artists.OrderByDescending(g => g.Tracks.Any(t => MusicBrainzId.Normalize(t.MusicBrainzArtistId) is not null)).ToArray();
        ArtistIdentificationReady = Task.Run(async () =>
        {
            try
            {
                foreach (var group in groups)
                {
                    token.ThrowIfCancellationRequested();
                    var result = await service.IdentifyAsync(group.Name, group.Tracks, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    await _positionTimer.Dispatcher.InvokeAsync(() =>
                    {
                        if (!token.IsCancellationRequested && !_musicBrowserDisposed) group.Identification.Result = result;
                    });
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { Trace.TraceWarning($"Artist identification failed: {ex.Message}"); }
        });
    }
}
