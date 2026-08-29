using System.Text.Json.Serialization;
using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

/// <summary>
/// The single redacted response contract for a Copilot capability that must be connected by a
/// human. Its action deliberately identifies an existing project-scoped handoff rather than
/// carrying a credential, OAuth URL, transaction, or callback state.
/// </summary>
public sealed record GitHubCopilotConnectionRequirement(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("action")] GitHubCopilotConnectionAction Action)
{
    public const string RequirementCode = "github_copilot_connection_required";
    public const string RequirementMessage =
        "Connect the project's GitHub Copilot App to continue.";

    public static GitHubCopilotConnectionRequirement ForProject(ProjectId projectId) =>
        new(
            RequirementCode,
            RequirementMessage,
            new GitHubCopilotConnectionAction(
                GitHubCopilotConnectionAction.ConnectProjectCopilotApp,
                projectId.ToString()));
}

/// <summary>
/// Typed action consumed by every UI surface that receives a
/// <see cref="GitHubCopilotConnectionRequirement"/>. The client starts the established
/// project Copilot App authorization endpoint, which creates the one-time browser handoff.
/// </summary>
public sealed record GitHubCopilotConnectionAction(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("project_id")] string ProjectId)
{
    public const string ConnectProjectCopilotApp = "connect_project_copilot_app";
}
