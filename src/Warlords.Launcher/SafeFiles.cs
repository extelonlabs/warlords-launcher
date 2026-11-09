using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Warlords.Launcher;

internal static class SafeFiles
{
    private static readonly Regex ReservedName = new(@"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Relative(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 1024 || value.Contains('\\') || value.StartsWith('/') || value.EndsWith('/'))
            throw new InvalidDataException("The package contains an invalid relative file path.");
        foreach (string part in value.Split('/'))
        {
            if (part.Length == 0 || part.Length > 255 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.Any(c => char.IsControl(c) || "<>:\"|?*".Contains(c)) || ReservedName.IsMatch(part)
                || part.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase)
                || part.Equals(".git", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The package contains an unsafe or excluded file path: " + value);
        }
        return value;
    }

    public static string Under(string root, string relative)
    {
        Relative(relative);
        string basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string result = Path.GetFullPath(Path.Combine(basePath, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A file path escaped the selected module.");
        NoLinks(result);
        return Long(result);
    }

    // .NET Framework does not lengthen paths for Windows by itself, and Windows stops at 260 characters
    // unless a path has this prefix. Most paths stay plain and get it at file calls. InstallService
    // starts from a prefixed Modules path, because the folders it moves and deletes run deep.
    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";
    private const int LongPathLength = 240;

    /// <summary>The path with the long-path prefix only when it needs one.</summary>
    public static string Long(string path)
    {
        string plain = Plain(path);
        return plain.Length < LongPathLength ? plain : Extended(plain);
    }

    /// <summary>Always prefixed. For calls that walk into a folder whose children may be long.</summary>
    public static string Extended(string path)
    {
        string full = Path.GetFullPath(Plain(path));
        if (full.StartsWith(ExtendedPrefix, StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal)) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? ExtendedUncPrefix + full.Substring(2) : ExtendedPrefix + full;
    }

    private static string Plain(string path)
    {
        if (path.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase)) return @"\\" + path.Substring(ExtendedUncPrefix.Length);
        // Only a drive path can lose the prefix. \\?\Volume{...}\ would become a relative path.
        bool drive = path.Length > 6 && path.StartsWith(ExtendedPrefix, StringComparison.Ordinal)
            && path[4] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' && path[5] == ':' && path[6] == '\\';
        return drive ? path.Substring(ExtendedPrefix.Length) : path;
    }

    /// <summary>The part of path below root. Refuses a path that is not inside root.</summary>
    public static string RelativeTo(string root, string path)
    {
        string basePath = Path.GetFullPath(Plain(root)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Plain(path));
        if (!full.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A file path escaped its folder: " + path);
        return full.Substring(basePath.Length);
    }

    /// <summary>Moves a file over an existing one in a single step, as newer .NET does with File.Move(source, destination, true).</summary>
    public static void MoveReplacing(string source, string destination)
    {
        if (MoveFileEx(Long(source), Long(destination), MoveFileReplaceExisting | MoveFileCopyAllowed)) return;
        int error = Marshal.GetLastWin32Error();
        int result = Marshal.GetHRForLastWin32Error();
        string message = new Win32Exception(error).Message;
        // The same exception families File.Move throws, which callers already catch.
        Exception failure = error switch
        {
            2 => new FileNotFoundException(message, source),
            3 => new DirectoryNotFoundException(message),
            5 => new UnauthorizedAccessException(message),
            _ => new IOException(message, result)
        };
        throw failure;
    }

    private const uint MoveFileReplaceExisting = 1;
    private const uint MoveFileCopyAllowed = 2;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true, EntryPoint = "MoveFileExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existing, string replacement, uint flags);

    public static void NoLinks(string path)
    {
        string? current = Path.GetFullPath(Plain(path));
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(Long(current)) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked folders and files are not supported: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    public static IReadOnlyList<string> TreeFiles(string root)
    {
        NoLinks(root);
        List<string> result = [];
        Stack<string> remaining = new();
        remaining.Push(root);
        while (remaining.Count > 0)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(Long(remaining.Pop())))
            {
                string path = Plain(entry);
                FileAttributes attributes = File.GetAttributes(Long(path));
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked files cannot be installed or verified: " + path);
                if ((attributes & FileAttributes.Directory) != 0) remaining.Push(path);
                else result.Add(path);
            }
        }
        return result;
    }

    public static string Modules(string gameRoot)
    {
        string root = Path.GetFullPath(gameRoot);
        NoLinks(root);
        if (!File.Exists(Path.Combine(root, "bin", "Win64_Shipping_Client", "Bannerlord.exe"))
            || !File.Exists(Path.Combine(root, "Modules", "Native", "SubModule.xml"))
            || !File.Exists(Path.Combine(root, "Modules", "Multiplayer", "SubModule.xml")))
            throw new IOException("Choose the Bannerlord installation folder containing bin and Modules.");
        string modules = Path.Combine(root, "Modules");
        NoLinks(modules);
        return modules;
    }

    public static XDocument ReadXml(string path)
    {
        NoLinks(path);
        if (new FileInfo(Long(path)).Length > 2 * 1024 * 1024) throw new InvalidDataException("The module descriptor is too large.");
        XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 };
        // XmlReader reads a path string as a web address, which breaks on the long-path prefix.
        using FileStream input = File.OpenRead(Long(path));
        using XmlReader reader = XmlReader.Create(input, settings);
        return XDocument.Load(reader);
    }

    public static async Task<bool> MatchesAsync(string path, long size, string sha256, CancellationToken cancellationToken)
    {
        NoLinks(path);
        if (!File.Exists(path) || new FileInfo(path).Length != size) return false;
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[131072];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            hash.AppendData(buffer, 0, read);
        return Net48.FixedTimeEquals(hash.GetHashAndReset(), Net48.FromHex(sha256));
    }
}
