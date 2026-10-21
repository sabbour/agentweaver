using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

/// <summary>Covers grouping, archive, and draft packaging for local skill uploads.</summary>
public sealed class SkillContentPackagerTests
{
    private static SkillContentPreview Preview(SkillContentCandidate candidate)
    {
        var content = candidate.Validate();
        return new SkillContentPreview(
            content.Name, content.Description, content.ContentDigest, content.Resources.Length, 0);
    }

    private static byte[] Skill(string name) =>
        Encoding.UTF8.GetBytes($"---\nname: {name}\ndescription: {name} skill\n---\n\nUse {name}.\n");

    private static SkillPackageFileRequest File(string path, byte[] content) =>
        new() { RelativePath = path, Content = content };

    private static SkillPackageFileRequest File(string path, string content) =>
        File(path, Encoding.UTF8.GetBytes(content));

    private static SkillContentPackageResult PackageFiles(params SkillPackageFileRequest[] files) =>
        SkillContentPackager.Package(
            new PackageSkillContentRequest { Files = files.Cast<SkillPackageFileRequest?>().ToImmutableArray() },
            Preview);

    [Fact]
    public void DraftComposesSkillMarkdownAndPreview()
    {
        var result = SkillContentPackager.Package(
            new PackageSkillContentRequest
            {
                Draft = new SkillPackageDraftRequest
                {
                    Name = " review-helper ", Description = "Reviews \"diffs\"", Instructions = " Check tests. ",
                },
            },
            Preview);

        var package = Assert.Single(result.Skills);
        Assert.Null(package.Error);
        Assert.Equal("SKILL.md", package.Location);
        Assert.Equal("review-helper", package.Preview!.Name);
        Assert.Equal("Reviews \"diffs\"", package.Preview.Description);
        Assert.Equal(
            "---\nname: \"review-helper\"\ndescription: \"Reviews \\\"diffs\\\"\"\n---\n\nCheck tests.\n",
            Encoding.UTF8.GetString(package.Candidate!.SkillMarkdown));
    }

    [Fact]
    public void DraftWithoutDescriptionUsesName()
    {
        var result = SkillContentPackager.Package(
            new PackageSkillContentRequest
            {
                Draft = new SkillPackageDraftRequest { Name = "notes", Instructions = "Write notes." },
            },
            Preview);

        Assert.Equal("notes", Assert.Single(result.Skills).Preview!.Description);
    }

    [Fact]
    public void FolderGroupsResourcesByNearestSkillRoot()
    {
        var result = PackageFiles(
            File("pack/alpha/SKILL.md", Skill("alpha")),
            File("pack/alpha/docs/guide.md", "guide"),
            File("pack/alpha/beta/SKILL.md", Skill("beta")),
            File("pack/alpha/beta/notes.txt", "notes"),
            File("pack/readme.md", "outside"));

        Assert.Equal(["pack/alpha", "pack/alpha/beta"], result.Skills.Select(skill => skill.Location));
        var alpha = result.Skills[0];
        Assert.Equal(["docs/guide.md"], alpha.Candidate!.Resources.Select(resource => resource!.RelativePath));
        var beta = result.Skills[1];
        Assert.Equal(["notes.txt"], beta.Candidate!.Resources.Select(resource => resource!.RelativePath));
        Assert.Equal(1, beta.Preview!.ResourceCount);
    }

    [Fact]
    public void BinaryAndOversizedFilesAreSkippedAndListed()
    {
        var result = PackageFiles(
            File("SKILL.md", Skill("alpha")),
            File("image.png", new byte[] { 0x89, 0x00, 0x01 }),
            File("big.txt", new string('a', SkillContentValidator.MaxResourceBytes + 1)),
            File("ok.md", "ok"));

        var package = Assert.Single(result.Skills);
        Assert.Equal(["big.txt", "image.png"], package.SkippedPaths.ToArray());
        Assert.Equal(["ok.md"], package.Candidate!.Resources.Select(resource => resource!.RelativePath));
    }

