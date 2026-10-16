using System.Buffers.Binary;
using System.Collections.Immutable;
using Agentweaver.Abstractions;

namespace Agentweaver.SourceControl;

/// <summary>Verified immutable baseline files, bundle, package, and serialized seed for one BuildTest request.</summary>
public sealed record GitWorkspaceBuildTestSeed(
    GitWorkspaceCapturedOutputManifest Baseline,
    ImmutableArray<byte> BundleBytes,
    ImmutableArray<byte> BaselinePackageBytes,
    ImmutableArray<byte> Bytes)
{
    /// <summary>Redacts seed bytes and repository content from diagnostic formatting.</summary>
    public override string ToString() => nameof(GitWorkspaceBuildTestSeed) + " [REDACTED]";
}

/// <summary>Parsed seed package and its original serialized bytes, with its digest.</summary>
public sealed record GitWorkspaceBuildTestSeedDocument(
    GitWorkspaceCaptureDocument Baseline,
    byte[] BundleBytes,
    byte[] Bytes,
    string Sha256)
{
    /// <summary>Redacts repository seed bytes from diagnostic formatting.</summary>
    public override string ToString() => nameof(GitWorkspaceBuildTestSeedDocument) + " [REDACTED]";
}

/// <summary>
/// Encodes and validates Source Control seed packages used to prepare a BuildTest workspace.
/// The package binds an immutable baseline inventory, Git bundle, and baseline file package to an accepted source.
/// </summary>
public static class GitWorkspaceBuildTestSeedPackage
{
    private static ReadOnlySpan<byte> Magic => "AWSEED02"u8;
    private const int HeaderBytes = 20;

    /// <summary>Creates a bounded seed package after verifying the baseline package and regular-file inventory.</summary>
    /// <param name="baseline">Verified baseline capture document.</param>
    /// <param name="bundleBytes">Git bundle containing the baseline repository objects.</param>
    /// <returns>Serialized seed bytes for the accepted command.</returns>
    /// <exception cref="ArgumentException">The bundle or baseline metadata is invalid.</exception>
    /// <exception cref="GitWorkspaceException">The seed exceeds the configured package limit.</exception>
    public static byte[] Create(GitWorkspaceCaptureDocument baseline, ReadOnlySpan<byte> bundleBytes)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var manifest = GitWorkspaceCapturePackage.ParseManifest(baseline.ManifestBytes);
        GitWorkspaceCapturePackage.VerifyPackage(
            baseline.PackageBytes, manifest, baseline.PackageSha256);
        if (bundleBytes.IsEmpty || baseline.ManifestSha256 != GitWorkspaceCapturePackage.Hash(baseline.ManifestBytes) ||
            manifest.Files.Any(file => file.Mode is not ("100644" or "100755")))
            throw new ArgumentException("The BuildTest seed requires a bundle and a verified regular-file baseline.");
        var length = checked((long)HeaderBytes + baseline.ManifestBytes.Length +
            bundleBytes.Length + baseline.PackageBytes.Length);
        if (length > ProducedRunCaptureLimits.MaximumPackageBytes)
            throw new GitWorkspaceException(
                GitWorkspaceFailureCode.CaptureTooLarge, "The BuildTest repository seed exceeds its byte limit.");

