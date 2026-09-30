namespace MusicPlayer.Models;

public enum LibrarySort { Artist, Title, Album, Year, Duration, FilePath, PlayCount }
public sealed record LibraryWorkflowPreferences(LibrarySort Sort = LibrarySort.Artist,
    bool Descending = false, bool AutomaticScanning = true);
