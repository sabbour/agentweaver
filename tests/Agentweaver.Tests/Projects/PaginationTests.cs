using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Projects;

/// <summary>
/// Integration tests for the shared pagination contract (<see cref="PagedResult{T}"/> /
/// <see cref="Paging"/>) as applied to list endpoints. Covers: default paging behavior, explicit
/// page/page_size params, boundary behavior (a page beyond available data returns an empty list,
/// not an error), and the max page_size clamp. Exercised against GET /api/projects and
/// GET /api/projects/{id}/runs, which share the same <see cref="Paging.Of{T}"/> helper as the other
/// updated list endpoints (decisions, decisions/inbox, memory, sessions).
/// </summary>
public sealed class PaginationTests : IClassFixture<ProjectsWebApplicationFactory>
{
    private readonly ProjectsWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PaginationTests(ProjectsWebApplicationFactory factory)
    {
        _factory = factory;
        _client  = factory.CreateAuthenticatedClient();
    }

    private string NewWorkingDir() => _factory.NewWorkingDirectory();

    private async Task<string> CreateBlankProjectAsync(string? name = null)
    {
        var request = new CreateProjectRequest
        {
            Name             = name ?? $"Pagination Test Project {Guid.NewGuid():N}",
            Origin           = "blank",
            WorkingDirectory = NewWorkingDir(),
        };
        var response = await _client.PostAsJsonAsync("/api/projects", request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("project_id").GetString()!;
    }

    private async Task InsertRunAsync(string projectId, DateTimeOffset startedAt)
    {
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        await runStore.InsertAsync(new Run
        {
            Id                = RunId.New(),
            RepositoryPath    = NewWorkingDir(),
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "pagination test run",
            SubmittingUser    = ProjectsWebApplicationFactory.TestUser,
            Status            = RunStatus.Completed,
            StartedAt         = startedAt,
            EndedAt           = startedAt.AddMinutes(1),
            ProjectId         = ProjectId.Parse(projectId),
        });
    }

    // =========================================================================
    // Default paging behavior: no page/page_size params -> page=1, page_size=25 (default)
    // =========================================================================
    [Fact]
    public async Task GetProjectRuns_DefaultPaging_UsesPage1AndDefaultPageSize()
    {
        var projectId = await CreateBlankProjectAsync();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 30; i++)
            await InsertRunAsync(projectId, now.AddMinutes(-i));

        var response = await _client.GetAsync($"/api/projects/{projectId}/runs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>();
        envelope.GetProperty("page").GetInt32().Should().Be(1);
        envelope.GetProperty("page_size").GetInt32().Should().Be(Paging.DefaultPageSize);
        envelope.GetProperty("total_count").GetInt32().Should().Be(30);
        envelope.GetProperty("total_pages").GetInt32().Should().Be(2);
        envelope.GetProperty("items").GetArrayLength().Should().Be(Paging.DefaultPageSize);
    }

    // =========================================================================
    // Explicit page/page_size params are honored
    // =========================================================================
    [Fact]
    public async Task GetProjectRuns_ExplicitPageAndPageSize_ReturnsRequestedSlice()
    {
        var projectId = await CreateBlankProjectAsync();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 25; i++)
            await InsertRunAsync(projectId, now.AddMinutes(-i));

        var response = await _client.GetAsync($"/api/projects/{projectId}/runs?page=2&page_size=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>();
        envelope.GetProperty("page").GetInt32().Should().Be(2);
        envelope.GetProperty("page_size").GetInt32().Should().Be(10);
        envelope.GetProperty("total_count").GetInt32().Should().Be(25);
        envelope.GetProperty("total_pages").GetInt32().Should().Be(3);
        envelope.GetProperty("items").GetArrayLength().Should().Be(10);
    }

    // =========================================================================
    // Boundary behavior: a page beyond available data returns an empty list, not an error
    // =========================================================================
    [Fact]
    public async Task GetProjectRuns_PageBeyondAvailableData_ReturnsEmptyItemsNotError()
    {
        var projectId = await CreateBlankProjectAsync();
        await InsertRunAsync(projectId, DateTimeOffset.UtcNow);

        var response = await _client.GetAsync($"/api/projects/{projectId}/runs?page=99&page_size=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>();
        envelope.GetProperty("page").GetInt32().Should().Be(99);
        envelope.GetProperty("total_count").GetInt32().Should().Be(1);
        envelope.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    // =========================================================================
    // Max page_size clamp: a page_size above the max clamps down to Paging.MaxPageSize
    // =========================================================================
    [Fact]
    public async Task GetProjects_PageSizeAboveMax_ClampsToMaxPageSize()
    {
        await CreateBlankProjectAsync();

        var response = await _client.GetAsync("/api/projects?page_size=5000");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>();
        envelope.GetProperty("page_size").GetInt32().Should().Be(Paging.MaxPageSize);
    }

    // =========================================================================
    // Invalid (non-positive) page/page_size values fall back to safe defaults rather than erroring
    // =========================================================================
    [Fact]
    public async Task GetProjects_NonPositivePageAndPageSize_FallBackToDefaults()
    {
        await CreateBlankProjectAsync();

        var response = await _client.GetAsync("/api/projects?page=0&page_size=-5");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>();
        envelope.GetProperty("page").GetInt32().Should().Be(1);
        envelope.GetProperty("page_size").GetInt32().Should().Be(Paging.DefaultPageSize);
    }

    // =========================================================================
    // Legacy `limit` param (pre-pagination) on /runs still works as a page_size alias
    // =========================================================================
    [Fact]
    public async Task GetProjectRuns_LegacyLimitParam_StillBoundsResultSize()
    {
        var projectId = await CreateBlankProjectAsync();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 10; i++)
            await InsertRunAsync(projectId, now.AddMinutes(-i));

        var response = await _client.GetAsync($"/api/projects/{projectId}/runs?limit=3");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>();
        envelope.GetProperty("page_size").GetInt32().Should().Be(3);
        envelope.GetProperty("items").GetArrayLength().Should().Be(3);
    }

    // =========================================================================
    // Overflow guard: an absurdly large page must not overflow int32 and silently return page 1's
    // data — it must return an empty items array with the requested (huge) page echoed back.
    // =========================================================================
    [Fact]
    public async Task GetProjectRuns_HugePageValue_ReturnsEmptyItemsNotPage1Data()
    {
        var projectId = await CreateBlankProjectAsync();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
            await InsertRunAsync(projectId, now.AddMinutes(-i));

        var response = await _client.GetAsync($"/api/projects/{projectId}/runs?page=100000000&page_size=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>();
        envelope.GetProperty("page").GetInt32().Should().Be(100000000);
        envelope.GetProperty("total_count").GetInt32().Should().Be(5);
        envelope.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    // =========================================================================
    // Empty result set: total_pages is 0 (not 1) when there is no data
    // =========================================================================
    [Fact]
    public async Task GetProjectRuns_EmptyProject_TotalPagesIsZero()
    {
        var projectId = await CreateBlankProjectAsync();

        var response = await _client.GetAsync($"/api/projects/{projectId}/runs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>();
        envelope.GetProperty("total_count").GetInt32().Should().Be(0);
        envelope.GetProperty("total_pages").GetInt32().Should().Be(0);
        envelope.GetProperty("items").GetArrayLength().Should().Be(0);
    }
}
