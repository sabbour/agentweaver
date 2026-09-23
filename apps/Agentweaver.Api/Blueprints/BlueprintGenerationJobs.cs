using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Generation;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Agentweaver.Api.Blueprints;

public static class BlueprintGenerationJobStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public sealed record BlueprintGenerationJobCreate(
    string Subject,
    string IdempotencyKey,
    string RequestFingerprint,
    string Description,
    string? ProjectId,
    string? TargetRepository,
    string? BlueprintModel,
    string? WorkflowModel,
    string ProviderKind,
    string? ProviderType,
    string ProviderKey,
    string ProviderScope,
    string ResolutionScope,
    string? CredentialBindingVersion,
    string QueuedProviderKey);

public sealed record BlueprintGenerationJobSnapshot(
    BlueprintGenerationJobRecord Job,
    BlueprintGenerationArtifactRecord? Artifact);

public enum BlueprintGenerationJobCreateDisposition
{
    Created,
    Existing,
    Conflict,
}

public sealed record BlueprintGenerationJobCreateResult(
    BlueprintGenerationJobCreateDisposition Disposition,
    BlueprintGenerationJobSnapshot Snapshot);

public sealed class BlueprintGenerationJobStore(MemoryDbContext db)
{
    public async Task<BlueprintGenerationJobCreateResult> CreateOrGetAsync(
        BlueprintGenerationJobCreate request,
        CancellationToken ct)
    {
        var existing = await FindByIdempotencyAsync(request.Subject, request.IdempotencyKey, ct)
            .ConfigureAwait(false);
        if (existing is not null)
            return ExistingResult(existing, request.RequestFingerprint);

        var now = DateTimeOffset.UtcNow;
        var record = new BlueprintGenerationJobRecord
        {
            JobId = Guid.NewGuid().ToString("N"),
            Subject = request.Subject,
            IdempotencyKey = request.IdempotencyKey,
            RequestFingerprint = request.RequestFingerprint,
            Description = request.Description,
            ProjectId = request.ProjectId,
            TargetRepository = request.TargetRepository,
            BlueprintModel = request.BlueprintModel,
            WorkflowModel = request.WorkflowModel,
            ProviderKind = request.ProviderKind,
            ProviderType = request.ProviderType,
            ProviderKey = request.ProviderKey,
            ProviderScope = request.ProviderScope,
            ResolutionScope = request.ResolutionScope,
            CredentialBindingVersion = request.CredentialBindingVersion,
            QueuedProviderKey = request.QueuedProviderKey,
            Status = BlueprintGenerationJobStatuses.Queued,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.BlueprintGenerationJobs.Add(record);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new(
                BlueprintGenerationJobCreateDisposition.Created,
                new(record, null));
        }
        catch (DbUpdateException)
        {
            db.Entry(record).State = EntityState.Detached;
            var raced = await FindByIdempotencyAsync(request.Subject, request.IdempotencyKey, ct)
                .ConfigureAwait(false);
            if (raced is null)
                throw;
            return ExistingResult(raced, request.RequestFingerprint);
        }
    }

