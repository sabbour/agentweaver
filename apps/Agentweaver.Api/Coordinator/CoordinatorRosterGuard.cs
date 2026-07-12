using Agentweaver.Squad.Model;
using Agentweaver.Squad.Squad;

namespace Agentweaver.Api.Coordinator;

public sealed class NoTeamException(string repositoryPath, Exception? innerException = null)
    : InvalidOperationException(NoTeamException.DefaultMessage, innerException)
{
    public const string ErrorCode = "no_team";
    public const string DefaultMessage = "This project has no team. Cast a team before starting an orchestration.";

    public string RepositoryPath { get; } = repositoryPath;
}

public sealed class InvalidTeamException(string repositoryPath, Exception innerException)
    : InvalidOperationException(InvalidTeamException.DefaultMessage, innerException)
{
    public const string ErrorCode = "invalid_team";
    public const string DefaultMessage = "The project team roster could not be read. Fix the team before starting an orchestration.";

    public string RepositoryPath { get; } = repositoryPath;
}

/// <summary>
/// Shared coordinator start/decompose guard for the dispatchable project team roster. It intentionally
/// reads the same source as the orchestrator assignment path: <see cref="SquadReader.ReadTeam"/>.
/// </summary>
public static class CoordinatorRosterGuard
{
    public static bool HasDispatchableTeam(string repositoryPath)
    {
        var reader = new SquadReader(repositoryPath);
        var team = reader.ReadTeam();
        return team?.Members.Any(IsDispatchableMember) == true;
    }

    public static void EnsureDispatchableTeam(string repositoryPath)
    {
        try
        {
            if (!HasDispatchableTeam(repositoryPath))
                throw new NoTeamException(repositoryPath);
        }
        catch (NoTeamException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidTeamException(repositoryPath, ex);
        }
    }

    public static bool IsDispatchableMember(CastMember member) =>
        member.Status == CastMemberStatus.Active
        && member.Role is not null
        && CoordinatorOrchestratorExecutor.IsDispatchable(member.Name, member.Role.Id, member.Role.Title);
}
