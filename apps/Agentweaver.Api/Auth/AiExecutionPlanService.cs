using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Microsoft.AspNetCore.WebUtilities;

namespace Agentweaver.Api.Auth;

public static class AiExecutionPlanHeaders
{
    public const string ProviderKey = "If-Model-Provider-Key";
}

public sealed record AiOperationDefinition(
    string Name,
    AiResolutionMode ResolutionMode,
    ProjectRole? MinimumProjectRole,
    bool RequiresPlatformRole,
    bool SupportsByok,
    ProjectModelProviderCapabilityPurpose? CapabilityPurpose = null,
    bool AllowsBroker = false);

public enum AiResolutionMode
{
    RequiredProject,
    OptionalProject,
    Platform,
    User,
}

public static class AiOperationCatalog
{
    private static readonly IReadOnlyDictionary<string, AiOperationDefinition> Operations =
        new Dictionary<string, AiOperationDefinition>(StringComparer.Ordinal)
        {
            ["orchestration"] = Project("orchestration", ProjectRole.Contributor, supportsByok: true),
            ["blueprint_generation"] = new(
                "blueprint_generation",
                AiResolutionMode.OptionalProject,
                ProjectRole.Owner,
                RequiresPlatformRole: true,
                SupportsByok: true,
                ProjectModelProviderCapabilityPurpose.BlueprintGeneration,
                AllowsBroker: true),
            ["workflow_generation"] = Project(
                "workflow_generation",
                ProjectRole.Owner,
                supportsByok: true,
                ProjectModelProviderCapabilityPurpose.WorkflowGeneration),
            ["skill_generation"] = Project(
                "skill_generation",
                ProjectRole.Contributor,
                supportsByok: true,
                ProjectModelProviderCapabilityPurpose.SkillGeneration),
            ["casting_generation"] = Project(
                "casting_generation",
                ProjectRole.Contributor,
                supportsByok: true,
                ProjectModelProviderCapabilityPurpose.CastingGeneration),
            ["backlog_decomposition"] = Project(
                "backlog_decomposition",
                ProjectRole.Contributor,
                supportsByok: true,
                ProjectModelProviderCapabilityPurpose.BacklogDecomposition),
            ["marketplace_catalog_classification"] = Project(
                "marketplace_catalog_classification",
                ProjectRole.Viewer,
                supportsByok: true,
                ProjectModelProviderCapabilityPurpose.MarketplaceCatalogClassification),
            ["outcome_spec_generation"] = Project("outcome_spec_generation", ProjectRole.Contributor, supportsByok: true),
            ["workflow_selection"] = Project("workflow_selection", ProjectRole.Contributor, supportsByok: false),
            ["story_independence_classification"] = Project("story_independence_classification", ProjectRole.Contributor, supportsByok: false),
            ["assembly_gate_classification"] = Project("assembly_gate_classification", ProjectRole.Contributor, supportsByok: false),
            ["preview_classification"] = Project("preview_classification", ProjectRole.Contributor, supportsByok: true),
            ["preview_command_generation"] = Project("preview_command_generation", ProjectRole.Contributor, supportsByok: true),
            ["agent_turn"] = Project("agent_turn", ProjectRole.Contributor, supportsByok: true),
            ["rai"] = Project("rai", ProjectRole.Contributor, supportsByok: false),
            ["rubberduck"] = Project("rubberduck", ProjectRole.Contributor, supportsByok: false),
            ["build_test"] = Project("build_test", ProjectRole.Contributor, supportsByok: false),
            ["scribe"] = Project("scribe", ProjectRole.Contributor, supportsByok: false),
            ["assistant_turn"] = new(
                "assistant_turn",
                AiResolutionMode.Platform,
                null,
                RequiresPlatformRole: true,
                SupportsByok: true),
            ["user_session"] = new(
                "user_session",
                AiResolutionMode.User,
                null,
                RequiresPlatformRole: false,
                SupportsByok: true),
        };

    public static IReadOnlyList<string> Names { get; } = Operations.Keys.ToArray();

