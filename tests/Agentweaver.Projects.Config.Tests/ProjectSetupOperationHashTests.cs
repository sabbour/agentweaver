using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class ProjectSetupOperationHashTests
{
    [Fact]
    public void SetupAndCleanupReceiptsReadWithoutOptionalOwnerGrantFields()
    {
        const string legacySetupJson =
            """
            {
              "invocationId": "62f18103-ea12-4c97-b749-9c26789a1d4a",
              "issuer": "https://issuer.example/",
              "subject": "subject-one",
              "tenantId": "tenant-one",
              "membershipId": "c2ea13f7-b56a-4a9a-8cb6-eaf7d76fe390",
              "membershipRevision": 1,
              "projectId": "0123456789abcdef0123456789abcdef",
              "createdByActorId": "subject-one",
              "projectRevision": 1,
              "projectConfigurationRevision": 1,
              "assignmentId": "1316e14f-1c10-4f9c-a52c-02ddce14d06c",
              "assignmentRevision": 1,
              "authorityAuditEventId": "bd03cdb3-aa6c-4125-a24e-a03346c1bda4",
              "createdAt": "2026-10-13T00:00:00+00:00"
            }
            """;
        const string legacyCleanupJson =
            """
            {
              "invocationId": "62f18103-ea12-4c97-b749-9c26789a1d4a",
              "tenantId": "tenant-one",
              "projectId": "0123456789abcdef0123456789abcdef",
              "assignmentId": "1316e14f-1c10-4f9c-a52c-02ddce14d06c",
              "state": "cleaned",
              "projectRevision": 2,
              "projectConfigurationRevision": 1,
              "assignmentRevision": 2,
              "authorityAuditEventId": "bd03cdb3-aa6c-4125-a24e-a03346c1bda4",
              "createdAt": "2026-10-13T00:00:00+00:00"
            }
            """;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

        var setup = JsonSerializer.Deserialize<ProjectSetupReceipt>(legacySetupJson, options);
        var cleanup = JsonSerializer.Deserialize<ProjectCleanupReceipt>(legacyCleanupJson, options);

        Assert.NotNull(setup);
        Assert.Null(setup.OwnerAssignmentId);
        Assert.Null(setup.OwnerAssignmentRevision);
        Assert.Null(setup.OwnerAuthorityAuditEventId);
        Assert.NotNull(cleanup);
        Assert.Null(cleanup.OwnerAssignmentId);
        Assert.Null(cleanup.OwnerAssignmentRevision);
        Assert.Null(cleanup.OwnerRevocationAuditEventId);
    }

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
