using System.Collections.Immutable;

namespace Agentweaver.Projects.Config.AgentSuite;

internal static class AgentSuiteProjectTeamExtension
{
    public const string Name = "agentweaver.project-team.v1";

    public static string ContentPath(string agentId) => $"agentweaver/project-team/{agentId}.json";
}

internal sealed record AgentSuiteProjectTeamMetadata(
    string? CustomRoleTitle,
    ImmutableArray<AgentSuiteToolProviderRequirement> RemoteToolProviders);
