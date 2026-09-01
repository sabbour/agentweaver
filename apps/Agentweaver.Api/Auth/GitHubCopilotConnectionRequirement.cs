using System.Text.Json.Serialization;
using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

/// <summary>
/// The single redacted response contract for a Copilot capability that must be connected by a
/// human. Its action deliberately identifies either an existing project-scoped handoff or the
/// platform-settings handoff rather than carrying a credential, OAuth URL, transaction, or
/// callback state.
/// </summary>
public sealed record GitHubCopilotConnectionRequirement(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("action")] GitHubCopilotConnectionAction Action)
{
    public const string RequirementCode = "github_copilot_connection_required";
    public const string RequirementMessage = ProjectRequirementMessage;
    public const string ProjectRequirementMessage =
        "Connect the project's GitHub Copilot App to continue.";
    public const string PlatformDefaultRequirementMessage =
        "Connect the platform-default GitHub Copilot account to continue.";

    public static GitHubCopilotConnectionRequirement ForProject(ProjectId projectId) =>
        new(
            RequirementCode,
            ProjectRequirementMessage,
            new GitHubCopilotConnectionAction(
                GitHubCopilotConnectionAction.ConnectProjectCopilotApp,
                projectId.ToString()));

    public static GitHubCopilotConnectionRequirement ForPlatformDefault() =>
        new(
            RequirementCode,
            PlatformDefaultRequirementMessage,
            new GitHubCopilotConnectionAction(
                GitHubCopilotConnectionAction.ConnectProjectCopilotApp,
                string.Empty));
}

/// <summary>
/// Raised before a run can enter an AgentHost launch path when its project-scoped or platform-wide
/// Copilot capability is not redeemable. Endpoint surfaces translate this to the single redacted
/// connection-required response contract.
/// </summary>
public sealed class GitHubCopilotConnectionRequiredException : Exception
{
    public GitHubCopilotConnectionRequiredException(ProjectId projectId)
        : this(GitHubCopilotConnectionRequirement.ForProject(projectId))
    {
    }

    public GitHubCopilotConnectionRequiredException()
        : this(GitHubCopilotConnectionRequirement.ForPlatformDefault())
    {
    }

    private GitHubCopilotConnectionRequiredException(GitHubCopilotConnectionRequirement requirement)
        : base(requirement.Message)
    {
        Requirement = requirement;
    }

    public GitHubCopilotConnectionRequirement Requirement { get; }
}

/// <summary>
/// Typed action consumed by every UI surface that receives a
/// <see cref="GitHubCopilotConnectionRequirement"/>. The client starts the established
/// project Copilot App authorization endpoint when a project id is present; otherwise the UI
/// routes to platform settings so a Platform Admin can connect the deployment-wide account.
/// </summary>
public sealed record GitHubCopilotConnectionAction(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("project_id")] string ProjectId)
{
    public const string ConnectProjectCopilotApp = "connect_project_copilot_app";
}
