using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace Agentweaver.AgentHost;

internal static class BuildTestOutputCollectorProgram
{
    private const int AtFileDescriptorCwd = -100;
    private const int AtEmptyPath = 0x1000;
    private const int OReadOnly = 0;
    private const int ONonBlock = 0x800;
    private const int OCloseOnExec = 0x80000;
    private const int ODirectory = 0x10000;
    private const int ONoFollow = 0x20000;
    private const int OPath = 0x200000;
    private const uint StatxBasicStats = 0x07ff;
    private const uint StatxMountId = 0x1000;
    private const ushort FileTypeMask = 0xf000;
    private const ushort RegularFileType = 0x8000;
    private const ushort DirectoryFileType = 0x4000;
    private const int NoEntry = 2;
    private const int NotDirectory = 20;
    private const int UnsupportedSyscall = 38;
    private const int InvalidArgument = 22;
    private const int TooManySymbolicLinks = 40;
    private const string PodUidEnvironmentVariable = "AGENTWEAVER_COLLECTOR_POD_UID";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    internal static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
            return Fail("collector_linux_required", 3);
        if (args.Length != 1 || args[0].Length > SandboxBuildTestLimits.MaximumCollectorRequestBytes * 2)
            return Fail("collector_request_invalid", 2);

        try
        {
            var requestBytes = DecodeRequest(args[0]);
            var request = JsonSerializer.Deserialize<SandboxBuildTestOutputCollectorRequest>(
                requestBytes,
                JsonOptions) ?? throw new ArgumentException("The collector request is empty.");
            request.Validate();
            var podUid = Environment.GetEnvironmentVariable(PodUidEnvironmentVariable);
            ArgumentException.ThrowIfNullOrWhiteSpace(podUid, PodUidEnvironmentVariable);
            if (podUid.Length > 256 || podUid.Any(char.IsControl))
                throw new ArgumentException("The collector Pod UID is invalid.", PodUidEnvironmentVariable);

            var outputs = await CollectAsync(request, podUid, cancellationToken).ConfigureAwait(false);
            var receipt = new SandboxBuildTestOutputCollectorReceipt(
                1,
                request.OperationId,
                request.ImmutableHash,
                request.RequestFingerprint,
                request.Checkpoint,
                podUid,
                SandboxBuildTestLimits.OutputCollectorContainerName,
                outputs,
                string.Empty);
            receipt = receipt with
            {
                ManifestSha256 = SandboxBuildTestOutputCollectorCanonicalization.ComputeManifestSha256(receipt)
            };
            _ = receipt.Validate(
                request,
                new SandboxBuildTestPodReference(
                    "collector",
                    "collector",
                    podUid,
                    "collector"));
            var receiptBytes = JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions);
            if (receiptBytes.Length > SandboxBuildTestLimits.MaximumCollectorRequestBytes)
                return Fail("collector_receipt_too_large", 4);
            await Console.OpenStandardOutput().WriteAsync(receiptBytes, cancellationToken).ConfigureAwait(false);
            await Console.OpenStandardOutput().WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (JsonException)
        {
            return Fail("collector_request_invalid", 2);
        }
        catch (FormatException)
        {
            return Fail("collector_request_invalid", 2);
        }
        catch (ArgumentException)
        {
            return Fail("collector_request_invalid", 2);
        }
        catch (DllNotFoundException)
        {
            return Fail("collector_native_capability_missing", 3);
        }
        catch (EntryPointNotFoundException)
        {
            return Fail("collector_native_capability_missing", 3);
        }
        catch (CollectorCapabilityException)
        {
            return Fail("collector_native_capability_missing", 3);
        }
        catch (IOException)
        {
            return Fail("collector_read_failed", 4);
        }
        catch (UnauthorizedAccessException)
        {
            return Fail("collector_read_failed", 4);
        }
    }

    private static async Task<ImmutableArray<SandboxBuildTestOutputEvidence>> CollectAsync(
        SandboxBuildTestOutputCollectorRequest request,
        string podUid,
        CancellationToken cancellationToken)
    {
        var prepared = new List<PreparedOutput>(request.Outputs.Length);
        try
        {
            var root = OpenWorkspaceRoot(request.WorkspaceMountPath);
            using (root.Handle)
            {
                var rootIdentity = ReadIdentity(root.Handle);
                foreach (var obligation in request.Outputs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    prepared.Add(PrepareOutput(
                        request.WorkspaceMountPath,
                        obligation,
                        rootIdentity));
                }

                long totalBytes = 0;
                var results = ImmutableArray.CreateBuilder<SandboxBuildTestOutputEvidence>(prepared.Count);
                for (var index = 0; index < prepared.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = prepared[index];
                    var obligation = request.Outputs[index];
                    var result = item.Exists
                        ? await ReadFileAsync(
                            item,
                            obligation.MaximumBytes,
                            request.MaximumTotalBytes - totalBytes,
                            cancellationToken).ConfigureAwait(false)
                        : new FileReadResult(false, 0, null);
                    totalBytes = checked(totalBytes + result.CapturedBytes);
                    results.Add(new(
                        podUid,
                        SandboxBuildTestLimits.OutputCollectorContainerName,
                        obligation.Name,
                        obligation.RelativePath,
                        obligation.Required,
                        obligation.MaximumBytes,
                        result.Exists,
                        result.CapturedBytes,
                        result.CapturedSha256));
                }

                foreach (var item in prepared)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    VerifyPathUnchanged(request.WorkspaceMountPath, item, rootIdentity);
                }

                return results.ToImmutable();
            }
        }
        finally
        {
            foreach (var item in prepared)
                item.Dispose();
        }
    }

    private static async Task<FileReadResult> ReadFileAsync(
        PreparedOutput output,
        long maximumFileBytes,
        long remainingTotalBytes,
        CancellationToken cancellationToken)
    {
        var originalIdentity = output.FileIdentity;
        if (output.FileHandle is null || originalIdentity is null)
            throw new IOException("A prepared regular file has no descriptor.");
        if (originalIdentity.Size > (ulong)maximumFileBytes ||
            originalIdentity.Size > (ulong)remainingTotalBytes)
            throw new IOException("A BuildTest output exceeds its accepted read limit.");

        var pathHandle = output.FileHandle;
        var before = ReadIdentity(pathHandle);
        RequireSameFile(originalIdentity, before);
        var procPath = $"/proc/self/fd/{pathHandle.DangerousGetHandle().ToInt64().ToString(CultureInfo.InvariantCulture)}";
        var readDescriptor = OpenAt(
            AtFileDescriptorCwd,
            procPath,
            OReadOnly | ONonBlock | OCloseOnExec);
        if (readDescriptor < 0)
            ThrowNative("openat");
        using var readHandle = new SafeFileHandle(new IntPtr(readDescriptor), ownsHandle: true);
        var opened = ReadIdentity(readHandle);
        RequireSameFile(before, opened);

        using var input = new FileStream(readHandle, FileAccess.Read, 8192, isAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[8192];
        long readBytes = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileRemaining = maximumFileBytes - readBytes;
            var totalRemaining = remainingTotalBytes - readBytes;
            var readLimit = (int)Math.Min(buffer.Length, Math.Min(fileRemaining, totalRemaining) + 1);
            var read = await input.ReadAsync(buffer.AsMemory(0, readLimit), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            readBytes = checked(readBytes + read);
            if (readBytes > maximumFileBytes || readBytes > remainingTotalBytes)
                throw new IOException("A BuildTest output exceeded its accepted read limit.");
            hash.AppendData(buffer.AsSpan(0, read));
        }

        var after = ReadIdentity(readHandle);
        RequireSameFile(before, after);
        if ((ulong)readBytes != before.Size)
            throw new IOException("A BuildTest output changed while it was read.");
        return new(true, readBytes, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static PreparedOutput PrepareOutput(
        string workspaceMountPath,
        SandboxBuildTestOutputObligation obligation,
        FileIdentity rootIdentity)
    {
        var segments = obligation.RelativePath.Split('/');
        var directoryIdentities = ImmutableArray.CreateBuilder<FileIdentity>(segments.Length);
        using var root = OpenWorkspaceRoot(workspaceMountPath);
        var rootAtPath = ReadIdentity(root.Handle);
        RequireSameDirectory(rootIdentity, rootAtPath);
        directoryIdentities.Add(rootAtPath);
        var current = root.Handle;
        SafeFileHandle? ownedDirectory = null;
        try
        {
            for (var index = 0; index < segments.Length - 1; index++)
            {
                var nextDescriptor = OpenAt(
                    current.DangerousGetHandle().ToInt32(),
                    segments[index],
                    OPath | ODirectory | ONoFollow | OCloseOnExec);
                if (nextDescriptor < 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == NoEntry)
                        return PreparedOutput.Missing(
                            obligation,
                            segments,
                            directoryIdentities.ToImmutable(),
                            index);
                    ThrowNative("openat", error);
                }
                var next = new SafeFileHandle(new IntPtr(nextDescriptor), ownsHandle: true);
                var identity = ReadIdentity(next);
                if ((identity.Mode & FileTypeMask) != DirectoryFileType ||
                    identity.MountId != rootIdentity.MountId)
                {
                    next.Dispose();
                    throw new IOException("A BuildTest output path escaped the workspace mount.");
                }
                directoryIdentities.Add(identity);
                ownedDirectory?.Dispose();
                ownedDirectory = next;
                current = next;
            }

            var fileDescriptor = OpenAt(
                current.DangerousGetHandle().ToInt32(),
                segments[^1],
                OPath | ONoFollow | ONonBlock | OCloseOnExec);
            if (fileDescriptor < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == NoEntry)
                    return PreparedOutput.Missing(
                        obligation,
                        segments,
                        directoryIdentities.ToImmutable(),
                        segments.Length - 1);
                ThrowNative("openat", error);
            }

            var fileHandle = new SafeFileHandle(new IntPtr(fileDescriptor), ownsHandle: true);
            var fileIdentity = ReadIdentity(fileHandle);
            if ((fileIdentity.Mode & FileTypeMask) != RegularFileType ||
                fileIdentity.MountId != rootIdentity.MountId)
            {
                fileHandle.Dispose();
                throw new IOException("A BuildTest output is not a regular file on the workspace mount.");
            }
            if (fileIdentity.Size > (ulong)obligation.MaximumBytes)
            {
                fileHandle.Dispose();
                throw new IOException("A BuildTest output exceeds its per-file limit.");
            }
            return PreparedOutput.Present(
                obligation,
                segments,
                directoryIdentities.ToImmutable(),
                fileHandle,
                fileIdentity);
        }
        finally
        {
            ownedDirectory?.Dispose();
        }
    }

    private static void VerifyPathUnchanged(
        string workspaceMountPath,
        PreparedOutput original,
        FileIdentity rootIdentity)
    {
        using var fresh = PrepareOutput(
            workspaceMountPath,
            original.Obligation,
            rootIdentity);
        if (!original.Exists)
        {
            if (fresh.Exists ||
                fresh.MissingSegmentIndex != original.MissingSegmentIndex ||
                !SameDirectories(original.DirectoryIdentities, fresh.DirectoryIdentities))
                throw new IOException("A missing BuildTest output changed during collection.");
            return;
        }
        var originalFileIdentity = original.FileIdentity;
        var freshFileIdentity = fresh.FileIdentity;
        if (!fresh.Exists ||
            originalFileIdentity is null ||
            freshFileIdentity is null ||
            !SameDirectories(original.DirectoryIdentities, fresh.DirectoryIdentities) ||
            !SameFileState(originalFileIdentity, freshFileIdentity))
            throw new IOException("A BuildTest output changed during collection.");
    }

    private static bool SameDirectories(
        ImmutableArray<FileIdentity> left,
        ImmutableArray<FileIdentity> right) =>
        left.Length == right.Length && left.Zip(right).All(pair =>
            SameFileState(pair.First, pair.Second));

    private static void RequireSameDirectory(FileIdentity expected, FileIdentity actual)
    {
        if ((expected.Mode & FileTypeMask) != DirectoryFileType ||
            (actual.Mode & FileTypeMask) != DirectoryFileType ||
            expected.MountId != actual.MountId ||
            !SameFileState(expected, actual))
            throw new IOException("A BuildTest workspace directory changed during collection.");
    }

    private static void RequireSameFile(FileIdentity expected, FileIdentity actual)
    {
        if ((expected.Mode & FileTypeMask) != RegularFileType || !SameFileState(expected, actual))
            throw new IOException("A BuildTest output changed while it was opened or read.");
    }

    private static bool SameFileState(FileIdentity left, FileIdentity right) =>
        left.DeviceMajor == right.DeviceMajor &&
        left.DeviceMinor == right.DeviceMinor &&
        left.Inode == right.Inode &&
        left.Mode == right.Mode &&
        left.Size == right.Size &&
        left.MountId == right.MountId &&
        left.ModificationSeconds == right.ModificationSeconds &&
        left.ModificationNanoseconds == right.ModificationNanoseconds &&
        left.ChangeSeconds == right.ChangeSeconds &&
        left.ChangeNanoseconds == right.ChangeNanoseconds;

    private static OpenWorkspaceResult OpenWorkspaceRoot(string path)
    {
        var descriptor = OpenAt(
            AtFileDescriptorCwd,
            "/",
            OPath | ODirectory | OCloseOnExec);
        if (descriptor < 0)
            ThrowNative("openat");
        var current = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        try
        {
            foreach (var segment in path.Split('/').Skip(1))
            {
                if (segment.Length == 0)
                    continue;
                var nextDescriptor = OpenAt(
                    current.DangerousGetHandle().ToInt32(),
                    segment,
                    OPath | ODirectory | ONoFollow | OCloseOnExec);
                if (nextDescriptor < 0)
                    ThrowNative("openat");
                var next = new SafeFileHandle(new IntPtr(nextDescriptor), ownsHandle: true);
                var identity = ReadIdentity(next);
                if ((identity.Mode & FileTypeMask) != DirectoryFileType)
                {
                    next.Dispose();
                    throw new IOException("The BuildTest workspace mount path contains a non-directory.");
                }
                current.Dispose();
                current = next;
            }
            return new(current);
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static FileIdentity ReadIdentity(SafeFileHandle handle)
    {
        if (Statx(
                handle.DangerousGetHandle().ToInt32(),
                string.Empty,
                AtEmptyPath,
                StatxBasicStats | StatxMountId,
                out var result) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is UnsupportedSyscall or InvalidArgument)
                throw new CollectorCapabilityException();
            ThrowNative("statx", error);
        }
        if ((result.Mask & (StatxBasicStats | StatxMountId)) != (StatxBasicStats | StatxMountId))
            throw new CollectorCapabilityException();
        return new(
            result.DeviceMajor,
            result.DeviceMinor,
            result.Inode,
            result.Mode,
            result.Size,
            result.MountId,
            result.ModificationTime.Seconds,
            result.ModificationTime.Nanoseconds,
            result.ChangeTime.Seconds,
            result.ChangeTime.Nanoseconds);
    }

    private static byte[] DecodeRequest(string encoded)
    {
        var padded = encoded.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight((padded.Length + 3) / 4 * 4, '=');
        var bytes = Convert.FromBase64String(padded);
        if (bytes.Length is < 1 or > SandboxBuildTestLimits.MaximumCollectorRequestBytes)
            throw new ArgumentException("The collector request size is invalid.");
        return bytes;
    }

    private static void ThrowNative(string operation, int? error = null)
    {
        error ??= Marshal.GetLastPInvokeError();
        if (error == NoEntry)
            throw new FileNotFoundException("A BuildTest output path does not exist.");
        if (error is TooManySymbolicLinks or NotDirectory)
            throw new IOException("A BuildTest output path contains a symbolic link or non-directory component.");
        if (operation == "statx" && error is (UnsupportedSyscall or InvalidArgument))
            throw new CollectorCapabilityException();
        throw new IOException(
            $"The collector {operation} operation failed with Linux error {error.Value}.",
            new Win32Exception(error.Value));
    }

    private static int Fail(string code, int exitCode)
    {
        Console.Error.WriteLine(code);
        return exitCode;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 16
        };
        return options;
    }

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        out LinuxStatx result);

    private sealed record OpenWorkspaceResult(SafeFileHandle Handle) : IDisposable
    {
        public void Dispose() => Handle.Dispose();
    }

    private sealed record FileIdentity(
        uint DeviceMajor,
        uint DeviceMinor,
        ulong Inode,
        ushort Mode,
        ulong Size,
        ulong MountId,
        long ModificationSeconds,
        uint ModificationNanoseconds,
        long ChangeSeconds,
        uint ChangeNanoseconds);

    private sealed record FileReadResult(bool Exists, long CapturedBytes, string? CapturedSha256);

    private sealed class CollectorCapabilityException : Exception
    {
    }

    private sealed class PreparedOutput(
        SandboxBuildTestOutputObligation obligation,
        string[] segments,
        ImmutableArray<FileIdentity> directoryIdentities,
        int? missingSegmentIndex,
        SafeFileHandle? fileHandle,
        FileIdentity? fileIdentity) : IDisposable
    {
        public SandboxBuildTestOutputObligation Obligation { get; } = obligation;
        public string[] Segments { get; } = segments;
        public ImmutableArray<FileIdentity> DirectoryIdentities { get; } = directoryIdentities;
        public int? MissingSegmentIndex { get; } = missingSegmentIndex;
        public SafeFileHandle? FileHandle { get; } = fileHandle;
        public FileIdentity? FileIdentity { get; } = fileIdentity;
        public bool Exists => FileHandle is not null;

        public static PreparedOutput Missing(
            SandboxBuildTestOutputObligation obligation,
            string[] segments,
            ImmutableArray<FileIdentity> directories,
            int missingSegmentIndex) =>
            new(obligation, segments, directories, missingSegmentIndex, null, null);

        public static PreparedOutput Present(
            SandboxBuildTestOutputObligation obligation,
            string[] segments,
            ImmutableArray<FileIdentity> directories,
            SafeFileHandle fileHandle,
            FileIdentity fileIdentity) =>
            new(obligation, segments, directories, null, fileHandle, fileIdentity);

        public void Dispose() => FileHandle?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct LinuxStatxTimestamp
    {
        public long Seconds;
        public uint Nanoseconds;
        private uint Reserved;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(80)] public LinuxStatxTimestamp BirthTime;
        [FieldOffset(96)] public LinuxStatxTimestamp ChangeTime;
        [FieldOffset(112)] public LinuxStatxTimestamp ModificationTime;
        [FieldOffset(128)] public uint RDeviceMajor;
        [FieldOffset(132)] public uint RDeviceMinor;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
        [FieldOffset(144)] public ulong MountId;
    }
}
