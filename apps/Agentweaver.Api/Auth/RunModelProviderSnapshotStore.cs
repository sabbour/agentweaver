using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Stores the accepted execution provider for a run outside public run events and API projections.
/// A BYOK snapshot includes the execution configuration because its secret material must survive a
/// later provider edit/removal while never being exposed from the run record.
/// </summary>
public sealed class RunModelProviderSnapshotStore(
    ISecretStore secrets,
    IServiceScopeFactory scopeFactory)
{
    private const int Version = 1;
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private sealed record Snapshot(
        int Version,
        string ProviderKind,
        string? ProviderId,
        string? ProviderType,
        string? CredentialVersion,
        ByokProviderConfiguration? ByokConfiguration);

    public sealed record Capture(
        ResolvedRunModelProviderBoundary Boundary,
        string? OwnedSecretReference);

    public async Task<ResolvedRunModelProviderBoundary?> TryGetAsync(Run run, CancellationToken ct)
    {
        var boundary = await TryGetAsync(run.Id, ct).ConfigureAwait(false);
        if (boundary is not null && boundary.Provider.ToModelSource() != run.ModelSource)
            throw SnapshotUnavailable();
        return boundary;
    }

    public async Task<ResolvedRunModelProviderBoundary?> TryGetAsync(RunId runId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var owner = await db.RunModelProviderSnapshotOwners.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RunId == runId.ToString(), ct).ConfigureAwait(false);
        if (owner is null)
            return null;

        if (!IsCandidateKey(owner.SecretReference, runId))
            throw SnapshotUnavailable();

        var stored = await secrets.GetSecretAsync(owner.SecretReference, ct).ConfigureAwait(false);
        if (!stored.Found || string.IsNullOrWhiteSpace(stored.Value))
            throw SnapshotUnavailable();

        try
        {
            var snapshot = JsonSerializer.Deserialize<Snapshot>(stored.Value, SnapshotJsonOptions);
            return snapshot is not null ? ToBoundary(snapshot) : throw SnapshotUnavailable();
        }
        catch (JsonException)
        {
            throw SnapshotUnavailable();
        }
    }

    public async Task<ResolvedRunModelProviderBoundary> CaptureAsync(
        Run run,
        EffectiveModelProviderResult provider,
        ByokProviderConfiguration? byokConfiguration,
        CancellationToken ct)
        => (await CaptureWithOwnershipAsync(run, provider, byokConfiguration, ct).ConfigureAwait(false)).Boundary;

    public async Task<Capture> CaptureWithOwnershipAsync(
        Run run,
        EffectiveModelProviderResult provider,
        ByokProviderConfiguration? byokConfiguration,
        CancellationToken ct)
    {
        var candidate = new Snapshot(
            Version,
            provider.ProviderKind(),
            provider.ProviderId(),
            provider.ProviderType(),
            provider.CredentialVersion(),
            byokConfiguration);
        ValidateSnapshot(candidate);
        var value = JsonSerializer.Serialize(candidate);
        var candidateSecretReference = CandidateKey(run.Id);
        await secrets.SetSecretAsync(candidateSecretReference, value, ct: ct).ConfigureAwait(false);
        var wonOwnership = false;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            db.RunModelProviderSnapshotOwners.Add(new RunModelProviderSnapshotOwner
            {
                RunId = run.Id.ToString(),
                SecretReference = candidateSecretReference,
                CapturedAt = DateTimeOffset.UtcNow,
            });
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                wonOwnership = true;
                return new Capture(ToBoundary(candidate), candidateSecretReference);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                db.ChangeTracker.Clear();
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        var winner = await TryGetAsync(run, ct).ConfigureAwait(false);
                        if (winner is not null)
                            return new Capture(winner, null);
                    }
                    catch (AgentProviderException) when (attempt < 2)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(10), ct).ConfigureAwait(false);
                    }
                }
                throw SnapshotUnavailable();
            }
        }
        finally
        {
            if (!wonOwnership)
                await secrets.DeleteSecretAsync(candidateSecretReference, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public Task<Capture> CaptureWithOwnershipAsync(
        Run run,
        ResolvedRunModelProviderBoundary boundary,
        CancellationToken ct)
    {
        ValidateBoundary(boundary);
        return CaptureWithOwnershipAsync(
            run,
            boundary.Provider,
            boundary.ByokProviderConfiguration,
            ct);
    }

    /// <summary>
    /// Releases an uncommitted capture. The conditional owner delete prevents a losing replica
    /// from deleting the durable winner selected by the unique run-id constraint.
    /// </summary>
    public async Task ReleaseAsync(Capture capture, CancellationToken ct)
    {
        if (capture.OwnedSecretReference is null)
            return;

        await secrets.DeleteSecretAsync(capture.OwnedSecretReference, ct).ConfigureAwait(false);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var owner = await db.RunModelProviderSnapshotOwners
            .SingleOrDefaultAsync(x => x.SecretReference == capture.OwnedSecretReference, ct)
            .ConfigureAwait(false);
        if (owner is null)
            return;

        db.RunModelProviderSnapshotOwners.Remove(owner);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static ResolvedRunModelProviderBoundary ToBoundary(Snapshot snapshot)
    {
        ValidateSnapshot(snapshot);
        EffectiveModelProviderResult provider = snapshot.ProviderKind switch
        {
            EffectiveModelProviderProvenance.KindByok when snapshot.ByokConfiguration is not null =>
                new EffectiveModelProviderResult.Byok(
                    snapshot.ByokConfiguration.Id,
                    snapshot.ByokConfiguration.Type,
                    snapshot.ByokConfiguration.ExecutionFingerprint()),
            EffectiveModelProviderProvenance.KindProjectGitHubCopilot when snapshot.ProviderId is not null =>
                new EffectiveModelProviderResult.ProjectGitHubCopilot(
                    snapshot.ProviderId, null, snapshot.CredentialVersion),
            EffectiveModelProviderProvenance.KindPlatformGitHubCopilot when snapshot.ProviderId is not null =>
                new EffectiveModelProviderResult.PlatformGitHubCopilot(
                    snapshot.ProviderId, null, snapshot.CredentialVersion),
            _ => throw SnapshotUnavailable(),
        };
        return new ResolvedRunModelProviderBoundary(
            provider,
            snapshot.ByokConfiguration?.ExecutionFingerprint(),
            snapshot.ByokConfiguration);
    }

    private static AgentProviderException SnapshotUnavailable() => new(
        ModelSource.GitHubCopilot,
        AgentProviderFailureKind.Configuration,
        "model_provider_changed",
        "The accepted model provider snapshot is unavailable.",
        isRetryable: true);

    private static string CandidateKey(RunId runId) =>
        $"run-model-provider-{runId}-{Guid.NewGuid():N}";

    private static void ValidateSnapshot(Snapshot snapshot)
    {
        if (snapshot.Version != Version)
            throw SnapshotUnavailable();

        switch (snapshot.ProviderKind)
        {
            case EffectiveModelProviderProvenance.KindByok:
                if (snapshot.ByokConfiguration is null
                    || !string.IsNullOrWhiteSpace(snapshot.CredentialVersion)
                    || !string.Equals(
                        snapshot.ProviderId,
                        snapshot.ByokConfiguration.Id,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        snapshot.ProviderType,
                        snapshot.ByokConfiguration.Type,
                        StringComparison.Ordinal))
                {
                    throw SnapshotUnavailable();
                }

                try
                {
                    ByokProviderConfigurationService.Validate(snapshot.ByokConfiguration);
                    if (string.IsNullOrWhiteSpace(snapshot.ByokConfiguration.Id)
                        || snapshot.ByokConfiguration.Headers?.Any(header =>
                            string.IsNullOrWhiteSpace(header.Key) || header.Value is null) == true)
                    {
                        throw SnapshotUnavailable();
                    }
                }
                catch (ArgumentException)
                {
                    throw SnapshotUnavailable();
                }

                return;

            case EffectiveModelProviderProvenance.KindProjectGitHubCopilot:
            case EffectiveModelProviderProvenance.KindPlatformGitHubCopilot:
                if (string.IsNullOrWhiteSpace(snapshot.ProviderId)
                    || snapshot.ByokConfiguration is not null
                    || snapshot.ProviderType is not null)
                {
                    throw SnapshotUnavailable();
                }

                return;

            default:
                throw SnapshotUnavailable();
        }
    }

    private static void ValidateBoundary(ResolvedRunModelProviderBoundary boundary)
    {
        if (boundary.Provider is EffectiveModelProviderResult.Byok expectedByok)
        {
            if (boundary.ByokProviderConfiguration is null
                || !GenerationModelProviderExecutor.Matches(
                    boundary.ByokProviderConfiguration,
                    expectedByok)
                || !string.Equals(
                    boundary.ByokProviderFingerprint,
                    expectedByok.ConfigurationFingerprint,
                    StringComparison.Ordinal))
            {
                throw SnapshotUnavailable();
            }

            return;
        }

        if (boundary.ByokProviderConfiguration is not null
            || boundary.ByokProviderFingerprint is not null)
        {
            throw SnapshotUnavailable();
        }
    }

    private static bool IsCandidateKey(string? secretReference, RunId runId)
    {
        if (string.IsNullOrWhiteSpace(secretReference))
            return false;

        var prefix = $"run-model-provider-{runId}-";
        return secretReference.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(secretReference[prefix.Length..], "N", out _);
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
        || exception.InnerException is SqliteException { SqliteErrorCode: 19 };
}
