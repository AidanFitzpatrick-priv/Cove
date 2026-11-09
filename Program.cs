using Avalonia;
using Velopack;
using Velopack.Sources;

namespace Cove;

sealed class Program
{
    const string Repo = "https://github.com/AidanFitzpatrick-priv/Cove";

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();
        try
        {
            ApplyUpdateAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            App.Log("update " + ex.Message);
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Installed copies look at the latest GitHub release. A newer one is downloaded and the app restarts into it.
    /// A folder you build yourself is not an install, so the check is skipped.
    /// </summary>
    static async Task ApplyUpdateAsync()
    {
        var manager = new UpdateManager(new GithubSource(Repo, null, false));
        if (!manager.IsInstalled) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var update = await manager.CheckForUpdatesAsync().WaitAsync(timeout.Token);
        if (update is null) return;
        App.Log("update " + update.TargetFullRelease.Version);
        await manager.DownloadUpdatesAsync(update);
        manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
