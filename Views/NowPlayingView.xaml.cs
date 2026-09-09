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
    private bool _attached;
    private bool _following = true;
    private DispatcherOperation? _pendingFollow;

    public NowPlayingView() => InitializeComponent();

    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        _attached = true;
        AttachLyrics();
    }

    private void View_Unloaded(object sender, RoutedEventArgs e)
    {
        _attached = false;
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
        if (_lyrics is not null) _lyrics.PropertyChanged -= Lyrics_PropertyChanged;
        _lyrics = (DataContext as MainViewModel)?.Lyrics;
        if (_lyrics is not null) _lyrics.PropertyChanged += Lyrics_PropertyChanged;
        ResumeFollowing();
        UpdateLayoutMode();
    }

    private void Lyrics_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsViewModel.IsEnabled)) UpdateLayoutMode();
        if (e.PropertyName == nameof(LyricsViewModel.Lines))
        {
            StopScrollAnimation();
            LyricsScroll.ScrollToTop();
            ResumeFollowing();
        }
        if (e.PropertyName == nameof(LyricsViewModel.ActiveLine)) ScheduleFollow();
    }

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
        var target = Math.Clamp(top - LyricsScroll.ViewportHeight * 0.3, 0, LyricsScroll.ScrollableHeight);
        var current = LyricsScroll.VerticalOffset;
        BeginAnimation(AnimatedOffsetProperty, null);
        SetValue(AnimatedOffsetProperty, current);
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
            ((NowPlayingView)sender).LyricsScroll.ScrollToVerticalOffset((double)args.NewValue)));

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
        BeginAnimation(AnimatedOffsetProperty, null);
        SetValue(AnimatedOffsetProperty, current);
        LyricsScroll.ScrollToVerticalOffset(current);
    }

    private void ResumeFollowing()
    {
        _following = true;
        ResumeFollowingButton.Visibility = Visibility.Collapsed;
        ScheduleFollow();
    }

    private void Lyrics_ManualScroll(object sender, MouseWheelEventArgs e) => PauseFollowing();
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