    public static bool TryGet(string? operation, out AiOperationDefinition definition) =>
        Operations.TryGetValue(operation?.Trim().ToLowerInvariant() ?? string.Empty, out definition!);

    private static AiOperationDefinition Project(
        string name,
        ProjectRole minimumRole,
        bool supportsByok,
        ProjectModelProviderCapabilityPurpose? capabilityPurpose = null) =>
        new(
            name,
            AiResolutionMode.RequiredProject,
            minimumRole,
            RequiresPlatformRole: false,
            SupportsByok: supportsByok,
            capabilityPurpose);
}

public sealed record AiExecutionPlan(
    string Operation,
    ProjectId? ProjectId,
    string Subject,
    string ResolutionScope,
    EffectiveModelProviderResult Provider,
    string? ProviderKey,
    DateTimeOffset ExpiresAt);

public sealed class AiExecutionPlanAccessor
{
    private readonly AsyncLocal<ScopeState?> _current = new();

    public AiExecutionPlan? Current => _current.Value is { IsActive: true } state ? state.Plan : null;
    public ByokProviderConfiguration? FrozenByokConfiguration =>
        _current.Value is { IsActive: true } state ? state.ByokProviderConfiguration : null;

    public IDisposable Push(AiExecutionPlan plan)
    {
        var previous = _current.Value;
        var state = new ScopeState(plan);
        _current.Value = state;
        return new Scope(() =>
        {
            state.Deactivate();
            if (ReferenceEquals(_current.Value, state))
                _current.Value = previous;
        });
    }

    public void FreezeByokConfiguration(ByokProviderConfiguration configuration)
    {
        var current = _current.Value is { IsActive: true } state
            ? state
            : throw new InvalidOperationException("There is no accepted AI execution plan to freeze.");
        current.Freeze(configuration);
    }

    private sealed class ScopeState(AiExecutionPlan plan)
    {
        private readonly object _gate = new();
        private int _active = 1;

        public AiExecutionPlan Plan { get; } = plan;
        public bool IsActive => Volatile.Read(ref _active) == 1;
        public ByokProviderConfiguration? ByokProviderConfiguration { get; private set; }

        public void Deactivate() => Interlocked.Exchange(ref _active, 0);

        public void Freeze(ByokProviderConfiguration configuration)
        {
            lock (_gate)
            {
                if (ByokProviderConfiguration is null)
                {
                    ByokProviderConfiguration = configuration;
                    return;
                }
                if (!string.Equals(
                        ByokProviderConfiguration.ExecutionFingerprint(),
                        configuration.ExecutionFingerprint(),
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The accepted BYOK provider configuration was already frozen to a different value.");
                }
            }
        }
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

public sealed class AiExecutionPlanException(
    string errorCode,
    AiExecutionContextResponse replacementContext,
    string message,
    int statusCode = StatusCodes.Status409Conflict) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
    public AiExecutionContextResponse ReplacementContext { get; } = replacementContext;
    public int StatusCode { get; } = statusCode;
}

public sealed class AiExecutionPlanService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(5);
    private readonly EffectiveModelProviderResolver _resolver;
    private readonly TimeProvider _timeProvider;
    private readonly byte[] _signingKey;

    public AiExecutionPlanService(
        EffectiveModelProviderResolver resolver,
        IConfiguration configuration,
        IHostEnvironment environment)
        : this(
            resolver,
            configuration,
            TimeProvider.System,
            allowDevelopmentApiKeyFallback:
                environment.IsDevelopment() || environment.IsEnvironment("Test") || environment.IsEnvironment("Testing"))
    {
    }

    internal AiExecutionPlanService(
        EffectiveModelProviderResolver resolver,
        IConfiguration configuration,
        TimeProvider? timeProvider = null)
        : this(resolver, configuration, timeProvider ?? TimeProvider.System, allowDevelopmentApiKeyFallback: true)
    {
    }

