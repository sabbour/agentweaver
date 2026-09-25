using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Tests;

public sealed class SqliteWorkflowPinPersistenceTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), $"agentweaver-sqlite-workflow-pin-{Guid.NewGuid():N}");

    [Fact]
    public async Task InsertAsync_RoundTripsCompleteExecutableWorkflowPin()
    {
        var db = await CreateDatabaseAsync();
        var store = new SqliteRunStore(db);
        var run = PinnedRun(projectId: null);

        await store.InsertAsync(run);

        var persisted = await store.GetAsync(run.Id);
        persisted.Should().NotBeNull();
        persisted!.GetExecutableWorkflowPin().Should().BeEquivalentTo(run.GetExecutableWorkflowPin());
    }

    [Fact]
    public async Task TryCreateProjectRunAsync_RoundTripsCompleteExecutableWorkflowPin()
    {
        var db = await CreateDatabaseAsync();
        var project = new Project
        {
            Id = ProjectId.New(),
            Name = "Pinned workflow project",
            Origin = ProjectOrigin.Blank(),
            WorkingDirectory = _tempDirectory,
            DefaultBranch = "main",
            Owner = "test-user",
            ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
            State = ProjectState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await new SqliteProjectStore(db).InsertAsync(project);
        var store = new SqliteRunStore(db);
        var run = PinnedRun(project.Id);

        (await store.TryCreateProjectRunAsync(run)).Should().BeTrue();

        var persisted = await store.GetAsync(run.Id);
        persisted.Should().NotBeNull();
        persisted!.GetExecutableWorkflowPin().Should().BeEquivalentTo(run.GetExecutableWorkflowPin());
    }

    [Fact]
    public async Task EnsureCreatedAsync_LegacyMetricsRebuild_PreservesWorkflowPinColumns()
    {
        var db = await CreateDatabaseAsync();
        var store = new SqliteRunStore(db);
        var run = PinnedRun(projectId: null);
        await store.InsertAsync(run);
        await using (var connection = await db.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE runs ADD COLUMN step_count INTEGER;";
            await command.ExecuteNonQueryAsync();
        }

        await db.EnsureCreatedAsync();

        await using var upgraded = await db.OpenConnectionAsync();
        await using var pragma = upgraded.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(runs);";
        await using var reader = await pragma.ExecuteReaderAsync();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));

        columns.Should().Contain(
        [
            "executable_workflow_pin_required",
            "executable_workflow_manifest_schema_version",
            "executable_workflow_definition_id",
            "executable_workflow_definition_version",
            "executable_workflow_source",
            "executable_workflow_content_digest",
            "executable_workflow_definition_yaml",
            "executable_workflow_pinned_at",
        ]);
        (await store.GetAsync(run.Id))!.GetExecutableWorkflowPin()
            .Should().BeEquivalentTo(run.GetExecutableWorkflowPin());
    }

    private async Task<SqliteDb> CreateDatabaseAsync()
    {
        Directory.CreateDirectory(_tempDirectory);
        var db = new SqliteDb(Configuration(Path.Combine(_tempDirectory, "agentweaver.db")));
        await db.EnsureCreatedAsync();
        return db;
    }

    private static IConfiguration Configuration(string path) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Path"] = path })
            .Build();

    private static Run PinnedRun(ProjectId? projectId)
    {
        const string yaml = "id: pinned-workflow";
        return new Run
        {
            Id = RunId.New(),
            RepositoryPath = Path.GetTempPath(),
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "persist workflow pin",
            SubmittingUser = "test-user",
            Status = RunStatus.Pending,
            StartedAt = DateTimeOffset.UtcNow,
            ProjectId = projectId,
            ExecutableWorkflowPinRequired = true,
            ExecutableWorkflowManifestSchemaVersion = ExecutableWorkflowPin.CurrentSchemaVersion,
            ExecutableWorkflowDefinitionId = "pinned-workflow",
            ExecutableWorkflowDefinitionVersion = "7",
            ExecutableWorkflowSource = "project",
            ExecutableWorkflowContentDigest = "sha256:" + new string('d', 64),
            ExecutableWorkflowDefinitionYaml = yaml,
            ExecutableWorkflowPinnedAt = DateTimeOffset.UtcNow,
        };
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }
}
