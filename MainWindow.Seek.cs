using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace MusicPlayer;

public partial class MainWindow
{
    private void SeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!SeekSlider.IsEnabled || SeekSlider.Maximum <= SeekSlider.Minimum) return;

        SeekSlider.Focus();
        if (!SeekSlider.CaptureMouse()) return;

        // Capture the entire bar so a click can continue into a drag, even when
        // the pointer starts outside the thumb or moves beyond the slider.
        SeekToPointer(e);
        e.Handled = true;
    }

    private void SeekSlider_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!SeekSlider.IsMouseCaptured) return;
        if (e.LeftButton == MouseButtonState.Pressed)
            SeekToPointer(e);
        else
            SeekSlider.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void SeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!SeekSlider.IsMouseCaptured) return;
        SeekToPointer(e);
        SeekSlider.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void SeekToPointer(MouseEventArgs e)
    {
        if (SeekSlider.Template.FindName("PART_Track", SeekSlider) is not Track track) return;
        var value = track.ValueFromPoint(e.GetPosition(track));
        if (!double.IsFinite(value)) return;

        // Preserve the two-way binding that forwards each seek to the player.
        SeekSlider.SetCurrentValue(Slider.ValueProperty,
            Math.Clamp(value, SeekSlider.Minimum, SeekSlider.Maximum));
    }
}
