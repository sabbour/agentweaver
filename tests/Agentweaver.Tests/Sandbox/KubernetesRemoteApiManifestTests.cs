using System.Text.RegularExpressions;
using FluentAssertions;

namespace Agentweaver.Tests.Sandbox;

public sealed class KubernetesRemoteApiManifestTests
{
    private const string RemoteApiUrl =
        "http://agentweaver-api.agentweaver.svc.cluster.local:8080";

    [Fact]
    public void ApiAndWorkerDeployments_ConfigureRemoteUrlWithoutReplacingExistingApiUrl()
    {
        var api = ReadManifest("api-deployment.yaml");
        var worker = ReadManifest("worker-deployment.yaml");

        EnvironmentValue(api, "Agentweaver__ApiBaseUrl").Should().Be("http://localhost:8080");
        EnvironmentValue(api, "Agentweaver__RemoteApiBaseUrl").Should().Be(RemoteApiUrl);
        EnvironmentValue(worker, "Agentweaver__ApiBaseUrl").Should().Be("http://agentweaver-api:8080");
        EnvironmentValue(worker, "Agentweaver__RemoteApiBaseUrl").Should().Be(RemoteApiUrl);
    }

    [Fact]
    public void AgentHostTemplate_UsesClusterDnsAndNarrowSandboxLabels()
    {
        var template = ReadManifest("sandbox-template-agenthost.yaml");

        template.Should().Contain("dnsPolicy: ClusterFirst");
        template.Should().MatchRegex(@"(?m)^\s+app: agentweaver-agent-host\s*$");
        template.Should().MatchRegex(@"(?m)^\s+agentweaver\.dev/sandbox: ""true""\s*$");
    }

    /// <summary>
    /// The executor sidecar is the process boundary for every model-controlled command, so its
    /// hardening must be identical to AgentHost's — the fix must not buy Kata compatibility with
    /// privileges. Asserted here because a manifest regression would be invisible to the .NET tests.
    /// </summary>
    [Fact]
    public void AgentHostTemplate_DeclaresHardenedExecutorSidecar()
    {
        var template = ReadManifest("sandbox-template-agenthost.yaml");

        template.Should().MatchRegex(@"(?m)^\s+- name: agentweaver-exec\s*$");
        template.Should().MatchRegex(@"(?m)^\s+args: \[""--exec-agent""\]\s*$");

        var sidecar = template[template.IndexOf("- name: agentweaver-exec", StringComparison.Ordinal)..];
        sidecar = sidecar[..sidecar.IndexOf("      volumes:", StringComparison.Ordinal)];

        sidecar.Should().Contain("runAsNonRoot: true");
        sidecar.Should().Contain("allowPrivilegeEscalation: false");
        sidecar.Should().MatchRegex(@"drop: \[""ALL""\]");
        sidecar.Should().Contain("type: RuntimeDefault");
        sidecar.Should().Contain("/var/run/agentweaver-exec");
        // The container running model-controlled code must hold no cluster identity.
        sidecar.Should().Contain("/var/run/secrets/kubernetes.io/serviceaccount");
    }

    /// <summary>
    /// Issue #1008. This volume was a default <c>emptyDir</c> from #757 until the AKS katapool node
    /// image upgrade on 2026-08-27 brought Kata 3.32.0, where upstream flipped
    /// <c>disable_guest_empty_dir</c> to <c>true</c> — turning it into a per-container virtio-fs
    /// share, on which a cross-container AF_UNIX <c>connect()</c> returns <c>ECONNREFUSED</c>.
    /// <c>medium: Memory</c> takes Kata's tmpfs branch, which is evaluated independently of that
    /// setting, so this line is what keeps the executor reachable.
    /// </summary>
    [Fact]
    public void AgentHostTemplate_KeepsTheExecutorIpcVolumeOnAGuestOwnedTmpfs()
    {
        var template = ReadManifest("sandbox-template-agenthost.yaml");

        template.Should().MatchRegex(
            @"(?m)^\s+- name: exec-ipc\s*\r?\n\s+emptyDir:\s*\r?\n\s+medium: Memory\s*$",
            "a default emptyDir reaches the Kata guest over virtio-fs and cannot host the socket");
    }

    /// <summary>
    /// The sidecar's value is its own PID namespace. Every one of these settings would either merge
    /// the namespaces again or hand the sandbox host-level reach, so their absence is the invariant.
    /// </summary>
    [Fact]
    public void AgentHostTemplate_NeverWeakensIsolationToSatisfyKata()
    {
        // Comments intentionally NAME the prohibited settings when explaining why they are absent,
        // so the invariant is asserted against the YAML body only.
        var template = string.Join(
            '\n',
            ReadManifest("sandbox-template-agenthost.yaml")
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith('#')));

