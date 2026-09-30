Two-second silent stereo files generated locally with FFmpeg. They exercise real
decoders without playing music. FFmpeg is not required to run the tests.

```powershell
ffmpeg -f lavfi -i anullsrc=r=44100:cl=stereo -t 2 -c:a pcm_s16le silence.wav
ffmpeg -i silence.wav -c:a libmp3lame silence.mp3
ffmpeg -i silence.wav -c:a flac silence.flac
ffmpeg -i silence.wav -c:a aac silence.m4a
ffmpeg -i silence.wav -c:a libvorbis silence.ogg
```

Run `dotnet run --project Tests/MusicPlayer.QueueTests.csproj -- --audio-smoke`
for decoder, seeking, queue transition, missing/corrupt/empty file, UI binding,
and simulated output failure/device change/sleep-resume coverage.

Run `dotnet run --project Tests/MusicPlayer.QueueTests.csproj -- --audio-device-live`
for native Windows endpoint enumeration, explicit output selection, output clock,
paused/playing seeking, and natural EOF for all five formats using silent audio.

Output selection is in the volume popup and is saved by Windows endpoint ID.
Windows default follows the multimedia default. An unavailable selected device
falls back to the default and is restored when it reconnects. A device failure
keeps the current track and queue for retry; Pause cancels automatic resume.

Physical unplug/replug and actual Windows sleep/resume still need a manual check:
play a track, switch default devices or unplug the selected output, reconnect it,
then sleep/resume once while playing and once while paused. Verify that the queue
does not advance, playback resumes near the audible position, paused playback
stays paused, and failures/fallbacks appear in the app's message area.
