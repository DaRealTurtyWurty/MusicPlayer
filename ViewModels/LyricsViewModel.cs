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
    private readonly ILyricsFilePicker _filePicker;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private CancellationTokenSource? _loadCancellation;
    private Track? _track;
    private TimeSpan? _duration;
    private LyricsDocument? _document;
    private double _position;
    private bool _disposed;

    public const double MinimumFontSize = 16;
    public const double MaximumFontSize = 40;
    public const double MaximumTimingAdjustment = 30;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundFontSize))]
    private double fontSize = 25;
    public double BackgroundFontSize => FontSize * 0.8;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimingAdjustmentLabel))]
    private double timingAdjustmentSeconds;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceLabel))]
    [NotifyCanExecuteChangedFor(nameof(UseAutomaticSourceCommand))]
    private string? selectedLyricsFilePath;

    public string SourceLabel => SelectedLyricsFilePath is null ? "Automatic local lyrics" : System.IO.Path.GetFileName(SelectedLyricsFilePath);
    public string TimingAdjustmentLabel => TimingAdjustmentSeconds == 0 ? "Timing: original"
        : $"{Math.Abs(TimingAdjustmentSeconds):0.0} s {(TimingAdjustmentSeconds > 0 ? "earlier" : "later")}";
    public bool HasTimedLyrics => Lines.Any(row => row.IsTimed);

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

    public LyricsViewModel(ILocalLyricsSource source, IUiPreferencesStore? preferences = null, ILyricsFilePicker? filePicker = null)
    {
        _source = source;
        _preferences = preferences;
        _filePicker = filePicker ?? new LyricsFilePicker();
        isEnabled = preferences?.LoadLyricsEnabled() ?? true;
        var savedFontSize = preferences?.LoadLyricsFontSize() ?? 25;
        fontSize = double.IsFinite(savedFontSize) ? Math.Clamp(savedFontSize, MinimumFontSize, MaximumFontSize) : 25;
    }

    partial void OnFontSizeChanged(double value)
    {
        var normalized = double.IsFinite(value) ? Math.Clamp(value, MinimumFontSize, MaximumFontSize) : 25;
        if (value != normalized) { FontSize = normalized; return; }
        _preferences?.SaveLyricsFontSize(value);
    }

    partial void OnTimingAdjustmentSecondsChanged(double value)
    {
        var normalized = double.IsFinite(value) ? Math.Round(Math.Clamp(value, -MaximumTimingAdjustment, MaximumTimingAdjustment), 1) : 0;
        if (value != normalized) { TimingAdjustmentSeconds = normalized; return; }
        UpdatePosition(_position);
    }

    [RelayCommand] private void ShowEarlier() => TimingAdjustmentSeconds += 0.1;
    [RelayCommand] private void ShowLater() => TimingAdjustmentSeconds -= 0.1;
    [RelayCommand] private void ResetTiming() => TimingAdjustmentSeconds = 0;

    private bool CanChooseFile() => !_disposed && IsEnabled && _track is not null;
    [RelayCommand(CanExecute = nameof(CanChooseFile))]
    private void ChooseFile()
    {
        var track = _track;
        if (_filePicker.PickLyricsFile() is not { } path) return;
        // Playback can advance while the modal picker is open.
        if (_disposed || !IsEnabled || !ReferenceEquals(track, _track)) return;
        SelectedLyricsFilePath = path;
        StartLoad();
    }

    private bool CanUseAutomaticSource() => CanChooseFile() && SelectedLyricsFilePath is not null;
    [RelayCommand(CanExecute = nameof(CanUseAutomaticSource))]
    private void UseAutomaticSource()
    {
        SelectedLyricsFilePath = null;
        StartLoad();
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
        if (!string.Equals(_track?.FilePath, track?.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            SelectedLyricsFilePath = null;
            TimingAdjustmentSeconds = 0;
        }
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
        ChooseFileCommand.NotifyCanExecuteChanged();
        UseAutomaticSourceCommand.NotifyCanExecuteChanged();
        if (_disposed || !IsEnabled || _track is null) return;

        var cancellation = _loadCancellation = new CancellationTokenSource();
        IsLoading = true;
        StatusTitle = "Loading lyrics…";
        StatusDetail = "";
        LoadingTask = LoadAsync(_track, _duration, SelectedLyricsFilePath, cancellation.Token);
    }

    private async Task LoadAsync(Track track, TimeSpan? duration, string? lyricsFilePath, CancellationToken cancellationToken)
    {
        LocalLyricsResult result;
        try
        {
            result = await _source.LoadAsync(track.FilePath, duration, lyricsFilePath, cancellationToken).ConfigureAwait(false);
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
                LocalLyricsStatus.NotFound when lyricsFilePath is not null => ("Lyrics file not found", "The selected file is no longer available. Choose another file or return to Automatic."),
                LocalLyricsStatus.NotFound => ("No local lyrics", "Choose a lyrics file, or place an .lrc, .ttml or .lyricsfile.yaml file with the same name beside this audio file."),
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
        var lyricSeconds = seconds + _document.Offset.TotalSeconds + TimingAdjustmentSeconds;
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
        SeekRequested?.Invoke(Math.Clamp(row.Line.Start.TotalSeconds - _document.Offset.TotalSeconds - TimingAdjustmentSeconds,
            0, _duration?.TotalSeconds ?? double.MaxValue));
    }

    public void Dispose()
    {
        _disposed = true;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        ChooseFileCommand.NotifyCanExecuteChanged();
        UseAutomaticSourceCommand.NotifyCanExecuteChanged();
        ReloadCommand.NotifyCanExecuteChanged();
    }

    partial void OnLinesChanged(IReadOnlyList<LyricLineViewModel> value) => OnPropertyChanged(nameof(HasTimedLyrics));
}
