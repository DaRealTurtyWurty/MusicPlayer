using System.Diagnostics;
using System.IO;
using MusicPlayer.Models;
using Windows.Media;
using Windows.Storage.Streams;

namespace MusicPlayer.Services;

/// <summary>Window-scoped SMTC session for NAudio playback in an unpackaged WPF app.</summary>
public sealed class WindowsSystemMediaControls : ISystemMediaControls
{
    private readonly SystemMediaTransportControls _controls;
    private IRandomAccessStream? _artworkStream;
    private SystemMediaState? _lastState;
    private bool _disposed;

    public event Action<SystemMediaTransportControlsButton>? ButtonPressed;
    public event Action<TimeSpan>? PositionRequested;

    public WindowsSystemMediaControls(nint windowHandle)
    {
        _controls = SystemMediaTransportControlsInterop.GetForWindow(windowHandle);
        _controls.IsEnabled = false;
        _controls.ButtonPressed += OnButtonPressed;
        _controls.PlaybackPositionChangeRequested += OnPositionRequested;
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        => ButtonPressed?.Invoke(args.Button);

    private void OnPositionRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args)
        => PositionRequested?.Invoke(args.RequestedPlaybackPosition);

    public void Update(SystemMediaState state)
    {
        if (_disposed || state == _lastState) return;
        if (_lastState is null || !ReferenceEquals(state.Track, _lastState.Track))
            UpdateMetadata(state.Track);

        if (_lastState?.CanPlay != state.CanPlay) _controls.IsPlayEnabled = state.CanPlay;
        if (_lastState?.CanPause != state.CanPause) _controls.IsPauseEnabled = state.CanPause;
        if (_lastState?.CanNext != state.CanNext) _controls.IsNextEnabled = state.CanNext;
        if (_lastState?.CanPrevious != state.CanPrevious) _controls.IsPreviousEnabled = state.CanPrevious;
        if (_lastState is null || (_lastState.Track is null) != (state.Track is null))
            _controls.IsStopEnabled = state.Track is not null;
        if (_lastState?.Status != state.Status) _controls.PlaybackStatus = state.Status;
        _controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            MinSeekTime = TimeSpan.Zero,
            EndTime = state.Duration,
            MaxSeekTime = state.Duration,
            Position = state.Position
        });
        if (_lastState is null || (_lastState.CanPlay || _lastState.CanPrevious) != (state.CanPlay || state.CanPrevious))
            _controls.IsEnabled = state.CanPlay || state.CanPrevious;
        _lastState = state;
    }

    private void UpdateMetadata(Track? track)
    {
        var display = _controls.DisplayUpdater;
        display.ClearAll();
        _artworkStream?.Dispose();
        _artworkStream = null;
        display.Type = MediaPlaybackType.Music;
        if (track is not null)
        {
            display.MusicProperties.Title = track.Title;
            display.MusicProperties.Artist = track.Artist ?? string.Empty;
            display.MusicProperties.AlbumTitle = track.Album ?? string.Empty;
            if (track.ArtworkData is { Length: > 0 } artwork)
            {
                try
                {
                    // Keep the stream alive while the shell reads the thumbnail. No temporary files,
                    // or asynchronous extraction that could overwrite a newer track's metadata.
                    _artworkStream = new MemoryStream(artwork, writable: false).AsRandomAccessStream();
                    display.Thumbnail = RandomAccessStreamReference.CreateFromStream(_artworkStream);
                }
                catch (Exception ex)
                {
                    _artworkStream?.Dispose();
                    _artworkStream = null;
                    Trace.TraceWarning($"Could not publish media artwork: {ex.Message}");
                }
            }
        }
        display.Update();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _controls.ButtonPressed -= OnButtonPressed;
        _controls.PlaybackPositionChangeRequested -= OnPositionRequested;
        try
        {
            _controls.IsEnabled = false;
            _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
            _controls.DisplayUpdater.ClearAll();
            _controls.DisplayUpdater.Update();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Could not clear Windows media controls: {ex.Message}");
        }
        finally
        {
            _artworkStream?.Dispose();
            _artworkStream = null;
        }
    }
}
