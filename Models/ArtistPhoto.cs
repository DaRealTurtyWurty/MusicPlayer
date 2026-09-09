using System.Windows.Media.Imaging;

namespace MusicPlayer.Models;

public sealed record ArtistPhotoCredit(string Provider, string SourceUrl, string Credit, string License,
    string? LicenseUrl, string Title)
{
    public string Caption => $"{Credit} · {License}";
    public string Description => $"{Title}\nPhoto: {Credit}\n{License} · {Provider}\nResized; cropped where needed to fit.\n{SourceUrl}";
}

public sealed record ArtistPhoto(BitmapSource Image, ArtistPhotoCredit Attribution, bool IsCustom = false);
