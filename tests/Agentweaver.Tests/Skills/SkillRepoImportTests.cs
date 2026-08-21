using Agentweaver.Api.Auth;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Security;
using Agentweaver.Api.Skills;
using Agentweaver.Domain;
using Agentweaver.Domain.Skills;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Skills;

public sealed class SkillRepoImportTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    [Fact]
    public async Task ImportFromRepoAsync_ImportsSkillPinnedToHistoricalTag()
    {
        var sourceRepository = CreateRepositoryWithHistoricalTag();
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projectStore = new SqliteProjectStore(testDb.Db);
        var project = new Project
        {
            Id = ProjectId.New(),
            Name = "skill-import-test",
            Origin = ProjectOrigin.Blank(),
            WorkingDirectory = NewTempDir(),
            DefaultBranch = "main",
            Owner = "owner-1",
            ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
            State = ProjectState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await projectStore.InsertAsync(project);

        var gitInit = new LocalRepositoryCloneInitializer(sourceRepository);
        var service = new SkillCatalogService(
            new SqliteSkillStore(testDb.Db),
            projectStore,
            gitInit,
            new SkillParser(),
            new FixedInstallationScopeStub(),
            new NullGitHubTokenStore(),
            NullLogger<SkillCatalogService>.Instance,
            projectRoles: new AllowAllProjectRoles(),
            configuration: new ConfigurationBuilder().AddInMemoryCollection().Build());

        var result = await service.ImportFromRepoAsync(
            project.Id,
            "https://github.com/owner/repo/tree/v1.0.0",
            locations: null,
            caller: new CallerContext { User = "owner-1" },
            ct: CancellationToken.None);

        result.Outcome.Should().Be(SkillOutcome.Ok);
        result.Error.Should().BeNull();
        result.Results.Should().ContainSingle();
        result.Results[0].Name.Should().Be("version-one");
        gitInit.LastPurpose.Should().Be(GitClonePurpose.SkillImport);
    }

    [Fact]
    public async Task PreviewThenImport_ReusesCachedClone()
    {
        var sourceRepository = CreateRepositoryWithHistoricalTag();
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projectStore = new SqliteProjectStore(testDb.Db);
        var project = new Project
        {
            Id = ProjectId.New(),
            Name = "skill-preview-cache-test",
            Origin = ProjectOrigin.Blank(),
            WorkingDirectory = NewTempDir(),
            DefaultBranch = "main",
            Owner = "owner-1",
            ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
            State = ProjectState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await projectStore.InsertAsync(project);

        var gitInit = new LocalRepositoryCloneInitializer(sourceRepository);
        var service = new SkillCatalogService(
            new SqliteSkillStore(testDb.Db),
            projectStore,
            gitInit,
            new SkillParser(),
            new FixedInstallationScopeStub(),
            new NullGitHubTokenStore(),
            NullLogger<SkillCatalogService>.Instance,
            projectRoles: new AllowAllProjectRoles(),
            configuration: new ConfigurationBuilder().AddInMemoryCollection().Build());

        const string repoUrl = "https://github.com/owner/repo/tree/main/SKILL.md";
        var preview = await service.PreviewRepoCandidatesAsync(
            project.Id,
            repoUrl,
            caller: new CallerContext { User = "owner-1" },
            ct: CancellationToken.None);
        var import = await service.ImportFromRepoAsync(
            project.Id,
            repoUrl,
            locations: null,
            caller: new CallerContext { User = "owner-1" },
            ct: CancellationToken.None);

        preview.Outcome.Should().Be(SkillOutcome.Ok);
        import.Outcome.Should().Be(SkillOutcome.Ok);
        gitInit.CloneCount.Should().Be(1,
            "the import should reuse the preview clone when the same repo URL is imported shortly after preview");
    }

    private string CreateRepositoryWithHistoricalTag()
    {
        var path = NewTempDir();
        Repository.Init(path);
        using var repository = new Repository(path);
        var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);

        File.WriteAllText(
            Path.Combine(path, "SKILL.md"),
            "---\nname: version-one\ndescription: Historical skill.\n---\nUse version one.\n");
        Commands.Stage(repository, "SKILL.md");
        repository.Commit("Add first skill", signature, signature);
        if (!string.Equals(repository.Head.FriendlyName, "main", StringComparison.Ordinal))
            repository.Refs.Rename(repository.Head.CanonicalName, "refs/heads/main");
        repository.ApplyTag("v1.0.0");

        File.WriteAllText(
            Path.Combine(path, "SKILL.md"),
            "---\nname: version-two\ndescription: Current skill.\n---\nUse version two.\n");
        Commands.Stage(repository, "SKILL.md");
        repository.Commit("Update skill", signature, signature);
        return path;
    }

    private string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aw-skill-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _tempDirs)
        {
            try { Directory.Delete(path, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private sealed class LocalRepositoryCloneInitializer(string localSourceRepository) : ProjectGitInitializer(
        NullLogger<ProjectGitInitializer>.Instance)
    {
        public GitClonePurpose? LastPurpose { get; private set; }
        public int CloneCount { get; private set; }

        public override string Clone(
            string workingDirectory,
            string sourceRepository,
            string accessToken,
            GitClonePurpose purpose)
        {
            CloneCount++;
            LastPurpose = purpose;
            var repositoryPath = Repository.Clone(localSourceRepository, workingDirectory);
            using var repository = new Repository(repositoryPath);
            return repository.Head.FriendlyName;
        }
    }

    private sealed class AllowAllProjectRoles : IProjectRoleAuthorizationService
    {
        public bool IsPlatformAdmin(CallerContext caller) => false;

        public Task<ProjectRole?> GetEffectiveRoleAsync(
            CallerContext caller,
            ProjectId projectId,
            CancellationToken ct = default) =>
            Task.FromResult<ProjectRole?>(ProjectRole.Owner);

        public Task<bool> HasRoleAsync(
            CallerContext caller,
            ProjectId projectId,
            ProjectRole minimumRole,
            CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyDictionary<ProjectId, ProjectRole>> ListExplicitRolesAsync(
            CallerContext caller,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<ProjectId, ProjectRole>>(
                new Dictionary<ProjectId, ProjectRole>());
    }
}
