using System.Collections.Specialized;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicPlayer.Models;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private IReadOnlyList<MusicGroup> albums = [];
    [ObservableProperty] private IReadOnlyList<MusicGroup> artists = [];
    [ObservableProperty] private MusicGroup? selectedAlbum;
    [ObservableProperty] private MusicGroup? selectedArtist;
    [ObservableProperty] private bool showArtistTracks;
    [ObservableProperty] private string musicSearchText = "";
    [ObservableProperty] private ReleaseTypeChoice selectedReleaseFilter = new(null, "All releases");
    [ObservableProperty] private ReleaseTypeChoice selectedReleaseTypeChoice = new(null, "Automatic");
    [ObservableProperty] private string? releaseTypeError;
    [ObservableProperty] private bool canEditReleaseType = true;
    private IReleaseTypeStore? _releaseTypeStore;
    private readonly Dictionary<string, ReleaseType> _releaseTypeOverrides = new(StringComparer.Ordinal);
    private bool _updatingReleaseTypeChoice;
    public IReadOnlyList<ReleaseTypeChoice> ReleaseTypeChoices { get; } =
    [new(null, "Automatic"), new(ReleaseType.Album, "Album"), new(ReleaseType.Single, "Single"),
        new(ReleaseType.EP, "EP"), new(ReleaseType.Other, "Other"), new(ReleaseType.Unknown, "Unknown type")];
    public IReadOnlyList<ReleaseTypeChoice> ReleaseFilters { get; } =
    [new(null, "All releases"), new(ReleaseType.Album, "Albums"), new(ReleaseType.Single, "Singles"),
        new(ReleaseType.EP, "EPs"), new(ReleaseType.Other, "Other"), new(ReleaseType.Unknown, "Unknown type")];
    public bool ShowReleaseFilters => SelectedPage != AppPage.Artists || SelectedArtist is not null;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayBrowseTrackCommand))]
    [NotifyCanExecuteChangedFor(nameof(QueueBrowseTrackCommand))]
    [NotifyCanExecuteChangedFor(nameof(PlayBrowseTrackNextCommand))]
    private Track? selectedBrowseTrack;

    private DispatcherOperation? _musicBrowserRefresh;
    private bool _musicBrowserDisposed;
    private bool _resettingMusicBrowser;
    private IReadOnlyList<MusicGroup>? _visibleMusicSource;
    private IReadOnlyList<MusicGroup> _visibleMusicGroups = [];
    private string? _visibleMusicQuery;
    private ReleaseType? _visibleReleaseType;

    public MusicGroup? BrowseSelection => SelectedAlbum ?? SelectedArtist;
    public string MusicBrowserTitle => SelectedPage == AppPage.Artists ? "Artists" : "Albums";
    public string MusicBrowserHeading => $"{MusicBrowserTitle} ({(SelectedPage == AppPage.Artists ? Artists.Count : Albums.Count)})";
    public string MusicSearchPlaceholder => SelectedPage == AppPage.Artists && SelectedArtist is null
        ? "Search artists" : "Search albums";
    public bool HasBrowseSelection => BrowseSelection is not null;
    public bool IsArtistDetail => SelectedArtist is not null && SelectedAlbum is null;
    public bool ShowBrowseTracks => SelectedAlbum is not null || (SelectedArtist is not null && ShowArtistTracks);
    public string ArtistTracksToggleLabel => ShowArtistTracks ? "Show releases" : "Show all tracks";
    public string MusicBackLabel => SelectedAlbum is not null && SelectedArtist is not null
        ? $"Back to {SelectedArtist.Name}" : $"Back to {MusicBrowserTitle.ToLowerInvariant()}";

    public IReadOnlyList<MusicGroup> VisibleMusicGroups
    {
        get
        {
            var source = SelectedArtist?.Albums ?? (SelectedPage == AppPage.Artists ? Artists : Albums);
            var query = MusicSearchText.Trim();
            var type = ShowReleaseFilters ? SelectedReleaseFilter?.Type : null;
            if (ReferenceEquals(source, _visibleMusicSource) && query == _visibleMusicQuery && type == _visibleReleaseType)
                return _visibleMusicGroups;
            _visibleMusicSource = source;
            _visibleMusicQuery = query;
            _visibleReleaseType = type;
            // Stable identity prevents unrelated detail/command notifications from recreating every card.
            return _visibleMusicGroups = query.Length == 0 && type is null ? source : source.Where(g =>
                (type is null || g.ReleaseType == type) &&
                (query.Length == 0 || ContainsMusicQuery(g.Name, query) || ContainsMusicQuery(g.Subtitle, query))).ToArray();
        }
    }

    public IReadOnlyList<Track> BrowseTracks => BrowseSelection?.Tracks ?? [];
    public string MusicEmptyMessage => Tracks.Count == 0 ? "Add music to explore your albums and artists."
        : "No matches. Try a different search.";

    private static bool ContainsMusicQuery(string value, string query) => value.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private void InitializeMusicBrowser(IReleaseTypeStore? releaseTypeStore)
    {
        _releaseTypeStore = releaseTypeStore;
        try
        {
            foreach (var entry in releaseTypeStore?.LoadReleaseTypes() ?? new Dictionary<string, ReleaseType>())
                _releaseTypeOverrides[entry.Key] = entry.Value;
        }
        catch (Exception ex)
        {
            CanEditReleaseType = false;
            ReleaseTypeError = $"Could not load release types: {ex.Message}";
        }
        Tracks.CollectionChanged += OnMusicLibraryChanged;
        RefreshMusicGroups();
    }

    private void OnMusicLibraryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Coalesce import/refresh batches instead of regrouping the library for each incoming song.
        if (_musicBrowserDisposed || _musicBrowserRefresh?.Status == DispatcherOperationStatus.Pending) return;
        _musicBrowserRefresh = _positionTimer.Dispatcher.BeginInvoke(DispatcherPriority.DataBind, RefreshMusicGroups);
    }

    private void RefreshMusicGroups()
    {
        if (_musicBrowserDisposed) return;
        var albumKey = SelectedAlbum?.Key;
        var artistKey = SelectedArtist?.Key;
        var selectedPath = SelectedBrowseTrack?.FilePath;
        // Album titles are scoped to the tagged artist so unrelated releases with identical titles stay separate.
        Albums = Tracks.GroupBy(t => (Artist: MusicGroup.Normalize(MusicGroup.ArtistName(t)), Album: MusicGroup.Normalize(MusicGroup.AlbumName(t))))
            .Select(group => new MusicGroup(MusicGroupKind.Album, MusicGroup.AlbumName(group.First()),
                MusicGroup.ArtistName(group.First()), group.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(t => t.FilePath, StringComparer.OrdinalIgnoreCase).ToArray(), []))
            .Select(ClassifyRelease)
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Artist, StringComparer.CurrentCultureIgnoreCase).ToArray();
        Artists = Albums.GroupBy(g => MusicGroup.Normalize(g.Artist)).Select(group =>
        {
            var artistAlbums = group.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            return new MusicGroup(MusicGroupKind.Artist, artistAlbums[0].Artist, artistAlbums[0].Artist,
                artistAlbums.SelectMany(g => g.Tracks).ToArray(), artistAlbums);
        }).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        SelectedArtist = Artists.FirstOrDefault(g => g.Key == artistKey);
        SelectedAlbum = Albums.FirstOrDefault(g => g.Key == albumKey);
        SelectedBrowseTrack = BrowseTracks.FirstOrDefault(t => string.Equals(t.FilePath, selectedPath, StringComparison.OrdinalIgnoreCase));
        NotifyMusicBrowser();
    }

    private MusicGroup ClassifyRelease(MusicGroup group)
    {
        var classification = _releaseTypeOverrides.TryGetValue(group.Key, out var type)
            ? new ReleaseClassification(type, "Release type set manually. Choose Automatic to use release tags and the single-track title rule again.")
            : ReleaseClassification.FromTracks(group.Tracks);
        return group with { ReleaseType = classification.Type, ReleaseTypeHint = classification.Explanation };
    }

    partial void OnSelectedReleaseFilterChanged(ReleaseTypeChoice value) => NotifyMusicBrowser();

    partial void OnSelectedReleaseTypeChoiceChanged(ReleaseTypeChoice value)
    {
        if (_updatingReleaseTypeChoice || SelectedAlbum is null || value is null || !CanEditReleaseType) return;
        try
        {
            _releaseTypeStore?.SaveReleaseType(SelectedAlbum.Key, value.Type);
            if (value.Type is { } type) _releaseTypeOverrides[SelectedAlbum.Key] = type;
            else _releaseTypeOverrides.Remove(SelectedAlbum.Key);
            ReleaseTypeError = null;
            RefreshMusicGroups();
        }
        catch (Exception ex)
        {
            ReleaseTypeError = $"Could not save release type: {ex.Message}";
            UpdateReleaseTypeChoice();
        }
    }

    private void UpdateReleaseTypeChoice()
    {
        _updatingReleaseTypeChoice = true;
        try
        {
            var type = SelectedAlbum is not null && _releaseTypeOverrides.TryGetValue(SelectedAlbum.Key, out var stored)
                ? stored : (ReleaseType?)null;
            SelectedReleaseTypeChoice = ReleaseTypeChoices.First(c => c.Type == type);
        }
        finally { _updatingReleaseTypeChoice = false; }
    }

    [RelayCommand]
    private void OpenMusicGroup(MusicGroup? group)
    {
        if (group is null) return;
        MusicSearchText = "";
        SelectedBrowseTrack = null;
        if (group.Kind == MusicGroupKind.Artist)
        {
            SelectedAlbum = null;
            SelectedArtist = group;
            ShowArtistTracks = false;
        }
        else SelectedAlbum = group;
    }

    [RelayCommand(CanExecute = nameof(HasBrowseSelection))]
    private void CloseMusicGroup()
    {
        if (SelectedAlbum is not null) SelectedAlbum = null;
        else SelectedArtist = null;
        SelectedBrowseTrack = null;
        MusicSearchText = "";
    }

    [RelayCommand(CanExecute = nameof(HasBrowseSelection))]
    private void PlayMusicGroup() => StartPlaybackSession(BrowseTracks);

    [RelayCommand(CanExecute = nameof(HasBrowseSelection))]
    private void QueueMusicGroup()
    {
        foreach (var track in BrowseTracks) Queue.Add(track);
    }

    [RelayCommand]
    private void ToggleArtistTracks() => ShowArtistTracks = !ShowArtistTracks;

    private bool HasBrowseTrack() => SelectedBrowseTrack is not null && BrowseTracks.Contains(SelectedBrowseTrack);

    [RelayCommand(CanExecute = nameof(HasBrowseTrack))]
    private void PlayBrowseTrack() => LoadTrack(SelectedBrowseTrack!, playImmediately: true);

    [RelayCommand(CanExecute = nameof(HasBrowseTrack))]
    private void QueueBrowseTrack() => Queue.Add(SelectedBrowseTrack!);

    [RelayCommand(CanExecute = nameof(HasBrowseTrack))]
    private void PlayBrowseTrackNext() => Queue.Insert(0, SelectedBrowseTrack!);

    [RelayCommand]
    private void ClearMusicSearch() => MusicSearchText = "";

    partial void OnMusicSearchTextChanged(string value) => NotifyMusicBrowser();
    partial void OnSelectedAlbumChanged(MusicGroup? value)
    {
        UpdateReleaseTypeChoice();
        NotifyMusicBrowser();
    }
    partial void OnSelectedArtistChanged(MusicGroup? value) => NotifyMusicBrowser();
    partial void OnShowArtistTracksChanged(bool value) => NotifyMusicBrowser();

    private void ResetMusicBrowser()
    {
        _resettingMusicBrowser = true;
        try
        {
            SelectedAlbum = null;
            SelectedArtist = null;
            SelectedBrowseTrack = null;
            MusicSearchText = "";
            ShowArtistTracks = false;
            SelectedReleaseFilter = ReleaseFilters[0];
        }
        finally { _resettingMusicBrowser = false; }
        NotifyMusicBrowser();
    }

    private void NotifyMusicBrowser()
    {
        if (_resettingMusicBrowser) return;
        foreach (var name in new[] { nameof(BrowseSelection), nameof(MusicBrowserTitle), nameof(MusicBrowserHeading),
            nameof(MusicSearchPlaceholder), nameof(HasBrowseSelection),
            nameof(IsArtistDetail), nameof(ShowBrowseTracks), nameof(ArtistTracksToggleLabel), nameof(MusicBackLabel),
            nameof(VisibleMusicGroups), nameof(BrowseTracks), nameof(MusicEmptyMessage), nameof(ShowReleaseFilters) })
            OnPropertyChanged(name);
        CloseMusicGroupCommand.NotifyCanExecuteChanged();
        PlayMusicGroupCommand.NotifyCanExecuteChanged();
        QueueMusicGroupCommand.NotifyCanExecuteChanged();
        PlayBrowseTrackCommand.NotifyCanExecuteChanged();
        QueueBrowseTrackCommand.NotifyCanExecuteChanged();
        PlayBrowseTrackNextCommand.NotifyCanExecuteChanged();
    }

    private void DisposeMusicBrowser()
    {
        _musicBrowserDisposed = true;
        _musicBrowserRefresh?.Abort();
        Tracks.CollectionChanged -= OnMusicLibraryChanged;
    }
}
