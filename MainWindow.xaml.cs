using System.Windows;
using System.Windows.Input;
using System.Diagnostics;
using System.Windows.Interop;
using System.Windows.Threading;
using MusicPlayer.Services;
using MusicPlayer.ViewModels;

namespace MusicPlayer;

public partial class MainWindow : Window
{
    private IInputElement? _focusBeforeImport;
    private SystemMediaControlsBinding? _mediaControls;
    private TaskbarPreviewService? _taskbarPreview;
    private IInputElement? _focusBeforeClearQueue;
    private IInputElement? _focusBeforeDeletePlaylist;
    private readonly DispatcherTimer _volumeCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };

    private void VolumeControl_MouseEnter(object sender, MouseEventArgs e) => OpenVolumePopup();

    private void VolumeControl_MouseLeave(object sender, MouseEventArgs e) => _volumeCloseTimer.Start();

    private void VolumeControl_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => OpenVolumePopup();

    private void VolumeControl_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => _volumeCloseTimer.Start();

    private void OpenVolumePopup()
    {
        _volumeCloseTimer.Stop();
        VolumePopup.IsOpen = true;
    }

    private void CloseVolumePopup()
    {
        _volumeCloseTimer.Stop();
        VolumePopup.IsOpen = false;
    }

    private void VolumeCloseTimer_Tick(object? sender, EventArgs e)
    {
        // Keep the popup alive while crossing the gap or dragging beyond its bounds.
        if (VolumePopupContent.IsMouseCaptureWithin) return;
        _volumeCloseTimer.Stop();
        if (!MuteButton.IsMouseOver && !VolumePopupContent.IsMouseOver)
            VolumePopup.IsOpen = false;
    }

    private void VolumeControl_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            MuteButton.Focus();
            CloseVolumePopup();
            e.Handled = true;
        }
        else if (sender == MuteButton && e.Key is Key.Up or Key.Down)
        {
            OpenVolumePopup();
            VolumeSlider.Focus();
            e.Handled = true;
        }
    }

    private void ImportProgressPanel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            CloseVolumePopup();
            _focusBeforeImport = System.Windows.Input.Keyboard.FocusedElement;
            ImportProgressPanel.Focus();
        }
        else if (_focusBeforeImport is { } previous)
        {
            System.Windows.Input.Keyboard.Focus(previous);
            _focusBeforeImport = null;
        }
    }

    private void ClearQueueDialog_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            CloseVolumePopup();
            _focusBeforeClearQueue = Keyboard.FocusedElement;
            CancelClearQueueButton.Focus();
        }
        else if (_focusBeforeClearQueue is { } previous)
        {
            Keyboard.Focus(previous);
            _focusBeforeClearQueue = null;
        }
    }

    public MainWindow() : this(CreateViewModel())
    {
    }

    private void DeletePlaylistDialog_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            CloseVolumePopup();
            _focusBeforeDeletePlaylist = Keyboard.FocusedElement;
            CancelDeletePlaylistButton.Focus();
        }
        else if (_focusBeforeDeletePlaylist is { } previous)
        {
            Keyboard.Focus(previous);
            _focusBeforeDeletePlaylist = null;
        }
    }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _volumeCloseTimer.Tick += VolumeCloseTimer_Tick;
        Deactivated += (_, _) => CloseVolumePopup();
    }

    private static MainViewModel CreateViewModel()
    {
        var metadataService = new TagLibMetadataService();
        var musicStore = new SqliteMusicStore();
        return new MainViewModel(
            new FilePickerService(),
            new FolderPickerService(),
            metadataService,
            new LibraryScanner(metadataService),
            new NAudioPlayer(),
            playlistStore: musicStore,
            uiPreferencesStore: new JsonUiPreferencesStore(),
            libraryStore: musicStore,
            playbackSessionStore: musicStore,
            releaseTypeStore: musicStore,
            artistIdentityService: new MusicBrainzArtistService(musicStore),
            artistPhotoService: new ArtistPhotoService(fanartApiKey: ArtistPhotoService.ConfiguredFanartApiKey),
            libraryRefreshService: new LibraryRefreshService(metadataService),
            trackMatchPicker: new MusicPlayer.Views.TrackMatchPicker()
        );
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (DataContext is not MainViewModel viewModel) return;
        try
        {
            _taskbarPreview = new TaskbarPreviewService(this, viewModel);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Taskbar preview is unavailable: {ex.Message}");
        }
        try
        {
            _mediaControls = new SystemMediaControlsBinding(viewModel,
                new WindowsSystemMediaControls(new WindowInteropHelper(this).Handle), Dispatcher);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Windows media controls are unavailable: {ex.Message}");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _taskbarPreview?.Dispose();
        _mediaControls?.Dispose();
        CloseVolumePopup();
        _volumeCloseTimer.Tick -= VolumeCloseTimer_Tick;
        (DataContext as MainViewModel)?.Dispose();
        base.OnClosed(e);
    }
}
