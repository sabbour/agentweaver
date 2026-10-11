using System.Buffers.Binary;
using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Xunit;

namespace Agentweaver.SourceControl.Tests;

public sealed class GitWorkspaceCaptureDocumentTests
{
    [Fact]
    public void CompleteDocumentRetainsManifestPatchAndBinaryFileBytes()
    {
        var document = Document();

        var bytes = GitWorkspaceCapturePackage.SerializeDocument(document);
        var parsed = GitWorkspaceCapturePackage.ParseDocument(bytes);

        Assert.Equal(document.ManifestBytes, parsed.ManifestBytes);
        Assert.Equal(document.PatchBytes, parsed.PatchBytes);
        Assert.Equal(document.PackageBytes, parsed.PackageBytes);
        Assert.Equal(document.ManifestSha256, parsed.ManifestSha256);
        Assert.Equal(document.PatchSha256, parsed.PatchSha256);
        Assert.Equal(document.PackageSha256, parsed.PackageSha256);
        Assert.Equal(new byte[] { 0, 255, 128, 10 },
            GitWorkspaceCapturePackage.ExtractFile(
                parsed.PackageBytes, parsed.Manifest, parsed.PackageSha256, "out/result.bin"));
    }

    [Theory]
    [InlineData("header")]
    [InlineData("manifest-limit")]
    [InlineData("patch-limit")]
    [InlineData("package-limit")]
    [InlineData("overflow")]
    [InlineData("short")]
    [InlineData("extra")]
    [InlineData("file-bytes")]
    public void DocumentRejectsInvalidLengthsOrChangedFileBytes(string fault)
    {
        var bytes = GitWorkspaceCapturePackage.SerializeDocument(Document());
        switch (fault)
        {
            case "header":
                bytes[0] ^= 1;
                break;
            case "manifest-limit":
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4),
                    ProducedRunCaptureLimits.MaximumManifestBytes + 1u);
                break;
            case "patch-limit":
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12, 4),
                    ProducedRunCaptureLimits.MaximumDiffBytes + 1u);
                break;
            case "package-limit":
                BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(16, 8),
                    ProducedRunCaptureLimits.MaximumPackageBytes + 1ul);
                break;
            case "overflow":
                BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(16, 8), ulong.MaxValue);
                break;
            case "short":
                bytes = bytes[..^1];
                break;
            case "extra":
                bytes = [.. bytes, 0];
                break;
            case "file-bytes":
                bytes[^1] ^= 1;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fault));
        }

        Assert.Throws<ArgumentException>(() => GitWorkspaceCapturePackage.ParseDocument(bytes));
    }

    [Fact]
    public void DocumentRejectsInconsistentSavedMetadata()
    {
        var document = Document();
        var changed = document with { ManifestSha256 = new string('b', 64) };

        Assert.Throws<ArgumentException>(() => GitWorkspaceCapturePackage.SerializeDocument(changed));
    }

    [Fact]
    public void TransferLimitIncludesAllExistingDocumentSections()
    {
        Assert.Equal(
            24 + ProducedRunCaptureLimits.MaximumManifestBytes +
            ProducedRunCaptureLimits.MaximumDiffBytes + ProducedRunCaptureLimits.MaximumPackageBytes,
            ProducedRunCaptureLimits.MaximumDocumentBytes);
        var bytes = new byte[ProducedRunCaptureLimits.MaximumDocumentBytes + 1];

        Assert.Throws<ArgumentException>(() => GitWorkspaceCapturePackage.ParseDocument(bytes));
    }

    private static GitWorkspaceCaptureDocument Document()
    {
        var content = ImmutableArray.Create<byte>(0, 255, 128, 10);
        return GitWorkspaceCapturePackage.Create(new GitWorkspaceCapture(
            "workspace", "run", "repository", 1,
            Guid.Parse("5a2daa59-cda2-4594-9b4b-92d0c261b5d9"),
            new string('a', 40), new string('b', 40), "binary patch\n",
            [new GitWorkspaceCapturedFile(
                "out/result.bin", "100644", GitWorkspaceCapturePackage.Hash(content.AsSpan()),
                content.Length, content)],
            "main"));
    }
}
