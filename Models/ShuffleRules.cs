namespace MusicPlayer.Models;

public sealed record ShuffleRules(bool AvoidConsecutiveArtists = false, bool AvoidConsecutiveAlbums = false);
