namespace MusicPlayer.Services;

public sealed record DiscordPresence(string Title, string? ArtistAndAlbum,
    DateTimeOffset? Start, DateTimeOffset? End, string? ArtworkUrl = null, string? ArtworkText = null);

/// <summary>Used on the WPF dispatcher; network I/O runs on the RPC client's worker.</summary>
public interface IDiscordPresenceClient : IDisposable
{
    bool IsConnected { get; }
    Task Shutdown { get; }
    event Action<string>? StatusChanged;
    void Initialize();
    void Update(DiscordPresence? presence);
    void Pump();
}
