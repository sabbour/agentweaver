using Agentweaver.Api.Execution;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Agentweaver.Tests.Execution;

public sealed class SqliteExecutionIdentityPersistenceTests
{
    [Fact]
    public async Task Insert_persists_run_and_descriptor_in_one_transaction()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var run = CreateRun();

        await store.InsertAsync(run);

        var descriptors = await ReadDescriptorsAsync(testDb.FilePath, run.Id.ToString());
        descriptors.Should().Equal(
            (ExecutionIdentityDescriptor.DescriptorIdFor(run.Id.ToString(), 1), 1));
    }

    [Fact]
    public async Task Descriptor_collision_rolls_back_the_run_insert()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var run = CreateRun();
        var descriptor = ExecutionIdentityDescriptor.Create(run);
        await SeedDescriptorAsync(testDb.FilePath, descriptor);

        var act = () => store.InsertAsync(run);

        await act.Should().ThrowAsync<SqliteException>();
        (await store.GetAsync(run.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Reopen_creates_a_distinct_retry_attempt_descriptor()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var run = CreateRun() with { Status = RunStatus.Failed, EndedAt = DateTimeOffset.UtcNow };
        await store.InsertAsync(run);

        (await store.TryReopenTerminalToInProgressAsync(run.Id)).Should().BeTrue();

        var descriptors = await ReadDescriptorsAsync(testDb.FilePath, run.Id.ToString());
        descriptors.Should().Equal(
            (ExecutionIdentityDescriptor.DescriptorIdFor(run.Id.ToString(), 1), 1),
            (ExecutionIdentityDescriptor.DescriptorIdFor(run.Id.ToString(), 2), 2));
        (await ReadCreatedAtAsync(testDb.FilePath, run.Id.ToString(), 2))
            .Should().BeAfter(run.StartedAt);
    }

    [Fact]
    public async Task Child_links_to_the_parent_current_attempt()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var parent = CreateRun() with { Status = RunStatus.Failed, EndedAt = DateTimeOffset.UtcNow };
        await store.InsertAsync(parent);
        (await store.TryReopenTerminalToInProgressAsync(parent.Id)).Should().BeTrue();
        var child = CreateRun() with
        {
            Id = RunId.Parse("00000000-0000-0000-0000-000000001405"),
            ParentRunId = parent.Id.ToString(),
            SubtaskId = "child-1",
            AgentName = "Tank",
        };

        await store.InsertAsync(child);

        (await ReadParentDescriptorAsync(testDb.FilePath, child.Id.ToString()))
            .Should().Be(ExecutionIdentityDescriptor.DescriptorIdFor(parent.Id.ToString(), 2));
    }

    private static Run CreateRun() => new()
    {
        Id = RunId.Parse("00000000-0000-0000-0000-000000001404"),
        RepositoryPath = @"C:\repo",
        OriginatingBranch = "dev",
        ModelSource = ModelSource.GitHubCopilot,
        Task = "test",
        SubmittingUser = "principal-123",
        Status = RunStatus.InProgress,
        StartedAt = DateTimeOffset.Parse("2026-09-26T12:00:00Z"),
        ProjectId = ProjectId.Parse("00000000-0000-0000-0000-000000001400"),
        AgentName = "Coordinator",
    };

    private static async Task<IReadOnlyList<(string DescriptorId, int Attempt)>> ReadDescriptorsAsync(
        string path,
        string runId)
    {
        var result = new List<(string, int)>();
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT descriptor_id, attempt FROM execution_identities WHERE run_id = $runId ORDER BY attempt;";
        command.Parameters.AddWithValue("$runId", runId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add((reader.GetString(0), reader.GetInt32(1)));
        return result;
    }

    private static async Task<DateTimeOffset> ReadCreatedAtAsync(string path, string runId, int attempt)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT created_at FROM execution_identities WHERE run_id = $runId AND attempt = $attempt;";
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$attempt", attempt);
        return DateTimeOffset.Parse((string)(await command.ExecuteScalarAsync())!);
    }

    private static async Task<string?> ReadParentDescriptorAsync(string path, string runId)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT parent_descriptor_id FROM execution_identities WHERE run_id = $runId;";
        command.Parameters.AddWithValue("$runId", runId);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task SeedDescriptorAsync(
        string path,
        ExecutionIdentityDescriptor descriptor)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO execution_identities (
                descriptor_id, schema_version, run_id, attempt, initiating_principal_id,
                executing_service_id, agent_assignment_id, created_at)
            VALUES ($descriptorId, 1, $runId, 1, 'principal', 'service', 'assignment', $createdAt);
            """;
        command.Parameters.AddWithValue("$descriptorId", descriptor.DescriptorId);
        command.Parameters.AddWithValue("$runId", descriptor.RunId);
        command.Parameters.AddWithValue("$createdAt", descriptor.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }
}
