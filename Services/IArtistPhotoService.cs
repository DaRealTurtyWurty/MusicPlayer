using MusicPlayer.Models;

namespace MusicPlayer.Services;

public interface IArtistPhotoService
{
    Task<ArtistPhoto?> LoadAsync(string musicBrainzArtistId, CancellationToken cancellationToken = default);

    // The library name also identifies a user override, even before MusicBrainz resolves it.
    Task<ArtistPhoto?> LoadAsync(string? musicBrainzArtistId, string artistName, CancellationToken cancellationToken = default) =>
        MusicBrainzId.Normalize(musicBrainzArtistId) is { } id ? LoadAsync(id, cancellationToken) : Task.FromResult<ArtistPhoto?>(null);

    Task<ArtistPhoto?> LoadAsync(string? musicBrainzArtistId, string artistName, IReadOnlyList<Track> tracks,
        CancellationToken cancellationToken = default) => LoadAsync(musicBrainzArtistId, artistName, cancellationToken);
}

public sealed class ArtistPhotoChangedEventArgs(string artistName) : EventArgs
{
    public string ArtistName { get; } = artistName;
}

public interface IArtistPhotoCustomization
{
    event EventHandler<ArtistPhotoChangedEventArgs>? PhotoChanged;
    Task SetCustomPhotoAsync(string artistName, string filePath, CancellationToken cancellationToken = default);
    Task ResetCustomPhotoAsync(string artistName, CancellationToken cancellationToken = default);
}
