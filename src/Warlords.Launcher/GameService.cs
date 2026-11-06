using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Warlords.Launcher;

/// <summary>Discovers the Steam game and inspects only the profile this launcher manages.</summary>
public sealed class GameService
{
    private const string GameName = "Mount & Blade II Bannerlord";
    private const string SteamAppId = "261550";
    private const string ArmoryId = "MBWarlords.Armory";
    private const int MaximumFolders = 2048;
    private static readonly Regex VdfPath = new("\\\"path\\\"\\s*\\\"(?<value>(?:\\\\.|[^\\\"])*)\\\"", RegexOptions.CultureInvariant);
    private static readonly Regex InstallDirectory = new("\\\"installdir\\\"\\s*\\\"(?<value>(?:\\\\.|[^\\\"])*)\\\"", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public string? Discover()
    {
        return SteamGameRoots().FirstOrDefault(IsGameRoot);
    }

    public GameInspection Inspect(string gameRoot)
    {
        var messages = new List<string>();
        string root;
        try
        {
            root = Path.GetFullPath(gameRoot.Trim());
            if (!IsGameRoot(root))
                return new GameInspection(root, null, false, ["Select the Bannerlord game folder containing Modules and bin/Win64_Shipping_Client."]);
            AssertNotLink(root);
            AssertNotLink(Path.Combine(root, "Modules"));
        }
        catch (Exception ex) when (IsPathError(ex))
        {
            return new GameInspection(gameRoot, null, false, ["Cannot read the selected game folder: " + ex.Message]);
        }

        var inventory = new Dictionary<string, List<ModuleDescriptor>>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool ready = true;
        bool inventoryReadable = true;
        foreach (string container in ModuleContainers(root))
        {
            try
            {
                AssertNotLink(container);
                int count = 0;
                foreach (string folder in Directory.EnumerateDirectories(container))
                {
                    if (++count > MaximumFolders)
                    {
                        messages.Add("There are too many module folders to inspect safely. Remove unused module copies before playing.");
                        ready = false;
                        inventoryReadable = false;
                        break;
                    }
                    string descriptor = Path.Combine(folder, "SubModule.xml");
                    if (!File.Exists(descriptor) || !folders.Add(Path.GetFullPath(folder))) continue;
                    try
                    {
                        AssertNotLink(folder);
                        AssertNotLink(descriptor);
                        ModuleDescriptor module = ReadModule(descriptor);
                        if (!Profile.Modules.Contains(module.Id, StringComparer.OrdinalIgnoreCase)) continue;
                        if (!inventory.TryGetValue(module.Id, out List<ModuleDescriptor>? modules))
                        {
                            modules = [];
                            inventory.Add(module.Id, modules);
                        }
                        modules.Add(module);
                    }
                    catch (Exception ex) when (IsDescriptorError(ex))
                    {
                        string folderName = Path.GetFileName(folder);
                        bool requiredFolder = folderName.Equals("Native", StringComparison.OrdinalIgnoreCase)
                            || folderName.Equals("Multiplayer", StringComparison.OrdinalIgnoreCase)
                            || folderName.Equals("StratLords", StringComparison.OrdinalIgnoreCase)
                            || folderName.Equals("3712562953", StringComparison.OrdinalIgnoreCase);
                        if (requiredFolder)
                        {
                            messages.Add($"Cannot read {folderName}/SubModule.xml: {ex.Message}");
                            ready = false;
                        }
                    }
                }
            }
            catch (Exception ex) when (IsPathError(ex))
            {
                messages.Add("Cannot inspect module library " + container + ": " + ex.Message);
                ready = false;
                inventoryReadable = false;
            }
        }

        string? installedVersion = null;
        var moduleReady = new Dictionary<string, bool>(StringComparer.Ordinal);
        var gameMessages = new List<string>();
        var armoryMessages = new List<string>();
        var warlordsMessages = new List<string>();
        bool warlordsNeedsManualAttention = false;
        foreach (string id in Profile.Modules)
        {
            moduleReady[id] = false;
            var requirementMessages = id == Profile.ModuleId ? warlordsMessages : id == ArmoryId ? armoryMessages : gameMessages;
            int before = messages.Count;
            void Issue(string message)
            {
                messages.Add(message);
                requirementMessages.Add(message);
                ready = false;
            }
            if (!inventory.TryGetValue(id, out List<ModuleDescriptor>? matches) || matches.Count == 0)
            {
                Issue(id == Profile.ModuleId
                    ? "Warlords is not installed. Choose Install Warlords to get started."
                    : id == ArmoryId
                        ? "Subscribe to Warlords Armory in Steam Workshop and let Steam finish its download."
                        : $"{id} is missing. Verify Bannerlord's files in Steam.");
                continue;
            }
            if (matches.Count != 1)
            {
                Issue($"More than one installed copy uses {id}. Keep one copy for this profile; the launcher will not delete Workshop content.");
                if (id == Profile.ModuleId) warlordsNeedsManualAttention = true;
                continue;
            }
            ModuleDescriptor module = matches[0];
            if (!module.Id.Equals(id, StringComparison.Ordinal))
            {
                Issue($"{id} has an unexpected module identity. Repair or reinstall its official package.");
            }
            string? requiredVersion = id is "Native" or "Multiplayer" ? Profile.GameVersion
                : id == ArmoryId ? Profile.ArmoryVersion : null;
            if (requiredVersion != null && !module.Version.Equals(requiredVersion, StringComparison.Ordinal))
            {
                Issue($"{id}: found {module.Version}; requires exactly {requiredVersion}.");
            }
            if (id == Profile.ModuleId)
            {
                installedVersion = module.Version;
                string expected = Path.Combine(root, "Modules", "StratLords", "SubModule.xml");
                if (!Path.GetFullPath(module.File).Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    Issue("An existing Warlords copy is outside the launcher's folder. Remove that copy before installing with this launcher; Workshop content is never removed automatically.");
                    warlordsNeedsManualAttention = true;
                }
                if (!module.IsMultiplayer)
                {
                    Issue("The Warlords module descriptor does not declare a multiplayer module. Repair this installation.");
                }
                foreach ((string dependency, string version) in new[] { ("Native", Profile.GameVersion), ("Multiplayer", Profile.GameVersion), (ArmoryId, Profile.ArmoryVersion) })
                {
                    if (!module.Dependencies.TryGetValue(dependency, out string? actual) || actual != version)
                    {
                        Issue($"Warlords must declare dependency {dependency} {version}. Repair this installation.");
                    }
                }
            }
            moduleReady[id] = messages.Count == before;
        }
        if (ready)
            messages.Add("Game and required modules are compatible. In Bannerlord, select the Multiplayer tab and the four modules shown below.");
        // A Warlords copy directly inside a Bannerlord Workshop folder is the old Workshop item.
        var workshopContainers = new HashSet<string>(ModuleContainers(root).Select(c => Path.TrimEndingDirectorySeparator(Path.GetFullPath(c))), StringComparer.OrdinalIgnoreCase);
        workshopContainers.Remove(Path.TrimEndingDirectorySeparator(Path.Combine(root, "Modules")));
        var oldWorkshopCopies = inventory.TryGetValue(Profile.ModuleId, out List<ModuleDescriptor>? warlordsCopies)
            ? warlordsCopies.Select(m => Path.GetDirectoryName(Path.GetFullPath(m.File))!)
                .Where(folder => workshopContainers.Contains(Path.GetDirectoryName(folder)!)).ToList()
            : [];
        return new GameInspection(root, installedVersion, ready, messages)
        {
            GameFound = true,
            GameReady = inventoryReadable && moduleReady["Native"] && moduleReady["Multiplayer"],
            ArmoryReady = inventoryReadable && moduleReady[ArmoryId],
            WarlordsNeedsManualAttention = warlordsNeedsManualAttention,
            GameMessages = gameMessages,
            ArmoryMessages = armoryMessages,
            WarlordsMessages = warlordsMessages,
            OldWorkshopCopies = oldWorkshopCopies
        };
    }

    /// <summary>Moves an old Workshop copy of Warlords to a backup folder in the same Steam library, after the player agreed.</summary>
    /// <returns>The backup folder, or null when Steam already removed the copy.</returns>
    public string? MoveOldWorkshopCopy(string gameRoot, string folder)
    {
        using IDisposable updateLock = InstallService.AcquireLock(gameRoot);
        EnsureClosed();
        if (!Directory.Exists(folder)) return null;
        if (!Inspect(gameRoot).OldWorkshopCopies.Contains(folder, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("That folder is no longer an old Workshop copy of Warlords. Choose Check again.");
        AssertNotLink(folder);
        // ...\steamapps\workshop\content\261550\<item>: the backup stays on the same drive, outside every module folder.
        string steamapps = Directory.GetParent(folder)!.Parent!.Parent!.Parent!.FullName;
        string backup = Path.Combine(steamapps, "warlords-workshop-backup", Path.GetFileName(folder) + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        Directory.Move(folder, backup);
        return backup;
    }

    public void Play(string gameRoot)
    {
        using IDisposable updateLock = InstallService.AcquireLock(gameRoot);
        PlayUnderLock(gameRoot);
    }

    // Called by InstallService's verified-launch callback while it owns the same installation lock.
    internal void PlayUnderLock(string gameRoot)
    {
        EnsureClosed();
        // Recovery changes the installation and belongs to the install operation, never to Play.
        string modules = Path.Combine(Path.GetFullPath(gameRoot), "Modules");
        if (File.Exists(Path.Combine(modules, ".StratLords.warlords-update.json")))
            throw new InvalidOperationException("An interrupted update needs recovery. Choose Install / Update before playing.");
        GameInspection inspection = Inspect(gameRoot);
        if (!inspection.CanPlay)
            throw new InvalidOperationException(string.Join(Environment.NewLine, inspection.Messages));
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
        bool registered = SteamGameRoots().Any(candidate => Path.TrimEndingDirectorySeparator(candidate).Equals(root, StringComparison.OrdinalIgnoreCase));
        if (!registered)
            throw new InvalidOperationException("Select the Bannerlord folder installed by Steam. Play opens Steam's registered game, so a separate copied game folder cannot be launched here.");
        EnsureClosed();
        // Shell activation can reuse an existing Steam process and legitimately return null.
        Process.Start(new ProcessStartInfo("steam://rungameid/" + SteamAppId)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.Combine(root, "bin", "Win64_Shipping_Client")
        });
    }

    public static void EnsureClosed()
    {
        var running = new List<string>();
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    string name = process.ProcessName;
                    if (name.StartsWith("Bannerlord", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("TaleWorlds.MountAndBlade.Launcher", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("TaleWorlds.MountAndBlade.SteamWorkshop", StringComparison.OrdinalIgnoreCase))
                        running.Add(name);
                }
                catch (InvalidOperationException) { /* A process that exited is already closed. */ }
            }
        }
        if (running.Count > 0)
            throw new InvalidOperationException("Close Bannerlord and its game launcher before continuing (" + string.Join(", ", running.Distinct()) + ").");
    }

    private static bool IsGameRoot(string root)
    {
        return Directory.Exists(Path.Combine(root, "Modules"))
            && File.Exists(Path.Combine(root, "bin", "Win64_Shipping_Client", "Bannerlord.exe"))
            && File.Exists(Path.Combine(root, "bin", "Win64_Shipping_Client", "TaleWorlds.MountAndBlade.Launcher.exe"));
    }

    private static IEnumerable<string> ModuleContainers(string gameRoot)
    {
        var containers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(gameRoot, "Modules") };
        foreach (string library in SteamLibraries())
        {
            string workshop = Path.Combine(library, "steamapps", "workshop", "content", SteamAppId);
            if (Directory.Exists(workshop)) containers.Add(workshop);
        }
        DirectoryInfo? common = Directory.GetParent(gameRoot);
        if (common?.Name.Equals("common", StringComparison.OrdinalIgnoreCase) == true && common.Parent != null)
        {
            string workshop = Path.Combine(common.Parent.FullName, "workshop", "content", SteamAppId);
            if (Directory.Exists(workshop)) containers.Add(workshop);
        }
        return containers;
    }

    private static IEnumerable<string> SteamGameRoots()
    {
        foreach (string library in SteamLibraries())
        {
            string installName = GameName;
            string manifest = Path.Combine(library, "steamapps", "appmanifest_" + SteamAppId + ".acf");
            try
            {
                if (File.Exists(manifest) && new FileInfo(manifest).Length <= 1024 * 1024)
                {
                    Match match = InstallDirectory.Match(File.ReadAllText(manifest));
                    if (match.Success)
                    {
                        string candidate = UnescapeVdf(match.Groups["value"].Value);
                        if (candidate.Length > 0 && !candidate.Contains('/') && !candidate.Contains('\\') && candidate is not "." and not "..")
                            installName = candidate;
                    }
                }
            }
            catch (Exception ex) when (IsPathError(ex)) { }
            yield return Path.GetFullPath(Path.Combine(library, "steamapps", "common", installName));
        }
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((RegistryKey hive, string keyName, string valueName) in new[]
        {
            (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath")
        })
        {
            try
            {
                using RegistryKey? key = hive.OpenSubKey(keyName);
                if (key?.GetValue(valueName) is string path && Directory.Exists(path)) roots.Add(Path.GetFullPath(path));
            }
            catch (Exception ex) when (IsPathError(ex) || ex is System.Security.SecurityException) { }
        }
        string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        if (Directory.Exists(fallback)) roots.Add(fallback);
        var libraries = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            try
            {
                if (!File.Exists(vdf) || new FileInfo(vdf).Length > 4 * 1024 * 1024) continue;
                foreach (Match match in VdfPath.Matches(File.ReadAllText(vdf)).Take(32))
                {
                    string path = UnescapeVdf(match.Groups["value"].Value);
                    if (Path.IsPathFullyQualified(path) && Directory.Exists(path)) libraries.Add(Path.GetFullPath(path));
                }
            }
            catch (Exception ex) when (IsPathError(ex)) { }
        }
        return libraries;
    }

    private static string UnescapeVdf(string value) => value.Replace("\\\\", "\\", StringComparison.Ordinal).Replace("\\\"", "\"", StringComparison.Ordinal);

    private static ModuleDescriptor ReadModule(string path)
    {
        using XmlReader reader = XmlReader.Create(path, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 2 * 1024 * 1024
        });
        XElement root = XDocument.Load(reader).Root ?? throw new InvalidDataException("Missing XML root.");
        if (root.Name != "Module") throw new InvalidDataException("Expected a Module descriptor.");
        string id = (string?)root.Element("Id")?.Attribute("value") ?? "";
        string version = (string?)root.Element("Version")?.Attribute("value") ?? "";
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
            throw new InvalidDataException("Missing module Id or Version.");
        var dependencies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (XElement element in root.Element("DependedModules")?.Elements("DependedModule") ?? [])
        {
            string dependency = (string?)element.Attribute("Id") ?? "";
            string required = (string?)element.Attribute("DependentVersion") ?? "";
            if (!dependencies.TryAdd(dependency, required)) throw new InvalidDataException("Duplicate dependency declaration.");
        }
        bool multiplayer = (string?)root.Element("ModuleCategory")?.Attribute("value") == "Multiplayer"
            || string.Equals((string?)root.Element("MultiplayerModule")?.Attribute("value"), "true", StringComparison.OrdinalIgnoreCase);
        return new ModuleDescriptor(id, version, path, multiplayer, dependencies);
    }

    private static void AssertNotLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked module folders and files are not supported: " + path);
    }

    private static bool IsPathError(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
    private static bool IsDescriptorError(Exception ex) => IsPathError(ex) || ex is XmlException or InvalidDataException;
    private sealed record ModuleDescriptor(string Id, string Version, string File, bool IsMultiplayer, Dictionary<string, string> Dependencies);
}
