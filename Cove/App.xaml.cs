using System.IO;
using System.Windows;
using System.Windows.Media;

namespace Cove;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            Log("startup");
            Theme.Apply(Store.Current.State.Accent);
            Resources["AccentBrush"] = new SolidColorBrush(Theme.Accent.Color);
            DispatcherUnhandledException += (_, args) =>
            {
                Log(args.Exception.ToString());
                try
                {
                    File.AppendAllText(Path.Combine(Store.Current.Root, "error.log"), $"\n{DateTimeOffset.Now:u}\n{args.Exception}\n");
                }
                catch
                {
                    // Logging must not crash the crash path.
                }
                MessageBox.Show(args.Exception.Message, "Cove");
                args.Handled = true;
            };
            base.OnStartup(e);
            var window = new MainWindow();
            Log("constructed");
            window.Show();
            Log("shown");
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            MessageBox.Show(ex.Message, "Cove");
            Shutdown(1);
        }
    }

    internal static void Log(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "cove-boot.log"), $"{DateTimeOffset.Now:HH:mm:ss} {message}\n");
        }
        catch
        {
            // Boot logging is optional.
        }
    }
}
