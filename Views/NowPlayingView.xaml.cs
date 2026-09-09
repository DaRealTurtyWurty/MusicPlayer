using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Views;

public partial class NowPlayingView : UserControl
{
    private LyricsViewModel? _lyrics;
    private MainViewModel? _main;
    private bool _rendering;
    private bool _attached;
    private bool _following = true;
    private bool _resettingScrollAnimation;
    private DispatcherOperation? _pendingFollow;

    public NowPlayingView() => InitializeComponent();

    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        _attached = true;
        AttachLyrics();
        IsVisibleChanged += View_IsVisibleChanged;
    }

    private void View_Unloaded(object sender, RoutedEventArgs e)
    {
        _attached = false;
        IsVisibleChanged -= View_IsVisibleChanged;
        if (_main is not null) _main.PropertyChanged -= Main_PropertyChanged;
        _main = null;
        UpdateRenderingSubscription();
        if (_lyrics is not null) _lyrics.PropertyChanged -= Lyrics_PropertyChanged;
        _lyrics = null;
        _pendingFollow?.Abort();
        StopScrollAnimation();
    }

    private void View_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_attached) AttachLyrics();
    }

    private void AttachLyrics()
    {
        if (_main is not null) _main.PropertyChanged -= Main_PropertyChanged;
        if (_lyrics is not null) _lyrics.PropertyChanged -= Lyrics_PropertyChanged;
        _main = DataContext as MainViewModel;
        if (_main is not null) _main.PropertyChanged += Main_PropertyChanged;
        _lyrics = _main?.Lyrics;
        if (_lyrics is not null) _lyrics.PropertyChanged += Lyrics_PropertyChanged;
        ResumeFollowing();
        UpdateLayoutMode();
        UpdateRenderingSubscription();
    }

    private void Lyrics_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LyricsViewModel.IsEnabled) or nameof(LyricsViewModel.HasLyrics)) UpdateRenderingSubscription();
        if (e.PropertyName == nameof(LyricsViewModel.IsEnabled)) UpdateLayoutMode();
        if (e.PropertyName == nameof(LyricsViewModel.Lines))
        {
            StopScrollAnimation();
            LyricsScroll.ScrollToTop();
            ResumeFollowing();
        }
        if (e.PropertyName is nameof(LyricsViewModel.ActiveLine) or nameof(LyricsViewModel.ActiveLines)) ScheduleFollow();
    }

    private void Main_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsPlaying)) UpdateRenderingSubscription();
    }

    private void View_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateRenderingSubscription();

    private void UpdateRenderingSubscription()
    {
        var enabled = _attached && IsVisible && _main?.IsPlaying == true && _lyrics is { IsEnabled: true, HasLyrics: true };
        if (enabled == _rendering) return;
        _rendering = enabled;
        if (enabled) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e) => _main?.RefreshLyricsPosition();

    private void View_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateLayoutMode();

    private void UpdateLayoutMode()
    {
        if (ContentGrid is null) return;
        var enabled = (DataContext as MainViewModel)?.Lyrics.IsEnabled == true;
        var compact = enabled && ActualWidth < 760;
        var verticalInfo = enabled && !compact || !enabled && ActualWidth < 460;
        TextBlock.SetTextAlignment(TrackDetails, enabled ? TextAlignment.Center : TextAlignment.Left);
        GoToLibrary.HorizontalAlignment = enabled ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        InfoColumn.Width = new GridLength(1, GridUnitType.Star);
        LyricsColumn.Width = new GridLength(enabled && !compact ? 1.4 : 0, GridUnitType.Star);
        InfoRow.Height = compact ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        LyricsRow.Height = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(LyricsPanel, compact ? 0 : 1);
        Grid.SetRow(LyricsPanel, compact ? 1 : 0);
        TrackScroll.MaxHeight = compact ? Math.Max(100, Math.Min(160, ActualHeight * 0.3)) : double.PositiveInfinity;
        TrackInfo.Margin = compact ? new Thickness(0, 0, 0, 18) : enabled ? new Thickness(0, 12, 28, 12) : new Thickness(0, 12, 0, 12);
        TrackInfo.MaxWidth = verticalInfo ? 328 : double.PositiveInfinity;
        ArtworkColumn.Width = compact ? new GridLength(80) : new GridLength(1, GridUnitType.Star);
        DetailsColumn.Width = new GridLength(verticalInfo ? 0 : 1, GridUnitType.Star);
        Grid.SetColumn(TrackDetails, verticalInfo ? 0 : 1);
        Grid.SetRow(TrackDetails, verticalInfo ? 1 : 0);
        TrackDetails.Margin = verticalInfo ? new Thickness(0, 22, 0, 0) : new Thickness(24, 0, 0, 0);
        Artwork.MaxWidth = compact ? 80 : enabled ? Math.Clamp(ActualHeight - 290, 120, 300) : 320;
        AlbumLabel.Visibility = DurationLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ScheduleFollow();
    }

    private void ScheduleFollow()
    {
        if (!_attached || !_following || _lyrics?.IsEnabled != true) return;
        _pendingFollow?.Abort();
        _pendingFollow = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FollowActiveLine());
    }

    private void FollowActiveLine()
    {
        if (!_attached || !_following || _lyrics?.ActiveLine is not { } active || !LyricsScroll.IsVisible) return;
        if (LyricsItems.ItemContainerGenerator.ContainerFromItem(active) is not FrameworkElement container) return;
        var top = container.TranslatePoint(new Point(), LyricsScroll).Y + LyricsScroll.VerticalOffset;
        var bottom = top + container.ActualHeight;
        var groupTop = top;
        var groupBottom = bottom;
        foreach (var row in _lyrics.ActiveLines)
        {
            if (LyricsItems.ItemContainerGenerator.ContainerFromItem(row) is not FrameworkElement part) continue;
            var y = part.TranslatePoint(new Point(), LyricsScroll).Y + LyricsScroll.VerticalOffset;
            groupTop = Math.Min(groupTop, y);
            groupBottom = Math.Max(groupBottom, y + part.ActualHeight);
        }
        if (groupBottom - groupTop <= LyricsScroll.ViewportHeight * 0.85) { top = groupTop; bottom = groupBottom; }
        var target = Math.Max(top - LyricsScroll.ViewportHeight * 0.3, Math.Min(top, bottom - LyricsScroll.ViewportHeight * 0.9));
        target = Math.Clamp(target, 0, LyricsScroll.ScrollableHeight);
        var current = LyricsScroll.VerticalOffset;
        StopScrollAnimation();
        if (!SystemParameters.ClientAreaAnimation)
        {
            LyricsScroll.ScrollToVerticalOffset(target);
            return;
        }
        BeginAnimation(AnimatedOffsetProperty, new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.HoldEnd
        });
    }

    private static readonly DependencyProperty AnimatedOffsetProperty = DependencyProperty.Register(
        "AnimatedOffset", typeof(double), typeof(NowPlayingView), new PropertyMetadata(0d, (sender, args) =>
        {
            var view = (NowPlayingView)sender;
            if (!view._resettingScrollAnimation) view.LyricsScroll.ScrollToVerticalOffset((double)args.NewValue);
        }));

    private void PauseFollowing()
    {
        _following = false;
        _pendingFollow?.Abort();
        StopScrollAnimation();
        ResumeFollowingButton.Visibility = Visibility.Visible;
    }

    private void StopScrollAnimation()
    {
        var current = LyricsScroll.VerticalOffset;
        // Removing an animation changes its DP value. Don't enqueue an old scroll
        // position here: that command can execute AFTER the user's wheel/key input.
        _resettingScrollAnimation = true;
        try
        {
            BeginAnimation(AnimatedOffsetProperty, null);
            SetValue(AnimatedOffsetProperty, current);
        }
        finally { _resettingScrollAnimation = false; }
    }

    private void ResumeFollowing()
    {
        _following = true;
        ResumeFollowingButton.Visibility = Visibility.Collapsed;
        ScheduleFollow();
    }

    private void Lyrics_ManualScroll(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0 || LyricsScroll.ScrollableHeight <= 0 || !LyricsScroll.IsVisible) return;
        PauseFollowing();
        var distance = SystemParameters.WheelScrollLines < 0
            ? LyricsScroll.ViewportHeight : SystemParameters.WheelScrollLines * 24d;
        LyricsScroll.ScrollToVerticalOffset(Math.Clamp(
            LyricsScroll.VerticalOffset - e.Delta / 120d * distance, 0, LyricsScroll.ScrollableHeight));
        e.Handled = true;
    }
    private void Lyrics_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is Visual; node = VisualTreeHelper.GetParent(node))
            if (node is ScrollBar) { PauseFollowing(); break; }
    }

    private void Lyrics_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End) PauseFollowing();
    }

    private void Lyric_Click(object sender, RoutedEventArgs e) => ResumeFollowing();
    private void ResumeFollowing_Click(object sender, RoutedEventArgs e) => ResumeFollowing();
}
