using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Windows;

namespace Warlords.Launcher;

public partial class App : Application
{
    private Mutex? _instance;

    // Runs before Main, so the embedded libraries can load from the first use.
    static App()
    {
        Net48.LoadEmbeddedLibraries();
        // .NET Framework otherwise allows only two connections per host.
        ServicePointManager.DefaultConnectionLimit = 8;
    }

    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarlordsLauncherV2");
    public static string LogPath => Path.Combine(DataDirectory, "launcher.log");
    public const string AfterUpdateArgument = "--after-update";
    // The location is read when the exe loads, so it stays right after self-update renames the file.
    public static string LauncherPath => Assembly.GetEntryAssembly()?.Location is { Length: > 0 } location ? location : throw new InvalidOperationException("The launcher path is unknown.");
    public static string NewLauncherPath => Path.Combine(Path.GetDirectoryName(LauncherPath)!, "Warlords.new.exe");
    public static string OldLauncherPath => Path.Combine(Path.GetDirectoryName(LauncherPath)!, "Warlords.old.exe");

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 2 * 1024 * 1024)
                SafeFiles.MoveReplacing(LogPath, LogPath + ".previous");
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Before anything needs the embedded libraries, which older .NET Framework cannot run.
        if (!Net48.IsNewEnough())
        {
            Log("Older .NET Framework found; asking the player to install 4.8.");
            if (MessageBox.Show("Warlords needs Microsoft .NET Framework 4.8, a free part of Windows that this PC does not have yet.\n\nChoose OK to open Microsoft's download page. Install it, restart if asked, then open Warlords again.",
                "Warlords", MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Profile.Net48DownloadUrl) { UseShellExecute = true }); }
                catch (Exception ex) { Log("Could not open the .NET Framework page: " + ex.Message); }
            }
            Shutdown();
            return;
        }
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
