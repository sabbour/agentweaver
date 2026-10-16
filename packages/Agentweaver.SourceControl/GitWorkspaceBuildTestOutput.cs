using Agentweaver.Abstractions;

namespace Agentweaver.SourceControl;

/// <summary>Validates and parses native BuildTest output against its pinned capture and seed evidence.</summary>
public static class GitWorkspaceBuildTestOutput
{
    /// <summary>
    /// Checks the complete document and each output obligation against the exact native capture reference.
    /// </summary>
    /// <param name="bytes">Serialized capture document returned by the native collector.</param>
    /// <param name="reference">Pinned lengths, digests, output tree, and per-output evidence.</param>
    /// <param name="source">Accepted repository source and baseline identity.</param>
    /// <param name="seedBytes">Accepted seed bytes used to prepare the workspace.</param>
    /// <param name="outputs">Output obligations fixed when the command was accepted.</param>
    /// <returns>A validated capture document.</returns>
    /// <exception cref="ArgumentException">The document, seed, or required-output evidence differs from its accepted binding.</exception>
    public static GitWorkspaceCaptureDocument Parse(
        ReadOnlySpan<byte> bytes,
        SandboxBuildTestWorkspaceCaptureReference reference,
        SandboxBuildTestWorkspaceSource source,
        ReadOnlySpan<byte> seedBytes,
        System.Collections.Immutable.ImmutableArray<SandboxBuildTestOutputObligation> outputs)
    {
        if (bytes.Length != reference.DocumentByteLength ||
            GitWorkspaceCapturePackage.Hash(bytes) != reference.DocumentSha256)
            throw new ArgumentException("The native output bytes differ from their exact capture reference.");
        var document = GitWorkspaceCapturePackage.ParseDocument(bytes);
        if (document.Manifest.OutputTreeSha != reference.OutputTreeSha ||
            document.ManifestSha256 != reference.ManifestSha256 ||
            document.ManifestBytes.LongLength != reference.ManifestByteLength ||
            document.PatchSha256 != reference.PatchSha256 ||
            document.PatchBytes.LongLength != reference.PatchByteLength ||
            document.PackageSha256 != reference.PackageSha256 ||
            document.PackageBytes.LongLength != reference.PackageByteLength)
            throw new ArgumentException("The native output sections differ from their exact capture reference.");
        var seed = GitWorkspaceBuildTestSeedPackage.Parse(seedBytes, source);
        GitWorkspaceBuildTestSeedPackage.ValidateCapture(
            document.Manifest, seed, outputs, reference.Outputs);
        return document;
    }
}
