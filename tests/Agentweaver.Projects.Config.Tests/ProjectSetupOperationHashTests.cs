using Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class ProjectSetupOperationHashTests
{
    [Fact]
    public void HashBindsInvocationPhaseActorTenantAndCleanupRevisionsButNotToken()
    {
        var invocationId = Guid.Parse("62f18103-ea12-4c97-b749-9c26789a1d4a");
        var setup = new ProjectSetupOperationRequest(
            1, invocationId, ProjectSetupOperationKind.Setup, "tenant-one", null, null);
        var cleanup = new ProjectSetupOperationRequest(
            1, invocationId, ProjectSetupOperationKind.Cleanup, "tenant-one", 1, 1);

        var setupHash = ProjectsConfigPrivilegedAuthorityStore.ComputeProjectSetupRequestHash(
            setup, "https://issuer.example/", "subject-one");
        Assert.Equal(
            setupHash,
            ProjectsConfigPrivilegedAuthorityStore.ComputeProjectSetupRequestHash(
                setup with { TenantId = "tenant-one" }, "https://issuer.example/", "subject-one"));
        Assert.NotEqual(
            setupHash,
            ProjectsConfigPrivilegedAuthorityStore.ComputeProjectSetupRequestHash(
                setup, "https://issuer.example/", "subject-two"));
        Assert.NotEqual(
            setupHash,
            ProjectsConfigPrivilegedAuthorityStore.ComputeProjectSetupRequestHash(
                setup with { TenantId = "tenant-two" }, "https://issuer.example/", "subject-one"));
        Assert.NotEqual(
            setupHash,
            ProjectsConfigPrivilegedAuthorityStore.ComputeProjectSetupRequestHash(
                cleanup, "https://issuer.example/", "subject-one"));
        Assert.NotEqual(
            ProjectsConfigPrivilegedAuthorityStore.ComputeProjectSetupRequestHash(
                cleanup, "https://issuer.example/", "subject-one"),
            ProjectsConfigPrivilegedAuthorityStore.ComputeProjectSetupRequestHash(
                cleanup with { ExpectedAssignmentRevision = 2 },
                "https://issuer.example/",
                "subject-one"));
    }
}
