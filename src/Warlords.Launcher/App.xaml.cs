using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace Warlords.Launcher;

public partial class App : Application
{
    private Mutex? _instance;
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarlordsLauncherV2");
    public static string LogPath => Path.Combine(DataDirectory, "launcher.log");

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 2 * 1024 * 1024)
                File.Move(LogPath, LogPath + ".previous", true);
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new Mutex(true, @"Local\WarlordsClosedTestLauncherV2", out var created);
        if (!created)
        {
            MessageBox.Show("Warlords is already open.", "Warlords", MessageBoxButton.OK, MessageBoxImage.Information);
            _instance.Dispose();
            _instance = null;
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception.ToString());
            MessageBox.Show("Warlords could not continue. " + args.Exception.Message + "\n\nDetails: " + LogPath,
                "Warlords", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            Shutdown(1);
        };
        try
        {
            string? channel = null;
            if (e.Args.Length == 2 && e.Args[0] == "--channel") channel = e.Args[1];
            MainWindow = new MainWindow(channel);
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            MessageBox.Show(ex.Message + "\n\nDetails: " + LogPath, "Warlords could not open", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_instance != null)
        {
            _instance.ReleaseMutex();
            _instance.Dispose();
        }
        base.OnExit(e);
    }
}
