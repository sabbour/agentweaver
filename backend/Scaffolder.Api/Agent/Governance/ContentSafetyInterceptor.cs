using Scaffolder.Api.Agent.ModelSources;
using Scaffolder.Api.Persistence;
using Scaffolder.Api.Runs;

namespace Scaffolder.Api.Agent.Governance;

/// <summary>
/// T046: Content-safety interceptor.
///
/// Every model output MUST pass through this interceptor before being written
/// to the event log or relayed to any SSE subscriber (FR-025, SC-008).
///
/// On a safety failure:
///   - The content is withheld (never written to the event log or stream).
///   - A run.failed event is appended with failureReason indicating content-safety failure.
///   - The run is transitioned to a Failed terminal state.
///   - 100% intercept is enforced — there is no bypass path.
/// </summary>
public sealed class ContentSafetyInterceptor
{
    private readonly EventLogService _eventLog;
    private readonly RunStateMachine _stateMachine;
    private readonly ILogger<ContentSafetyInterceptor> _logger;

    public ContentSafetyInterceptor(
        EventLogService eventLog,
        RunStateMachine stateMachine,
        ILogger<ContentSafetyInterceptor> logger)
    {
        _eventLog = eventLog;
        _stateMachine = stateMachine;
        _logger = logger;
    }

    /// <summary>
    /// Checks the model output via the adapter's content-safety API.
    /// Returns <c>true</c> if the content is safe to use.
    /// Returns <c>false</c> if the content was withheld and the run has been failed.
    /// </summary>
    public async Task<bool> CheckAndEnforceAsync(
        Guid runId,
        string modelOutput,
        IModelSourceAdapter adapter,
        CancellationToken ct = default)
    {
        string? failureReason;
        try
        {
            failureReason = await adapter.ApplyContentSafetyCheckAsync(modelOutput, ct);
        }
        catch (Exception ex)
        {
            // A safety-check error is treated as a safety failure per FR-025.
            failureReason = $"Content-safety check threw an exception: {ex.Message}";
            _logger.LogError(ex,
                "ContentSafetyInterceptor: safety-check exception for run {RunId}", runId);
        }

        if (failureReason is null)
        {
            return true; // Content is safe
        }

        // Withhold content and transition to failed terminal state.
        _logger.LogWarning(
            "ContentSafetyInterceptor: content safety failure for run {RunId}. " +
            "Reason: {Reason}. Content withheld.", runId, failureReason);

        try
        {
            await _stateMachine.TransitionToFailedAsync(
                runId,
                $"Content-safety failure: {failureReason}",
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ContentSafetyInterceptor: failed to transition run {RunId} to Failed", runId);
        }

        return false; // Content was withheld
    }
}
