using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MusicPlayer.Models;
using MusicPlayer.Services;

namespace MusicPlayer.Controls;

public partial class ArtistArtwork : UserControl
{
    public static readonly DependencyProperty ArtistIdProperty = DependencyProperty.Register(nameof(ArtistId), typeof(string), typeof(ArtistArtwork),
        new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty ArtistNameProperty = DependencyProperty.Register(nameof(ArtistName), typeof(string), typeof(ArtistArtwork),
        new PropertyMetadata("", Changed));
    public static readonly DependencyProperty TracksProperty = DependencyProperty.Register(nameof(Tracks), typeof(IReadOnlyList<Track>), typeof(ArtistArtwork),
        new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty ServiceProperty = DependencyProperty.Register(nameof(Service), typeof(IArtistPhotoService), typeof(ArtistArtwork),
        new PropertyMetadata(null, Changed));
    public string? ArtistId { get => (string?)GetValue(ArtistIdProperty); set => SetValue(ArtistIdProperty, value); }
    public string ArtistName { get => (string)GetValue(ArtistNameProperty); set => SetValue(ArtistNameProperty, value); }
    public IReadOnlyList<Track> Tracks { get => (IReadOnlyList<Track>?)GetValue(TracksProperty) ?? []; set => SetValue(TracksProperty, value); }
    public IArtistPhotoService? Service { get => (IArtistPhotoService?)GetValue(ServiceProperty); set => SetValue(ServiceProperty, value); }
    public Task ArtworkReady { get; private set; } = Task.CompletedTask;
    public Task PhotoEditReady { get; private set; } = Task.CompletedTask;
    public ArtistPhoto? Photo { get; private set; }
    private CancellationTokenSource? _cancellation;
    private readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromMinutes(2) };
    private string? _requestedId;
    private string? _requestedName;
    private IReadOnlyList<Track>? _requestedTracks;
    private IArtistPhotoService? _requestedService;
    private IArtistPhotoCustomization? _subscribed;
    private bool _editing;
    private readonly IArtistPhotoFilePicker _photoPicker;

    public ArtistArtwork() : this(new ArtistPhotoFilePicker()) { }

    public ArtistArtwork(IArtistPhotoFilePicker photoPicker)
    {
        _photoPicker = photoPicker;
        InitializeComponent();
        Loaded += (_, _) => Refresh();
        IsVisibleChanged += (_, _) => Refresh();
        Unloaded += (_, _) => { Stop(); Subscribe(null); };
        _retry.Tick += (_, _) => { if (ArtworkReady.IsCompleted) Refresh(force: true); };
    }

    private static void Changed(DependencyObject control, DependencyPropertyChangedEventArgs args) => ((ArtistArtwork)control).Refresh();

    private void Subscribe(IArtistPhotoCustomization? service)
    {
        if (ReferenceEquals(_subscribed, service)) return;
        if (_subscribed is not null) _subscribed.PhotoChanged -= OnPhotoChanged;
        _subscribed = service;
        if (_subscribed is not null) _subscribed.PhotoChanged += OnPhotoChanged;
    }

    private void OnPhotoChanged(object? sender, ArtistPhotoChangedEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (IsLoaded && ReferenceEquals(sender, Service) &&
                ArtistPhotoService.NormalizeArtistName(ArtistName) == ArtistPhotoService.NormalizeArtistName(e.ArtistName)) Refresh(force: true);
        });
    }
    private void Stop()
    {
        _retry.Stop();
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }

    private void Refresh(bool force = false)
    {
        if (PhotoImage is null) return;
        if (!IsLoaded || !IsVisible) { Stop(); Subscribe(null); return; }
        Subscribe(Service as IArtistPhotoCustomization);
        if (!force && _cancellation is not null && _requestedId == ArtistId && _requestedName == ArtistName &&
            ReferenceEquals(_requestedTracks, Tracks) && ReferenceEquals(_requestedService, Service)) return;
        Stop();
        _requestedId = ArtistId;
        _requestedName = ArtistName;
        _requestedTracks = Tracks;
        _requestedService = Service;
        ShowPhoto(null);
        if (Service is not { } service) return;
        _cancellation = new CancellationTokenSource();
        ArtworkReady = LoadAsync(service, ArtistId, ArtistName, _cancellation.Token);
    }

    private async Task LoadAsync(IArtistPhotoService service, string? id, string artistName, CancellationToken token)
    {
        try
        {
            var photo = await service.LoadAsync(id, artistName, Tracks, token);
            if (token.IsCancellationRequested) return;
            ShowPhoto(photo);
            if (photo is null) _retry.Start();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            Trace.TraceWarning($"Artist photo control unavailable ({ex.GetType().Name}).");
            _retry.Start();
        }
    }

    private void ShowPhoto(ArtistPhoto? photo)
    {
        Photo = photo;
        PhotoImage.Source = photo?.Image;
        // Wide band portraits/composites should not crop away most of the members.
        PhotoImage.Stretch = photo is not null && photo.Image.PixelWidth > photo.Image.PixelHeight * 1.8
            ? Stretch.Uniform : Stretch.UniformToFill;
        ToolTip = photo?.Attribution.Description ?? "Artist photo unavailable";
        var menu = new ContextMenu();
        if (Service is IArtistPhotoCustomization customization && !string.IsNullOrWhiteSpace(ArtistName))
        {
            // Capture the target before a picker opens: a recycled tile must not edit a different artist.
            var name = ArtistName;
            var choose = new MenuItem { Header = "Choose artist photo", IsEnabled = !_editing };
            choose.Click += (_, e) =>
            {
                e.Handled = true;
                if (_photoPicker.PickPhoto(name, Window.GetWindow(this)) is { } path)
                    PhotoEditReady = EditPhotoAsync(() => customization.SetCustomPhotoAsync(name, path));
            };
            menu.Items.Add(choose);
            var reset = new MenuItem { Header = "Reset to automatic", IsEnabled = !_editing };
            reset.Click += (_, e) =>
            {
                e.Handled = true;
                PhotoEditReady = EditPhotoAsync(() => customization.ResetCustomPhotoAsync(name));
            };
            menu.Items.Add(reset);
        }
        void Link(string label, string? url)
        {
            if (!ArtistPhotoProvider.WebUrl(url)) return;
            if (menu.Items.Count == 2 && menu.Items[0] is MenuItem { Header: "Choose artist photo" }) menu.Items.Add(new Separator());
            var item = new MenuItem { Header = label };
            item.Click += (_, e) =>
            {
                e.Handled = true;
                try { Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true }); }
                catch (Exception ex) { Trace.TraceWarning($"Could not open photo credit: {ex.Message}"); }
            };
            menu.Items.Add(item);
        }
        Link("Photo source and credits", photo?.Attribution.SourceUrl);
        Link("Photo licence", photo?.Attribution.LicenseUrl);
        ContextMenu = menu.Items.Count > 0 ? menu : null;
    }

    private async Task EditPhotoAsync(Func<Task> edit)
    {
        if (_editing) return;
        _editing = true;
        ShowPhoto(Photo);
        try { await edit(); }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Could not change artist photo: {ex.Message}");
            MessageBox.Show("Could not change the artist photo. Choose a valid image under 8 MB and check that the app's storage is writable.",
                "Artist photo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _editing = false; ShowPhoto(Photo); }
    }
}
