using CommunityToolkit.Mvvm.ComponentModel;

namespace MusicPlayer.Models;

public partial class ArtistIdentification : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MusicBrainzId))]
    private ArtistIdentity? result;

    public string? MusicBrainzId => Result?.Status == ArtistIdentityStatus.Identified ? Result.MusicBrainzId : null;
}
