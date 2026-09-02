using System.Text.Json.Serialization;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

/// <summary>
/// The single redacted response contract for a model-provider capability that must be connected
/// by a human. Its action deliberately identifies either an existing project-scoped handoff or
/// the platform-settings handoff rather than carrying a credential, OAuth URL, transaction, or
/// callback state.
/// </summary>
public sealed record ModelProviderConnectionRequirement(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("action")] ModelProviderConnectionAction Action)
{
    public const string RequirementCode = "model_provider_connection_required";
    public const string RequirementMessage = ProjectRequirementMessage;
    public const string ProjectRequirementMessage =
        "Connect the project's GitHub Copilot App to continue.";
    public const string PlatformDefaultRequirementMessage =
        "Connect the platform-default GitHub Copilot account to continue.";

    public static ModelProviderConnectionRequirement ForProject(ProjectId projectId) =>
        new(
            RequirementCode,
            ProjectRequirementMessage,
            new ModelProviderConnectionAction(
                ModelProviderConnectionAction.ConfigureProjectModelProvider,
                projectId.ToString()));

    public static ModelProviderConnectionRequirement ForPlatformDefault() =>
        new(
            RequirementCode,
            PlatformDefaultRequirementMessage,
            new ModelProviderConnectionAction(
                ModelProviderConnectionAction.ConfigurePlatformModelProvider,
                string.Empty));
}

/// <summary>
/// Raised before a run can enter an AgentHost launch path when its project-scoped or platform-wide
/// model-provider capability is not redeemable. Endpoint surfaces translate this to the single
/// redacted connection-required response contract.
/// </summary>
public sealed class ModelProviderConnectionRequiredException : AgentProviderException
{
    public ModelProviderConnectionRequiredException(ProjectId projectId)
        : this(ModelProviderConnectionRequirement.ForProject(projectId))
    {
    }

    public ModelProviderConnectionRequiredException()
        : this(ModelProviderConnectionRequirement.ForPlatformDefault())
    {
    }

    private ModelProviderConnectionRequiredException(ModelProviderConnectionRequirement requirement)
        : base(
            ModelSource.GitHubCopilot,
            AgentProviderFailureKind.Authorization,
            requirement.Code,
            requirement.Message,
            isRetryable: false)
    {
        Requirement = requirement;
    }

    public ModelProviderConnectionRequirement Requirement { get; }
}

/// <summary>
/// Typed action consumed by every UI surface that receives a
/// <see cref="ModelProviderConnectionRequirement"/>. The action type distinguishes project scope
/// from platform scope directly (rather than via an empty-string project id sentinel), so the
/// client can route a project-scoped requirement to the project's model-provider settings and a
/// platform-scoped requirement to Platform Settings so a Platform Admin can connect the
/// deployment-wide account.
/// </summary>
public sealed record ModelProviderConnectionAction(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("project_id")] string ProjectId)
{
    public const string ConfigureProjectModelProvider = "configure_project_model_provider";
    public const string ConfigurePlatformModelProvider = "configure_platform_model_provider";
}
