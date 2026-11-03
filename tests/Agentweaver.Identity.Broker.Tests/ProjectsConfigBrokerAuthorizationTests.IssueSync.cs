extern alias ProjectsConfig;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Agentweaver.Abstractions;
using Microsoft.IdentityModel.Tokens;
using ProjectsConfig::Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed partial class ProjectsConfigBrokerAuthorizationTests
{
    [Fact]
    public async Task IssueSyncProofRequiresCurrentExactOwnerAndSelectedRepository()
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath, _signingCertificate.Password);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            _connectionString, new X509SecurityKey(certificate), grantRoleAssignmentWrites: true);
        var token = await IssueTokenAsync("projects.admin", [TenantId], "issue-sync-owner", null, null, []);
        var subject = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(token).Claims, "sub");
        var membership = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, subject, TenantId);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, membership.MembershipId,
            ProjectAuthorityResourceType.Tenant, TenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await CreateProjectsTestProjectAsync(projects.Client, token, TenantId, "Issue sync");
        var ownerPath = $"/api/projects/{project.ProjectId}/issue-sync-owner";
        var repositoryPath = $"/api/projects/{project.ProjectId}/issue-sync-authority";
        var roleAssignmentsPath = $"/api/projects/{project.ProjectId}/role-assignments";
        var tenantAdminToken = await IssueTokenAsync(
            "projects.admin", [TenantId], "issue-sync-tenant-admin", null, null, []);
        var tenantAdminSubject = SingleClaim(
            new JwtSecurityTokenHandler().ReadJwtToken(tenantAdminToken).Claims, "sub");
        var tenantAdminMembership = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, tenantAdminSubject, TenantId);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, tenantAdminMembership.MembershipId,
            ProjectAuthorityResourceType.Tenant, TenantId, ProjectAuthorityRole.TenantAdmin);

        using (var tenantAdminOnly = await SendAsync(
            projects.Client, HttpMethod.Get, ownerPath, tenantAdminToken, [TenantId]))
            Assert.Equal(HttpStatusCode.Forbidden, tenantAdminOnly.StatusCode);
        using (var adminRevocation = await SendAsync(projects.Client, HttpMethod.Get,
            $"/api/projects/{project.ProjectId}/issue-sync-grant-revocation", tenantAdminToken, [TenantId]))
        {
            Assert.Equal(HttpStatusCode.OK, adminRevocation.StatusCode);
            Assert.Contains("\"isAdministrator\":true", await adminRevocation.Content.ReadAsStringAsync());
        }
        using (var owner = await SendAsync(projects.Client, HttpMethod.Get, ownerPath, token, [TenantId]))
        {
            Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
            Assert.Equal("no-store", owner.Headers.CacheControl?.ToString());
        }
        Guid ownerAssignmentId;
        using (var listed = await SendAsync(
            projects.Client, HttpMethod.Get, roleAssignmentsPath, token, [TenantId]))
        {
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            using var document = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
            var assignment = Assert.Single(document.RootElement.GetProperty("assignments").EnumerateArray(),
                item => item.GetProperty("principalId").GetString() == subject &&
                    item.GetProperty("role").GetString() == "owner");
            ownerAssignmentId = assignment.GetProperty("assignmentId").GetGuid();
            Assert.Equal(1, assignment.GetProperty("revision").GetInt64());
            Assert.Equal(subject, assignment.GetProperty("createdBy").GetString());
        }
        using (var disconnected = await SendAsync(projects.Client, HttpMethod.Get, repositoryPath, token, [TenantId]))
        {
            Assert.Equal(HttpStatusCode.Conflict, disconnected.StatusCode);
            Assert.Contains("issue_sync_repository_not_selected", await disconnected.Content.ReadAsStringAsync());
        }
        using (var foreign = await SendAsync(projects.Client, HttpMethod.Get, ownerPath, token, [OtherTenantId]))
            Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        using (var privateRoute = await projects.Client.PostAsync("/internal/issue-sync/project-authority", null))
            Assert.Equal(HttpStatusCode.NotFound, privateRoute.StatusCode);
        var replacement = await AddMembershipAsync(
            projects.PrivilegedFixtureDataSource, Guid.NewGuid().ToString("D"), TenantId);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, replacement.MembershipId,
            ProjectAuthorityResourceType.Project, project.ProjectId, ProjectAuthorityRole.Owner);
        await RevokeRoleAsync(projects.PrivilegedFixtureDataSource, ownerAssignmentId, 1);
        using var revoked = await SendAsync(projects.Client, HttpMethod.Get, ownerPath, token, [TenantId]);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
    }
}