    private AiExecutionPlanService(
        EffectiveModelProviderResolver resolver,
        IConfiguration configuration,
        TimeProvider timeProvider,
        bool allowDevelopmentApiKeyFallback)
    {
        _resolver = resolver;
        _timeProvider = timeProvider;
        var configuredKey = configuration["AiExecution:ProviderKeySigningKey"];
        if (string.IsNullOrWhiteSpace(configuredKey))
            configuredKey = configuration["Auth:CopilotApp:ClientSecret"];
        if (string.IsNullOrWhiteSpace(configuredKey))
            configuredKey = configuration["Auth:RepoApp:ClientSecret"];
        if (string.IsNullOrWhiteSpace(configuredKey) && allowDevelopmentApiKeyFallback)
            configuredKey = configuration["Auth:ApiKey"];
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            throw new InvalidOperationException(
                "AiExecution:ProviderKeySigningKey or an existing server-only GitHub App client secret is required.");
        }
        _signingKey = SHA256.HashData(Encoding.UTF8.GetBytes("agentweaver:ai-execution:v2:" + configuredKey));
    }

    public async Task<AiExecutionPlan> PrepareAsync(
        AiOperationDefinition operation,
        ProjectId? projectId,
        CallerContext caller,
        CancellationToken ct)
    {
        if (operation.ResolutionMode == AiResolutionMode.User
            && string.IsNullOrWhiteSpace(caller.EntraObjectId))
        {
            throw new InvalidOperationException(
                "User-scoped AI execution requires an explicit Entra object identity.");
        }
        var (provider, resolutionScope) = await ResolveForOperationAsync(
            operation, projectId, caller, ct).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var expiresAt = now.Add(PlanLifetime);
        var payload = new ProviderKeyPayload(
            Version: 2,
            Operation: operation.Name,
            ProjectId: projectId?.ToString(),
            Subject: Subject(caller),
            ProviderIdentity: IdentityDigest(provider.ProviderIdentity),
            ExpiresAtUnixSeconds: expiresAt.ToUnixTimeSeconds(),
            Queued: false);
        var providerKey = provider is EffectiveModelProviderResult.Unavailable
            ? null
            : Sign(payload);
        return new AiExecutionPlan(
            operation.Name,
            projectId,
            payload.Subject,
            resolutionScope,
            provider,
            providerKey,
            expiresAt);
    }

    public async Task<AiExecutionPlan> AcceptAsync(
        string? providerKey,
        AiOperationDefinition operation,
        ProjectId? projectId,
        CallerContext caller,
        CancellationToken ct)
    {
        ProviderKeyPayload? expected = null;
        var invalid = false;
        var missing = string.IsNullOrWhiteSpace(providerKey);
        if (missing)
        {
            invalid = true;
        }
        else
        {
            try
            {
                expected = Verify(providerKey!);
            }
            catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException)
            {
                invalid = true;
            }
        }

        var replacement = await PrepareAsync(operation, projectId, caller, ct).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var expired = expected is not null
            && expected.ExpiresAtUnixSeconds <= now.ToUnixTimeSeconds();
        if (invalid
            || expected is null
            || expected.Version is not (1 or 2)
            || expected.Queued
            || expired
            || !string.Equals(expected.Operation, operation.Name, StringComparison.Ordinal)
            || !string.Equals(expected.ProjectId, projectId?.ToString(), StringComparison.Ordinal)
            || !string.Equals(expected.Subject, Subject(caller), StringComparison.Ordinal)
            || !string.Equals(
                expected.ProviderIdentity,
                IdentityDigest(replacement.Provider.ProviderIdentity),
                StringComparison.Ordinal))
        {
            throw new AiExecutionPlanException(
                missing
                    ? "ai_execution_context_required"
                    : expired
                        ? "ai_execution_context_expired"
                        : "model_provider_changed",
                ToResponse(replacement, "prepared"),
                missing
                    ? "Prepare the AI execution context before starting this operation."
                    : expired
                        ? "The prepared AI execution context expired. Review the provider and retry."
                        : "The effective model provider changed. Review the replacement context and retry.");
        }

