using System.IO;
using Microsoft.Web.WebView2.Core;

namespace Cove;

static class BrowserHost
{
    static Task<CoreWebView2Environment>? _environment;

    public static Task<CoreWebView2Environment> GetAsync() => _environment ??= CreateAsync();

    public static string PagesDirectory => Path.Combine(AppContext.BaseDirectory, "pages");

    static async Task<CoreWebView2Environment> CreateAsync()
    {
        var folder = Path.Combine(Store.Current.Root, "WebView2");
        Directory.CreateDirectory(folder);
        var options = new CoreWebView2EnvironmentOptions
        {
            AreBrowserExtensionsEnabled = true,
            EnableTrackingPrevention = true
        };
        return await CoreWebView2Environment.CreateAsync(null, folder, options);
    }
}
