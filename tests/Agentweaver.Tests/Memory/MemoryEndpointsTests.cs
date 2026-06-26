using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Memory;

public sealed class MemoryEndpointsTests : IClassFixture<ProjectsWebApplicationFactory>
{
    private readonly ProjectsWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public MemoryEndpointsTests(ProjectsWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();

        _factory.TokenStore.SetAsync(
            GitHubTokenScope.Installation,
            new GitHubToken("access-token", null, null, "sabbour", null, Array.Empty<string>()))
            .GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Test_InboxSubmit_StoresPendingEntry()
    {
        var projectId = await CreateProjectAsync();

        var response = await SubmitInboxAsync(
            projectId,
            agentName: "smith",
            slug: "arch-record",
            title: "Decision title",
            content: "Decision content",
            type: "architectural");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Object);
        body.GetProperty("id").GetInt32().Should().BeGreaterThan(0);
        body.GetProperty("agentName").GetString().Should().Be("smith");
        body.GetProperty("slug").GetString().Should().Be("arch-record");
        body.GetProperty("type").GetString().Should().Be("architectural");
        body.GetProperty("title").GetString().Should().Be("Decision title");
        body.GetProperty("content").GetString().Should().Be("Decision content");
        body.GetProperty("status").GetString().Should().Be("pending");

        var entry = await GetInboxEntryBySlugAsync(projectId, "arch-record");
        entry.Should().NotBeNull();
        entry!.Status.Should().Be("pending");
        entry.AgentName.Should().Be("smith");
        entry.Slug.Should().Be("arch-record");
        entry.Type.Should().Be("architectural");
        entry.Title.Should().Be("Decision title");
        entry.Content.Should().Be("Decision content");
    }

    [Fact]
    public async Task Test_InboxSubmit_IsIdempotentBySlug()
    {
        var projectId = await CreateProjectAsync();
        const string slug = "idempotent-arch";

        (await SubmitInboxAsync(projectId, slug: slug, title: "Original title", content: "original content"))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var second = await SubmitInboxAsync(projectId, slug: slug, title: "Updated title", content: "updated content");

        second.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
        (await CountInboxEntriesBySlugAsync(projectId, slug)).Should().Be(1);

        var entry = await GetInboxEntryBySlugAsync(projectId, slug);
        entry.Should().NotBeNull();
        entry!.Title.Should().Be("Updated title", "idempotent submit updates the pending entry title");
        entry.Content.Should().Be("updated content", "idempotent submit updates the pending entry content");
    }

    [Fact]
    public async Task Test_InboxMerge_AtomicallyCreatesDecision()
    {
        var projectId = await CreateProjectAsync();
        var entryId = await SeedInboxEntryAsync(projectId, "smith", "merge-atomic", "architectural", "Merge me", "atomic content");

        var response = await _client.PostAsync($"/api/projects/{projectId}/decisions/inbox/{entryId}/merge", null);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetInt32().Should().Be(entryId);
        body.GetProperty("status").GetString().Should().Be("merged");
        var decisionId = body.GetProperty("decisionId").GetInt32();
        body.GetProperty("mergedAt").ValueKind.Should().NotBe(JsonValueKind.Null);
        decisionId.Should().BeGreaterThan(0);

        var decisionsResp = await _client.GetAsync($"/api/projects/{projectId}/decisions");
        decisionsResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var decisions = await decisionsResp.Content.ReadFromJsonAsync<JsonElement[]>();
        var decision = decisions!.Single(d => d.GetProperty("id").GetInt32() == decisionId);
        decision.GetProperty("type").GetString().Should().Be("architectural");
        decision.GetProperty("content").GetString().Should().Be("atomic content");
        decision.GetProperty("agentName").GetString().Should().Be("smith");

        var entry = await GetInboxEntryAsync(entryId);
        entry!.Status.Should().Be("merged");
        entry.MergedAt.Should().NotBeNull();
        entry.DecisionId.Should().Be(decisionId);
    }

