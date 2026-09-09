using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MusicPlayer.Controls;

/// <summary>Renders the vendored Lucide geometry on its original 24 × 24 grid.</summary>
public sealed class LucideIcon : Control
{
    public static readonly DependencyProperty GeometryProperty = DependencyProperty.Register(
        nameof(Geometry), typeof(Geometry), typeof(LucideIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Geometry? Geometry
    {
        get => (Geometry?)GetValue(GeometryProperty);
        set => SetValue(GeometryProperty, value);
    }

    static LucideIcon()
    {
        WidthProperty.OverrideMetadata(typeof(LucideIcon), new FrameworkPropertyMetadata(18d));
        HeightProperty.OverrideMetadata(typeof(LucideIcon), new FrameworkPropertyMetadata(18d));
        FocusableProperty.OverrideMetadata(typeof(LucideIcon), new FrameworkPropertyMetadata(false));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(LucideIcon), new FrameworkPropertyMetadata(false));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (Geometry is null) return;
        var pen = new Pen(Foreground, 2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        drawingContext.PushTransform(new ScaleTransform(ActualWidth / 24, ActualHeight / 24));
        drawingContext.DrawGeometry(null, pen, Geometry);
        drawingContext.Pop();
    }
}