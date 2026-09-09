using System.Windows;
using Microsoft.Win32;

namespace MusicPlayer.Services;

public interface IArtistPhotoFilePicker
{
    string? PickPhoto(string artistName, Window? owner);
}

public sealed class ArtistPhotoFilePicker : IArtistPhotoFilePicker
{
    public string? PickPhoto(string artistName, Window? owner)
    {
        var picker = new OpenFileDialog
        {
            Title = $"Choose artist photo for {artistName}",
            Filter = "Images (*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff)|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff",
            CheckFileExists = true, Multiselect = false
        };
        return (owner is null ? picker.ShowDialog() : picker.ShowDialog(owner)) == true ? picker.FileName : null;
    }
}
