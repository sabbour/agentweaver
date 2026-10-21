using System.Net;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Agentweaver.SourceControl;
using Npgsql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

[Collection("Coordination PostgreSQL")]
public sealed class IssueSyncEnginePostgresTests(CoordinationPostgresFixture fixture) : IAsyncLifetime
{
    private readonly string _schema = "sync_" + Guid.NewGuid().ToString("N");
    private readonly string _project = "project-" + Guid.NewGuid().ToString("N");
    private readonly GitHubIssueHandler _github = new();
    private IssueSyncStore _store = null!;
    private BacklogOwnerStore _backlog = null!;
    private IssueSyncEngine _engine = null!;
    private TestAuthority _authority = null!;
    private IssueSyncSettings _settings = null!;

    public async Task InitializeAsync()
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        _store = new(fixture.DataSource, _schema);
        _backlog = new(fixture.DataSource, _schema);
        _settings = new(true, Guid.NewGuid(), 30, 10, 30);
        _authority = new(new(_settings.GrantId,
            new(_project, "tenant", Guid.NewGuid().ToString("D"), "connection", new("octo", "widget")), true));
        await _store.ConfigureAsync(_project, "tenant", _settings, _authority.Grant, 0, _ => Task.CompletedTask, default);
        var descriptor = GitHubSourceControlAdapter.CreateDescriptor();
        var catalog = ProviderCatalog.Create(
            [new ProviderRegistration(descriptor, true, "test-revision", 1)],
            [new ProviderSelection(ProviderSeam.SourceControl, "github")], []).Value!;
        var adapter = new GitHubSourceControlAdapter(new HttpClient(_github) { BaseAddress = new("https://api.github.com/") });
        _engine = new(_store, _backlog, _authority, adapter, adapter, new(catalog), TimeProvider.System);
    }

    public async Task DisposeAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ImportExportRepeatAndBidirectionalContentCloseReopenUseRealProviderAndDurableStores()
    {
        _github.Issues.Add(1, new(1, "Imported", "Provider body", false));
        _github.Issues.Add(2, new(2, "Not imported", "Closed work", true));
        await AddTask("local", "Local title", "Local body");
        await Cycle();
        var graph = await Graph();
        Assert.Equal(2, graph.Tasks.Length);
        Assert.Single(graph.Tasks, task => task.Title == "Imported");
        Assert.Equal(1, _github.Creates);
        Assert.All(graph.Tasks, task => Assert.NotNull(task.RepositoryIssue));
        await Cycle();
        Assert.Equal(1, _github.Creates);
        Assert.Equal(2, (await Graph()).Tasks.Length);
        var imported = graph.Tasks.Single(task => task.Title == "Imported");
        _github.Issues[1] = new(1, "Provider title edit", "New provider body", true);
        await Cycle();
        var closed = (await Graph()).Tasks.Single(task => task.Reference == imported.Reference);
        Assert.Equal("Provider title edit", closed.Title);
        Assert.Equal(BacklogTaskLifecycle.Closed, closed.Lifecycle);
        await Edit(closed.Reference.TaskId, "Owner edit", "Owner body", BacklogTaskLifecycle.Open);
        await Cycle();
        Assert.Equal(new(1, "Owner edit", "Owner body", false), _github.Issues[1]);
        _github.Issues[1] = _github.Issues[1] with { Closed = true };
        await Cycle();
        _github.Issues[1] = _github.Issues[1] with { Closed = false };
        await Cycle();
        Assert.Equal(BacklogTaskLifecycle.Open, (await Graph()).Tasks.Single(task => task.Reference == imported.Reference).Lifecycle);
        Assert.Equal("succeeded", (await _store.ReadAsync(_project, default))!.Status);
        Assert.True(_authority.Mints >= 6);
        Assert.All(_github.Paths, path => Assert.StartsWith("/repos/octo/widget", path));
    }

    [Fact]
    public async Task LostCreateResponseRecoversMarkerWithoutDuplicatePost()
    {
        await AddTask("local", "Local");
        _github.LoseCreateResponse = true;
        await Cycle();
        Assert.Equal(1, _github.Creates);
        Assert.Null(Assert.Single((await Graph()).Tasks).RepositoryIssue);
        Assert.Equal("failed", (await _store.ReadAsync(_project, default))!.Status);
        await Cycle();
        Assert.Equal(1, _github.Creates);
        Assert.NotNull(Assert.Single((await Graph()).Tasks).RepositoryIssue);
        Assert.False((await _store.ReadReceiptAsync(_project, "local", default))!.CreatePending);
        Assert.Equal("succeeded", (await _store.ReadAsync(_project, default))!.Status);
    }

    [Fact]
    public async Task TransientReadAndDefinitiveCreateRejectionRetryOnNextCycle()
    {
        await AddTask("local", "Local");
        _github.FailReadOnce = true;
        await Cycle();
        Assert.Equal("failed", (await _store.ReadAsync(_project, default))!.Status);
        Assert.Equal(0, _github.Creates);
        _github.RejectCreateOnce = true;
        await Cycle();
        Assert.False((await _store.ReadReceiptAsync(_project, "local", default))!.CreatePending);
        await Cycle();
        Assert.Equal(1, _github.Creates);
        Assert.Equal("succeeded", (await _store.ReadAsync(_project, default))!.Status);
    }

    [Theory]
    [InlineData("issue_sync_grant_revoked")]
    [InlineData("issue_sync_creator_not_owner")]
    [InlineData("issue_sync_repository_not_selected")]
    public async Task GrantFailureStopsExternalAccessAndPersistsExactReason(string reason)
    {
        _authority.Failure = reason;
        await Cycle();
        Assert.Empty(_github.Paths);
        Assert.Equal(reason, (await _store.ReadAsync(_project, default))!.ErrorCode);
    }

    [Fact]
    public async Task RevocationInsideGraphWriteRollsBackImport()
    {
        _github.Issues.Add(1, new(1, "Import", "", false));
        _authority.DenyAfterValidations = 2;
        await Cycle();
        Assert.Empty((await Graph()).Tasks);
        Assert.Equal("issue_sync_grant_revoked", (await _store.ReadAsync(_project, default))!.ErrorCode);
    }

    [Fact]
    public async Task SettingsDisableBoundsAndConcurrentCycleLockAreDurable()
    {
        var held = await _store.TryLockAsync(_project, default);
        Assert.NotNull(held);
        try
        {
            await _engine.CycleAsync(_project, default);
            Assert.Empty(_github.Paths);
            await Assert.ThrowsAsync<CoordinationException>(() => _store.ConfigureAsync(_project, "tenant",
                _settings with { Enabled = false }, _authority.Grant, 1, _ => Task.CompletedTask, default));
        }
        finally { await IssueSyncStore.UnlockAsync(held!, _project); }
        await _store.ConfigureAsync(_project, "tenant", _settings with { Enabled = false },
            _authority.Grant, 1, _ => Task.CompletedTask, default);
        await Cycle();
        Assert.Empty(_github.Paths);
        Assert.Equal("disabled", (await _store.ReadAsync(_project, default))!.Status);
        await Assert.ThrowsAsync<CoordinationException>(() => _store.ConfigureAsync(_project, "other",
            _settings, _authority.Grant, 2, _ => Task.CompletedTask, default));
    }

    [Fact]
    public async Task BoundedPagesEventuallyImportAllOpenIssuesAndNeverImportPullRequests()
    {
        await _store.ConfigureAsync(_project, "tenant", _settings with { MaxItems = 1 }, _authority.Grant, 1,
            _ => Task.CompletedTask, default);
        for (var i = 1; i <= 3; i++) _github.Issues.Add(i, new(i, "Issue " + i, "", false));
        await Cycle();
        Assert.Single((await Graph()).Tasks);
        await Cycle();
        Assert.Equal(2, (await Graph()).Tasks.Length);
        await Cycle();
        Assert.Equal(3, (await Graph()).Tasks.Length);
    }

    private async Task Cycle()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var due = new NpgsqlCommand(
            $"UPDATE \"{_schema}\".issue_sync_projects SET next_cycle_at=clock_timestamp()-interval '1 second'", connection);
        await due.ExecuteNonQueryAsync();
        await _engine.CycleAsync(_project, default);
    }
    private async Task<BacklogDependencyGraph> Graph() => (await _backlog.ReadGraphAsync(_project, default)).Value!;
    private async Task AddTask(string task, string title, string body = "")
    {
        var graph = await Graph();
        Assert.True((await _backlog.AddTaskAsync(new(_project, task), new BacklogTaskContent(title, body),
            graph.Revision, default)).IsSuccess);
    }
    private async Task Edit(string taskId, string title, string body, BacklogTaskLifecycle lifecycle)
    {
        var graph = await Graph();
        var task = graph.Tasks.Single(task => task.Reference.TaskId == taskId);
        Assert.True((await _backlog.SetTaskContentAsync(task.Reference,
            new(title, body, lifecycle, task.RepositoryIssue), graph.Revision, task.Revision, default)).IsSuccess);
    }

    private sealed class TestAuthority(IssueSyncGrantReceipt grant) : IIssueSyncAuthority
    {
        internal IssueSyncGrantReceipt Grant { get; } = grant;
        internal string? Failure { get; set; }
        internal int Mints { get; private set; }
        internal int DenyAfterValidations { get; set; } = int.MaxValue;
        private int _validations;
        public Task<IssueSyncCredentialResponse> MintAsync(Guid id, CancellationToken ct)
        {
            if (Failure is not null) throw new RuntimeAuthorizationException(Failure);
            Mints++;
            return Task.FromResult(new IssueSyncCredentialResponse(Grant, "cycle-token-" + Mints, DateTimeOffset.UtcNow.AddMinutes(5)));
        }
        public Task<IssueSyncGrantReceipt> ValidateAsync(Guid id, CancellationToken ct)
        {
            if (++_validations > DenyAfterValidations)
                throw new RuntimeAuthorizationException("issue_sync_grant_revoked");
            return Task.FromResult(Grant);
        }
    }

    private sealed class GitHubIssueHandler : HttpMessageHandler
    {
        internal Dictionary<long, SourceControlIssueContent> Issues { get; } = [];
        internal List<string> Paths { get; } = [];
        internal int Creates { get; private set; }
        internal bool LoseCreateResponse { get; set; }
        internal bool RejectCreateOnce { get; set; }
        internal bool FailReadOnce { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Paths.Add(uri.PathAndQuery);
            Assert.StartsWith("cycle-token-", request.Headers.Authorization!.Parameter!);
            if (FailReadOnce)
            {
                FailReadOnce = false;
                return new(HttpStatusCode.ServiceUnavailable);
            }
            if (uri.AbsolutePath == "/repos/octo/widget")
                return Json("""
                    {"id":123,"full_name":"octo/widget","default_branch":"main","private":true,
                     "created_at":"2020-01-01T00:00:00Z","has_issues":true,
                     "permissions":{"pull":true,"push":true,"admin":false}}
                    """);
            if (request.Method == HttpMethod.Post)
            {
                if (RejectCreateOnce)
                {
                    RejectCreateOnce = false;
                    return new(HttpStatusCode.TooManyRequests);
                }
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var number = Issues.Count == 0 ? 1 : Issues.Keys.Max() + 1;
                var issue = new SourceControlIssueContent(number, body.RootElement.GetProperty("title").GetString()!,
                    body.RootElement.GetProperty("body").GetString()!, false);
                Issues.Add(number, issue);
                Creates++;
                if (LoseCreateResponse)
                {
                    LoseCreateResponse = false;
                    throw new HttpRequestException("test lost response after remote write");
                }
                return Json(JsonSerializer.Serialize(new { number, title = issue.Title, body = issue.Body,
                    state = "open", html_url = $"https://github.com/octo/widget/issues/{number}" }));
            }
            if (uri.AbsolutePath == "/repos/octo/widget/issues")
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
                var page = int.Parse(query["page"].ToString());
                var size = int.Parse(query["per_page"].ToString());
                return Json(JsonSerializer.Serialize(Issues.Values.OrderBy(issue => issue.Number)
                    .Skip((page - 1) * size).Take(size).Select(Wire)));
            }
            var id = long.Parse(uri.Segments.Last());
            if (request.Method == HttpMethod.Patch)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Issues[id] = new(id, body.RootElement.GetProperty("title").GetString()!,
                    body.RootElement.GetProperty("body").GetString()!, body.RootElement.GetProperty("state").GetString() == "closed");
            }
            return Json(JsonSerializer.Serialize(Wire(Issues[id])));
        }
        private static object Wire(SourceControlIssueContent issue) =>
            new { number = issue.Number, title = issue.Title, body = issue.Body, state = issue.Closed ? "closed" : "open" };
        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
