using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicPlayer.Models;
using MusicPlayer.Services;

namespace MusicPlayer.ViewModels;

public sealed partial class LyricLineViewModel(LyricLine line) : ObservableObject
{
    public LyricLine Line { get; } = line;
    public string Text => Line.IsTimed && string.IsNullOrWhiteSpace(Line.Text) ? "•••" : Line.Text;
    public bool IsTimed => Line.IsTimed;
    public string? SeekHint => IsTimed ? "Click to play from this line" : null;
    public string VocalLabel => Line.IsBackground ? string.IsNullOrWhiteSpace(Line.VocalistName) ? "Backing vocals" : $"{Line.VocalistName} · Backing vocals"
        : Line.VocalistName ?? "";
    public bool HasVocalLabel => VocalLabel.Length > 0;
    public bool IsBackground => Line.IsBackground;
    public bool IsSecondaryVocal { get; init; }
    public string SeekLabel => IsTimed ? $"Seek to {Line.Start:m\\:ss}: {VocalLabel} {Text}" : Text;
    [ObservableProperty] private bool isActive;
    [ObservableProperty] private double lyricSeconds;

    public double GetSegmentProgress(int index)
    {
        var segment = Line.Segments[index];
        if (LyricSeconds < segment.Start.TotalSeconds) return 0;
        // With no usable end there is no honest duration to interpolate; highlight at onset.
        if (segment.End is not { } end || end <= segment.Start) return 1;
        return Math.Clamp((LyricSeconds - segment.Start.TotalSeconds) / (end.TotalSeconds - segment.Start.TotalSeconds), 0, 1);
    }
}

