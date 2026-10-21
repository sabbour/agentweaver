using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Agentweaver.Orchestrator;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

/// <summary>
/// Verifies the project run list against migrated owner tables and the fresh Projects read-authority check.
/// </summary>
[Collection("Coordination PostgreSQL")]
public sealed class ProjectRunListPostgresTests(CoordinationPostgresFixture fixture) : IAsyncLifetime
{
    private const string Project = "run-list-project";
    private readonly string _schema = "runlist_" + Guid.NewGuid().ToString("N");
    private readonly CoordinationActor _actor = new("https://identity.example/", Guid.NewGuid().ToString("D"));
    private OrchestratorOptions _options = null!;
    private ProjectRunReadStore _store = null!;

    public async Task InitializeAsync()
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        _options = new(_actor.Issuer, "orchestrator", "https://projects.test/", "projects",
            "https://events.test/", "events", _schema);
        _store = new ProjectRunReadStore(fixture.DataSource, _schema);
    }

    public async Task DisposeAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ListPagesNewestFirstWithStableCursor()
    {
        var runs = new List<string>();
        for (var index = 0; index < 5; index++)
            runs.Add(await AcceptRunAsync("tenant-1", DateTimeOffset.Parse("2026-10-01T00:00:00Z").AddHours(index)));

        var first = await _store.ListAsync("tenant-1", Project, null, [], null, 2, default);
        Assert.Equal([runs[4], runs[3]], first.Items.Select(item => item.RunId));
        Assert.NotNull(first.NextCursor);
        var root = Assert.IsType<string>(first.Items[0].RootSessionId);
        Assert.False(string.IsNullOrEmpty(root));
        Assert.Equal("idle", first.Items[0].State);
        Assert.Null(first.Items[0].Title);
        Assert.Null(first.Items[0].Goal);
        Assert.Null(first.Items[0].CompletedAt);
        Assert.Equal(new ProjectRunStartedBy(_actor.Issuer, _actor.Subject), first.Items[0].StartedBy);

        // A run accepted after the first page does not shift later pages.
        await AcceptRunAsync("tenant-1", DateTimeOffset.Parse("2026-10-02T00:00:00Z"));
        var second = await _store.ListAsync("tenant-1", Project, null, [], first.NextCursor, 2, default);
        Assert.Equal([runs[2], runs[1]], second.Items.Select(item => item.RunId));
        var third = await _store.ListAsync("tenant-1", Project, null, [], second.NextCursor, 2, default);
        Assert.Equal([runs[0]], third.Items.Select(item => item.RunId));
        Assert.Null(third.NextCursor);
    }

    [Fact]
    public async Task ListDerivesStateFromPersistedRowsAndFilters()
    {
        var time = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        var idle = await AcceptRunAsync("tenant-1", time);
        var active = await AcceptRunAsync("tenant-1", time.AddMinutes(1));
        var blocked = await AcceptRunAsync("tenant-1", time.AddMinutes(2));
        var completed = await AcceptRunAsync("tenant-1", time.AddMinutes(3));
        var failed = await AcceptRunAsync("tenant-1", time.AddMinutes(4));
        await ExecuteAsync($"UPDATE \"{_schema}\".coordination_sessions SET turn_state = 'active' WHERE run_id = @run AND parent_session_id IS NULL", active);
        await ExecuteAsync($"UPDATE \"{_schema}\".coordination_sessions SET turn_state = 'blocked' WHERE run_id = @run AND parent_session_id IS NULL", blocked);
        await ExecuteAsync($"UPDATE \"{_schema}\".coordination_sessions SET lifecycle_state = 'completed' WHERE run_id = @run AND parent_session_id IS NULL", completed);
        await ExecuteAsync($"UPDATE \"{_schema}\".accepted_runs SET completed_at = created_at + interval '1 hour' WHERE run_id = @run", completed);
        await ExecuteAsync($"UPDATE \"{_schema}\".accepted_runs SET execution_state = 'failed', execution_cause_code = 'test_failure', execution_reference = 'test-reference', completed_at = created_at + interval '1 hour' WHERE run_id = @run", failed);

        var all = await _store.ListAsync("tenant-1", Project, null, [], null, 25, default);
        var states = all.Items.ToDictionary(item => item.RunId, item => item.State);
        Assert.Equal("idle", states[idle]);
        Assert.Equal("active", states[active]);
        Assert.Equal("blocked", states[blocked]);
        Assert.Equal("completed", states[completed]);
        Assert.Equal("failed", states[failed]);
        Assert.NotNull(all.Items.Single(item => item.RunId == completed).CompletedAt);

        var open = await _store.ListAsync(
            "tenant-1", Project, null, ProjectRunReadStore.ParseStateFilter("open"), null, 25, default);
        Assert.Equal([blocked, active, idle], open.Items.Select(item => item.RunId));
        var terminal = await _store.ListAsync(
            "tenant-1", Project, null, ProjectRunReadStore.ParseStateFilter("terminal"), null, 25, default);
        Assert.Equal([failed, completed], terminal.Items.Select(item => item.RunId));
    }

    [Fact]
    public async Task ListIsolatesTenantsAndNarrowsRunBoundCallers()
    {
        var time = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        var mine = await AcceptRunAsync("tenant-1", time);
        var other = await AcceptRunAsync("tenant-1", time.AddMinutes(1));
        await AcceptRunAsync("tenant-2", time.AddMinutes(2));

        var tenant = await _store.ListAsync("tenant-1", Project, null, [], null, 25, default);
        Assert.Equal([other, mine], tenant.Items.Select(item => item.RunId));
        var bound = await _store.ListAsync("tenant-1", Project, mine, [], null, 25, default);
        Assert.Equal([mine], bound.Items.Select(item => item.RunId));
    }

    [Fact]
    public async Task ListRejectsCursorFromAnotherQueryAndInvalidInput()
    {
        var time = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        await AcceptRunAsync("tenant-1", time);
        await AcceptRunAsync("tenant-1", time.AddMinutes(1));
        var page = await _store.ListAsync("tenant-1", Project, null, [], null, 1, default);
        Assert.NotNull(page.NextCursor);

        await AssertCodeAsync("cursor_invalid",
            () => _store.ListAsync("tenant-1", Project, null, [], page.NextCursor, 2, default));
        await AssertCodeAsync("cursor_invalid", () => _store.ListAsync(
            "tenant-1", Project, null, ProjectRunReadStore.ParseStateFilter("open"), page.NextCursor, 1, default));
        await AssertCodeAsync("cursor_invalid",
            () => _store.ListAsync("tenant-1", "other-project", null, [], page.NextCursor, 1, default));
        await AssertCodeAsync("cursor_invalid",
            () => _store.ListAsync("tenant-1", Project, null, [], page.NextCursor + "x", 1, default));
        await AssertCodeAsync("page_size_invalid",
            () => _store.ListAsync("tenant-1", Project, null, [], null, 101, default));
    }

    [Fact]
    public void StateFilterAcceptsAliasesAndRejectsUnknownStates()
    {
        Assert.Equal(["active", "blocked", "idle"], ProjectRunReadStore.ParseStateFilter("open").ToArray());
        Assert.Equal(["cancelled", "completed"], ProjectRunReadStore.ParseStateFilter("completed,cancelled,completed").ToArray());
        Assert.Empty(ProjectRunReadStore.ParseStateFilter("open,terminal"));
        Assert.Empty(ProjectRunReadStore.ParseStateFilter(null));
        Assert.Equal("run_state_filter_invalid",
            Assert.Throws<CoordinationException>(() => ProjectRunReadStore.ParseStateFilter("done")).Code);
        Assert.Equal("run_state_filter_invalid",
            Assert.Throws<CoordinationException>(() => ProjectRunReadStore.ParseStateFilter("idle,")).Code);
    }

    [Theory]
    [InlineData("project", "readProjects", null, null, true)]
    [InlineData("project", "readRunSelection", null, null, true)]
    [InlineData("tenant", "readProjects", null, null, true)]
    [InlineData("project", "readProjects", Project, null, true)]
    [InlineData("project", "readProjects", Project, "run-1", true)]
    [InlineData("project", "readProjects", "other-project", null, false)]
    [InlineData("project", "writeProjects", null, null, false)]
    [InlineData("tenant", "readRunSelection", null, null, false)]
    public async Task ReadAuthorityRequiresFreshProjectReadPermission(
        string resourceType, string permission, string? boundProject, string? boundRun, bool allowed)
    {
        var resourceId = resourceType == "project" ? Project : "tenant-1";
        var authorization = new ProjectsAuthorizationContext(
            1, _actor.Issuer, _actor.Subject, "tenant-1", 1, boundProject, boundRun,
            [new ProjectsAuthority(resourceType, resourceId, [new ProjectsPermissionGrant(permission, 1)])]);
        using var http = new HttpClient(new AuthorizationHandler(authorization));
        var client = new ProjectsRunSelectionClient(
            http, _options, new ReviewedRemoteToolSnapshotStore(fixture.DataSource, _schema));
        var context = Context(boundProject, boundRun);

        if (allowed)
        {
            var result = await client.ReadProjectReadAuthorityAsync(context, Project, default);
            Assert.Equal("tenant-1", result.TenantId);
            Assert.Equal(boundRun, result.BoundRunId);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<CoordinationException>(
                () => client.ReadProjectReadAuthorityAsync(context, Project, default));
            Assert.Equal(StatusCodes.Status403Forbidden, exception.StatusCode);
        }
    }

    [Fact]
    public async Task ReadAuthorityRejectsMissingReadScopeBeforeCallingProjects()
    {
        var authorization = new ProjectsAuthorizationContext(
            1, _actor.Issuer, _actor.Subject, "tenant-1", 1, null, null,
            [new ProjectsAuthority("project", Project, [new ProjectsPermissionGrant("readProjects", 1)])]);
        var handler = new AuthorizationHandler(authorization);
        using var http = new HttpClient(handler);
        var client = new ProjectsRunSelectionClient(
            http, _options, new ReviewedRemoteToolSnapshotStore(fixture.DataSource, _schema));

        var exception = await Assert.ThrowsAsync<CoordinationException>(
            () => client.ReadProjectReadAuthorityAsync(Context(null, null, "projects.orchestrator"), Project, default));
        Assert.Equal("caller_scope_invalid", exception.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("?pageSize=0", "page_size_invalid")]
    [InlineData("?pageSize=abc", "page_size_invalid")]
    [InlineData("?page=1", "query_invalid")]
    [InlineData("?state=idle&state=active", "query_invalid")]
    [InlineData("?state=done", "run_state_filter_invalid")]
    public void QueryValidationRejectsUnknownRepeatedAndOutOfRangeValues(string query, string code)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        var exception = Assert.Throws<CoordinationException>(() =>
        {
            var values = ProjectRunEndpoints.ReadSingleValuedQuery(
                context.Request.Query, new(["state", "cursor", "pageSize"], StringComparer.Ordinal));
            _ = ProjectRunReadStore.ParseStateFilter(values.GetValueOrDefault("state"));
            _ = ProjectRunEndpoints.ParsePageSize(values.GetValueOrDefault("pageSize"));
        });
        Assert.Equal(code, exception.Code);
        Assert.Equal(StatusCodes.Status400BadRequest, exception.StatusCode);
    }

    private HttpContext Context(string? boundProject, string? boundRun, string scope = "api.read")
    {
        var claims = new List<Claim>
        {
            new("sub", _actor.Subject), new("aud", _options.Audience), new("aud", _options.ProjectsAudience),
            new("scope", scope)
        };
        if (boundProject is not null)
            claims.Add(new("project_id", boundProject));
        if (boundRun is not null)
            claims.Add(new("run_id", boundRun));
        var context = new DefaultHttpContext { User = new(new ClaimsIdentity(claims, "controlled-owner")) };
        context.Request.Headers.Authorization = "Bearer controlled-owner";
        context.Request.Headers["X-Agentweaver-Tenant"] = "tenant-1";
        return context;
    }

    private async Task<string> AcceptRunAsync(string tenant, DateTimeOffset createdAt)
    {
        var run = Guid.NewGuid().ToString("N");
        var snapshot = JsonSerializer.SerializeToElement(new
        {
            projectId = Project, runId = run, projectRevision = 1,
            projectConfigurationRevision = 1, platformRuntimeRevision = 1, contextRevision = "context-1"
        });
        var selection = new AuthorizedRunSelection(
            new(Project, run, 1, 1, 1, "context-1", snapshot),
            new(1, _actor.Issuer, _actor.Subject, tenant, 1, Project, run,
                [new("project", Project, [new("acceptRunSelection", 1)])]));
        await new CoordinationOwnerStore(fixture.DataSource, _schema).AcceptRootAsync(_actor, selection, "root", default);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"UPDATE \"{_schema}\".accepted_runs SET created_at = @created, updated_at = @created WHERE run_id = @run",
            connection);
        command.Parameters.AddWithValue("created", createdAt);
        command.Parameters.AddWithValue("run", run);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        return run;
    }

    private async Task ExecuteAsync(string sql, string run)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("run", run);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task AssertCodeAsync(string code, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<CoordinationException>(action);
        Assert.Equal(code, exception.Code);
    }

    /// <summary>Returns a fixed Projects authorization context with the required no-store header.</summary>
    private sealed class AuthorizationHandler(ProjectsAuthorizationContext authorization) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Assert.Equal("/api/authorization/context", request.RequestUri!.AbsolutePath);
            Assert.Equal("tenant-1", Assert.Single(request.Headers.GetValues("X-Agentweaver-Tenant")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = JsonContent.Create(authorization),
                Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
            });
        }
    }
}
