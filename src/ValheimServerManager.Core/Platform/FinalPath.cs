using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ValheimServerManager.Core.Platform;

/// <summary>
/// The path Windows really opens for a folder: junctions, symbolic links, subst drives and 8.3 names
/// resolved. Two spellings of the game's save folder must never pass as two different folders.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class FinalPath
{
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    /// <summary>
    /// Resolves the deepest existing ancestor of <paramref name="fullPath"/> and appends the part that
    /// does not exist yet. Falls back to the input when Windows cannot say.
    /// </summary>
    public static string Resolve(string fullPath)
    {
        var existing = fullPath;
        var missing = new Stack<string>();
        while (!Directory.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (parent is null)
            {
                return fullPath;
            }

            missing.Push(Path.GetFileName(existing));
            existing = parent;
        }

        var resolved = Query(existing);
        if (resolved is null)
        {
            return fullPath;
        }

        return missing.Count == 0 ? resolved : Path.Combine([resolved, .. missing]);
    }

    private static string? Query(string directory)
    {
        using var handle = CreateFileW(directory, FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        const int capacity = 1024;
        string path;
        unsafe
        {
            var buffer = stackalloc char[capacity];
            var length = GetFinalPathNameByHandleW(handle, buffer, capacity, 0);
            if (length is 0 or >= capacity)
            {
                return null;
            }

            path = new string(buffer, 0, (int)length);
        }

        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandleW(SafeFileHandle file, char* buffer, uint bufferLength, uint flags);
}
