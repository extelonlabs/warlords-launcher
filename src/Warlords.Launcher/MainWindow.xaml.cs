using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace Warlords.Launcher;

public partial class MainWindow : Window
{
    private readonly GameService _game = new();
    private readonly InstallService _installer = new();
    private readonly ChannelService _channels = new();
    private LauncherUpdate? _launcherUpdate;
    private readonly ChangelogService _changelogs = new();
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
    private bool _showVerifyResult;
    private string _busyTitle = "Checking your setup";
    private CancellationTokenSource? _changelogOperation;
    private string? _changelogLoadedKey;
    private string? _changelogCachedText;
    private bool _windowClosed;
    private Storyboard? _splash;
    private bool? _setupFoldedFor;
    private readonly Stopwatch _speedClock = new();
    private long _speedBytes;
    private double _bytesPerSecond;
    private readonly List<(string Key, string Label, DockPanel Line)> _steps = new();
    private int _activeStep = -1;
    private bool _stepsOpen;
    // Steps of each operation, in order. A step that does not apply this time is dropped.
    private static readonly (string Key, string Label)[] InstallSteps =
    {
        ("version", "Check your Bannerlord version"),
        ("closed", "Make sure Bannerlord is closed"),
        ("download", "Download Warlords"),
        ("check", "Check the download is safe"),
        ("unpack", "Unpack and check every file"),
        ("settings", "Keep your settings"),
        ("backup", "Save a backup of the old version"),
        ("swap", "Put the new version in place"),
    };
    private static readonly (string Key, string Label)[] LauncherSteps =
    {
        ("download", "Download the new launcher"),
        ("check", "Check it is the real, signed file"),
        ("swap", "Swap the old launcher for the new one"),
        ("restart", "Restart the launcher"),
    };
    private const string ChangelogLoadedStatus = "Changes for this build and recent releases. Some features may still be in testing.";

