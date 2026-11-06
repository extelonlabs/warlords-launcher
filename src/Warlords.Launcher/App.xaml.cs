using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace Warlords.Launcher;

public partial class App : Application
{
    private Mutex? _instance;
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarlordsLauncherV2");
    public static string LogPath => Path.Combine(DataDirectory, "launcher.log");
    public const string AfterUpdateArgument = "--after-update";
    // Environment.ProcessPath: Assembly.Location is empty in a single-file exe.
    public static string LauncherPath => Environment.ProcessPath ?? throw new InvalidOperationException("The launcher path is unknown.");
    public static string NewLauncherPath => Path.Combine(Path.GetDirectoryName(LauncherPath)!, "Warlords.new.exe");
    public static string OldLauncherPath => Path.Combine(Path.GetDirectoryName(LauncherPath)!, "Warlords.old.exe");

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
        var args = e.Args.Where(arg => arg != AfterUpdateArgument).ToArray();
        _instance = new Mutex(true, @"Local\WarlordsClosedTestLauncherV2", out var created);
        // A self-update starts the new launcher while the old one is still closing.
        if (!created && args.Length != e.Args.Length)
        {
            try { created = _instance.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { created = true; }
        }
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
        try { File.Delete(OldLauncherPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        try
        {
            string? channel = null;
            if (args.Length == 2 && args[0] == "--channel") channel = args[1];
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

    /// <summary>Lets the updated launcher take the single-instance lock before this one closes.</summary>
    public void ReleaseSingleInstance()
    {
        _instance?.ReleaseMutex();
        _instance?.Dispose();
        _instance = null;
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
