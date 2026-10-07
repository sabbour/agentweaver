using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using Xunit;

namespace Agentweaver.Environment.Tests;

public sealed class EnvironmentSandboxManagerTests
{
    [Fact]
    public async Task ProvisionRequiresFreshWriteProjectsAuthorityBeforeSelectionOrProviderAccess()
    {
        var projects = new FakeProjectsConfigClient();
        var provider = new RecordingSandboxProvider();
        var options = new AgentSandboxOptions(
            1,
            "sandbox-options-1",
            "agentweaver",
            "azure-files-csi",
            "ghcr.io/agentweaver/agenthost:1",
            "kata-vm",
            "kata-qemu",
            "500m",
            "512Mi",
            1,
            1);
        var egressManager = new EnvironmentEgressManager(
            projects,
            cilium: null!,
            providerOptions: null!,
            lifecycleStore: null!);
        var manager = new EnvironmentSandboxManager(
            projects,
            lifecycleStore: null!,
            leaseStore: null!,
            provider,
            options,
            kubernetesClient: null!,
            ciliumOptions: null!,
            egressManager);

        var exception = await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
            manager.ProvisionAsync(
                new CurrentCallerRequest("validated.jwt.token", "tenant-a"),
                "project-a",
                "run-a",
                "environment-a",
                new SandboxProvisionApiRequest(
                    "workspace-a",
                    1,
                    0,
                    "/workspace/agentweaver/project",
                    ReadOnly: false,
                    NetworkPolicyGeneration: 1,
                    IdempotencyKey: "provision-a"),
                CancellationToken.None));

        Assert.Equal("project_write_not_authorized", exception.Code);
        Assert.Equal(0, projects.RunSelectionCalls);
        Assert.Equal(0, provider.ProvisionCalls);
    }

    private sealed class FakeProjectsConfigClient : IProjectsConfigClient
    {
        private static readonly ProjectAuthorizationContextResponse Context = new(
            ProjectAuthorizationContextContract.CurrentVersion,
            "https://projects.example",
            "actor-a",
            "tenant-a",
            3,
            "project-a",
            "run-a",
            [
                new EffectiveProjectAuthorization(
                    ProjectAuthorityResourceType.Project,
                    "project-a",
                    [new ProjectAuthorizationPermissionGrant(ProjectAuthorizationPermission.ReadProjects, 1)])
            ]);

        public int RunSelectionCalls { get; private set; }

        public Task<ProjectAuthorizationContextResponse> GetAuthorizationContextAsync(
            CurrentCallerRequest caller,
            CancellationToken cancellationToken) =>
            Task.FromResult(Context);

        public Task<EffectiveNetworkPolicySelection> GetRunSelectionAsync(
            CurrentCallerRequest caller,
            string projectId,
            string runId,
            CancellationToken cancellationToken)
        {
            RunSelectionCalls++;
            return Task.FromResult(new EffectiveNetworkPolicySelection(
                projectId,
                runId,
                1,
                1,
                1,
                "selection-1",
                [],
                [],
                null,
                [],
                []));
        }
    }

    private sealed class RecordingSandboxProvider : ISandboxProvider
    {
        public int ProvisionCalls { get; private set; }

        public Task<SandboxProvisionedResource> ProvisionAsync(
            SandboxProvisionRequest request,
            CancellationToken cancellationToken = default)
        {
            ProvisionCalls++;
            throw new InvalidOperationException("The provider must not be reached without WriteProjects.");
        }

        public Task<SandboxObservation> DescribeAsync(
            SandboxDescribeRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Describe is not part of an unauthorized provision.");

        public Task<IReadOnlyList<SandboxObservation>> ListOwnedAsync(
            SandboxListOwnedRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("ListOwned is not part of an unauthorized provision.");

        public Task<SandboxReleaseReceipt> ReleaseAsync(
            SandboxReleaseRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Release is not part of an unauthorized provision.");
    }
}
