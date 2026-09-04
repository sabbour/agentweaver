using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Api.Contracts;
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
    public const string StateResolved = "resolved";
    public const string StateUnavailable = "unavailable";
    public const string ScopeProject = "project";
    public const string ScopePlatform = "platform";
    public const string ScopeUser = "user";
    public const string ScopeNone = "none";
    public const string ScopeUnknown = "unknown";

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

    /// <summary>The scope where the selected provider itself is configured.</summary>
    public static string ProviderScope(this EffectiveModelProviderResult result) => result switch
    {
        EffectiveModelProviderResult.Byok => ScopePlatform,
        EffectiveModelProviderResult.ProjectGitHubCopilot => ScopeProject,
        EffectiveModelProviderResult.PlatformGitHubCopilot => ScopePlatform,
        EffectiveModelProviderResult.UserByok => ScopeUser,
        EffectiveModelProviderResult.UserGitHubCopilot => ScopeUser,
        _ => ScopeNone,
    };

    /// <summary>
    /// Opaque stable fingerprint of provider identity. The UI may compare it but must never display
    /// it; raw provider/binding identifiers remain an internal provenance detail.
    /// </summary>
    public static string? ProviderKey(this EffectiveModelProviderResult result)
    {
        if (result is EffectiveModelProviderResult.Unavailable)
            return null;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(result.ProviderIdentity));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    public static EffectiveModelProviderDto ToContract(
        this EffectiveModelProviderResult result,
        string resolutionScope,
        string? modelId = null) =>
        new()
        {
            State = result is EffectiveModelProviderResult.Unavailable ? StateUnavailable : StateResolved,
            ProviderKind = result.ProviderKind(),
            ResolutionScope = resolutionScope,
            ProviderScope = result.ProviderScope(),
            ProviderType = result.ProviderType(),
            GitHubLogin = result.GitHubLogin(),
            ModelId = modelId,
            ProviderKey = result.ProviderKey(),
            UnavailableReason = result is EffectiveModelProviderResult.Unavailable unavailable
                ? ToUnavailableReason(unavailable.UnavailableReason)
                : null,
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
        string? modelId,
        string resolutionScope = ScopeUnknown) =>
        new
        {
            runId,
            state = result is EffectiveModelProviderResult.Unavailable ? StateUnavailable : StateResolved,
            providerKind = result.ProviderKind(),
            providerId = result.ProviderId(),
            providerType = result.ProviderType(),
            githubLogin = result.GitHubLogin(),
            modelSource = result.ToModelSource().ToApiString(),
            modelId,
            providerKey = result.ProviderKey(),
            resolutionScope,
            providerScope = result.ProviderScope(),
            unavailableReason = result is EffectiveModelProviderResult.Unavailable unavailable
                ? unavailable.UnavailableReason.ToString()
                : null,
            timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
        };

    public static EffectiveModelProviderDto? TryReadContract(object? payload)
    {
        if (payload is null)
            return null;

        JsonElement element;
        try
        {
            element = payload is JsonElement json
                ? json
                : JsonSerializer.SerializeToElement(payload);
        }
        catch (Exception ex) when (payload is not JsonElement
                                   && ex is JsonException or NotSupportedException)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var providerKind = ReadString(element, "providerKind", "provider_kind");
        if (string.IsNullOrWhiteSpace(providerKind))
            return null;

        var providerScope = ReadString(element, "providerScope", "provider_scope")
            ?? ProviderScopeFromKind(providerKind);
        var providerKey = ReadString(element, "providerKey", "provider_key");
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            var providerId = ReadString(element, "providerId", "provider_id");
            if (!string.IsNullOrWhiteSpace(providerId))
            {
                var identity = LegacyProviderIdentity(
                    providerKind,
                    providerId,
                    ReadString(element, "providerType", "provider_type"),
                    ReadString(element, "githubLogin", "github_login"));
                if (identity is not null)
                {
                    providerKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
                        .ToLowerInvariant();
                }
            }
        }

        return new EffectiveModelProviderDto
        {
            State = ReadString(element, "state")
                ?? (providerKind == KindUnavailable ? StateUnavailable : StateResolved),
            ProviderKind = providerKind,
            ResolutionScope = ReadString(element, "resolutionScope", "resolution_scope") ?? ScopeUnknown,
            ProviderScope = providerScope,
            ProviderType = ReadString(element, "providerType", "provider_type"),
            GitHubLogin = ReadString(element, "githubLogin", "github_login"),
            ModelId = ReadString(element, "modelId", "model_id"),
            ProviderKey = providerKey,
            UnavailableReason = NormalizeUnavailableReason(
                ReadString(element, "unavailableReason", "unavailable_reason")),
        };
    }

    private static string ProviderScopeFromKind(string providerKind) => providerKind switch
    {
        KindByok or KindPlatformGitHubCopilot => ScopePlatform,
        KindProjectGitHubCopilot => ScopeProject,
        KindUserByok or KindUserGitHubCopilot => ScopeUser,
        _ => ScopeNone,
    };

    private static string? LegacyProviderIdentity(
        string providerKind,
        string providerId,
        string? providerType,
        string? githubLogin) => providerKind switch
    {
        KindByok => $"byok:{providerType}:{providerId}",
        KindProjectGitHubCopilot => $"copilot-project:{providerId}:{githubLogin}",
        KindPlatformGitHubCopilot => $"copilot-platform:{providerId}:{githubLogin}",
        // Legacy user-scoped events did not carry the user id required by the canonical identity.
        KindUserByok or KindUserGitHubCopilot => null,
        _ => null,
    };

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString();
            }
        }
        return null;
    }

    private static string ToUnavailableReason(EffectiveModelProviderUnavailableReason reason) => reason switch
    {
        EffectiveModelProviderUnavailableReason.NoProvider => "no_provider",
        EffectiveModelProviderUnavailableReason.ProjectBindingRequiresReauthorization =>
            "project_binding_requires_reauthorization",
        EffectiveModelProviderUnavailableReason.UserProviderRequired => "user_provider_required",
        EffectiveModelProviderUnavailableReason.UserBindingRequiresReauthorization =>
            "user_binding_requires_reauthorization",
        _ => "unknown",
    };

    private static string? NormalizeUnavailableReason(string? reason) => reason switch
    {
        nameof(EffectiveModelProviderUnavailableReason.NoProvider) => "no_provider",
        nameof(EffectiveModelProviderUnavailableReason.ProjectBindingRequiresReauthorization) =>
            "project_binding_requires_reauthorization",
        nameof(EffectiveModelProviderUnavailableReason.UserProviderRequired) => "user_provider_required",
        nameof(EffectiveModelProviderUnavailableReason.UserBindingRequiresReauthorization) =>
            "user_binding_requires_reauthorization",
        _ => reason,
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
