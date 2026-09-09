using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicPlayer.Models;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IFilePickerService _filePickerService;
    private readonly IFolderPickerService _folderPickerService;
    private readonly IMetadataService _metadataService;
    private readonly ILibraryScanner _libraryScanner;
    private readonly IAudioPlayer _audioPlayer;
    private readonly IFileLocationService _fileLocationService;
    private readonly Random _random;
    private readonly IUiPreferencesStore? _uiPreferencesStore;
    public LyricsViewModel Lyrics { get; }
    private readonly Stack<(Track Track, bool Recycled)> _playbackHistory = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaybackStatus))]
    [NotifyCanExecuteChangedFor(nameof(TogglePlaybackCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand))]
    private Track? currentTrack;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShuffleLabel))]
    private bool isShuffleEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRepeatEnabled))]
    [NotifyPropertyChangedFor(nameof(RepeatLabel))]
    [NotifyPropertyChangedFor(nameof(IsRepeatOne))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private PlaybackRepeatMode repeatMode;

    public bool IsRepeatEnabled => RepeatMode != PlaybackRepeatMode.Off;

    public string ShuffleLabel => IsShuffleEnabled
        ? "Shuffle on — turn off to keep the current queue order"
        : "Shuffle off — turn on to mix upcoming tracks";

    public string RepeatLabel => RepeatMode switch
    {
        PlaybackRepeatMode.All => "Repeat all — click to repeat one",
        PlaybackRepeatMode.One => "Repeat one — click to turn repeat off",
        _ => "Repeat off — click to repeat all"
    };

    public bool IsRepeatOne => RepeatMode == PlaybackRepeatMode.One;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaybackStatus))]
    [NotifyPropertyChangedFor(nameof(PlayPauseGlyph))]
    [NotifyPropertyChangedFor(nameof(PlayPauseLabel))]
    private bool isPlaying;

    [ObservableProperty] private bool isPlaybackStopped = true;

    public string PlaybackStatus =>
        CurrentTrack is null ? "READY TO LISTEN" : IsPlaying ? "NOW PLAYING" : "READY TO PLAY";

    public string PlayPauseGlyph => IsPlaying ? "\uE769" : "\uE768";
    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";

    private double _volume = 100;
    private double _volumeBeforeMute = 100;

    public double Volume
    {
        get => _volume;
        set
        {
            if (!double.IsFinite(value)) return;
            var volume = Math.Clamp(value, 0, 100);
            if (!SetProperty(ref _volume, volume)) return;
            if (volume > 0) _volumeBeforeMute = volume;
            _audioPlayer.Volume = (float)(volume / 100);
            OnPropertyChanged(nameof(IsMuted));
            OnPropertyChanged(nameof(MuteLabel));
            _uiPreferencesStore?.SaveVolume(_volume, _volumeBeforeMute);
        }
    }

    public bool IsMuted => Volume == 0;
    public string MuteLabel => IsMuted ? "Unmute" : "Mute";

    [RelayCommand]
    private void ToggleMute() => Volume = IsMuted ? _volumeBeforeMute : 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlaySelectedTrackCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddToQueueCommand))]
    [NotifyCanExecuteChangedFor(nameof(PlayNextCommand))]
    private Track? selectedTrack;

    [ObservableProperty] private int selectedQueueIndex = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueueToggleLabel))]
    private bool isQueueOpen;

    public string QueueToggleLabel => $"{(IsQueueOpen ? "Hide" : "Show")} queue — {QueueSummary}";

    partial void OnIsQueueOpenChanged(bool value) => _uiPreferencesStore?.SaveQueueOpen(value);

    [ObservableProperty] private string? playbackError;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PositionText))]
    private double positionSeconds;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DurationText))]
    private double durationSeconds;

    public ObservableCollection<Track> Tracks { get; } = new LibraryTrackCollection();
    public ObservableCollection<Track> Queue { get; } = [];
    public string QueueSummary => $"{Queue.Count} upcoming {(Queue.Count == 1 ? "track" : "tracks")}";
    public bool IsQueueEmpty => Queue.Count == 0;

    public string PositionText =>
        FormatTime(TimeSpan.FromSeconds(PositionSeconds));

    public string DurationText =>
        FormatTime(TimeSpan.FromSeconds(DurationSeconds));

    private readonly DispatcherTimer _positionTimer;
    private bool _updatingPosition;

    public MainViewModel(
        IFilePickerService filePickerService,
        IFolderPickerService folderPickerService,
        IMetadataService metadataService,
        ILibraryScanner libraryScanner,
        IAudioPlayer audioPlayer,
        Random? random = null,
        IPlaylistStore? playlistStore = null,
        IPlaylistImportService? playlistImportService = null,
        IUiPreferencesStore? uiPreferencesStore = null,
        ILibraryStore? libraryStore = null,
        IPlaybackSessionStore? playbackSessionStore = null,
        ILibraryRefreshService? libraryRefreshService = null,
        ITrackMatchPicker? trackMatchPicker = null,
        bool monitorLibrary = true,
        IReleaseTypeStore? releaseTypeStore = null,
        IFileLocationService? fileLocationService = null,
        IArtistIdentityService? artistIdentityService = null,
        IArtistPhotoService? artistPhotoService = null,
        ILocalLyricsSource? lyricsSource = null)
    {
        _filePickerService = filePickerService;
        _folderPickerService = folderPickerService;
        _metadataService = metadataService;
        _libraryScanner = libraryScanner;
        _audioPlayer = audioPlayer;
        _fileLocationService = fileLocationService ?? new FileLocationService();
        _random = random ?? Random.Shared;
        _uiPreferencesStore = uiPreferencesStore;
        Lyrics = new LyricsViewModel(lyricsSource ?? new LocalLyricsSource(), uiPreferencesStore);
        Lyrics.SeekRequested += SeekToLyrics;
        (_volume, _volumeBeforeMute) = _uiPreferencesStore?.LoadVolume() ?? (100d, 100d);
        _audioPlayer.Volume = (float)(_volume / 100);
        isQueueOpen = _uiPreferencesStore?.LoadQueueOpen() ?? false;
        selectedPage = _uiPreferencesStore?.LoadSelectedPage() ?? AppPage.Library;
        // Restore the flags directly: enabling shuffle via its setter would reshuffle a saved session.
        (isShuffleEnabled, repeatMode) = _uiPreferencesStore?.LoadPlaybackModes() ?? (false, PlaybackRepeatMode.Off);
        _playlistImportService = playlistImportService ?? new PlaylistImportService(metadataService);

        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };

        _positionTimer.Tick += OnPositionTimerTick;
        _positionTimer.Start();
        _audioPlayer.PlaybackEnded += OnPlaybackEnded;
        Queue.CollectionChanged += OnTimelineQueueChanged;
        InitializeLibrary(libraryStore);
        InitializePages(playlistStore);
        InitializeLibraryPlaylists();
        InitializePlaybackSession(playbackSessionStore);
        InitializeLibraryMaintenance(libraryRefreshService, trackMatchPicker, monitorLibrary);
        _artistIdentityService = artistIdentityService;
        ArtistPhotoService = artistPhotoService;
        InitializeMusicBrowser(releaseTypeStore);
    }

    [RelayCommand]
    private void OpenFile()
    {
        var filePath = _filePickerService.PickAudioFile();

        if (filePath is null)
            return;

        var track = _metadataService.ReadTrack(filePath);
        AddLibraryTracks([track], explicitlyAdded: true);
        LoadTrack(track, playImmediately: false);
    }

    [RelayCommand]
    private void Play()
    {
        if (_reloadCurrentTrack && CurrentTrack is { } relocated)
        {
            var position = PositionSeconds;
            if (LoadTrack(relocated, playImmediately: true, rememberCurrent: false))
                PositionSeconds = Math.Clamp(position, 0, DurationSeconds);
            return;
        }
        if (CurrentTrack is null)
        {
            Next();
            return;
        }

        if (_audioPlayer.Position >= _audioPlayer.Duration)
            _audioPlayer.Seek(TimeSpan.Zero);
        _audioPlayer.Play();
        IsPlaybackStopped = false;
        IsPlaying = true;
    }

    private bool CanTogglePlayback() => CurrentTrack is not null || Queue.Count > 0;

    [RelayCommand(CanExecute = nameof(CanTogglePlayback))]
    private void TogglePlayback()
    {
        if (IsPlaying)
            Pause();
        else
            Play();
    }

    [RelayCommand(CanExecute = nameof(CanPlaySelectedTrack))]
    private void PlaySelectedTrack()
    {
        if (SelectedTrack is null)
            return;

        LoadTrack(SelectedTrack, playImmediately: true);
    }

    private bool CanPlaySelectedTrack()
    {
        return SelectedTrack is not null;
    }

    [RelayCommand(CanExecute = nameof(CanPlaySelectedTrack))]
    private void AddToQueue() => Queue.Add(SelectedTrack!);

    [RelayCommand(CanExecute = nameof(CanPlaySelectedTrack))]
    private void PlayNext() => Queue.Insert(0, SelectedTrack!);

    [RelayCommand]
    private void ToggleShuffle() => IsShuffleEnabled = !IsShuffleEnabled;

    partial void OnIsShuffleEnabledChanged(bool value)
    {
        if (value)
            ShuffleQueue();
        _uiPreferencesStore?.SavePlaybackModes(value, RepeatMode);
    }

    partial void OnRepeatModeChanged(PlaybackRepeatMode value) =>
        _uiPreferencesStore?.SavePlaybackModes(IsShuffleEnabled, value);

    private void ShuffleQueue()
    {
        // Track indices, rather than track identity, preserve selection for duplicate songs.
        var selectedIndex = SelectedQueueIndex;
        for (var i = Queue.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            if (i == j) continue;
            (Queue[i], Queue[j]) = (Queue[j], Queue[i]);
            if (selectedIndex == i) selectedIndex = j;
            else if (selectedIndex == j) selectedIndex = i;
        }

        SelectedQueueIndex = selectedIndex;
    }

    [RelayCommand]
    private void CycleRepeat() => RepeatMode = RepeatMode switch
    {
        PlaybackRepeatMode.Off => PlaybackRepeatMode.All,
        PlaybackRepeatMode.All => PlaybackRepeatMode.One,
        _ => PlaybackRepeatMode.Off
    };

    private bool HasLibraryTracks() => !LibraryTracks.IsEmpty;

    [RelayCommand(CanExecute = nameof(HasLibraryTracks))]
    private void PlayAll() => StartPlaybackSession(LibraryTracks.Cast<Track>());

    private void StartPlaybackSession(IEnumerable<Track> tracks)
    {
        var sessionTracks = tracks.ToArray();
        _playbackHistory.Clear();
        RefreshTimelineHistory();
        PreviousCommand.NotifyCanExecuteChanged();
        Queue.Clear();
        foreach (var track in sessionTracks)
            Queue.Add(track);
        if (IsShuffleEnabled)
            ShuffleQueue();
        // A new library session must not recycle the previously playing track.
        AdvanceQueue(recycleCurrent: false);
    }

    private bool HasQueuedTracks() => Queue.Count > 0;

    private bool CanAdvance() => HasQueuedTracks() ||
                                 (RepeatMode == PlaybackRepeatMode.All && CurrentTrack is not null);

    private bool CanGoPrevious() => CurrentTrack is not null || _playbackHistory.Count > 0;

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void Previous()
    {
        if (CurrentTrack is not null && (_audioPlayer.Position.TotalSeconds > 3 || _playbackHistory.Count == 0))
        {
            _audioPlayer.Seek(TimeSpan.Zero);
            PositionSeconds = 0;
            Play();
            return;
        }

        var interruptedTrack = CurrentTrack;
        while (_playbackHistory.TryPop(out var previous))
        {
            RefreshTimelineHistory();
            // Repeat-all places the played entry at the tail. Pull it back out when
            // navigating backwards, so going forward does not grow the repeat cycle.
            if (previous.Recycled && Queue.Count > 0 &&
                string.Equals(Queue[^1].FilePath, previous.Track.FilePath, StringComparison.OrdinalIgnoreCase))
                Queue.RemoveAt(Queue.Count - 1);
            if (!LoadTrack(previous.Track, playImmediately: true, rememberCurrent: false))
                continue;
            if (interruptedTrack is not null)
                Queue.Insert(0, interruptedTrack);
            PreviousCommand.NotifyCanExecuteChanged();
            return;
        }

        if (interruptedTrack is not null)
            LoadTrack(interruptedTrack, playImmediately: true, rememberCurrent: false);
        PreviousCommand.NotifyCanExecuteChanged();
    }

    private void RememberTrack(Track? track, bool recycled = false)
    {
        if (track is null) return;
        _playbackHistory.Push((track, recycled));
        RefreshTimelineHistory();
        PreviousCommand.NotifyCanExecuteChanged();
    }

    private bool HasQueueSelection() => SelectedQueueIndex >= 0 && SelectedQueueIndex < Queue.Count;
    private bool CanMoveUp() => HasQueueSelection() && SelectedQueueIndex > 0;
    private bool CanMoveDown() => HasQueueSelection() && SelectedQueueIndex < Queue.Count - 1;

    [RelayCommand(CanExecute = nameof(CanAdvance))]
    private void Next() => AdvanceQueue(recycleCurrent: true);

    private void AdvanceQueue(bool recycleCurrent)
    {
        var previous = CurrentTrack;
        var recycled = recycleCurrent && RepeatMode == PlaybackRepeatMode.All && previous is not null;
        if (recycled)
            Queue.Add(previous!);

        // Unreadable tracks should not prevent the rest of the queue from playing.
        // Recycle only once per advance so a queue of failed files cannot loop forever.
        while (Queue.Count > 0)
        {
            var track = Queue[0];
            Queue.RemoveAt(0);
            if (LoadTrack(track, playImmediately: true, rememberCurrent: false))
            {
                if (recycleCurrent)
                    RememberTrack(previous, recycled);
                return;
            }
        }
        if (recycleCurrent && CurrentTrack is null)
            RememberTrack(previous, recycled);
    }

    [RelayCommand(CanExecute = nameof(HasQueueSelection))]
    private void PlayQueuedTrack()
    {
        if (!HasQueueSelection()) return;
        var track = Queue[SelectedQueueIndex];
        Queue.RemoveAt(SelectedQueueIndex);
        var previous = CurrentTrack;
        var recycled = RepeatMode == PlaybackRepeatMode.All && previous is not null;
        if (recycled)
            Queue.Add(previous!);
        var loaded = LoadTrack(track, playImmediately: true, rememberCurrent: false);
        RememberTrack(previous, recycled);
        if (!loaded)
            AdvanceQueue(recycleCurrent: false);
    }

    [RelayCommand(CanExecute = nameof(HasQueueSelection))]
    private void RemoveFromQueue()
    {
        if (!HasQueueSelection()) return;
        var index = SelectedQueueIndex;
        Queue.RemoveAt(index);
        SelectedQueueIndex = Math.Min(index, Queue.Count - 1);
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveQueueUp()
    {
        if (!CanMoveUp()) return;
        var index = SelectedQueueIndex;
        Queue.Move(index, index - 1);
        SelectedQueueIndex = index - 1;
    }

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveQueueDown()
    {
        if (!CanMoveDown()) return;
        var index = SelectedQueueIndex;
        Queue.Move(index, index + 1);
        SelectedQueueIndex = index + 1;
    }

    [RelayCommand(CanExecute = nameof(HasQueuedTracks))]
    private void ClearQueue()
    {
        if (HasQueuedTracks()) IsClearQueueConfirmationOpen = true;
    }

    partial void OnSelectedQueueIndexChanged(int value)
    {
        SelectUpcomingEntry();
        UpdateQueueCommands();
    }

    private void UpdateQueueCommands()
    {
        OnPropertyChanged(nameof(QueueSummary));
        OnPropertyChanged(nameof(QueueToggleLabel));
        OnPropertyChanged(nameof(IsQueueEmpty));
        NextCommand.NotifyCanExecuteChanged();
        TogglePlaybackCommand.NotifyCanExecuteChanged();
        ClearQueueCommand.NotifyCanExecuteChanged();
        ConfirmClearQueueCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ClearQueueConfirmationMessage));
        if (!HasQueuedTracks()) IsClearQueueConfirmationOpen = false;
        PlayQueuedTrackCommand.NotifyCanExecuteChanged();
        RemoveFromQueueCommand.NotifyCanExecuteChanged();
        MoveQueueUpCommand.NotifyCanExecuteChanged();
        MoveQueueDownCommand.NotifyCanExecuteChanged();
    }

    private void OnPlaybackEnded(object? sender, EventArgs e)
    {
        IsPlaybackStopped = true;
        IsPlaying = false;
        if (RepeatMode == PlaybackRepeatMode.One && CurrentTrack is not null)
        {
            if (LoadTrack(CurrentTrack, playImmediately: true, rememberCurrent: false))
                return;
        }

        Next();
    }

    private bool LoadTrack(Track track, bool playImmediately, bool rememberCurrent = true)
    {
        var previous = CurrentTrack;
        try
        {
            // Saved tracks keep metadata; artwork is read from the music file on demand.
            if (track.ArtworkData is null && System.IO.File.Exists(track.FilePath))
            {
                try
                {
                    track = _metadataService.ReadTrack(track.FilePath);
                }
                catch
                {
                    /* Playback can still succeed when tags cannot be read. */
                }
            }

            _audioPlayer.Load(track.FilePath);
            _reloadCurrentTrack = false;
            CurrentTrack = track;
            PlaybackError = null;

            DurationSeconds = _audioPlayer.Duration.TotalSeconds;
            PositionSeconds = 0;

            if (playImmediately)
                _audioPlayer.Play();
            IsPlaybackStopped = false;
            IsPlaying = playImmediately;
            if (rememberCurrent)
                RememberTrack(previous);
            return true;
        }
        catch (Exception ex)
        {
            IsPlaying = false;
            CurrentTrack = null;
            DurationSeconds = 0;
            PositionSeconds = 0;
            PlaybackError = $"Could not play {track.Title}: {ex.Message}";
            if (rememberCurrent) RememberTrack(previous);
            return false;
        }
    }

    [RelayCommand]
    private void Pause()
    {
        _audioPlayer.Pause();
        IsPlaybackStopped = false;
        IsPlaying = false;
    }

    [RelayCommand]
    private void Stop()
    {
        _audioPlayer.Stop();
        IsPlaybackStopped = true;
        IsPlaying = false;
        PositionSeconds = 0;
    }

    private static string FormatTime(TimeSpan time)
    {
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");
    }

    private void OnPositionTimerTick(object? sender, EventArgs e)
    {
        _updatingPosition = true;

        PositionSeconds = _audioPlayer.Position.TotalSeconds;

        _updatingPosition = false;
        RefreshLyricsPosition();
    }

    partial void OnPositionSecondsChanged(double value)
    {
        if (_updatingPosition)
            return;

        _audioPlayer.Seek(TimeSpan.FromSeconds(value));
        RefreshLyricsPosition();
    }

    public void RefreshLyricsPosition() => Lyrics.UpdatePosition(_audioPlayer.PresentationPosition.TotalSeconds);

    partial void OnIsPlayingChanged(bool value) => RefreshLyricsPosition();

    private void SeekToLyrics(double seconds) => PositionSeconds = Math.Clamp(seconds, 0, DurationSeconds);

    public void Dispose()
    {
        Lyrics.SeekRequested -= SeekToLyrics;
        Lyrics.Dispose();
        DisposeMusicBrowser();
        DisposeLibraryMaintenance();
        DisposePlaybackSession();
        _positionTimer.Stop();
        _audioPlayer.PlaybackEnded -= OnPlaybackEnded;
        DisposeLibrary();
        if (SelectedPlaylist is not null)
            SelectedPlaylist.Tracks.CollectionChanged -= OnPlaylistTracksChanged;
        (_audioPlayer as IDisposable)?.Dispose();
    }
}