        var bytes = new byte[(int)length];
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), (uint)baseline.ManifestBytes.Length);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12, 4), (uint)bundleBytes.Length);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), (uint)baseline.PackageBytes.Length);
        var offset = HeaderBytes;
        baseline.ManifestBytes.CopyTo(bytes, offset);
        offset += baseline.ManifestBytes.Length;
        bundleBytes.CopyTo(bytes.AsSpan(offset));
        offset += bundleBytes.Length;
        baseline.PackageBytes.CopyTo(bytes, offset);
        return bytes;
    }

    /// <summary>Parses the exact accepted seed and verifies its source, baseline, bundle, and package digests.</summary>
    /// <param name="bytes">Serialized seed bytes.</param>
    /// <param name="source">Accepted source metadata and expected digests.</param>
    /// <returns>A seed object whose sections have passed validation.</returns>
    /// <exception cref="ArgumentException">The seed framing or any accepted source binding differs.</exception>
    public static GitWorkspaceBuildTestSeed Parse(
        ReadOnlySpan<byte> bytes,
        SandboxBuildTestWorkspaceSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _ = source.Validate();
        if (bytes.Length != source.SeedByteLength || bytes.Length < HeaderBytes ||
            GitWorkspaceCapturePackage.Hash(bytes) != source.SeedSha256 ||
            !bytes[..Magic.Length].SequenceEqual(Magic))
            throw new ArgumentException("The BuildTest repository seed length, header, or digest is invalid.");

        var manifestLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(8, 4));
        var bundleLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(12, 4));
        var packageLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(16, 4));
        if (manifestLength != source.BaselineManifestByteLength || bundleLength != source.BundleByteLength ||
            packageLength < ProducedRunCaptureLimits.PackageHeaderBytes ||
            (long)HeaderBytes + manifestLength + bundleLength + packageLength != bytes.Length)
            throw new ArgumentException("The BuildTest repository seed sections do not match the accepted binding.");
        var manifestBytes = bytes.Slice(HeaderBytes, (int)manifestLength);
        var bundleBytes = bytes.Slice(HeaderBytes + (int)manifestLength, (int)bundleLength);
        var packageBytes = bytes[(HeaderBytes + (int)manifestLength + (int)bundleLength)..];
        if (GitWorkspaceCapturePackage.Hash(manifestBytes) != source.BaselineManifestSha256 ||
            GitWorkspaceCapturePackage.Hash(bundleBytes) != source.BundleSha256)
            throw new ArgumentException("The BuildTest repository seed bundle or inventory digest is invalid.");
        var manifest = GitWorkspaceCapturePackage.ParseManifest(manifestBytes);
        if (manifest.WorkspaceId != source.WorkspaceId || manifest.RunId != source.Identity.RunId ||
            manifest.RepositoryId != source.SourceControlResource.ResourceId ||
            manifest.ResourceGeneration != source.SourceControlResource.Generation ||
            manifest.WorkspaceIncarnationId != source.WorkspaceIncarnationId ||
            manifest.BranchName != source.BranchName || manifest.BaseSha != source.BaseSha ||
            manifest.OutputTreeSha != source.BaselineTreeSha ||
            manifest.Files.Any(file => file.Mode is not ("100644" or "100755")))
            throw new ArgumentException("The BuildTest repository seed inventory has a different source binding.");
        var package = packageBytes.ToArray();
        GitWorkspaceCapturePackage.VerifyPackage(package, manifest, GitWorkspaceCapturePackage.Hash(package));
        return new(manifest, bundleBytes.ToArray().ToImmutableArray(), package.ToImmutableArray(),
            bytes.ToArray().ToImmutableArray());
    }

    /// <summary>Extracts baseline file content from a package already verified against the seed manifest.</summary>
    internal static ImmutableArray<GitWorkspaceCapturedFile> ReadBaselineFiles(GitWorkspaceBuildTestSeed seed)
    {
        var package = seed.BaselinePackageBytes.ToArray();
        GitWorkspaceCapturePackage.VerifyPackage(package, seed.Baseline, GitWorkspaceCapturePackage.Hash(package));
        var files = ImmutableArray.CreateBuilder<GitWorkspaceCapturedFile>(seed.Baseline.Files.Length);
        var offset = ProducedRunCaptureLimits.PackageHeaderBytes;
        foreach (var file in seed.Baseline.Files)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt64BigEndian(
                package.AsSpan(offset, ProducedRunCaptureLimits.PackageFileLengthBytes)));
            offset += ProducedRunCaptureLimits.PackageFileLengthBytes;
            files.Add(new(file.Path, file.Mode, file.Sha256, file.ByteLength,
                ImmutableArray.Create(package, offset, length)));
            offset += length;
        }
        return files.MoveToImmutable();
    }

    /// <summary>
    /// Ensures captured files preserve the baseline except for declared outputs and match output evidence.
    /// </summary>
    /// <param name="capture">Post-command output manifest.</param>
    /// <param name="seed">Verified pre-command baseline.</param>
    /// <param name="obligations">Accepted required and optional output declarations.</param>
    /// <param name="evidence">Collector evidence corresponding by index to each obligation.</param>
    /// <exception cref="GitWorkspaceException">Any identity, path, file, or evidence invariant is violated.</exception>
    public static void ValidateCapture(
        GitWorkspaceCapturedOutputManifest capture,
        GitWorkspaceBuildTestSeed seed,
        ImmutableArray<SandboxBuildTestOutputObligation> obligations,
        ImmutableArray<SandboxBuildTestOutputEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(seed);
        var baseline = seed.Baseline;
        GitWorkspaceCapturePackage.ValidateManifest(capture);
        GitWorkspaceCapturePackage.ValidateManifest(baseline);
        if (capture.WorkspaceId != baseline.WorkspaceId || capture.RunId != baseline.RunId ||
            capture.RepositoryId != baseline.RepositoryId ||
            capture.ResourceGeneration != baseline.ResourceGeneration ||
            capture.WorkspaceIncarnationId != baseline.WorkspaceIncarnationId ||
            capture.BranchName != baseline.BranchName || capture.BaseSha != baseline.BaseSha ||
            capture.Files.IsDefault || baseline.Files.IsDefault || obligations.IsDefault || evidence.IsDefault ||
            obligations.Length != evidence.Length || obligations.Any(output => output is null) ||
            evidence.Any(output => output is null) ||
            obligations.Select(output => output.Name).Distinct(StringComparer.Ordinal).Count() != obligations.Length ||
            obligations.Select(output => output.RelativePath).Distinct(StringComparer.Ordinal).Count() != obligations.Length ||
            capture.Files.Any(file => file is null) ||
            capture.Files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != capture.Files.Length)
            throw InvalidCapture();

        var requiredByPath = obligations.ToDictionary(output => output.RelativePath, StringComparer.Ordinal);
        var baselineByPath = baseline.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var captureByPath = capture.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var evidenceByPath = new Dictionary<string, SandboxBuildTestOutputEvidence>(StringComparer.Ordinal);
        for (var index = 0; index < obligations.Length; index++)
        {
            var output = obligations[index].Validate();
            var observed = evidence[index];
            if (observed.Name != output.Name || observed.RelativePath != output.RelativePath ||
                observed.Required != output.Required || observed.MaximumBytes != output.MaximumBytes ||
                observed.CapturedBytes < 0 || observed.CapturedBytes > output.MaximumBytes ||
                output.Required && !observed.Exists ||
                !observed.Exists && (observed.CapturedBytes != 0 || observed.CapturedSha256 is not null))
                throw InvalidCapture();
            evidenceByPath.Add(output.RelativePath, observed);
        }

        foreach (var file in capture.Files)
        {
            if (requiredByPath.ContainsKey(file.Path))
            {
                var observed = evidenceByPath[file.Path];
                if (!observed.Exists || observed.CapturedBytes != file.ByteLength ||
                    observed.CapturedSha256 != file.Sha256 || file.Mode is not ("100644" or "100755"))
                    throw InvalidCapture();
            }
            else if (!baselineByPath.TryGetValue(file.Path, out var original) || file != original)
            {
                throw InvalidCapture();
            }
        }

        foreach (var file in baseline.Files)
            if (!captureByPath.ContainsKey(file.Path))
                throw InvalidCapture();
        foreach (var output in obligations)
            if (evidenceByPath[output.RelativePath].Exists != captureByPath.ContainsKey(output.RelativePath))
                throw InvalidCapture();
    }

    /// <summary>Creates the common corruption error returned for an inconsistent captured output.</summary>
    private static GitWorkspaceException InvalidCapture() =>
        new(GitWorkspaceFailureCode.CorruptWorkspace,
            "The BuildTest capture does not match its accepted baseline, declared outputs, or output receipt.");
}
