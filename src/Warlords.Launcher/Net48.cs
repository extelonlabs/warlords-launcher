using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace System.Runtime.CompilerServices
{
    // Lets C# records and init properties compile on .NET Framework.
    internal static class IsExternalInit { }
}

namespace Warlords.Launcher
{
    /// <summary>What .NET Framework 4.8 lacks and the launcher needs. Behavior matches the newer .NET calls these replace.</summary>
    internal static class Net48
    {
        private const string LibraryPrefix = "lib/";
        private static readonly Dictionary<string, Assembly> Libraries = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Loads the libraries embedded by the csproj, so self-update can keep swapping exactly one exe.</summary>
        public static void LoadEmbeddedLibraries() => AppDomain.CurrentDomain.AssemblyResolve += ResolveLibrary;

        private static Assembly? ResolveLibrary(object? sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            lock (Libraries)
            {
                if (Libraries.TryGetValue(name, out Assembly? loaded)) return loaded;
                using Stream? resource = typeof(Net48).Assembly.GetManifestResourceStream(LibraryPrefix + name + ".dll");
                if (resource is null) return null;
                using MemoryStream bytes = new();
                resource.CopyTo(bytes);
                // Libraries ask for each other by older versions. With no config file to redirect them, one copy answers all.
                return Libraries[name] = Assembly.Load(bytes.ToArray());
            }
        }

        /// <summary>Microsoft's documented test for 4.8. If the registry can't be read, the launcher still tries to run.</summary>
        public static bool IsNewEnough()
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
                return key?.GetValue("Release") is int release && release >= MinimumRelease;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return true;
            }
        }

        private const int MinimumRelease = 528040;

        public static string ToHex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");

        public static byte[] FromHex(string hex)
        {
            if (hex.Length % 2 != 0) throw new FormatException("A hex string must have an even length.");
            byte[] bytes = new byte[hex.Length / 2];
            for (int index = 0; index < bytes.Length; index++)
                bytes[index] = (byte)(HexDigit(hex[index * 2]) << 4 | HexDigit(hex[index * 2 + 1]));
            return bytes;
        }

        private static int HexDigit(char digit) => digit switch
        {
            >= '0' and <= '9' => digit - '0',
            >= 'a' and <= 'f' => digit - 'a' + 10,
            >= 'A' and <= 'F' => digit - 'A' + 10,
            _ => throw new FormatException("A hex string contains an invalid character.")
        };

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            int difference = 0;
            for (int index = 0; index < left.Length; index++) difference |= left[index] ^ right[index];
            return difference == 0;
        }

        /// <summary>Quotes one argument for Process.Start by the rules Windows uses to split a command line.</summary>
        public static string QuoteArgument(string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0) return argument;
            StringBuilder quoted = new("\"");
            int backslashes = 0;
            foreach (char character in argument)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }
                quoted.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes).Append(character);
                backslashes = 0;
            }
            return quoted.Append('\\', backslashes * 2).Append('"').ToString();
        }

        public static string TrimEndingSeparator(string path)
        {
            bool ends = path.Length > 0 && IsSeparator(path[path.Length - 1]);
            return ends && path.Length > Path.GetPathRoot(path).Length ? path.Substring(0, path.Length - 1) : path;
        }

        /// <summary>True for C:\x and \\server\x. False for C:x, \x and x, which depend on the current folder.</summary>
        public static bool IsPathFullyQualified(string path)
        {
            if (path.Length < 2) return false;
            if (IsSeparator(path[0])) return path[1] == '?' || IsSeparator(path[1]);
            return path.Length >= 3 && path[1] == ':' && IsSeparator(path[2]) && (path[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        }

        private static bool IsSeparator(char character) => character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar;

        public static Task<Stream> ReadAsStreamAsync(this HttpContent content, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return content.ReadAsStreamAsync();
        }

        /// <summary>A read that stops on cancel. .NET Framework checks the token only at the start, so a stuck download would never stop.</summary>
        public static async Task<int> ReadAsync(this Stream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            Task<int> read = stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
            TaskCompletionSource<bool> cancelled = new();
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(read, cancelled.Task).ConfigureAwait(false) != read)
                {
                    // Disposing the stream ends the abandoned read; its error has nowhere to go.
                    _ = read.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            return await read.ConfigureAwait(false);
        }

        public static bool StartsWith(this string value, char character) => value.Length > 0 && value[0] == character;

        public static bool EndsWith(this string value, char character) => value.Length > 0 && value[value.Length - 1] == character;

        public static bool TryAdd<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key, TValue value)
        {
            if (dictionary.ContainsKey(key)) return false;
            dictionary.Add(key, value);
            return true;
        }

        /// <summary>The modern Windows folder picker, the same one newer WPF wraps in OpenFolderDialog.</summary>
        public static string? PickFolder(Window owner, string title)
        {
            IFileOpenDialog dialog = (IFileOpenDialog)new FileOpenDialog();
            try
            {
                dialog.GetOptions(out uint options);
                dialog.SetOptions(options | PickFolders | ForceFileSystem | PathMustExist);
                dialog.SetTitle(title);
                int result = dialog.Show(new WindowInteropHelper(owner).Handle);
                if (result == Cancelled) return null;
                Marshal.ThrowExceptionForHR(result);
                dialog.GetResult(out IShellItem item);
                try
                {
                    item.GetDisplayName(FileSystemPath, out string path);
                    return path;
                }
                finally { Marshal.ReleaseComObject(item); }
            }
            finally { Marshal.ReleaseComObject(dialog); }
        }

        private const uint PickFolders = 0x20;
        private const uint ForceFileSystem = 0x40;
        private const uint PathMustExist = 0x800;
        private const uint FileSystemPath = 0x80058000;
        private const int Cancelled = unchecked((int)0x800704C7);

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialog { }

        // Method order is the Windows interface layout and must not change. Later methods are unused and left out.
        [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr owner);
            void SetFileTypes(uint count, IntPtr types);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IntPtr item);
            void SetFolder(IntPtr item);
            void GetFolder(out IntPtr item);
            void GetCurrentSelection(out IntPtr item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName(out IntPtr name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr context, ref Guid handler, ref Guid interfaceId, out IntPtr result);
            void GetParent(out IntPtr parent);
            void GetDisplayName(uint form, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        }
    }
}