    [Fact]
    public void InvalidSkillReturnsErrorWithoutFailingOtherSkills()
    {
        var result = PackageFiles(
            File("good/SKILL.md", Skill("good")),
            File("bad/SKILL.md", "no frontmatter"));

        var bad = Assert.Single(result.Skills, skill => skill.Location == "bad");
        Assert.Null(bad.Candidate);
        Assert.NotNull(bad.Error);
        Assert.NotNull(Assert.Single(result.Skills, skill => skill.Location == "good").Preview);
    }

    [Fact]
    public void ArchiveIsReadWithTheSameGrouping()
    {
        var archive = Zip(("tools/SKILL.md", Skill("tools")), ("tools/run.md", Encoding.UTF8.GetBytes("run")));

        var result = SkillContentPackager.Package(new PackageSkillContentRequest { Archive = archive }, Preview);

        var package = Assert.Single(result.Skills);
        Assert.Equal("tools", package.Preview!.Name);
        Assert.Equal(["run.md"], package.Candidate!.Resources.Select(resource => resource!.RelativePath));
    }

    [Fact]
    public void ArchiveExpansionLimitIsEnforced()
    {
        var archive = Zip(
            ("SKILL.md", Skill("big")),
            ("a.txt", new byte[SkillContentPackager.MaxUploadBytes / 2 + 1]),
            ("b.txt", new byte[SkillContentPackager.MaxUploadBytes / 2 + 1]));

        var exception = Assert.Throws<SkillContentValidationException>(() =>
            SkillContentPackager.Package(new PackageSkillContentRequest { Archive = archive }, Preview));
        Assert.Contains("expands", exception.Message);
    }

    [Theory]
    [InlineData("../escape/SKILL.md")]
    [InlineData("/abs/SKILL.md")]
    public void ArchivePathTraversalIsRejected(string path)
    {
        var archive = Zip((path, Skill("escape")));

        Assert.Throws<SkillContentValidationException>(() =>
            SkillContentPackager.Package(new PackageSkillContentRequest { Archive = archive }, Preview));
    }

    [Fact]
    public void InvalidArchiveIsRejected()
    {
        var exception = Assert.Throws<SkillContentValidationException>(() =>
            SkillContentPackager.Package(new PackageSkillContentRequest { Archive = [1, 2, 3] }, Preview));
        Assert.Contains("ZIP", exception.Message);
    }

    [Fact]
    public void RequestNeedsExactlyOneInput()
    {
        Assert.Throws<SkillContentValidationException>(() =>
            SkillContentPackager.Package(new PackageSkillContentRequest(), Preview));
        Assert.Throws<SkillContentValidationException>(() =>
            SkillContentPackager.Package(
                new PackageSkillContentRequest
                {
                    Archive = [1],
                    Draft = new SkillPackageDraftRequest { Name = "a", Instructions = "b" },
                },
                Preview));
    }

    [Fact]
    public void UploadWithoutSkillFileIsRejected()
    {
        var exception = Assert.Throws<SkillContentValidationException>(() =>
            PackageFiles(File("readme.md", "text")));
        Assert.Contains("SKILL.md", exception.Message);
    }

    [Fact]
    public void DuplicateAndUnsafePathsAreRejected()
    {
        Assert.Throws<SkillContentValidationException>(() =>
            PackageFiles(File("SKILL.md", Skill("a")), File("Notes.md", "x"), File("notes.md", "y")));
        Assert.Throws<SkillContentValidationException>(() =>
            PackageFiles(File("SKILL.md", Skill("a")), File("../notes.md", "x")));
    }

    [Fact]
    public void UploadSizeLimitIsEnforced()
    {
        Assert.Throws<SkillContentValidationException>(() =>
            PackageFiles(
                File("SKILL.md", Skill("a")),
                File("a.txt", new byte[SkillContentPackager.MaxUploadBytes])));
    }

    private static byte[] Zip(params (string Path, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                entryStream.Write(content);
            }
        }
        return stream.ToArray();
    }
}
