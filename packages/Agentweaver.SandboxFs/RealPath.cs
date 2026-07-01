using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Agentweaver.SandboxFs;

/// <summary>
/// Resolves symbolic links, junctions, and 8.3 short names to the final
/// canonical path. Uses GetFinalPathNameByHandle on Windows and
/// realpath(3) P/Invoke on Unix (Linux &amp; macOS).
/// </summary>
/// <remarks>
/// This remains public package API because the API layer's repository-root
/// validation uses it to compare configured allowlist roots against submitted
/// repository paths after symlinks and junctions are resolved.
/// </remarks>
public static class RealPath
{
    /// <summary>
    /// Returns the fully resolved real path for <paramref name="absolutePath"/>,
    /// following all symlinks, junctions, and normalizing 8.3 short names.
    /// Throws <see cref="IOException"/> if the path cannot be resolved
    /// (e.g., the target does not exist or is inaccessible).
    /// </summary>
    public static string Resolve(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            throw new IOException("Cannot resolve an empty path.");

        if (OperatingSystem.IsWindows())
            return ResolveWindows(absolutePath);

        return ResolveUnix(absolutePath);
    }

    [SupportedOSPlatform("windows")]
    private static string ResolveWindows(string path)
    {
        // Open the path with FILE_FLAG_BACKUP_SEMANTICS to allow opening directories.
        const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        const uint GENERIC_READ = 0x80000000;
        const uint FILE_SHARE_ALL = 0x00000001 | 0x00000002 | 0x00000004;
        const uint OPEN_EXISTING = 3;

        using var handle = CreateFileW(
            path,
            GENERIC_READ,
            FILE_SHARE_ALL,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        if (handle.IsInvalid)
            throw new IOException($"Cannot resolve path: unable to open handle (error {Marshal.GetLastWin32Error()}).");

        const uint FILE_NAME_NORMALIZED = 0x0;
        var sb = new StringBuilder(32768);
        uint result = GetFinalPathNameByHandleW(handle.DangerousGetHandle(), sb, (uint)sb.Capacity, FILE_NAME_NORMALIZED);
        if (result == 0)
            throw new IOException($"Cannot resolve path: GetFinalPathNameByHandle failed (error {Marshal.GetLastWin32Error()}).");

        var resolved = sb.ToString();

        // Strip the \\?\ extended-length prefix if present.
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            return @"\\" + resolved[8..];
        if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal))
            return resolved[4..];

        return resolved;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    [SupportedOSPlatform("windows")]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW")]
    [SupportedOSPlatform("windows")]
    private static extern uint GetFinalPathNameByHandleW(
        IntPtr hFile,
        StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    [DllImport("libc", SetLastError = true, EntryPoint = "realpath")]
    private static extern IntPtr unix_realpath(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        IntPtr resolvedPath);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void unix_free(IntPtr ptr);

    private static string ResolveUnix(string path)
    {
        // Pass IntPtr.Zero so glibc/macOS libc allocate the result buffer (POSIX.1-2008);
        // we must free it. realpath resolves directories and follows all symlinks.
        var ptr = unix_realpath(path, IntPtr.Zero);
        if (ptr == IntPtr.Zero)
            throw new IOException($"Cannot resolve path (errno {Marshal.GetLastPInvokeError()}).");
        try
        {
            return Marshal.PtrToStringUTF8(ptr)
                ?? throw new IOException("Cannot resolve path: null result.");
        }
        finally
        {
            unix_free(ptr);
        }
    }
}