    private enum PrimaryAction { ChooseFolder, CheckAgain, GetArmory, RemoveWorkshopCopy, Install, Play }

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
        // Players always use the official feed, even when an old settings file names another one.
        _channelLocation = channelOverride ?? Profile.DefaultChannelUrl;
        _localChannelMode = channelOverride is not null && !channelOverride.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        ChoosePackButton.Visibility = _localChannelMode ? Visibility.Visible : Visibility.Collapsed;
        Loaded += async (_, _) => await RunAsync(InitializeAsync);
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _windowClosed = true;
            CancelChangelogRequest();
        };
        // Window chrome and the startup animation are visual only. The splash
        // starts after the setup check above and any click or key skips it.
        Loaded += (_, _) => StartSplash();
        PreviewMouseDown += (_, _) => FinishSplash();
        PreviewKeyDown += (_, _) => FinishSplash();
        StateChanged += (_, _) => UpdateWindowChrome();
        SetupExpander.Expanded += (_, _) => HeroBlock.MinHeight = 180;
        SetupExpander.Collapsed += (_, _) => HeroBlock.MinHeight = 320;
        UpdateButtons();
    }

    private async Task InitializeAsync(CancellationToken token)
    {
        Append("Warlords " + Profile.LauncherVersion + ". Automatic updates: " + Profile.Branch + ".");
        if (!_localChannelMode) _ = CheckLauncherUpdateAsync();
        if (string.IsNullOrWhiteSpace(GameRootBox.Text))
            GameRootBox.Text = await Task.Run(_game.Discover, token) ?? "";
        await InspectGameAsync(token);
        if (string.IsNullOrWhiteSpace(_channelLocation))
        {
            ReleaseText.Text = "No automatic update channel is configured.";
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
            File.WriteAllText(_preferencesPath,
                JsonSerializer.Serialize(new LauncherPreferences(GameRootBox.Text.Trim()), Profile.Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Append("Folder preferences could not be saved: " + ex.Message);
        }
    }

    private async Task LoadChannelAsync(string location, CancellationToken token)
    {
        CancelChangelogRequest();
        ChangelogText.Clear();
        ChangelogStatusText.Text = "Release notes will be available after the update check.";
        ChangelogRetryButton.Visibility = Visibility.Collapsed;
        _source = null;
        _releaseReady = false;
        _channelLocation = location;
        ReleaseText.Text = "Checking the latest version…";
        SetStatus("Checking for the latest Warlords update…", "Checking for updates");
        var loaded = await _channels.LoadAsync(location, token);
        _source = loaded;
        _channelLocation = loaded.Location;
        var release = loaded.Release;
        ReleaseText.Text = $"Latest version: {release.Version}";
        SavePreferences();
        Append($"Loaded module {release.Version}, source {release.SourceCommit.Substring(0, 12)}, {release.Files.Count} verified inventory entries.");
        SetStatus("Latest version found. Checking your installation…", "Checking your files");
        _ = RefreshChangelogAsync();
    }

    private Task RefreshAndSynchronizeAsync(CancellationToken token) => RefreshAndSynchronizeAsync(token, false);

    // fullCheck hashes every file. Otherwise an unchanged folder since the last full check passes at once.
    private async Task RefreshAndSynchronizeAsync(CancellationToken token, bool fullCheck)
    {
        if (string.IsNullOrWhiteSpace(_channelLocation))
            throw new InvalidOperationException("Connect the owner's HTTPS update channel first.");
        await LoadChannelAsync(_channelLocation, token);
        await SynchronizeCurrentAsync(token, fullCheck);
    }

    private async Task<bool> SynchronizeCurrentAsync(CancellationToken token, bool fullCheck = false)
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
        // A killed update leaves a record behind. Put the last good copy back before judging the files.
        if (_inspection?.GameReady == true && await Task.Run(() => InstallService.Recover(root), token))
            await InspectGameAsync(token);
        if (!IsManagedInstallation(root))
        {
            // Install is the next step only once the game and Armory are ready.
            SetStatus(_inspection?.GameFound != true ? "Choose your Bannerlord folder to continue."
                : _inspection.CanInstall ? "Choose Install Warlords to set up automatic updates."
                : "Finish the setup steps below, then choose Check again.");
            return false;
        }
        if (_inspection?.CanPlay != true)
        {
            SetStatus("Finish the setup steps below, then choose Check again.");
            return false;
        }
        SetStatus("Checking your installed files…", "Checking your files");
        if (await _installer.VerifyAsync(root, source.Release, Reporter(), token, fullCheck))
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
        StartSteps(InstallSteps);
        ShowStep("version", "Checking your Bannerlord version");
        // Check the actual game descriptors before writing a new module, including
        // first installations where the managed module does not yet exist.
        var modules = SafeFiles.Modules(root);
        foreach (var id in new[] { "Native", "Multiplayer" })
            if (SafeFiles.ReadXml(Path.Combine(modules, id, "SubModule.xml")).Root?.Element("Version")?.Attribute("value")?.Value != Profile.GameVersion)
                throw new InvalidOperationException("This build requires Bannerlord " + Profile.GameVersion + ". Select the matching Steam installation.");
        var result = await _installer.InstallAsync(root, source, Reporter(), token);
        EndSteps(true);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Xml.XmlException or ArgumentException or NotSupportedException or InvalidOperationException)
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
                _inspection.GameFound ? "The required game files need attention. Open logs in Settings & help has the details." : FirstMessage(_inspection.Messages, "Choose a valid Bannerlord folder."));
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
        return new UiProgress(Dispatcher, value =>
        {
            if (!_busy || !ReferenceEquals(_operation, operation) || operation?.IsCancellationRequested == true) return;
            OperationText.Text = value.Message;
            ShowDownloadDetail(value);
            if (value.Step is { } step)
                ShowStep(step, value.DoneBytes is { } done && value.TotalBytes is { } total
                    ? $"{value.Message}  {done / (1024.0 * 1024):0} / {total / (1024.0 * 1024):0} MB"
                    : value.Message);
            Progress.IsIndeterminate = value.Fraction is null;
            if (value.Fraction is { } fraction)
            {
                Progress.Value = Math.Max(0, Math.Min(fraction, 1)) * 100;
                ProgressPercentText.Text = $"{Progress.Value:0}%";
            }
            else ProgressPercentText.Text = "Working…";
        });
    }

    private void ShowDownloadDetail(OperationProgress value)
    {
        if (value.DoneBytes is not { } done || value.TotalBytes is not { } total)
        {
            _speedClock.Reset();
            _bytesPerSecond = 0;
            ProgressDetailText.Visibility = Visibility.Collapsed;
            return;
        }
        if (!_speedClock.IsRunning)
        {
            _speedClock.Start();
            _speedBytes = done;
        }
        else if (_speedClock.Elapsed.TotalSeconds >= 1)
        {
            // Smoothed, so the time left doesn't jump around.
            var latest = (done - _speedBytes) / _speedClock.Elapsed.TotalSeconds;
            _bytesPerSecond = _bytesPerSecond == 0 ? latest : _bytesPerSecond * 0.7 + latest * 0.3;
            _speedBytes = done;
            _speedClock.Restart();
        }
        const double Megabyte = 1024 * 1024;
        var text = $"{done / Megabyte:0} MB of {total / Megabyte:0} MB";
        if (_bytesPerSecond > 0)
        {
            var seconds = (total - done) / _bytesPerSecond;
            text += $"  ·  {_bytesPerSecond / Megabyte:0.0} MB/s  ·  about "
                + (seconds < 60 ? $"{Math.Ceiling(seconds):0} s left" : $"{Math.Ceiling(seconds / 60):0} min left");
        }
        ProgressDetailText.Text = text;
        ProgressDetailText.Visibility = Visibility.Visible;
    }

    // Like Progress<T>, but a report made on the UI thread shows at once. Otherwise a step
    // reported just before a sudden failure would arrive after the failure was shown.
    private sealed class UiProgress(System.Windows.Threading.Dispatcher dispatcher, Action<OperationProgress> handler) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value)
        {
            if (dispatcher.CheckAccess()) handler(value);
            else dispatcher.BeginInvoke(handler, value);
        }
    }

    private void StartSteps((string Key, string Label)[] steps)
    {
        StepsList.Children.Clear();
        _steps.Clear();
        _activeStep = -1;
        _stepsOpen = true;
        foreach (var (key, label) in steps)
        {
            // Mark in a fixed column: ✓ comes from a fallback font with a different width.
            var line = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            line.Children.Add(new TextBlock { Width = 22 });
            line.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap });
            StepsList.Children.Add(line);
            _steps.Add((key, label, line));
            SetStepLine(line, "○", label, "Muted");
        }
        StepsBox.Visibility = Visibility.Visible;
    }

    // Ticks the current step and lights up the named one. Steps never go backwards.
    private void ShowStep(string key, string text)
    {
        var index = _steps.FindIndex(step => step.Key == key);
        if (!_stepsOpen || index < 0 || index < _activeStep) return;
        if (index > _activeStep)
        {
            if (_activeStep >= 0) SetStepLine(_steps[_activeStep].Line, "✓", _steps[_activeStep].Label, "Success");
            // Steps passed over did not apply this time (no old version, no saved settings).
            for (var skipped = index - 1; skipped > _activeStep; skipped--)
            {
                StepsList.Children.Remove(_steps[skipped].Line);
                _steps.RemoveAt(skipped);
                index--;
            }
            _activeStep = index;
        }
        SetStepLine(_steps[index].Line, "●", text, "Accent");
    }

    private void EndSteps(bool success, bool cancelled = false, string reason = "failed")
    {
        if (!_stepsOpen) return;
        _stepsOpen = false;
        if (_activeStep < 0) return;
        var (_, label, line) = _steps[_activeStep];
        if (!success)
        {
            SetStepLine(line, cancelled ? "■" : "✗", label + " — " + (cancelled ? "cancelled" : reason), cancelled ? "Muted" : "Danger");
            return;
        }
        SetStepLine(line, "✓", label, "Success");
        for (var unused = _steps.Count - 1; unused > _activeStep; unused--)
        {
            StepsList.Children.Remove(_steps[unused].Line);
            _steps.RemoveAt(unused);
        }
    }

    private void SetStepLine(DockPanel line, string mark, string text, string brush)
    {
        var markText = (TextBlock)line.Children[0];
        var lineText = (TextBlock)line.Children[1];
        markText.Text = mark;
        markText.Foreground = (Brush)FindResource(brush);
        lineText.Text = text;
        lineText.Foreground = (Brush)FindResource(brush == "Muted" ? "Muted" : "Text");
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_busy) return;
        _busy = true;
        _hadError = false;
        _wasCancelled = false;
        _steamOpened = false;
        _showVerifyResult = false;
        _busyTitle = "Checking your setup";
        _operation = new CancellationTokenSource();
        OperationText.Foreground = (Brush)FindResource("Text");
        OperationText.Text = "Checking your setup…";
        Progress.Value = 0;
        Progress.IsIndeterminate = true;
        ProgressPercentText.Text = "Working…";
        StepsBox.Visibility = Visibility.Collapsed;
        _stepsOpen = false;
        UpdateButtons();
        try { await action(_operation.Token); }
        catch (OperationCanceledException)
        {
            _releaseReady = false;
            _wasCancelled = _operation.IsCancellationRequested;
            _hadError = !_wasCancelled;
            EndSteps(false, _wasCancelled, "timed out");
            if (_source is null) ReleaseText.Text = _wasCancelled ? "Update check cancelled." : "Latest version unavailable.";
            SetStatus(_operation.IsCancellationRequested
                ? "Cancelled. Any interrupted installation will be recovered before the next update."
                : "The update request timed out. Check your connection, then choose Try again.");
        }
        catch (Exception ex)
        {
            _releaseReady = false;
            _hadError = true;
            EndSteps(false, reason: ex is System.Net.Http.HttpRequestException ? "could not reach the server" : ex.Message);
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
            if (_source is null && ChangelogExpander.IsExpanded) ShowChangelogNeedsBuild();
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
        if (!_busy) ProgressDetailText.Visibility = Visibility.Collapsed;
        DetectButton.IsEnabled = BrowseButton.IsEnabled = ChoosePackButton.IsEnabled = RefreshButton.IsEnabled = !_busy;
        ArmoryButton.IsEnabled = LauncherUpdateButton.IsEnabled = !_busy;
        GameRootBox.IsEnabled = !_busy;
        BrowseButton.Content = hasGame ? "_Change…" : "Choose _folder";
        RefreshButton.Content = _hadError ? "Try _again" : "Check _again";
        // The primary button already says Try again after an error.
        RefreshButton.Visibility = _hadError ? Visibility.Collapsed : Visibility.Visible;

        InstallStateText.Text = inspection is { WarlordsNeedsManualAttention: true }
            ? FirstMessage(inspection.WarlordsMessages, "Keep one Warlords copy for this installation.")
            : _releaseReady && inspection?.CanPlay == true ? "Installed and up to date."
            : !_busy && !_managedInstallation && inspection?.InstalledVersion is { } unmanaged
                ? $"Warlords {unmanaged.TrimStart('v')} is installed, but not by this launcher."
            : inspection?.InstalledVersion is { } installed ? $"Installed version: {installed.TrimStart('v')}."
            : "Not installed yet. The launcher will keep it updated after setup.";

        var next = NextAction();
        PrimaryButton.Content = _busy ? "Working…" : next switch
        {
            PrimaryAction.ChooseFolder => "Choose game _folder",
            PrimaryAction.GetArmory => "Get _Armory",
            PrimaryAction.RemoveWorkshopCopy => "_Remove old Workshop copy",
            PrimaryAction.Install => _managedInstallation ? "_Repair Warlords" : "_Install Warlords",
            PrimaryAction.Play => "_Play Warlords",
            _ => _hadError || _wasCancelled ? "_Try again" : "_Check again"
        };
        PrimaryButton.Tag = _busy ? null : next switch
        {
            PrimaryAction.Play => "Play",
            PrimaryAction.Install => "Install",
            _ => null
        };
        // When the primary button is Get Armory, the card button would only repeat it.
        ArmoryButton.Visibility = hasGame && inspection?.ArmoryReady != true && next != PrimaryAction.GetArmory ? Visibility.Visible : Visibility.Collapsed;
        // The operation line only repeats the heading in these states. Verify files keeps it, as the answer the player asked for.
        OperationRow.Visibility = !_busy && !_hadError && !_wasCancelled && !_showVerifyResult
            && next is PrimaryAction.Play or PrimaryAction.Install or PrimaryAction.ChooseFolder or PrimaryAction.CheckAgain or PrimaryAction.GetArmory ? Visibility.Collapsed : Visibility.Visible;

        // Badges: "Done" = check, "Action" = needs the player, empty = step number.
        var playReady = _releaseReady && inspection?.CanPlay == true;
        var gameVersion = Profile.GameVersion.TrimStart('v');
        var armoryVersion = Profile.ArmoryVersion.TrimStart('v');
        var setupDone = inspection is { CanInstall: true, OldWorkshopCopies.Count: 0 };
        SetupExpander.Tag = setupDone ? "Done" : _busy ? null : "Action";
        SetupExpander.Header = playReady ? $"Everything is ready  ·  Bannerlord {gameVersion}  ·  Armory {armoryVersion}  ·  Warlords {_source?.Release.Version}"
            : setupDone ? $"Bannerlord {gameVersion} and Armory {armoryVersion} are ready"
            : inspection is null ? "Checking your setup" : "Setup steps";
        // Only when the answer changes, so a player's own open or close stays as they left it.
        if (!_busy && _setupFoldedFor != setupDone)
        {
            _setupFoldedFor = setupDone;
            SetupExpander.IsExpanded = !setupDone;
        }
        HeroBlock.MinHeight = SetupExpander.IsExpanded ? 180 : 320;
        GameStepBadge.Tag = inspection?.GameReady == true ? "Done" : inspection is not null || !_busy ? "Action" : null;
        ArmoryStepBadge.Tag = inspection?.ArmoryReady == true ? "Done" : inspection?.GameReady == true ? "Action" : null;
        WarlordsStepBadge.Tag = playReady ? "Done"
            : inspection is { WarlordsNeedsManualAttention: true } || (!_busy && next == PrimaryAction.Install) ? "Action" : null;
        StatusBadge.Tag = _busy ? "Busy" : _hadError ? "Error" : _steamOpened || playReady ? "Ready" : "Attention";
        static string StepState(object? tag) => tag switch { "Done" => "done", "Action" => "needs action", _ => "pending" };
        AutomationProperties.SetName(GameStepBadge, "Step 1 Bannerlord: " + StepState(GameStepBadge.Tag));
        AutomationProperties.SetName(ArmoryStepBadge, "Step 2 Armory: " + StepState(ArmoryStepBadge.Tag));
        AutomationProperties.SetName(WarlordsStepBadge, "Step 3 Warlords: " + StepState(WarlordsStepBadge.Tag));
        AutomationProperties.SetName(StatusBadge, "Status: " + StatusBadge.Tag switch { "Busy" => "working", "Error" => "error", "Ready" => "ready", _ => "attention" });

        if (_busy && _operation?.IsCancellationRequested == true)
        {
            StatusTitleText.Text = _closeAfterOperation ? "Finishing before closing" : "Cancelling safely";
            StatusHintText.Text = "The current file operation will finish before Warlords stops.";
        }
        else if (_busy)
        {
            StatusTitleText.Text = _busyTitle;
            StatusHintText.Text = "Keep this window open while this finishes. You can cancel safely.";
        }
        else if (_hadError)
        {
            StatusTitleText.Text = "Something went wrong";
            StatusHintText.Text = next == PrimaryAction.ChooseFolder
                ? "Choose your Bannerlord folder to continue. Open logs in Settings & help has the details."
                : "Follow the message below, then try again. Open logs in Settings & help has the details.";
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
            StatusHintText.Text = $"Warlords needs Bannerlord {Profile.GameVersion.TrimStart('v')} with its Multiplayer files. Fix the problem shown below, then choose Check again.";
        }
        else if (inspection.ArmoryReady != true)
        {
            StatusTitleText.Text = "Install Armory in Steam";
            StatusHintText.Text = "Subscribe to Armory in Steam. When it has downloaded, press Check again.";
        }
        else if (inspection.OldWorkshopCopies.Count > 0)
        {
            StatusTitleText.Text = "Remove the old Workshop copy";
            StatusHintText.Text = "An old Warlords from Steam Workshop is in the way. Choose Remove old Workshop copy and unsubscribe in Steam. The launcher waits, then clears any old files Steam leaves.";
        }
        else if (!hasRelease)
        {
            StatusTitleText.Text = "Check for the latest version";
            StatusHintText.Text = "Connect to the internet and choose Check again to load the Warlords update.";
        }
        else if (inspection.WarlordsNeedsManualAttention)
        {
            StatusTitleText.Text = "Keep only one Warlords copy";
            StatusHintText.Text = "Fix the Warlords problem shown below, then choose Check again. Workshop files are never removed for you.";
        }
        else if (_releaseReady && inspection.CanPlay)
        {
            StatusTitleText.Text = "Ready to play";
            StatusHintText.Text = "Press Play. Steam will open the game.";
        }
        else if (!_managedInstallation && inspection.InstalledVersion is not null)
        {
            StatusTitleText.Text = "Let the launcher manage Warlords";
            StatusHintText.Text = "The launcher can only update a copy it installed. Install once and future updates are automatic. Your player options are kept.";
        }
        else
        {
            StatusTitleText.Text = _managedInstallation ? "Repair your Warlords files" : "Install Warlords once";
            StatusHintText.Text = _managedInstallation
                ? "Some files changed or are missing. The launcher replaces them and keeps a backup."
                : "The launcher downloads the correct version and keeps it updated for you.";
        }
    }

    private PrimaryAction NextAction()
    {
        var inspection = _inspection;
        if (inspection is not { GameFound: true }) return PrimaryAction.ChooseFolder;
        if (_hadError || _wasCancelled || !inspection.GameReady) return PrimaryAction.CheckAgain;
        if (!inspection.ArmoryReady) return PrimaryAction.GetArmory;
        if (inspection.OldWorkshopCopies.Count > 0) return PrimaryAction.RemoveWorkshopCopy;
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
            case PrimaryAction.RemoveWorkshopCopy: await RunAsync(RemoveOldWorkshopCopyAsync); break;
            case PrimaryAction.Install: await RunAsync(InstallWithConsentAsync); break;
            case PrimaryAction.Play: await RunAsync(PlayCurrentAsync); break;
            default:
                // "Check again" hashes every file. "Try again" after an error or cancel is a quick retry.
                var fullCheck = !_hadError && !_wasCancelled;
                await RunAsync(token => RefreshAndSynchronizeAsync(token, fullCheck));
                break;
        }
    }

    private async void Changelog_Expanded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        QueueChangelogIntoView();
        await RefreshChangelogAsync();
        QueueChangelogIntoView();
    }

    // The notes are plain text: a "Warlords 1.1.3 · date" line, then "• " items, for each release.
    private void ShowNews(string notes)
    {
        var lines = notes.Split('\n').Select(line => line.Trim()).ToList();
        var items = lines.Skip(1).TakeWhile(line => !line.StartsWith("Warlords ", StringComparison.Ordinal))
            .Where(line => line.StartsWith("• ", StringComparison.Ordinal)).Take(3)
            .Select(line => line.Length > 120 ? line.Substring(0, 117).TrimEnd() + "…" : line).ToList();
        NewsCard.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NewsTitleText.Text = "New in " + lines[0];
        NewsItemsText.Text = string.Join("\n", items);
    }

    private void NewsAll_Click(object sender, RoutedEventArgs e)
    {
        ChangelogExpander.Visibility = Visibility.Visible;
        ChangelogExpander.IsExpanded = true;
        QueueChangelogIntoView();
    }

    private async void ChangelogRetry_Click(object sender, RoutedEventArgs e)
    {
        await RefreshChangelogAsync();
        QueueChangelogIntoView();
    }

    private void QueueChangelogIntoView()
    {
        if (_windowClosed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_windowClosed && ChangelogExpander.IsExpanded) ChangelogExpander.BringIntoView();
        }));
    }

    private static string ChangelogKey(ChannelSource source)
        => source.Location + "\n" + source.Release.SourceCommit + "\n" + source.Release.Version;

    private void CancelChangelogRequest()
    {
        var previous = _changelogOperation;
        _changelogOperation = null;
        // Each request owns its own disposal in finally; cancellation alone
        // avoids disposing a token while its read is still completing.
        previous?.Cancel();
    }

    private void ShowChangelogNeedsBuild()
    {
        ChangelogText.Clear();
        ChangelogStatusText.Text = _busy
            ? "Release notes will appear after the update check finishes."
            : "Choose Check again to load the current build and its release notes.";
        ChangelogRetryButton.Visibility = Visibility.Collapsed;
    }

    private bool IsCurrentChangelogRequest(CancellationTokenSource request, string key)
        => !_windowClosed && ReferenceEquals(_changelogOperation, request) && !request.IsCancellationRequested
            && _source is { } source && ChangelogKey(source) == key;

    private async Task RefreshChangelogAsync()
    {
        if (_windowClosed) return;
        var source = _source;
        if (source is null)
        {
            ShowChangelogNeedsBuild();
            return;
        }
        var key = ChangelogKey(source);
        if (_changelogLoadedKey == key && _changelogCachedText is { } cached)
        {
            ChangelogText.Text = cached;
            ShowNews(cached);
            ChangelogStatusText.Text = ChangelogLoadedStatus;
            ChangelogRetryButton.Visibility = Visibility.Collapsed;
            return;
        }

        CancelChangelogRequest();
        var request = new CancellationTokenSource();
        _changelogOperation = request;
        ChangelogText.Clear();
        ChangelogStatusText.Text = "Loading release notes…";
        ChangelogRetryButton.Visibility = Visibility.Collapsed;
        ChangelogRetryButton.IsEnabled = false;
        try
        {
            var notes = await _changelogs.LoadAsync(source, request.Token);
            if (!IsCurrentChangelogRequest(request, key)) return;
            _changelogLoadedKey = key;
            _changelogCachedText = notes;
            ChangelogText.Text = notes;
            ShowNews(notes);
            ChangelogStatusText.Text = ChangelogLoadedStatus;
        }
        catch (Exception ex)
        {
            if (!IsCurrentChangelogRequest(request, key)) return;
            ChangelogText.Clear();
            NewsCard.Visibility = Visibility.Collapsed;
            ChangelogExpander.Visibility = Visibility.Visible;
            ChangelogStatusText.Text = ex switch
            {
                FileNotFoundException or DirectoryNotFoundException => "Release notes are not available for this build yet. You can still play.",
                InvalidDataException => "Release notes could not be read for this build. You can still play.",
                OperationCanceledException => "Release notes took too long to load. You can still play.",
                _ => "Could not load release notes. You can still play."
            };
            ChangelogRetryButton.IsEnabled = true;
            ChangelogRetryButton.Visibility = Visibility.Visible;
            App.Log("Optional release notes unavailable: " + ex.GetType().Name);
        }
        finally
        {
            if (ReferenceEquals(_changelogOperation, request)) _changelogOperation = null;
            request.Dispose();
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
        if (Net48.PickFolder(this, "Select Mount & Blade II Bannerlord") is not { } folder) return;
        GameRootBox.Text = folder;
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
        await RefreshAndSynchronizeAsync(token, true);
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
        var valid = await _installer.VerifyAsync(GameRootBox.Text.Trim(), _source.Release, Reporter(), token, true);
        await InspectGameAsync(token);
        _releaseReady = valid && _inspection?.CanPlay == true && IsManagedInstallation(GameRootBox.Text.Trim());
        _showVerifyResult = true;
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
            Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = Net48.QuoteArgument(App.DataDirectory), UseShellExecute = true });
        }
        catch (Exception ex) { SetStatus("Could not open the logs folder: " + ex.Message); }
    }

    // Fixed addresses from Profile only; never built from channel or remote data.
    private void Website_Click(object sender, RoutedEventArgs e) => OpenLink(Profile.WebsiteUrl, "the StratLords website");

    private void Discord_Click(object sender, RoutedEventArgs e) => OpenLink(Profile.DiscordUrl, "the StratLords Discord");

    private void GitHub_Click(object sender, RoutedEventArgs e) => OpenLink(Profile.GitHubUrl, "the Warlords GitHub page");

    private void OpenLink(string url, string name)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus("Could not open " + name + ": " + ex.Message); }
    }

    // Never blocks the mod update or Play: any failure only hides the button.
    private async Task CheckLauncherUpdateAsync()
    {
        try
        {
            _launcherUpdate = await _channels.CheckLauncherUpdateAsync(CancellationToken.None);
            if (_launcherUpdate is null) return;
            LauncherUpdateText.Text = "Update launcher " + _launcherUpdate.Version;
            LauncherUpdateButton.Visibility = Visibility.Visible;
            Append("Launcher " + _launcherUpdate.Version + " is available. Choose Update launcher at the top.");
        }
        catch (Exception ex)
        {
            App.Log("Launcher update check skipped: " + ex.Message);
        }
    }

    private async void UpdateLauncher_Click(object sender, RoutedEventArgs e) => await RunAsync(UpdateLauncherAsync);

    // Swaps this exe for the signed newer one, then restarts. Mod files are not touched.
    private async Task UpdateLauncherAsync(CancellationToken token)
    {
        var update = _launcherUpdate ?? throw new InvalidOperationException("No launcher update is available.");
        if (MessageBox.Show(this,
            $"Update the Warlords launcher from {Profile.LauncherVersion} to {update.Version}?\n\n"
            + "The launcher downloads the new version, checks its signature and fingerprint, and restarts. Your Warlords mod files are not touched.",
            "Update launcher", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        SetStatus("Downloading launcher " + update.Version + "…", "Updating the launcher");
        StartSteps(LauncherSteps);
        var current = App.LauncherPath;
        try
        {
            await _channels.DownloadLauncherAsync(update, App.NewLauncherPath, Reporter(), token);
            ShowStep("swap", "Swapping the old launcher for the new one");
            if (File.Exists(App.OldLauncherPath)) File.Delete(App.OldLauncherPath);
            // Windows lets a running exe be renamed, not overwritten.
            File.Move(current, App.OldLauncherPath);
            try
            {
                // Antivirus often holds a freshly written exe for a moment.
                for (var attempt = 1; ; attempt++)
                {
                    try { File.Move(App.NewLauncherPath, current); break; }
                    catch (IOException) when (attempt < 5) { await Task.Delay(1000, token); }
                }
            }
            catch
            {
                File.Move(App.OldLauncherPath, current);
                throw;
            }
        }
        catch (UnauthorizedAccessException)
        {
            if (File.Exists(App.NewLauncherPath)) File.Delete(App.NewLauncherPath);
            EndSteps(false, reason: "the launcher folder can't be changed");
            OpenLink(Profile.LauncherReleasesUrl, "the launcher download page");
            SetStatus("The launcher's folder can't be changed. Download the new Warlords.exe from the page that just opened.");
            return;
        }
        var arguments = Environment.GetCommandLineArgs().Skip(1).Where(arg => arg != App.AfterUpdateArgument).Append(App.AfterUpdateArgument);
        var restart = new ProcessStartInfo(current) { Arguments = string.Join(" ", arguments.Select(Net48.QuoteArgument)), UseShellExecute = false };
        ShowStep("restart", "Restarting the launcher");
        ((App)Application.Current).ReleaseSingleInstance();
        Process.Start(restart);
        EndSteps(true);
        SetStatus("Launcher " + update.Version + " installed. Restarting…");
        _closeAfterOperation = true;
    }

    // The old Workshop item shares the Warlords module Id, so Bannerlord would see two copies.
    private async Task RemoveOldWorkshopCopyAsync(CancellationToken token)
    {
        var root = GameRootBox.Text.Trim();
        var copies = new System.Collections.Generic.List<string>(_inspection?.OldWorkshopCopies ?? []);
        if (copies.Count == 0)
        {
            await InspectGameAsync(token);
            return;
        }
        if (MessageBox.Show(this,
            "An old Warlords copy from Steam Workshop clashes with the launcher's copy.\n\n"
            + "1. Steam opens the old Workshop page. Choose Unsubscribe there.\n"
            + "2. The launcher waits until Steam lets go of it. Steam then removes the old files itself. Any it leaves are moved to a backup folder in your Steam library.\n\n"
            + string.Join("\n", copies),
            "Remove old Workshop copy", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            SetStatus("Nothing was changed. The old Workshop copy must go before Warlords can be installed.");
            return;
        }
        foreach (var folder in copies)
            Process.Start(new ProcessStartInfo("steam://url/CommunityFilePage/" + Path.GetFileName(folder)) { UseShellExecute = true });
        // Moving the files while Steam still lists the item breaks Bannerlord's own launcher, so wait for the unsubscribe.
        SetStatus("Waiting for you to unsubscribe from the old Warlords in Steam…");
        for (var waited = 0; copies.Any(GameService.IsWorkshopItemSubscribed); waited += 2)
        {
            if (waited >= 120)
            {
                SetStatus("Steam still lists the old Warlords. Nothing was moved. Unsubscribe in Steam, then choose Check again.");
                return;
            }
            await Task.Delay(2000, token);
        }
        var moved = await Task.Run(() =>
        {
            var backups = new System.Collections.Generic.List<string>();
            foreach (var folder in copies)
                if (_game.MoveOldWorkshopCopy(root, folder) is { } backup) backups.Add(backup);
            return backups;
        }, token);
        SetStatus(moved.Count > 0
            ? "Old Workshop files moved to " + string.Join(", ", moved) + "."
            : "Steam removed the old Workshop files.");
        await RefreshAndSynchronizeAsync(token);
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

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    // Close() keeps the OnClosing path that finishes a file operation first.
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateWindowChrome()
    {
        var maximized = WindowState == WindowState.Maximized;
        // A maximized custom-chrome window extends past the screen by its resize
        // frame plus the padded border; inset the content by the same amount.
        var frame = SystemParameters.WindowResizeBorderThickness;
        var padded = GetSystemMetrics(SmCxPaddedBorder) / VisualTreeHelper.GetDpi(this).DpiScaleX;
        WindowFrame.Margin = maximized
            ? new Thickness(frame.Left + padded, frame.Top + padded, frame.Right + padded, frame.Bottom + padded)
            : new Thickness();
        WindowFrame.BorderThickness = new Thickness(maximized ? 0 : 1);
        MaximizeGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
        AutomationProperties.SetName(MaximizeButton, maximized ? "Restore" : "Maximize");
    }

    private void StartSplash()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        ((Storyboard)FindResource("HeroDrift")).Begin(this, true);
        var splash = ((Storyboard)FindResource("SplashStoryboard")).Clone();
        splash.Completed += (_, _) => FinishSplash();
        _splash = splash;
        splash.Begin(this, true);
    }

    // Removing the animation restores the finished layout: overlay collapsed,
    // not hit-testable, content fully visible.
    private void FinishSplash()
    {
        var splash = _splash;
        if (splash is null) return;
        _splash = null;
        splash.Remove(this);
    }

    private const int SmCxPaddedBorder = 92;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
