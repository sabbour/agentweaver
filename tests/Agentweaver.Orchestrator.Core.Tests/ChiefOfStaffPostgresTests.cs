using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Agentweaver.Orchestrator;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

/// <summary>
/// Verifies Chief of Staff conversations and Priorities against migrated owner tables. Each test maps to a
/// proposal §8 scenario: reload and reconnect, duplicate input, conflicts, permission loss, cancelled work,
/// and Priority edits that never change execution state.
/// </summary>
[Collection("Coordination PostgreSQL")]
public sealed class ChiefOfStaffPostgresTests(CoordinationPostgresFixture fixture) : IAsyncLifetime
{
    private const string Tenant = "tenant-1";
    private const string Project = "cos-project";
    private const string OtherProject = "cos-other-project";
    private readonly string _schema = "cos_" + Guid.NewGuid().ToString("N");
    private readonly CoordinationActor _actor = new("https://identity.example/", Guid.NewGuid().ToString("D"));
    private OrchestratorOptions _options = null!;
    private CoordinationOwnerStore _owner = null!;
    private ChiefOfStaffStore _store = null!;

    public async Task InitializeAsync()
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        _options = new(_actor.Issuer, "orchestrator", "https://projects.test/", "projects",
            "https://events.test/", "events", _schema);
        var catalog = ProviderCatalog.Create([], [], []).Value!;
        var contexts = new CoordinatorRunSelectionContextStore(
            fixture.DataSource, _schema, catalog, new ProviderResolver(catalog), []);
        var decisions = new CoordinatorDecisionOwnerStore(fixture.DataSource, _schema, contexts, TimeProvider.System);
        _owner = new CoordinationOwnerStore(fixture.DataSource, _schema, TimeProvider.System, decisions);
        _store = new ChiefOfStaffStore(fixture.DataSource, _schema);
    }

    public async Task DisposeAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    // Scenario 1: the conversation survives reload and reconnect, and a duplicate bind returns the same entry.
    [Fact]
    public async Task ConversationSurvivesReloadAndDuplicateBindReturnsOneEntry()
    {
        var request = await CreateRunRequestAsync("key-1");
        var first = await _store.BindConversationAsync(_actor, Tenant, Project, request.RunId, default);
        var duplicate = await _store.BindConversationAsync(_actor, Tenant, Project, request.RunId, default);
        Assert.Equal(first, duplicate);
        Assert.Equal("available", first.Access);
        Assert.Equal(request.RootSessionId, first.RootSessionId);
        Assert.Equal("awaiting_binding", first.RequestState);
        Assert.Null(first.RunState);

        // A fresh store instance models a reload with a new connection.
        var reloaded = new ChiefOfStaffStore(fixture.DataSource, _schema);
        var page = await reloaded.ListConversationsAsync(_actor, Tenant, null, 25, default);
        Assert.Equal([first.ConversationId], page.Items.Select(item => item.ConversationId));
        Assert.Equal(first, await reloaded.GetConversationAsync(_actor, Tenant, first.ConversationId, default));

        await AcceptRunAsync(request.RunId, request.RootSessionId);
        var accepted = await reloaded.GetConversationAsync(_actor, Tenant, first.ConversationId, default);
        Assert.Equal("idle", accepted!.RunState);
        Assert.Equal(1L, await CountAsync("chief_of_staff_conversations"));
    }

    [Fact]
    public async Task ConversationsArePrivateToTheStarter()
    {
        var request = await CreateRunRequestAsync("key-1");
        var other = _actor with { Subject = Guid.NewGuid().ToString("D") };
        var error = await Assert.ThrowsAsync<CoordinationException>(
            () => _store.BindConversationAsync(other, Tenant, Project, request.RunId, default));
        Assert.Equal("conversation_run_not_found", error.Code);
        Assert.Equal(404, error.StatusCode);

        var mine = await _store.BindConversationAsync(_actor, Tenant, Project, request.RunId, default);
        Assert.Null(await _store.GetConversationAsync(other, Tenant, mine.ConversationId, default));
        Assert.Empty((await _store.ListConversationsAsync(other, Tenant, null, 25, default)).Items);
        Assert.Null(await _store.GetConversationAsync(_actor, "tenant-2", mine.ConversationId, default));
    }

    [Fact]
    public async Task ConversationPagesAreNewestFirstWithStableCursor()
    {
        var ids = new List<string>();
        for (var index = 0; index < 3; index++)
        {
            var request = await CreateRunRequestAsync($"key-{index}");
            ids.Add((await _store.BindConversationAsync(_actor, Tenant, Project, request.RunId, default)).ConversationId);
        }

        var first = await _store.ListConversationsAsync(_actor, Tenant, null, 2, default);
        Assert.Equal([ids[2], ids[1]], first.Items.Select(item => item.ConversationId));
        var second = await _store.ListConversationsAsync(_actor, Tenant, first.NextCursor, 2, default);
        Assert.Equal([ids[0]], second.Items.Select(item => item.ConversationId));
        Assert.Null(second.NextCursor);
        await AssertCodeAsync("cursor_invalid",
            () => _store.ListConversationsAsync(_actor, Tenant, first.NextCursor + "x", 2, default));
    }

    // Scenario 2: a duplicate create with the same key makes one Priority; a changed body conflicts.
    [Fact]
    public async Task DuplicatePriorityCreateMakesOneRecord()
    {
        var request = new CreateChiefOfStaffPriorityRequest("Ship the release", "Notes", [new(null, "Draft notes", false)]);
        var (first, created) = await _store.CreatePriorityAsync(_actor, Tenant, "key-1", request, default);
        var (replay, replayCreated) = await _store.CreatePriorityAsync(_actor, Tenant, "key-1", request, default);
        Assert.True(created);
        Assert.False(replayCreated);
        Assert.Equal(first.PriorityId, replay.PriorityId);
        Assert.Equal(1L, await CountAsync("chief_of_staff_priorities"));
        Assert.False(string.IsNullOrEmpty(Assert.Single(first.Checklist).ItemId));

        await AssertCodeAsync("idempotency_conflict", () => _store.CreatePriorityAsync(
            _actor, Tenant, "key-1", request with { Outcome = "Different" }, default));
    }

    // Scenario 3: stale revisions and stale order revisions fail with conflicts and keep current content.
    [Fact]
    public async Task StaleRevisionsConflictAndKeepCurrentContent()
    {
        var (priority, _) = await _store.CreatePriorityAsync(
            _actor, Tenant, "key-1", new("Ship the release", null, null), default);
        var updated = await _store.UpdatePriorityAsync(_actor, Tenant, priority.PriorityId,
            new(priority.Revision, null, "First edit", null, null), default);
        Assert.Equal(priority.Revision + 1, updated.Revision);
        Assert.Equal("First edit", updated.Notes);

        var conflict = await Assert.ThrowsAsync<CoordinationException>(() => _store.UpdatePriorityAsync(
            _actor, Tenant, priority.PriorityId, new(priority.Revision, null, "Stale edit", null, null), default));
        Assert.Equal("priority_revision_conflict", conflict.Code);
        Assert.Equal(409, conflict.StatusCode);
        Assert.Equal("First edit", (await _store.GetPriorityAsync(_actor, Tenant, priority.PriorityId, default))!.Notes);

        var cleared = await _store.UpdatePriorityAsync(_actor, Tenant, priority.PriorityId,
            new(updated.Revision, null, "", "achieved", null), default);
        Assert.Null(cleared.Notes);
        Assert.Equal("achieved", cleared.State);

        var (second, _) = await _store.CreatePriorityAsync(_actor, Tenant, "key-2", new("Hire", null, null), default);
        var list = await _store.ListPrioritiesAsync(_actor, Tenant, default);
        Assert.Equal([priority.PriorityId, second.PriorityId], list.Items.Select(item => item.PriorityId));
        var reordered = await _store.ReorderPrioritiesAsync(_actor, Tenant,
            new(list.OrderRevision, [second.PriorityId, priority.PriorityId]), default);
        Assert.Equal([second.PriorityId, priority.PriorityId], reordered.Items.Select(item => item.PriorityId));
        await AssertCodeAsync("priority_order_conflict", () => _store.ReorderPrioritiesAsync(_actor, Tenant,
            new(list.OrderRevision, [priority.PriorityId, second.PriorityId]), default));
        await AssertCodeAsync("priority_order_invalid", () => _store.ReorderPrioritiesAsync(_actor, Tenant,
            new(reordered.OrderRevision, [priority.PriorityId]), default));
    }

    [Fact]
    public async Task PrioritiesArePrivateToTheOwner()
    {
        var (priority, _) = await _store.CreatePriorityAsync(_actor, Tenant, "key-1", new("Mine", null, null), default);
        var other = _actor with { Subject = Guid.NewGuid().ToString("D") };
        Assert.Null(await _store.GetPriorityAsync(other, Tenant, priority.PriorityId, default));
        Assert.Empty((await _store.ListPrioritiesAsync(other, Tenant, default)).Items);
        await AssertCodeAsync("priority_not_found", () => _store.UpdatePriorityAsync(
            other, Tenant, priority.PriorityId, new(priority.Revision, "Taken", null, null, null), default));
    }

    [Theory]
    [InlineData("issue")]
    [InlineData("pull_request")]
    public async Task RepositoryIdentitySurvivesReceiptsReloadAndPermissionLoss(string kind)
    {
        var (priority, _) = await _store.CreatePriorityAsync(_actor, Tenant, "repository-links", new("Ship", null, null), default);
        // Number 42 in each repository is a separate target, not a duplicate.
        foreach (var repository in new[] { new ProjectSourceControlRepository("octo", "api"), new("other", "repo") })
            priority = await _store.AddLinkAsync(_actor, Tenant, priority.PriorityId,
                new(priority.Revision, kind, Project, null, null, 42, null, "github", repository), default);
        Assert.Equal(2, priority.Links.Length);
        Assert.All(priority.Links, link => Assert.Equal("github", link.ProviderId));
        var reloaded = await new ChiefOfStaffStore(fixture.DataSource, _schema)
            .GetPriorityAsync(_actor, Tenant, priority.PriorityId, default);
        // Compare link values, not the backing-array identity of independently materialized receipts.
        Assert.Equal(priority.Links.ToArray(), reloaded!.Links.ToArray());
        var listed = await _store.ListPrioritiesAsync(_actor, Tenant, default);
        Assert.Equal(priority.Links.ToArray(), Assert.Single(listed.Items).Links.ToArray());
        await AssertCodeAsync("priority_link_exists", () => _store.AddLinkAsync(_actor, Tenant, priority.PriorityId,
            new(priority.Revision, kind, Project, null, null, 42, null, "github", new("other", "repo")), default));

        // New writes must identify a repository; legacy rows stay readable without a guessed identity.
        await AssertCodeAsync("priority_link_invalid", () => _store.AddLinkAsync(_actor, Tenant, priority.PriorityId,
            new(priority.Revision, kind, Project, null, null, 43, null), default));
        await ExecuteAsync($"""
            INSERT INTO "{_schema}".chief_of_staff_priority_links
                (priority_id, link_id, kind, project_id, item_number)
            VALUES ('{priority.PriorityId}', 'legacy-link', '{kind}', '{Project}', 42)
            """);
        var legacyRead = await _store.GetPriorityAsync(_actor, Tenant, priority.PriorityId, default);
        var legacy = Assert.Single(legacyRead!.Links, link => link.LinkId == "legacy-link");
        Assert.Null(legacy.ProviderId);
        Assert.Null(legacy.Repository);
        // Legacy uniqueness stays independent of identified targets with the same number.
        var duplicateLegacy = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"""
            INSERT INTO "{_schema}".chief_of_staff_priority_links
                (priority_id, link_id, kind, project_id, item_number)
            VALUES ('{priority.PriorityId}', 'legacy-duplicate', '{kind}', '{Project}', 42)
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateLegacy.SqlState);
        var partialIdentity = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"""
            INSERT INTO "{_schema}".chief_of_staff_priority_links
                (priority_id, link_id, kind, project_id, item_number, provider_id)
            VALUES ('{priority.PriorityId}', 'partial-identity', '{kind}', '{Project}', 43, 'github')
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, partialIdentity.SqlState);

        // Permission trimming removes both new identity fields as well as the old target fields.
        var hidden = ChiefOfStaffEndpoints.Redact(legacyRead, Authority(OtherProject));
        Assert.All(hidden.Links, link => {
            Assert.Equal("unavailable", link.Access);
            Assert.Null(link.ProviderId);
            Assert.Null(link.Repository);
            Assert.Null(link.Number);
        });
    }

    // Scenario 4: Priority edits and links never change run, request, session, or gate rows.
    [Fact]
    public async Task PriorityEditsNeverChangeExecutionState()
    {
        var request = await CreateRunRequestAsync("key-1");
        await AcceptRunAsync(request.RunId, request.RootSessionId);
        var conversation = await _store.BindConversationAsync(_actor, Tenant, Project, request.RunId, default);
        var before = await SnapshotExecutionStateAsync();

        var (priority, _) = await _store.CreatePriorityAsync(_actor, Tenant, "key-1", new("Ship", null, null), default);
        priority = await _store.AddLinkAsync(_actor, Tenant, priority.PriorityId,
            new(priority.Revision, "run", Project, request.RunId, null, null, null), default);
        priority = await _store.AddLinkAsync(_actor, Tenant, priority.PriorityId,
            new(priority.Revision, "conversation", Project, null, conversation.ConversationId, null, null), default);
        priority = await _store.UpdatePriorityAsync(_actor, Tenant, priority.PriorityId,
            new(priority.Revision, "Ship it", "Notes", "dropped", [new(null, "Step", true)]), default);
        priority = await _store.RemoveLinkAsync(
            _actor, Tenant, priority.PriorityId, priority.Links[0].LinkId, priority.Revision, default);

        Assert.Equal(before, await SnapshotExecutionStateAsync());
        Assert.Single(priority.Links);
    }

    // Scenario 4: an outcome survives cancelled and completed runs, and each link shows its own run state.
    [Fact]
    public async Task PrioritySurvivesCancelledAndCompletedRunsWithDistinctStates()
    {
        var cancelled = await CreateRunRequestAsync("key-1");
        await AcceptRunAsync(cancelled.RunId, cancelled.RootSessionId);
        var completed = await CreateRunRequestAsync("key-2");
        await AcceptRunAsync(completed.RunId, completed.RootSessionId);
        var (priority, _) = await _store.CreatePriorityAsync(_actor, Tenant, "key-1", new("Ship", null, null), default);
        foreach (var run in new[] { cancelled.RunId, completed.RunId })
            priority = await _store.AddLinkAsync(_actor, Tenant, priority.PriorityId,
                new(priority.Revision, "run", Project, run, null, null, null), default);
        priority = await _store.AddLinkAsync(_actor, Tenant, priority.PriorityId,
            new(priority.Revision, "run", Project, "never-accepted", null, null, null), default);
        priority = await _store.AddLinkAsync(_actor, Tenant, priority.PriorityId,
            new(priority.Revision, "pull_request", Project, null, null, 42, null, "github", new("octo", "api")), default);

        await ExecuteAsync($"""
            UPDATE "{_schema}".accepted_runs
            SET execution_state = 'cancelled', cancelled_at = clock_timestamp(), completed_at = clock_timestamp()
            WHERE run_id = @run
            """, cancelled.RunId);
        await ExecuteAsync($"""
            UPDATE "{_schema}".coordination_sessions SET lifecycle_state = 'completed'
            WHERE run_id = @run AND parent_session_id IS NULL
            """, completed.RunId);

        var read = (await _store.GetPriorityAsync(_actor, Tenant, priority.PriorityId, default))!;
        Assert.Equal("active", read.State);
        var byRun = read.Links.Where(link => link.Kind == "run").ToDictionary(link => link.RunId!);
        Assert.Equal("cancelled", byRun[cancelled.RunId].State);
        Assert.Equal("completed", byRun[completed.RunId].State);
        Assert.Equal("missing", byRun["never-accepted"].Access);
        var pullRequest = Assert.Single(read.Links, link => link.Kind == "pull_request");
        Assert.Equal("unavailable", pullRequest.State);
        Assert.Equal(42, pullRequest.Number);
    }

    [Fact]
    public async Task LinksRejectDuplicatesStaleRevisionsAndForeignConversations()
    {
        var request = await CreateRunRequestAsync("key-1");
        var (priority, _) = await _store.CreatePriorityAsync(_actor, Tenant, "key-1", new("Ship", null, null), default);
        var link = new AddChiefOfStaffPriorityLinkRequest(priority.Revision, "issue", Project, null, null, 7, null,
            "github", new("octo", "api"));
        priority = await _store.AddLinkAsync(_actor, Tenant, priority.PriorityId, link, default);
        await AssertCodeAsync("priority_revision_conflict",
            () => _store.AddLinkAsync(_actor, Tenant, priority.PriorityId, link, default));
        await AssertCodeAsync("priority_link_exists", () => _store.AddLinkAsync(
            _actor, Tenant, priority.PriorityId, link with { ExpectedRevision = priority.Revision }, default));

        var other = _actor with { Subject = Guid.NewGuid().ToString("D") };
        var otherRequest = await _owner.CreateProjectRunAsync(other, Tenant, Project, "key-1",
            new CreateProjectRunRequest("Other", 1), default);
        var foreign = await _store.BindConversationAsync(other, Tenant, Project, otherRequest.RunId, default);
        var error = await Assert.ThrowsAsync<CoordinationException>(() => _store.AddLinkAsync(_actor, Tenant,
            priority.PriorityId,
            new(priority.Revision, "conversation", Project, null, foreign.ConversationId, null, null), default));
        Assert.Equal(404, error.StatusCode);
        _ = request;
    }

    // Scenario 3: permission loss removes protected content from conversations and links.
    [Fact]
    public async Task PermissionLossRemovesProtectedContent()
    {
        var request = await CreateRunRequestAsync("key-1");
        var conversation = await _store.BindConversationAsync(_actor, Tenant, Project, request.RunId, default);
        var (priority, _) = await _store.CreatePriorityAsync(_actor, Tenant, "key-1", new("Ship", "My notes", null), default);
        priority = await _store.AddLinkAsync(_actor, Tenant, priority.PriorityId,
            new(priority.Revision, "conversation", Project, null, conversation.ConversationId, null, null), default);
        priority = await _store.AddLinkAsync(_actor, Tenant, priority.PriorityId,
            new(priority.Revision, "issue", OtherProject, null, null, 9, null, "github", new("octo", "api")), default);

        var lost = Authority(OtherProject);
        var redacted = ChiefOfStaffEndpoints.Redact(conversation, lost);
        Assert.Equal(new ChiefOfStaffConversation(conversation.ConversationId, "unavailable",
            null, null, null, null, null, null, conversation.CreatedAt), redacted);

        var redactedPriority = ChiefOfStaffEndpoints.Redact(priority, lost);
        Assert.Equal("My notes", redactedPriority.Notes);
        var hidden = Assert.Single(redactedPriority.Links, link => link.Kind == "conversation");
        Assert.Equal(new ChiefOfStaffPriorityLink(hidden.LinkId, "conversation", "unavailable",
            null, null, null, null, null, null, null), hidden);
        var json = JsonSerializer.Serialize(hidden, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain(Project, json);
        Assert.Equal("available", Assert.Single(redactedPriority.Links, link => link.Kind == "issue").Access);

        var kept = ChiefOfStaffEndpoints.Redact(priority, Authority(Project, OtherProject));
        Assert.All(kept.Links, link => Assert.Equal("available", link.Access));
    }

    [Theory]
    [InlineData("project", "readProjects", null, true)]
    [InlineData("tenant", "readProjects", null, true)]
    [InlineData("project", "readRunSelection", null, true)]
    [InlineData("project", "writeProjects", null, false)]
    [InlineData("project", "readProjects", Project, false)]
    public async Task CallerAuthorityRequiresAnUnboundTokenAndFreshReadPermission(
        string resourceType, string permission, string? boundProject, bool canRead)
    {
        var authorization = new ProjectsAuthorizationContext(
            1, _actor.Issuer, _actor.Subject, Tenant, 1, boundProject, null,
            [new ProjectsAuthority(resourceType, resourceType == "project" ? Project : Tenant,
                [new ProjectsPermissionGrant(permission, 1)])]);
        var handler = new AuthorizationHandler(authorization);
        using var http = new HttpClient(handler);
        var client = new ProjectsRunSelectionClient(
            http, _options, new ReviewedRemoteToolSnapshotStore(fixture.DataSource, _schema));

        if (boundProject is not null)
        {
            var error = await Assert.ThrowsAsync<CoordinationException>(
                () => client.ReadCallerAuthorityAsync(Context(boundProject), default));
            Assert.Equal(403, error.StatusCode);
            Assert.Equal(0, handler.Calls);
            return;
        }

        var authority = await client.ReadCallerAuthorityAsync(Context(null), default);
        Assert.Equal(canRead, ProjectsRunSelectionClient.CanReadProject(authority, Project));
    }

    private ProjectsAuthorizationContext Authority(params string[] readable) =>
        new(1, _actor.Issuer, _actor.Subject, Tenant, 1, null, null,
            [.. readable.Select(project =>
                new ProjectsAuthority("project", project, [new ProjectsPermissionGrant("readProjects", 1)]))]);

    private Task<CoordinationOwnerStore.ProjectRunRequestRow> CreateRunRequestAsync(string key) =>
        _owner.CreateProjectRunAsync(_actor, Tenant, Project, key, new CreateProjectRunRequest("Plan " + key, 1), default);

    private async Task AcceptRunAsync(string run, string root)
    {
        var snapshot = JsonSerializer.SerializeToElement(new
        {
            projectId = Project, runId = run, projectRevision = 1,
            projectConfigurationRevision = 1, platformRuntimeRevision = 1, contextRevision = "context-1"
        });
        var selection = new AuthorizedRunSelection(
            new(Project, run, 1, 1, 1, "context-1", snapshot),
            new(1, _actor.Issuer, _actor.Subject, Tenant, 1, Project, run,
                [new("project", Project, [new("acceptRunSelection", 1)])]));
        await _owner.AcceptRootAsync(_actor, selection, root, default);
    }

    /// <summary>Serializes every execution-owned row so a test can prove that Priority writes leave them unchanged.</summary>
    private async Task<string> SnapshotExecutionStateAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT jsonb_build_object(
                'runs', (SELECT jsonb_agg(to_jsonb(r) ORDER BY run_id) FROM "{_schema}".accepted_runs r),
                'requests', (SELECT jsonb_agg(to_jsonb(q) ORDER BY run_id) FROM "{_schema}".project_run_requests q),
                'sessions', (SELECT jsonb_agg(to_jsonb(s) ORDER BY session_id) FROM "{_schema}".coordination_sessions s),
                'gates', (SELECT jsonb_agg(to_jsonb(g)) FROM "{_schema}".coordinator_gates g),
                'cancellations', (SELECT jsonb_agg(to_jsonb(c)) FROM "{_schema}".project_run_cancellations c))::text
            """, connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> CountAsync(string table)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM \"{_schema}\".{table}", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql, string? run = null)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        if (run is not null) command.Parameters.AddWithValue("run", run);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task AssertCodeAsync(string code, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<CoordinationException>(action);
        Assert.Equal(code, exception.Code);
    }

    private HttpContext Context(string? boundProject)
    {
        var claims = new List<Claim>
        {
            new("sub", _actor.Subject), new("aud", _options.Audience), new("aud", _options.ProjectsAudience),
            new("scope", "api.read")
        };
        if (boundProject is not null)
            claims.Add(new("project_id", boundProject));
        var context = new DefaultHttpContext { User = new(new ClaimsIdentity(claims, "controlled-owner")) };
        context.Request.Headers.Authorization = "Bearer token";
        context.Request.Headers["X-Agentweaver-Tenant"] = Tenant;
        return context;
    }

    /// <summary>Returns a fixed Projects authorization context with the required no-store header.</summary>
    private sealed class AuthorizationHandler(ProjectsAuthorizationContext authorization) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
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
