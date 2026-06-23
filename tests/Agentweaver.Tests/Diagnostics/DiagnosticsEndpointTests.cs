using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Agentweaver.Api.Diagnostics;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Diagnostics;

/// <summary>
/// Integration tests for the diagnostics and heartbeat endpoints (FR-013, FR-016, FR-017).
/// Uses a real in-process host, real SQLite database, and real probes — no mocks.
///
/// Covered:
///   FR-013  — GET /health is unauthenticated and returns {status:"ok"}
///   FR-013  — GET /api/health requires an API key and returns {status:"ok"}
///   FR-016  — GET /api/diagnostics returns all system checks with pass/warn/fail status
///   FR-016  — check names, status values, duration_ms, and top-level fields are present
///   FR-016  — GET /api/projects/{id}/diagnostics returns project-scoped checks (workspace, etc.)
///   FR-017  — GET /api/diagnostics/heartbeat returns enabled/interval/automations/recent_activity
///   FR-017  — recent_activity ring buffer starts empty and captures outcomes after simulated ticks
/// </summary>
public sealed class DiagnosticsEndpointTests : IClassFixture<ProjectsWebApplicationFactory>
{
    private readonly ProjectsWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public DiagnosticsEndpointTests(ProjectsWebApplicationFactory factory)
    {
        _factory = factory;
        _client  = factory.CreateAuthenticatedClient();
    }

    // -------------------------------------------------------------------------
    // GET /health  (unauthenticated)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Health_NoAuth_Returns200WithStatusOk()
    {
        using var anon = _factory.CreateClient();
        var response = await anon.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("ok");
    }

    // -------------------------------------------------------------------------
    // GET /api/health  (requires API key)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ApiHealth_WithApiKey_Returns200WithStatusOk()
    {
        var response = await _client.GetAsync("/api/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("ok");
    }

    [Fact]
    public async Task ApiHealth_WithoutApiKey_Returns401()
    {
        using var anon = _factory.CreateClient();
        var response = await anon.GetAsync("/api/health");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -------------------------------------------------------------------------
    // GET /api/diagnostics
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Diagnostics_Returns200WithRequiredTopLevelFields()
    {
        var response = await _client.GetAsync("/api/diagnostics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.TryGetProperty("api_version", out _).Should().BeTrue("api_version must be present");
        body.TryGetProperty("process_started_utc", out _).Should().BeTrue();
        body.TryGetProperty("uptime_seconds", out var uptime).Should().BeTrue();
        uptime.GetDouble().Should().BeGreaterThan(0);
        body.TryGetProperty("total_projects", out _).Should().BeTrue();
        body.TryGetProperty("total_runs", out _).Should().BeTrue();
        body.TryGetProperty("active_runs", out _).Should().BeTrue();
        body.TryGetProperty("generated_utc", out _).Should().BeTrue();
        body.TryGetProperty("total_duration_ms", out var dur).Should().BeTrue();
        dur.GetDouble().Should().BeGreaterThan(0);
        body.TryGetProperty("checks", out var checks).Should().BeTrue();
        checks.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task Diagnostics_ChecksArrayContainsExpectedCheckNames()
    {
        var response = await _client.GetAsync("/api/diagnostics");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var checks = body.GetProperty("checks").EnumerateArray().ToList();
        var names = checks.Select(c => c.GetProperty("name").GetString()).ToHashSet();

        names.Should().Contain("sqlite_reachable");
        names.Should().Contain("disk_writable");
        names.Should().Contain("built_in_workflow");
        names.Should().Contain("built_in_review_policy");
        names.Should().Contain("heartbeat_service");
        names.Should().Contain("project_store");
        names.Should().Contain("github_cli");
    }

    [Fact]
    public async Task Diagnostics_EachCheckHasValidFields()
    {
        var response = await _client.GetAsync("/api/diagnostics");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        foreach (var check in body.GetProperty("checks").EnumerateArray())
        {
            var name = check.GetProperty("name").GetString();
            var status = check.GetProperty("status").GetString();
            status.Should().BeOneOf(["pass", "warn", "fail"],
                because: $"check '{name}' must have a valid status");

            check.TryGetProperty("detail", out var detail).Should().BeTrue($"check '{name}' needs detail");
            detail.GetString().Should().NotBeNullOrEmpty();

            check.TryGetProperty("duration_ms", out var dMs).Should().BeTrue($"check '{name}' needs duration_ms");
            dMs.GetDouble().Should().BeGreaterThanOrEqualTo(0);
        }
    }

    [Fact]
    public async Task Diagnostics_SqliteReachableCheck_Passes()
    {
        var response = await _client.GetAsync("/api/diagnostics");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var sqliteCheck = body.GetProperty("checks")
            .EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "sqlite_reachable");

        sqliteCheck.GetProperty("status").GetString().Should().Be("pass");
    }

    [Fact]
    public async Task Diagnostics_BuiltInWorkflowCheck_Passes()
    {
        var response = await _client.GetAsync("/api/diagnostics");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var check = body.GetProperty("checks")
            .EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "built_in_workflow");

        check.GetProperty("status").GetString().Should().Be("pass");
        check.GetProperty("detail").GetString().Should().Contain("nodes");
    }

    [Fact]
    public async Task Diagnostics_BuiltInReviewPolicyCheck_Passes()
    {
        var response = await _client.GetAsync("/api/diagnostics");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var check = body.GetProperty("checks")
            .EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "built_in_review_policy");

        check.GetProperty("status").GetString().Should().Be("pass");
        check.GetProperty("detail").GetString().Should().Contain("steps");
    }

    [Fact]
    public async Task Diagnostics_DiskWritableCheck_Passes()
    {
        var response = await _client.GetAsync("/api/diagnostics");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var check = body.GetProperty("checks")
            .EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "disk_writable");

        check.GetProperty("status").GetString().Should().Be("pass");
    }