    public async Task<BlueprintGenerationJobSnapshot?> GetAsync(string jobId, CancellationToken ct)
    {
        var job = await db.BlueprintGenerationJobs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.JobId == jobId, ct)
            .ConfigureAwait(false);
        if (job is null)
            return null;
        var artifact = await db.BlueprintGenerationArtifacts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.JobId == jobId, ct)
            .ConfigureAwait(false);
        return new(job, artifact);
    }

    public async Task<BlueprintGenerationJobSnapshot?> TryClaimNextAsync(
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = await db.BlueprintGenerationJobs.AsNoTracking()
            .Where(x => x.Status == BlueprintGenerationJobStatuses.Queued)
            .OrderBy(x => x.JobId)
            .Take(10)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        const int pageSize = 100;
        var expiredCount = 0;
        for (var page = 0; expiredCount < 10; page++)
        {
            var runningPage = await db.BlueprintGenerationJobs.AsNoTracking()
                .Where(x => x.Status == BlueprintGenerationJobStatuses.Running)
                .OrderBy(x => x.JobId)
                .Skip(page * pageSize)
                .Take(pageSize)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var expired = runningPage
                .Where(x => x.LeaseExpiresAt is { } leaseExpiresAt && leaseExpiresAt <= now)
                .Take(10 - expiredCount)
                .ToList();
            candidates.AddRange(expired);
            expiredCount += expired.Count;
            if (runningPage.Count < pageSize)
                break;
        }

        candidates = candidates.OrderBy(x => x.CreatedAt).ToList();

        foreach (var candidate in candidates)
        {
            var claimable = db.BlueprintGenerationJobs.Where(x => x.JobId == candidate.JobId);
            claimable = candidate.Status == BlueprintGenerationJobStatuses.Queued
                ? claimable.Where(x => x.Status == BlueprintGenerationJobStatuses.Queued)
                : claimable.Where(x => x.Status == BlueprintGenerationJobStatuses.Running
                    && x.LeaseOwner == candidate.LeaseOwner
                    && x.LeaseExpiresAt == candidate.LeaseExpiresAt);
            var claimed = await claimable
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, BlueprintGenerationJobStatuses.Running)
                    .SetProperty(x => x.LeaseOwner, leaseOwner)
                    .SetProperty(x => x.LeaseExpiresAt, now.Add(leaseDuration))
                    .SetProperty(x => x.StartedAt, x => x.StartedAt ?? now)
                    .SetProperty(x => x.UpdatedAt, now)
                    .SetProperty(x => x.Attempt, x => x.Attempt + 1), ct)
                .ConfigureAwait(false);
            if (claimed == 1)
                return await GetAsync(candidate.JobId, ct).ConfigureAwait(false);
        }

        return null;
    }

    public async Task<bool> RenewLeaseAsync(
        string jobId,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        return await db.BlueprintGenerationJobs
            .Where(x => x.JobId == jobId
                && x.Status == BlueprintGenerationJobStatuses.Running
                && x.LeaseOwner == leaseOwner)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LeaseExpiresAt, now.Add(leaseDuration))
                .SetProperty(x => x.UpdatedAt, now), ct)
            .ConfigureAwait(false) == 1;
    }

    public async Task<bool> CompleteAsync(
        string jobId,
        string leaseOwner,
        BlueprintGenerationResult result,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var artifact = new BlueprintGenerationArtifactRecord
        {
            ArtifactId = Guid.NewGuid().ToString("N"),
            JobId = jobId,
            LogicalId = result.Blueprint!.Id,
            Version = 1,
            BlueprintJson = JsonSerializer.Serialize(BlueprintDto.FromModel(result.Blueprint)),
            GeneratedWorkflowYaml = result.GeneratedWorkflowYaml,
            WarningsJson = JsonSerializer.Serialize(result.Warnings),
            CreatedAt = now,
        };
        db.BlueprintGenerationArtifacts.Add(artifact);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            var completed = await db.BlueprintGenerationJobs
                .Where(x => x.JobId == jobId
                    && x.Status == BlueprintGenerationJobStatuses.Running
                    && x.LeaseOwner == leaseOwner)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, BlueprintGenerationJobStatuses.Completed)
                    .SetProperty(x => x.LeaseOwner, (string?)null)
                    .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.CompletedAt, now)
                    .SetProperty(x => x.UpdatedAt, now), ct)
                .ConfigureAwait(false);
            if (completed != 1)
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                db.Entry(artifact).State = EntityState.Detached;
                return false;
            }
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            db.Entry(artifact).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<bool> FailAsync(
        string jobId,
        string leaseOwner,
        string code,
        string message,
        bool retryable,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        return await db.BlueprintGenerationJobs
            .Where(x => x.JobId == jobId
                && x.Status == BlueprintGenerationJobStatuses.Running
                && x.LeaseOwner == leaseOwner)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, BlueprintGenerationJobStatuses.Failed)
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(x => x.FailureCode, code)
                .SetProperty(x => x.FailureMessage, message)
                .SetProperty(x => x.FailureRetryable, retryable)
                .SetProperty(x => x.CompletedAt, now)
                .SetProperty(x => x.UpdatedAt, now), ct)
            .ConfigureAwait(false) == 1;
    }

    public async Task<BlueprintGenerationJobSnapshot?> CancelAsync(string jobId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        _ = await db.BlueprintGenerationJobs
            .Where(x => x.JobId == jobId
                && (x.Status == BlueprintGenerationJobStatuses.Queued
                    || x.Status == BlueprintGenerationJobStatuses.Running))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, BlueprintGenerationJobStatuses.Cancelled)
                .SetProperty(x => x.LeaseOwner, (string?)null)
                .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(x => x.CompletedAt, now)
                .SetProperty(x => x.UpdatedAt, now), ct)
            .ConfigureAwait(false);
        return await GetAsync(jobId, ct).ConfigureAwait(false);
    }

    public async Task<BlueprintGenerationJobSnapshot?> RetryAsync(string jobId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        _ = await db.BlueprintGenerationJobs
            .Where(x => x.JobId == jobId
                && ((x.Status == BlueprintGenerationJobStatuses.Failed && x.FailureRetryable)
                    || x.Status == BlueprintGenerationJobStatuses.Cancelled)
                && !db.BlueprintGenerationArtifacts.Any(a => a.JobId == x.JobId))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, BlueprintGenerationJobStatuses.Queued)
                .SetProperty(x => x.FailureCode, (string?)null)
                .SetProperty(x => x.FailureMessage, (string?)null)
                .SetProperty(x => x.FailureRetryable, false)
                .SetProperty(x => x.CompletedAt, (DateTimeOffset?)null)
                .SetProperty(x => x.UpdatedAt, now), ct)
            .ConfigureAwait(false);
        return await GetAsync(jobId, ct).ConfigureAwait(false);
    }

    private async Task<BlueprintGenerationJobSnapshot?> FindByIdempotencyAsync(
        string subject,
        string idempotencyKey,
        CancellationToken ct)
    {
        var job = await db.BlueprintGenerationJobs.AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Subject == subject && x.IdempotencyKey == idempotencyKey,
                ct)
            .ConfigureAwait(false);
        return job is null ? null : await GetAsync(job.JobId, ct).ConfigureAwait(false);
    }

    private static BlueprintGenerationJobCreateResult ExistingResult(
        BlueprintGenerationJobSnapshot existing,
        string fingerprint) =>
        new(
            string.Equals(existing.Job.RequestFingerprint, fingerprint, StringComparison.Ordinal)
                ? BlueprintGenerationJobCreateDisposition.Existing
                : BlueprintGenerationJobCreateDisposition.Conflict,
            existing);
}

