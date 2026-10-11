using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Agentweaver.Abstractions;
using Xunit;

namespace Agentweaver.SourceControl.Tests;

public sealed class GitWorkspaceBuildTestSeedTests
{
    [Fact]
    public void SeedKeepsTheCompleteBaselineAndExactBundleBytes()
    {
        var fixture = Fixture();

        var parsed = GitWorkspaceBuildTestSeedPackage.Parse(fixture.Bytes, fixture.Source);

        Assert.Equal(fixture.Baseline.Manifest.Files.ToArray(), parsed.Baseline.Files.ToArray());
        Assert.Equal(fixture.Bundle, parsed.BundleBytes.ToArray());
        Assert.Equal(fixture.Baseline.PackageBytes, parsed.BaselinePackageBytes.ToArray());
        Assert.Equal(fixture.Bytes, parsed.Bytes.ToArray());
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("header")]
    [InlineData("section")]
    [InlineData("bundle")]
    [InlineData("inventory")]
    [InlineData("incarnation")]
    [InlineData("workspace")]
    [InlineData("repository")]
    public void SeedRejectsChangedBytesOrSourceBindingBeforePreparation(string fault)
    {
        var fixture = Fixture();
        var bytes = fixture.Bytes.ToArray();
        var source = fixture.Source;
        switch (fault)
        {
            case "digest":
                bytes[^1] ^= 1;
                break;
            case "header":
                bytes[0] ^= 1;
                source = source with { SeedSha256 = GitWorkspaceCapturePackage.Hash(bytes) };
                break;
            case "section":
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), uint.MaxValue);
                source = source with { SeedSha256 = GitWorkspaceCapturePackage.Hash(bytes) };
                break;
            case "bundle":
                source = source with { BundleSha256 = new string('b', 64) };
                break;
            case "inventory":
                source = source with { BaselineManifestSha256 = new string('b', 64) };
                break;
            case "incarnation":
                source = source with { WorkspaceIncarnationId = Guid.NewGuid() };
                break;
            case "workspace":
                source = source with { WorkspaceId = "another-workspace" };
                break;
            case "repository":
                source = source with
                {
                    SourceControlResource = source.SourceControlResource with { ResourceId = "other-repository" }
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fault));
        }

        Assert.Throws<ArgumentException>(() => GitWorkspaceBuildTestSeedPackage.Parse(bytes, source));
    }

    [Fact]
    public void FullCaptureAcceptsUnchangedReadmeAndOneDeclaredBinaryArtifact()
    {
        var fixture = Fixture();
        var artifact = File("out/result.bin", [0, 255, 128, 10]);
        var capture = Capture([.. fixture.Files, artifact]);
        var document = GitWorkspaceCapturePackage.Create(capture);
        var seed = GitWorkspaceBuildTestSeedPackage.Parse(fixture.Bytes, fixture.Source);

        GitWorkspaceBuildTestSeedPackage.ValidateCapture(
            document.Manifest, seed, [Obligation()], [Evidence(artifact)]);

        Assert.Equal(
            artifact.Content.ToArray(),
            GitWorkspaceCapturePackage.ExtractFile(
                document.PackageBytes, document.Manifest, document.PackageSha256, artifact.Path));
        Assert.Equal(2, document.Manifest.Files.Length);
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("modified-input")]
    [InlineData("deleted-input")]
    [InlineData("missing-output")]
    [InlineData("hash")]
    [InlineData("length")]
    [InlineData("receipt-path")]
    [InlineData("receipt-required")]
    [InlineData("workspace")]
    [InlineData("incarnation")]
    public void FullCaptureRejectsEveryUndeclaredChangeAndReceiptMismatch(string fault)
    {
        var fixture = Fixture();
        var artifact = File("out/result.bin", [0, 255, 128, 10]);
        var files = ImmutableArray.Create(fixture.Files[0], artifact);
        var evidence = Evidence(artifact);
        switch (fault)
        {
            case "extra":
                files = files.Add(File("extra.txt", [1]));
                break;
            case "modified-input":
                files = [File("README.md", Encoding.UTF8.GetBytes("changed")), artifact];
                break;
            case "deleted-input":
                files = [artifact];
                break;
            case "missing-output":
                files = fixture.Files;
                break;
            case "hash":
                evidence = evidence with { CapturedSha256 = new string('b', 64) };
                break;
            case "length":
                evidence = evidence with { CapturedBytes = artifact.ByteLength + 1 };
                break;
            case "receipt-path":
                evidence = evidence with { RelativePath = "other.bin" };
                break;
            case "receipt-required":
                evidence = evidence with { Required = false };
                break;
        }
        var capture = GitWorkspaceCapturePackage.Create(Capture(files)).Manifest;
        if (fault == "workspace")
            capture = capture with { WorkspaceId = "other" };
        if (fault == "incarnation")
            capture = capture with { WorkspaceIncarnationId = Guid.NewGuid() };
        var seed = GitWorkspaceBuildTestSeedPackage.Parse(fixture.Bytes, fixture.Source);

        Assert.Throws<GitWorkspaceException>(() =>
            GitWorkspaceBuildTestSeedPackage.ValidateCapture(capture, seed, [Obligation()], [evidence]));
    }

    [Fact]
    public void DeclaredOutputStillRequiresItsReceiptWhenItsBytesEqualTheBaseline()
    {
        var original = File("out/result.bin", [1, 2, 3]);
        var fixture = Fixture([File("README.md", [10]), original]);
        var seed = GitWorkspaceBuildTestSeedPackage.Parse(fixture.Bytes, fixture.Source);
        var incorrect = Evidence(original) with { CapturedSha256 = new string('b', 64) };

        Assert.Throws<GitWorkspaceException>(() =>
            GitWorkspaceBuildTestSeedPackage.ValidateCapture(
                fixture.Baseline.Manifest, seed, [Obligation()], [incorrect]));
        GitWorkspaceBuildTestSeedPackage.ValidateCapture(
            fixture.Baseline.Manifest, seed, [Obligation()], [Evidence(original)]);
    }

    [Fact]
    public void OptionalOutputCannotAuthorizeDeletionOfABaselineFile()
    {
        var original = File("out/result.bin", [1, 2, 3]);
        var fixture = Fixture([File("README.md", [10]), original]);
        var seed = GitWorkspaceBuildTestSeedPackage.Parse(fixture.Bytes, fixture.Source);
        var capture = GitWorkspaceCapturePackage.Create(Capture([fixture.Files[0]])).Manifest;
        var optional = Obligation() with { Required = false };
        var missing = Evidence(original) with
        {
            Required = false,
            Exists = false,
            CapturedBytes = 0,
            CapturedSha256 = null
        };

        Assert.Throws<GitWorkspaceException>(() =>
            GitWorkspaceBuildTestSeedPackage.ValidateCapture(capture, seed, [optional], [missing]));
    }

    [Fact]
    public void AbsentOptionalNewOutputMustMatchAnAbsentReceipt()
    {
        var fixture = Fixture();
        var seed = GitWorkspaceBuildTestSeedPackage.Parse(fixture.Bytes, fixture.Source);
        var optional = Obligation() with { Required = false };
        var missing = new SandboxBuildTestOutputEvidence(
            "collector-uid", SandboxBuildTestLimits.OutputCollectorContainerName,
            optional.Name, optional.RelativePath, false, optional.MaximumBytes, false, 0, null);

        GitWorkspaceBuildTestSeedPackage.ValidateCapture(
            fixture.Baseline.Manifest, seed, [optional], [missing]);
    }

    private static SeedFixture Fixture(ImmutableArray<GitWorkspaceCapturedFile> files = default)
    {
        files = files.IsDefault ? [File("README.md", Encoding.UTF8.GetBytes("existing input"))] : files;
        var baseline = GitWorkspaceCapturePackage.Create(Capture(files));
        var bundle = Encoding.UTF8.GetBytes("unit-test bundle bytes");
        var bytes = GitWorkspaceBuildTestSeedPackage.Create(baseline, bundle);
        var source = new SandboxBuildTestWorkspaceSource(
            2, new("project", "run", "session"), "tenant", "issuer", "actor",
            "source-pin", new string('a', 64),
            new(ProviderSeam.SourceControl, "github", "repository", 3),
            "workspace", Guid.Parse("8c1d3b3e-9713-4658-b1ae-176c8c8bbf24"),
            "work-branch", baseline.Manifest.BaseSha, baseline.Manifest.OutputTreeSha,
            "seed-reference", GitWorkspaceCapturePackage.Hash(bytes), bytes.Length,
            GitWorkspaceCapturePackage.Hash(bundle), bundle.Length,
            baseline.ManifestSha256, baseline.ManifestBytes.Length);
        return new(files, baseline, bundle, bytes, source);
    }

    private static GitWorkspaceCapture Capture(ImmutableArray<GitWorkspaceCapturedFile> files) =>
        new("workspace", "run", "repository", 3,
            Guid.Parse("8c1d3b3e-9713-4658-b1ae-176c8c8bbf24"),
            new string('a', 40), new string('c', 40), string.Empty, files, "work-branch");

    private static GitWorkspaceCapturedFile File(string path, byte[] bytes) =>
        new(path, "100644", GitWorkspaceCapturePackage.Hash(bytes), bytes.Length, bytes.ToImmutableArray());

    private static SandboxBuildTestOutputObligation Obligation() =>
        new("result", "out/result.bin", true, 128);

    private static SandboxBuildTestOutputEvidence Evidence(GitWorkspaceCapturedFile file) =>
        new("collector-uid", SandboxBuildTestLimits.OutputCollectorContainerName,
            "result", file.Path, true, 128, true, file.ByteLength, file.Sha256);

    private sealed record SeedFixture(
        ImmutableArray<GitWorkspaceCapturedFile> Files,
        GitWorkspaceCaptureDocument Baseline,
        byte[] Bundle,
        byte[] Bytes,
        SandboxBuildTestWorkspaceSource Source);
}
