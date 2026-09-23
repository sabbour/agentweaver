using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using FluentAssertions;
using LibGit2Sharp;
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

    [Fact]
    public async Task ConcurrentExport_ClaimsBeforeInvokingSideEffect()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scribe-export-{Guid.NewGuid():N}.db");
        var repositoryPath = CreateRepository();
        try
        {
            Run seededRun;
            await using (var setup = CreateContext($"Data Source={path}"))
            {
                await setup.Database.EnsureCreatedAsync();
                seededRun = Seed(setup, "worker");
                await setup.SaveChangesAsync();
            }

            await using var db1 = CreateContext($"Data Source={path};Default Timeout=5");
            await using var db2 = CreateContext($"Data Source={path};Default Timeout=5");
            var project = ProjectFor(seededRun, repositoryPath);
            var exporter = new RecordingExportOperation(
                new ScribeExportOperation(db1),
                blockApply: true);
            var service1 = new ScribeHousekeepingService(
                db1,
                new SingleProjectStore(project),
                NullLogger<ScribeHousekeepingService>.Instance,
                exporter);
            var service2 = new ScribeHousekeepingService(
                db2,
                new SingleProjectStore(project),
                NullLogger<ScribeHousekeepingService>.Instance,
                exporter);
            var request1 = new ScribeHousekeepingRequest(
                await LoadRunShapeAsync(db1), ScribeAuthority.Worker, "completed");
            var request2 = new ScribeHousekeepingRequest(
                await LoadRunShapeAsync(db2), ScribeAuthority.Worker, "completed");

            var first = service1.RunAsync(request1, CancellationToken.None);
            await exporter.WaitUntilInvokedAsync();
            var second = service2.RunAsync(request2, CancellationToken.None);
            await exporter.WaitUntilActiveClaimObservedAsync();
            exporter.Release();
            await Task.WhenAll(first, second);

            exporter.ApplyCount.Should().Be(1);
            CountOperationCommits(
                repositoryPath,
                project.DefaultBranch,
                $"scribe:{seededRun.Id}:generation:{seededRun.LifecycleGeneration}:export")
                .Should().Be(1);
            await using var verify = CreateContext($"Data Source={path}");
            (await verify.ScribeOperationAttempts.CountAsync(attempt =>
                attempt.OperationType == "export" && attempt.Status == "completed")).Should().Be(1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
                File.Delete(path);
            DeleteRepository(repositoryPath);
        }
    }

    [Fact]
    public async Task AppliedExportRecovery_CompletesClaimWithoutInvokingSideEffectAgain()
    {
        var repositoryPath = CreateRepository();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var run = Seed(db, "worker");
        var operationKey = $"scribe:{run.Id}:generation:{run.LifecycleGeneration}:export";
        db.ScribeOperationAttempts.Add(new ScribeOperationAttempt
        {
            OperationKey = operationKey,
            ProjectId = run.ProjectId!.Value.ToString(),
            RunId = run.Id.ToString(),
            LifecycleGeneration = run.LifecycleGeneration,
            OperationType = "export",
            Status = "started",
            StartedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var project = ProjectFor(run, repositoryPath);
        var realExporter = new ScribeExportOperation(db);
        await realExporter.ApplyAsync(
            run.ProjectId!.Value.ToString(),
            repositoryPath,
            project.DefaultBranch,
            operationKey,
            CancellationToken.None);
        var exporter = new RecordingExportOperation(realExporter);
        var service = new ScribeHousekeepingService(
            db,
            new SingleProjectStore(project),
            NullLogger<ScribeHousekeepingService>.Instance,
            exporter);

        try
        {
            await service.RunAsync(
                new ScribeHousekeepingRequest(run, ScribeAuthority.Worker, "completed"),
                CancellationToken.None);

            exporter.ApplyCount.Should().Be(0);
            CountOperationCommits(repositoryPath, project.DefaultBranch, operationKey)
                .Should().Be(1);
            (await db.ScribeOperationAttempts.SingleAsync(attempt =>
                attempt.OperationKey == operationKey && attempt.Status == "completed"))
                .CompletedAt.Should().NotBeNull();
        }
        finally
        {
            DeleteRepository(repositoryPath);
        }
    }

    [Fact]
    public async Task AbandonedExportClaim_IsFailedBeforeBoundedRecovery()
    {
        var repositoryPath = CreateRepository();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var run = Seed(db, "worker");
        var operationKey = $"scribe:{run.Id}:generation:{run.LifecycleGeneration}:export";
        db.ScribeOperationAttempts.Add(new ScribeOperationAttempt
        {
            OperationKey = operationKey,
            ProjectId = run.ProjectId!.Value.ToString(),
            RunId = run.Id.ToString(),
            LifecycleGeneration = run.LifecycleGeneration,
            OperationType = "export",
            Status = "started",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-11),
        });
        await db.SaveChangesAsync();

        var project = ProjectFor(run, repositoryPath);
        var exporter = new RecordingExportOperation(new ScribeExportOperation(db));
        var service = new ScribeHousekeepingService(
            db,
            new SingleProjectStore(project),
            NullLogger<ScribeHousekeepingService>.Instance,
            exporter);

        try
        {
            await service.RunAsync(
                new ScribeHousekeepingRequest(run, ScribeAuthority.Worker, "completed"),
                CancellationToken.None);

            exporter.ApplyCount.Should().Be(1);
            (await db.ScribeOperationAttempts.CountAsync(attempt =>
                attempt.OperationKey == operationKey
                && attempt.Status == "failed"
                && attempt.FailureCode == "scribe_abandoned_claim")).Should().Be(1);
            (await db.ScribeOperationAttempts.CountAsync(attempt =>
                attempt.OperationKey == operationKey && attempt.Status == "completed")).Should().Be(1);
            CountOperationCommits(repositoryPath, project.DefaultBranch, operationKey)
                .Should().Be(1);
        }
        finally
        {
            DeleteRepository(repositoryPath);
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

    private static Project ProjectFor(Run run, string workingDirectory) => new()
    {
        Id = run.ProjectId!.Value,
        Name = "test",
        Origin = ProjectOrigin.Blank(),
        WorkingDirectory = workingDirectory,
        DefaultBranch = GetCurrentBranch(workingDirectory),
        Owner = "owner",
        ProviderSettings = new ProjectProviderSettings
        {
            DefaultProvider = ModelSource.GitHubCopilot,
        },
        State = ProjectState.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

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

    private static string CreateRepository()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scribe-export-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        Repository.Init(path);
        using var repo = new Repository(path);
        File.WriteAllText(Path.Combine(path, "README.md"), "test");
        Commands.Stage(repo, "README.md");
        var signature = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
        repo.Commit("initial", signature, signature);
        return path;
    }

    private static string GetCurrentBranch(string repositoryPath)
    {
        using var repo = new Repository(repositoryPath);
        return repo.Head.FriendlyName;
    }

    private static void DeleteRepository(string repositoryPath)
    {
        foreach (var file in Directory.EnumerateFiles(
                     repositoryPath, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(repositoryPath, recursive: true);
    }

    private static int CountOperationCommits(
        string repositoryPath,
        string branch,
        string operationKey)
    {
        using var repo = new Repository(repositoryPath);
        var marker = $"Agentweaver-Scribe-Operation: {operationKey}";
        return repo.Branches[branch]!.Commits.Count(
            commit => commit.Message.Contains(marker, StringComparison.Ordinal));
    }

    private sealed class RecordingExportOperation(
        IScribeExportOperation inner,
        bool blockApply = false) : IScribeExportOperation
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _activeClaimObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _isAppliedCount;
        private int _applyCount;

        public int ApplyCount => Volatile.Read(ref _applyCount);

        public async Task<bool> IsAppliedAsync(
            string workingDirectory,
            string defaultBranch,
            string operationKey,
            CancellationToken ct)
        {
            if (Interlocked.Increment(ref _isAppliedCount) >= 2)
                _activeClaimObserved.TrySetResult();
            return await inner.IsAppliedAsync(
                workingDirectory, defaultBranch, operationKey, ct);
        }

        public async Task ApplyAsync(
            string projectId,
            string workingDirectory,
            string defaultBranch,
            string operationKey,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _applyCount);
            _entered.TrySetResult();
            if (blockApply)
                await _release.Task.WaitAsync(ct);
            await inner.ApplyAsync(
                projectId, workingDirectory, defaultBranch, operationKey, ct);
        }

        public Task WaitUntilInvokedAsync() => _entered.Task;
        public Task WaitUntilActiveClaimObservedAsync() =>
            _activeClaimObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        public void Release() => _release.TrySetResult();
    }

    private sealed class SingleProjectStore(Project project) : IProjectStore
    {
        public Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default) =>
            Task.FromResult<Project?>(id == project.Id ? project : null);

        public Task InsertAsync(Project project, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateNameAsync(ProjectId id, string name, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateProviderSettingsAsync(ProjectId id, ProjectProviderSettings settings, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateGenerationModelSettingsAsync(ProjectId id, string? blueprintGenerationModel, string? workflowGenerationModel, string? outcomeSpecGenerationModel, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateOriginAsync(ProjectId id, ProjectOrigin origin, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> TryBeginDeleteAsync(ProjectId id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteAsync(ProjectId id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdatePickupSettingsAsync(ProjectId id, int maxReadyPerHeartbeat, bool autopilot, bool autoApproveTools, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateDefaultWorkflowAsync(ProjectId id, string? workflowId, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateActiveReviewPolicyAsync(ProjectId id, string? policyName, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateSandboxProfileAsync(ProjectId id, string? sandboxProfile, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateSourceBlueprintAsync(ProjectId id, string? blueprintId, string? blueprintType, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateAllowedWorkflowIdsAsync(ProjectId id, IReadOnlyList<string>? allowedWorkflowIds, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IProjectTeamMutationLease?> TryBeginTeamMutationAsync(ProjectId id, long expectedRevision, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
