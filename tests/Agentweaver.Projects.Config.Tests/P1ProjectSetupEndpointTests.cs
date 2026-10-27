using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task P1RunBoundVerifierRequiresExactCurrentAuthorityAndNeverCreatesReceipts()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var service = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "p1-bound-tenant-" + suffix;
        var admin = await SeedCallerAsync(
            db, authorityStore, "p1-bound-platform-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var defaults = await service.GetPlatformRuntimeDefaultsAsync(admin, CancellationToken.None);
        await service.UpdatePlatformRuntimeDefaultsAsync(
            admin, defaults.Revision, PlatformDefaults(), CancellationToken.None);
        var caller = await SeedCallerAsync(
            db, authorityStore, "p1-bound-admin-" + suffix, tenantId,
            ["api.read", "projects.admin", "projects.orchestrator"],
            ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        var grant = Assert.Single(caller.Grants);
        var invocationId = Guid.NewGuid();
        var issued = await P1ProjectSetupEndpoints.IssueAsync(
            fixture.DataSource, caller, invocationId, grant, CancellationToken.None);
        caller = await ResolveCallerAsync(
            db, caller.ActorId, tenantId, ["api.read", "projects.admin", "projects.orchestrator"]);
        var projectId = issued.Receipt.ProjectId;
        var runId = "p1-bound-run-" + suffix;
        await service.AcceptInteractiveRunSelectionAsync(
            caller, projectId, runId,
            new InteractiveRunSelectionRequest(issued.Receipt.ProjectConfigurationRevision),
            CancellationToken.None);
        await using var app = await StartP1SetupServerAsync(fixture.DataSource);
        using var client = app.GetTestClient();
        var before = await CountsAsync();

        using (var request = BoundRequest(invocationId))
        {
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal(issued.Receipt, await response.Content.ReadFromJsonAsync<ProjectSetupReceipt>());
        }
        using (var missing = BoundRequest(Guid.NewGuid()))
            Assert.Equal(HttpStatusCode.Forbidden, await SendStatusAsync(client, missing));
        using (var wrongRun = BoundRequest(invocationId))
        {
            wrongRun.Headers.Remove("X-Test-Bound-Run");
            wrongRun.Headers.Add("X-Test-Bound-Run", "another-run");
            Assert.Equal(HttpStatusCode.Forbidden, await SendStatusAsync(client, wrongRun));
        }
        using (var unbound = BoundRequest(invocationId))
        {
            unbound.Headers.Remove("X-Test-Bound-Run");
            Assert.Equal(HttpStatusCode.Forbidden, await SendStatusAsync(client, unbound));
        }
        using (var wrongScope = BoundRequest(invocationId))
        {
            wrongScope.Headers.Remove("X-Test-Scope");
            wrongScope.Headers.Add("X-Test-Scope", "api.read projects.admin");
            Assert.Equal(HttpStatusCode.Forbidden, await SendStatusAsync(client, wrongScope));
        }
        using (var wrongActor = BoundRequest(invocationId))
        {
            wrongActor.Headers.Remove("X-Test-Subject");
            wrongActor.Headers.Add("X-Test-Subject", admin.ActorId);
            Assert.Equal(HttpStatusCode.Forbidden, await SendStatusAsync(client, wrongActor));
        }
        using (var wrongTenant = BoundRequest(invocationId))
        {
            wrongTenant.Headers.Remove(ProjectAuthorizationOwner.TenantSelectorHeader);
            wrongTenant.Headers.Add(ProjectAuthorizationOwner.TenantSelectorHeader, "foreign-tenant");
            Assert.Equal(HttpStatusCode.Forbidden, await SendStatusAsync(client, wrongTenant));
        }
        using (var query = BoundRequest(invocationId))
        {
            query.RequestUri = new Uri(query.RequestUri!.OriginalString + "?unexpected=1", UriKind.Relative);
            Assert.Equal(HttpStatusCode.BadRequest, await SendStatusAsync(client, query));
        }
        using (var body = BoundRequest(invocationId))
        {
            body.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.BadRequest, await SendStatusAsync(client, body));
        }
        Assert.Equal(before, await CountsAsync());
        await service.UpdateProjectConfigurationAsync(
            caller, projectId, issued.Receipt.ProjectConfigurationRevision, ProjectSettings(),
            CancellationToken.None);
        var afterConfigChange = await CountsAsync();
        using (var stale = BoundRequest(invocationId))
            Assert.Equal(HttpStatusCode.Conflict, await SendStatusAsync(client, stale));
        Assert.Equal(afterConfigChange, await CountsAsync());
        await authorityStore.RevokeRoleAssignmentAsync(grant.AssignmentId, grant.Revision, caller.ActorId);
        using (var revoked = BoundRequest(invocationId))
            Assert.Equal(HttpStatusCode.Forbidden, await SendStatusAsync(client, revoked));

        HttpRequestMessage BoundRequest(Guid id)
        {
            var request = SetupRequest(
                id, caller.ActorId, tenantId, scope: "api.read projects.orchestrator",
                boundProjectId: projectId, method: HttpMethod.Get);
            request.RequestUri = new Uri(
                $"/internal/projects/{projectId}/runs/{runId}/p1-setup/{id:D}", UriKind.Relative);
            request.Headers.Add("X-Test-Bound-Run", runId);
            return request;
        }

        async Task<(int Projects, int Roles, int Audits, int Operations)> CountsAsync() => (
            await db.Projects.CountAsync(), await db.RoleAssignments.CountAsync(),
            await db.AuthorityAudit.CountAsync(), await db.ProjectSetupOperations.CountAsync());
    }

    [Fact]
    public async Task P1SetupReadEndpointRechecksAuthorityAndNeverIssuesMissingReceipts()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var tenantId = $"p1-read-tenant-{Guid.NewGuid():N}";
        var caller = await SeedCallerAsync(
            db, authorityStore, $"p1-read-admin-{Guid.NewGuid():N}", tenantId,
            ["api.read", "projects.admin", "projects.orchestrator"],
            ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        var other = await SeedCallerAsync(
            db, authorityStore, $"p1-read-other-{Guid.NewGuid():N}", tenantId,
            ["api.read", "projects.admin", "projects.orchestrator"],
            ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        var foreignTenant = $"p1-read-foreign-{Guid.NewGuid():N}";
        var foreign = await SeedCallerAsync(
            db, authorityStore, $"p1-read-foreign-admin-{Guid.NewGuid():N}", foreignTenant,
            ["api.read", "projects.admin", "projects.orchestrator"],
            ProjectAuthorityResourceType.Tenant, foreignTenant, ProjectAuthorityRole.TenantAdmin);
        var grant = Assert.Single(caller.Grants.Where(item => item.Role == ProjectAuthorityRole.TenantAdmin));
        var invocationId = Guid.NewGuid();
        var issued = await P1ProjectSetupEndpoints.IssueAsync(
            fixture.DataSource, caller, invocationId, grant, CancellationToken.None);
        await using var app = await StartP1SetupServerAsync(fixture.DataSource);
        using var client = app.GetTestClient();
        var before = await CountsAsync();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = ReadRequest(invocationId);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(json.TryGetProperty("readback", out _));
            Assert.False(json.TryGetProperty("accessToken", out _));
            Assert.Equal(issued.Receipt, json.Deserialize<ProjectSetupReceipt>(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }

        using (var missing = ReadRequest(Guid.NewGuid()))
            await AssertProblemAsync(missing, HttpStatusCode.Forbidden, "forbidden");
        using (var wrongOwner = SetupRequest(
                   invocationId, other.ActorId, tenantId, method: HttpMethod.Get))
            await AssertProblemAsync(wrongOwner, HttpStatusCode.Forbidden, "forbidden");
        using (var foreignOwner = SetupRequest(
                   invocationId, foreign.ActorId, foreignTenant, method: HttpMethod.Get))
            await AssertProblemAsync(foreignOwner, HttpStatusCode.Forbidden, "forbidden");
        using (var missingScope = SetupRequest(
                   invocationId, caller.ActorId, tenantId, scope: "api.read projects.orchestrator",
                   method: HttpMethod.Get))
            await AssertProblemAsync(missingScope, HttpStatusCode.Forbidden, "forbidden");
        using (var bound = SetupRequest(
                   invocationId, caller.ActorId, tenantId, boundProjectId: issued.Receipt.ProjectId,
                   method: HttpMethod.Get))
            await AssertProblemAsync(bound, HttpStatusCode.Forbidden, "forbidden");
        using (var query = SetupRequest(
                   invocationId, caller.ActorId, tenantId, query: "?projectId=existing", method: HttpMethod.Get))
            await AssertProblemAsync(query, HttpStatusCode.BadRequest, "invalid_project_setup_request");
        using (var missingTenant = SetupRequest(
                   invocationId, caller.ActorId, null, method: HttpMethod.Get))
            await AssertProblemAsync(missingTenant, HttpStatusCode.BadRequest, "invalid_project_setup_request");
        using (var body = ReadRequest(invocationId))
        {
            body.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            await AssertProblemAsync(body, HttpStatusCode.BadRequest, "invalid_project_setup_request");
        }
        Assert.Equal(before, await CountsAsync());

        await authorityStore.RevokeRoleAssignmentAsync(
            issued.Receipt.AssignmentId, issued.Receipt.AssignmentRevision, caller.ActorId);
        var afterRevocation = await CountsAsync();
        using (var stale = ReadRequest(invocationId))
            await AssertProblemAsync(stale, HttpStatusCode.Conflict, "project_setup_receipt_stale");
        Assert.Equal(afterRevocation, await CountsAsync());

        await authorityStore.RevokeRoleAssignmentAsync(grant.AssignmentId, grant.Revision, caller.ActorId);
        afterRevocation = await CountsAsync();
        using (var revoked = ReadRequest(invocationId))
            await AssertProblemAsync(revoked, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal(afterRevocation, await CountsAsync());

        HttpRequestMessage ReadRequest(Guid id) =>
            SetupRequest(id, caller.ActorId, tenantId, method: HttpMethod.Get);

        async Task<(int Projects, int Roles, int Audits, int Operations)> CountsAsync() => (
            await db.Projects.CountAsync(),
            await db.RoleAssignments.CountAsync(),
            await db.AuthorityAudit.CountAsync(),
            await db.ProjectSetupOperations.CountAsync());

        async Task AssertProblemAsync(HttpRequestMessage request, HttpStatusCode status, string code)
        {
            using var response = await client.SendAsync(request);
            Assert.Equal(status, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task P1SetupEndpointIssuesExactReceiptAndRejectsChangedActorAndInvalidRequest()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var tenantId = $"p1-endpoint-tenant-{Guid.NewGuid():N}";
        var subject = $"p1-endpoint-admin-{Guid.NewGuid():N}";
        var caller = await SeedCallerAsync(
            db,
            authorityStore,
            subject,
            tenantId,
            ["api.read", "projects.admin", "projects.orchestrator"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var otherTenant = $"p1-endpoint-other-tenant-{Guid.NewGuid():N}";
        var otherCaller = await SeedCallerAsync(
            db,
            authorityStore,
            $"p1-endpoint-other-admin-{Guid.NewGuid():N}",
            otherTenant,
            ["api.read", "projects.admin", "projects.orchestrator"],
            ProjectAuthorityResourceType.Tenant,
            otherTenant,
            ProjectAuthorityRole.TenantAdmin);
        var adminGrant = Assert.Single(caller.Grants.Where(grant =>
            grant.ResourceType == ProjectAuthorityResourceType.Tenant &&
            grant.ResourceId == tenantId &&
            grant.Role == ProjectAuthorityRole.TenantAdmin));
        await using var app = await StartP1SetupServerAsync(fixture.DataSource);
        using var client = app.GetTestClient();
        var invocationId = Guid.NewGuid();

        using (var body = SetupRequest(invocationId, subject, tenantId))
        {
            body.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.BadRequest, await SendStatusAsync(client, body));
        }
        using (var query = SetupRequest(invocationId, subject, tenantId, query: "?unexpected=1"))
            Assert.Equal(HttpStatusCode.BadRequest, await SendStatusAsync(client, query));
        using (var missingTenant = SetupRequest(invocationId, subject, null))
            Assert.Equal(HttpStatusCode.BadRequest, await SendStatusAsync(client, missingTenant));
        using (var wrongTenant = SetupRequest(invocationId, subject, "another-tenant"))
            Assert.Equal(HttpStatusCode.Forbidden, await SendStatusAsync(client, wrongTenant));
        using (var wrongActor = SetupRequest(invocationId, otherCaller.ActorId, tenantId))
            Assert.Equal(HttpStatusCode.Forbidden, await SendStatusAsync(client, wrongActor));
        using (var missingScope = SetupRequest(
                   invocationId, subject, tenantId, scope: "api.read projects.orchestrator"))
            Assert.Equal(HttpStatusCode.Forbidden, await SendStatusAsync(client, missingScope));
        using (var boundRequest = SetupRequest(
                   invocationId, subject, tenantId, boundProjectId: "bound-project"))
            Assert.Equal(HttpStatusCode.Forbidden, await SendStatusAsync(client, boundRequest));
        using (var mismatchedAssertion = SetupRequest(
                   invocationId, subject, tenantId, tenantAssertion: "foreign-tenant"))
            Assert.Equal(HttpStatusCode.Forbidden, await SendStatusAsync(client, mismatchedAssertion));

        using var initial = SetupRequest(invocationId, subject, tenantId);
        using var response = await client.SendAsync(initial);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<ProjectSetupReceipt>();
        Assert.NotNull(receipt);
        Assert.Equal(invocationId, receipt.InvocationId);
        Assert.Equal(subject, receipt.Subject);
        Assert.Equal(tenantId, receipt.TenantId);
        Assert.Equal(caller.MembershipId, receipt.MembershipId);
        Assert.Equal(adminGrant.Revision, caller.Grants.Single(grant =>
            grant.AssignmentId == adminGrant.AssignmentId).Revision);
        Assert.NotNull(receipt.OwnerAssignmentId);

        using var replayRequest = SetupRequest(invocationId, subject, tenantId);
        using var replayResponse = await client.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        Assert.Equal(receipt, await replayResponse.Content.ReadFromJsonAsync<ProjectSetupReceipt>());

        var secondAdmin = await SeedCallerAsync(
            db,
            authorityStore,
            $"p1-endpoint-second-admin-{Guid.NewGuid():N}",
            tenantId,
            ["api.read", "projects.admin", "projects.orchestrator"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        using var changedOwner = SetupRequest(invocationId, secondAdmin.ActorId, tenantId);
        using var changedOwnerResponse = await client.SendAsync(changedOwner);
        Assert.Equal(HttpStatusCode.Conflict, changedOwnerResponse.StatusCode);

        await authorityStore.ExecuteProjectSetupOperationAsync(
            caller,
            new ProjectSetupOperationRequest(
                1,
                invocationId,
                ProjectSetupOperationKind.Cleanup,
                tenantId,
                receipt.ProjectRevision,
                receipt.AssignmentRevision));
        using var cleanedReplayRequest = SetupRequest(invocationId, subject, tenantId);
        using var cleanedReplayResponse = await client.SendAsync(cleanedReplayRequest);
        Assert.Equal(HttpStatusCode.Conflict, cleanedReplayResponse.StatusCode);
    }

    [Fact]
    public async Task P1SetupFunctionOnlyRoleCanIssueAndReadWithoutLedgerAccess()
    {
        await using var db = CreateDbContext();
        var ownerOptions = CreateDbContextOptions();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(ownerOptions, TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = $"p1-function-tenant-{suffix}";
        var caller = await SeedCallerAsync(
            db,
            authorityStore,
            $"p1-function-admin-{suffix}",
            tenantId,
            ["api.read", "projects.admin", "projects.orchestrator"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var adminGrant = Assert.Single(caller.Grants.Where(grant =>
            grant.ResourceType == ProjectAuthorityResourceType.Tenant &&
            grant.ResourceId == tenantId &&
            grant.Role == ProjectAuthorityRole.TenantAdmin));
        var roleName = $"aw_p1_fn_{suffix}";
        var password = Guid.NewGuid().ToString("N");
        var roleCreated = false;

        async Task ExecuteOwnerSqlAsync(string sql)
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            await ExecuteOwnerSqlAsync(
                $"CREATE ROLE \"{roleName}\" LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT");
            roleCreated = true;
            await ExecuteOwnerSqlAsync($"GRANT USAGE ON SCHEMA projects_config TO \"{roleName}\"");
            await ExecuteOwnerSqlAsync($"""
                GRANT EXECUTE ON FUNCTION projects_config.issue_p1_project(
                    uuid, text, text, text, uuid, bigint, uuid, bigint),
                    projects_config.read_p1_project(
                    uuid, text, text, text, uuid, bigint, uuid, bigint)
                TO "{roleName}"
                """);

            var restrictedConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                Username = roleName,
                Password = password,
                Pooling = false,
            }.ConnectionString;
            await using var restrictedDataSource = NpgsqlDataSource.Create(restrictedConnectionString);
            var invocationId = Guid.NewGuid();
            var issued = await P1ProjectSetupEndpoints.IssueAsync(
                restrictedDataSource, caller, invocationId, adminGrant, CancellationToken.None);
            Assert.Equal(invocationId, issued.Receipt.InvocationId);
            Assert.True(issued.Readback.ProjectExists);

            var replay = await P1ProjectSetupEndpoints.IssueAsync(
                restrictedDataSource, caller, invocationId, adminGrant, CancellationToken.None);
            Assert.Equal(issued, replay);
            var read = await P1ProjectSetupEndpoints.ReadAsync(
                restrictedDataSource, caller, invocationId, adminGrant, CancellationToken.None);
            Assert.Equal(issued, read);

            await using var connection = await restrictedDataSource.OpenConnectionAsync();
            await using var privilegeCheck = new NpgsqlCommand(
                """
                SELECT has_function_privilege(
                           current_user,
                           'projects_config.issue_p1_project(uuid, text, text, text, uuid, bigint, uuid, bigint)',
                           'EXECUTE')
                   AND has_function_privilege(
                           current_user,
                           'projects_config.read_p1_project(uuid, text, text, text, uuid, bigint, uuid, bigint)',
                           'EXECUTE')
                   AND NOT has_table_privilege(
                           current_user, 'projects_config.project_setup_operations', 'SELECT')
                   AND NOT has_table_privilege(
                           current_user, 'projects_config.project_setup_operations', 'INSERT')
                   AND NOT has_table_privilege(
                           current_user, 'projects_config.projects', 'INSERT')
                """,
                connection);
            Assert.True((bool?)await privilegeCheck.ExecuteScalarAsync());

            await using var directRead = new NpgsqlCommand(
                "SELECT * FROM projects_config.project_setup_operations WHERE invocation_id = @invocation_id",
                connection);
            directRead.Parameters.AddWithValue("invocation_id", invocationId);
            var denied = await Assert.ThrowsAsync<PostgresException>(() => directRead.ExecuteReaderAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);

            await ExecuteOwnerSqlAsync($"""
                REVOKE EXECUTE ON FUNCTION projects_config.issue_p1_project(
                    uuid, text, text, text, uuid, bigint, uuid, bigint) FROM "{roleName}";
                GRANT SELECT ON projects_config.tenant_memberships,
                    projects_config.project_role_assignments TO "{roleName}";
                """);
            await using var app = await StartP1SetupServerAsync(restrictedDataSource);
            using var client = app.GetTestClient();
            using var readRequest = SetupRequest(
                invocationId, caller.ActorId, tenantId, method: HttpMethod.Get);
            using var readResponse = await client.SendAsync(readRequest);
            Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
            Assert.Equal(issued.Receipt, await readResponse.Content.ReadFromJsonAsync<ProjectSetupReceipt>());
            var before = (
                await db.Projects.CountAsync(), await db.RoleAssignments.CountAsync(),
                await db.AuthorityAudit.CountAsync(), await db.ProjectSetupOperations.CountAsync());
            using var unknown = SetupRequest(
                Guid.NewGuid(), caller.ActorId, tenantId, method: HttpMethod.Get);
            Assert.Equal(HttpStatusCode.Forbidden, await SendStatusAsync(client, unknown));
            using var deniedIssue = SetupRequest(Guid.NewGuid(), caller.ActorId, tenantId);
            Assert.Equal(HttpStatusCode.Forbidden, await SendStatusAsync(client, deniedIssue));
            Assert.Equal(before, (
                await db.Projects.CountAsync(), await db.RoleAssignments.CountAsync(),
                await db.AuthorityAudit.CountAsync(), await db.ProjectSetupOperations.CountAsync()));
        }
        finally
        {
            if (roleCreated)
            {
                await ExecuteOwnerSqlAsync($"DROP OWNED BY \"{roleName}\"");
                await ExecuteOwnerSqlAsync($"DROP ROLE IF EXISTS \"{roleName}\"");
            }
        }
    }

    [Fact]
    public async Task P1SetupFunctionRejectsWrongActorRevocationAndPartialWrites()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = $"p1-revocation-tenant-{suffix}";
        var subject = $"p1-revocation-admin-{suffix}";
        var caller = await SeedCallerAsync(
            db,
            authorityStore,
            subject,
            tenantId,
            ["api.read", "projects.admin", "projects.orchestrator"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var adminGrant = Assert.Single(caller.Grants.Where(grant =>
            grant.ResourceType == ProjectAuthorityResourceType.Tenant &&
            grant.ResourceId == tenantId &&
            grant.Role == ProjectAuthorityRole.TenantAdmin));
        var otherCaller = await SeedCallerAsync(
            db,
            authorityStore,
            $"p1-revocation-other-admin-{suffix}",
            tenantId,
            ["api.read", "projects.admin", "projects.orchestrator"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var otherAdminGrant = Assert.Single(otherCaller.Grants.Where(grant =>
            grant.ResourceType == ProjectAuthorityResourceType.Tenant &&
            grant.ResourceId == tenantId &&
            grant.Role == ProjectAuthorityRole.TenantAdmin));
        var invocationId = Guid.NewGuid();

        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        {
            await using var dbTransaction = await connection.BeginTransactionAsync();
            await using (var trigger = new NpgsqlCommand(
                $"""
                CREATE FUNCTION projects_config.tests_fail_p1_setup_audit()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.invocation_id = '{invocationId}'::uuid THEN
                        RAISE EXCEPTION 'injected P1 setup failure' USING ERRCODE = 'P0001';
                    END IF;
                    RETURN NEW;
                END
                $$;
                CREATE TRIGGER tests_fail_p1_setup_audit
                BEFORE INSERT ON projects_config.authority_audit
                FOR EACH ROW EXECUTE FUNCTION projects_config.tests_fail_p1_setup_audit();
                """,
                connection,
                dbTransaction))
            {
                await trigger.ExecuteNonQueryAsync();
            }
            await dbTransaction.CommitAsync();
        }

        try
        {
            var failure = await Assert.ThrowsAsync<PostgresException>(() =>
                P1ProjectSetupEndpoints.IssueAsync(
                    fixture.DataSource, caller, invocationId, adminGrant, CancellationToken.None));
            Assert.Equal("P0001", failure.SqlState);
        }
        finally
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var removeTrigger = new NpgsqlCommand(
                """
                DROP TRIGGER IF EXISTS tests_fail_p1_setup_audit ON projects_config.authority_audit;
                DROP FUNCTION IF EXISTS projects_config.tests_fail_p1_setup_audit();
                """,
                connection);
            await removeTrigger.ExecuteNonQueryAsync();
        }

        Assert.Equal(0, await db.ProjectSetupOperations.CountAsync(
            operation => operation.InvocationId == invocationId));
        Assert.Equal(0, await db.Projects.CountAsync(project => project.Name == $"p1-{invocationId:D}"));

        var concurrentIssues = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            P1ProjectSetupEndpoints.IssueAsync(
                fixture.DataSource, caller, invocationId, adminGrant, CancellationToken.None)));
        var issued = concurrentIssues[0];
        Assert.All(concurrentIssues, result => Assert.Equal(issued, result));
        Assert.Equal(1, await db.ProjectSetupOperations.CountAsync(operation =>
            operation.InvocationId == invocationId && operation.OperationKind == ProjectSetupOperationKind.Setup));
        Assert.Equal(1, await db.Projects.CountAsync(project => project.ProjectId == issued.Receipt.ProjectId));
        Assert.Equal(1, await db.ProjectConfigurationRevisions.CountAsync(revision =>
            revision.ProjectId == issued.Receipt.ProjectId && revision.Revision == 1));
        Assert.Equal(2, await db.RoleAssignments.CountAsync(assignment =>
            assignment.ResourceType == ProjectAuthorityResourceType.Project &&
            assignment.ResourceId == issued.Receipt.ProjectId));
        Assert.Equal(2, await db.AuthorityAudit.CountAsync(audit => audit.InvocationId == invocationId));

        var wrongOwner = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            P1ProjectSetupEndpoints.IssueAsync(
                fixture.DataSource, otherCaller, invocationId, otherAdminGrant, CancellationToken.None));
        Assert.Equal("project_setup_invocation_conflict", wrongOwner.Code);

        await authorityStore.RevokeRoleAssignmentAsync(
            adminGrant.AssignmentId,
            adminGrant.Revision,
            subject);
        var revoked = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            P1ProjectSetupEndpoints.ReadAsync(
                fixture.DataSource, caller, invocationId, adminGrant, CancellationToken.None));
        Assert.Equal(403, revoked.StatusCode);

        var replacementAssignment = await authorityStore.AssignRoleAsync(
            caller.MembershipId,
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin,
            subject);
        var replacementGrant = new ProjectAuthorizationGrant(
            replacementAssignment.AssignmentId,
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin,
            replacementAssignment.Revision);
        var reassigned = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            P1ProjectSetupEndpoints.ReadAsync(
                fixture.DataSource, caller, invocationId, replacementGrant, CancellationToken.None));
        Assert.Equal(403, reassigned.StatusCode);

        Assert.NotNull(issued.Receipt.OwnerAssignmentId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task P1SetupEndpointBoundsIncompleteBodyProbe(bool runBound)
    {
        await using var app = await StartP1SetupServerAsync(fixture.DataSource);
        using var client = app.GetTestClient();
        using var request = SetupRequest(Guid.NewGuid(), "p1-body-probe-actor", "p1-body-probe-tenant");
        if (runBound)
        {
            request.Method = HttpMethod.Get;
            request.RequestUri = new Uri(
                $"/internal/projects/project-a/runs/run-a/p1-setup/{Guid.NewGuid():D}", UriKind.Relative);
        }
        request.Content = new StalledContent();
        request.Version = HttpVersion.Version20;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;

        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.RequestTimeout, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("request_body_timeout", (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code").GetString());
    }

    private async Task<WebApplication> StartP1SetupServerAsync(NpgsqlDataSource dataSource)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddAuthentication("p1-test")
            .AddScheme<AuthenticationSchemeOptions, P1SetupTestAuthenticationHandler>("p1-test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(dataSource);
        builder.Services.AddSingleton(new ProjectsConfigIdentityOptions(TestIssuer));
        builder.Services.AddDbContext<ProjectsConfigDbContext>((_, options) =>
            options.UseNpgsql(dataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema)));
        builder.Services.AddScoped<ProjectAuthorizationOwner>();
        builder.Services.AddSingleton(CreateProviderCatalog());
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<ProjectsConfigService>();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapP1ProjectSetupEndpoint();
        await app.StartAsync();
        return app;
    }

    private static HttpRequestMessage SetupRequest(
        Guid invocationId,
        string subject,
        string? tenantId,
        string? scope = null,
        string? boundProjectId = null,
        string? tenantAssertion = null,
        string? query = null,
        HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(
            method ?? HttpMethod.Post,
            $"/api/p1/invocations/{invocationId:D}/setup{query}");
        request.Headers.Add("X-Test-Subject", subject);
        request.Headers.Add(
            "X-Test-Scope",
            scope ?? "api.read projects.admin projects.orchestrator");
        if (tenantId is not null)
            request.Headers.Add(ProjectAuthorizationOwner.TenantSelectorHeader, tenantId);
        if (boundProjectId is not null)
            request.Headers.Add("X-Test-Bound-Project", boundProjectId);
        if (tenantAssertion is not null)
            request.Headers.Add("X-Test-Tenant-Assertion", tenantAssertion);
        return request;
    }

    private static async Task<HttpStatusCode> SendStatusAsync(
        HttpClient client,
        HttpRequestMessage request)
    {
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private sealed class StalledContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.Delay(Timeout.InfiniteTimeSpan);

        protected override Task SerializeToStreamAsync(
            Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    internal sealed class P1SetupTestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim>
            {
                new("sub", Request.Headers["X-Test-Subject"].ToString()),
                new("scope", Request.Headers["X-Test-Scope"].ToString()),
            };
            if (Request.Headers.TryGetValue("X-Test-Bound-Project", out var projectId))
                claims.Add(new Claim("project_id", projectId.ToString()));
            if (Request.Headers.TryGetValue("X-Test-Bound-Run", out var runId))
                claims.Add(new Claim("run_id", runId.ToString()));
            if (Request.Headers.TryGetValue("X-Test-Tenant-Assertion", out var tenantId))
                claims.Add(new Claim("tenant_id", tenantId.ToString()));

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
