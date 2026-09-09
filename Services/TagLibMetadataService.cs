using System.IO;
using MusicPlayer.Models;

namespace MusicPlayer.Services;

public sealed class TagLibMetadataService : IMetadataService
{
    public Track ReadTrack(string filePath)
    {
        using var tagFile = TagLib.File.Create(filePath);

        var artwork = tagFile.Tag.Pictures.FirstOrDefault();
        var info = new FileInfo(filePath);

        return new Track
        {
            FilePath = filePath,
            FileSize = info.Length,
            LastWriteTimeUtcTicks = info.LastWriteTimeUtc.Ticks,

            Title = string.IsNullOrWhiteSpace(tagFile.Tag.Title)
                ? Path.GetFileNameWithoutExtension(filePath)
                : tagFile.Tag.Title,

            Artist = tagFile.Tag.FirstPerformer,
            MusicBrainzArtistId = ReadArtistId(tagFile.Tag),

            Album = tagFile.Tag.Album,
            ReleaseTypeTag = ReadReleaseType(tagFile),

            Duration = tagFile.Properties.Duration,

            ArtworkData = artwork?.Data.Data
        };
    }

    private static string? ReadArtistId(TagLib.Tag tag)
    {
        var value = tag.MusicBrainzArtistId;
        if (MusicBrainzId.Normalize(value) is { } id) return id;
        // Multi-value tags follow performer order; the library currently uses FirstPerformer.
        var ids = value?.Split([';', '/', '\0'], StringSplitOptions.RemoveEmptyEntries);
        return ids is { Length: > 1 } && ids.Length == tag.Performers.Length &&
               ids.All(id => MusicBrainzId.Normalize(id) is not null)
            ? MusicBrainzId.Normalize(ids[0]) : null;
    }

    private static string? ReadReleaseType(TagLib.File file)
    {
        var releaseType = file.Tag.MusicBrainzReleaseType;
        if (!string.IsNullOrWhiteSpace(releaseType)) return releaseType;

        if (file.GetTag(TagLib.TagTypes.Xiph) is not TagLib.Ogg.XiphComment comments) return null;

        // Some FLAC taggers store release types in MEDIATYPE. It can also describe
        // a physical medium (CD, vinyl, etc.), so only accept explicit release types.
        var types = comments.GetField("MEDIATYPE")
            .SelectMany(value => value.Split([';', '/', ',', '\0'], StringSplitOptions.RemoveEmptyEntries))
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(value => value is "album" or "single" or "ep" or "other" or "broadcast")
            .Distinct().ToArray();
        return types.Length == 0 ? null : string.Join(';', types);
    }
}
