using Agentweaver.Api.Blueprints;
using Agentweaver.Squad.Model;
using FluentAssertions;

namespace Agentweaver.Tests.Blueprints;

public sealed class BlueprintBespokeCharterPathTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Directory.GetCurrentDirectory(), "test-artifacts", "bespoke-charter-paths", Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(
        Directory.GetCurrentDirectory(), "test-artifacts", "bespoke-charter-outside", Guid.NewGuid().ToString("N"));

    public BlueprintBespokeCharterPathTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    [Fact]
    public void ExistingOrdinaryInProjectCharter_IsAccepted()
    {
        var charter = Path.Combine(_root, "charters", "reviewer.md");
        Directory.CreateDirectory(Path.GetDirectoryName(charter)!);
        File.WriteAllText(charter, "# Reviewer");

        var resolution = BlueprintService.ResolvePathWithinProject(
            _root, Path.Combine("charters", "reviewer.md"));

        resolution.Kind.Should().Be(CharterPathResolutionKind.ExistingSafe);
        File.ReadAllText(resolution.ResolvedPath!).Should().Be("# Reviewer");
    }

    [Fact]
    public void PosixInProjectFileSymlink_IsAccepted()
    {
        if (!IsPosix)
            return;

        var target = Path.Combine(_root, "charters", "reviewer.md");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "# Reviewer");
        File.CreateSymbolicLink(Path.Combine(_root, "reviewer.md"), target);

        var resolution = BlueprintService.ResolvePathWithinProject(_root, "reviewer.md");

        resolution.Kind.Should().Be(CharterPathResolutionKind.ExistingSafe);
        File.ReadAllText(resolution.ResolvedPath!).Should().Be("# Reviewer");
    }

    [Fact]
    public void PosixInProjectDirectorySymlink_IsAccepted()
    {
        if (!IsPosix)
            return;

        var targetDirectory = Path.Combine(_root, "charters-target");
        Directory.CreateDirectory(targetDirectory);
        File.WriteAllText(Path.Combine(targetDirectory, "reviewer.md"), "# Reviewer");
        Directory.CreateSymbolicLink(Path.Combine(_root, "charters"), targetDirectory);

        var resolution = BlueprintService.ResolvePathWithinProject(
            _root, Path.Combine("charters", "reviewer.md"));

        resolution.Kind.Should().Be(CharterPathResolutionKind.ExistingSafe);
        File.ReadAllText(resolution.ResolvedPath!).Should().Be("# Reviewer");
    }

    [Fact]
    public void OrdinaryMissingCharter_UsesMissingFileDiagnostic()
    {
        var errors = BlueprintService.ValidateBespokeCharterReferences(
            BlueprintWithCharter("file:charters/missing.md"), _root);

        errors.Should().ContainSingle()
            .Which.Should().Be("bespoke role 'bespoke' references missing charter file 'charters/missing.md'.");
    }

    [Fact]
    public void ExistingDirectoryCharter_UsesMissingFileDiagnostic()
    {
        Directory.CreateDirectory(Path.Combine(_root, "charters", "reviewer.md"));

        BlueprintService.ResolvePathWithinProject(_root, Path.Combine("charters", "reviewer.md")).Kind
            .Should().Be(CharterPathResolutionKind.MissingOrdinary);

        var errors = BlueprintService.ValidateBespokeCharterReferences(
            BlueprintWithCharter("file:charters/reviewer.md"), _root);

        errors.Should().ContainSingle()
            .Which.Should().Be("bespoke role 'bespoke' references missing charter file 'charters/reviewer.md'.");
    }

    [Fact]
    public void PosixInProjectDirectorySymlinkFinalTarget_UsesMissingFileDiagnostic()
    {
        if (!IsPosix)
            return;

        var targetDirectory = Path.Combine(_root, "charters-target");
        Directory.CreateDirectory(targetDirectory);
        File.CreateSymbolicLink(Path.Combine(_root, "reviewer.md"), targetDirectory);

        BlueprintService.ResolvePathWithinProject(_root, "reviewer.md").Kind
            .Should().Be(CharterPathResolutionKind.MissingOrdinary);

        var errors = BlueprintService.ValidateBespokeCharterReferences(
            BlueprintWithCharter("file:reviewer.md"), _root);

        errors.Should().ContainSingle()
            .Which.Should().Be("bespoke role 'bespoke' references missing charter file 'reviewer.md'.");
    }

    [Fact]
    public void PosixBrokenFileSymlink_IsInvalidUnsafe()
    {
        if (!IsPosix)
            return;

        File.CreateSymbolicLink(Path.Combine(_root, "broken.md"), Path.Combine(_outside, "missing.md"));

        BlueprintService.ResolvePathWithinProject(_root, "broken.md").Kind
            .Should().Be(CharterPathResolutionKind.InvalidUnsafe);
    }

    [Fact]
    public void PosixBrokenDirectorySymlink_IsInvalidUnsafe()
    {
        if (!IsPosix)
            return;

        Directory.CreateSymbolicLink(Path.Combine(_root, "charters"), Path.Combine(_outside, "missing"));

        BlueprintService.ResolvePathWithinProject(_root, Path.Combine("charters", "reviewer.md")).Kind
            .Should().Be(CharterPathResolutionKind.InvalidUnsafe);
    }

    [Fact]
    public void OverlongCharterPath_IsInvalidUnsafe()
    {
        var overlong = new string('a', 32_769) + ".md";

        BlueprintService.ResolvePathWithinProject(_root, overlong).Kind
            .Should().Be(CharterPathResolutionKind.InvalidUnsafe);
    }

    [Fact]
    public void PosixLoopingLink_IsInvalidUnsafe()
    {
        if (!IsPosix)
            return;

        var loop = Path.Combine(_root, "loop");
        Directory.CreateSymbolicLink(loop, "loop");
        BlueprintService.ResolvePathWithinProject(_root, Path.Combine("loop", "charter.md")).Kind
            .Should().Be(CharterPathResolutionKind.InvalidUnsafe);
    }

    [Fact]
    public void PosixEscapingFileSymlink_IsInvalidUnsafe()
    {
        if (!IsPosix)
            return;

        var outsideCharter = Path.Combine(_outside, "outside.md");
        File.WriteAllText(outsideCharter, "# Outside");
        File.CreateSymbolicLink(Path.Combine(_root, "charter.md"), outsideCharter);

        BlueprintService.ResolvePathWithinProject(_root, "charter.md").Kind
            .Should().Be(CharterPathResolutionKind.InvalidUnsafe);
    }

    [Fact]
    public void PosixEscapingDirectorySymlink_IsInvalidUnsafe()
    {
        if (!IsPosix)
            return;

        var outsideCharter = Path.Combine(_outside, "outside.md");
        File.WriteAllText(outsideCharter, "# Outside");
        Directory.CreateSymbolicLink(Path.Combine(_root, "charters"), _outside);

        BlueprintService.ResolvePathWithinProject(_root, Path.Combine("charters", "outside.md")).Kind
            .Should().Be(CharterPathResolutionKind.InvalidUnsafe);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_outside, recursive: true); } catch { }
    }

    private static bool IsPosix => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    private static Blueprint BlueprintWithCharter(string charter) => new(
        "test",
        "Test",
        "Test blueprint",
        ["bespoke"],
        ["default"],
        "default",
        "default")
    {
        BespokeRoles = [new BespokeRole("bespoke", "Bespoke", charter)],
    };
}