public sealed class BlueprintGenerationJobWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BlueprintGenerationJobWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan LeaseHeartbeat = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);
    internal const int WorkerCount = 4;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        await Task.WhenAll(Enumerable.Range(0, WorkerCount)
            .Select(_ => RunLoopAsync(stoppingToken))).ConfigureAwait(false);
    }

    private async Task RunLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await RunOneAsync(stoppingToken).ConfigureAwait(false))
                    await Task.Delay(IdleDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Blueprint generation worker iteration failed");
                await Task.Delay(IdleDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task<bool> RunOneAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<BlueprintGenerationJobStore>();
        var leaseOwner = $"{Environment.MachineName}:{Guid.NewGuid():N}";
        var snapshot = await store.TryClaimNextAsync(leaseOwner, LeaseDuration, ct).ConfigureAwait(false);
        if (snapshot is null)
            return false;

        using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var execution = ExecuteClaimedAsync(scope.ServiceProvider, snapshot.Job, executionCts.Token);
        while (!execution.IsCompleted)
        {
            var heartbeat = Task.Delay(LeaseHeartbeat, ct);
            if (await Task.WhenAny(execution, heartbeat).ConfigureAwait(false) == execution)
                break;
            if (!await store.RenewLeaseAsync(
                    snapshot.Job.JobId, leaseOwner, LeaseDuration, ct).ConfigureAwait(false))
            {
                executionCts.Cancel();
                break;
            }
        }

        BlueprintGenerationResult result;
        try
        {
            result = await execution.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (executionCts.IsCancellationRequested)
        {
            return true;
        }
        catch (AiExecutionPlanException)
        {
            await store.FailAsync(
                snapshot.Job.JobId,
                leaseOwner,
                "blueprint_provider_authorization_required",
                "The accepted AI provider binding changed. Reauthorize it and submit a new Blueprint-generation request.",
                retryable: false,
                ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Blueprint generation job {JobId} failed unexpectedly", snapshot.Job.JobId);
            await store.FailAsync(
                snapshot.Job.JobId,
                leaseOwner,
                "blueprint_generation_failed",
                "Blueprint generation failed before an artifact could be persisted.",
                retryable: true,
                ct).ConfigureAwait(false);
            return true;
        }

        if (result.Succeeded)
        {
            _ = await store.CompleteAsync(snapshot.Job.JobId, leaseOwner, result, ct).ConfigureAwait(false);
            return true;
        }

        var failure = CanonicalFailure(result);
        _ = await store.FailAsync(
            snapshot.Job.JobId,
            leaseOwner,
            failure.Code,
            failure.Message,
            failure.Retryable,
            ct).ConfigureAwait(false);
        return true;
    }

    private static async Task<BlueprintGenerationResult> ExecuteClaimedAsync(
        IServiceProvider services,
        BlueprintGenerationJobRecord job,
        CancellationToken ct)
    {
        if (!AiOperationCatalog.TryGet("blueprint_generation", out var operation))
            throw new InvalidOperationException("Blueprint generation AI operation is not registered.");
        var plans = services.GetRequiredService<AiExecutionPlanService>();
        var accessor = services.GetRequiredService<AiExecutionPlanAccessor>();
        var projectId = ProjectId.TryParse(job.ProjectId, out var parsed) ? parsed : (ProjectId?)null;
        var plan = await plans.RestoreAcceptedAsync(
            job.QueuedProviderKey,
            operation,
            projectId,
            job.Subject,
            ct).ConfigureAwait(false);
        using var accepted = accessor.Push(plan);
        var blueprints = services.GetRequiredService<BlueprintService>();
        return await blueprints.GenerateAsync(
            job.Description,
            ct,
            job.Subject,
            job.TargetRepository,
            job.ProjectId,
            job.BlueprintModel,
            job.WorkflowModel).ConfigureAwait(false);
    }

    private static (string Code, string Message, bool Retryable) CanonicalFailure(
        BlueprintGenerationResult result)
    {
        var code = result.ErrorCode ?? "";
        if (code.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "blueprint_provider_timeout",
                "The AI provider did not complete Blueprint generation before its deadline.",
                true);
        }

        return result.FailureKind switch
        {
            BlueprintGenerationFailureKind.ProviderAuthorization => (
                "blueprint_provider_authorization_required",
                "The configured AI provider requires reauthorization before Blueprint generation can continue.",
                false),
            BlueprintGenerationFailureKind.ProviderConfiguration => (
                "blueprint_provider_configuration_invalid",
                "The configured AI provider or model is not available for Blueprint generation.",
                false),
            BlueprintGenerationFailureKind.ProviderRateLimited => (
                "blueprint_provider_unavailable",
                "The AI provider is temporarily unavailable for Blueprint generation. Retry later.",
                true),
            BlueprintGenerationFailureKind.ProviderUnavailable or BlueprintGenerationFailureKind.ModelRunFailed => (
                "blueprint_provider_unavailable",
                "The AI provider is temporarily unavailable for Blueprint generation. Retry later.",
                true),
            BlueprintGenerationFailureKind.Validation => (
                "blueprint_generation_invalid",
                "The generated Blueprint did not satisfy the Blueprint contract.",
                false),
            _ => (
                "blueprint_generation_failed",
                "Blueprint generation failed before an artifact could be persisted.",
                true),
        };
    }
}

public static class BlueprintGenerationFingerprint
{
    public static string Create(
        GenerateBlueprintRequest request,
        string? projectId,
        string? blueprintModel,
        string? workflowModel,
        AiExecutionPlan plan)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            description = request.Description?.Trim(),
            project_id = projectId,
            target_repository = request.TargetRepository?.Trim(),
            blueprint_model = blueprintModel,
            workflow_model = workflowModel,
            provider_key = plan.Provider.ProviderKey(),
            credential_binding_version = plan.Provider.CredentialVersion(),
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}
