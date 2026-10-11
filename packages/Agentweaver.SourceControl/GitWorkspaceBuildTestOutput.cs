using Agentweaver.Abstractions;

namespace Agentweaver.SourceControl;

public static class GitWorkspaceBuildTestOutput
{
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
