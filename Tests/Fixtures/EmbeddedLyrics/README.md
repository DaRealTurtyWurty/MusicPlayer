These two 0.1-second silent files were generated locally with FFmpeg for container/tag
integration tests. They contain no music or lyrics; tests copy them to temporary
directories before writing original test text into their tags.

Generation commands (FFmpeg is not required to run the tests):

```
ffmpeg -f lavfi -i anullsrc=r=44100:cl=mono -t 0.1 -c:a aac silence.m4a
ffmpeg -f lavfi -i anullsrc=r=44100:cl=mono -t 0.1 -c:a libvorbis silence.ogg
```
