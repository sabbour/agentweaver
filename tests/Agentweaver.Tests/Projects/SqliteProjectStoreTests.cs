using FluentAssertions;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Projects;

/// <summary>
/// Unit tests for SqliteProjectStore: CRUD operations and CAS gating methods.
/// Each test uses an isolated in-process SQLite database via TestSqliteDb.
/// </summary>
public sealed class SqliteProjectStoreTests
{
    // =========================================================================
    // Helpers
    // =========================================================================

    private static Project MakeProject(string? name = null, string? workingDir = null) => new()
    {
        Id               = ProjectId.New(),
        Name             = name ?? "Test Project",
        Origin           = ProjectOrigin.Blank(),
        WorkingDirectory = workingDir ?? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
        DefaultBranch    = "main",
        Owner            = "test-user",
        ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
        State            = ProjectState.Active,
        CreatedAt        = DateTimeOffset.UtcNow,
        UpdatedAt        = DateTimeOffset.UtcNow,
    };

    // =========================================================================
    // PS-01: InsertAsync / GetAsync roundtrip
    // =========================================================================
    [Fact]
    public async Task Insert_Get_Roundtrip()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteProjectStore(testDb.Db);
        var project = MakeProject("My Project");

        await store.InsertAsync(project);
        var retrieved = await store.GetAsync(project.Id);

        retrieved.Should().NotBeNull();
        retrieved!.Id.Should().Be(project.Id);
        retrieved.Name.Should().Be("My Project");
        retrieved.State.Should().Be(ProjectState.Active);
    }

    // =========================================================================
    // PS-02: GetAsync returns null for unknown id
    // =========================================================================
    [Fact]
    public async Task GetAsync_ReturnsNull_ForUnknownId()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteProjectStore(testDb.Db);

        var result = await store.GetAsync(ProjectId.New());

        result.Should().BeNull();
    }

    // =========================================================================
    // PS-03: ListAsync returns all inserted projects ordered by created_at DESC
    // =========================================================================
    [Fact]
    public async Task ListAsync_ReturnsAllProjects()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteProjectStore(testDb.Db);

        var p1 = MakeProject("Alpha");
        var p2 = MakeProject("Beta");
        await store.InsertAsync(p1);
        await Task.Delay(5); // ensure distinct timestamps
        await store.InsertAsync(p2);

        var list = await store.ListAsync();

        list.Should().HaveCount(2);
        // Most recently inserted first
        list[0].Name.Should().Be("Beta");
        list[1].Name.Should().Be("Alpha");
    }

    // =========================================================================
    // PS-04: UpdateNameAsync persists the new name
    // =========================================================================
    [Fact]
    public async Task UpdateNameAsync_PersistsNewName()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteProjectStore(testDb.Db);
        var project = MakeProject("Original Name");
        await store.InsertAsync(project);

        await store.UpdateNameAsync(project.Id, "Renamed Project", DateTimeOffset.UtcNow);

        var retrieved = await store.GetAsync(project.Id);
        retrieved!.Name.Should().Be("Renamed Project");
    }

    // =========================================================================
    // PS-05: UpdateProviderSettingsAsync persists new provider settings
    // =========================================================================
    [Fact]
    public async Task UpdateProviderSettingsAsync_PersistsNewSettings()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteProjectStore(testDb.Db);
        var project = MakeProject();
        await store.InsertAsync(project);

        var newSettings = new ProjectProviderSettings
        {
            DefaultProvider       = ModelSource.MicrosoftFoundry,
            GitHubCopilotModel    = "gpt-4o",
            MicrosoftFoundryModel = "foundry-model-1",
        };
        await store.UpdateProviderSettingsAsync(project.Id, newSettings, DateTimeOffset.UtcNow);

        var retrieved = await store.GetAsync(project.Id);
        retrieved!.ProviderSettings.DefaultProvider.Should().Be(ModelSource.MicrosoftFoundry);
        retrieved.ProviderSettings.GitHubCopilotModel.Should().Be("gpt-4o");
        retrieved.ProviderSettings.MicrosoftFoundryModel.Should().Be("foundry-model-1");
    }

    // =========================================================================
    // PS-06: DeleteAsync removes the record
    // =========================================================================
    [Fact]
    public async Task DeleteAsync_RemovesRecord()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteProjectStore(testDb.Db);
        var project = MakeProject();
        await store.InsertAsync(project);

        await store.DeleteAsync(project.Id);

        var result = await store.GetAsync(project.Id);
        result.Should().BeNull();
    }

    // =========================================================================
    // PS-07: TryBeginDeleteAsync Active->Deleting succeeds
    // =========================================================================
    [Fact]
    public async Task TryBeginDeleteAsync_ActiveToDeleting_Succeeds()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteProjectStore(testDb.Db);
        var project = MakeProject();
        await store.InsertAsync(project);

        var result = await store.TryBeginDeleteAsync(project.Id);

        result.Should().BeTrue("Active -> Deleting CAS must succeed for an active project");
        var retrieved = await store.GetAsync(project.Id);
        retrieved!.State.Should().Be(ProjectState.Deleting);
    }

    // =========================================================================
    // PS-08: TryBeginDeleteAsync Deleting->Deleting returns false (CAS gate)
    // =========================================================================
    [Fact]
    public async Task TryBeginDeleteAsync_DeletingToDeleting_ReturnsFalse()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteProjectStore(testDb.Db);
        var project = MakeProject();
        await store.InsertAsync(project);
        await store.TryBeginDeleteAsync(project.Id); // first call puts it in Deleting

        var secondResult = await store.TryBeginDeleteAsync(project.Id);

        secondResult.Should().BeFalse("second CAS must fail because project is already Deleting");
    }

    // =========================================================================
    // PS-09: TryBeginDeleteAsync returns false for unknown project
    // =========================================================================
    [Fact]
    public async Task TryBeginDeleteAsync_UnknownId_ReturnsFalse()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteProjectStore(testDb.Db);

        var result = await store.TryBeginDeleteAsync(ProjectId.New());

        result.Should().BeFalse();
    }
}
