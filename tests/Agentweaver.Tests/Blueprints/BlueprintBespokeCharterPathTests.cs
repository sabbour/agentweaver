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
    public void EscapingFileSymlink_IsRejectedWhenSupported()
    {
        var outsideCharter = Path.Combine(_outside, "outside.md");
        File.WriteAllText(outsideCharter, "# Outside");
        var link = Path.Combine(_root, "charter.md");
        if (!TryCreateFileLink(link, outsideCharter))
            return;

        BlueprintService.TryResolvePathWithinProject(_root, "charter.md", out _, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void EscapingDirectorySymlink_IsRejectedWhenSupported()
    {
        var outsideCharter = Path.Combine(_outside, "outside.md");
        File.WriteAllText(outsideCharter, "# Outside");
        var link = Path.Combine(_root, "charters");
        if (!TryCreateDirectoryLink(link, _outside))
            return;

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
    public void LinuxBrokenAndLoopingLinks_AreRejected()
    {
        if (!OperatingSystem.IsLinux())
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

    private static bool TryCreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
