using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        return result;
    }

    public static void NoLinks(string path)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
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
            foreach (string path in Directory.EnumerateFileSystemEntries(remaining.Pop()))
            {
                FileAttributes attributes = File.GetAttributes(path);
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
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("The module descriptor is too large.");
        XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 };
        using XmlReader reader = XmlReader.Create(path, settings);
        return XDocument.Load(reader);
    }

    public static async Task<bool> MatchesAsync(string path, long size, string sha256, CancellationToken cancellationToken)
    {
        NoLinks(path);
        if (!File.Exists(path) || new FileInfo(path).Length != size) return false;
        await using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] actual = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(sha256));
    }
}
