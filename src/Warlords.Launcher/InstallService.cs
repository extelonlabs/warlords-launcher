using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
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
        if (release is null) throw new ArgumentNullException(nameof(release));
        if (release.SchemaVersion != 1 || (release.Branch != Profile.Branch && release.Branch != Profile.PreviousBranch) || release.SourceRepository != Profile.Repository
            || release.RequiredGameVersion != Profile.GameVersion || release.RequiredArmoryVersion != Profile.ArmoryVersion)
            throw new InvalidDataException("This update is not for your version of the game. Check for a newer launcher.");
        if (release.SourceCommit is null || !Commit.IsMatch(release.SourceCommit) || release.Version is null || !ModuleVersion.IsMatch(release.Version)
            || string.IsNullOrWhiteSpace(release.BuildId) || release.BuildId.Length > 200 || release.BuildId.Any(char.IsControl))
            throw new InvalidDataException("The update information is damaged. Try again later.");
        if (release.CreatedUtc == default || release.CreatedUtc > DateTimeOffset.UtcNow.AddMinutes(10)
            || (release.ExpiresUtc is { } expiry && (expiry <= DateTimeOffset.UtcNow || expiry <= release.CreatedUtc)))
            throw new InvalidDataException("This update has expired. Try again later.");
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
            throw new InvalidDataException("The update information is incomplete. Try again later.");
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
                if (paths.Contains(string.Join("/", segments.Take(index))))
                    throw new InvalidDataException("The package treats a file as a folder.");
        }
        if (RequiredFiles.Any(path => !paths.Contains(path) || release.Files.Single(file => file.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).Size == 0))
            throw new InvalidDataException("The package is missing the module descriptor or a required client assembly.");
    }

    public static IDisposable AcquireLock(string gameRoot)
    {
        string modules = SafeFiles.Extended(SafeFiles.Modules(gameRoot));
        string lockPath = Path.Combine(modules, ".StratLords.warlords-update.lock");
        SafeFiles.NoLinks(lockPath);
        try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new IOException("Another Warlords launcher is updating or starting this installation. Close it and try again.", error); }
    }

    /// <summary>Finishes cleaning up after a killed update. Returns false when there was nothing to do.</summary>
    public static bool Recover(string gameRoot)
    {
        string modules = SafeFiles.Extended(SafeFiles.Modules(gameRoot));
        // Checked first, so a running game is no reason to stop when nothing needs recovery.
        if (!File.Exists(Path.Combine(modules, JournalName))) return false;
        using IDisposable exclusive = AcquireLock(gameRoot);
        GameService.EnsureClosed();
        RecoverCore(modules);
        return true;
    }

    public async Task<InstallResult> InstallAsync(string gameRoot, ChannelSource source, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ValidateRelease(source.Release);
        using IDisposable exclusive = AcquireLock(gameRoot);
        progress.Report(new("Making sure Bannerlord is closed", Step: "closed"));
        GameService.EnsureClosed();
        string modules = SafeFiles.Extended(SafeFiles.Modules(gameRoot));
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
            if (!await TryPatchAsync(target, stage, source, progress, cancellationToken).ConfigureAwait(false))
            {
                PackageLocation packageLocation = await GetPackageAsync(source, progress, cancellationToken).ConfigureAwait(false);
                string package = packageLocation.Path;
                if (packageLocation.Temporary) downloadedPackage = package;
                progress.Report(new("Checking the download", 0.82, Step: "check"));
                if (!await SafeFiles.MatchesAsync(package, source.Release.PackageSize, source.Release.PackageSha256, cancellationToken).ConfigureAwait(false))
                    throw new InvalidDataException("The download is damaged or incomplete. Choose Try again to download it again.");
                await ExtractAsync(package, stage, source.Release, progress, cancellationToken).ConfigureAwait(false);
            }
            ValidateDescriptor(stage, source.Release);
            string oldOptions = SafeFiles.Under(target, OptionsPath);
            if (File.Exists(oldOptions))
            {
                progress.Report(new("Keeping your settings", 0.97, Step: "settings"));
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
            string backup = Path.Combine(recovery, journal.BackupName);
            SafeFiles.NoLinks(backup);
            if (File.Exists(backup) || Directory.Exists(backup)) throw new IOException("The backup destination already exists. Nothing was replaced.");
            if (Directory.Exists(target))
            {
                progress.Report(new("Saving a backup of the old version", 0.98, Step: "backup"));
                Directory.Move(target, backup);
            }
            WriteJournal(modules, journal with { Phase = "backedUp" });
            progress.Report(new("Installing Warlords", 0.99, Step: "swap"));
            Directory.Move(stage, target);
            WriteJournal(modules, journal with { Phase = "committed" });
            File.Delete(Path.Combine(modules, JournalName));
            // Every file was checked against its SHA-256 on the way in, so the next start or Play need not read them again.
            try
            {
                if (Stamp(target, source.Release) is { } stamp) WriteStamp(stamp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                App.Log("The file check stamp could not be saved: " + ex.Message);
            }
            if (journal.TargetWasPresent) RemoveOlderBackups(recovery, journal.BackupName);
            progress.Report(new("Installed", 1));
            return new InstallResult(SafeFiles.Long(target), journal.TargetWasPresent ? SafeFiles.Long(backup) : null);
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

    public async Task<bool> VerifyAsync(string gameRoot, ReleaseChannel release, IProgress<OperationProgress> progress, CancellationToken cancellationToken, bool fullCheck)
    {
        ValidateRelease(release);
        using IDisposable exclusive = AcquireLock(gameRoot);
        return await VerifyCoreAsync(gameRoot, release, progress, cancellationToken, fullCheck).ConfigureAwait(false);
    }

    public async Task<bool> VerifyAndRunAsync(string gameRoot, ReleaseChannel release, IProgress<OperationProgress> progress, CancellationToken cancellationToken, Action whenValid)
    {
        if (whenValid is null) throw new ArgumentNullException(nameof(whenValid));
        ValidateRelease(release);
        using IDisposable exclusive = AcquireLock(gameRoot);
        if (!await VerifyCoreAsync(gameRoot, release, progress, cancellationToken, false).ConfigureAwait(false)) return false;
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRelease(release);
        whenValid();
        return true;
    }

    // A quick check compares sizes and dates with the stamp of the last full check. It can only pass
    // or fall through to the full check, never fail on its own: a failed check starts a reinstall.
    private static async Task<bool> VerifyCoreAsync(string gameRoot, ReleaseChannel release, IProgress<OperationProgress> progress, CancellationToken cancellationToken, bool fullCheck)
    {
        GameService.EnsureClosed();
        string modules = SafeFiles.Extended(SafeFiles.Modules(gameRoot));
        SafeFiles.NoLinks(Path.Combine(modules, JournalName));
        if (File.Exists(Path.Combine(modules, JournalName))) throw new IOException("An interrupted update needs recovery. Choose Check again.");
        string target = Path.Combine(modules, "StratLords");
        if (!Directory.Exists(target)) return false;
        ValidateExistingTarget(target);
        Dictionary<string, ReleaseFile> expected = release.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        HashSet<string> actual = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in SafeFiles.TreeFiles(target))
        {
            string relative = SafeFiles.RelativeTo(target, path).Replace('\\', '/');
            if (!actual.Add(relative)) return false;
            if (relative.Equals(StateName, StringComparison.OrdinalIgnoreCase) || relative.Equals(OptionsPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (!expected.ContainsKey(relative)) return false;
        }
        // An install of another build needs the update anyway, so reading every file first only wastes time.
        if (InstalledBuild(target) is { } installed && (installed.SourceCommit != release.SourceCommit || installed.BuildId != release.BuildId)) return false;
        // Read before any hashing, so a file that changes during the check is never stamped as good.
        string? stamp = Stamp(target, release);
        if (!fullCheck && stamp is not null && ReadStamp() == stamp)
        {
            ValidateDescriptor(target, release);
            progress.Report(new("Your files have not changed since the last full check", 1));
            return true;
        }
        DeleteStamp();
        int count = 0;
        foreach (ReleaseFile file in release.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!file.Path.Equals(OptionsPath, StringComparison.OrdinalIgnoreCase)
                && !await SafeFiles.MatchesAsync(SafeFiles.Under(target, file.Path), file.Size, file.Sha256, cancellationToken).ConfigureAwait(false)) return false;
            progress.Report(new("Checking your files (" + (++count) + " / " + release.Files.Count + ")", (double)count / release.Files.Count));
        }
        ValidateDescriptor(target, release);
        if (stamp is not null) WriteStamp(stamp);
        return true;
    }

    private static string StampPath => Path.Combine(App.DataDirectory, "verified-files.txt");

    // One line that changes when the folder, the build, or any file's size or date changes.
    private static string? Stamp(string target, ReleaseChannel release)
    {
        StringBuilder text = new();
        text.Append(SafeFiles.Long(target)).Append('\n').Append(release.SourceCommit).Append('\n').Append(release.BuildId).Append('\n');
        foreach (ReleaseFile file in release.Files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (file.Path.Equals(OptionsPath, StringComparison.OrdinalIgnoreCase)) continue;
            FileInfo info = new(SafeFiles.Under(target, file.Path));
            if (!info.Exists || info.Length != file.Size) return null;
            text.Append(file.Path).Append('|').Append(file.Size).Append('|').Append(file.Sha256).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
        }
        using SHA256 sha = SHA256.Create();
        return Net48.ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static string? ReadStamp()
    {
        try { return File.Exists(StampPath) ? File.ReadAllText(StampPath).Trim() : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void WriteStamp(string stamp)
    {
        try
        {
            Directory.CreateDirectory(App.DataDirectory);
            File.WriteAllText(StampPath, stamp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log("The file check stamp could not be saved: " + ex.Message);
        }
    }

    private static void DeleteStamp()
    {
        try { File.Delete(StampPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log("The file check stamp could not be removed: " + ex.Message);
        }
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
                || SafeFiles.RelativeTo(target, path).Split(Path.DirectorySeparatorChar).Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
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

    private static Uri? RemoteAddress(ChannelSource source)
    {
        if (Uri.TryCreate(source.Release.Package, UriKind.Absolute, out Uri? absolute)) return absolute;
        if (Uri.TryCreate(source.Location, UriKind.Absolute, out Uri? channelUri) && channelUri.Scheme == Uri.UriSchemeHttps)
            return new Uri(channelUri, source.Release.Package);
        return null;
    }

    // An update keeps the installed files that did not change and downloads only the changed ones,
    // reading just their bytes out of the ZIP with HTTP ranges. Every file is still checked against
    // its SHA-256. Any surprise empties the stage and returns false, and the whole ZIP is downloaded.
    private static async Task<bool> TryPatchAsync(string target, string stage, ChannelSource source, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ReleaseChannel release = source.Release;
        Uri? address = RemoteAddress(source);
        if (address is null || address.Scheme != Uri.UriSchemeHttps || !Directory.Exists(target)) return false;
        try
        {
            using HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Warlords-Launcher/0.2.0");
            // Read the ZIP's table of contents first, so a server without ranges costs nothing.
            (Dictionary<string, ZipEntry> entries, List<long> starts) = await ReadZipDirectoryAsync(client, address, release, cancellationToken).ConfigureAwait(false);
            List<ReleaseFile> changed = [];
            int done = 0;
            foreach (ReleaseFile file in release.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await CopyIfSameAsync(SafeFiles.Under(target, file.Path), SafeFiles.Under(stage, file.Path), file, cancellationToken).ConfigureAwait(false))
                    changed.Add(file);
                progress.Report(new("Keeping your unchanged files (" + (++done) + " / " + release.Files.Count + ")", 0.6 * done / release.Files.Count, Step: "download"));
            }
            await DownloadChangedAsync(client, address, release, entries, starts, changed, stage, progress, cancellationToken).ConfigureAwait(false);
            App.Log("Kept " + (release.Files.Count - changed.Count) + " unchanged files and downloaded " + changed.Count + " changed files.");
            return true;
        }
        // A timed-out request also throws a cancellation; only the player's Cancel stops the update.
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            App.Log("Downloading only the changed files did not work, so the whole update is downloaded: " + ex.Message);
            // The stage and its owner file stay, so recovery still knows the stage is ours if this stops halfway.
            _ = SafeFiles.TreeFiles(stage); // Refuses a folder with links inside.
            foreach (string entry in Directory.EnumerateFileSystemEntries(SafeFiles.Extended(stage)).ToList())
            {
                if (Path.GetFileName(entry).Equals(OwnerName, StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.Exists(entry)) Directory.Delete(entry, true);
                else File.Delete(entry);
            }
            return false;
        }
    }

    // Copies an installed file into the stage while hashing it. Returns false, keeping nothing, when it differs.
    private static async Task<bool> CopyIfSameAsync(string installed, string destination, ReleaseFile file, CancellationToken cancellationToken)
    {
        if (!File.Exists(installed) || new FileInfo(installed).Length != file.Size) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        bool same;
        using (FileStream input = new(installed, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
        using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            byte[] buffer = new byte[131072];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > file.Size) break;
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
            }
            same = total == file.Size && Net48.FixedTimeEquals(hash.GetHashAndReset(), Net48.FromHex(file.Sha256));
        }
        if (!same) File.Delete(destination);
        return same;
    }

    private sealed record ZipEntry(long Offset, long CompressedSize, long Size, int Method);

    // Reads the central directory: the list at the end of a ZIP of every file and where its bytes start.
    private static async Task<(Dictionary<string, ZipEntry> Entries, List<long> Starts)> ReadZipDirectoryAsync(HttpClient client, Uri address, ReleaseChannel release, CancellationToken cancellationToken)
    {
        long packageSize = release.PackageSize;
        int tailLength = (int)Math.Min(packageSize, 65536 + 22);
        byte[] tail = await GetRangeAsync(client, address, packageSize, packageSize - tailLength, tailLength, cancellationToken).ConfigureAwait(false);
        int end = -1;
        for (int index = tail.Length - 22; index >= 0 && end < 0; index--)
            if (BitConverter.ToUInt32(tail, index) == 0x06054b50) end = index;
        if (end < 0) throw new InvalidDataException("The ZIP has no end record.");
        int count = BitConverter.ToUInt16(tail, end + 10);
        long directorySize = BitConverter.ToUInt32(tail, end + 12);
        long directoryOffset = BitConverter.ToUInt32(tail, end + 16);
        // A ZIP64 or split archive marks these fields; it goes the safe whole-file way.
        if (BitConverter.ToUInt16(tail, end + 4) != 0 || BitConverter.ToUInt16(tail, end + 6) != 0 || BitConverter.ToUInt16(tail, end + 8) != count
            || count == 0xFFFF || directorySize == 0xFFFFFFFF || directoryOffset == 0xFFFFFFFF
            || directorySize > 16 * 1024 * 1024 || directoryOffset + directorySize > packageSize - tailLength + end)
            throw new InvalidDataException("The ZIP's file list is not supported for partial downloads.");
        byte[] directory = await GetRangeAsync(client, address, packageSize, directoryOffset, (int)directorySize, cancellationToken).ConfigureAwait(false);
        Dictionary<string, ZipEntry> entries = new(StringComparer.OrdinalIgnoreCase);
        int position = 0;
        for (int index = 0; index < count; index++)
        {
            if (position + 46 > directory.Length || BitConverter.ToUInt32(directory, position) != 0x02014b50) throw new InvalidDataException("The ZIP's file list is damaged.");
            int flags = BitConverter.ToUInt16(directory, position + 8);
            int method = BitConverter.ToUInt16(directory, position + 10);
            long compressed = BitConverter.ToUInt32(directory, position + 20);
            long size = BitConverter.ToUInt32(directory, position + 24);
            int nameLength = BitConverter.ToUInt16(directory, position + 28);
            int skip = nameLength + BitConverter.ToUInt16(directory, position + 30) + BitConverter.ToUInt16(directory, position + 32);
            long offset = BitConverter.ToUInt32(directory, position + 42);
            if (position + 46 + skip > directory.Length) throw new InvalidDataException("The ZIP's file list is damaged.");
            byte[] nameBytes = new byte[nameLength];
            Array.Copy(directory, position + 46, nameBytes, 0, nameLength);
            // Names are UTF-8 when flag 11 is set; otherwise only plain ASCII names are safe to read.
            if ((flags & 0x800) == 0 && nameBytes.Any(value => value >= 0x80)) throw new InvalidDataException("The ZIP has a file name in an old encoding.");
            string name = Encoding.UTF8.GetString(nameBytes);
            position += 46 + skip;
            if (name.EndsWith('/')) continue;
            if (!name.StartsWith("StratLords/", StringComparison.Ordinal)) throw new InvalidDataException("The ZIP must contain only the StratLords module folder.");
            if ((flags & 1) != 0 || method is not (0 or 8) || (method == 0 && compressed != size) || offset >= directoryOffset)
                throw new InvalidDataException("The ZIP stores a file in an unsupported way.");
            if (!entries.TryAdd(name.Substring("StratLords/".Length), new ZipEntry(offset, compressed, size, method)))
                throw new InvalidDataException("The ZIP contains duplicate paths.");
        }
        foreach (ReleaseFile file in release.Files)
            if (!entries.TryGetValue(file.Path, out ZipEntry? entry) || entry.Size != file.Size)
                throw new InvalidDataException("The ZIP does not match its approved file inventory: " + file.Path);
        // Each entry's bytes end where the next entry starts, so one range read covers its header and data.
        List<long> starts = entries.Values.Select(entry => entry.Offset).Concat(new[] { directoryOffset }).Distinct().OrderBy(offset => offset).ToList();
        if (starts.Count != entries.Count + 1) throw new InvalidDataException("The ZIP has overlapping files.");
        return (entries, starts);
    }

    private static async Task DownloadChangedAsync(HttpClient client, Uri address, ReleaseChannel release, Dictionary<string, ZipEntry> entries, List<long> starts, List<ReleaseFile> changed,
        string stage, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        if (changed.Count == 0) return;
        const long Gap = 256 * 1024, MaximumRange = 64L * 1024 * 1024;
        // Neighbouring files are fetched together; a gap is cheaper to download than another request.
        List<(long Start, long End, List<ReleaseFile> Files)> ranges = [];
        foreach (ReleaseFile file in changed.OrderBy(file => entries[file.Path].Offset))
        {
            ZipEntry entry = entries[file.Path];
            // The last start is the file list itself, so every entry has a next start.
            long end = starts[starts.BinarySearch(entry.Offset) + 1];
            if (end - entry.Offset > MaximumRange) throw new InvalidDataException("A changed file is too large for a partial download.");
            int last = ranges.Count - 1;
            if (last >= 0 && entry.Offset - ranges[last].End <= Gap && end - ranges[last].Start <= MaximumRange)
            {
                ranges[last].Files.Add(file);
                ranges[last] = (ranges[last].Start, end, ranges[last].Files);
            }
            else ranges.Add((entry.Offset, end, [file]));
        }
        if (ranges.Count > 300) throw new InvalidDataException("Too many separate changes for a partial download.");
        long total = ranges.Sum(range => range.End - range.Start), received = 0;
        int written = 0;
        foreach ((long start, long end, List<ReleaseFile> files) in ranges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = await GetRangeAsync(client, address, release.PackageSize, start, (int)(end - start), cancellationToken).ConfigureAwait(false);
            foreach (ReleaseFile file in files)
            {
                ZipEntry entry = entries[file.Path];
                int header = (int)(entry.Offset - start);
                if (header + 30 > bytes.Length || BitConverter.ToUInt32(bytes, header) != 0x04034b50) throw new InvalidDataException("A ZIP file header is damaged: " + file.Path);
                long data = header + 30L + BitConverter.ToUInt16(bytes, header + 26) + BitConverter.ToUInt16(bytes, header + 28);
                if (data + entry.CompressedSize > bytes.Length) throw new InvalidDataException("A ZIP file runs past its range: " + file.Path);
                string destination = SafeFiles.Under(stage, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using Stream packed = new MemoryStream(bytes, (int)data, (int)entry.CompressedSize, false);
                using Stream input = entry.Method == 8 ? new DeflateStream(packed, CompressionMode.Decompress) : packed;
                using (FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
                {
                    using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    byte[] buffer = new byte[131072];
                    long length = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        length += read;
                        if (length > file.Size) throw new InvalidDataException("A ZIP entry expands beyond its approved length.");
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                    }
                    if (length != file.Size || !Net48.FixedTimeEquals(hash.GetHashAndReset(), Net48.FromHex(file.Sha256)))
                        throw new InvalidDataException("A downloaded file failed SHA-256 verification: " + file.Path);
                }
                written++;
            }
            received += end - start;
            progress.Report(new("Downloading changed files (" + written + " / " + changed.Count + ")", 0.6 + 0.37 * received / total, received, total, "download"));
        }
    }

    // Reads exactly length bytes at offset. Only a 206 reply for that exact range is accepted.
    private static async Task<byte[]> GetRangeAsync(HttpClient client, Uri address, long packageSize, long offset, int length, CancellationToken cancellationToken)
    {
        // HttpClient's own timeout stops at the headers; this one also ends a stalled body read.
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        CancellationToken token = timeout.Token;
        for (int redirects = 0; redirects < 6; redirects++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, address);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + length - 1);
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or (HttpStatusCode)308)
            {
                Uri next = response.Headers.Location is { } location ? new Uri(address, location) : throw new IOException("The download server returned an empty redirect.");
                if (next.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(next.UserInfo)) throw new IOException("The download redirected away from a safe HTTPS address.");
                address = next;
                continue;
            }
            System.Net.Http.Headers.ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
            if (response.StatusCode != HttpStatusCode.PartialContent || range is null || range.From != offset || range.To != offset + length - 1 || range.Length != packageSize)
                throw new IOException("The download server does not support partial downloads.");
            using Stream input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            byte[] bytes = new byte[length];
            byte[] buffer = new byte[131072];
            int total = 0, read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (read > length - total) throw new IOException("The server sent more data than the requested range.");
                Array.Copy(buffer, 0, bytes, total, read);
                total += read;
            }
            if (total != length) throw new IOException("A partial download ended early.");
            return bytes;
        }
        throw new IOException("The download server returned too many redirects.");
    }

    private static async Task<PackageLocation> GetPackageAsync(ChannelSource source, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        Uri? address = RemoteAddress(source);
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
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or (HttpStatusCode)308)
                {
                    Uri next = response.Headers.Location is { } location ? new Uri(address, location) : throw new IOException("The download server returned an empty redirect.");
                    if (next.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(next.UserInfo)) throw new IOException("The download redirected away from a safe HTTPS address.");
                    address = next;
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } length && length != source.Release.PackageSize)
                    throw new InvalidDataException("The server package length does not match the channel.");
                using Stream input = await response.Content.ReadAsStreamAsync(downloadToken).ConfigureAwait(false);
                using FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous);
                byte[] buffer = new byte[131072];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, downloadToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > source.Release.PackageSize) throw new InvalidDataException("The server sent more data than the approved package.");
                    await output.WriteAsync(buffer, 0, read, downloadToken).ConfigureAwait(false);
                    progress.Report(new("Downloading Warlords", 0.8 * total / source.Release.PackageSize, total, source.Release.PackageSize, "download"));
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
            string relative = full.Substring("StratLords/".Length);
            bool directory = relative.EndsWith('/');
            if (directory) relative = relative.Substring(0, relative.Length - 1);
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
            using Stream input = entry.Open();
            using (FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
            {
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[131072];
                long total = 0;
                int read;
                // The token is checked at each read. A read must never be abandoned here: disposing
                // the ZIP stream during one would free native memory it is still using.
                while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > file.Size) throw new InvalidDataException("A ZIP entry expands beyond its approved length.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                }
                if (total != file.Size || !Net48.FixedTimeEquals(hash.GetHashAndReset(), Net48.FromHex(file.Sha256)))
                    throw new InvalidDataException("A ZIP file failed SHA-256 verification: " + relative);
            }
            progress.Report(new("Unpacking files (" + (++completed) + " / " + release.Files.Count + ")", 0.82 + 0.15 * completed / release.Files.Count, Step: "unpack"));
        }
        if (completed != release.Files.Count) throw new InvalidDataException("The ZIP is missing approved files.");
    }

    // A backup is a full copy of the module, so only the newest stays. The update already worked, so
    // a failure here must not undo it: whatever can't be removed now goes at the next update.
    private static void RemoveOlderBackups(string recovery, string keep)
    {
        string[] folders;
        try { folders = Directory.GetDirectories(recovery, ".StratLords.warlords-backup-*"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log("Older backups could not be listed: " + ex.Message);
            return;
        }
        foreach (string folder in folders)
        {
            if (Path.GetFileName(folder).Equals(keep, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                _ = SafeFiles.TreeFiles(folder); // Refuses a folder with links inside.
                Directory.Delete(SafeFiles.Extended(folder), true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                App.Log("An older backup could not be removed: " + ex.Message);
            }
        }
    }

    private static void WriteJournal(string modules, Journal journal)
    {
        string path = Path.Combine(modules, JournalName);
        string temporary = path + ".tmp";
        SafeFiles.NoLinks(path);
        SafeFiles.NoLinks(temporary);
        File.WriteAllText(temporary, JsonSerializer.Serialize(journal, Profile.Json));
        SafeFiles.MoveReplacing(temporary, path);
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
            // Empty: stopped before the owner file was written, and there is nothing to lose.
            bool empty = !Directory.EnumerateFileSystemEntries(stage).Any();
            if (!owned && !empty && !StateOwns(stage, journal.Transaction)) throw new IOException("The staging folder is not owned by this update. Keep it intact and contact the owner.");
            _ = SafeFiles.TreeFiles(stage);
            Directory.Delete(SafeFiles.Extended(stage), true);
        }
        File.Delete(path);
    }

    private static InstalledState? InstalledBuild(string folder)
    {
        string path = Path.Combine(folder, StateName);
        SafeFiles.NoLinks(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 8192) return null;
        try { return JsonSerializer.Deserialize<InstalledState>(File.ReadAllText(path), Profile.Json); }
        catch (JsonException) { return null; }
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