    [Fact]
    public async Task Test_InboxMerge_AlreadyMerged_Returns409()
    {
        var projectId = await CreateProjectAsync();
        var decisionId = await SeedDecisionAsync(projectId, "smith", "architectural", "Existing", "decision");
        var entryId = await SeedInboxEntryAsync(projectId, "smith", "already-merged", "architectural", "Done", "done",
            status: "merged", decisionId: decisionId, mergedAt: DateTimeOffset.UtcNow);

        var response = await _client.PostAsync($"/api/projects/{projectId}/decisions/inbox/{entryId}/merge", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Test_InboxReject_MarksRejectedNoDecision()
    {
        var projectId = await CreateProjectAsync();
        var entryId = await SeedInboxEntryAsync(projectId, "smith", "reject-me", "architectural", "Reject", "no decision");

        var response = await _client.PostAsync($"/api/projects/{projectId}/decisions/inbox/{entryId}/reject", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Object);
        body.GetProperty("id").GetInt32().Should().Be(entryId);
        body.GetProperty("status").GetString().Should().Be("rejected");

        var entry = await GetInboxEntryAsync(entryId);
        entry.Should().NotBeNull();
        entry!.Status.Should().Be("rejected");
        entry.DecisionId.Should().BeNull();
        entry.MergedAt.Should().BeNull();

        var decisionsResp = await _client.GetAsync($"/api/projects/{projectId}/decisions");
        decisionsResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var decisions = await decisionsResp.Content.ReadFromJsonAsync<JsonElement[]>();
        decisions.Should().NotContain(d => d.GetProperty("content").GetString() == "no decision");
    }

    [Fact]
    public async Task Test_GetInbox_DefaultsToOnlyPending()
    {
        var projectId = await CreateProjectAsync();
        await SeedInboxEntryAsync(projectId, "smith", "pending-only", "architectural", "Pending", "pending");
        await SeedInboxEntryAsync(projectId, "smith", "merged-hidden", "architectural", "Merged", "merged", status: "merged", mergedAt: DateTimeOffset.UtcNow);
        await SeedInboxEntryAsync(projectId, "smith", "rejected-hidden", "architectural", "Rejected", "rejected", status: "rejected");

        var response = await _client.GetAsync($"/api/projects/{projectId}/decisions/inbox");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entries = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        entries.Should().ContainSingle();
        entries![0].GetProperty("slug").GetString().Should().Be("pending-only");
    }

    [Fact]
    public async Task Test_GetInbox_FiltersWorkCorrectly()
    {
        var projectId = await CreateProjectAsync();
        await SeedInboxEntryAsync(projectId, "agent-a", "pending-a", "architectural", "Pending", "pending");
        await SeedInboxEntryAsync(projectId, "agent-a", "merged-a", "architectural", "Merged A", "merged", status: "merged", mergedAt: DateTimeOffset.UtcNow);
        await SeedInboxEntryAsync(projectId, "agent-b", "merged-b", "architectural", "Merged B", "merged", status: "merged", mergedAt: DateTimeOffset.UtcNow);
        await SeedInboxEntryAsync(projectId, "agent-a", "merged-process", "process", "Merged Process", "merged process", status: "merged", mergedAt: DateTimeOffset.UtcNow);

        var mergedResp = await _client.GetAsync($"/api/projects/{projectId}/decisions/inbox?status=merged");
        mergedResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var merged = await mergedResp.Content.ReadFromJsonAsync<JsonElement[]>();
        merged.Should().NotBeNull();
        merged!.Select(e => e.GetProperty("slug").GetString()).Should().BeEquivalentTo("merged-a", "merged-b", "merged-process");
        merged.Should().OnlyContain(e => e.GetProperty("status").GetString() == "merged");

        var agentResp = await _client.GetAsync($"/api/projects/{projectId}/decisions/inbox?status=merged&agent=agent-a");
        agentResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var agentEntries = await agentResp.Content.ReadFromJsonAsync<JsonElement[]>();
        agentEntries.Should().NotBeNull();
        agentEntries!.Select(e => e.GetProperty("slug").GetString()).Should().BeEquivalentTo("merged-a", "merged-process");
        agentEntries.Should().OnlyContain(e => e.GetProperty("agentName").GetString() == "agent-a");

        var typeResp = await _client.GetAsync($"/api/projects/{projectId}/decisions/inbox?status=merged&type=process");
        typeResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var typeEntries = await typeResp.Content.ReadFromJsonAsync<JsonElement[]>();
        typeEntries.Should().ContainSingle();
        typeEntries![0].GetProperty("slug").GetString().Should().Be("merged-process");
        typeEntries[0].GetProperty("type").GetString().Should().Be("process");
    }

    [Fact]
    public async Task Test_GetDecisions_DefaultsToOnlyActive()
    {
        var projectId = await CreateProjectAsync();
        await SeedDecisionAsync(projectId, "smith", "architectural", "Active", "active", status: "active");
        await SeedDecisionAsync(projectId, "smith", "architectural", "Superseded", "superseded", status: "superseded");
        await SeedDecisionAsync(projectId, "smith", "architectural", "Archived", "archived", status: "archived");

        var response = await _client.GetAsync($"/api/projects/{projectId}/decisions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var decisions = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        decisions.Should().ContainSingle();
        decisions![0].GetProperty("title").GetString().Should().Be("Active");
    }

    [Fact]
    public async Task Test_PutDecision_SupersedesWithValidation()
    {
        var projectId = await CreateProjectAsync();
        var a = await SeedDecisionAsync(projectId, "smith", "architectural", "A", "a");
        var b = await SeedDecisionAsync(projectId, "smith", "architectural", "B", "b");

        var response = await _client.PutAsJsonAsync($"/api/projects/{projectId}/decisions/{a}", new { superseded_by_id = b });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("superseded_by_id").GetInt32().Should().Be(b);
        var decision = await GetDecisionAsync(a);
        decision!.Status.Should().Be("superseded");
        decision.SupersededById.Should().Be(b);

        var missing = await _client.PutAsJsonAsync($"/api/projects/{projectId}/decisions/{a}", new { superseded_by_id = 99999 });
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Test_Memory_StoreAndRetrieveByAgent()
    {
        var projectId = await CreateProjectAsync();

        var create = await _client.PostAsJsonAsync($"/api/projects/{projectId}/agents/smith/memory", new
        {
            type = "learning",
            importance = "high",
            content = "remember this",
            session_id = "sess-1",
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await _client.GetAsync($"/api/projects/{projectId}/agents/smith/memory");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var memories = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        memories.Should().ContainSingle();
        var memory = memories![0];
        memory.GetProperty("type").GetString().Should().Be("learning");
        memory.GetProperty("importance").GetString().Should().Be("high");
        memory.GetProperty("content").GetString().Should().Be("remember this");
        memory.GetProperty("sessionId").GetString().Should().Be("sess-1");
    }

    [Fact]
    public async Task Test_Memory_FiltersTypeAndImportance()
    {
        var projectId = await CreateProjectAsync();
        await SeedAgentMemoryAsync(projectId, "smith", "learning", "high", "one");
        await SeedAgentMemoryAsync(projectId, "smith", "learning", "low", "two");
        await SeedAgentMemoryAsync(projectId, "smith", "pattern", "high", "three");

        var response = await _client.GetAsync($"/api/projects/{projectId}/agents/smith/memory?type=learning&importance=high");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var memories = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        memories.Should().ContainSingle();
        memories![0].GetProperty("content").GetString().Should().Be("one");
    }

    [Fact]
    public async Task Test_Memory_CrossAgentTagSearch()
    {
        var projectId = await CreateProjectAsync();
        var createA = await _client.PostAsJsonAsync($"/api/projects/{projectId}/agents/agent-a/memory", new
        {
            type = "learning",
            importance = "high",
            content = "database memory",
            tags = "cross-team, database",
        });
        createA.StatusCode.Should().Be(HttpStatusCode.Created);
        var createB = await _client.PostAsJsonAsync($"/api/projects/{projectId}/agents/agent-b/memory", new
        {
            type = "learning",
            importance = "high",
            content = "unrelated memory",
            tags = "unrelated",
        });
        createB.StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await _client.GetAsync($"/api/projects/{projectId}/memory?tags=database");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var memories = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        memories.Should().ContainSingle();
        var memory = memories![0];
        memory.GetProperty("agentName").GetString().Should().Be("agent-a");
        memory.GetProperty("content").GetString().Should().Be("database memory");
        memory.GetProperty("tags").GetString().Should().Be(",cross-team,database,");
    }

    [Fact]
    public async Task Test_Session_CreateAndGetCurrent()
    {
        var projectId = await CreateProjectAsync();

        var create = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new
        {
            focus_area = "feature-x",
            session_id = "sess-abc",
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await _client.GetAsync($"/api/projects/{projectId}/sessions/current");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var session = await response.Content.ReadFromJsonAsync<JsonElement>();
        session.GetProperty("sessionId").GetString().Should().Be("sess-abc");
        session.GetProperty("focusArea").GetString().Should().Be("feature-x");
        session.GetProperty("ended_at").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Test_Session_DuplicateIdReturns409()
    {
        var projectId = await CreateProjectAsync();

        var first = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new
        {
            focus_area = "feature-x",
            session_id = "sess-dupe",
        });
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new
        {
            focus_area = "feature-y",
            session_id = "sess-dupe",
        });

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Test_Session_EndSession()
    {
        var projectId = await CreateProjectAsync();
        (await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new
        {
            focus_area = "feature-x",
            session_id = "sess-end",
        })).StatusCode.Should().Be(HttpStatusCode.Created);

        var end = await _client.PutAsJsonAsync($"/api/projects/{projectId}/sessions/current", new { end = true });

        end.StatusCode.Should().Be(HttpStatusCode.OK);
        var current = await _client.GetAsync($"/api/projects/{projectId}/sessions/current");
        current.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var sessionsResp = await _client.GetAsync($"/api/projects/{projectId}/sessions");
        sessionsResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var sessions = await sessionsResp.Content.ReadFromJsonAsync<JsonElement[]>();
        sessions.Should().ContainSingle();
        var persisted = sessions![0];
        persisted.GetProperty("sessionId").GetString().Should().Be("sess-end");
        persisted.GetProperty("ended_at").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Test_Session_PutWithNoActive_Returns404()
    {
        var projectId = await CreateProjectAsync();

        var response = await _client.PutAsJsonAsync($"/api/projects/{projectId}/sessions/current", new { summary = "none" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Test_InboxSubmit_ConflictOnMergedSlug_Returns409()
    {
        // When a slug was already merged (status != "pending"), re-submitting the same slug must 409.
        // This is the live scenario that caused opaque "Tool execution failed" in the agent runtime.
        var projectId = await CreateProjectAsync();
        var decisionId = await SeedDecisionAsync(projectId, "smith", "architectural", "Existing", "decision");
        await SeedInboxEntryAsync(projectId, "smith", "already-merged-slug", "architectural",
            "Done", "done", status: "merged", decisionId: decisionId, mergedAt: DateTimeOffset.UtcNow);

        var response = await SubmitInboxAsync(projectId, slug: "already-merged-slug",
            title: "Re-submit attempt", content: "Should conflict");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "re-submitting a slug whose entry is already merged must return 409, not update the entry");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Test_InboxSubmit_ConflictOnRejectedSlug_Returns409()
    {
        var projectId = await CreateProjectAsync();
        await SeedInboxEntryAsync(projectId, "smith", "already-rejected-slug", "architectural",
            "Rejected", "rejected content", status: "rejected");

        var response = await SubmitInboxAsync(projectId, slug: "already-rejected-slug",
            title: "Re-submit attempt", content: "Should conflict");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "re-submitting a slug whose entry is already rejected must return 409");
    }

    private async Task<string> CreateProjectAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new
        {
            name = $"Memory Test {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = _factory.NewWorkingDirectory(),
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project_id").GetString()!;
    }

    private Task<HttpResponseMessage> SubmitInboxAsync(
        string projectId,
        string agentName = "smith",
        string slug = "decision-slug",
        string title = "Decision title",
        string content = "Decision content",
        string type = "architectural") =>
        _client.PostAsJsonAsync($"/api/projects/{projectId}/decisions/inbox", new
        {
            agent_name = agentName,
            slug,
            title,
            content,
            type,
        });

    private async Task<int> SeedInboxEntryAsync(
        string projectId,
        string agentName,
        string slug,
        string type,
        string title,
        string content,
        string status = "pending",
        int? decisionId = null,
        DateTimeOffset? mergedAt = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        var entry = new DecisionInboxEntry
        {
            ProjectId = projectId,
            AgentName = agentName,
            Slug = slug,
            Type = type,
            Title = title,
            Content = content,
            Status = status,
            DecisionId = decisionId,
            MergedAt = mergedAt,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.DecisionInbox.Add(entry);
        await db.SaveChangesAsync();
        return entry.Id;
    }

    private async Task<int> SeedDecisionAsync(
        string projectId,
        string agentName,
        string type,
        string title,
        string content,
        string status = "active")
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        var decision = new Decision
        {
            ProjectId = projectId,
            AgentName = agentName,
            Type = type,
            Status = status,
            Title = title,
            Content = content,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Decisions.Add(decision);
        await db.SaveChangesAsync();
        return decision.Id;
    }

    private async Task SeedAgentMemoryAsync(
        string projectId,
        string agentName,
        string type,
        string importance,
        string content,
        string? tags = null,
        string? sessionId = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.AgentMemory.Add(new AgentMemory
        {
            ProjectId = projectId,
            AgentName = agentName,
            Type = type,
            Importance = importance,
            Content = content,
            Tags = tags,
            SessionId = sessionId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private async Task<DecisionInboxEntry?> GetInboxEntryAsync(int entryId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.DecisionInbox.AsNoTracking().FirstOrDefaultAsync(e => e.Id == entryId);
    }

    private async Task<DecisionInboxEntry?> GetInboxEntryBySlugAsync(string projectId, string slug)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.DecisionInbox.AsNoTracking().FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Slug == slug);
    }

    private async Task<int> CountInboxEntriesBySlugAsync(string projectId, string slug)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.DecisionInbox.CountAsync(e => e.ProjectId == projectId && e.Slug == slug);
    }

    private async Task<Decision?> GetDecisionAsync(int decisionId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.Decisions.AsNoTracking().FirstOrDefaultAsync(d => d.Id == decisionId);
    }
}