    // -------------------------------------------------------------------------
    // GET /api/diagnostics/heartbeat
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Heartbeat_Returns200WithRequiredFields()
    {
        var response = await _client.GetAsync("/api/diagnostics/heartbeat");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.TryGetProperty("enabled", out _).Should().BeTrue();
        body.TryGetProperty("interval_seconds", out var interval).Should().BeTrue();
        interval.GetDouble().Should().BeGreaterThan(0);
        body.TryGetProperty("service_status", out var ss).Should().BeTrue();
        ss.GetString().Should().BeOneOf("running", "waiting_first_tick", "disabled");
        body.TryGetProperty("last_error", out _).Should().BeTrue();
        body.TryGetProperty("recent_activity", out var activity).Should().BeTrue();
        activity.ValueKind.Should().Be(JsonValueKind.Array);
        body.TryGetProperty("automations", out var automations).Should().BeTrue();
        automations.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task Heartbeat_AutomationsArrayContainsCoordinatorHeartbeatAndCheckpointGc()
    {
        var response = await _client.GetAsync("/api/diagnostics/heartbeat");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var automations = body.GetProperty("automations").EnumerateArray().ToList();
        automations.Should().HaveCount(2);

        var names = automations.Select(a => a.GetProperty("name").GetString()).ToList();
        names.Should().Contain("Coordinator Heartbeat");
        names.Should().Contain("Checkpoint GC");
    }

    [Fact]
    public async Task Heartbeat_EachAutomationHasRequiredFields()
    {
        var response = await _client.GetAsync("/api/diagnostics/heartbeat");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        foreach (var auto in body.GetProperty("automations").EnumerateArray())
        {
            var name = auto.GetProperty("name").GetString();
            auto.TryGetProperty("description", out var desc).Should().BeTrue($"automation '{name}' needs description");
            desc.GetString().Should().NotBeNullOrEmpty();
            auto.TryGetProperty("cadence_seconds", out var cadence).Should().BeTrue();
            cadence.GetDouble().Should().BeGreaterThan(0);
            auto.TryGetProperty("status", out var st).Should().BeTrue();
            st.GetString().Should().BeOneOf("running", "waiting_first_tick", "disabled");
        }
    }

    // -------------------------------------------------------------------------
    // GET /api/projects/{id}/diagnostics
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ProjectDiagnostics_ForExistingProject_ReturnsChecks()
    {
        // Create a project via the API so we have a real project to test against.
        var workDir = _factory.NewWorkingDirectory();
        var create = await _client.PostAsJsonAsync("/api/projects", new
        {
            name              = "diag-test-project",
            origin            = "blank",
            working_directory = workDir,
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = created.GetProperty("project_id").GetString()!;

        var response = await _client.GetAsync($"/api/projects/{projectId}/diagnostics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("project_id").GetString().Should().Be(projectId);
        body.TryGetProperty("project_name", out _).Should().BeTrue();
        body.TryGetProperty("generated_utc", out _).Should().BeTrue();
        body.TryGetProperty("total_duration_ms", out _).Should().BeTrue();

        var checks = body.GetProperty("checks").EnumerateArray().ToList();
        checks.Should().NotBeEmpty();

        var checkNames = checks.Select(c => c.GetProperty("name").GetString()).ToHashSet();
        checkNames.Should().Contain("workspace_available");
        checkNames.Should().Contain("workflows_directory");
        checkNames.Should().Contain("review_policies_directory");
        checkNames.Should().Contain("active_workflow");
        checkNames.Should().Contain("active_review_policy");
    }

    [Fact]
    public async Task ProjectDiagnostics_WorkspaceAvailableCheck_PassesForRealDirectory()
    {
        var workDir = _factory.NewWorkingDirectory();
        var create = await _client.PostAsJsonAsync("/api/projects", new
        {
            name              = "diag-ws-test",
            origin            = "blank",
            working_directory = workDir,
        });
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = created.GetProperty("project_id").GetString()!;

        var response = await _client.GetAsync($"/api/projects/{projectId}/diagnostics");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var wsCheck = body.GetProperty("checks")
            .EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "workspace_available");

        wsCheck.GetProperty("status").GetString().Should().Be("pass");
    }

    [Fact]
    public async Task ProjectDiagnostics_ActiveWorkflowCheck_PassesWhenNoScaffoldersDir()
    {
        // A project with no .scaffolders/workflows/ should still pass via built-in fallback.
        var workDir = _factory.NewWorkingDirectory();
        var create = await _client.PostAsJsonAsync("/api/projects", new
        {
            name              = "diag-workflow-test",
            origin            = "blank",
            working_directory = workDir,
        });
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = created.GetProperty("project_id").GetString()!;

        var response = await _client.GetAsync($"/api/projects/{projectId}/diagnostics");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var wfCheck = body.GetProperty("checks")
            .EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "active_workflow");

        wfCheck.GetProperty("status").GetString().Should().Be("pass");
    }

    [Fact]
    public async Task ProjectDiagnostics_UnknownProjectId_Returns404()
    {
        var response = await _client.GetAsync($"/api/projects/{Guid.NewGuid()}/diagnostics");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ProjectDiagnostics_InvalidProjectId_Returns400()
    {
        var response = await _client.GetAsync("/api/projects/not-a-valid-id/diagnostics");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

/// <summary>
/// Unit tests for <see cref="HeartbeatStatusStore"/> ring buffer behaviour — real in-memory
/// operations, no network, no file system.
/// </summary>
public sealed class HeartbeatStatusStoreRingBufferTests
{
    private static HeartbeatStatusStore BuildStore(bool enabled = true, int intervalSeconds = 10)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coordinator:HeartbeatEnabled"]         = enabled ? "true" : "false",
                ["Coordinator:HeartbeatIntervalSeconds"] = intervalSeconds.ToString(),
            })
            .Build();
        return new HeartbeatStatusStore(config);
    }

    [Fact]
    public void InitialState_LastTickUtcIsNull_RecentActivityIsEmpty_LastErrorIsNull()
    {
        var store = BuildStore();

        store.LastTickUtc.Should().BeNull();
        store.GetRecentActivity().Should().BeEmpty();
        store.LastError.Should().BeNull();
    }

    [Fact]
    public void RecordTickOutcome_SingleRecord_LastTickUtcMatchesTimestamp()
    {
        var store = BuildStore();
        var t = DateTimeOffset.UtcNow;

        store.RecordTickOutcome(t, actedCount: 2, errorCount: 0, durationMs: 5.5, error: null);

        store.LastTickUtc.Should().Be(t);
        store.LastError.Should().BeNull();
    }

    [Fact]
    public void RecordTickOutcome_MultipleRecords_ReturnsNewestFirst()
    {
        var store = BuildStore();
        var base_ = DateTimeOffset.UtcNow;

        for (int i = 0; i < 5; i++)
            store.RecordTickOutcome(base_.AddSeconds(i), i, 0, 1.0, null);

        var activity = store.GetRecentActivity();
        activity.Should().HaveCount(5);
        // Newest first
        activity[0].TimestampUtc.Should().Be(base_.AddSeconds(4));
        activity[4].TimestampUtc.Should().Be(base_.AddSeconds(0));
    }

    [Fact]
    public void RecordTickOutcome_ExceededCapacity_OldestDropped()
    {
        var store = BuildStore();
        var base_ = DateTimeOffset.UtcNow;

        for (int i = 0; i < 55; i++)   // exceeds RingCapacity = 50
            store.RecordTickOutcome(base_.AddSeconds(i), i, 0, 1.0, null);

        var activity = store.GetRecentActivity();
        activity.Should().HaveCount(50);
        // Newest is tick 54
        activity[0].TimestampUtc.Should().Be(base_.AddSeconds(54));
        activity[0].ActedCount.Should().Be(54);
        // Oldest retained is tick 5 (ticks 0–4 were overwritten)
        activity[49].TimestampUtc.Should().Be(base_.AddSeconds(5));
    }

    [Fact]
    public void RecordTickOutcome_WithError_LastErrorUpdated()
    {
        var store = BuildStore();
        store.RecordTickOutcome(DateTimeOffset.UtcNow, 0, 1, 3.0, "something went wrong");

        store.LastError.Should().Be("something went wrong");
    }

    [Fact]
    public void RecordTickOutcome_SubsequentSuccessDoesNotClearLastError()
    {
        // LastError is sticky — it is not cleared by later error-free ticks.
        var store = BuildStore();
        store.RecordTickOutcome(DateTimeOffset.UtcNow, 0, 1, 1.0, "initial error");
        store.RecordTickOutcome(DateTimeOffset.UtcNow.AddSeconds(10), 1, 0, 1.0, null);

        store.LastError.Should().Be("initial error");
    }

    [Fact]
    public void EnabledProperty_ReflectsConfiguration()
    {
        BuildStore(enabled: true).Enabled.Should().BeTrue();
        BuildStore(enabled: false).Enabled.Should().BeFalse();
    }

    [Fact]
    public void IntervalProperty_ReflectsConfigurationWithMinimumFloor()
    {
        BuildStore(intervalSeconds: 30).Interval.Should().Be(TimeSpan.FromSeconds(30));
        // floor = 1 second
        BuildStore(intervalSeconds: 0).Interval.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void GetRecentActivity_IsSnapshot_NotLiveReference()
    {
        var store = BuildStore();
        store.RecordTickOutcome(DateTimeOffset.UtcNow, 1, 0, 1.0, null);
        var snapshot = store.GetRecentActivity();

        // Add a new record after taking the snapshot.
        store.RecordTickOutcome(DateTimeOffset.UtcNow.AddSeconds(1), 2, 0, 1.0, null);

        snapshot.Should().HaveCount(1, "the snapshot must not be mutated by subsequent writes");
    }
}
