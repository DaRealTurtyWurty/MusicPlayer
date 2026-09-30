using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using MusicPlayer.Models;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels.Coordination;

internal interface ILibraryState
{
    ObservableCollection<Playlist> Playlists { get; }
    Track? SelectedTrack { get; set; }
    string? LibraryError { get; set; }
    bool ApplyingTrackUpdates { get; }
    bool IsRelinkingTrack { get; }
    bool IsImportingPlaylist { get; }
    IEnumerable<Track> KnownPlaybackTracks();
    void MembershipChanged(bool refreshView);
    void ScheduleLibraryRefresh();
}

internal sealed class LibraryCoordinator : IDisposable
{
    private readonly ILibraryStore? _libraryStore;
    private bool _canSaveLibrary = true;
    private bool _librarySavePending;
    private readonly HashSet<Playlist> _libraryPlaylists = [];
    private HashSet<string> _playlistTrackPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Track> _trackCatalog = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILibraryState _state;
    public LibraryTrackCollection Tracks { get; } = new();
    public ILibraryStore? Store => _libraryStore;
    public bool CanSave => _canSaveLibrary;
    public bool SavePending { get => _librarySavePending; set => _librarySavePending = value; }
    public IEnumerable<Track> KnownTracks => _trackCatalog.Values;

    public LibraryCoordinator(ILibraryState state, ILibraryStore? store)
    {
        _state = state;
        _libraryStore = store;
    }

    public static string LibraryTrackKey(string path) => Path.GetFullPath(path);
    public bool IsPlaylistTrack(Track track) => _playlistTrackPaths.Contains(LibraryTrackKey(track.FilePath));
    public bool IsExplicitTrack(Track track) => _trackCatalog.TryGetValue(LibraryTrackKey(track.FilePath), out var known) && known.ExplicitlyAddedToLibrary;