        if (replacement.Provider is EffectiveModelProviderResult.Unavailable)
        {
            throw new AiExecutionPlanException(
                "model_provider_unavailable",
                ToResponse(replacement, "prepared"),
                "The effective model provider is unavailable for this operation.");
        }

        return replacement with { ProviderKey = providerKey! };
    }

    public AiExecutionContextResponse ToResponse(AiExecutionPlan plan, string phase) =>
        new()
        {
            AiRequired = true,
            Operation = plan.Operation,
            Phase = phase,
            ExecutionKey = plan.ProviderKey,
            ExpiresAt = plan.ProviderKey is null ? null : plan.ExpiresAt,
            EffectiveModelProvider = plan.Provider.ToContract(plan.ResolutionScope),
        };

    public string CreateQueuedProviderKey(AiExecutionPlan plan)
    {
        return Sign(new ProviderKeyPayload(
            Version: 2,
            Operation: plan.Operation,
            ProjectId: plan.ProjectId?.ToString(),
            Subject: plan.Subject,
            ProviderIdentity: IdentityDigest(plan.Provider.ProviderIdentity),
            ExpiresAtUnixSeconds: 0,
            Queued: true));
    }

    public async Task<AiExecutionPlan> RevalidateAcceptedAsync(
        AiExecutionPlan plan,
        CancellationToken ct)
    {
        if (!AiOperationCatalog.TryGet(plan.Operation, out var operation))
            throw new InvalidOperationException($"Unknown accepted AI operation '{plan.Operation}'.");
        var caller = new CallerContext
        {
            User = plan.Subject,
            EntraObjectId = operation.ResolutionMode == AiResolutionMode.User ? plan.Subject : null,
        };
        var replacement = await PrepareAsync(operation, plan.ProjectId, caller, ct).ConfigureAwait(false);
        if (!string.Equals(
                replacement.Provider.ProviderIdentity,
                plan.Provider.ProviderIdentity,
                StringComparison.Ordinal))
        {
            throw new AiExecutionPlanException(
                "model_provider_changed",
                ToResponse(replacement, "prepared"),
                "The effective model provider changed before model invocation.");
        }
        if (replacement.Provider is EffectiveModelProviderResult.Unavailable)
        {
            throw new AiExecutionPlanException(
                "model_provider_unavailable",
                ToResponse(replacement, "prepared"),
                "The effective model provider is unavailable for this operation.");
        }
        return plan;
    }

    public async Task<AiExecutionPlan> RestoreAcceptedAsync(
        string providerKey,
        AiOperationDefinition operation,
        ProjectId? projectId,
        string subject,
        CancellationToken ct)
    {
        ProviderKeyPayload expected;
        try
        {
            expected = Verify(providerKey);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException)
        {
            throw new InvalidOperationException("The persisted AI execution plan is invalid.", ex);
        }

        var validQueuedToken = expected.Version == 2 && expected.Queued;
        var validLegacyQueuedToken = expected.Version == 1
            && expected.ExpiresAtUnixSeconds > _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if ((!validQueuedToken && !validLegacyQueuedToken)
            || !string.Equals(expected.Operation, operation.Name, StringComparison.Ordinal)
            || !string.Equals(expected.ProjectId, projectId?.ToString(), StringComparison.Ordinal)
            || !string.Equals(expected.Subject, subject, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The persisted AI execution plan does not match the queued operation.");
        }

        var caller = new CallerContext { User = subject };
        var replacement = await PrepareAsync(operation, projectId, caller, ct).ConfigureAwait(false);
        if (!string.Equals(
                expected.ProviderIdentity,
                IdentityDigest(replacement.Provider.ProviderIdentity),
                StringComparison.Ordinal)
            || replacement.Provider is EffectiveModelProviderResult.Unavailable)
        {
            throw new AiExecutionPlanException(
                "model_provider_changed",
                ToResponse(replacement, "prepared"),
                "The effective model provider changed before queued model execution.");
        }

        return replacement with { ProviderKey = providerKey };
    }

    public async Task<AiExecutionPlanException> ChangedAsync(
        AiExecutionPlan plan,
        CancellationToken ct)
    {
        if (!AiOperationCatalog.TryGet(plan.Operation, out var operation))
            throw new InvalidOperationException($"Unknown accepted AI operation '{plan.Operation}'.");
        var caller = new CallerContext
        {
            User = plan.Subject,
            EntraObjectId = operation.ResolutionMode == AiResolutionMode.User ? plan.Subject : null,
        };
        var replacement = await PrepareAsync(operation, plan.ProjectId, caller, ct).ConfigureAwait(false);
        return new AiExecutionPlanException(
            "model_provider_changed",
            ToResponse(replacement, "prepared"),
            "The effective model provider changed before model invocation.");
    }

    private async Task<(EffectiveModelProviderResult Provider, string ResolutionScope)> ResolveForOperationAsync(
        AiOperationDefinition operation,
        ProjectId? projectId,
        CallerContext caller,
        CancellationToken ct)
    {
        EffectiveModelProviderResult resolved;
        string resolutionScope;
        if (operation.ResolutionMode == AiResolutionMode.User)
        {
            resolved = await _resolver.ResolveForSessionAsync(
                caller.EntraObjectId!,
                ct).ConfigureAwait(false);
            resolutionScope = EffectiveModelProviderProvenance.ScopeUser;
        }
        else
        {
            var resolutionProjectId = operation.ResolutionMode is
                AiResolutionMode.RequiredProject or AiResolutionMode.OptionalProject
                ? projectId
                : null;
            resolved = await _resolver.ResolveAsync(resolutionProjectId, ct).ConfigureAwait(false);
            resolutionScope = resolutionProjectId is null
                ? EffectiveModelProviderProvenance.ScopePlatform
                : EffectiveModelProviderProvenance.ScopeProject;
        }

        if (!operation.SupportsByok && resolved is EffectiveModelProviderResult.Byok)
        {
            resolved = new EffectiveModelProviderResult.Unavailable(
                EffectiveModelProviderUnavailableReason.OperationRequiresGitHubCopilot,
                $"{operation.Name} currently requires GitHub Copilot.");
        }

        return (resolved, resolutionScope);
    }

    private string Sign(ProviderKeyPayload payload)
    {
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[payloadBytes.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_signingKey, tag.Length);
        aes.Encrypt(nonce, payloadBytes, ciphertext, tag);

        var token = new byte[nonce.Length + ciphertext.Length + tag.Length];
        nonce.CopyTo(token, 0);
        ciphertext.CopyTo(token, nonce.Length);
        tag.CopyTo(token, nonce.Length + ciphertext.Length);
        return WebEncoders.Base64UrlEncode(token);
    }

    private static string IdentityDigest(string providerIdentity) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(providerIdentity))).ToLowerInvariant();

    private ProviderKeyPayload Verify(string token)
    {
        var tokenBytes = WebEncoders.Base64UrlDecode(token);
        if (tokenBytes.Length <= 28)
            throw new FormatException("Invalid provider key.");
        var nonce = tokenBytes.AsSpan(0, 12);
        var ciphertext = tokenBytes.AsSpan(12, tokenBytes.Length - 28);
        var tag = tokenBytes.AsSpan(tokenBytes.Length - 16, 16);
        var payload = new byte[ciphertext.Length];
        using var aes = new AesGcm(_signingKey, tag.Length);
        aes.Decrypt(nonce, ciphertext, tag, payload);
        return JsonSerializer.Deserialize<ProviderKeyPayload>(payload, JsonOptions)
            ?? throw new JsonException("Invalid provider key payload.");
    }

    private static string Subject(CallerContext caller) =>
        caller.EntraObjectId ?? caller.User;

    private sealed record ProviderKeyPayload(
        int Version,
        string Operation,
        string? ProjectId,
        string Subject,
        string ProviderIdentity,
        long ExpiresAtUnixSeconds,
        bool Queued = false);
}
