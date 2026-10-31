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
    private bool _managedInstallation;
    private bool _hadError;
    private bool _wasCancelled;
    private bool _steamOpened;
    private string _busyTitle = "Checking your setup";

    private enum PrimaryAction { ChooseFolder, CheckAgain, GetArmory, Install, Play }

    public MainWindow(string? channelOverride)
    {
        InitializeComponent();
        LauncherVersionText.Text = "Launcher " + Profile.LauncherVersion;
        var workArea = SystemParameters.WorkArea;
        var availableWidth = Math.Max(1, workArea.Width - 24);
        var availableHeight = Math.Max(1, workArea.Height - 24);
        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);
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
        ReleaseText.Text = "Checking the latest version…";
        SourceText.Text = "";
        ChannelSourceText.Text = "";
        ExpiryText.Text = "";
        SetStatus("Checking for the latest Warlords update…", "Checking for updates");
        var loaded = await _channels.LoadAsync(location, token);
        _source = loaded;
        _channelLocation = loaded.Location;
        var release = loaded.Release;
        ReleaseText.Text = $"Latest version: {release.Version}";
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
        SetStatus("Latest version found. Checking your installation…", "Checking your files");
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
            SetStatus("Choose your Bannerlord folder to continue.");
            return false;
        }
        var root = GameRootBox.Text.Trim();
        if (!IsManagedInstallation(root))
        {
            SetStatus("Choose Install Warlords to set up automatic updates.");
            return false;
        }
        if (_inspection?.CanPlay != true)
        {
            SetStatus("Finish the setup steps below, then choose Check again.");
            return false;
        }
        SetStatus("Checking your installed files…", "Checking your files");
        if (await _installer.VerifyAsync(root, source.Release, Reporter(), token))
        {
            _releaseReady = true;
            Progress.Value = 100;
            SetStatus($"Warlords {source.Release.Version} is up to date.");
            return true;
        }
        SetStatus($"Updating Warlords to {source.Release.Version}…", "Updating Warlords");
        await InstallCurrentAsync(token);
        return _releaseReady;
    }

    private async Task InstallCurrentAsync(CancellationToken token)
    {
        _releaseReady = false;
        var source = _source ?? throw new InvalidOperationException("Refresh the update channel before installing.");
        var root = GameRootBox.Text.Trim();
        SetStatus($"Preparing Warlords {source.Release.Version}…", _managedInstallation ? "Updating Warlords" : "Installing Warlords");
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
            ? $"Warlords {source.Release.Version} is up to date."
            : "Warlords is installed. Finish the remaining setup steps before playing.");
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
            _managedInstallation = false;
            GameStatusText.Text = "Choose the folder where Steam installed Bannerlord.";
            ArmoryStatusText.Text = "Checked after Bannerlord is found.";
            return;
        }
        var result = await Task.Run(() => (Inspection: _game.Inspect(root), Managed: IsManagedInstallation(root)), token);
        _inspection = result.Inspection;
        _managedInstallation = result.Managed;
        GameStatusText.Text = _inspection.GameReady
            ? $"Bannerlord {Profile.GameVersion.TrimStart('v')} found."
            : FirstMessage(_inspection.GameMessages,
                _inspection.GameFound ? "The required game files need attention. See Settings & help for details." : FirstMessage(_inspection.Messages, "Choose a valid Bannerlord folder."));
        ArmoryStatusText.Text = _inspection.ArmoryReady
            ? $"Armory {Profile.ArmoryVersion.TrimStart('v')} is installed."
            : _inspection.GameFound
                ? FirstMessage(_inspection.ArmoryMessages, "Armory could not be checked. Finish the Bannerlord step first.")
                : "Checked after Bannerlord is found.";
        if (!_inspection.CanPlay)
            Append(string.Join(Environment.NewLine, _inspection.Messages));
        SavePreferences();
        UpdateButtons();
    }

    private IProgress<OperationProgress> Reporter()
    {
        var operation = _operation;
        return new Progress<OperationProgress>(value =>
        {
            if (!_busy || !ReferenceEquals(_operation, operation) || operation?.IsCancellationRequested == true) return;
            OperationText.Text = value.Message;
            Progress.IsIndeterminate = value.Fraction is null;
            if (value.Fraction is { } fraction)
            {
                Progress.Value = Math.Clamp(fraction, 0, 1) * 100;
                ProgressPercentText.Text = $"{Progress.Value:0}%";
            }
            else ProgressPercentText.Text = "Working…";
        });
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_busy) return;
        _busy = true;
        _hadError = false;
        _wasCancelled = false;
        _steamOpened = false;
        _busyTitle = "Checking your setup";
        _operation = new CancellationTokenSource();
        OperationText.Foreground = (Brush)FindResource("Text");
        OperationText.Text = "Checking your setup…";
        Progress.Value = 0;
        Progress.IsIndeterminate = true;
        ProgressPercentText.Text = "Working…";
        UpdateButtons();
        try { await action(_operation.Token); }
        catch (OperationCanceledException)
        {
            _releaseReady = false;
            _wasCancelled = _operation.IsCancellationRequested;
            _hadError = !_wasCancelled;
            if (_source is null) ReleaseText.Text = _wasCancelled ? "Update check cancelled." : "Latest version unavailable.";
            SetStatus(_operation.IsCancellationRequested
                ? "Cancelled. Any interrupted installation will be recovered before the next update."
                : "The update request timed out. Check your connection, then choose Try again.");
        }
        catch (Exception ex)
        {
            _releaseReady = false;
            _hadError = true;
            if (_source is null) ReleaseText.Text = "Latest version unavailable.";
            OperationText.Foreground = new SolidColorBrush(Color.FromRgb(255, 181, 166));
            SetStatus(ex is System.Net.Http.HttpRequestException
                ? "Could not check for updates. Check your internet connection, then choose Try again."
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

    private void SetStatus(string message, string? busyTitle = null)
    {
        if (busyTitle is not null) _busyTitle = busyTitle;
        OperationText.Text = message;
        Append(message);
        UpdateButtons();
    }

    private void Append(string message)
    {
        ActivityBox.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
        ActivityBox.ScrollToEnd();
        App.Log(message);
    }

    private void UpdateButtons()
    {
        var inspection = _inspection;
        var hasGame = inspection?.GameFound == true;
        var hasRelease = _source is not null;
        InstallButton.IsEnabled = !_busy && hasRelease && inspection?.CanInstall == true;
        VerifyButton.IsEnabled = !_busy && hasGame && hasRelease;
        PrimaryButton.IsEnabled = !_busy;
        CancelButton.IsEnabled = _busy && _operation?.IsCancellationRequested != true;
        CancelButton.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        Progress.Visibility = ProgressPercentText.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        DetectButton.IsEnabled = BrowseButton.IsEnabled = ChoosePackButton.IsEnabled = RefreshButton.IsEnabled = BranchButton.IsEnabled = ConnectButton.IsEnabled = !_busy;
        ArmoryButton.IsEnabled = !_busy;
        ArmoryButton.Visibility = hasGame && inspection?.ArmoryReady != true ? Visibility.Visible : Visibility.Collapsed;
        GameRootBox.IsEnabled = ChannelUrlBox.IsEnabled = !_busy;
        BrowseButton.Content = hasGame ? "_Change…" : "Choose _folder";
        RefreshButton.Content = _hadError ? "Try _again" : "Check _again";

        InstallStateText.Text = inspection is { WarlordsNeedsManualAttention: true }
            ? FirstMessage(inspection.WarlordsMessages, "Keep one Warlords copy for this installation.")
            : _releaseReady && inspection?.CanPlay == true ? "Installed and up to date."
            : inspection?.InstalledVersion is { } installed ? $"Installed version: {installed.TrimStart('v')}."
            : "Not installed yet. The launcher will keep it updated after setup.";

        var next = NextAction();
        PrimaryButton.Content = _busy ? "Working…" : next switch
        {
            PrimaryAction.ChooseFolder => "Choose game _folder",
            PrimaryAction.GetArmory => "Get _Armory",
            PrimaryAction.Install => _managedInstallation ? "_Repair Warlords" : "_Install Warlords",
            PrimaryAction.Play => "_Play Warlords",
            _ => _hadError || _wasCancelled ? "_Try again" : "_Check again"
        };

        if (_busy && _operation?.IsCancellationRequested == true)
        {
            StatusTitleText.Text = _closeAfterOperation ? "Finishing before closing" : "Cancelling safely";
            StatusHintText.Text = "The current file operation will finish before Warlords stops.";
        }
        else if (_busy)
        {
            StatusTitleText.Text = _busyTitle;
            StatusHintText.Text = "Keep Warlords open while this finishes. You can cancel safely.";
        }
        else if (_hadError)
        {
            StatusTitleText.Text = "Something needs attention";
            StatusHintText.Text = next == PrimaryAction.ChooseFolder
                ? "Choose your Bannerlord folder to continue. Details are available in Settings & help."
                : "Follow the message below, then try again. Details are available in Settings & help.";
        }
        else if (_wasCancelled)
        {
            StatusTitleText.Text = next == PrimaryAction.ChooseFolder ? "Setup paused" : "Update cancelled";
            StatusHintText.Text = next == PrimaryAction.ChooseFolder
                ? "Choose your Bannerlord folder when you’re ready to continue."
                : "Choose Try again when you’re ready. Interrupted updates are recovered safely.";
        }
        else if (_steamOpened)
        {
            StatusTitleText.Text = "Continue in Steam";
            StatusHintText.Text = "In Bannerlord’s launcher, select Multiplayer and enable Native, Multiplayer, Armory and Warlords.";
        }
        else if (inspection is not { GameFound: true })
        {
            StatusTitleText.Text = "Find your Bannerlord game";
            StatusHintText.Text = "Choose the folder where Steam installed Mount & Blade II Bannerlord.";
        }
        else if (!inspection.GameReady)
        {
            StatusTitleText.Text = "Check your Bannerlord setup";
            StatusHintText.Text = $"Bannerlord {Profile.GameVersion.TrimStart('v')} and its Multiplayer files are required. After fixing the issue below, choose Check again.";
        }
        else if (inspection.ArmoryReady != true)
        {
            StatusTitleText.Text = "Install Armory in Steam";
            StatusHintText.Text = "Subscribe to Armory, let Steam finish downloading, then choose Check again.";
        }
        else if (!hasRelease)
        {
            StatusTitleText.Text = "Check for the latest version";
            StatusHintText.Text = "Connect to the internet and choose Check again to load the Warlords update.";
        }
        else if (inspection.WarlordsNeedsManualAttention)
        {
            StatusTitleText.Text = "Keep one Warlords installation";
            StatusHintText.Text = "Resolve the Warlords message below, then choose Check again. Workshop files are never removed automatically.";
        }
        else if (_releaseReady && inspection.CanPlay)
        {
            StatusTitleText.Text = "Ready to play";
            StatusHintText.Text = "You’re up to date. Play opens Steam; future Warlords updates are automatic.";
        }
        else
        {
            StatusTitleText.Text = _managedInstallation ? "Repair your Warlords files" : "Install Warlords once";
            StatusHintText.Text = _managedInstallation
                ? "The launcher will replace changed files and keep a recovery backup."
                : "The launcher downloads the correct version and keeps it updated for you.";
        }
    }

    private PrimaryAction NextAction()
    {
        var inspection = _inspection;
        if (inspection is not { GameFound: true }) return PrimaryAction.ChooseFolder;
        if (_hadError || _wasCancelled || !inspection.GameReady) return PrimaryAction.CheckAgain;
        if (!inspection.ArmoryReady) return PrimaryAction.GetArmory;
        if (_source is null || inspection.WarlordsNeedsManualAttention) return PrimaryAction.CheckAgain;
        return _releaseReady && inspection.CanPlay ? PrimaryAction.Play : PrimaryAction.Install;
    }

    private static string FirstMessage(System.Collections.Generic.IReadOnlyList<string> messages, string fallback)
        => messages.Count > 0 ? messages[0] : fallback;

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        switch (NextAction())
        {
            case PrimaryAction.ChooseFolder: await ChooseGameFolderAsync(); break;
            case PrimaryAction.GetArmory: OpenArmoryPage(); break;
            case PrimaryAction.Install: await RunAsync(InstallWithConsentAsync); break;
            case PrimaryAction.Play: await RunAsync(PlayCurrentAsync); break;
            default: await RunAsync(RefreshAndSynchronizeAsync); break;
        }
    }

    private async void Detect_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        GameRootBox.Text = await Task.Run(_game.Discover, token) ?? "";
        await RefreshAndSynchronizeAsync(token);
    });

    private async void Browse_Click(object sender, RoutedEventArgs e) => await ChooseGameFolderAsync();

    private async Task ChooseGameFolderAsync()
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

    private async void Install_Click(object sender, RoutedEventArgs e) => await RunAsync(InstallWithConsentAsync);

    private async Task InstallWithConsentAsync(CancellationToken token)
    {
        await LoadChannelAsync(_channelLocation, token);
        await InspectGameAsync(token);
        if (_inspection?.CanInstall != true)
        {
            SetStatus("Finish the Bannerlord and Armory setup steps, then choose Check again.");
            return;
        }
        var root = GameRootBox.Text.Trim();
        var target = Path.Combine(root, "Modules", "StratLords");
        if (Directory.Exists(target) && !IsManagedInstallation(root))
        {
            if (MessageBox.Show(this,
                "Let Warlords manage this existing installation and download the latest version?\n\nThe launcher keeps a recovery backup and preserves your player options. Future updates are automatic.\n\n" + target,
                "Set up Warlords", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                SetStatus("Nothing was changed. You can choose Install Warlords when you’re ready.");
                return;
            }
        }
        await InstallCurrentAsync(token);
    }

    private async void Verify_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        await LoadChannelAsync(_channelLocation, token);
        if (_source is null) return;
        var valid = await _installer.VerifyAsync(GameRootBox.Text.Trim(), _source.Release, Reporter(), token);
        await InspectGameAsync(token);
        _releaseReady = valid && _inspection?.CanPlay == true && IsManagedInstallation(GameRootBox.Text.Trim());
        SetStatus(_releaseReady ? "Your Warlords files are up to date."
            : valid ? "Files are up to date. Choose Install Warlords once to enable automatic updates."
            : "Some Warlords files need updating. Choose Repair Warlords to replace them.");
    });

    private async Task PlayCurrentAsync(CancellationToken token)
    {
        await LoadChannelAsync(_channelLocation, token);
        if (!await SynchronizeCurrentAsync(token) || _source is null) return;
        var root = GameRootBox.Text.Trim();
        var valid = await _installer.VerifyAndRunAsync(root, _source.Release, Reporter(), token, () => _game.PlayUnderLock(root));
        _releaseReady = valid;
        _steamOpened = valid;
        SetStatus(valid
            ? "Steam opened. In Bannerlord's launcher, select Multiplayer and the four required modules, then Play."
            : "Some Warlords files need updating. Choose Repair Warlords before playing.");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _operation?.Cancel();
        SetStatus("Cancelling safely…");
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true;
        _closeAfterOperation = true;
        _operation?.Cancel();
        SetStatus("Finishing the current file operation before closing…");
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

    private void Armory_Click(object sender, RoutedEventArgs e) => OpenArmoryPage();

    private void OpenArmoryPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://steamcommunity.com/sharedfiles/filedetails/?id=3712562953") { UseShellExecute = true });
            SetStatus("Armory’s Steam Workshop page is open. Subscribe, wait for Steam to finish, then choose Check again.");
        }
        catch (Exception ex)
        {
            _hadError = true;
            SetStatus("Could not open the Armory page: " + ex.Message);
        }
    }
}
