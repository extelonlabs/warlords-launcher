using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Warlords.Launcher;

public partial class MainWindow : Window
{
    private readonly GameService _game = new();
    private readonly InstallService _installer = new();
    private readonly ChannelService _channels = new();
    private readonly string _preferencesPath = Path.Combine(App.DataDirectory, "settings.json");
    private ChannelSource? _source;
    private GameInspection? _inspection;
    private string _channelLocation;
    private CancellationTokenSource? _operation;
    private bool _busy;
    private bool _closeAfterOperation;
    private bool _releaseReady;
    private bool _localChannelMode;

    public MainWindow(string? channelOverride)
    {
        InitializeComponent();
        var preferences = ReadPreferences();
        GameRootBox.Text = preferences.GameRoot ?? "";
        var preferredChannel = preferences.Channel ?? "";
        var savedOnlineChannel = preferredChannel.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        _channelLocation = channelOverride ?? (savedOnlineChannel ? preferredChannel : Profile.DefaultChannelUrl);
        _localChannelMode = channelOverride is not null && !channelOverride.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        ChoosePackButton.Visibility = _localChannelMode ? Visibility.Visible : Visibility.Collapsed;
        if (Uri.TryCreate(_channelLocation, UriKind.Absolute, out var uri) && uri.Scheme == "https")
            ChannelUrlBox.Text = _channelLocation;
        Loaded += async (_, _) => await RunAsync(InitializeAsync);
        Closing += OnClosing;
        UpdateButtons();
    }

    private async Task InitializeAsync(CancellationToken token)
    {
        Append("Warlords " + Profile.LauncherVersion + ". Automatic updates: StratLords.");
        if (string.IsNullOrWhiteSpace(GameRootBox.Text))
            GameRootBox.Text = await Task.Run(_game.Discover, token) ?? "";
        await InspectGameAsync(token);
        if (string.IsNullOrWhiteSpace(_channelLocation))
        {
            ReleaseText.Text = "No automatic update channel is configured.";
            SourceText.Text = "Enter the owner's published HTTPS channel below.";
            SetStatus("Connect an update channel to get started.");
            return;
        }
        await RefreshAndSynchronizeAsync(token);
    }

    private LauncherPreferences ReadPreferences()
    {
        try
        {
            return File.Exists(_preferencesPath)
                ? JsonSerializer.Deserialize<LauncherPreferences>(File.ReadAllText(_preferencesPath), Profile.Json) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            App.Log("Preferences could not be read: " + ex.Message);
            return new();
        }
    }

