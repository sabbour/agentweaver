using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Identity;
using Microsoft.Win32.SafeHandles;

namespace Agentweaver.AgentRuntime;

internal interface IRuntimeWorkspaceMountObserver
{
    RuntimeHostWorkspaceMountEvidence Observe(
        string mountPath, bool expectedReadOnly, string podUid, DateTimeOffset observedAt);
}

internal sealed class LinuxRuntimeWorkspaceMountObserver : IRuntimeWorkspaceMountObserver
{
    private const int ODirectory = 0x10000;
    private const int ONoFollow = 0x20000;
    private const int OCloexec = 0x80000;
    private const int OPath = 0x200000;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxMountId = 0x1000;
    private const uint StatxInode = 0x100;

    public RuntimeHostWorkspaceMountEvidence Observe(
        string mountPath, bool expectedReadOnly, string podUid, DateTimeOffset observedAt)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Workspace mount evidence requires Linux.");
        ArgumentException.ThrowIfNullOrWhiteSpace(mountPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(podUid);
        if (podUid.Length > 256 || podUid.Any(char.IsControl) || observedAt == default)
            throw new RuntimeAuthorizationException("runtime_workspace_mount_observation_invalid");

        var fullPath = Path.GetFullPath(mountPath);
        if (!Directory.Exists(fullPath) ||
            (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new RuntimeAuthorizationException("runtime_workspace_mount_unavailable");

        var descriptor = OpenDirectory(fullPath, OPath | ODirectory | ONoFollow | OCloexec);
        if (descriptor < 0)
            throw new IOException(
                "The configured workspace directory could not be opened for mount observation.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        using var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);

        // Pair the mount table with the opened directory identity so a path-only check cannot pass across a remount.
        var statx = new LinuxStatx();
        if (Statx(descriptor, string.Empty, AtEmptyPath, StatxMountId | StatxInode, ref statx) != 0)
            throw new IOException(
                "The configured workspace directory identity could not be observed.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        if ((statx.Mask & (StatxMountId | StatxInode)) != (StatxMountId | StatxInode))
            throw new RuntimeAuthorizationException("runtime_workspace_mount_identity_unavailable");

        var mountInfo = File.ReadAllText("/proc/self/mountinfo", Encoding.UTF8);
        return CreateReceipt(mountInfo, fullPath, expectedReadOnly, podUid, observedAt,
            statx.MountId, statx.DeviceMajor, statx.DeviceMinor, statx.Inode);
    }

    internal static RuntimeHostWorkspaceMountEvidence CreateReceipt(
        string mountInfo,
        string mountPath,
        bool expectedReadOnly,
        string podUid,
        DateTimeOffset observedAt,
        ulong directoryMountId,
        uint directoryDeviceMajor,
        uint directoryDeviceMinor,
        ulong directoryInode)
    {
        ArgumentNullException.ThrowIfNull(mountInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(mountPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(podUid);
        if (directoryMountId == 0 || directoryInode == 0 || observedAt == default)
            throw new RuntimeAuthorizationException("runtime_workspace_mount_identity_unavailable");

        var entries = mountInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseLine)
            .ToArray();
        var exact = entries.Where(entry => entry.MountPoint == mountPath).ToArray();
        var root = entries.Where(entry => entry.MountPoint == "/").ToArray();
        if (exact.Length != 1 || root.Length != 1 ||
            exact[0].MountId == root[0].MountId ||
            exact[0].MountId != directoryMountId ||
            exact[0].DeviceMajor != directoryDeviceMajor ||
            exact[0].DeviceMinor != directoryDeviceMinor ||
            exact[0].ReadOnly != expectedReadOnly)
            throw new RuntimeAuthorizationException("runtime_workspace_mount_mismatch");

        return new(1, observedAt, podUid, mountPath, directoryMountId,
            directoryDeviceMajor, directoryDeviceMinor, directoryInode,
            exact[0].FileSystemType, exact[0].ReadOnly,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(exact[0].RawLine))));
    }

    private static MountInfoEntry ParseLine(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var separator = Array.IndexOf(fields, "-");
        if (separator < 6 || fields.Length <= separator + 3 ||
            !ulong.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var mountId) ||
            mountId == 0 || !TryDevice(fields[2], out var major, out var minor))
            throw new RuntimeAuthorizationException("runtime_workspace_mount_info_invalid");

        var mountOptions = fields[5].Split(',', StringSplitOptions.RemoveEmptyEntries);
        var readOnly = mountOptions.Contains("ro", StringComparer.Ordinal);
        if (readOnly == mountOptions.Contains("rw", StringComparer.Ordinal))
            throw new RuntimeAuthorizationException("runtime_workspace_mount_info_invalid");
        return new(mountId, major, minor, DecodePath(fields[4]), fields[separator + 1],
            readOnly, line);
    }

    private static string DecodePath(string value)
    {
        var decoded = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\')
            {
                decoded.Append(value[index]);
                continue;
            }
            if (index + 3 >= value.Length ||
                value[index + 1] is < '0' or > '7' ||
                value[index + 2] is < '0' or > '7' ||
                value[index + 3] is < '0' or > '7')
                throw new RuntimeAuthorizationException("runtime_workspace_mount_info_invalid");
            var encoded = (byte)((value[index + 1] - '0') * 64 +
                (value[index + 2] - '0') * 8 + value[index + 3] - '0');
            decoded.Append((char)encoded);
            index += 3;
        }
        return decoded.ToString();
    }

    private static bool TryDevice(string value, out uint major, out uint minor)
    {
        major = 0;
        minor = 0;
        var parts = value.Split(':');
        return parts.Length == 2 &&
            uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out major) &&
            uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor);
    }

    private sealed record MountInfoEntry(
        ulong MountId, uint DeviceMajor, uint DeviceMinor, string MountPoint,
        string FileSystemType, bool ReadOnly, string RawLine);

    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct LinuxStatx
    {
        internal uint Mask;
        internal uint BlockSize;
        internal ulong Attributes;
        internal uint LinkCount;
        internal uint UserId;
        internal uint GroupId;
        internal ushort Mode;
        internal ushort Spare0;
        internal ulong Inode;
        internal ulong Size;
        internal ulong Blocks;
        internal ulong AttributesMask;
        internal LinuxStatxTimestamp AccessTime;
        internal LinuxStatxTimestamp BirthTime;
        internal LinuxStatxTimestamp ChangeTime;
        internal LinuxStatxTimestamp ModifyTime;
        internal uint DeviceTypeMajor;
        internal uint DeviceTypeMinor;
        internal uint DeviceMajor;
        internal uint DeviceMinor;
        internal ulong MountId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LinuxStatxTimestamp
    {
        internal long Seconds;
        internal uint Nanoseconds;
        internal int Reserved;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenDirectory(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        int directoryDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        ref LinuxStatx result);
}
