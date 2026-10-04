using System.Security.Cryptography;
using System.Text;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Helpers;

internal static class RunStoreTestExtensions
{
    public static Task PinDefaultExecutableWorkflowForTestAsync(
        this IRunStore store,
        RunId runId,
        CancellationToken ct = default)
    {
        var resolved = BuiltInWorkflows.Default;
        var definition = resolved.Definition
            ?? throw new InvalidOperationException("The built-in default workflow is invalid.");
        var yaml = WorkflowDefinitionYamlSerializer.Serialize(definition);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(yaml));
        return store.UpdateExecutableWorkflowPinAsync(
            runId,
            new ExecutableWorkflowPin
            {
                ManifestSchemaVersion = ExecutableWorkflowPin.CurrentSchemaVersion,
                DefinitionId = definition.Id,
                DefinitionVersion = definition.Version,
                Source = resolved.Source,
                ContentDigest = "sha256:" + Convert.ToHexString(hash).ToLowerInvariant(),
                DefinitionYaml = yaml,
                PinnedAt = DateTimeOffset.UtcNow,
            },
            ct);
    }

    public static async Task<bool> TerminalizeForTestAsync(
        this IRunStore store,
        RunId runId,
        RunStatus status,
        string? result = null,
        DateTimeOffset? occurredAt = null,
        CancellationToken ct = default)
    {
        var run = await store.GetAsync(runId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Run {runId} was not found.");
        var at = occurredAt ?? DateTimeOffset.UtcNow;
        var outcome = status switch
        {
            RunStatus.Completed => TerminalRunOutcome.Create(
                status, EventTypes.RunCompleted, new { result }, at, run.LifecycleGeneration),
            RunStatus.Failed => TerminalRunOutcome.Create(
                status, EventTypes.RunFailed, new { reason = result }, at, run.LifecycleGeneration),
            RunStatus.Merged => TerminalRunOutcome.Create(
                status, EventTypes.MergeCompleted, new { result }, at, run.LifecycleGeneration),
            RunStatus.Declined => TerminalRunOutcome.Create(
                status, EventTypes.ReviewDeclined, new { result }, at, run.LifecycleGeneration),
            RunStatus.MergeFailed => TerminalRunOutcome.Create(
                status, EventTypes.MergeFailed, new { result }, at, run.LifecycleGeneration),
            RunStatus.AssembleReady => TerminalRunOutcome.Create(
                status, EventTypes.RunAssembleReady, new { result }, at, run.LifecycleGeneration),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "A typed terminal outcome is required."),
        };
        return await store.TrySetTerminalOutcomeAsync(runId, outcome, result, ct).ConfigureAwait(false);
    }
}
