using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.SourceControl;
using Microsoft.Win32.SafeHandles;
using WorkspaceRequest = Agentweaver.Abstractions.SandboxBuildTestBindingPreparation.SandboxBuildTestWorkspaceRequest;
using PreparationReceipt = Agentweaver.Abstractions.SandboxBuildTestBindingPreparation.SandboxBuildTestWorkspacePreparationReceipt;

namespace Agentweaver.AgentHost;

internal static partial class BuildTestOutputCollectorProgram
{
    private const string NativeCaptureStatePath = "/state/buildtest";

    internal static async Task<int> RunWorkspaceAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
            return Fail("collector_linux_required", 3);
        try
        {
            if (args.Length is < 1 or > 2)
                throw new ArgumentException("The workspace collector arguments are invalid.");
            var request = JsonSerializer.Deserialize<WorkspaceRequest>(DecodeRequest(args[0]), JsonOptions)
                ?? throw new ArgumentException("The workspace request is empty.");
            request.Validate();
            var podUid = Environment.GetEnvironmentVariable(PodUidEnvironmentVariable);
            ArgumentException.ThrowIfNullOrWhiteSpace(podUid);
            if (podUid.Length > 256 || podUid.Any(char.IsControl))
                throw new ArgumentException("The native workspace Pod UID is invalid.");
            if (args.Length == 2 && args[1] == "hold")
            {
                if (!request.PreparingWorkspace)
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                else
                {
                    var path = PreparationStatePath(request);
                    while (!File.Exists(path))
                        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                    var receipt = await ReadBoundedStateAsync(path,
                        SandboxBuildTestLimits.MaximumCollectorRequestBytes, cancellationToken).ConfigureAwait(false);
                    await Console.OpenStandardOutput().WriteAsync(receipt, cancellationToken).ConfigureAwait(false);
                }
                return 0;
            }
            var action = args.Length == 1 ? "prepare" : args[1];
            if (request.PreparingWorkspace != (action == "prepare") ||
                action is not ("prepare" or "capture" or "export"))
                throw new ArgumentException("The workspace action differs from its immutable request.");
            using var root = OpenWorkspaceRoot(request.WorkspaceMountPath);
            var rootIdentity = ReadIdentity(root.Handle);
            var output = Console.OpenStandardOutput();
            await output.WriteAsync(Encoding.UTF8.GetBytes(
                $"AWPODIO2 {podUid} {request.RequestFingerprint}\n"), cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (action == "export")
            {
                var bytes = await ReadSealedCaptureAsync(request, podUid, cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                return 0;
            }
            var seed = await ReadSeedAsync(request.Source, cancellationToken).ConfigureAwait(false);
            byte[] response;
            if (action == "prepare")
            {
                var manager = new GitWorkspaceManager(request.WorkspaceMountPath);
                var workspace = await manager.PrepareBuildTestSeedAsync(request.Source, seed, cancellationToken)
                    .ConfigureAwait(false);
                var expectedPath = request.WorkspaceMountPath.TrimEnd('/') + "/" +
                    request.Source.PreparedRepositoryRelativePath;
                if (workspace.Path != expectedPath)
                    throw new IOException("The prepared repository has an unexpected native path.");
                using var prepared = OpenWorkspaceRoot(expectedPath);
                if (ReadIdentity(prepared.Handle).MountId != rootIdentity.MountId)
                    throw new IOException("The prepared repository escaped its workspace mount.");
                response = JsonSerializer.SerializeToUtf8Bytes(new PreparationReceipt(
                    request.OperationId, request.ImmutableHash, request.RequestFingerprint, request.Checkpoint,
                    podUid, request.Source.PreparedRepositoryRelativePath,
                    request.Source.SeedSha256, request.Source.BaselineTreeSha), JsonOptions);
                Directory.CreateDirectory(NativeCaptureStatePath);
                var receiptPath = PreparationStatePath(request);
                if (!File.Exists(receiptPath))
                    await WriteStateAsync(receiptPath, response, cancellationToken).ConfigureAwait(false);
                else if (!(await ReadBoundedStateAsync(receiptPath,
                             SandboxBuildTestLimits.MaximumCollectorRequestBytes, cancellationToken)
                         .ConfigureAwait(false)).AsSpan().SequenceEqual(response))
                    throw new IOException("The native preparation belongs to another operation or Pod.");
            }
            else
            {
                var reference = await CaptureWorkspaceAsync(request, podUid, seed, cancellationToken)
                    .ConfigureAwait(false);
                response = JsonSerializer.SerializeToUtf8Bytes(reference, JsonOptions);
            }
            if (response.Length > SandboxBuildTestLimits.MaximumCollectorRequestBytes)
                throw new IOException("The workspace receipt exceeds its byte limit.");
            await output.WriteAsync(response, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (JsonException) { return Fail("collector_request_invalid", 2); }
        catch (FormatException) { return Fail("collector_request_invalid", 2); }
        catch (ArgumentException) { return Fail("collector_request_invalid", 2); }
        catch (GitWorkspaceException) { return Fail("collector_workspace_invalid", 4); }
        catch (DllNotFoundException) { return Fail("collector_native_capability_missing", 3); }
        catch (EntryPointNotFoundException) { return Fail("collector_native_capability_missing", 3); }
        catch (CollectorCapabilityException) { return Fail("collector_native_capability_missing", 3); }
        catch (IOException) { return Fail("collector_read_failed", 4); }
        catch (UnauthorizedAccessException) { return Fail("collector_read_failed", 4); }
    }

    private static string PreparationStatePath(WorkspaceRequest request) =>
        NativeCaptureStatePath + "/preparation-" + request.OperationId.ToString("N") + ".json";

    private static async Task<byte[]> ReadSeedAsync(
        SandboxBuildTestWorkspaceSource source,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[checked((int)source.SeedByteLength)];
        var input = Console.OpenStandardInput();
        await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var extra = new byte[1];
        if (await input.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0 ||
            GitWorkspaceCapturePackage.Hash(bytes) != source.SeedSha256)
            throw new ArgumentException("The native seed bytes differ from their accepted source.");
        return bytes;
    }

    private static async Task<SandboxBuildTestWorkspaceCaptureReference> CaptureWorkspaceAsync(
        WorkspaceRequest request,
        string podUid,
        byte[] seed,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(NativeCaptureStatePath);
        using var stateRoot = OpenWorkspaceRoot(NativeCaptureStatePath);
        var statePath = NativeCaptureStatePath + "/" + request.OperationId.ToString("N");
        Directory.CreateDirectory(statePath);
        using var state = OpenWorkspaceRoot(statePath);
        using var lease = new FileStream(statePath + "/capture.lock",
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var binding = JsonSerializer.SerializeToUtf8Bytes(new { request, podUid }, JsonOptions);
        var bindingPath = statePath + "/binding.json";
        if (File.Exists(bindingPath))
        {
            var previous = await ReadBoundedStateAsync(
                bindingPath, SandboxBuildTestLimits.MaximumCollectorRequestBytes, cancellationToken)
                .ConfigureAwait(false);
            if (!previous.AsSpan().SequenceEqual(binding))
                throw new IOException("The native capture state belongs to another operation or Pod.");
        }
        else
            await WriteStateAsync(bindingPath, binding, cancellationToken).ConfigureAwait(false);

        var documentPath = statePath + "/output.capture";
        var pendingDocumentPath = documentPath + ".pending";
        if (!File.Exists(documentPath) && File.Exists(pendingDocumentPath))
        {
            var pending = await ReadBoundedStateAsync(pendingDocumentPath,
                ProducedRunCaptureLimits.MaximumDocumentBytes, cancellationToken).ConfigureAwait(false);
            if (GitWorkspaceCapturePackage.IsIncompleteDocument(pending))
            {
                Console.Error.WriteLine("collector_unsealed_capture_retry");
                File.Delete(pendingDocumentPath);
            }
            else
            {
                var complete = GitWorkspaceCapturePackage.ParseDocument(pending);
                GitWorkspaceBuildTestSeedPackage.ValidateCapture(complete.Manifest,
                    GitWorkspaceBuildTestSeedPackage.Parse(seed, request.Source), request.Outputs,
                    CreateWorkspaceEvidence(request, podUid, complete.Manifest.Files));
                await PromotePendingStateAsync(pendingDocumentPath, documentPath, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        GitWorkspaceCaptureDocument document;
        if (File.Exists(documentPath))
        {
            var existing = await ReadBoundedStateAsync(
                documentPath, ProducedRunCaptureLimits.MaximumDocumentBytes, cancellationToken)
                .ConfigureAwait(false);
            document = GitWorkspaceCapturePackage.ParseDocument(existing);
        }
        else
        {
            var repositoryPath = request.WorkspaceMountPath.TrimEnd('/') + "/" +
                request.Source.PreparedRepositoryRelativePath;
            using var workspaceRoot = OpenWorkspaceRoot(request.WorkspaceMountPath);
            using var repository = OpenWorkspaceRoot(repositoryPath);
            if (ReadIdentity(workspaceRoot.Handle).MountId != ReadIdentity(repository.Handle).MountId)
                throw new IOException("The native output repository escaped its workspace mount.");
            var files = await CaptureCompleteTreeAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
            var evidence = CreateWorkspaceEvidence(request, podUid, files);
            var manager = new GitWorkspaceManager(statePath + "/git");
            document = await manager.CreateBuildTestOutputDocumentAsync(
                request.Source, seed, files, request.Outputs, evidence, cancellationToken).ConfigureAwait(false);
            await WriteStateAsync(documentPath, GitWorkspaceCapturePackage.SerializeDocument(document),
                cancellationToken).ConfigureAwait(false);
        }
        var outputs = CreateWorkspaceEvidence(request, podUid, document.Manifest.Files);
        GitWorkspaceBuildTestSeedPackage.ValidateCapture(
            document.Manifest, GitWorkspaceBuildTestSeedPackage.Parse(seed, request.Source), request.Outputs, outputs);
        var bytes = GitWorkspaceCapturePackage.SerializeDocument(document);
        var reference = new SandboxBuildTestWorkspaceCaptureReference(
            request.OperationId, request.ImmutableHash, request.RequestFingerprint, request.Checkpoint,
            podUid, request.Source.PreparedRepositoryRelativePath, request.Source.SeedSha256,
            GitWorkspaceCapturePackage.Hash(bytes), bytes.LongLength,
            document.Manifest.OutputTreeSha,
            document.ManifestSha256, document.ManifestBytes.LongLength,
            document.PatchSha256, document.PatchBytes.LongLength,
            document.PackageSha256, document.PackageBytes.LongLength, outputs);
        var referencePath = statePath + "/reference.json";
        var referenceBytes = JsonSerializer.SerializeToUtf8Bytes(reference, JsonOptions);
        if (File.Exists(referencePath))
        {
            var previous = await ReadBoundedStateAsync(referencePath,
                SandboxBuildTestLimits.MaximumCollectorRequestBytes, cancellationToken).ConfigureAwait(false);
            if (!previous.AsSpan().SequenceEqual(referenceBytes))
                throw new IOException("The sealed native capture reference changed.");
        }
        else
            await WriteStateAsync(referencePath, referenceBytes, cancellationToken).ConfigureAwait(false);
        return reference;
    }

    private static async Task<byte[]> ReadSealedCaptureAsync(
        WorkspaceRequest request,
        string podUid,
        CancellationToken cancellationToken)
    {
        var statePath = NativeCaptureStatePath + "/" + request.OperationId.ToString("N");
        using var state = OpenWorkspaceRoot(statePath);
        var binding = await ReadBoundedStateAsync(statePath + "/binding.json",
            SandboxBuildTestLimits.MaximumCollectorRequestBytes, cancellationToken).ConfigureAwait(false);
        if (!binding.AsSpan().SequenceEqual(
                JsonSerializer.SerializeToUtf8Bytes(new { request, podUid }, JsonOptions)))
            throw new IOException("The native output export belongs to another operation or Pod.");
        var referenceBytes = await ReadBoundedStateAsync(statePath + "/reference.json",
            SandboxBuildTestLimits.MaximumCollectorRequestBytes, cancellationToken).ConfigureAwait(false);
        var reference = JsonSerializer.Deserialize<SandboxBuildTestWorkspaceCaptureReference>(
            referenceBytes, JsonOptions) ?? throw new IOException("The native capture reference is missing.");
        var bytes = await ReadBoundedStateAsync(statePath + "/output.capture",
            ProducedRunCaptureLimits.MaximumDocumentBytes, cancellationToken).ConfigureAwait(false);
        if (bytes.LongLength != reference.DocumentByteLength ||
            GitWorkspaceCapturePackage.Hash(bytes) != reference.DocumentSha256)
            throw new IOException("The sealed native output bytes changed.");
        return bytes;
    }

    private static async Task<byte[]> ReadBoundedStateAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var root = OpenWorkspaceRoot(Path.GetDirectoryName(path)!);
        var rootIdentity = ReadIdentity(root.Handle);
        var obligation = new SandboxBuildTestOutputObligation("capture-state", Path.GetFileName(path),
            true, maximumBytes);
        using var file = PrepareOutput(Path.GetDirectoryName(path)!, obligation, rootIdentity);
        using var bytes = new MemoryStream();
        await ReadFileAsync(file, maximumBytes, maximumBytes, cancellationToken, bytes).ConfigureAwait(false);
        VerifyPathUnchanged(Path.GetDirectoryName(path)!, file, rootIdentity);
        return bytes.ToArray();
    }

    private static async Task WriteStateAsync(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var temporary = path + ".pending";
        var previousLength = 0;
        if (File.Exists(temporary))
        {
            var previous = await ReadBoundedStateAsync(
                temporary, bytes.Length, cancellationToken).ConfigureAwait(false);
            if (previous.Length > bytes.Length || !bytes.AsSpan(0, previous.Length).SequenceEqual(previous))
                throw new IOException("The pending native state differs from its exact operation.");
            previousLength = previous.Length;
        }
        await using (var output = new FileStream(temporary,
                         previousLength == 0 && !File.Exists(temporary) ? FileMode.CreateNew : FileMode.Open,
                         FileAccess.Write, FileShare.None))
        {
            if (output.Length != previousLength)
                throw new IOException("The pending native state changed during recovery.");
            output.Position = previousLength;
            await output.WriteAsync(bytes.AsMemory(previousLength), cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        File.Move(temporary, path);
    }

    private static async Task PromotePendingStateAsync(
        string pendingPath, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using (var file = new FileStream(pendingPath, FileMode.Open, FileAccess.Write, FileShare.None))
            file.Flush(flushToDisk: true);
        File.Move(pendingPath, path);
    }

    private static ImmutableArray<SandboxBuildTestOutputEvidence> CreateWorkspaceEvidence(
        WorkspaceRequest request,
        string podUid,
        ImmutableArray<GitWorkspaceCapturedFile> files) =>
        CreateWorkspaceEvidence(request, podUid, files.Select(file =>
            new GitWorkspaceCapturedOutputFile(file.Path, file.Mode, file.Sha256, file.ByteLength)).ToImmutableArray());

    private static ImmutableArray<SandboxBuildTestOutputEvidence> CreateWorkspaceEvidence(
        WorkspaceRequest request,
        string podUid,
        ImmutableArray<GitWorkspaceCapturedOutputFile> files)
    {
        var byPath = files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        return request.Outputs.Select(obligation =>
        {
            byPath.TryGetValue(obligation.RelativePath, out var file);
            return new SandboxBuildTestOutputEvidence(podUid, SandboxBuildTestLimits.WorkspaceCaptureContainerName,
                obligation.Name, obligation.RelativePath, obligation.Required, obligation.MaximumBytes,
                file is not null, file?.ByteLength ?? 0, file?.Sha256);
        }).ToImmutableArray();
    }

    private static async Task<ImmutableArray<GitWorkspaceCapturedFile>> CaptureCompleteTreeAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var before = ReadWorkspaceTree(repositoryPath, cancellationToken);
        var rootIdentity = before[string.Empty];
        var files = ImmutableArray.CreateBuilder<GitWorkspaceCapturedFile>();
        long total = 0;
        foreach (var pair in before.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if ((pair.Value.Mode & FileTypeMask) != RegularFileType)
                continue;
            var obligation = new SandboxBuildTestOutputObligation(
                "workspace-file", pair.Key, true, ProducedRunCaptureLimits.MaximumFileBytes).Validate();
            using var file = PrepareOutput(repositoryPath, obligation, rootIdentity);
            if (file.FileIdentity is null)
                throw new IOException("A native workspace file disappeared.");
            RequireSameFile(pair.Value, file.FileIdentity);
            using var content = new MemoryStream();
            var result = await ReadFileAsync(file, ProducedRunCaptureLimits.MaximumFileBytes,
                ProducedRunCaptureLimits.MaximumTotalFileBytes - total, cancellationToken, content)
                .ConfigureAwait(false);
            VerifyPathUnchanged(repositoryPath, file, rootIdentity);
            total = checked(total + result.CapturedBytes);
            files.Add(new(pair.Key, (pair.Value.Mode & 0x40) == 0 ? "100644" : "100755",
                result.CapturedSha256 ?? throw new IOException("A native workspace file has no digest."),
                result.CapturedBytes, content.ToArray().ToImmutableArray()));
            if (files.Count > ProducedRunCaptureLimits.MaximumFiles)
                throw new IOException("The complete native workspace has too many files.");
        }
        var after = ReadWorkspaceTree(repositoryPath, cancellationToken);
        if (before.Count != after.Count ||
            before.Any(pair => !after.TryGetValue(pair.Key, out var identity) ||
                !SameFileState(pair.Value, identity)))
            throw new IOException("The complete native workspace changed during capture.");
        return files.ToImmutable();
    }

    private static Dictionary<string, FileIdentity> ReadWorkspaceTree(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        using var root = OpenWorkspaceRoot(repositoryPath);
        var rootIdentity = ReadIdentity(root.Handle);
        var entries = new Dictionary<string, FileIdentity>(StringComparer.Ordinal) { [string.Empty] = rootIdentity };
        var pending = new Stack<string>();
        pending.Push(string.Empty);
        while (pending.TryPop(out var relative))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var directory = OpenWorkspaceRoot(repositoryPath + (relative.Length == 0 ? "" : "/" + relative));
            RequireSameDirectory(entries[relative], ReadIdentity(directory.Handle));
            foreach (var name in ReadDirectoryNames(directory.Handle))
            {
                var path = relative.Length == 0 ? name : relative + "/" + name;
                _ = new SandboxBuildTestOutputObligation("workspace-entry", path, true, 1).Validate();
                var descriptor = OpenAt(directory.Handle.DangerousGetHandle().ToInt32(), name,
                    OPath | ONoFollow | ONonBlock | OCloseOnExec);
                if (descriptor < 0)
                    ThrowNative("openat");
                using var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
                var identity = ReadIdentity(handle);
                if (identity.MountId != rootIdentity.MountId ||
                    (identity.Mode & FileTypeMask) is not (RegularFileType or DirectoryFileType))
                    throw new IOException("The complete native workspace contains a link, special file, or nested mount.");
                if (path == ".git")
                {
                    if ((identity.Mode & FileTypeMask) != DirectoryFileType)
                        throw new IOException("The prepared repository has no ordinary Git directory.");
                    continue;
                }
                if (!entries.TryAdd(path, identity) ||
                    entries.Count > ProducedRunCaptureLimits.MaximumFiles * 2 + 1)
                    throw new IOException("The complete native workspace inventory exceeds its path limit.");
                if ((identity.Mode & FileTypeMask) == DirectoryFileType)
                    pending.Push(path);
            }
        }
        return entries;
    }

    private static string[] ReadDirectoryNames(SafeFileHandle directory)
    {
        var descriptor = OpenAt(directory.DangerousGetHandle().ToInt32(), ".",
            OReadOnly | ODirectory | ONoFollow | OCloseOnExec);
        if (descriptor < 0)
            ThrowNative("openat");
        var stream = OpenDirectoryDescriptor(descriptor);
        if (stream == IntPtr.Zero)
        {
            using var failed = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
            ThrowNative("fdopendir");
        }
        try
        {
            var names = new List<string>();
            while (true)
            {
                var entry = ReadDirectoryEntry(stream);
                if (entry == IntPtr.Zero)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error != 0)
                        ThrowNative("readdir64", error);
                    break;
                }
                var length = (ushort)Marshal.ReadInt16(entry, 16);
                if (length is < 20 or > 280)
                    throw new IOException("A native directory entry is invalid.");
                var name = Marshal.PtrToStringUTF8(IntPtr.Add(entry, 19))
                    ?? throw new IOException("A native directory entry has no name.");
                if (name is "." or "..")
                    continue;
                names.Add(name);
                if (names.Count > ProducedRunCaptureLimits.MaximumFiles * 2)
                    throw new IOException("A native directory has too many entries.");
            }
            return names.Order(StringComparer.Ordinal).ToArray();
        }
        finally
        {
            CloseDirectory(stream);
        }
    }

    [DllImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
    private static extern IntPtr OpenDirectoryDescriptor(int descriptor);

    [DllImport("libc", EntryPoint = "readdir64", SetLastError = true)]
    private static extern IntPtr ReadDirectoryEntry(IntPtr directory);

    [DllImport("libc", EntryPoint = "closedir", SetLastError = true)]
    private static extern int CloseDirectory(IntPtr directory);
}
