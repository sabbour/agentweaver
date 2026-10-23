using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Npgsql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

/// <summary>
/// Verifies the stored project issue and pull request lists, their endpoints, and reviewed migration 032.
/// </summary>
[Collection("Coordination PostgreSQL")]
public sealed class ProjectSourceControlListPostgresTests(CoordinationPostgresFixture fixture) : IAsyncLifetime
{
    private readonly string _schema = "sclist_" + Guid.NewGuid().ToString("N");
    private readonly string _project = "project-" + Guid.NewGuid().ToString("N");
    private readonly CoordinationActor _actor = new("https://identity.example/", Guid.NewGuid().ToString("D"));
    private OrchestratorOptions _options = null!;
    private BacklogOwnerStore _backlog = null!;
    private ProjectIssueReadStore _issues = null!;
    private ProjectPullRequestReadStore _pullRequests = null!;

    public async Task InitializeAsync()
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        _options = new(_actor.Issuer, "orchestrator", "https://projects.test/", "projects",
            "https://events.test/", "events", _schema);
        _backlog = new(fixture.DataSource, _schema);
        _issues = new(fixture.DataSource, _schema);
        _pullRequests = new(fixture.DataSource, _schema);
    }

    public async Task DisposeAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ReviewedMigrationAppliesPullRequestRecordsAndStartupVerificationPasses()
    {
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        Assert.True(await TableExistsAsync());
        Assert.Equal(1, await LedgerCountAsync());
        var page = await _pullRequests.ListAsync("tenant-1", _project, null, "all", null, 25, default);
        Assert.Empty(page.Items);
        Assert.False(page.Coverage.IncludesEarlierPullRequests);
        Assert.Equal(await AppliedAtAsync(), page.Coverage.RecordedSince);
    }

    [Fact]
    public async Task ReviewedMigrationIsIdempotentAndPreservesCoverageStart()
    {
        var recordedSince = await AppliedAtAsync();
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        Assert.True(await TableExistsAsync());
        Assert.Equal(1, await LedgerCountAsync());
        Assert.Equal(recordedSince, await AppliedAtAsync());

        var page = await _pullRequests.ListAsync("tenant-1", _project, null, "all", null, 25, default);
        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
        Assert.False(page.Coverage.IncludesEarlierPullRequests);
        Assert.Equal(recordedSince, page.Coverage.RecordedSince);
    }

    [Fact]
    public async Task VerificationRejectsPartialPullRequestRecordState()
    {
        await ExecuteAsync($"DELETE FROM \"{_schema}\".coordination_schema_migrations WHERE version = 32");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema));

        await ExecuteAsync($"INSERT INTO \"{_schema}\".coordination_schema_migrations (version) VALUES (32)");
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        await ExecuteAsync($"DROP TABLE \"{_schema}\".source_control_pull_requests");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema));
    }

    [Fact]
    public async Task IssueListPagesSyncedIssuesWithStateFilterTenantIsolationAndBoundCursor()
    {
        await ConfigureIssueSyncAsync(_project, "tenant-1");
        for (var number = 1; number <= 4; number++)
            await AddLinkedTaskAsync(_project, "task-" + number, "Issue " + number, number,
                number == 2 ? BacklogTaskLifecycle.Closed : BacklogTaskLifecycle.Open);
        await AddLinkedTaskAsync(_project, "task-archived", "Archived", 5, BacklogTaskLifecycle.Open);
        await ArchiveAsync(_project, "task-archived");
        var graph = (await _backlog.ReadGraphAsync(_project, default)).Value!;
        Assert.True((await _backlog.AddTaskAsync(
            new(_project, "task-unlinked"), new BacklogTaskContent("Unlinked"), graph.Revision, default)).IsSuccess);

        var first = await _issues.ListAsync("tenant-1", _project, "open", null, 2, default);
        Assert.Equal([4L, 3L], first.Items.Select(item => item.Number));
        Assert.All(first.Items, item =>
        {
            Assert.Equal("open", item.State);
            Assert.Equal("github", item.ProviderId);
            Assert.Equal(new ProjectSourceControlRepository("octo", "widget"), item.Repository);
        });
        Assert.Equal("Issue 4", first.Items[0].Title);
        Assert.NotNull(first.NextCursor);
        var second = await _issues.ListAsync("tenant-1", _project, "open", first.NextCursor, 2, default);
        Assert.Equal([1L], second.Items.Select(item => item.Number));
        Assert.Null(second.NextCursor);

        Assert.Equal([2L], (await _issues.ListAsync("tenant-1", _project, "closed", null, 25, default))
            .Items.Select(item => item.Number));
        Assert.Equal([4L, 3L, 2L, 1L], (await _issues.ListAsync("tenant-1", _project, "all", null, 25, default))
            .Items.Select(item => item.Number));
        Assert.Empty((await _issues.ListAsync("tenant-2", _project, "all", null, 25, default)).Items);

        await AssertCodeAsync("cursor_invalid",
            () => _issues.ListAsync("tenant-1", _project, "open", first.NextCursor, 3, default));
        await AssertCodeAsync("cursor_invalid",
            () => _issues.ListAsync("tenant-1", _project, "all", first.NextCursor, 2, default));
        await AssertCodeAsync("cursor_invalid",
            () => _issues.ListAsync("tenant-1", "other-project", "open", first.NextCursor, 2, default));
        await AssertCodeAsync("page_size_invalid",
            () => _issues.ListAsync("tenant-1", _project, "open", null, 0, default));
        await AssertCodeAsync("page_size_invalid",
            () => _issues.ListAsync("tenant-1", _project, "open", null, 101, default));
    }

    [Fact]
    public async Task IssueListIgnoresLinkedTasksInProjectsWithoutIssueSync()
    {
        await AddLinkedTaskAsync(_project, "task-1", "Linked without sync", 1, BacklogTaskLifecycle.Open);
        Assert.Empty((await _issues.ListAsync("tenant-1", _project, "all", null, 25, default)).Items);
    }

    [Fact]
    public async Task PullRequestEndpointReturnsUnavailableBeforeMigrationAndCoverageAfterIt()
    {
        // Model a prior release's complete 031 schema without touching any shared database.
        await ExecuteAsync($"DROP TABLE \"{_schema}\".source_control_pull_requests");
        await ExecuteAsync($"DELETE FROM \"{_schema}\".coordination_schema_migrations WHERE version = 32");
        var authorization = ReadAuthorization();
        var before = await InvokePullRequestsAsync("", [authorization, authorization]);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, StatusCode(before));
        Assert.Equal("pull_request_records_unavailable", ErrorCode(before));

        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        var after = Assert.IsType<Ok<ProjectPullRequestPage>>(
            await InvokePullRequestsAsync("?state=all&pageSize=5", [authorization, authorization]));
        Assert.Empty(after.Value!.Items);
        Assert.Equal(await AppliedAtAsync(), after.Value.Coverage.RecordedSince);
        Assert.False(after.Value.Coverage.IncludesEarlierPullRequests);
    }

    [Fact]
    public async Task ListsReturnForbiddenWithoutItemsWhenPermissionIsLostDuringTheRead()
    {
        await ConfigureIssueSyncAsync(_project, "tenant-1");
        await AddLinkedTaskAsync(_project, "task-1", "Visible", 1, BacklogTaskLifecycle.Open);
        var granted = ReadAuthorization();
        var revoked = ReadAuthorization(permission: "readRunSelection");

        Assert.IsType<Ok<ProjectIssuePage>>(await InvokeIssuesAsync("", [granted, granted]));
        var issues = await InvokeIssuesAsync("", [granted, revoked]);
        Assert.Equal(StatusCodes.Status403Forbidden, StatusCode(issues));
        Assert.Equal("backlog_project_permission_denied", ErrorCode(issues));

        var pullRequests = await InvokePullRequestsAsync("", [granted, ReadAuthorization(permission: "writeProjects")]);
        Assert.Equal(StatusCodes.Status403Forbidden, StatusCode(pullRequests));
        Assert.Equal("project_read_permission_denied", ErrorCode(pullRequests));

        var denied = await InvokeIssuesAsync("", [revoked]);
        Assert.Equal(StatusCodes.Status403Forbidden, StatusCode(denied));
    }

    [Theory]
    [InlineData("?pageSize=0", "page_size_invalid")]
    [InlineData("?pageSize=101", "page_size_invalid")]
    [InlineData("?page=1", "query_invalid")]
    [InlineData("?state=open&state=closed", "query_invalid")]
    [InlineData("?state=merged", "issue_state_filter_invalid")]
    public async Task IssueEndpointRejectsInvalidQueriesBeforeCallingProjects(string query, string code)
    {
        var handler = new AuthorizationHandler([ReadAuthorization()]);
        var result = await InvokeIssuesAsync(query, handler);
        Assert.Equal(StatusCodes.Status400BadRequest, StatusCode(result));
        Assert.Equal(code, ErrorCode(result));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("?pageSize=abc", "page_size_invalid")]
    [InlineData("?cursor=a&cursor=b", "query_invalid")]
    [InlineData("?state=draft", "pull_request_state_filter_invalid")]
    public async Task PullRequestEndpointRejectsInvalidQueriesBeforeCallingProjects(string query, string code)
    {
        var handler = new AuthorizationHandler([ReadAuthorization()]);
        var result = await InvokePullRequestsAsync(query, handler);
        Assert.Equal(StatusCodes.Status400BadRequest, StatusCode(result));
        Assert.Equal(code, ErrorCode(result));
        Assert.Equal(0, handler.Calls);
    }

    private ProjectsAuthorizationContext ReadAuthorization(string permission = "readProjects") =>
        new(1, _actor.Issuer, _actor.Subject, "tenant-1", 1, null, null,
            [new ProjectsAuthority("project", _project, [new ProjectsPermissionGrant(permission, 1)])]);

    private Task<IResult> InvokeIssuesAsync(string query, ProjectsAuthorizationContext[] responses) =>
        InvokeIssuesAsync(query, new AuthorizationHandler(responses));

    private async Task<IResult> InvokeIssuesAsync(string query, AuthorizationHandler handler)
    {
        using var http = new HttpClient(handler);
        return await ProjectSourceControlEndpoints.ListIssuesAsync(
            _project, Context(query), Client(http), _issues, default);
    }

    private Task<IResult> InvokePullRequestsAsync(string query, ProjectsAuthorizationContext[] responses) =>
        InvokePullRequestsAsync(query, new AuthorizationHandler(responses));

    private async Task<IResult> InvokePullRequestsAsync(string query, AuthorizationHandler handler)
    {
        using var http = new HttpClient(handler);
        var context = Context(query);
        var result = await ProjectSourceControlEndpoints.ListPullRequestsAsync(
            _project, context, Client(http), _pullRequests, default);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        return result;
    }

    private ProjectsRunSelectionClient Client(HttpClient http) =>
        new(http, _options, new ReviewedRemoteToolSnapshotStore(fixture.DataSource, _schema));

    private HttpContext Context(string query)
    {
        var claims = new List<Claim>
        {
            new("sub", _actor.Subject), new("aud", _options.Audience), new("aud", _options.ProjectsAudience),
            new("scope", "api.read")
        };
        var context = new DefaultHttpContext { User = new(new ClaimsIdentity(claims, "controlled-owner")) };
        context.Request.Headers.Authorization = "Bearer test-token";
        context.Request.Headers["X-Agentweaver-Tenant"] = "tenant-1";
        context.Request.QueryString = new QueryString(query);
        return context;
    }

    private static int? StatusCode(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode;

    private static string? ErrorCode(IResult result)
    {
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        Assert.False(document.RootElement.TryGetProperty("items", out _));
        return document.RootElement.GetProperty("error").GetString();
    }

    private async Task ConfigureIssueSyncAsync(string project, string tenant)
    {
        var settings = new IssueSyncSettings(true, Guid.NewGuid(), 30, 10, 30);
        var receipt = new IssueSyncGrantReceipt(settings.GrantId,
            new(project, tenant, Guid.NewGuid().ToString("D"), "connection", new("octo", "widget")), true);
        await new IssueSyncStore(fixture.DataSource, _schema).ConfigureAsync(
            project, tenant, settings, receipt, 0, _ => Task.CompletedTask, default);
    }

    private async Task AddLinkedTaskAsync(
        string project, string taskId, string title, long number, BacklogTaskLifecycle lifecycle)
    {
        var graph = (await _backlog.ReadGraphAsync(project, default)).Value!;
        var reference = new BacklogTaskReference(project, taskId);
        Assert.True((await _backlog.AddTaskAsync(
            reference, new BacklogTaskContent(title), graph.Revision, default)).IsSuccess);
        graph = (await _backlog.ReadGraphAsync(project, default)).Value!;
        var task = graph.Tasks.Single(item => item.Reference == reference);
        Assert.True((await _backlog.SetTaskContentAsync(reference,
            new BacklogTaskContent(title, "body", lifecycle,
                new BacklogRepositoryIssueLink("github", new("octo", "widget"), number)),
            graph.Revision, task.Revision, default)).IsSuccess);
    }

    private async Task ArchiveAsync(string project, string taskId)
    {
        var graph = (await _backlog.ReadGraphAsync(project, default)).Value!;
        var task = graph.Tasks.Single(item => item.Reference.TaskId == taskId);
        Assert.True((await _backlog.ArchiveTaskAsync(task.Reference, graph.Revision, task.Revision, default))
            .IsSuccess);
    }

    private async Task<bool> TableExistsAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT to_regclass('\"{_schema}\".source_control_pull_requests') IS NOT NULL", connection);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> LedgerCountAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM \"{_schema}\".coordination_schema_migrations WHERE version = 32", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<DateTimeOffset> AppliedAtAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT applied_at FROM \"{_schema}\".coordination_schema_migrations WHERE version = 32", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertCodeAsync(string code, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<CoordinationException>(action);
        Assert.Equal(code, exception.Code);
    }

    /// <summary>Returns Projects authorization contexts in order, one per call, with the required no-store header.</summary>
    private sealed class AuthorizationHandler(ProjectsAuthorizationContext[] responses) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("/api/authorization/context", request.RequestUri!.AbsolutePath);
            var authorization = responses[Math.Min(Calls, responses.Length - 1)];
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = JsonContent.Create(authorization),
                Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
            });
        }
    }
}
