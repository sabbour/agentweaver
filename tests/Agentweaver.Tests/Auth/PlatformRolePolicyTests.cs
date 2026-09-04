using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Agentweaver.Api.Auth;

namespace Agentweaver.Tests.Auth;

public sealed class PlatformRolePolicyTests
{
    [Fact]
    public void PlatformRoles_FilterRecognized_RetainsOnlyKnownRoles()
    {
        PlatformRoles.FilterRecognized(["intruder", PlatformRoles.ProjectCreator, PlatformRoles.Viewer, "reader"])
            .Should().BeEquivalentTo([PlatformRoles.ProjectCreator, PlatformRoles.Viewer]);
    }

    [Fact]
    public async Task RecognizedPlatformRole_SatisfiesRequirement()
    {
        var principal = BuildPrincipal(PlatformRoles.PlatformAdmin);
        var requirement = new PlatformRoleRequirement();
        var context = new AuthorizationHandlerContext([requirement], principal, resource: null);

        await new PlatformRoleAuthorizationHandler().HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task UnrecognizedRole_DoesNotSatisfyRequirement()
    {
        var principal = BuildPrincipal("UnknownAdmin");
        var requirement = new PlatformRoleRequirement();
        var context = new AuthorizationHandlerContext([requirement], principal, resource: null);

        await new PlatformRoleAuthorizationHandler().HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task InternalService_BypassesRoleRequirement()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(
                AgentweaverClaimTypes.AuthenticationScheme,
                AgentweaverAuthenticationSchemes.InternalServiceKey),
            new Claim(AgentweaverClaimTypes.InternalService, "true"),
        ], authenticationType: "test-internal");
        var principal = new ClaimsPrincipal(identity);
        var requirement = new PlatformRoleRequirement();
        var context = new AuthorizationHandlerContext([requirement], principal, resource: null);

        await new PlatformRoleAuthorizationHandler().HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact(Skip = "project-operation role policies are tracked separately from the endpoint auth cutover")]
    public async Task ProjectCreator_CanCreateProjects_ButCannotAccessPlatformAdminOnlyOperations()
    {
        await Task.CompletedTask;
    }

    [Fact(Skip = "project-operation role policies are tracked separately from the endpoint auth cutover")]
    public async Task Contributor_CannotSatisfy_ProjectCreationRequirement_UnlessPolicyExplicitlyAllowsIt()
    {
        await Task.CompletedTask;
    }

    [Fact(Skip = "project-operation role policies are tracked separately from the endpoint auth cutover")]
    public async Task Viewer_IsReadOnly_AndCannotSatisfyMutationRequirements()
    {
        await Task.CompletedTask;
    }

    [Fact(Skip = "covered by Entra request-level authorization tests")]
    public async Task MissingAppRole_FailsRequirement_ServerSide()
    {
        await Task.CompletedTask;
    }

    private static ClaimsPrincipal BuildPrincipal(params string[] roles) =>
        new(new ClaimsIdentity(
        [
            new Claim("oid", "00000000-0000-0000-0000-000000000321"),
            .. roles.Select(role => new Claim(ClaimTypes.Role, role)),
        ], authenticationType: "test-entra"));
}
