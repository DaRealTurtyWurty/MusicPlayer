using System.IO;
using System.Text;

internal static class FixtureLibrary
{
    // Silent local files for repeatable UI testing without a user's music library.
    public static void Write(string directory)
    {
        Directory.CreateDirectory(directory);
        var tracks = new[]
        {
            ("A Walk", "Tycho", "Dive"),
            ("Weightless", "Marconi Union", "Ambient Transmissions"),
            ("First Breath After Coma", "Explosions in the Sky", "The Earth Is Not a Cold Dead Place"),
            ("Awake", "Tycho", "Awake"),
            ("Open Eye Signal", "Jon Hopkins", "Immunity"),
            ("Dayvan Cowboy", "Boards of Canada", "The Campfire Headphase"),
            ("Says", "Nils Frahm", "Spaces"),
            ("An Ending (Ascent)", "Brian Eno", "Apollo")
        };
        for (var i = 0; i < tracks.Length; i++)
        {
            var path = Path.Combine(directory, $"{i + 1:00}.wav");
            var data = new byte[44100 * 2 * (20 + i * 3)];
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + data.Length);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                writer.Write((short)1); writer.Write((short)1); writer.Write(44100);
                writer.Write(88200); writer.Write((short)2); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(data.Length); writer.Write(data);
            }
            using var file = TagLib.File.Create(path);
            file.Tag.Title = tracks[i].Item1;
            file.Tag.Performers = [tracks[i].Item2];
            file.Tag.Album = tracks[i].Item3;
            file.Save();
        }
        Console.WriteLine(directory);
    }
}
