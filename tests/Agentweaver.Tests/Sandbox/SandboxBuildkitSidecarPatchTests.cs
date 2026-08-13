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
            4,
            "the patch appends one container and two tmpfs volumes, and sets the workload-identity opt-out");
        body.Should().Contain("path: /spec/podTemplate/spec/containers/-");
        body.Should().Contain("path: /spec/podTemplate/spec/volumes/-");
    }

    /// <summary>
    /// Both of these were found by patching the real template rather than a standalone probe pod.
    /// The base template sets <c>runAsNonRoot: true</c> / <c>runAsUser: 1000</c> /
    /// <c>runAsGroup: 1000</c> at pod level, and every line is inherited unless overridden:
    /// without <c>runAsNonRoot: false</c> the kubelet refuses to start the container
    /// (<c>CreateContainerConfigError: container's runAsUser breaks non-root policy</c>), and
    /// without <c>runAsGroup: 0</c> the daemon runs as uid 0 / gid 1000 and every build step dies in
    /// runc with <c>open container mntns: … permission denied</c>. Dropping either line ships a
    /// builder that cannot build.
    /// </summary>
    [Fact]
    public void Patch_OverridesThePodLevelNonRootAndGroupThatWouldOtherwiseBreakTheBuilder()
    {
        var body = PatchBody();

        body.Should().Contain("runAsUser: 0");
        body.Should().Contain("runAsGroup: 0");
        body.Should().Contain("runAsNonRoot: false");
    }

    /// <summary>
    /// Build steps must not share the pod's network namespace. Under <c>--oci-worker-net host</c> a
    /// <c>RUN</c> step joins it, and runc grants build steps the default capability set — which
    /// includes NET_RAW and cannot be reduced — so a Dockerfile could sniff the pod's plaintext
    /// traffic, including the <c>POST /configure</c> call that carries the run owner's GitHub token.
    /// Demonstrated on AKS Kata before the change (<c>tcpdump: listening on any</c> against
    /// <c>eth0 10.244.2.183</c>). CNI mode with a config that attaches nothing gives each step an
    /// empty namespace instead.
    /// </summary>
    [Fact]
    public void Patch_GivesBuildStepsAnEmptyNetworkNamespaceRatherThanThePods()
    {
        var body = PatchBody();

        body.Should().Contain("--oci-worker-net cni");
        body.Should().NotContain("--oci-worker-net host");
        body.Should().NotContain("--oci-worker-net bridge");

        // BuildKit reads a single CNI *conf* from this exact path and always also invokes a plugin
        // named `loopback`, so both files are required for the daemon to become ready.
        body.Should().Contain("/etc/buildkit/cni.json");
        body.Should().Contain("/opt/cni/bin/loopback");
    }

    /// <summary>
    /// The builder must hold no cluster or cloud identity, for the same reason the executor sidecar
    /// does not: it runs code that untrusted input reaches. Verified on-cluster — the
    /// service-account directory contains only <c>.</c> and <c>..</c>, and <c>env | grep -c ^AZURE_</c>
    /// returns 0.
    /// </summary>
    [Fact]
    public void Patch_LeavesTheBuilderWithoutAServiceAccountTokenOrWorkloadIdentity()
    {
        var body = PatchBody();

        body.Should().Contain("/var/run/secrets/kubernetes.io/serviceaccount");
        body.Should().Contain("name: exec-no-serviceaccount");
        body.Should().Contain("azure.workload.identity~1skip-containers");
        body.Should().Contain("agentweaver-exec,buildkitd");
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
