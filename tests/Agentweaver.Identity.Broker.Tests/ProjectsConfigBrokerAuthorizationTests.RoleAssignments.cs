extern alias ProjectsConfig;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Agentweaver.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using ProjectsConfig::Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed partial class ProjectsConfigBrokerAuthorizationTests
{
    [Fact]
    public async Task TenantAdminAssignsAndRevokesProjectRolesThroughPublicApi()
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath, _signingCertificate.Password);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            _connectionString, new X509SecurityKey(certificate), grantRoleAssignmentWrites: true);

        var adminToken = await IssueTokenAsync("projects.admin", [TenantId], "role-api-admin", null, null, []);
        var adminSubject = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(adminToken).Claims, "sub");
        var adminMembership = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, adminSubject, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            adminMembership.MembershipId,
            ProjectAuthorityResourceType.Tenant,
            TenantId,
            ProjectAuthorityRole.TenantAdmin);

        var memberToken = await IssueTokenAsync("projects.admin", [TenantId], "role-api-member", null, null, []);
        var memberSubject = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(memberToken).Claims, "sub");
        await AddMembershipAsync(projects.PrivilegedFixtureDataSource, memberSubject, TenantId);

        var project = await CreateProjectsTestProjectAsync(projects.Client, adminToken, TenantId, "Role API");
        var path = $"/api/projects/{project.ProjectId}/role-assignments";

        using (var self = await PostRoleAsync(projects.Client, path, adminToken, adminSubject, "owner"))
            Assert.Equal(HttpStatusCode.Created, self.StatusCode);

        using var created = await PostRoleAsync(projects.Client, path, adminToken, memberSubject, "orchestrator");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("no-store", created.Headers.CacheControl?.ToString());
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var assignment = createdBody.RootElement;
        var assignmentId = assignment.GetProperty("assignmentId").GetGuid();
        Assert.Equal($"{path}/{assignmentId:D}", created.Headers.Location?.OriginalString);
        Assert.Equal(project.ProjectId, assignment.GetProperty("projectId").GetString());
        Assert.Equal(memberSubject, assignment.GetProperty("principalId").GetString());
        Assert.Equal("orchestrator", assignment.GetProperty("role").GetString());
        Assert.Equal("active", assignment.GetProperty("state").GetString());
        Assert.Equal(1, assignment.GetProperty("revision").GetInt64());
        Assert.Equal(adminSubject, assignment.GetProperty("grantedBy").GetString());

        using (var duplicate = await PostRoleAsync(projects.Client, path, adminToken, memberSubject, "orchestrator"))
            await AssertProblemAsync(duplicate, HttpStatusCode.Conflict, "role_assignment_exists");
        using (var tenantRole = await PostRoleAsync(projects.Client, path, adminToken, memberSubject, "tenantAdmin"))
            await AssertProblemAsync(tenantRole, HttpStatusCode.BadRequest, "invalid_project_role");
        using (var stranger = await PostRoleAsync(projects.Client, path, adminToken, "not-a-member", "viewer"))
            await AssertProblemAsync(stranger, HttpStatusCode.NotFound, "principal_not_found");
        using (var nonAdmin = await PostRoleAsync(projects.Client, path, memberToken, memberSubject, "owner"))
            Assert.Equal(HttpStatusCode.Forbidden, nonAdmin.StatusCode);
        using (var withQuery = await PostRoleAsync(projects.Client, path + "?force=true", adminToken, memberSubject, "viewer"))
            await AssertProblemAsync(withQuery, HttpStatusCode.BadRequest, "invalid_request");

        using (var memberList = await SendAsync(projects.Client, HttpMethod.Get, path, memberToken, [TenantId]))
            Assert.Equal(HttpStatusCode.Forbidden, memberList.StatusCode);
        using (var listWithQuery = await SendAsync(
            projects.Client, HttpMethod.Get, path + "?state=revoked", adminToken, [TenantId]))
            await AssertProblemAsync(listWithQuery, HttpStatusCode.BadRequest, "invalid_request");
        using (var adminList = await SendAsync(projects.Client, HttpMethod.Get, path, adminToken, [TenantId]))
        {
            Assert.Equal(HttpStatusCode.OK, adminList.StatusCode);
            Assert.Equal("no-store", adminList.Headers.CacheControl?.ToString());
            using var listBody = JsonDocument.Parse(await adminList.Content.ReadAsStringAsync());
            Assert.Equal(project.ProjectId, listBody.RootElement.GetProperty("projectId").GetString());
            var items = listBody.RootElement.GetProperty("assignments").EnumerateArray().ToArray();
            Assert.Equal(2, items.Length);
            var listed = Assert.Single(items, item => item.GetProperty("assignmentId").GetGuid() == assignmentId);
            Assert.Equal(
                ["assignmentId", "principalId", "role", "revision", "createdBy", "createdAt"],
                listed.EnumerateObject().Select(property => property.Name).ToArray());
            Assert.Equal(memberSubject, listed.GetProperty("principalId").GetString());
            Assert.Equal("orchestrator", listed.GetProperty("role").GetString());
            Assert.Equal(1, listed.GetProperty("revision").GetInt64());
            Assert.Equal(adminSubject, listed.GetProperty("createdBy").GetString());
        }

        // A project Owner without TenantAdmin can read the list but still cannot change it.
        using (var memberOwner = await PostRoleAsync(projects.Client, path, adminToken, memberSubject, "owner"))
            Assert.Equal(HttpStatusCode.Created, memberOwner.StatusCode);
        using (var ownerList = await SendAsync(projects.Client, HttpMethod.Get, path, memberToken, [TenantId]))
        {
            Assert.Equal(HttpStatusCode.OK, ownerList.StatusCode);
            using var ownerListBody = JsonDocument.Parse(await ownerList.Content.ReadAsStringAsync());
            Assert.Equal(3, ownerListBody.RootElement.GetProperty("assignments").GetArrayLength());
        }

        using (var missingRevision = await SendAsync(
            projects.Client, HttpMethod.Delete, $"{path}/{assignmentId:D}", adminToken, [TenantId]))
            await AssertProblemAsync(missingRevision, HttpStatusCode.BadRequest, "invalid_expected_revision");
        using (var signedRevision = await SendAsync(
            projects.Client, HttpMethod.Delete, $"{path}/{assignmentId:D}?expectedRevision=+1", adminToken, [TenantId]))
            await AssertProblemAsync(signedRevision, HttpStatusCode.BadRequest, "invalid_expected_revision");
        using (var stale = await SendAsync(
            projects.Client, HttpMethod.Delete, $"{path}/{assignmentId:D}?expectedRevision=2", adminToken, [TenantId]))
            await AssertProblemAsync(stale, HttpStatusCode.Conflict, "revision_conflict");
        using (var memberRevoke = await SendAsync(
            projects.Client, HttpMethod.Delete, $"{path}/{assignmentId:D}?expectedRevision=1", memberToken, [TenantId]))
            Assert.Equal(HttpStatusCode.Forbidden, memberRevoke.StatusCode);

        using var revoked = await SendAsync(
            projects.Client, HttpMethod.Delete, $"{path}/{assignmentId:D}?expectedRevision=1", adminToken, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        using var revokedBody = JsonDocument.Parse(await revoked.Content.ReadAsStringAsync());
        Assert.Equal("revoked", revokedBody.RootElement.GetProperty("state").GetString());
        Assert.Equal(2, revokedBody.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal(adminSubject, revokedBody.RootElement.GetProperty("revokedBy").GetString());
        using (var afterRevoke = await SendAsync(projects.Client, HttpMethod.Get, path, adminToken, [TenantId]))
        {
            using var afterBody = JsonDocument.Parse(await afterRevoke.Content.ReadAsStringAsync());
            Assert.DoesNotContain(
                afterBody.RootElement.GetProperty("assignments").EnumerateArray(),
                item => item.GetProperty("assignmentId").GetGuid() == assignmentId);
        }

        await using var connection = await projects.PrivilegedFixtureDataSource.OpenConnectionAsync();
        await using var audit = new NpgsqlCommand(
            """
            SELECT string_agg(event_type || ':' || revision || ':' || actor, ',' ORDER BY revision)
            FROM projects_config.authority_audit
            WHERE assignment_id = @assignment_id
            """,
            connection);
        audit.Parameters.AddWithValue("assignment_id", assignmentId);
        Assert.Equal(
            $"role_assigned:1:{adminSubject},role_revoked:2:{adminSubject}",
            await audit.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ProjectRoleAssignmentReturnsUnavailableWithoutRuntimeGrants()
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath, _signingCertificate.Password);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            _connectionString, new X509SecurityKey(certificate));
        var adminToken = await IssueTokenAsync("projects.admin", [TenantId], "role-api-ungranted", null, null, []);
        var adminSubject = SingleClaim(new JwtSecurityTokenHandler().ReadJwtToken(adminToken).Claims, "sub");
        var adminMembership = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, adminSubject, TenantId);
        await AssignRoleAsync(
            projects.PrivilegedFixtureDataSource,
            adminMembership.MembershipId,
            ProjectAuthorityResourceType.Tenant,
            TenantId,
            ProjectAuthorityRole.TenantAdmin);
        var project = await CreateProjectsTestProjectAsync(projects.Client, adminToken, TenantId, "Role API ungranted");

        using var response = await PostRoleAsync(
            projects.Client, $"/api/projects/{project.ProjectId}/role-assignments", adminToken, adminSubject, "owner");

        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "role_assignment_unavailable");
        // Listing needs only the existing read grants.
        using var list = await SendAsync(
            projects.Client, HttpMethod.Get, $"/api/projects/{project.ProjectId}/role-assignments", adminToken, [TenantId]);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listBody = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.Equal(0, listBody.RootElement.GetProperty("assignments").GetArrayLength());
        await AssertRuntimeCannotWriteAuthorityAsync(projects.RuntimeDataSource);
    }

    private static async Task<HttpResponseMessage> PostRoleAsync(
        HttpClient client, string path, string token, string principalId, string role)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new { principalId, role }),
        };
        AddBearerAndTenant(request, token, TenantId);
        return await client.SendAsync(request);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"Expected {status}, got {response.StatusCode}: {body}");
        Assert.Contains(code, body, StringComparison.Ordinal);
    }
}
