using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Cove;

sealed class VideoWindow : Window
{
    readonly MediaElement _media;
    bool _paused;

    public VideoWindow(string src, double seconds)
    {
        Title = "Cove";
        Width = 560;
        Height = 340;
        MinWidth = 320;
        MinHeight = 180;
        Topmost = true;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Close,
            Stretch = Stretch.Uniform,
            Source = new Uri(src)
        };
        Content = _media;
        _media.MediaOpened += (_, _) =>
        {
            if (seconds > 1) _media.Position = TimeSpan.FromSeconds(seconds);
            _media.Play();
        };
        _media.MediaFailed += (_, _) =>
        {
            Content = new TextBlock
            {
                Text = "This video cannot play in a separate window.",
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(24)
            };
        };
        _media.MouseLeftButtonDown += (_, _) =>
        {
            if (_paused) { _media.Play(); _paused = false; }
            else { _media.Pause(); _paused = true; }
        };
    }
}
