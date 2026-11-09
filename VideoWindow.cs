using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;

namespace Cove;

sealed class VideoWindow : Window
{
    readonly WebHostView _host = new();

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
        Content = _host;
        Opened += (_, _) =>
        {
            try
            {
                var page = WebEngine.OpenPage(isPrivate: false, store: IntPtr.Zero);
                _host.Adopt(page);
                _host.Activate(page);
                page.LoadHtml(Html(src, seconds));
            }
            catch (Exception ex)
            {
                App.Log(ex.ToString());
                Content = new TextBlock
                {
                    Text = "This video cannot play in a separate window.",
                    Foreground = Brushes.White,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(24)
                };
            }
        };
        Closing += (_, _) => _host.Shutdown();
    }

    static string Html(string src, double seconds)
    {
        var safe = src.Replace("&", "&amp;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal);
        var time = seconds.ToString("0.###", CultureInfo.InvariantCulture);
        return $$"""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8"></head>
            <body style="margin:0;background:#000;height:100vh">
            <video id="v" src="{{safe}}" controls autoplay style="width:100%;height:100%;object-fit:contain;background:#000"></video>
            <script>const v=document.getElementById('v'); v.addEventListener('loadedmetadata', () => { try { v.currentTime = {{time}}; } catch (e) {} });</script>
            </body></html>
            """;
    }
}
