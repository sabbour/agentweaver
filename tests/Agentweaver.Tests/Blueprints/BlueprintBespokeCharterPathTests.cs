using Agentweaver.Api.Blueprints;
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

        var valid = BlueprintService.TryResolvePathWithinProject(
            _root, Path.Combine("charters", "reviewer.md"), out var resolved, out var exists);

        valid.Should().BeTrue();
        exists.Should().BeTrue();
        File.ReadAllText(resolved).Should().Be("# Reviewer");
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

        var valid = BlueprintService.TryResolvePathWithinProject(
            _root, "reviewer.md", out var resolved, out var exists);

        valid.Should().BeTrue();
        exists.Should().BeTrue();
        File.ReadAllText(resolved).Should().Be("# Reviewer");
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

        var valid = BlueprintService.TryResolvePathWithinProject(
            _root, Path.Combine("charters", "reviewer.md"), out var resolved, out var exists);

        valid.Should().BeTrue();
        exists.Should().BeTrue();
        File.ReadAllText(resolved).Should().Be("# Reviewer");
    }

    [Fact]
    public void PosixEscapingFileSymlink_IsRejected()
    {
        if (!IsPosix)
            return;

        var outsideCharter = Path.Combine(_outside, "outside.md");
        File.WriteAllText(outsideCharter, "# Outside");
        var link = Path.Combine(_root, "charter.md");
        File.CreateSymbolicLink(link, outsideCharter);

        BlueprintService.TryResolvePathWithinProject(_root, "charter.md", out _, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void PosixEscapingDirectorySymlink_IsRejected()
    {
        if (!IsPosix)
            return;

        var outsideCharter = Path.Combine(_outside, "outside.md");
        File.WriteAllText(outsideCharter, "# Outside");
        var link = Path.Combine(_root, "charters");
        Directory.CreateSymbolicLink(link, _outside);

        BlueprintService.TryResolvePathWithinProject(
            _root, Path.Combine("charters", "outside.md"), out _, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void OverlongCharterPath_IsRejectedAsValidationFailure()
    {
        var overlong = new string('a', 32_769) + ".md";

        BlueprintService.TryResolvePathWithinProject(_root, overlong, out _, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void PosixBrokenAndLoopingLinks_AreRejected()
    {
        if (!IsPosix)
            return;

        var broken = Path.Combine(_root, "broken.md");
        File.CreateSymbolicLink(broken, Path.Combine(_outside, "missing.md"));
        BlueprintService.TryResolvePathWithinProject(_root, "broken.md", out _, out _)
            .Should().BeFalse();

        var loop = Path.Combine(_root, "loop");
        Directory.CreateSymbolicLink(loop, "loop");
        BlueprintService.TryResolvePathWithinProject(_root, Path.Combine("loop", "charter.md"), out _, out _)
            .Should().BeFalse();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_outside, recursive: true); } catch { }
    }

    private static bool IsPosix => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();
}
