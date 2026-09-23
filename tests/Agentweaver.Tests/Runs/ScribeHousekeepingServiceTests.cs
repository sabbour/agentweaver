using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Runs;

public sealed class ScribeHousekeepingServiceTests
{
    [Fact]
    public async Task RepeatedAndPartialReplay_ProducesExactlyOnceLogicalState()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var run = Seed(db, "worker");
        await db.SaveChangesAsync();

        var service = new ScribeHousekeepingService(
            db, projectStore: null, NullLogger<ScribeHousekeepingService>.Instance);
        var request = new ScribeHousekeepingRequest(run, ScribeAuthority.Worker, "completed");

        await service.RunAsync(request, CancellationToken.None);
        await service.RunAsync(request, CancellationToken.None);

        (await db.Decisions.CountAsync()).Should().Be(1);
        (await db.DecisionInbox.SingleAsync(entry => entry.Type == "learning")).Status.Should().Be("merged");
        (await db.DecisionInbox.SingleAsync(entry => entry.Type == "architectural")).Status.Should().Be("pending");
        var summary = (await db.SessionContexts.SingleAsync()).Summary!;
        summary.Split('\n').Should().ContainSingle();
        (await db.ScribeOperationAttempts.CountAsync(attempt => attempt.Status == "completed"))
            .Should().Be(5);

        db.ScribeOperationAttempts.Add(new ScribeOperationAttempt
        {
            OperationKey = $"scribe:{run.Id}:generation:{run.LifecycleGeneration}:export",
            ProjectId = run.ProjectId!.Value.ToString(),
            RunId = run.Id.ToString(),
            LifecycleGeneration = run.LifecycleGeneration,
            OperationType = "export",
            Status = "failed",
            FailureCode = "scribe_persistence_failure",
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await service.RunAsync(request, CancellationToken.None);
        (await db.Decisions.CountAsync()).Should().Be(1);
        (await db.ScribeOperationAttempts.CountAsync(attempt => attempt.Status == "failed"))
            .Should().Be(1, "failed attempts remain auditable without reopening completed operations");
    }

    [Fact]
    public async Task ConcurrentRecovery_KeepsDecisionAndSessionUnique()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scribe-{Guid.NewGuid():N}.db");
        try
        {
            await using (var setup = CreateContext($"Data Source={path}"))
            {
                await setup.Database.EnsureCreatedAsync();
                Seed(setup, "worker");
                await setup.SaveChangesAsync();
            }

            await using var db1 = CreateContext($"Data Source={path};Default Timeout=5");
            await using var db2 = CreateContext($"Data Source={path};Default Timeout=5");
            var run1 = await LoadRunShapeAsync(db1);
            var run2 = await LoadRunShapeAsync(db2);
            var service1 = new ScribeHousekeepingService(db1, null, NullLogger<ScribeHousekeepingService>.Instance);
            var service2 = new ScribeHousekeepingService(db2, null, NullLogger<ScribeHousekeepingService>.Instance);

            var results = await Task.WhenAll(
                Capture(service1.RunAsync(
                    new ScribeHousekeepingRequest(run1, ScribeAuthority.Worker, "completed"),
                    CancellationToken.None)),
                Capture(service2.RunAsync(
                    new ScribeHousekeepingRequest(run2, ScribeAuthority.Worker, "completed"),
                    CancellationToken.None)));

            results.Count(result => result is null).Should().BeGreaterThanOrEqualTo(1);
            await using var verify = CreateContext($"Data Source={path}");
            (await verify.Decisions.CountAsync()).Should().Be(1);
            (await verify.SessionContexts.SingleAsync()).Summary!.Split('\n').Should().ContainSingle();
            (await verify.ScribeOperationAttempts.CountAsync(attempt => attempt.Status == "failed"))
                .Should().BeLessThanOrEqualTo(1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static async Task<Exception?> Capture(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static Run Seed(MemoryDbContext db, string agentName)
    {
        var projectId = ProjectId.New();
        var run = new Run
        {
            Id = RunId.New(),
            RepositoryPath = "",
            OriginatingBranch = "dev",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "test",
            SubmittingUser = "owner",
            Status = RunStatus.Completed,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ProjectId = projectId,
            AgentName = agentName,
            LifecycleGeneration = 3,
        };
        db.SessionContexts.Add(new SessionContext
        {
            ProjectId = projectId.ToString(),
            SessionId = "session",
            FocusArea = "test",
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
        });
        db.DecisionInbox.AddRange(
            Entry(run, "learning", "learning"),
            Entry(run, "architectural", "architecture"));
        return run;
    }

    private static DecisionInboxEntry Entry(Run run, string type, string slug) => new()
    {
        ProjectId = run.ProjectId!.Value.ToString(),
        AgentName = run.AgentName!,
        Slug = slug,
        Type = type,
        Title = slug,
        Content = slug,
        Status = "pending",
        SourceKind = MemorySourceKinds.Run,
        SourceIdentity = $"run:{run.Id}",
        SourceRunId = run.Id.ToString(),
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static Task<Run> LoadRunShapeAsync(MemoryDbContext db)
    {
        var entry = db.DecisionInbox.AsNoTracking().First();
        return Task.FromResult(new Run
        {
            Id = RunId.Parse(entry.SourceRunId!),
            RepositoryPath = "",
            OriginatingBranch = "dev",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "test",
            SubmittingUser = "owner",
            Status = RunStatus.Completed,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ProjectId = ProjectId.Parse(entry.ProjectId),
            AgentName = entry.AgentName,
            LifecycleGeneration = 3,
        });
    }

    private static MemoryDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(connection).Options);

    private static MemoryDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(connectionString).Options);
}
