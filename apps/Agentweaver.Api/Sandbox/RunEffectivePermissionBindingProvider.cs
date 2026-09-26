using System.Text.Json;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

namespace Agentweaver.Api.Sandbox;

/// <summary>Resolves current run permissions and intersects every child with its parent chain.</summary>
public sealed class RunEffectivePermissionBindingProvider(
    IRunStore runStore,
    ISandboxPolicyStore policyStore,
    IRunEventStream eventStream) : IEffectivePermissionBindingProvider
{
    private const int MaximumParentDepth = 64;

    public async Task<EffectivePermissionBinding> ResolveAsync(
        string runId,
        string repositoryPath,
        EffectivePermissionBinding? ceiling = null,
        CancellationToken ct = default)
    {
        var binding = await ResolveAsync(
            runId,
            repositoryPath,
            establishLaunchCeiling: true,
            ct).ConfigureAwait(false);
        if (ceiling is not null)
            binding = EffectivePermissionBinding.Intersect(binding, ceiling);
        binding.Validate(runId, binding.Attempt);
        return binding;
    }

    public Task<EffectivePermissionBinding> ResolveForInspectionAsync(
        string runId,
        string repositoryPath,
        CancellationToken ct = default) =>
        ResolveAsync(runId, repositoryPath, establishLaunchCeiling: false, ct);

    private async Task<EffectivePermissionBinding> ResolveAsync(
        string runId,
        string repositoryPath,
        bool establishLaunchCeiling,
        CancellationToken ct)
    {
        if (!RunId.TryParse(runId, out var parsedRunId))
            throw new EffectivePermissionBindingException("Effective permission binding requires a valid run id.");

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var binding = await ResolveCoreAsync(
            parsedRunId,
            repositoryPath,
            visited,
            depth: 0,
            establishLaunchCeiling,
            ct).ConfigureAwait(false);
        binding.Validate(runId, binding.Attempt);
        return binding;
    }

    private async Task<EffectivePermissionBinding> ResolveCoreAsync(
        RunId runId,
        string repositoryPath,
        HashSet<string> visited,
        int depth,
        bool establishLaunchCeiling,
        CancellationToken ct)
    {
        var id = runId.ToString();
        if (depth >= MaximumParentDepth || !visited.Add(id))
            throw new EffectivePermissionBindingException("Effective permission parent chain is cyclic or too deep.");

        var run = await runStore.GetAsync(runId, ct).ConfigureAwait(false)
            ?? throw new EffectivePermissionBindingException(
                $"Run '{id}' is unavailable while resolving effective permissions.");

        EffectivePermissionBinding? parent = null;
        if (!string.IsNullOrWhiteSpace(run.ParentRunId))
        {
            if (!RunId.TryParse(run.ParentRunId, out var parentRunId))
                throw new EffectivePermissionBindingException(
                    $"Run '{id}' has an invalid parent permission identity.");
            parent = await ResolveCoreAsync(
                parentRunId,
                run.RepositoryPath,
                visited,
                depth + 1,
                establishLaunchCeiling,
                ct).ConfigureAwait(false);
        }

        var policyPath = string.IsNullOrWhiteSpace(repositoryPath)
            ? run.RepositoryPath
            : repositoryPath;
        var policy = await policyStore.GetPolicyAsync(policyPath, ct).ConfigureAwait(false);
        var scope = run.ProjectId is { } projectId
            ? $"project:{projectId}"
            : $"repository:{run.RepositoryPath}";
        var current = EffectivePermissionBinding.Create(
            id,
            run.LifecycleGeneration,
            "current-project-sandbox-policy",
            scope,
            policy,
            parent);
        var launchCeiling = establishLaunchCeiling
            ? await GetOrCreateLaunchCeilingAsync(run, current, ct).ConfigureAwait(false)
            : await GetLaunchCeilingAsync(run, ct).ConfigureAwait(false);
        var binding = launchCeiling is null
            ? current
            : EffectivePermissionBinding.Intersect(current, launchCeiling);
        binding.Validate(id, run.LifecycleGeneration);
        return binding;
    }

    private async Task<EffectivePermissionBinding?> GetLaunchCeilingAsync(
        Run run,
        CancellationToken ct)
    {
        var runId = run.Id.ToString();
        var events = await eventStream
            .GetPersistedEventsAsync(runId, 0, ct)
            .ConfigureAwait(false);
        return FindLaunchCeiling(events, runId, run.LifecycleGeneration);
    }

    private async Task<EffectivePermissionBinding> GetOrCreateLaunchCeilingAsync(
        Run run,
        EffectivePermissionBinding candidate,
        CancellationToken ct)
    {
        var runId = run.Id.ToString();
        var events = await eventStream
            .GetPersistedEventsAsync(runId, 0, ct)
            .ConfigureAwait(false);
        if (FindLaunchCeiling(events, runId, run.LifecycleGeneration) is { } existing)
            return existing;

        await eventStream.AppendAsync(
            runId,
            new RunEvent(0, EventTypes.PermissionBindingBound, candidate),
            ct).ConfigureAwait(false);

        events = await eventStream
            .GetPersistedEventsAsync(runId, 0, ct)
            .ConfigureAwait(false);
        return FindLaunchCeiling(events, runId, run.LifecycleGeneration)
            ?? throw new EffectivePermissionBindingException(
                $"Run '{runId}' effective permission launch ceiling could not be persisted.");
    }

    private static EffectivePermissionBinding? FindLaunchCeiling(
        IReadOnlyList<RunEvent> events,
        string runId,
        int attempt)
    {
        foreach (var evt in events
                     .Where(candidate => candidate.Type == EventTypes.PermissionBindingBound)
                     .OrderBy(candidate => candidate.Sequence))
        {
            EffectivePermissionBinding? binding = evt.Payload switch
            {
                EffectivePermissionBinding value => value,
                JsonElement json => json.Deserialize<EffectivePermissionBinding>(
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                _ => null,
            };
            if (binding is null || binding.Attempt != attempt)
                continue;
            binding.Validate(runId, attempt);
            return binding;
        }

        return null;
    }
}
