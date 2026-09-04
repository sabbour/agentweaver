using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Projects an <see cref="EffectiveModelProviderResult"/> onto the run-level bookkeeping every
/// consumer needs: the durable <see cref="ModelSource"/> stamped on the <see cref="Run"/> row, the
/// <c>run.model_provider_resolved</c> provenance payload, and the correctly SCOPED
/// <see cref="ModelProviderConnectionRequiredException"/>.
///
/// <para>
/// Before this existed, every run insert site hardcoded <see cref="ModelSource.GitHubCopilot"/> even
/// though the resolver had already decided otherwise, so a BYOK run's persisted row (and the UI that
/// reads it) always claimed "GitHub Copilot". The connection-required error had the mirror-image
/// defect: it picked "project" vs "platform" wording by testing whether a project id STRING PARSED,
/// so a platform-scoped authorization failure told the user to reconnect the project's Copilot App.
/// Both now derive from the resolver's ACTUAL returned case, in one place.
/// </para>
/// </summary>
public static class EffectiveModelProviderProvenance
{
    /// <summary>Provider kinds surfaced on the <c>run.model_provider_resolved</c> event.</summary>
    public const string KindByok = "byok";
    public const string KindProjectGitHubCopilot = "project_github_copilot";
    public const string KindPlatformGitHubCopilot = "platform_github_copilot";
    public const string KindUserGitHubCopilot = "user_github_copilot";
    public const string KindUserByok = "user_byok";
    public const string KindUnavailable = "unavailable";

    /// <summary>
    /// The durable <see cref="Run.ModelSource"/> for <paramref name="result"/>. Only the BYOK case is
    /// a non-Copilot source; an unresolvable provider keeps the Copilot default so a run that fails
    /// closed still reports the provider family it was fenced against.
    /// </summary>
    public static ModelSource ToModelSource(this EffectiveModelProviderResult result) =>
        result is EffectiveModelProviderResult.Byok or EffectiveModelProviderResult.UserByok
            ? ModelSource.Byok
            : ModelSource.GitHubCopilot;

    /// <summary>The stable provider-kind discriminator for <paramref name="result"/>.</summary>
    public static string ProviderKind(this EffectiveModelProviderResult result) => result switch
    {
        EffectiveModelProviderResult.Byok => KindByok,
        EffectiveModelProviderResult.UserByok => KindUserByok,
        EffectiveModelProviderResult.ProjectGitHubCopilot => KindProjectGitHubCopilot,
        EffectiveModelProviderResult.PlatformGitHubCopilot => KindPlatformGitHubCopilot,
        EffectiveModelProviderResult.UserGitHubCopilot => KindUserGitHubCopilot,
        _ => KindUnavailable,
    };

    /// <summary>
    /// The BYOK provider id or the GitHub Copilot binding id backing <paramref name="result"/> —
    /// the identifier needed to reconstruct which configured provider/binding actually ran.
    /// </summary>
    public static string? ProviderId(this EffectiveModelProviderResult result) => result switch
    {
        EffectiveModelProviderResult.Byok byok => byok.ProviderId,
        EffectiveModelProviderResult.UserByok byok => byok.ProviderId,
        EffectiveModelProviderResult.ProjectGitHubCopilot project => project.BindingId,
        EffectiveModelProviderResult.PlatformGitHubCopilot platform => platform.BindingId,
        EffectiveModelProviderResult.UserGitHubCopilot user => user.BindingId,
        _ => null,
    };

    /// <summary>The BYOK provider type (<c>openai</c>/<c>azure</c>/<c>anthropic</c>), else null.</summary>
    public static string? ProviderType(this EffectiveModelProviderResult result) =>
        result switch
        {
            EffectiveModelProviderResult.Byok byok => byok.ProviderType,
            EffectiveModelProviderResult.UserByok byok => byok.ProviderType,
            _ => null,
        };

    /// <summary>The GitHub account login backing a Copilot binding, else null.</summary>
    public static string? GitHubLogin(this EffectiveModelProviderResult result) => result switch
    {
        EffectiveModelProviderResult.ProjectGitHubCopilot project => project.GitHubLogin,
        EffectiveModelProviderResult.PlatformGitHubCopilot platform => platform.GitHubLogin,
        EffectiveModelProviderResult.UserGitHubCopilot user => user.GitHubLogin,
        _ => null,
    };

    /// <summary>
    /// Builds the <see cref="EventTypes.RunModelProviderResolved"/> payload. Carries the provider
    /// kind, the provider/binding id, the GitHub login (Copilot bindings only), the durable model
    /// source, and the model id actually in effect, so a completed run's provenance can be
    /// reconstructed from its event stream alone.
    /// </summary>
    public static object ToProvenancePayload(
        this EffectiveModelProviderResult result,
        string runId,
        string? modelId) =>
        new
        {
            runId,
            providerKind = result.ProviderKind(),
            providerId = result.ProviderId(),
            providerType = result.ProviderType(),
            githubLogin = result.GitHubLogin(),
            modelSource = result.ToModelSource().ToApiString(),
            modelId,
            unavailableReason = result is EffectiveModelProviderResult.Unavailable unavailable
                ? unavailable.UnavailableReason.ToString()
                : null,
            timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
        };

    /// <summary>
    /// Builds the connection-required failure whose scope matches the binding the resolver ACTUALLY
    /// selected: a platform-default Copilot binding routes the human to Platform Settings, a
    /// project binding routes them to the project's model-provider settings.
    ///
    /// <para>
    /// <paramref name="result"/> may be <see langword="null"/> when no resolver is wired (unit-test
    /// executors); that preserves the legacy project-scoped behaviour keyed off
    /// <paramref name="projectId"/>.
    /// </para>
    /// </summary>
    public static ModelProviderConnectionRequiredException ToConnectionRequiredException(
        this EffectiveModelProviderResult? result,
        ProjectId? projectId)
    {
        var projectScoped = result switch
        {
            EffectiveModelProviderResult.PlatformGitHubCopilot => false,
            EffectiveModelProviderResult.Unavailable unavailable =>
                unavailable.UnavailableReason != EffectiveModelProviderUnavailableReason.NoProvider
                || projectId is not null,
            // ProjectGitHubCopilot, Byok, and "no resolver wired" all keep the project handoff when a
            // project id is known.
            _ => true,
        };

        return projectScoped && projectId is { } project
            ? new ModelProviderConnectionRequiredException(project)
            : new ModelProviderConnectionRequiredException();
    }
}
