namespace MusicPlayer.Services;

public interface IGaplessAudioPlayer
{
    /// <summary>Raised on the UI thread when the prepared track becomes audible.</summary>
    event EventHandler? NextTrackStarted;
    /// <summary>Prepare the next decoder without consuming the queue. Null cancels it.</summary>
    void PrepareNext(string? filePath);
}