        template.Should().NotContain("shareProcessNamespace");
        template.Should().NotContain("hostPID");
        template.Should().NotContain("hostIPC");
        template.Should().NotContain("hostNetwork");
        template.Should().NotContain("hostPath");
        template.Should().NotContain("privileged: true");
        template.Should().NotContain("procMount");
        template.Should().NotContain("type: Unconfined");
        template.Should().NotContain("add:");
        template.Should().Contain("azure.workload.identity/skip-containers: \"agentweaver-exec\"");
    }

    [Fact]
    public void ApiIngress_AllowsOnlyDoublyLabelledAgentHostPodsOnTcp8080()
    {
        var document = DocumentNamed(
            ReadManifest("networkpolicy-agenthost.yaml"),
            "allow-agenthost-to-api");

        document.Should().MatchRegex(
            @"(?s)podSelector:\s+matchLabels:\s+app: agentweaver-api.*" +
            @"from:\s+- podSelector:\s+matchLabels:\s+" +
            @"app: agentweaver-agent-host\s+agentweaver\.dev/sandbox: ""true"".*" +
            @"ports:\s+- protocol: TCP\s+port: 8080");
        document.Should().NotContain("namespaceSelector");
    }

    [Fact]
    public void AgentHostEgress_ExplicitlyAllowsMcpOnTcp8080()
    {
        var document = DocumentNamed(
            ReadManifest("networkpolicy-agenthost-egress.yaml"),
            "agenthost-egress-allowlist");

        document.Should().Contain("app.kubernetes.io/component: agent-host");
        document.Should().Contain("app: agentweaver-mcp");
        document.Should().Contain("port: 8080");
    }

    [Fact]
    public void AgentHostEgress_ExplicitlyAllowsApiOnTcp8080()
    {
        var document = DocumentNamed(
            ReadManifest("networkpolicy-agenthost-egress.yaml"),
            "agenthost-egress-allowlist");

        // #424: identity-based (podSelector) egress to the API pod is the only thing that
        // authorizes east-west traffic to the API ClusterIP under Cilium — a CIDR/ipBlock
        // allow (even 0.0.0.0/0) does not match in-cluster pod identities.
        document.Should().MatchRegex(
            @"(?s)- to:\s+- podSelector:\s+matchLabels:\s+app: agentweaver-api\s+" +
            @"ports:\s+- protocol: TCP\s+port: 8080");
    }

    [Fact]
    public void ExistingAgentHostPolicies_SelectRenamedAgentHostLabel()
    {
        var files = new[]
        {
            "networkpolicy-agenthost.yaml",
            "networkpolicy-agenthost-api-egress.yaml",
            "networkpolicy-sandbox.yaml",
            "networkpolicy-worker.yaml",
            "cilium-network-policy-sandbox.yaml",
        };

        files.Select(ReadManifest).Should().OnlyContain(
            manifest => !manifest.Contains("app: agentweaver-sandbox", StringComparison.Ordinal)
                && manifest.Contains("app: agentweaver-agent-host", StringComparison.Ordinal));
    }

    [Fact]
    public void SandboxEgress_IsScopedToPublicHttpsAndDeniesClusterAndPrivateRanges()
    {
        var document = DocumentNamed(
            ReadManifest("networkpolicy-sandbox.yaml"),
            "sandbox-egress-allowlist");

        // Public egress is restricted to TCP/443 (no more allow-all-ports).
        document.Should().MatchRegex(
            @"(?s)- ipBlock:\s+cidr: 0\.0\.0\.0/0\s+except:.*ports:\s+- protocol: TCP\s+port: 443");

        // Cluster/private/link-local ranges are denied to block lateral movement + IMDS SSRF.
        document.Should().Contain("10.0.0.0/8");
        document.Should().Contain("172.16.0.0/12");
        document.Should().Contain("192.168.0.0/16");
        document.Should().Contain("169.254.0.0/16");

        // The previous effective allow-all (0.0.0.0/0 excepting ONLY link-local, all ports) is gone.
        document.Should().NotContain("Unrestricted outbound egress");
    }

    [Fact]
    public void PublicMcp_UsesDedicatedLeastPrivilegeServiceAccount()
    {
        var deployment = ReadManifest("mcp-deployment.yaml");
        deployment.Should().Contain("serviceAccountName: agentweaver-mcp");
        deployment.Should().NotContain("serviceAccountName: agentweaver-api");

        var serviceAccount = ReadManifest("serviceaccount-mcp.yaml");
        serviceAccount.Should().MatchRegex(@"(?m)^\s+name: agentweaver-mcp\s*$");
        serviceAccount.Should().Contain("automountServiceAccountToken: false");

        // The MCP identity must NOT be bound to the pod-create/exec sandbox Role.
        var rbac = ReadManifest("rbac-api.yaml");
        rbac.Should().NotContain("agentweaver-mcp");
    }

    [Fact]
    public void ApiSandboxRole_GrantsPatchAndUpdateOnSandboxClaims()
    {
        // #570: RenewBackingClaimTtlAsync (#560/#564) JSON-merge-patches the backing SandboxClaim's
        // spec.lifecycle.ttlSecondsAfterFinished from KeepAliveAsync and both teardown-deferral paths.
        // Without patch/update on this rule every renewal 403s and the #564 fix is a silent no-op —
        // the sandbox controller still reaps the pod on the claim's original TTL. This test pins the
        // rule so a future edit cannot regress the verb list without failing CI.
        var rbac = ReadManifest("rbac-api.yaml");

        // The first sandboxclaims rule in the file belongs to the agentweaver-api-sandbox Role (the
        // worker Role's identical-looking rule is a separate, later block); matching directly on the
        // rule text avoids ambiguity between the Role and RoleBinding, which share the
        // `agentweaver-api-sandbox` metadata name.
        var rule = Regex.Match(
            rbac,
            @"(?s)apiGroups:\s+- extensions\.agents\.x-k8s\.io\s+resources:\s+- sandboxclaims\s+verbs:\s+(?<verbs>(?:- \S+\s*)+)");

        rule.Success.Should().BeTrue("the sandboxclaims rule must exist in the agentweaver-api-sandbox Role");
        var verbs = Regex.Matches(rule.Groups["verbs"].Value, @"- (\S+)")
            .Select(m => m.Groups[1].Value);

        verbs.Should().Contain(new[] { "get", "list", "create", "delete", "patch", "update" });
    }

    [Fact]
    public void ApiAndWorker_CannotMutateTemplatesPoolsOrWorkspacePvcs()
    {
        var rbac = ReadManifest("rbac-api.yaml");
        var templatePoolRules = Regex.Matches(
            rbac,
            @"(?ms)^\s{4}resources:\r?\n\s{6}- sandboxtemplates\r?\n\s{6}- sandboxwarmpools\r?\n" +
            @"\s{4}verbs:\r?\n(?<verbs>(?:\s{6}- \S+\r?\n)+)");

        templatePoolRules.Should().NotBeEmpty();
        foreach (Match rule in templatePoolRules)
        {
            var verbs = Regex.Matches(rule.Groups["verbs"].Value, @"- (\S+)")
                .Select(match => match.Groups[1].Value);
            verbs.Should().BeEquivalentTo(["get", "list"],
                "issue #476 does not overlap #481 by creating per-run templates, pools, or volumes");
        }
        rbac.Should().NotContain("persistentvolumeclaims");
    }

    [Fact]
    public void ProductionOverlay_EnablesAgentHostMtls()
    {
        var patch = ReadOverlayManifest("production", "patch-agenthost-mtls.yaml");

        patch.Should().Contain("name: agenthost-config");
        patch.Should().Contain("\"A2A\": {");
        patch.Should().Contain("\"Url\": \"https://0.0.0.0:8088\"");
        patch.Should().Contain("\"Path\": \"/mnt/a2a-tls/tls.crt\"");
        patch.Should().Contain("\"KeyPath\": \"/mnt/a2a-tls/tls.key\"");
        patch.Should().Contain("\"RequireMtls\": true");
        patch.Should().Contain("\"RequireClientCertificate\": true");
        patch.Should().Contain("\"SkipTlsHostnameVerification\": false");
    }

    private static string EnvironmentValue(string manifest, string variable)
    {
        var match = Regex.Match(
            manifest,
            $@"(?m)^\s+- name: {Regex.Escape(variable)}\s*\r?\n\s+value: (?<value>\S+)\s*$");

        match.Success.Should().BeTrue($"{variable} must be configured");
        return match.Groups["value"].Value;
    }

    private static string DocumentNamed(string manifest, string name) =>
        Regex.Split(manifest, @"(?m)^---\s*$")
            .Single(document => Regex.IsMatch(
                document,
                $@"(?m)^\s+name: {Regex.Escape(name)}\s*$"));

    private static string ReadManifest(string fileName) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "k8s", "base", fileName));

    private static string ReadOverlayManifest(string overlay, string fileName) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "k8s", "overlays", overlay, fileName));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "agentweaver.sln")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
