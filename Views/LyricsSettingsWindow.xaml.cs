using System.Windows;
using System.Windows.Input;
using MusicPlayer.ViewModels;

namespace MusicPlayer.Views;

public partial class LyricsSettingsWindow : Window
{
    public LyricsSettingsWindow(LyricsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Close();
        e.Handled = true;
    }
}
