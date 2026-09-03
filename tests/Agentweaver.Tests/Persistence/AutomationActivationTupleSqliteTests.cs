using Agentweaver.Api.Memory;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Tests.Persistence;

public sealed class AutomationActivationTupleSqliteTests
{
    [Theory]
    [MemberData(nameof(RepositoryTupleCases))]
    public async Task Active_activation_requires_complete_or_absent_repository_tuple(
        bool hasInstallation,
        bool hasRepository,
        string? repositoryGrantDigest,
        bool isValid)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsAssembly("Agentweaver.Api"))
            .Options;
        await using var db = new MemoryDbContext(options);
        await db.Database.MigrateAsync();

        await AutomationActivationTupleTestData.AssertInsertAsync(
            db, hasInstallation, hasRepository, repositoryGrantDigest, isValid);
    }

    public static TheoryData<bool, bool, string?, bool> RepositoryTupleCases =>
        AutomationActivationTupleTestData.RepositoryTupleCases;
}

internal static class AutomationActivationTupleTestData
{
    private static long _nextDatabaseId = 9_000_000_000;

    internal static TheoryData<bool, bool, string?, bool> RepositoryTupleCases => new()
    {
        { false, false, null, true },
        { false, false, "", false },
        { false, false, "repo-digest", false },
        { false, true, null, false },
        { false, true, "repo-digest", false },
        { true, false, null, false },
        { true, false, "repo-digest", false },
        { true, true, null, false },
        { true, true, "", false },
        { true, true, "repo-digest", true },
    };

    internal static async Task AssertInsertAsync(
        MemoryDbContext db,
        bool hasInstallation,
        bool hasRepository,
        string? repositoryGrantDigest,
        bool isValid)
    {
        var unique = Interlocked.Increment(ref _nextDatabaseId);
        var projectId = $"tuple-{Guid.NewGuid():N}";
        var installationId = hasInstallation ? unique : (long?)null;
        var repositoryId = hasRepository ? unique : (long?)null;
        db.Projects.Add(new ProjectRecord { ProjectId = projectId });
        if (installationId is not null)
        {
            db.GitHubInstallations.Add(new GitHubInstallationRecord
            {
                InstallationId = installationId.Value,
                AppKind = GitHubAppKind.Repo,
                ProjectId = projectId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        if (installationId is not null && repositoryId is not null)
        {
            db.GitHubRepositoryGrants.Add(new GitHubRepositoryGrantRecord
            {
                InstallationId = installationId.Value,
                RepositoryId = repositoryId.Value,
                ProjectId = projectId,
                FullNameDisplay = "owner/repository",
                PermissionDigest = "repo-digest",
                GrantedAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync();

        db.AutomationActivations.Add(new AutomationActivationRecord
        {
            Id = $"activation-{Guid.NewGuid():N}",
            ProjectId = projectId,
            InstallationId = installationId,
            RepositoryId = repositoryId,
            RepositoryGrantDigest = repositoryGrantDigest,
            CopilotBindingId = "binding",
            CopilotBindingGrantDigest = "copilot-digest",
            AutomationKey = "tuple-test",
            Status = AutomationActivationStatus.Active,
            ActivatedAt = DateTimeOffset.UtcNow,
        });

        Func<Task> save = () => db.SaveChangesAsync();
        if (isValid)
            await save.Should().NotThrowAsync();
        else
            await save.Should().ThrowAsync<DbUpdateException>();
    }
}
