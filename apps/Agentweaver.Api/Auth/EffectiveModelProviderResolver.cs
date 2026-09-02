using System.Text.Json;
using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

/// <summary>
/// The single source of truth for "which model provider is actually in effect right now" — the
/// shared replacement for the nine bespoke precedence implementations that used to exist across
/// endpoints, run startup, the Assistant, backlog decomposition, marketplace classification, and
/// the non-run generators. Every consumer that needs to know whether AI should run against BYOK,
/// a project's own GitHub Copilot binding, or the platform-default GitHub Copilot binding must go
/// through <see cref="ResolveAsync"/> rather than re-deriving the precedence itself.
/// </summary>
/// <remarks>
/// Precedence rule (the one rule, replacing the nine):
/// <list type="bullet">
///   <item>
///     Project scope (<c>projectId</c> supplied): an explicit project-scoped model-provider
///     override — today, only a project GitHub Copilot binding — always wins when active.
///     Otherwise the project inherits the platform default (BYOK first, then platform Copilot).
///   </item>
///   <item>
///     Platform/non-project scope (<c>projectId</c> is <see langword="null"/>): the
///     deployment-wide active BYOK provider wins when configured; otherwise the platform-default
///     GitHub Copilot binding is used.
///   </item>
/// </list>
/// </remarks>
public sealed class EffectiveModelProviderResolver(
    GitHubConnectionsPersistenceStore persistence,
    ByokProviderConfigurationService byokSettings,
    ISecretStore secretStore)
{
    public async Task<EffectiveModelProviderResult> ResolveAsync(ProjectId? projectId, CancellationToken ct)
    {
        if (projectId is { } project)
        {
            var projectBinding = await persistence.GetActiveProjectCopilotBindingAsync(project.ToString(), ct)
                .ConfigureAwait(false);
            if (projectBinding is not null)
            {
                var login = await TryReadGitHubLoginAsync(projectBinding.CredentialReference, ct).ConfigureAwait(false);
                return new EffectiveModelProviderResult.ProjectGitHubCopilot(projectBinding.Id, login);
            }
        }

        var byok = await byokSettings.GetAsync(ct).ConfigureAwait(false);
        if (byok is not null)
            return new EffectiveModelProviderResult.Byok(byok.Id, byok.Type);

        var platformBinding = await persistence.GetActivePlatformDefaultCopilotBindingAsync(ct).ConfigureAwait(false);
        if (platformBinding is not null)
        {
            var usable = await IsUsableCopilotSecretAsync(platformBinding.CredentialReference, ct).ConfigureAwait(false);
            if (usable)
            {
                var login = await TryReadGitHubLoginAsync(platformBinding.CredentialReference, ct).ConfigureAwait(false);
                return new EffectiveModelProviderResult.PlatformGitHubCopilot(platformBinding.Id, login);
            }
        }

        return new EffectiveModelProviderResult.Unavailable(
            projectId is null
                ? "No deployment-wide BYOK provider or platform-default GitHub Copilot binding is configured."
                : "The project has no GitHub Copilot binding, and no BYOK provider or platform-default GitHub Copilot binding is configured.");
    }

    /// <summary>
    /// Case-insensitive so this reads both the PascalCase JSON produced by
    /// <c>PlatformDefaultCopilotBindingService</c>/<c>ProjectCopilotBindingService</c> (they serialize
    /// a C# record's actual property names) and any lowercase-keyed secret written by other paths.
    /// </summary>
    private static readonly JsonSerializerOptions CredentialJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private async Task<CopilotCredentialSnapshot?> ReadCredentialAsync(string credentialReference, CancellationToken ct)
    {
        var secret = await secretStore.GetSecretAsync(credentialReference, ct).ConfigureAwait(false);
        if (!secret.Found || string.IsNullOrWhiteSpace(secret.Value))
            return null;
        try
        {
            return JsonSerializer.Deserialize<CopilotCredentialSnapshot>(secret.Value, CredentialJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<bool> IsUsableCopilotSecretAsync(string credentialReference, CancellationToken ct)
    {
        var credential = await ReadCredentialAsync(credentialReference, ct).ConfigureAwait(false);
        return credential is not null &&
               string.Equals(credential.Status, "signed-in", StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(credential.AccessToken);
    }

    private async Task<string?> TryReadGitHubLoginAsync(string credentialReference, CancellationToken ct)
    {
        var credential = await ReadCredentialAsync(credentialReference, ct).ConfigureAwait(false);
        return credential?.GitHubLogin;
    }

    private sealed record CopilotCredentialSnapshot(string? Status, string? AccessToken, string? GitHubLogin = null);
}

/// <summary>
/// The effective model provider for one resolution scope. Exactly one case is ever returned by
/// <see cref="EffectiveModelProviderResolver.ResolveAsync"/>.
/// </summary>
public abstract record EffectiveModelProviderResult
{
    private EffectiveModelProviderResult()
    {
    }

    /// <summary>The deployment-wide "bring your own key" provider is active.</summary>
    public sealed record Byok(string ProviderId, string ProviderType) : EffectiveModelProviderResult;

    /// <summary>The project's own GitHub Copilot binding overrides the platform default.</summary>
    public sealed record ProjectGitHubCopilot(string BindingId, string? GitHubLogin) : EffectiveModelProviderResult;

    /// <summary>The deployment-wide platform-default GitHub Copilot binding is in effect.</summary>
    public sealed record PlatformGitHubCopilot(string BindingId, string? GitHubLogin) : EffectiveModelProviderResult;

    /// <summary>No usable model provider is configured for this scope.</summary>
    public sealed record Unavailable(string Reason) : EffectiveModelProviderResult;
}
