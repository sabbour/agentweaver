using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class ProjectAuthorizationContextTests
{
    private const string ProjectId = "project-1";
    private const string RunId = "run-1";

    /// <summary>Cancellation follows project access, while creating a run still needs an Orchestrator assignment.</summary>
    [Theory]
    [InlineData(ProjectAuthorityRole.Owner, true)]
    [InlineData(ProjectAuthorityRole.Contributor, true)]
    [InlineData(ProjectAuthorityRole.Viewer, false)]
    [InlineData(ProjectAuthorityRole.Orchestrator, false)]
    public void UnboundCancellationPermissionMatchesProjectRole(ProjectAuthorityRole role, bool canCancel)
    {
        var caller = CreateContext([Grant(ProjectId, role, 2)],
            ["api.read", "projects.orchestrator"], boundProjectId: null, boundRunId: null);
        Assert.Equal(canCancel, HasPermission(caller.ToEffectiveResponse(), ProjectAuthorizationPermission.CancelRun));
        var bound = CreateContext([Grant(ProjectId, role, 2)], ["api.read", "projects.orchestrator"]);
        Assert.False(HasPermission(bound.ToEffectiveResponse(), ProjectAuthorizationPermission.CancelRun));
        Assert.Equal(role == ProjectAuthorityRole.Orchestrator,
            HasPermission(caller.ToEffectiveResponse(), ProjectAuthorizationPermission.AcceptRunSelection));
    }

    /// <summary>Tenant administration can cancel tenant project runs but does not grant Orchestrator admission.</summary>
    [Fact]
    public void TenantAdminCancellationIsTenantScopedAndUnbound()
    {
        var grant = new ProjectAuthorizationGrant(
            Guid.NewGuid(), ProjectAuthorityResourceType.Tenant, "tenant", ProjectAuthorityRole.TenantAdmin, 3);
        var caller = CreateContext([grant], ["api.read", "projects.orchestrator"],
            boundProjectId: null, boundRunId: null);
        var authority = Assert.Single(caller.ToEffectiveResponse().EffectiveAuthority);
        Assert.Equal("tenant", authority.ResourceId);
        Assert.Contains(authority.Permissions, permission => permission.Permission == ProjectAuthorizationPermission.CancelRun);
        Assert.DoesNotContain(authority.Permissions, permission =>
            permission.Permission == ProjectAuthorizationPermission.AcceptRunSelection);
    }

    [Fact]
    public void RunBoundOwnerAndOrchestratorWithSupportedScopesCanReadPrivateKnowledge()
    {
        var context = CreateContext(
            [
                Grant(ProjectId, ProjectAuthorityRole.Owner, 2),
                Grant(ProjectId, ProjectAuthorityRole.Orchestrator, 3),
            ],
            ["api.read", "projects.orchestrator"]);

        var response = context.ToEffectiveResponse();
        Assert.True(HasPermission(response, ProjectAuthorizationPermission.AccessPrivateKnowledge));
        Assert.False(HasPermission(response, ProjectAuthorizationPermission.WriteProjects));
        Assert.False(HasPermission(response, ProjectAuthorizationPermission.CreateProjects));
        Assert.Equal(ProjectId, response.BoundProjectId);
        Assert.Equal(RunId, response.BoundRunId);
        Assert.Throws<ProjectConfigException>(() =>
            context.RequireExactRunBinding(ProjectId, "other-run"));
        Assert.Throws<ProjectConfigException>(() =>
            context.RequireExactRunBinding("other-project", RunId));
    }

    [Fact]
    public void UnboundOwnerWithAdminScopeRetainsPrivateKnowledgeAccess()
    {
        var context = CreateContext(
            [Grant(ProjectId, ProjectAuthorityRole.Owner, 2)],
            ["api.read", "projects.admin"],
            boundProjectId: null,
            boundRunId: null);

        Assert.True(HasPrivateKnowledge(context));
    }

    [Fact]
    public void RunBoundPrivateKnowledgeRequiresCurrentSameProjectOwnerAndOrchestratorAuthority()
    {
        var ownerOnly = CreateContext(
            [Grant(ProjectId, ProjectAuthorityRole.Owner, 2)],
            ["api.read", "projects.orchestrator"]);
        var orchestratorOnly = CreateContext(
            [Grant(ProjectId, ProjectAuthorityRole.Orchestrator, 3)],
            ["api.read", "projects.orchestrator"]);
        var differentProjectOrchestrator = CreateContext(
            [
                Grant(ProjectId, ProjectAuthorityRole.Owner, 2),
                Grant("other-project", ProjectAuthorityRole.Orchestrator, 3),
            ],
            ["api.read", "projects.orchestrator"]);
        var adminOnlyOwnerRetainsRunBoundKnowledgeAccess = CreateContext(
            [Grant(ProjectId, ProjectAuthorityRole.Owner, 2)],
            ["api.read", "projects.admin"]);
        var missingRunScope = CreateContext(
            [
                Grant(ProjectId, ProjectAuthorityRole.Owner, 2),
                Grant(ProjectId, ProjectAuthorityRole.Orchestrator, 3),
            ],
            ["api.read"]);
        var missingApiRead = CreateContext(
            [
                Grant(ProjectId, ProjectAuthorityRole.Owner, 2),
                Grant(ProjectId, ProjectAuthorityRole.Orchestrator, 3),
            ],
            ["projects.orchestrator"]);
        var unboundOwnerAndOrchestrator = CreateContext(
            [
                Grant(ProjectId, ProjectAuthorityRole.Owner, 2),
                Grant(ProjectId, ProjectAuthorityRole.Orchestrator, 3),
            ],
            ["api.read", "projects.orchestrator"],
            boundProjectId: null,
            boundRunId: null);
        var viewerAndOrchestrator = CreateContext(
            [
                Grant(ProjectId, ProjectAuthorityRole.Viewer, 2),
                Grant(ProjectId, ProjectAuthorityRole.Orchestrator, 3),
            ],
            ["api.read", "projects.orchestrator"]);
        var contributorAndOrchestrator = CreateContext(
            [
                Grant(ProjectId, ProjectAuthorityRole.Contributor, 2),
                Grant(ProjectId, ProjectAuthorityRole.Orchestrator, 3),
            ],
            ["api.read", "projects.orchestrator"]);

        Assert.False(HasPrivateKnowledge(ownerOnly));
        Assert.False(HasPrivateKnowledge(orchestratorOnly));
        Assert.False(HasPrivateKnowledge(differentProjectOrchestrator));
        Assert.True(HasPrivateKnowledge(adminOnlyOwnerRetainsRunBoundKnowledgeAccess));
        Assert.False(HasPrivateKnowledge(missingRunScope));
        Assert.False(HasPrivateKnowledge(unboundOwnerAndOrchestrator));
        Assert.False(HasPrivateKnowledge(viewerAndOrchestrator));
        Assert.False(HasPrivateKnowledge(contributorAndOrchestrator));
        Assert.Throws<ProjectConfigException>(() => missingApiRead.ToEffectiveResponse());
    }

    private static ProjectAuthorizationContext CreateContext(
        IEnumerable<ProjectAuthorizationGrant> grants,
        IEnumerable<string> scopes,
        string? boundProjectId = ProjectId,
        string? boundRunId = RunId) =>
        new(
            "https://issuer.test/",
            "actor",
            "tenant",
            Guid.NewGuid(),
            1,
            grants.ToImmutableArray(),
            ImmutableHashSet.CreateRange(StringComparer.Ordinal, scopes),
            boundProjectId,
            boundRunId,
            null);

    private static ProjectAuthorizationGrant Grant(
        string projectId,
        ProjectAuthorityRole role,
        long revision) =>
        new(Guid.NewGuid(), ProjectAuthorityResourceType.Project, projectId, role, revision);

    private static bool HasPrivateKnowledge(ProjectAuthorizationContext context) =>
        HasPermission(context.ToEffectiveResponse(), ProjectAuthorizationPermission.AccessPrivateKnowledge);

    private static bool HasPermission(
        ProjectAuthorizationContextResponse response,
        ProjectAuthorizationPermission permission) =>
        response.EffectiveAuthority
            .Where(resource =>
                resource.ResourceType == ProjectAuthorityResourceType.Project &&
                resource.ResourceId == ProjectId)
            .SelectMany(resource => resource.Permissions)
            .Any(grant => grant.Permission == permission);
}
