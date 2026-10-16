using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.Win32.SafeHandles;

namespace Agentweaver.AgentRuntime;

internal static class MountedWorkspaceFlush
{
    private const int ODirectory = 0x10000;
    private const int OCloexec = 0x80000;

    internal static async Task<WorkspaceVolumeFlushReceipt> FlushAndCreateReceiptAsync(
        string mountedPath, WorkspaceVolumeFlushRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mountedPath);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Verified workspace flush requires Linux syncfs support.");

        var root = Path.GetFullPath(mountedPath);
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The configured workspace mount is unavailable or is a link.");

        var descriptor = OpenDirectory(root, ODirectory | OCloexec);
        if (descriptor < 0)
            throw new IOException(
                "The configured workspace mount could not be opened for a durability flush.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        using var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        cancellationToken.ThrowIfCancellationRequested();
        if (SyncFileSystem(descriptor) != 0)
            throw new IOException(
                "The configured workspace filesystem could not be flushed.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));

        var treeHash = await ComputeTreeSha256Async(root, cancellationToken).ConfigureAwait(false);
        var receipt = new WorkspaceVolumeFlushReceipt(
            1,
            request.EnvironmentFence,
            new WorkspaceVolumeReference(
                request.Spec.ProjectId, request.Spec.VolumeId, request.ResourceGeneration),
            request.Resource,
            request.ExpectedDataGeneration,
            checked(request.ExpectedDataGeneration + 1),
            request.IdempotencyKey,
            treeHash,
            ProviderCheckpointId: null);
        return receipt.ValidateFor(request);
    }

    internal static async Task<string> ComputeTreeSha256Async(
        string mountedPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mountedPath);
        var root = Path.GetFullPath(mountedPath);
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The configured workspace mount is unavailable or is a link.");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendRecord(hash, 1, ".", []);
        await AppendDirectoryChildrenAsync(root, ".", hash, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async Task AppendDirectoryChildrenAsync(
        string directory, string parentPath, IncrementalHash hash, CancellationToken cancellationToken)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory)
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = parentPath == "."
                ? Path.GetFileName(path)
                : parentPath + "/" + Path.GetFileName(path);
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                var linkTarget = (attributes & FileAttributes.Directory) != 0
                    ? new DirectoryInfo(path).LinkTarget
                    : new FileInfo(path).LinkTarget;
                if (linkTarget is null)
                    throw new IOException("A workspace link target could not be read.");
                AppendRecord(hash, 3, relative, Encoding.UTF8.GetBytes(linkTarget));
            }
            else if ((attributes & FileAttributes.Directory) != 0)
            {
                AppendRecord(hash, 1, relative, []);
                await AppendDirectoryChildrenAsync(path, relative, hash, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await AppendFileAsync(path, relative, hash, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task AppendFileAsync(
        string path, string relativePath, IncrementalHash hash, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var contentHash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        AppendRecord(hash, 2, relativePath, contentHash);
    }

    private static void AppendRecord(
        IncrementalHash hash, byte kind, string relativePath, ReadOnlySpan<byte> content)
    {
        var pathBytes = Encoding.UTF8.GetBytes(relativePath);
        Span<byte> header = stackalloc byte[5];
        header[0] = kind;
        BinaryPrimitives.WriteInt32BigEndian(header[1..], pathBytes.Length);
        hash.AppendData(header);
        hash.AppendData(pathBytes);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, content.Length);
        hash.AppendData(length);
        hash.AppendData(content);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenDirectory(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "syncfs", SetLastError = true)]
    private static extern int SyncFileSystem(int descriptor);
}
