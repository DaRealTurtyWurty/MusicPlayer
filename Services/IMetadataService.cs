using MusicPlayer.Models;

namespace MusicPlayer.Services;

public interface IMetadataService
{
    Track ReadTrack(string filePath);
}