    public void UpdatePlaylistPaths() => _playlistTrackPaths = _state.Playlists.SelectMany(p => p.Tracks)
        .Select(t => LibraryTrackKey(t.FilePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public void UpdateCatalog(IReadOnlyDictionary<string, Track> updates)
    {
        foreach (var (oldPath, replacement) in updates)
        {
            if (_trackCatalog.TryGetValue(oldPath, out var original))
                replacement.ExplicitlyAddedToLibrary |= original.ExplicitlyAddedToLibrary;
            var newPath = LibraryTrackKey(replacement.FilePath);
            if (_trackCatalog.TryGetValue(newPath, out var destination))
                replacement.ExplicitlyAddedToLibrary |= destination.ExplicitlyAddedToLibrary;
            if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) _trackCatalog.Remove(oldPath);
            _trackCatalog[newPath] = replacement;
        }
    }

    public void Load()
    {
        Tracks.CollectionChanged += OnLibraryCollectionChanged;
        try
        {
            var visible = _libraryStore?.Load() ?? [];
            RegisterKnownTracks(_libraryStore is ILibraryMembershipStore membership ? membership.LoadKnownTracks() : visible);
            AddTracks(visible, save: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"Library load failed: {ex}");
            _canSaveLibrary = false;
            _state.LibraryError = $"Could not load saved library: {ex.Message}";
        }
    }

    public void InitializePlaylists()
    {
        _state.Playlists.CollectionChanged += OnLibraryPlaylistsChanged;
        SynchronizeLibraryPlaylists();
    }

    private void SynchronizeLibraryPlaylists()
    {
        foreach (var removed in _libraryPlaylists.Where(p => !_state.Playlists.Contains(p)).ToArray())
        {
            removed.Tracks.CollectionChanged -= OnLibraryPlaylistTracksChanged;
            _libraryPlaylists.Remove(removed);
        }
        foreach (var playlist in _state.Playlists)
            if (_libraryPlaylists.Add(playlist))
                playlist.Tracks.CollectionChanged += OnLibraryPlaylistTracksChanged;
        RefreshPlaylistMembership();
    }

    public void RefreshPlaylistMembership()
    {
        if (_state.ApplyingTrackUpdates) return;
        var tracks = _state.Playlists.SelectMany(p => p.Tracks).ToArray();
        _playlistTrackPaths = tracks.Select(t => LibraryTrackKey(t.FilePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        AddTracks(tracks);
        var retained = Tracks.Where(t => t.ExplicitlyAddedToLibrary || _playlistTrackPaths.Contains(LibraryTrackKey(t.FilePath))).ToArray();
        if (retained.Length != Tracks.Count)
        {
            var selected = _state.SelectedTrack;
            Tracks.ReplaceAll(retained);
            _state.SelectedTrack = selected is not null && retained.Contains(selected) ? selected : null;
        }
        _state.MembershipChanged(refreshView: true);
    }

    public int AddTracks(IEnumerable<Track> tracks, bool save = true, bool explicitlyAdded = false)
    {
        var incoming = tracks.ToArray();
        if (explicitlyAdded) MarkTracksExplicit(incoming);
        RegisterKnownTracks(incoming);
        var known = Tracks.Select(t => LibraryTrackKey(t.FilePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = incoming.Where(t => known.Add(LibraryTrackKey(t.FilePath)))
            .Select(t => _trackCatalog[LibraryTrackKey(t.FilePath)]).ToArray();
        Tracks.AddRange(added);
        if (save && added.Length > 0) _librarySavePending = true;
        if (save && !_state.IsImportingPlaylist) Save();
        if (save && added.Length > 0) _state.ScheduleLibraryRefresh();
        return added.Length;
    }

    private void RegisterKnownTracks(IEnumerable<Track> tracks)
    {
        foreach (var track in tracks)
        {
            var key = LibraryTrackKey(track.FilePath);
            if (_trackCatalog.TryGetValue(key, out var known))
            {
                known.ExplicitlyAddedToLibrary |= track.ExplicitlyAddedToLibrary;
                track.ExplicitlyAddedToLibrary |= known.ExplicitlyAddedToLibrary;
            }
            else _trackCatalog.Add(key, track);
        }
    }

    public void MarkTracksExplicit(IEnumerable<Track> tracks, bool savePending = true)
    {
        var incoming = tracks.ToArray();
        var paths = incoming.Select(t => LibraryTrackKey(t.FilePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var track in _state.KnownPlaybackTracks().Concat(incoming))
            if (paths.Contains(LibraryTrackKey(track.FilePath))) track.ExplicitlyAddedToLibrary = true;
        if (savePending && paths.Count > 0) _librarySavePending = true;
        _state.MembershipChanged(refreshView: false);
    }

    public void Save()
    {
        if (!_canSaveLibrary || !_librarySavePending || _state.IsRelinkingTrack) return; // Preserve unreadable storage for recovery.
        try
        {
            _libraryStore?.Save(_state.KnownPlaybackTracks().DistinctBy(t => LibraryTrackKey(t.FilePath), StringComparer.OrdinalIgnoreCase));
            _librarySavePending = false;
            _state.LibraryError = null;
        }
        catch (Exception ex) { _state.LibraryError = $"Library changes could not be saved: {ex.Message}"; }
    }

    public async Task SaveAsync()
    {
        if (!_canSaveLibrary || !_librarySavePending || _state.IsRelinkingTrack) return;
        var snapshot = _state.KnownPlaybackTracks().DistinctBy(t => LibraryTrackKey(t.FilePath), StringComparer.OrdinalIgnoreCase).ToArray();
        try
        {
            await Task.Run(() => _libraryStore?.Save(snapshot));
            _librarySavePending = false;
            _state.LibraryError = null;
        }
        catch (Exception ex) { _state.LibraryError = $"Library changes could not be saved: {ex.Message}"; }
    }

    public void Dispose()
    {
        Tracks.CollectionChanged -= OnLibraryCollectionChanged;
        _state.Playlists.CollectionChanged -= OnLibraryPlaylistsChanged;
        foreach (var playlist in _libraryPlaylists)
            playlist.Tracks.CollectionChanged -= OnLibraryPlaylistTracksChanged;
    }

    private void OnLibraryPlaylistsChanged(object? sender, NotifyCollectionChangedEventArgs e) => SynchronizeLibraryPlaylists();

    private void OnLibraryPlaylistTracksChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshPlaylistMembership();

    private void OnLibraryCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RegisterKnownTracks(e.NewItems?.Cast<Track>() ?? (e.Action == NotifyCollectionChangedAction.Reset ? Tracks : []));

}
