using MusicPlayer.Models;
using MusicPlayer.ViewModels;

internal static partial class Program
{
    private static void CheckCoordinatorLifecycle()
    {
        var player = new FakePlayer();
        using var vm = new MainViewModel(new Picker(), new Picker(), new Metadata(), new Scanner(), player);
        var first = Track("Coordinator first");
        var second = Track("Coordinator second");
        var playlist = new Playlist([first, second]);
        vm.Playlists.Add(playlist);
        Check(vm.Tracks.SequenceEqual(new[] { first, second }),
            "Library coordinator tracks playlist membership without explicit additions");

        vm.Playlists.Remove(playlist);
        playlist.Tracks.Add(Track("Detached playlist"));
        Check(vm.Tracks.Count == 0, "Removed playlists no longer update library membership");

        vm.Playlists.Add(playlist);
        vm.SelectedTrack = first;
        vm.PlaySelectedTrackCommand.Execute(null);
        vm.Queue.Add(second);
        var timeline = vm.QueueTimeline.ToArray();
        var library = vm.Tracks.ToArray();
        vm.Dispose();

        player.End();
        Check(vm.CurrentTrack == first && vm.Queue.SequenceEqual(new[] { second }),
            "Disposed playback coordination ignores backend completion events");
        vm.Queue.Add(first);
        Check(vm.QueueTimeline.SequenceEqual(timeline),
            "Disposed queue coordination detaches timeline collection handlers");
        playlist.Tracks.Add(Track("After disposal"));
        vm.Playlists.Add(new Playlist([Track("After disposal playlist")]));
        Check(vm.Tracks.SequenceEqual(library),
            "Disposed library coordination detaches playlist and track collection handlers");
    }
}
