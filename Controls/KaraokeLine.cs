using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Controls;

/// <summary>Shapes a whole lyric line once, then clips the bright copy to its timed text ranges.</summary>
public sealed class KaraokeLine : Control
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(nameof(Row), typeof(LyricLineViewModel),
        typeof(KaraokeLine), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, OnRowChanged));
    public static readonly DependencyProperty HighlightBrushProperty = DependencyProperty.Register(nameof(HighlightBrush), typeof(Brush),
        typeof(KaraokeLine), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender, OnLayoutChanged));
    public static readonly DependencyProperty DimBrushProperty = DependencyProperty.Register(nameof(DimBrush), typeof(Brush),
        typeof(KaraokeLine), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender, OnLayoutChanged));

    public LyricLineViewModel? Row { get => (LyricLineViewModel?)GetValue(RowProperty); set => SetValue(RowProperty, value); }
    public Brush HighlightBrush { get => (Brush)GetValue(HighlightBrushProperty); set => SetValue(HighlightBrushProperty, value); }
    public Brush DimBrush { get => (Brush)GetValue(DimBrushProperty); set => SetValue(DimBrushProperty, value); }

    private FormattedText? _dimText;
    private FormattedText? _brightText;
    private double _layoutWidth = double.NaN;
    private LyricLineViewModel? _subscribedRow;
    private SegmentRegion[] _regions = [];

    private sealed record SegmentRegion(Rect[] Rectangles, bool RightToLeft);

    public KaraokeLine()
    {
        Focusable = false;
        IsHitTestVisible = false; // The parent button owns seeking and its accessible text.
        Loaded += (_, _) => AttachRow();
        Unloaded += (_, _) => DetachRow();
    }

    private static void OnRowChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var control = (KaraokeLine)sender;
        control.ClearLayout();
        if (control.IsLoaded) control.AttachRow();
    }

    private static void OnLayoutChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) => ((KaraokeLine)sender).ClearLayout();

    private void AttachRow()
    {
        DetachRow();
        _subscribedRow = Row;
        if (_subscribedRow is not null) _subscribedRow.PropertyChanged += RowChanged;
        InvalidateVisual();
    }

    private void DetachRow()
    {
        if (_subscribedRow is not null) _subscribedRow.PropertyChanged -= RowChanged;
        _subscribedRow = null;
    }

    private void RowChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FontFamilyProperty || e.Property == FontSizeProperty || e.Property == FontStyleProperty ||
            e.Property == FontWeightProperty || e.Property == FontStretchProperty || e.Property == FlowDirectionProperty ||
            e.Property == LanguageProperty || e.Property == ForegroundProperty)
            ClearLayout();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ClearLayout();
    }

    private void ClearLayout()
    {
        _dimText = _brightText = null;
        _regions = [];
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size constraint)
    {
        EnsureLayout(constraint.Width);
        return _dimText is null ? new Size() : new Size(Math.Min(_layoutWidth, _dimText.WidthIncludingTrailingWhitespace), _dimText.Height);
    }

    private void EnsureLayout(double width)
    {
        // FormattedText requires a finite width; unbounded hosts can still measure naturally.
        width = double.IsFinite(width) ? Math.Max(1, width) : 100000;
        if (_dimText is not null && Math.Abs(width - _layoutWidth) < 0.1) return;
        _layoutWidth = width;
        if (Row is not { } row) return;
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var culture = Language.GetSpecificCulture();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Format(Brush brush) => new(row.Text, culture, FlowDirection, typeface, FontSize, brush, dpi)
        {
            MaxTextWidth = width, Trimming = TextTrimming.None
        };
        _dimText = Format(row.Line.Segments.Count > 0 ? DimBrush : Foreground);
        _brightText = Format(HighlightBrush);
        _regions = new SegmentRegion[row.Line.Segments.Count];
        var textIndex = 0;
        for (var i = 0; i < _regions.Length; i++)
        {
            var segment = row.Line.Segments[i];
            var count = Math.Min(segment.Text.Length, row.Text.Length - textIndex);
            var rightToLeft = IsRightToLeft(segment.Text);
            // WPF can merge a wrapped range into one connected polygon. Query grapheme
            // ranges, then merge adjacent bounds on the SAME visual row, never across rows.
            var rectangles = new List<Rect>();
            var elements = StringInfo.ParseCombiningCharacters(segment.Text[..count]);
            for (var element = 0; element < elements.Length; element++)
            {
                var start = elements[element];
                var length = (element + 1 < elements.Length ? elements[element + 1] : count) - start;
                var bounds = _brightText.BuildHighlightGeometry(new Point(), textIndex + start, length)?.Bounds;
                if (bounds is { IsEmpty: false, Width: > 0 } rect) rectangles.Add(rect);
            }
            var merged = new List<Rect>();
            foreach (var rect in rectangles.OrderBy(rect => rect.Top).ThenBy(rect => rect.Left))
            {
                if (merged.Count > 0 && Math.Abs(merged[^1].Top - rect.Top) < 0.1 &&
                    Math.Abs(merged[^1].Bottom - rect.Bottom) < 0.1 && rect.Left <= merged[^1].Right + 0.1)
                    merged[^1] = Rect.Union(merged[^1], rect);
                else merged.Add(rect);
            }
            _regions[i] = new SegmentRegion(merged.OrderBy(rect => rect.Top)
                .ThenBy(rect => rightToLeft ? -rect.Right : rect.Left).ToArray(), rightToLeft);
            textIndex += count;
        }
    }

    private bool IsRightToLeft(string text)
    {
        foreach (var character in text)
        {
            if (!char.IsLetter(character)) continue;
            return character is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF';
        }
        return FlowDirection == FlowDirection.RightToLeft;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        EnsureLayout(ActualWidth);
        if (_dimText is null || Row is not { } row) return;
        // FrameworkElement mirrors RTL visuals; FormattedText already shapes and orders
        // RTL text. Cancel that outer mirror so glyphs and their sweep aren't flipped twice.
        if (FlowDirection == FlowDirection.RightToLeft)
            drawingContext.PushTransform(new ScaleTransform(-1, 1, ActualWidth / 2, 0));
        DrawLine(drawingContext, row);
        if (FlowDirection == FlowDirection.RightToLeft) drawingContext.Pop();
    }

    private void DrawLine(DrawingContext drawingContext, LyricLineViewModel row)
    {
        drawingContext.DrawText(_dimText, new Point());
        if (!row.IsActive || _regions.Length == 0 || _brightText is null) return;

        var clip = new GeometryGroup { FillRule = FillRule.Nonzero };
        for (var i = 0; i < _regions.Length; i++)
        {
            var region = _regions[i];
            var remaining = region.Rectangles.Sum(rect => rect.Width) * row.GetSegmentProgress(i);
            foreach (var rect in region.Rectangles)
            {
                var width = Math.Min(rect.Width, remaining);
                if (width <= 0) break;
                clip.Children.Add(new RectangleGeometry(new Rect(region.RightToLeft ? rect.Right - width : rect.Left,
                    rect.Top, width, rect.Height)));
                remaining -= width;
            }
        }
        if (clip.Children.Count == 0) return;
        clip.Freeze();
        drawingContext.PushClip(clip);
        drawingContext.DrawText(_brightText, new Point());
        drawingContext.Pop();
    }
}
