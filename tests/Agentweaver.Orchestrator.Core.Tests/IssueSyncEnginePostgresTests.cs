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
        // The fake records the remote issue before it loses the create response.
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
    public async Task ClosedFirstExportCreatesThenPatchesAndAtomicallyAttachesTheTask()
    {
        await AddTask("local", "Already closed", "Closed body");
        var before = (await Graph()).Tasks.Single();
        await Edit(before.Reference.TaskId, before.Title, before.Description ?? "", BacklogTaskLifecycle.Closed);

        await Cycle();

        var task = Assert.Single((await Graph()).Tasks);
        Assert.NotNull(task.RepositoryIssue);
        Assert.Equal(BacklogTaskLifecycle.Closed, task.Lifecycle);
        Assert.True(_github.Issues[1].Closed);
        var patch = JsonDocument.Parse(Assert.Single(_github.Patches));
        Assert.Equal(["state"], patch.RootElement.EnumerateObject().Select(field => field.Name).ToArray());
        var receipt = (await _store.ReadReceiptAsync(_project, task.Reference.TaskId, default))!;
        Assert.False(receipt.CreatePending);
        Assert.True(receipt.Baseline!.Closed);
    }

    [Fact]
    public async Task EditDuringFirstCreateIsPreservedAndPatchedWithTheInitialAttachment()
    {
        await AddTask("local", "Original", "Original body");
        var task = (await Graph()).Tasks.Single();
        _github.BeforeCreate = async () =>
            await Edit(task.Reference.TaskId, "New title", "New body", BacklogTaskLifecycle.Closed);

        await Cycle();

        var linked = Assert.Single((await Graph()).Tasks);
        Assert.Equal("New title", linked.Title);
        Assert.Equal("New body", linked.Description);
        Assert.Equal(BacklogTaskLifecycle.Closed, linked.Lifecycle);
        Assert.NotNull(linked.RepositoryIssue);
        Assert.Equal("New title", _github.Issues[1].Title);
        Assert.Equal("New body\n\n" + IssueSyncEngine.Marker(_project, task.Reference.TaskId),
            _github.Issues[1].Body);
        Assert.True(_github.Issues[1].Closed);
        var receipt = (await _store.ReadReceiptAsync(_project, task.Reference.TaskId, default))!;
        Assert.False(receipt.CreatePending);
        Assert.Equal(new(1, "New title", "New body", true), receipt.Baseline);
    }

    [Fact]
    public async Task ExplicitUnlinkAfterInitialAttachmentBlocksConfirmedPatchCommit()
    {
        await AddTask("local", "Local", "Body");
        var task = Assert.Single((await Graph()).Tasks);
        await Edit(task.Reference.TaskId, task.Title, task.Description ?? "", BacklogTaskLifecycle.Closed);
        _github.BeforePatch = async (_, _) => await SetLink(task.Reference.TaskId, null);

        await Cycle();

        Assert.True(_github.Issues[1].Closed);
        Assert.Null(Assert.Single((await Graph()).Tasks).RepositoryIssue);
        var receipt = (await _store.ReadReceiptAsync(_project, task.Reference.TaskId, default))!;
        Assert.False(receipt.CreatePending);
        Assert.False(receipt.Baseline!.Closed);
        Assert.Equal("issue_sync_mapping_changed", (await _store.ReadAsync(_project, default))!.ErrorCode);

        await Cycle();
        Assert.Null(Assert.Single((await Graph()).Tasks).RepositoryIssue);
        Assert.Single(_github.Patches);
        Assert.Equal("succeeded", (await _store.ReadAsync(_project, default))!.Status);
    }

    [Fact]
    public async Task OpenIssueUnlinkDuringInitialTitlePatchDoesNotImportDuplicate()
    {
        await AddTask("local", "Original", "Body");
        var task = Assert.Single((await Graph()).Tasks);
        _github.BeforeCreate = async () =>
            await Edit(task.Reference.TaskId, "New title", task.Description ?? "", BacklogTaskLifecycle.Open);
        _github.BeforePatch = async (_, _) =>
        {
            await SetLink(task.Reference.TaskId, null);
            _github.BeforePatch = null;
        };

        await Cycle();

        Assert.Equal("New title", _github.Issues[1].Title);
        Assert.False(_github.Issues[1].Closed);
        Assert.Single(_github.Patches);
        Assert.Null(Assert.Single((await Graph()).Tasks).RepositoryIssue);
        Assert.Equal("issue_sync_mapping_changed", (await _store.ReadAsync(_project, default))!.ErrorCode);

        await Cycle();

        Assert.Single((await Graph()).Tasks);
        Assert.Null(Assert.Single((await Graph()).Tasks).RepositoryIssue);
        Assert.Single(_github.Patches);
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

    [Fact]
    public async Task StateOnlyPatchPreservesRemoteTitleChangedAfterRead()
    {
        var task = await Import("Original");
        await Edit(task.Reference.TaskId, task.Title, task.Description ?? "", BacklogTaskLifecycle.Closed);
        _github.BeforePatch = (_, _) =>
        {
            _github.Issues[1] = _github.Issues[1] with
            {
                Title = "Remote title race winner",
                Body = "Remote body race winner"
            };
            return Task.CompletedTask;
        };

        await Cycle();

        Assert.Equal("Remote title race winner", _github.Issues[1].Title);
        Assert.Equal("Remote body race winner", _github.Issues[1].Body);
        Assert.True(_github.Issues[1].Closed);
        var merged = (await Graph()).Tasks.Single();
        Assert.Equal("Remote title race winner", merged.Title);
        Assert.Equal("Remote body race winner", merged.Description);
        using var patch = JsonDocument.Parse(Assert.Single(_github.Patches));
        Assert.Equal(["state"], patch.RootElement.EnumerateObject().Select(item => item.Name).ToArray());
    }

    [Fact]
    public async Task ReopenDuringConfirmedPatchRemainsPendingLocalIntent()
    {
        var task = await Import("Original");
        await Edit(task.Reference.TaskId, task.Title, task.Description ?? "", BacklogTaskLifecycle.Closed);
        _github.BeforePatch = async (_, _) => await Edit(task.Reference.TaskId, task.Title,
            task.Description ?? "", BacklogTaskLifecycle.Open);

        await Cycle();

        var local = (await Graph()).Tasks.Single();
        Assert.True(_github.Issues[1].Closed);
        Assert.Equal(BacklogTaskLifecycle.Open, local.Lifecycle);
        Assert.True((await _store.ReadReceiptAsync(_project, task.Reference.TaskId, default))!.Baseline!.Closed);

        await Cycle();
        Assert.False(_github.Issues[1].Closed);
    }

    [Fact]
    public async Task LocalIntentThatMatchedReadSurvivesAConcurrentRemoteEdit()
    {
        var task = await Import("Original");
        const string intent = "Shared at read";
        _github.Issues[1] = _github.Issues[1] with { Title = intent };
        await Edit(task.Reference.TaskId, intent, "", BacklogTaskLifecycle.Closed);
        _github.BeforePatch = (_, _) =>
        {
            _github.Issues[1] = _github.Issues[1] with { Title = "Remote race" };
            return Task.CompletedTask;
        };

        await Cycle();

        Assert.Equal(intent, (await Graph()).Tasks.Single().Title);
        Assert.Equal("Remote race", _github.Issues[1].Title);
        Assert.True(_github.Issues[1].Closed);

        _github.BeforePatch = null;
        await Cycle();
        Assert.Equal(intent, _github.Issues[1].Title);
    }

    [Fact]
    public async Task RemappedTaskIsRejectedBeforePatch()
    {
        var task = await Import("Original");
        await Edit(task.Reference.TaskId, task.Title, task.Description ?? "", BacklogTaskLifecycle.Closed);
        _github.BeforeRepositoryRead = async (_, read) =>
        {
            if (read == 5)
                await SetLink(task.Reference.TaskId, 2);
        };

        await Cycle();

        Assert.Empty(_github.Patches);
        Assert.False(_github.Issues[1].Closed);
        Assert.Equal("issue_sync_mapping_changed", (await _store.ReadAsync(_project, default))!.ErrorCode);
    }

    [Fact]
    public async Task RevocationAfterPatchKeepsConfirmedRemoteProgressWithoutLocalOverwrite()
    {
        var task = await Import("Original");
        await Edit(task.Reference.TaskId, task.Title, task.Description ?? "", BacklogTaskLifecycle.Closed);
        _github.BeforePatch = (_, _) =>
        {
            _authority.Failure = "issue_sync_grant_revoked";
            return Task.CompletedTask;
        };

        await Cycle();

        Assert.True(_github.Issues[1].Closed);
        Assert.Equal(BacklogTaskLifecycle.Closed,
            (await Graph()).Tasks.Single(item => item.Reference == task.Reference).Lifecycle);
        Assert.False((await _store.ReadReceiptAsync(_project, task.Reference.TaskId, default))!.Baseline!.Closed);
        Assert.Equal("issue_sync_grant_revoked", (await _store.ReadAsync(_project, default))!.ErrorCode);
    }

    [Theory]
    [InlineData("remap")]
    [InlineData("unlink")]
    [InlineData("archive")]
    public async Task ChangedOrMissingMappingAndArchiveAfterPatchAreRejected(string change)
    {
        var task = await Import("Original");
        await Edit(task.Reference.TaskId, task.Title, task.Description ?? "", BacklogTaskLifecycle.Closed);
        _github.BeforePatch = async (_, _) =>
        {
            if (change == "remap")
                await SetLink(task.Reference.TaskId, 2);
            else if (change == "unlink")
                await SetLink(task.Reference.TaskId, null);
            else
            {
                var graph = await Graph();
                var current = graph.Tasks.Single();
                Assert.True((await _backlog.ArchiveTaskAsync(current.Reference, graph.Revision,
                    current.Revision, default)).IsSuccess);
            }
        };

        await Cycle();

        Assert.True(_github.Issues[1].Closed);
        var error = (await _store.ReadAsync(_project, default))!.ErrorCode;
        Assert.Contains(error, new[] { "issue_sync_mapping_changed", "issue_sync_task_archived" });
        Assert.False((await _store.ReadReceiptAsync(_project, task.Reference.TaskId, default))!.Baseline!.Closed);
    }

    [Fact]
    public async Task AutomationHoldKeepsRemoteImportAndConfirmedStateForReleasePass()
    {
        var task = await Import("Original");
        await Edit(task.Reference.TaskId, task.Title, task.Description ?? "", BacklogTaskLifecycle.Closed);
        _github.BeforePatch = async (_, _) =>
        {
            _github.Issues[1] = _github.Issues[1] with { Title = "Imported later" };
            await SetAutomationHold(task.Reference.TaskId, true);
        };

        await Cycle();

        Assert.True(_github.Issues[1].Closed);
        var held = (await Graph()).Tasks.Single();
        Assert.Equal("Original", held.Title);
        Assert.Equal(BacklogTaskLifecycle.Closed, held.Lifecycle);
        var receipt = (await _store.ReadReceiptAsync(_project, task.Reference.TaskId, default))!;
        Assert.Equal("Original", receipt.Baseline!.Title);
        Assert.True(receipt.Baseline.Closed);

        await SetAutomationHold(task.Reference.TaskId, false);
        _github.BeforePatch = null;
        await Cycle();

        Assert.Equal("Imported later", (await Graph()).Tasks.Single().Title);
        Assert.Equal("Imported later", _github.Issues[1].Title);
        Assert.Equal("succeeded", (await _store.ReadAsync(_project, default))!.Status);
    }

    [Fact]
    public async Task HoldAdvancesConfirmedCloseWhileRetainingReopenAndBlockingTitleImport()
    {
        var task = await Import("Original");
        await Edit(task.Reference.TaskId, task.Title, task.Description ?? "", BacklogTaskLifecycle.Closed);
        _github.BeforePatch = async (_, _) =>
        {
            _github.Issues[1] = _github.Issues[1] with { Title = "Remote later title" };
            await Edit(task.Reference.TaskId, task.Title, task.Description ?? "", BacklogTaskLifecycle.Open);
            await SetAutomationHold(task.Reference.TaskId, true);
        };

        await Cycle();

        var held = Assert.Single((await Graph()).Tasks);
        Assert.Equal("Original", held.Title);
        Assert.Equal(BacklogTaskLifecycle.Open, held.Lifecycle);
        var receipt = (await _store.ReadReceiptAsync(_project, task.Reference.TaskId, default))!;
        Assert.Equal("Original", receipt.Baseline!.Title);
        Assert.True(receipt.Baseline.Closed);

        await SetAutomationHold(task.Reference.TaskId, false);
        _github.BeforePatch = null;
        await Cycle();
        Assert.False(_github.Issues[1].Closed);
    }

    [Fact]
    public async Task FinalAuthorityCheckRollsBackChangedTaskAndReceipt()
    {
        var task = await Import("Original");
        await Edit(task.Reference.TaskId, task.Title, task.Description ?? "", BacklogTaskLifecycle.Closed);
        _github.BeforePatch = (_, _) =>
        {
            _github.Issues[1] = _github.Issues[1] with { Title = "Remote title" };
            return Task.CompletedTask;
        };
        _authority.ResetValidations();
        _authority.DenyAtValidation = 5;

        await Cycle();

        Assert.Equal(5, _authority.ValidationCount);
        Assert.Equal("issue_sync_grant_revoked", (await _store.ReadAsync(_project, default))!.ErrorCode);
        var unchanged = Assert.Single((await Graph()).Tasks);
        Assert.Equal("Original", unchanged.Title);
        Assert.Equal(BacklogTaskLifecycle.Closed, unchanged.Lifecycle);
        var receipt = (await _store.ReadReceiptAsync(_project, task.Reference.TaskId, default))!;
        Assert.Equal("Original", receipt.Baseline!.Title);
        Assert.False(receipt.Baseline.Closed);
        Assert.True(_github.Issues[1].Closed);
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
    private async Task<BacklogTaskSnapshot> Import(string title)
    {
        _github.Issues.Add(1, new(1, title, "", false));
        await Cycle();
        return (await Graph()).Tasks.Single();
    }
    private async Task Edit(string taskId, string title, string body, BacklogTaskLifecycle lifecycle)
    {
        var graph = await Graph();
        var task = graph.Tasks.Single(task => task.Reference.TaskId == taskId);
        Assert.True((await _backlog.SetTaskContentAsync(task.Reference,
            new(title, body, lifecycle, task.RepositoryIssue), graph.Revision, task.Revision, default)).IsSuccess);
    }
    private async Task SetLink(string taskId, long? issueNumber)
    {
        var graph = await Graph();
        var task = graph.Tasks.Single(item => item.Reference.TaskId == taskId);
        var link = issueNumber is null ? null : task.RepositoryIssue! with { IssueNumber = issueNumber.Value };
        Assert.True((await _backlog.SetTaskContentAsync(task.Reference,
            new(task.Title, task.Description, task.Lifecycle, link), graph.Revision, task.Revision, default)).IsSuccess);
    }
    private async Task SetAutomationHold(string taskId, bool held)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"UPDATE \"{_schema}\".backlog_tasks SET automation_invocation_pending=@held " +
            "WHERE project_id=@project AND task_id=@task", connection);
        command.Parameters.AddWithValue("held", held);
        command.Parameters.AddWithValue("project", _project);
        command.Parameters.AddWithValue("task", taskId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private sealed class TestAuthority(IssueSyncGrantReceipt grant) : IIssueSyncAuthority
    {
        internal IssueSyncGrantReceipt Grant { get; } = grant;
        internal string? Failure { get; set; }
        internal int Mints { get; private set; }
        internal int DenyAfterValidations { get; set; } = int.MaxValue;
        internal int? DenyAtValidation { get; set; }
        internal int ValidationCount { get; private set; }
        internal void ResetValidations() => ValidationCount = 0;
        public Task<IssueSyncCredentialResponse> MintAsync(Guid id, CancellationToken ct)
        {
            if (Failure is not null) throw new RuntimeAuthorizationException(Failure);
            Mints++;
            return Task.FromResult(new IssueSyncCredentialResponse(Grant, "cycle-token-" + Mints,
                DateTimeOffset.UtcNow.AddMinutes(5), true));
        }
        public Task<IssueSyncGrantReceipt> ValidateAsync(Guid id, CancellationToken ct)
        {
            if (Failure is not null) throw new RuntimeAuthorizationException(Failure);
            ValidationCount++;
            if (ValidationCount > DenyAfterValidations || ValidationCount == DenyAtValidation)
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
        internal List<string> Patches { get; } = [];
        internal Func<long, HttpRequestMessage, Task>? BeforePatch { get; set; }
        internal Func<Task>? BeforeCreate { get; set; }
        internal Func<HttpRequestMessage, int, Task>? BeforeRepositoryRead { get; set; }
        internal int RepositoryReads { get; private set; }
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
            {
                RepositoryReads++;
                if (BeforeRepositoryRead is not null)
                    await BeforeRepositoryRead(request, RepositoryReads);
                return Json("""
                    {"id":123,"full_name":"octo/widget","default_branch":"main","private":true,
                     "created_at":"2020-01-01T00:00:00Z","has_issues":true,
                     "permissions":{"pull":false,"push":false,"admin":false}}
                    """);
            }
            if (request.Method == HttpMethod.Post)
            {
                if (RejectCreateOnce)
                {
                    RejectCreateOnce = false;
                    return new(HttpStatusCode.TooManyRequests);
                }
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                if (BeforeCreate is not null)
                    await BeforeCreate();
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
                var patch = body.RootElement;
                Patches.Add(patch.GetRawText());
                if (BeforePatch is not null)
                    await BeforePatch(id, request);
                var current = Issues[id];
                Issues[id] = new(id,
                    patch.TryGetProperty("title", out var title) ? title.GetString()! : current.Title,
                    patch.TryGetProperty("body", out var description) ? description.GetString()! : current.Body,
                    patch.TryGetProperty("state", out var state) ? state.GetString() == "closed" : current.Closed);
            }
            return Json(JsonSerializer.Serialize(Wire(Issues[id])));
        }
        private static object Wire(SourceControlIssueContent issue) =>
            new { number = issue.Number, title = issue.Title, body = issue.Body, state = issue.Closed ? "closed" : "open" };
        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
