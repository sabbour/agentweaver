using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Skills;

internal static class SkillTestProject
{
    public static Task InsertAsync(SqliteDb db, ProjectId id, string workingDirectory)
    {
        var now = DateTimeOffset.UtcNow;
        return new SqliteProjectStore(db).InsertAsync(new Project
        {
            Id = id,
            Name = "Skill test project",
            Origin = ProjectOrigin.Blank(),
            WorkingDirectory = workingDirectory,
            DefaultBranch = "main",
            Owner = "skill-test",
            ProviderSettings = new ProjectProviderSettings
            {
                DefaultProvider = ModelSource.GitHubCopilot,
            },
            State = ProjectState.Active,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }
}