    private void SavePreferences()
    {
        try
        {
            Directory.CreateDirectory(App.DataDirectory);
            // A temporary signed/channel URL can contain credentials in its query.
            // Use it for this session only; do not persist that query to preferences.
            var savedChannel = _channelLocation;
            if (Uri.TryCreate(savedChannel, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Query.Length != 0)
                savedChannel = "";
            File.WriteAllText(_preferencesPath,
                JsonSerializer.Serialize(new LauncherPreferences(GameRootBox.Text.Trim(), savedChannel), Profile.Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Append("Folder preferences could not be saved: " + ex.Message);
        }
    }

    private async Task LoadChannelAsync(string location, CancellationToken token)
    {
        _source = null;
        _releaseReady = false;
        _channelLocation = location;
        ReleaseText.Text = "Checking the latest StratLords build…";
        SourceText.Text = "";
        ChannelSourceText.Text = "";
        ExpiryText.Text = "";
        var loaded = await _channels.LoadAsync(location, token);
        _source = loaded;
        _channelLocation = loaded.Location;
        var release = loaded.Release;
        ReleaseText.Text = $"StratLords {release.Version} · {release.Files.Count:N0} files";
        SourceText.Text = $"Built from {Profile.Branch} / {release.SourceCommit[..12]}";
        SourceText.ToolTip = release.SourceCommit;
        if (Uri.TryCreate(location, UriKind.Absolute, out var channelUri) && channelUri.Scheme == "https")
        {
            ChannelSourceText.Text = "Channel: " + channelUri.Host + channelUri.AbsolutePath;
            ChannelSourceText.ToolTip = channelUri.GetLeftPart(UriPartial.Path);
        }
        else
        {
            ChannelSourceText.Text = "Test pack: " + Path.GetFileName(Path.GetDirectoryName(location));
            ChannelSourceText.ToolTip = location;
        }
        ExpiryText.Text = release.ExpiresUtc is { } expiry
            ? $"Server connection proof expires {expiry.ToLocalTime():d MMM yyyy, HH:mm zzz}. Request a fresh pack after that time."
            : "Use the test server selected by the host.";
        BranchStatusText.Text = location.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? "Automatic updates are checked whenever Warlords opens and before Play."
            : "Local test mode. Online updates are disabled for this explicitly selected pack.";
        SavePreferences();
        Append($"Loaded module {release.Version}, source {release.SourceCommit[..12]}, {release.Files.Count} verified inventory entries.");
        SetStatus("Latest build found. Checking your installation…");
    }

    private async Task RefreshAndSynchronizeAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(_channelLocation))
            throw new InvalidOperationException("Connect the owner's HTTPS update channel first.");
        await LoadChannelAsync(_channelLocation, token);
        await SynchronizeCurrentAsync(token);
    }

    private async Task<bool> SynchronizeCurrentAsync(CancellationToken token)
    {
        _releaseReady = false;
        var source = _source ?? throw new InvalidOperationException("The update channel has not been loaded.");
        await InspectGameAsync(token);
        if (string.IsNullOrWhiteSpace(GameRootBox.Text))
        {
            SetStatus("Select your Bannerlord folder, then choose Install / Update once. Future updates are automatic.");
            return false;
        }
        var root = GameRootBox.Text.Trim();
        if (!IsManagedInstallation(root))
        {
            SetStatus("Choose Install / Update once to set up this installation. Future updates are automatic.");
            return false;
        }
        if (_inspection?.CanPlay != true)
        {
            SetStatus("Check the requirements above before updating or playing.");
            return false;
        }
        SetStatus("Checking installed files against the latest build…");
        if (await _installer.VerifyAsync(root, source.Release, Reporter(), token))
        {
            _releaseReady = true;
            Progress.Value = 100;
            SetStatus($"StratLords {source.Release.Version} is up to date. Ready to play.");
            return true;
        }
        SetStatus($"Updating automatically to StratLords {source.Release.Version}…");
        await InstallCurrentAsync(token);
        return _releaseReady;
    }

    private async Task InstallCurrentAsync(CancellationToken token)
    {
        _releaseReady = false;
        var source = _source ?? throw new InvalidOperationException("Refresh the update channel before installing.");
        var root = GameRootBox.Text.Trim();
        // Check the actual game descriptors before writing a new module, including
        // first installations where the managed module does not yet exist.
        var modules = SafeFiles.Modules(root);
        foreach (var id in new[] { "Native", "Multiplayer" })
            if (SafeFiles.ReadXml(Path.Combine(modules, id, "SubModule.xml")).Root?.Element("Version")?.Attribute("value")?.Value != Profile.GameVersion)
                throw new InvalidOperationException("This build requires Bannerlord " + Profile.GameVersion + ". Select the matching Steam installation.");
        var result = await _installer.InstallAsync(root, source, Reporter(), token);
        if (result.BackupPath is not null) Append("Previous module retained at " + result.BackupPath);
        await InspectGameAsync(token);
        _releaseReady = _inspection?.CanPlay == true && IsManagedInstallation(root);
        Progress.Value = 100;
        SetStatus(_releaseReady
            ? $"StratLords {source.Release.Version} is up to date. Ready to play."
            : "Latest build installed. Finish the requirements above before playing.");
    }

    private static bool IsManagedInstallation(string root)
    {
        try
        {
            var target = Path.Combine(root, "Modules", "StratLords");
            var statePath = Path.Combine(target, ".warlords-launcher-state.json");
            SafeFiles.NoLinks(statePath);
            if (!File.Exists(statePath) || new FileInfo(statePath).Length > 8192) return false;
            using var state = JsonDocument.Parse(File.ReadAllText(statePath));
            var value = state.RootElement;
            if (!value.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 1
                || !value.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String
                || !value.TryGetProperty("sourceCommit", out var commit) || commit.ValueKind != JsonValueKind.String
                || !Regex.IsMatch(commit.GetString() ?? "", "^[0-9a-f]{40}$")
                || !value.TryGetProperty("buildId", out var build) || build.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(build.GetString())
                || !value.TryGetProperty("transaction", out var transaction) || transaction.ValueKind != JsonValueKind.String
                || !Regex.IsMatch(transaction.GetString() ?? "", "^[0-9a-f]{32}$")) return false;
            var module = SafeFiles.ReadXml(Path.Combine(target, "SubModule.xml")).Root;
            return module?.Name == "Module" && module.Element("Id")?.Attribute("value")?.Value == Profile.ModuleId
                && module.Element("Version")?.Attribute("value")?.Value.TrimStart('v') == version.GetString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Xml.XmlException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task InspectGameAsync(CancellationToken token)
    {
        var root = GameRootBox.Text.Trim();
        if (root.Length == 0)
        {
            _inspection = null;
            GameStatusText.Text = "Bannerlord was not found. Browse to its installation folder.";
            return;
        }
        _inspection = await Task.Run(() => _game.Inspect(root), token);
        GameStatusText.Text = _inspection.CanPlay
            ? $"Installed Warlords {_inspection.InstalledVersion}. Game and required modules found."
            : string.Join(Environment.NewLine, _inspection.Messages);
        SavePreferences();
    }

    private IProgress<OperationProgress> Reporter() => new Progress<OperationProgress>(value =>
    {
        OperationText.Text = value.Message;
        Progress.IsIndeterminate = value.Fraction is null;
        if (value.Fraction is { } fraction) Progress.Value = Math.Clamp(fraction, 0, 1) * 100;
    });

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_busy) return;
        _busy = true;
        _operation = new CancellationTokenSource();
        OperationText.Foreground = (Brush)FindResource("Text");
        UpdateButtons();
        try { await action(_operation.Token); }
        catch (OperationCanceledException)
        {
            _releaseReady = false;
            SetStatus(_operation.IsCancellationRequested
                ? "Cancelled. Any interrupted installation will be recovered before the next update."
                : "The update request timed out. Check your connection, then choose Check for updates.");
        }
        catch (Exception ex)
        {
            _releaseReady = false;
            OperationText.Foreground = new SolidColorBrush(Color.FromRgb(255, 181, 166));
            SetStatus(ex is System.Net.Http.HttpRequestException
                ? "The update feed could not be reached. Check your connection, then choose Check for updates."
                : ex.Message);
            App.Log(ex.ToString());
        }
        finally
        {
            _operation.Dispose();
            _operation = null;
            _busy = false;
            Progress.IsIndeterminate = false;
            UpdateButtons();
            if (_closeAfterOperation) Close();
        }
    }

    private void SetStatus(string message)
    {
        OperationText.Text = message;
        Append(message);
    }

    private void Append(string message)
    {
        ActivityBox.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
        ActivityBox.ScrollToEnd();
        App.Log(message);
    }

    private void UpdateButtons()
    {
        var hasGame = !string.IsNullOrWhiteSpace(GameRootBox.Text);
        var hasRelease = _source is not null;
        InstallButton.IsEnabled = !_busy && hasGame && hasRelease;
        VerifyButton.IsEnabled = !_busy && hasGame && hasRelease;
        PlayButton.IsEnabled = !_busy && hasGame && hasRelease && _releaseReady && _inspection?.CanPlay == true;
        CancelButton.IsEnabled = _busy;
        DetectButton.IsEnabled = BrowseButton.IsEnabled = ChoosePackButton.IsEnabled = RefreshButton.IsEnabled = BranchButton.IsEnabled = ConnectButton.IsEnabled = !_busy;
        GameRootBox.IsEnabled = ChannelUrlBox.IsEnabled = !_busy;
    }

    private async void Detect_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        GameRootBox.Text = await Task.Run(_game.Discover, token) ?? "";
        await RefreshAndSynchronizeAsync(token);
    });

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select Mount & Blade II Bannerlord", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        GameRootBox.Text = dialog.FolderName;
        await RunAsync(RefreshAndSynchronizeAsync);
    }

    private async void GameRoot_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_busy && !string.Equals(_inspection?.GameRoot, GameRootBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            await RunAsync(RefreshAndSynchronizeAsync);
    }

    private async void ChoosePack_Click(object sender, RoutedEventArgs e)
    {
        if (!_localChannelMode) return;
        var dialog = new OpenFileDialog { Title = "Choose channel.json from the extracted Warlords test pack", Filter = "Warlords channel (channel.json)|channel.json|JSON files (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
            await RunAsync(async token =>
            {
                await LoadChannelAsync(dialog.FileName, token);
                await SynchronizeCurrentAsync(token);
            });
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        await RefreshAndSynchronizeAsync(token);
    });

    private async void Connect_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        if (!Uri.TryCreate(ChannelUrlBox.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new InvalidOperationException("Enter the owner's HTTPS channel.json URL.");
        _localChannelMode = false;
        ChoosePackButton.Visibility = Visibility.Collapsed;
        await LoadChannelAsync(uri.AbsoluteUri, token);
        await SynchronizeCurrentAsync(token);
    });

    private async void Branch_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        try
        {
            var head = await _channels.ReadBranchHeadAsync(token);
            BranchStatusText.Text = _source?.Release.SourceCommit == head
                ? $"This test pack matches the current StratLords source ({head[..12]})."
                : $"Source is at {head[..12]}. Ask the host for a matched build of that commit; source files cannot be installed as a playable client.";
            Append(BranchStatusText.Text);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            if (token.IsCancellationRequested) throw;
            BranchStatusText.Text = "GitHub source status is unavailable. A private repository needs owner access; the packaged build can still be used.";
            Append(BranchStatusText.Text);
        }
    });

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async token =>
        {
            await LoadChannelAsync(_channelLocation, token);
            var root = GameRootBox.Text.Trim();
            var target = Path.Combine(root, "Modules", "StratLords");
            if (Directory.Exists(target) && !IsManagedInstallation(root))
            {
                if (MessageBox.Show(this,
                    "Let Warlords manage this existing StratLords module and install the latest build?\n\nThe launcher keeps a recovery backup and preserves your player options. Future updates are automatic.\n\n" + target,
                    "Set up Warlords", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                {
                    SetStatus("Installation was not changed. Choose Install / Update when you are ready.");
                    return;
                }
            }
            await InstallCurrentAsync(token);
        });
    }

    private async void Verify_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        await LoadChannelAsync(_channelLocation, token);
        if (_source is null) return;
        var valid = await _installer.VerifyAsync(GameRootBox.Text.Trim(), _source.Release, Reporter(), token);
        await InspectGameAsync(token);
        _releaseReady = valid && _inspection?.CanPlay == true && IsManagedInstallation(GameRootBox.Text.Trim());
        SetStatus(_releaseReady ? "All managed release files match the latest build. Ready to play."
            : valid ? "Files match the latest build. Choose Install / Update once to enable automatic management."
            : "Installed files differ from the latest build. Use Install / Update to repair them.");
    });

    private async void Play_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        await LoadChannelAsync(_channelLocation, token);
        if (!await SynchronizeCurrentAsync(token) || _source is null) return;
        var root = GameRootBox.Text.Trim();
        var valid = await _installer.VerifyAndRunAsync(root, _source.Release, Reporter(), token, () => _game.PlayUnderLock(root));
        _releaseReady = valid;
        SetStatus(valid
            ? "Steam opened. In Bannerlord's launcher, select Multiplayer and the four required modules, then Play."
            : "This installation differs from the selected test build. Use Install / Update before playing.");
    });

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _operation?.Cancel();
        OperationText.Text = "Cancelling safely…";
        CancelButton.IsEnabled = false;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true;
        _closeAfterOperation = true;
        _operation?.Cancel();
        OperationText.Text = "Finishing the current file operation before closing…";
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(App.DataDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { App.DataDirectory }, UseShellExecute = true });
        }
        catch (Exception ex) { SetStatus("Could not open the logs folder: " + ex.Message); }
    }

    private void Armory_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://steamcommunity.com/sharedfiles/filedetails/?id=3712562953") { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus("Could not open the Armory page: " + ex.Message); }
    }
}
