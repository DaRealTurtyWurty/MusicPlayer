using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicPlayer.Models;
using MusicPlayer.Services;
using MusicPlayer.ViewModels.Coordination;

namespace MusicPlayer.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable, IPlaybackState, IQueuePlaybackState, ILibraryState
{
    private readonly IFilePickerService _filePickerService;
    private readonly IFolderPickerService _folderPickerService;
    private readonly IMetadataService _metadataService;
    private readonly ILibraryScanner _libraryScanner;
    private readonly IAudioPlayer _audioPlayer;
    private readonly IFileLocationService _fileLocationService;
    private readonly IUiPreferencesStore? _uiPreferencesStore;
    public LyricsViewModel Lyrics { get; }
    private readonly PlaybackCoordinator _playback;
    private readonly QueueCoordinator _queue;
    private readonly LibraryCoordinator _library;

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

    public ObservableCollection<Track> Tracks => _library.Tracks;
    public ObservableCollection<Track> Queue => _queue.Queue;
    public string QueueSummary => $"{Queue.Count} upcoming {(Queue.Count == 1 ? "track" : "tracks")}";
    public bool IsQueueEmpty => Queue.Count == 0;

    public string PositionText =>
        FormatTime(TimeSpan.FromSeconds(PositionSeconds));

    public string DurationText =>
        FormatTime(TimeSpan.FromSeconds(DurationSeconds));

    private readonly DispatcherTimer _positionTimer;
    private bool _updatingPosition;
    public TimeSpan PlaybackPosition => _audioPlayer.Position;
    public event EventHandler? PlaybackSeeked;

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
        ILocalLyricsSource? lyricsSource = null,
        IListeningHistoryStore? listeningHistoryStore = null)
    {
        _filePickerService = filePickerService;
        _folderPickerService = folderPickerService;
        _metadataService = metadataService;
        _libraryScanner = libraryScanner;
        _audioPlayer = audioPlayer;
        _fileLocationService = fileLocationService ?? new FileLocationService();
        _queue = new QueueCoordinator(this, random ?? Random.Shared);
        _playback = new PlaybackCoordinator(this, audioPlayer, metadataService);
        _library = new LibraryCoordinator(this, libraryStore);
        _uiPreferencesStore = uiPreferencesStore;
        InitializeReplayGain();
        InitializeCrossfade();
        isGaplessPlaybackEnabled = _uiPreferencesStore?.LoadGaplessPlaybackEnabled() ?? true;
        InitializeAudioDevices();
        _discordPresenceOptions = _uiPreferencesStore?.LoadDiscordPresence() ?? new();
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
        _playback.Initialize();
        Queue.CollectionChanged += OnTimelineQueueChanged;
        InitializeLibrary();
        InitializePages(playlistStore);
        InitializeLibraryPlaylists();
        InitializePlaybackSession(playbackSessionStore);
        InitializeLibraryMaintenance(libraryRefreshService, trackMatchPicker, monitorLibrary);
        _artistIdentityService = artistIdentityService;
        ArtistPhotoService = artistPhotoService;
        InitializeMusicBrowser(releaseTypeStore);
        InitializeListeningHistory(listeningHistoryStore ?? libraryStore as IListeningHistoryStore ?? playbackSessionStore as IListeningHistoryStore);
    }

    [RelayCommand]
    private void OpenFile()
    {
        var filePath = _filePickerService.PickAudioFile();

        if (filePath is null)
            return;

        try
        {
            var track = _metadataService.ReadTrack(filePath);
            AddLibraryTracks([track], explicitlyAdded: true);
            LoadTrack(track, playImmediately: false);
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError($"Playback failed: {ex}"); PlaybackError = $"Could not open audio file: {ex.Message}"; }
    }

    [RelayCommand]
    private void Play() => _playback.Play();

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

    partial void OnRepeatModeChanged(PlaybackRepeatMode value)
    {
        _uiPreferencesStore?.SavePlaybackModes(IsShuffleEnabled, value);
        PrepareGaplessTrack();
    }

    private void ShuffleQueue() => _queue.ShuffleQueue();

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

    private void StartPlaybackSession(IEnumerable<Track> tracks) => _queue.StartPlaybackSession(tracks);

    private bool HasQueuedTracks() => Queue.Count > 0;

    private bool CanAdvance() => _queue.CanAdvance;

    private bool CanGoPrevious() => _queue.CanGoPrevious;

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void Previous() => _queue.Previous();

    private void RememberTrack(Track? track, bool recycled = false) => _queue.RememberTrack(track, recycled);

    private bool HasQueueSelection() => _queue.HasSelection;
    private bool CanMoveUp() => _queue.CanMoveUp;
    private bool CanMoveDown() => _queue.CanMoveDown;

    [RelayCommand(CanExecute = nameof(CanAdvance))]
    private void Next() => AdvanceQueue(recycleCurrent: true);

    private void AdvanceQueue(bool recycleCurrent) => _queue.AdvanceQueue(recycleCurrent);

    [RelayCommand(CanExecute = nameof(HasQueueSelection))]
    private void PlayQueuedTrack() => _queue.PlayQueuedTrack();

    [RelayCommand(CanExecute = nameof(HasQueueSelection))]
    private void RemoveFromQueue() => _queue.RemoveFromQueue();

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveQueueUp() => _queue.MoveQueueUp();

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveQueueDown() => _queue.MoveQueueDown();

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

    private bool LoadTrack(Track track, bool playImmediately, bool rememberCurrent = true) => _playback.LoadTrack(track, playImmediately, rememberCurrent);

    [RelayCommand]
    private void Pause() => _playback.Pause();

    [RelayCommand]
    private void Stop() => _playback.Stop();

    private static string FormatTime(TimeSpan time)
    {
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");
    }

    private void OnPositionTimerTick(object? sender, EventArgs e)
    {
        _playback.RecordPlaybackStart();
        _updatingPosition = true;

        PositionSeconds = _audioPlayer.Position.TotalSeconds;

        _updatingPosition = false;
        RefreshLyricsPosition();
    }

    partial void OnPositionSecondsChanged(double value)
    {
        if (_updatingPosition)
            return;

        if (!double.IsFinite(value) || !TryPlaybackAction(() => _audioPlayer.Seek(TimeSpan.FromSeconds(value)))) return;
        RefreshLyricsPosition();
        PlaybackSeeked?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshLyricsPosition() => Lyrics.UpdatePosition(_audioPlayer.PresentationPosition.TotalSeconds);

    partial void OnIsPlayingChanged(bool value) => RefreshLyricsPosition();

    private void SeekToLyrics(double seconds) => PositionSeconds = Math.Clamp(seconds, 0, DurationSeconds);

    public void Dispose()
    {
        Tracks.CollectionChanged -= OnListeningTracksChanged;
        Lyrics.SeekRequested -= SeekToLyrics;
        Lyrics.Dispose();
        DisposeMusicBrowser();
        DisposeLibraryMaintenance();
        DisposePlaybackSession();
        _positionTimer.Stop();
        _positionTimer.Tick -= OnPositionTimerTick;
        _playback.Dispose();
        Queue.CollectionChanged -= OnTimelineQueueChanged;
        DisposeAudioDevices();
        DisposeLibrary();
        if (SelectedPlaylist is not null)
            SelectedPlaylist.Tracks.CollectionChanged -= OnPlaylistTracksChanged;
        (_audioPlayer as IDisposable)?.Dispose();
    }
}
