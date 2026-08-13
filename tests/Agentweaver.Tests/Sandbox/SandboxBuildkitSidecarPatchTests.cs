using System.Text.RegularExpressions;
using FluentAssertions;

namespace Agentweaver.Tests.Sandbox;

/// <summary>
/// The optional builder sidecar is applied by patching a <c>SandboxTemplate</c>, which is a custom
/// resource. kubectl cannot strategic-merge a custom resource, and a plain <c>--type merge</c>
/// patch <i>replaces</i> list fields instead of merging them by name — so shipping this patch in
/// merge shape would delete every other container and volume in the template the moment an operator
/// applied it. Measured against the live CRD with a server-side dry run:
///
/// <code>
/// --type merge  -> containers: probe-merge-behaviour             (the real container is gone)
/// --type json   -> containers: agentweaver-agent-host buildkitd  (appended, nothing lost)
/// </code>
///
/// These assertions keep the file in RFC 6902 shape so that failure mode cannot come back.
/// </summary>
public sealed class SandboxBuildkitSidecarPatchTests
{
    private static string PatchText() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "k8s", "optional", "sandbox-buildkit-sidecar.yaml"));

    /// <summary>
    /// The patch body with comment lines removed. The header deliberately *names* the things this
    /// file must not do ("no host namespaces, no hostPath"), so asserting against the raw text would
    /// flag the very prose that documents the constraint.
    /// </summary>
    private static string PatchBody() =>
        string.Join(
            '\n',
            PatchText()
                .Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => !line.TrimStart().StartsWith('#')));

    [Fact]
    public void Patch_IsAJsonPatchThatAppendsRatherThanAMergePatchThatReplaces()
    {
        var body = PatchBody();

        // A merge patch would start the document body with the resource shape.
        Regex.IsMatch(body, @"(?m)^spec:\s*$").Should().BeFalse(
            "a merge-shaped patch replaces the container and volume lists instead of appending to them");

        Regex.Matches(body, @"(?m)^- op: add$").Should().HaveCount(
            3,
            "the patch appends exactly one container and two tmpfs volumes");
        body.Should().Contain("path: /spec/podTemplate/spec/containers/-");
        body.Should().Contain("path: /spec/podTemplate/spec/volumes/-");
    }

    /// <summary>
    /// The documented apply command has to match the shape of the file. An operator who copies a
    /// <c>--type merge</c> line out of this header destroys the template.
    /// </summary>
    [Fact]
    public void Patch_DocumentsTheJsonPatchApplyCommandAndNeverAMergeApply()
    {
        var patch = PatchText();

        patch.Should().Contain("--type json --patch-file k8s/optional/sandbox-buildkit-sidecar.yaml");
        Regex.IsMatch(patch, @"kubectl patch[^\n]*--type merge")
            .Should().BeFalse("the apply instructions must never tell an operator to merge-patch this");
    }

    /// <summary>
    /// The builder is elevated; the untrusted container must not be. If this patch ever grows a
    /// privileged flag, a host namespace or a hostPath, the isolation argument in
    /// <c>docs/deep-dive/sandbox-pod-execution.md</c> stops being true.
    /// </summary>
    [Fact]
    public void Patch_KeepsTheElevationBoundedToTheBuilderContainer()
    {
        var body = PatchBody();

        body.Should().Contain("privileged: false");
        body.Should().Contain("type: RuntimeDefault");
        body.Should().NotContain("hostPath");
        body.Should().NotContain("hostNetwork");
        body.Should().NotContain("hostPID");
        body.Should().NotContain("shareProcessNamespace");
        body.Should().NotContain("privileged: true");

        // The socket has to be group-readable by the sandbox uid or nothing can build.
        body.Should().Contain("--group 1000");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "agentweaver.sln")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
