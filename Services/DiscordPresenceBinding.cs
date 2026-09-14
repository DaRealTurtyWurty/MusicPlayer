using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;
using MusicPlayer.Models;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Services;

public sealed class DiscordPresenceBinding : IDisposable
{
    private readonly MainViewModel _viewModel;
    private readonly Dispatcher _dispatcher;
    private readonly Func<string, IDiscordPresenceClient> _factory;
    private readonly TimeProvider _clock;
    private readonly IAlbumArtworkUrlResolver? _artworkResolver;
    private Track? _artworkTrack;
    private CancellationTokenSource? _artworkRequest;
    private string? _artworkUrl;
    private DateTimeOffset _artworkRetryAfter;
    private bool _timelineDirty = true;
    private readonly DispatcherTimer _timer;
    private IDiscordPresenceClient? _client;
    private Task _pendingShutdown = Task.CompletedTask;
    private DispatcherOperation? _pendingUpdate;
    private DiscordPresence? _lastPresence;
    private DateTimeOffset _nextPublish;
    private DateTimeOffset _retryAfter;
    private bool _dirty = true;
    private bool _disposed;

    public DiscordPresenceBinding(MainViewModel viewModel, Dispatcher dispatcher,
        Func<string, IDiscordPresenceClient>? factory = null, TimeProvider? clock = null,
        IAlbumArtworkUrlResolver? artworkResolver = null)
    {
        _viewModel = viewModel;
        _dispatcher = dispatcher;
        _factory = factory ?? (id => new DiscordPresenceClient(id));
        _clock = clock ?? TimeProvider.System;
        _artworkResolver = artworkResolver;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += OnTick;
        _viewModel.PropertyChanged += OnPropertyChanged;
        _viewModel.PlaybackSeeked += OnSeeked;
        Configure();
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.DiscordPresenceOptions)) Configure();
        else if (e.PropertyName is nameof(MainViewModel.CurrentTrack) or nameof(MainViewModel.IsPlaying)
                 or nameof(MainViewModel.IsPlaybackStopped) or nameof(MainViewModel.DurationSeconds)) ScheduleUpdate();
        // PositionSeconds also changes every 250ms during normal playback. Ignore it.
    }

    private void OnSeeked(object? sender, EventArgs e) => ScheduleUpdate();

    private void Configure()
    {
        _timer.Stop();
        CancelArtwork();
        ReleaseClient();
        _dirty = true;
        _timelineDirty = true;
        _nextPublish = default;
        _retryAfter = default;
        var options = _viewModel.DiscordPresenceOptions;
        if (!options.Enabled)
        {
            _viewModel.DiscordPresenceStatus = "Off";
            return;
        }
        if (!DiscordPresenceOptions.IsValidApplicationId(options.ApplicationId))
        {
            _viewModel.DiscordPresenceStatus = "Enter a valid Discord application ID";
            return;
        }
        _timer.Start();
        ScheduleUpdate();
    }

    private void ScheduleUpdate(bool timelineChanged = true)
    {
        if (_disposed) return;
        _dirty = true;
        _timelineDirty |= timelineChanged;
        if (_pendingUpdate?.Status == DispatcherOperationStatus.Pending) return;
        // A track load changes several properties. Read only the completed playback state.
        _pendingUpdate = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Flush));
    }

    private void OnTick(object? sender, EventArgs e) => Flush();

    private void Flush()
    {
        if (_disposed || !_timer.IsEnabled) return;
        var now = _clock.GetUtcNow();
        if (now < _retryAfter) return;
        try
        {
            if (_client is null)
            {
                if (!_pendingShutdown.IsCompleted) return;
                _client = _factory(_viewModel.DiscordPresenceOptions.ApplicationId);
                _client.StatusChanged += OnStatusChanged;
                _viewModel.DiscordPresenceStatus = "Waiting for Discord desktop";
                _client.Initialize();
                _dirty = true;
            }

            var playing = _viewModel.CurrentTrack is not null && _viewModel.IsPlaying && !_viewModel.IsPlaybackStopped;
            UpdateArtwork(playing, now);
            // Keep the offline snapshot current before pumping a possible Ready event.
            // Clearing presence bypasses the throttle so pause/stop take effect promptly.
            if (_dirty && (!playing || !_client.IsConnected || _lastPresence is null || now >= _nextPublish))
            {
                var presence = playing ? CreatePresence(now) : null;
                if (presence != _lastPresence)
                {
                    _client.Update(presence);
                    _lastPresence = presence;
                    _nextPublish = now.AddSeconds(1);
                }
                _dirty = false;
                _timelineDirty = false;
            }
            _client.Pump();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Discord presence is unavailable: {ex.Message}");
            ReleaseClient();
            _dirty = true;
            _nextPublish = default;
            _retryAfter = now.AddSeconds(10);
            _viewModel.DiscordPresenceStatus = "Discord unavailable; retrying shortly";
        }
    }

    private DiscordPresence CreatePresence(DateTimeOffset now)
    {
        var track = _viewModel.CurrentTrack!;
        if (!_timelineDirty && _lastPresence is not null)
            return _lastPresence with { ArtworkUrl = _artworkUrl, ArtworkText = track.Album };
        var metadata = string.Join(" — ", new[] { track.Artist, track.Album }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var duration = _viewModel.DurationSeconds;
        var position = _viewModel.PlaybackPosition.TotalSeconds;
        // Missing/invalid duration must not produce an invalid or already expired timer.
        if (!double.IsFinite(duration) || duration <= 0 || !double.IsFinite(position) || position >= duration)
            return new(track.Title, metadata, null, null, _artworkUrl, track.Album);
        var start = now.AddSeconds(-Math.Clamp(position, 0, duration));
        return new(track.Title, metadata, start, start.AddSeconds(duration), _artworkUrl, track.Album);
    }

    private void UpdateArtwork(bool playing, DateTimeOffset now)
    {
        if (!playing || !_viewModel.DiscordPresenceOptions.LookupAlbumCovers || _artworkResolver is null)
        {
            CancelArtwork();
            return;
        }
        var track = _viewModel.CurrentTrack!;
        if (!ReferenceEquals(_artworkTrack, track))
        {
            CancelArtwork();
            _artworkTrack = track;
            _dirty = true;
        }
        if (_artworkRequest is not null || _artworkUrl is not null || now < _artworkRetryAfter) return;
        var request = _artworkRequest = new CancellationTokenSource();
        _ = ResolveArtworkAsync(track, request, request.Token);
    }

    private async Task ResolveArtworkAsync(Track track, CancellationTokenSource request, CancellationToken token)
    {
        string? url = null;
        try
        {
            // Cache and tag-independent lookup work must not run on the WPF thread.
            url = await Task.Run(() => _artworkResolver!.ResolveAsync(track, token), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Trace.TraceWarning($"Could not resolve Discord album art: {ex.Message}"); }
        try
        {
            if (token.IsCancellationRequested || _dispatcher.HasShutdownStarted) return;
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed || _artworkRequest != request || !ReferenceEquals(_viewModel.CurrentTrack, track)) return;
                _artworkRequest = null;
                _artworkUrl = url;
                _artworkRetryAfter = _clock.GetUtcNow().AddMinutes(1);
                ScheduleUpdate(timelineChanged: false);
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException) { }
        finally { request.Dispose(); }
    }

    private void CancelArtwork()
    {
        _artworkRequest?.Cancel();
        _artworkRequest = null;
        _artworkTrack = null;
        _artworkUrl = null;
        _artworkRetryAfter = default;
    }

    private void OnStatusChanged(string status) => _viewModel.DiscordPresenceStatus = status;

    private void ReleaseClient()
    {
        var client = _client;
        _client = null;
        _lastPresence = null;
        if (client is null) return;
        client.StatusChanged -= OnStatusChanged;
        try { client.Update(null); }
        catch (Exception ex) { Trace.TraceWarning($"Could not clear Discord presence: {ex.Message}"); }
        try
        {
            client.Dispose();
            _pendingShutdown = client.Shutdown;
        }
        catch (Exception ex) { Trace.TraceWarning($"Could not close Discord connection: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelArtwork();
        _timer.Stop();
        _timer.Tick -= OnTick;
        _pendingUpdate?.Abort();
        _viewModel.PropertyChanged -= OnPropertyChanged;
        _viewModel.PlaybackSeeked -= OnSeeked;
        ReleaseClient();
    }
}
