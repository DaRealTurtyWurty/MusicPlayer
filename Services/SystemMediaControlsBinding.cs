using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using MusicPlayer.ViewModels;
using Windows.Media;

namespace MusicPlayer.Services;

/// <summary>Connects system controls to the same playback commands used by the UI.</summary>
public sealed class SystemMediaControlsBinding : IDisposable
{
    private readonly MainViewModel _viewModel;
    private readonly ISystemMediaControls _controls;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public SystemMediaControlsBinding(MainViewModel viewModel, ISystemMediaControls controls, Dispatcher dispatcher)
    {
        _viewModel = viewModel;
        _controls = controls;
        _dispatcher = dispatcher;
        _viewModel.PropertyChanged += OnPropertyChanged;
        _viewModel.TogglePlaybackCommand.CanExecuteChanged += OnCommandsChanged;
        _viewModel.NextCommand.CanExecuteChanged += OnCommandsChanged;
        _viewModel.PreviousCommand.CanExecuteChanged += OnCommandsChanged;
        _controls.ButtonPressed += OnButtonPressed;
        _controls.PositionRequested += OnPositionRequested;
        Update();
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CurrentTrack) or nameof(MainViewModel.IsPlaying)
            or nameof(MainViewModel.IsPlaybackStopped) or nameof(MainViewModel.PositionSeconds)
            or nameof(MainViewModel.DurationSeconds))
            Update();
    }

    private void OnCommandsChanged(object? sender, EventArgs e) => Update();

    private void Update()
    {
        if (_disposed) return;
        var duration = TimeSpan.FromSeconds(Math.Max(0, _viewModel.DurationSeconds));
        var position = TimeSpan.FromSeconds(Math.Clamp(_viewModel.PositionSeconds, 0, duration.TotalSeconds));
        var status = _viewModel.CurrentTrack is null ? MediaPlaybackStatus.Closed
            : _viewModel.IsPlaying ? MediaPlaybackStatus.Playing
            : _viewModel.IsPlaybackStopped ? MediaPlaybackStatus.Stopped : MediaPlaybackStatus.Paused;
        try
        {
            _controls.Update(new SystemMediaState(_viewModel.CurrentTrack, status,
                _viewModel.TogglePlaybackCommand.CanExecute(null), _viewModel.CurrentTrack is not null,
                _viewModel.NextCommand.CanExecute(null), _viewModel.PreviousCommand.CanExecute(null),
                position, duration));
        }
        catch (Exception ex)
        {
            // Shell integration must not turn a successful audio load into a playback failure.
            Trace.TraceWarning($"Could not update Windows media controls: {ex.Message}");
        }
    }

    private void OnButtonPressed(SystemMediaTransportControlsButton button) => Dispatch(() =>
    {
        ICommand? command = button switch
        {
            // Explicit Play/Pause requests must be idempotent, never toggles.
            SystemMediaTransportControlsButton.Play when !_viewModel.IsPlaying &&
                _viewModel.TogglePlaybackCommand.CanExecute(null) => _viewModel.PlayCommand,
            SystemMediaTransportControlsButton.Pause when _viewModel.IsPlaying => _viewModel.PauseCommand,
            SystemMediaTransportControlsButton.Stop when _viewModel.CurrentTrack is not null => _viewModel.StopCommand,
            SystemMediaTransportControlsButton.Next => _viewModel.NextCommand,
            SystemMediaTransportControlsButton.Previous => _viewModel.PreviousCommand,
            _ => null
        };
        if (command?.CanExecute(null) == true) command.Execute(null);
    });

    private void OnPositionRequested(TimeSpan position) => Dispatch(() =>
    {
        if (_viewModel.CurrentTrack is not null)
            _viewModel.PositionSeconds = Math.Clamp(position.TotalSeconds, 0, _viewModel.DurationSeconds);
    });

    private void Dispatch(Action action)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        // SMTC callbacks arrive on a worker thread. Queue and recheck lifetime on the WPF thread.
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            try { action(); }
            catch (Exception ex)
            {
                _viewModel.PlaybackError = $"Could not handle media control: {ex.Message}";
                Trace.TraceWarning(_viewModel.PlaybackError);
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _viewModel.PropertyChanged -= OnPropertyChanged;
        _viewModel.TogglePlaybackCommand.CanExecuteChanged -= OnCommandsChanged;
        _viewModel.NextCommand.CanExecuteChanged -= OnCommandsChanged;
        _viewModel.PreviousCommand.CanExecuteChanged -= OnCommandsChanged;
        _controls.ButtonPressed -= OnButtonPressed;
        _controls.PositionRequested -= OnPositionRequested;
        _controls.Dispose();
    }
}