/// <summary>Owns local lyrics loading and line selection, independently of the Now Playing view lifetime.</summary>
public sealed partial class LyricsViewModel : ObservableObject, IDisposable
{
    private readonly ILocalLyricsSource _source;
    private readonly IUiPreferencesStore? _preferences;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private CancellationTokenSource? _loadCancellation;
    private Track? _track;
    private TimeSpan? _duration;
    private LyricsDocument? _document;
    private double _position;
    private bool _disposed;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ToggleLabel))]
    private bool isEnabled;
    public string ToggleLabel => IsEnabled ? "Hide lyrics" : "Show lyrics";

    [ObservableProperty] private IReadOnlyList<LyricLineViewModel> lines = Array.Empty<LyricLineViewModel>();
    [ObservableProperty] private LyricLineViewModel? activeLine;
    [ObservableProperty] private IReadOnlyList<LyricLineViewModel> activeLines = Array.Empty<LyricLineViewModel>();
    [ObservableProperty] private string statusTitle = "Nothing playing";
    [ObservableProperty] private string statusDetail = "Play a track to see its local lyrics.";
    [ObservableProperty] private string? warning;
    [ObservableProperty] private bool hasLyrics;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ReloadCommand))]
    private bool isLoading;

    public Task LoadingTask { get; private set; } = Task.CompletedTask;
    public event Action<double>? SeekRequested;

    public LyricsViewModel(ILocalLyricsSource source, IUiPreferencesStore? preferences = null)
    {
        _source = source;
        _preferences = preferences;
        isEnabled = preferences?.LoadLyricsEnabled() ?? true;
    }

    [RelayCommand]
    private void Toggle() => IsEnabled = !IsEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        _preferences?.SaveLyricsEnabled(value);
        StartLoad();
    }

    public void SetTrack(Track? track, TimeSpan? duration)
    {
        _track = track;
        _duration = duration > TimeSpan.Zero ? duration : null;
        _position = 0;
        StartLoad();
    }

    private bool CanReload() => !_disposed && IsEnabled && _track is not null && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanReload))]
    private void Reload() => StartLoad();

    private void StartLoad()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _document = null;
        Lines = Array.Empty<LyricLineViewModel>();
        ActiveLine = null;
        ActiveLines = Array.Empty<LyricLineViewModel>();
        HasLyrics = false;
        Warning = null;
        IsLoading = false;
        StatusTitle = _track is null ? "Nothing playing" : "Lyrics are hidden";
        StatusDetail = _track is null ? "Play a track to see its local lyrics." : "Turn on lyrics to follow this track.";
        ReloadCommand.NotifyCanExecuteChanged();
        if (_disposed || !IsEnabled || _track is null) return;

        var cancellation = _loadCancellation = new CancellationTokenSource();
        IsLoading = true;
        StatusTitle = "Loading lyrics…";
        StatusDetail = "";
        LoadingTask = LoadAsync(_track, _duration, cancellation.Token);
    }

    private async Task LoadAsync(Track track, TimeSpan? duration, CancellationToken cancellationToken)
    {
        LocalLyricsResult result;
        try
        {
            result = await _source.LoadAsync(track.FilePath, duration, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            result = new LocalLyricsResult(LocalLyricsStatus.Unavailable, null, Error: ex.Message);
        }

        if (cancellationToken.IsCancellationRequested || _dispatcher.HasShutdownStarted) return;
        // Even sources that ignore cancellation cannot replace a newer track or update a hidden/disposed panel.
        await _dispatcher.InvokeAsync(() =>
        {
            if (_disposed || cancellationToken.IsCancellationRequested || !ReferenceEquals(track, _track)) return;
            IsLoading = false;
            if (result.Status == LocalLyricsStatus.Loaded && result.Document is { HasLyrics: true } document)
            {
                _document = document;
                var voices = document.Lines.Where(line => !line.IsBackground && line.VocalistId is not null)
                    .Select(line => line.VocalistId!).Distinct(StringComparer.Ordinal).ToList();
                var displayLines = document.TimingMode == LyricsTimingMode.Plain
                    ? (document.PlainText ?? "").ReplaceLineEndings("\n").Split('\n').Select(text =>
                        new LyricLine(text, TimeSpan.Zero, null, false, []) { IsTimed = false })
                    : document.Lines;
                Lines = Array.AsReadOnly(displayLines.Select(line => new LyricLineViewModel(line)
                {
                    IsSecondaryVocal = line.VocalistId is { } id && voices.IndexOf(id) % 2 == 1
                }).ToArray());
                HasLyrics = true;
                StatusTitle = StatusDetail = "";
                Warning = document.Diagnostics.Count == 0 ? null : document.Diagnostics[0].Message;
                UpdatePosition(_position);
                return;
            }
            if (result.Status == LocalLyricsStatus.Loaded && result.Document?.IsInstrumental == true)
            {
                StatusTitle = "Instrumental";
                StatusDetail = "This track has no vocal lyrics.";
                return;
            }
            (StatusTitle, StatusDetail) = result.Status switch
            {
                LocalLyricsStatus.NotFound => ("No local lyrics", "Place an .lrc, .ttml or .lyricsfile.yaml file with the same name beside this audio file."),
                LocalLyricsStatus.Invalid => ("No usable lyrics", result.Error ?? "This file does not contain valid timed lyrics."),
                _ => ("Could not load lyrics", "The lyrics file could not be read. Check that it is accessible.")
            };
        });
    }

    public void UpdatePosition(double seconds)
    {
        _position = seconds;
        if (_document is null || !double.IsFinite(seconds)) return;
        // LRC offset advances display; source timestamps themselves are never modified.
        var lyricSeconds = seconds + _document.Offset.TotalSeconds;
        var active = new List<LyricLineViewModel>();
        foreach (var row in Lines)
        {
            var wasActive = row.IsActive;
            row.IsActive = row.IsTimed && (_duration is null || seconds < _duration.Value.TotalSeconds) &&
                row.Line.Start.TotalSeconds <= lyricSeconds &&
                (row.Line.End is null || lyricSeconds < row.Line.End.Value.TotalSeconds);
            // Only active rows need per-frame notifications. Inactive rows draw a dim line.
            if (row.IsActive || wasActive) row.LyricSeconds = lyricSeconds;
            if (row.IsActive) active.Add(row);
        }
        if (!ActiveLines.SequenceEqual(active)) ActiveLines = active.AsReadOnly();
        // Backing vocals must not pull the viewport away from an ongoing lead vocal.
        ActiveLine = active.FirstOrDefault(row => !row.IsBackground) ?? active.FirstOrDefault();
    }

    [RelayCommand]
    private void SeekToLine(LyricLineViewModel? row)
    {
        if (_disposed || !IsEnabled || _document is null || row is null || !row.IsTimed || !Lines.Contains(row)) return;
        SeekRequested?.Invoke(Math.Clamp(row.Line.Start.TotalSeconds - _document.Offset.TotalSeconds,
            0, _duration?.TotalSeconds ?? double.MaxValue));
    }

    public void Dispose()
    {
        _disposed = true;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
    }
}
