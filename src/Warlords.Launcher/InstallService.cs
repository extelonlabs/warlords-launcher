using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Warlords.Launcher;

public sealed class InstallService
{
    private const long MaximumPackage = 16L * 1024 * 1024 * 1024;
    private const long MaximumRelease = 100L * 1024 * 1024 * 1024;
    private const string StageName = ".StratLords.warlords-stage";
    private const string JournalName = ".StratLords.warlords-update.json";
    private const string StateName = ".warlords-launcher-state.json";
    private const string OwnerName = ".warlords-stage-owner.json";
    private const string OptionsPath = "ModuleData/wl_client_options.json";
    private static readonly Regex Hash = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex Commit = new("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);
    private static readonly Regex ModuleVersion = new(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant);
    private static readonly string[] RequiredFiles =
    [
        "SubModule.xml",
        "bin/Win64_Shipping_Client/MBWarlords.LastConcord.dll",
        "bin/Win64_Shipping_Client/MBWarlords.LastConcord.View.dll",
        "bin/Win64_Shipping_Client/MBWarlords.LastConcord.GauntletUI.dll",
        "bin/Win64_Shipping_Client/StrategusBattleBridge.GauntletUI.dll"
    ];

    public static void ValidateRelease(ReleaseChannel release)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (release.SchemaVersion != 1 || (release.Branch != Profile.Branch && release.Branch != Profile.PreviousBranch) || release.SourceRepository != Profile.Repository
            || release.RequiredGameVersion != Profile.GameVersion || release.RequiredArmoryVersion != Profile.ArmoryVersion)
            throw new InvalidDataException("This test channel is not for the supported StratLords profile.");
        if (release.SourceCommit is null || !Commit.IsMatch(release.SourceCommit) || release.Version is null || !ModuleVersion.IsMatch(release.Version)
            || string.IsNullOrWhiteSpace(release.BuildId) || release.BuildId.Length > 200 || release.BuildId.Any(char.IsControl))
            throw new InvalidDataException("The test channel has invalid build identity metadata.");
        if (release.CreatedUtc == default || release.CreatedUtc > DateTimeOffset.UtcNow.AddMinutes(10)
            || (release.ExpiresUtc is { } expiry && (expiry <= DateTimeOffset.UtcNow || expiry <= release.CreatedUtc)))
            throw new InvalidDataException("This test pack has expired or has invalid dates. Ask the owner for a fresh matched pack.");
        if (release.PackageSize <= 0 || release.PackageSize > MaximumPackage || release.PackageSha256 is null || !Hash.IsMatch(release.PackageSha256)
            || string.IsNullOrWhiteSpace(release.Package))
            throw new InvalidDataException("The package size, address or SHA-256 is invalid.");
        if (Uri.TryCreate(release.Package, UriKind.Absolute, out Uri? packageUri))
        {
            if (packageUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(packageUri.UserInfo))
                throw new InvalidDataException("Remote packages must use HTTPS without embedded credentials.");
        }
        else
        {
            SafeFiles.Relative(release.Package);
            if (release.Package.Contains('/')) throw new InvalidDataException("A local package must be beside its channel file.");
        }
        if (release.Files is null || release.Files.Count == 0 || release.Files.Count > 50000)
            throw new InvalidDataException("The test channel has no complete file inventory.");
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (ReleaseFile? file in release.Files)
        {
            if (file is null || file.Path is null || file.Sha256 is null || !Hash.IsMatch(file.Sha256) || file.Size < 0 || file.Size > MaximumPackage)
                throw new InvalidDataException("The package inventory contains invalid file metadata.");
            string path = SafeFiles.Relative(file.Path);
            if (!paths.Add(path) || path.Equals(StateName, StringComparison.OrdinalIgnoreCase) || path.Equals(OwnerName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The package inventory contains duplicate or reserved files.");
            total = checked(total + file.Size);
            if (total > MaximumRelease) throw new InvalidDataException("The package expands beyond the supported size.");
        }
        foreach (string path in paths)
        {
            string[] segments = path.Split('/');
            for (int index = 1; index < segments.Length; index++)
                if (paths.Contains(string.Join('/', segments.Take(index))))
                    throw new InvalidDataException("The package treats a file as a folder.");
        }
        if (RequiredFiles.Any(path => !paths.Contains(path) || release.Files.Single(file => file.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).Size == 0))
            throw new InvalidDataException("The package is missing the module descriptor or a required client assembly.");
    }

    public static IDisposable AcquireLock(string gameRoot)
    {
        string modules = SafeFiles.Modules(gameRoot);
        string lockPath = Path.Combine(modules, ".StratLords.warlords-update.lock");
        SafeFiles.NoLinks(lockPath);
        try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new IOException("Another Warlords launcher is updating or starting this installation. Close it and try again.", error); }
    }

    public static void Recover(string gameRoot)
    {
        using IDisposable exclusive = AcquireLock(gameRoot);
        GameService.EnsureClosed();
        RecoverCore(SafeFiles.Modules(gameRoot));
    }

    public async Task<InstallResult> InstallAsync(string gameRoot, ChannelSource source, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ValidateRelease(source.Release);
        using IDisposable exclusive = AcquireLock(gameRoot);
        GameService.EnsureClosed();
        string modules = SafeFiles.Modules(gameRoot);
        RecoverCore(modules);
        string target = Path.Combine(modules, "StratLords");
        ValidateExistingTarget(target);
        string recovery = RecoveryFolder(modules, true);
        string stage = Path.Combine(recovery, StageName);
        if (Directory.Exists(stage) || File.Exists(stage)) throw new IOException("An unrecognized staging folder remains. Keep it intact and contact the owner.");
        string transaction = Guid.NewGuid().ToString("N");
        Journal journal = new(1, transaction, ".StratLords.warlords-backup-" + transaction, "staging", Directory.Exists(target));
        string? downloadedPackage = null;
        WriteJournal(modules, journal);
        try
        {
            Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, OwnerName), transaction);
            PackageLocation packageLocation = await GetPackageAsync(source, progress, cancellationToken).ConfigureAwait(false);
            string package = packageLocation.Path;
            if (packageLocation.Temporary) downloadedPackage = package;
            progress.Report(new("Checking the complete test package", 0.25));
            if (!await SafeFiles.MatchesAsync(package, source.Release.PackageSize, source.Release.PackageSha256, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("The test package is incomplete or its SHA-256 does not match. Download a fresh copy.");
            await ExtractAsync(package, stage, source.Release, progress, cancellationToken).ConfigureAwait(false);
            ValidateDescriptor(stage, source.Release);
            string oldOptions = SafeFiles.Under(target, OptionsPath);
            if (File.Exists(oldOptions))
            {
                if (new FileInfo(oldOptions).Length > 4 * 1024 * 1024) throw new IOException("Player options are unexpectedly large; keep the current installation and contact the owner.");
                string options = SafeFiles.Under(stage, OptionsPath);
                Directory.CreateDirectory(Path.GetDirectoryName(options)!);
                File.Copy(oldOptions, options, true);
            }
            File.WriteAllText(Path.Combine(stage, StateName), JsonSerializer.Serialize(new InstalledState(1, source.Release.Version, source.Release.SourceCommit, source.Release.BuildId, transaction), Profile.Json));
            File.Delete(Path.Combine(stage, OwnerName));
            WriteJournal(modules, journal with { Phase = "prepared" });
            cancellationToken.ThrowIfCancellationRequested();
            GameService.EnsureClosed();
            ValidateRelease(source.Release);
            ValidateExistingTarget(target);
            progress.Report(new("Installing the verified build; keeping the previous module as a backup", 0.95));
            string backup = Path.Combine(recovery, journal.BackupName);
            SafeFiles.NoLinks(backup);
            if (File.Exists(backup) || Directory.Exists(backup)) throw new IOException("The backup destination already exists. Nothing was replaced.");
            if (Directory.Exists(target)) Directory.Move(target, backup);
            WriteJournal(modules, journal with { Phase = "backedUp" });
            Directory.Move(stage, target);
            WriteJournal(modules, journal with { Phase = "committed" });
            File.Delete(Path.Combine(modules, JournalName));
            progress.Report(new("Installed and verified. The previous module is retained as a backup.", 1));
            return new InstallResult(target, journal.TargetWasPresent ? backup : null);
        }
        catch
        {
            // Recovery either restores the previous directory or recognizes the completed
            // verified swap. It never guesses about an unrelated or unowned folder.
            RecoverCore(modules);
            throw;
        }
        finally
        {
            if (downloadedPackage is not null)
            {
                SafeFiles.NoLinks(downloadedPackage);
                File.Delete(downloadedPackage);
            }
        }
    }

    public async Task<bool> VerifyAsync(string gameRoot, ReleaseChannel release, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ValidateRelease(release);
        using IDisposable exclusive = AcquireLock(gameRoot);
        return await VerifyCoreAsync(gameRoot, release, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> VerifyAndRunAsync(string gameRoot, ReleaseChannel release, IProgress<OperationProgress> progress, CancellationToken cancellationToken, Action whenValid)
    {
        ArgumentNullException.ThrowIfNull(whenValid);
        ValidateRelease(release);
        using IDisposable exclusive = AcquireLock(gameRoot);
        if (!await VerifyCoreAsync(gameRoot, release, progress, cancellationToken).ConfigureAwait(false)) return false;
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRelease(release);
        whenValid();
        return true;
    }

    private static async Task<bool> VerifyCoreAsync(string gameRoot, ReleaseChannel release, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        GameService.EnsureClosed();
        string modules = SafeFiles.Modules(gameRoot);
        SafeFiles.NoLinks(Path.Combine(modules, JournalName));
        if (File.Exists(Path.Combine(modules, JournalName))) throw new IOException("An interrupted update needs recovery. Choose Install / Update before playing.");
        string target = Path.Combine(modules, "StratLords");
        if (!Directory.Exists(target)) return false;
        ValidateExistingTarget(target);
        Dictionary<string, ReleaseFile> expected = release.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        HashSet<string> actual = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in SafeFiles.TreeFiles(target))
        {
            string relative = Path.GetRelativePath(target, path).Replace('\\', '/');
            if (!actual.Add(relative)) return false;
            if (relative.Equals(StateName, StringComparison.OrdinalIgnoreCase) || relative.Equals(OptionsPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (!expected.ContainsKey(relative)) return false;
        }
        int count = 0;
        foreach (ReleaseFile file in release.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!file.Path.Equals(OptionsPath, StringComparison.OrdinalIgnoreCase)
                && !await SafeFiles.MatchesAsync(SafeFiles.Under(target, file.Path), file.Size, file.Sha256, cancellationToken).ConfigureAwait(false)) return false;
            progress.Report(new("Verifying installed files (" + (++count) + " / " + release.Files.Count + ")", (double)count / release.Files.Count));
        }
        ValidateDescriptor(target, release);
        return true;
    }

    private static void ValidateExistingTarget(string target)
    {
        SafeFiles.NoLinks(target);
        if (File.Exists(target)) throw new IOException("Modules/StratLords is a file, not a managed module folder.");
        if (!Directory.Exists(target) || !Directory.EnumerateFileSystemEntries(target).Any()) return;
        if (new[] { ".git", ".svn", ".hg" }.Any(name => Directory.Exists(Path.Combine(target, name)) || File.Exists(Path.Combine(target, name))))
            throw new IOException("The selected target contains source-control files. A developer checkout cannot be replaced by the launcher.");
        foreach (string path in SafeFiles.TreeFiles(target))
        {
            string name = Path.GetFileName(path);
            if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || name.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || Path.GetRelativePath(target, path).Split(Path.DirectorySeparatorChar).Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("The selected target contains source files. A developer checkout cannot be replaced by the launcher.");
        }
        string descriptor = Path.Combine(target, "SubModule.xml");
        if (!File.Exists(descriptor) || SafeFiles.ReadXml(descriptor).Root?.Element("Id")?.Attribute("value")?.Value != Profile.ModuleId)
            throw new IOException("Modules/StratLords contains unrecognized files. Keep it intact and choose the real game installation.");
    }

    private static void ValidateDescriptor(string root, ReleaseChannel release)
    {
        XElement? module = SafeFiles.ReadXml(Path.Combine(root, "SubModule.xml")).Root;
        if (module?.Name != "Module" || module.Elements("Id").Count() != 1 || module.Element("Id")?.Attribute("value")?.Value != Profile.ModuleId
            || module.Elements("Version").Count() != 1 || module.Element("Version")?.Attribute("value")?.Value.TrimStart('v') != release.Version
            || (module.Element("ModuleCategory")?.Attribute("value")?.Value != "Multiplayer"
                && !string.Equals(module.Element("MultiplayerModule")?.Attribute("value")?.Value, "true", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The packaged module descriptor does not match this multiplayer build.");
        List<XElement> dependencies = module.Element("DependedModules")?.Elements("DependedModule").ToList() ?? [];
        if (dependencies.Select(element => element.Attribute("Id")?.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != dependencies.Count)
            throw new InvalidDataException("The module descriptor has duplicate dependencies.");
        foreach (string id in Profile.Modules.Take(3))
        {
            XElement? dependency = dependencies.SingleOrDefault(element => element.Attribute("Id")?.Value == id);
            string version = id == "MBWarlords.Armory" ? Profile.ArmoryVersion : Profile.GameVersion;
            if (dependency is null || dependency.Attribute("DependentVersion")?.Value != version || !string.Equals(dependency.Attribute("Optional")?.Value, "false", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The packaged module has an incompatible dependency: " + id);
        }
    }

    private static async Task<PackageLocation> GetPackageAsync(ChannelSource source, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        Uri? address = null;
        if (Uri.TryCreate(source.Release.Package, UriKind.Absolute, out Uri? absolute)) address = absolute;
        else if (Uri.TryCreate(source.Location, UriKind.Absolute, out Uri? channelUri) && channelUri.Scheme == Uri.UriSchemeHttps)
            address = new Uri(channelUri, source.Release.Package);
        if (address is null)
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(source.Location)) ?? throw new InvalidDataException("The local channel has no parent folder.");
            string path = SafeFiles.Under(folder, source.Release.Package);
            if (!File.Exists(path)) throw new FileNotFoundException("Keep the complete test-pack ZIP beside channel.json. Extract the entire launcher kit first.", path);
            return new PackageLocation(path, false);
        }
        if (address.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(address.UserInfo)) throw new InvalidDataException("The package address must use HTTPS.");
        string temporary = Path.Combine(Path.GetTempPath(), "Warlords-" + Guid.NewGuid().ToString("N") + ".warlords-download");
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(20));
            CancellationToken downloadToken = timeout.Token;
            using HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Warlords-Launcher/0.2.0");
            for (int redirects = 0; redirects < 6; redirects++)
            {
                using HttpResponseMessage response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, downloadToken).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    Uri next = response.Headers.Location is { } location ? new Uri(address, location) : throw new IOException("The download server returned an empty redirect.");
                    if (next.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(next.UserInfo)) throw new IOException("The download redirected away from a safe HTTPS address.");
                    address = next;
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } length && length != source.Release.PackageSize)
                    throw new InvalidDataException("The server package length does not match the channel.");
                await using Stream input = await response.Content.ReadAsStreamAsync(downloadToken).ConfigureAwait(false);
                await using FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous);
                byte[] buffer = new byte[131072];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, downloadToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > source.Release.PackageSize) throw new InvalidDataException("The server sent more data than the approved package.");
                    await output.WriteAsync(buffer.AsMemory(0, read), downloadToken).ConfigureAwait(false);
                    progress.Report(new("Downloading the matched test build", 0.25 * total / source.Release.PackageSize));
                }
                if (total != source.Release.PackageSize) throw new InvalidDataException("The package download ended early.");
                return new PackageLocation(temporary, true);
            }
            throw new IOException("The download server returned too many redirects.");
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    private static async Task ExtractAsync(string package, string stage, ReleaseChannel release, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        using ZipArchive archive = ZipFile.OpenRead(package);
        if (archive.Entries.Count > release.Files.Count * 2 + 100) throw new InvalidDataException("The ZIP contains too many entries.");
        Dictionary<string, ReleaseFile> expected = release.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        int completed = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string full = entry.FullName;
            if (full == "StratLords/") continue;
            if (!full.StartsWith("StratLords/", StringComparison.Ordinal)) throw new InvalidDataException("The ZIP must contain only the StratLords module folder.");
            string relative = full["StratLords/".Length..];
            bool directory = relative.EndsWith('/');
            if (directory) relative = relative[..^1];
            SafeFiles.Relative(relative);
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The ZIP contains a linked or special file.");
            if (!names.Add(relative)) throw new InvalidDataException("The ZIP contains duplicate paths.");
            if (directory)
            {
                if (entry.Length != 0 || !expected.Keys.Any(path => path.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("The ZIP contains an unexpected directory.");
                continue;
            }
            if (!expected.TryGetValue(relative, out ReleaseFile? file) || entry.Length != file.Size)
                throw new InvalidDataException("The ZIP does not match its approved file inventory: " + relative);
            string destination = SafeFiles.Under(stage, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using Stream input = entry.Open();
            await using (FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
            {
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[131072];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > file.Size) throw new InvalidDataException("A ZIP entry expands beyond its approved length.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                if (total != file.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(file.Sha256)))
                    throw new InvalidDataException("A ZIP file failed SHA-256 verification: " + relative);
            }
            progress.Report(new("Preparing verified files (" + (++completed) + " / " + release.Files.Count + ")", 0.25 + 0.65 * completed / release.Files.Count));
        }
        if (completed != release.Files.Count) throw new InvalidDataException("The ZIP is missing approved files.");
    }

    private static void WriteJournal(string modules, Journal journal)
    {
        string path = Path.Combine(modules, JournalName);
        string temporary = path + ".tmp";
        SafeFiles.NoLinks(path);
        SafeFiles.NoLinks(temporary);
        File.WriteAllText(temporary, JsonSerializer.Serialize(journal, Profile.Json));
        File.Move(temporary, path, true);
    }

    private static void RecoverCore(string modules)
    {
        string path = Path.Combine(modules, JournalName);
        SafeFiles.NoLinks(path);
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > 8192) throw new IOException("The interrupted-update record is unrecognized. Keep all recovery folders intact.");
        Journal journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(path), Profile.Json)
            ?? throw new IOException("The interrupted-update record is empty.");
        if (journal.SchemaVersion != 1 || journal.Transaction is null || !Regex.IsMatch(journal.Transaction, "^[0-9a-f]{32}$")
            || journal.BackupName != ".StratLords.warlords-backup-" + journal.Transaction || journal.Phase is not ("staging" or "prepared" or "backedUp" or "committed"))
            throw new IOException("The interrupted-update record is unsafe. Keep it intact and contact the owner.");
        string target = Path.Combine(modules, "StratLords");
        string recovery = RecoveryFolder(modules, false);
        string stage = Path.Combine(recovery, StageName);
        string backup = Path.Combine(recovery, journal.BackupName);
        SafeFiles.NoLinks(target);
        SafeFiles.NoLinks(stage);
        SafeFiles.NoLinks(backup);
        bool completed = Directory.Exists(target) && StateOwns(target, journal.Transaction);
        if (!completed && !Directory.Exists(target) && Directory.Exists(backup))
        {
            ValidateExistingTarget(backup);
            Directory.Move(backup, target);
        }
        else if (!completed && Directory.Exists(backup))
            throw new IOException("Both the installed module and recovery backup exist unexpectedly. Keep them intact and contact the owner.");
        else if (!completed && journal.TargetWasPresent && !Directory.Exists(target))
            throw new IOException("The previous module and its backup are missing. Keep all recovery files intact and contact the owner.");
        if (Directory.Exists(stage))
        {
            string owner = Path.Combine(stage, OwnerName);
            SafeFiles.NoLinks(owner);
            bool owned = File.Exists(owner) && new FileInfo(owner).Length <= 100 && File.ReadAllText(owner) == journal.Transaction;
            if (!owned && !StateOwns(stage, journal.Transaction)) throw new IOException("The staging folder is not owned by this update. Keep it intact and contact the owner.");
            _ = SafeFiles.TreeFiles(stage);
            Directory.Delete(stage, true);
        }
        File.Delete(path);
    }

    private static bool StateOwns(string folder, string transaction)
    {
        string path = Path.Combine(folder, StateName);
        SafeFiles.NoLinks(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 8192) return false;
        try { return JsonSerializer.Deserialize<InstalledState>(File.ReadAllText(path), Profile.Json)?.Transaction == transaction; }
        catch (JsonException) { return false; }
    }

    private static string RecoveryFolder(string modules, bool create)
    {
        // Native discovers direct children of Modules, so neither a staged
        // descriptor nor a retained backup may live there.
        string gameRoot = Path.GetDirectoryName(modules) ?? throw new IOException("The game installation has no parent folder.");
        string recovery = Path.Combine(gameRoot, ".warlords-recovery");
        SafeFiles.NoLinks(recovery);
        if (File.Exists(recovery)) throw new IOException("The launcher recovery path is occupied by a file. Keep it intact and contact the owner.");
        if (create)
        {
            Directory.CreateDirectory(recovery);
            SafeFiles.NoLinks(recovery);
        }
        return recovery;
    }

    private sealed record Journal(int SchemaVersion, string Transaction, string BackupName, string Phase, bool TargetWasPresent);
    private sealed record InstalledState(int SchemaVersion, string Version, string SourceCommit, string BuildId, string Transaction);
    private sealed record PackageLocation(string Path, bool Temporary);
}
