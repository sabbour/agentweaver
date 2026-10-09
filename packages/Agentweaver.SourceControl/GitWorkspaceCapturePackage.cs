using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.SourceControl;

public sealed record GitWorkspaceCapturedOutputFile(
    string Path,
    string Mode,
    string Sha256,
    long ByteLength);

public sealed record GitWorkspaceCapturedOutputManifest(
    int ContractVersion,
    string WorkspaceId,
    string RunId,
    string RepositoryId,
    long ResourceGeneration,
    Guid WorkspaceIncarnationId,
    string BranchName,
    string BaseSha,
    string OutputTreeSha,
    ImmutableArray<GitWorkspaceCapturedOutputFile> Files);

public sealed record GitWorkspaceCaptureDocument(
    GitWorkspaceCapturedOutputManifest Manifest,
    byte[] ManifestBytes,
    string ManifestSha256,
    byte[] PatchBytes,
    string PatchSha256,
    byte[] PackageBytes,
    string PackageSha256);

public static class GitWorkspaceCapturePackage
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("AWSCAP01");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static GitWorkspaceCaptureDocument Create(GitWorkspaceCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (capture.Files.IsDefault || capture.Files.Length > ProducedRunCaptureLimits.MaximumFiles)
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.CaptureTooLarge,
                "The captured workspace contains an invalid number of files.");
        if (!IsSha(capture.BaseSha) || !IsSha(capture.OutputTreeSha) ||
            capture.WorkspaceIncarnationId == Guid.Empty)
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.CorruptWorkspace,
                "The captured workspace identity is invalid.");

        long totalFileBytes = 0;
        var fileMetadata = ImmutableArray.CreateBuilder<GitWorkspaceCapturedOutputFile>(capture.Files.Length);
        long packageLength = ProducedRunCaptureLimits.PackageHeaderBytes;
        foreach (var file in capture.Files)
        {
            ValidateFileMetadata(file);
            totalFileBytes = checked(totalFileBytes + file.ByteLength);
            packageLength = checked(packageLength +
                ProducedRunCaptureLimits.PackageFileLengthBytes + file.ByteLength);
            if (totalFileBytes > ProducedRunCaptureLimits.MaximumTotalFileBytes ||
                packageLength > ProducedRunCaptureLimits.MaximumPackageBytes)
                throw new GitWorkspaceException(
                    GitWorkspaceFailureCode.CaptureTooLarge,
                    "The captured workspace exceeded the configured size limit.");
            fileMetadata.Add(new(file.Path, file.Mode, file.Sha256, file.ByteLength));
        }

        var manifest = new GitWorkspaceCapturedOutputManifest(
            ProducedRunCaptureLimits.ContractVersion,
            capture.WorkspaceId,
            capture.RunId,
            capture.RepositoryId,
            capture.ResourceGeneration,
            capture.WorkspaceIncarnationId,
            capture.BranchName,
            capture.BaseSha.ToLowerInvariant(),
            capture.OutputTreeSha.ToLowerInvariant(),
            fileMetadata.MoveToImmutable());
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        if (manifestBytes.Length is 0 or > ProducedRunCaptureLimits.MaximumManifestBytes)
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.CaptureTooLarge,
                "The captured workspace manifest exceeded the configured size limit.");

        byte[] patchBytes;
        try
        {
            patchBytes = new UTF8Encoding(false, true).GetBytes(capture.Patch);
        }
        catch (EncoderFallbackException exception)
        {
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.CorruptWorkspace,
                "The captured workspace diff is not valid UTF-8.",
                innerException: exception);
        }
        if (patchBytes.Length > ProducedRunCaptureLimits.MaximumDiffBytes)
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.DiffTooLarge,
                "The captured workspace diff exceeded the configured size limit.");

        var package = new byte[checked((int)packageLength)];
        Magic.CopyTo(package, 0);
        BinaryPrimitives.WriteUInt32BigEndian(
            package.AsSpan(Magic.Length, sizeof(uint)), checked((uint)capture.Files.Length));
        var offset = ProducedRunCaptureLimits.PackageHeaderBytes;
        foreach (var file in capture.Files)
        {
            BinaryPrimitives.WriteUInt64BigEndian(
                package.AsSpan(offset, ProducedRunCaptureLimits.PackageFileLengthBytes),
                checked((ulong)file.ByteLength));
            offset += ProducedRunCaptureLimits.PackageFileLengthBytes;
            file.Content.AsSpan().CopyTo(package.AsSpan(offset));
            offset += checked((int)file.ByteLength);
        }

        return new(
            manifest,
            manifestBytes,
            Hash(manifestBytes),
            patchBytes,
            Hash(patchBytes),
            package,
            Hash(package));
    }

    public static GitWorkspaceCapturedOutputManifest ParseManifest(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > ProducedRunCaptureLimits.MaximumManifestBytes)
            throw new ArgumentException("The captured output manifest length is invalid.", nameof(bytes));
        var manifest = JsonSerializer.Deserialize<GitWorkspaceCapturedOutputManifest>(bytes, JsonOptions)
            ?? throw new ArgumentException("The captured output manifest is empty.", nameof(bytes));
        ValidateManifest(manifest);
        var canonical = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        if (!bytes.SequenceEqual(canonical))
            throw new ArgumentException("The captured output manifest is not canonical.", nameof(bytes));
        return manifest;
    }

    public static void VerifyPackage(
        byte[] package,
        GitWorkspaceCapturedOutputManifest manifest,
        string expectedSha256)
    {
        ArgumentNullException.ThrowIfNull(package);
        ValidateManifest(manifest);
        if (package.Length < ProducedRunCaptureLimits.PackageHeaderBytes ||
            package.Length > ProducedRunCaptureLimits.MaximumPackageBytes ||
            Hash(package) != expectedSha256 ||
            !package.AsSpan(0, Magic.Length).SequenceEqual(Magic) ||
            BinaryPrimitives.ReadUInt32BigEndian(
                package.AsSpan(Magic.Length, sizeof(uint))) != manifest.Files.Length)
            throw new ArgumentException("The captured output package header or digest is invalid.", nameof(package));

        var offset = ProducedRunCaptureLimits.PackageHeaderBytes;
        for (var index = 0; index < manifest.Files.Length; index++)
        {
            if (package.Length - offset < ProducedRunCaptureLimits.PackageFileLengthBytes)
                throw new ArgumentException("The captured output package is truncated.", nameof(package));
            var length = BinaryPrimitives.ReadUInt64BigEndian(
                package.AsSpan(offset, ProducedRunCaptureLimits.PackageFileLengthBytes));
            offset += ProducedRunCaptureLimits.PackageFileLengthBytes;
            var file = manifest.Files[index];
            if (length != (ulong)file.ByteLength || length > (ulong)(package.Length - offset))
                throw new ArgumentException("The captured output file length is invalid.", nameof(package));
            var content = package.AsSpan(offset, checked((int)length));
            if (Hash(content) != file.Sha256)
                throw new ArgumentException("A captured output file digest is invalid.", nameof(package));
            offset += checked((int)length);
        }

        if (offset != package.Length)
            throw new ArgumentException("The captured output package contains trailing bytes.", nameof(package));
    }

    public static byte[] ExtractFile(
        byte[] package,
        GitWorkspaceCapturedOutputManifest manifest,
        string expectedSha256,
        string path)
    {
        VerifyPackage(package, manifest, expectedSha256);
        var offset = ProducedRunCaptureLimits.PackageHeaderBytes;
        foreach (var file in manifest.Files)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt64BigEndian(
                package.AsSpan(offset, ProducedRunCaptureLimits.PackageFileLengthBytes)));
            offset += ProducedRunCaptureLimits.PackageFileLengthBytes;
            if (string.Equals(file.Path, path, StringComparison.Ordinal))
                return package.AsSpan(offset, length).ToArray();
            offset += length;
        }
        throw new FileNotFoundException("The captured output path does not exist.", path);
    }

    public static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void ValidateManifest(GitWorkspaceCapturedOutputManifest manifest)
    {
        if (manifest.ContractVersion != ProducedRunCaptureLimits.ContractVersion ||
            string.IsNullOrWhiteSpace(manifest.WorkspaceId) ||
            manifest.WorkspaceId.Length > 128 ||
            manifest.WorkspaceId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')) ||
            string.IsNullOrWhiteSpace(manifest.RunId) ||
            string.IsNullOrWhiteSpace(manifest.RepositoryId) ||
            manifest.ResourceGeneration < 1 ||
            manifest.WorkspaceIncarnationId == Guid.Empty ||
            !GitWorkspaceManager.IsValidBranchName(manifest.BranchName) ||
            !IsSha(manifest.BaseSha) ||
            !IsSha(manifest.OutputTreeSha) ||
            manifest.Files.IsDefault ||
            manifest.Files.Length > ProducedRunCaptureLimits.MaximumFiles)
            throw new ArgumentException("The captured output manifest binding is invalid.", nameof(manifest));

        var paths = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var file in manifest.Files)
        {
            if (file is null ||
                !IsSafePath(file.Path) ||
                !paths.Add(file.Path) ||
                file.Mode is not ("100644" or "100755" or "120000") ||
                !IsSha256(file.Sha256) ||
                file.ByteLength is < 0 or > ProducedRunCaptureLimits.MaximumFileBytes)
                throw new ArgumentException("The captured output manifest contains an invalid file.", nameof(manifest));
            totalBytes = checked(totalBytes + file.ByteLength);
            if (totalBytes > ProducedRunCaptureLimits.MaximumTotalFileBytes)
                throw new ArgumentException("The captured output manifest exceeds the file byte limit.", nameof(manifest));
        }
    }

    private static void ValidateFileMetadata(GitWorkspaceCapturedFile file)
    {
        if (!IsSafePath(file.Path) ||
            file.Mode is not ("100644" or "100755" or "120000") ||
            !IsSha256(file.Sha256) ||
            file.ByteLength is < 0 or > ProducedRunCaptureLimits.MaximumFileBytes ||
            file.Content.Length != file.ByteLength ||
            Hash(file.Content.AsSpan()) != file.Sha256)
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.CorruptWorkspace,
                "The captured workspace contains an invalid file.");
    }

    private static bool IsSafePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.Length <= 4096 &&
        path[0] != '/' &&
        !path.Contains('\\') &&
        !path.Contains(':') &&
        !Path.IsPathRooted(path) &&
        !path.Any(char.IsControl) &&
        !path.Split('/').Any(segment => segment is "" or "." or "..") &&
        !string.Equals(path, ".git", StringComparison.OrdinalIgnoreCase) &&
        !path.StartsWith(".git/", StringComparison.OrdinalIgnoreCase);

    private static bool IsSha(string? value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
