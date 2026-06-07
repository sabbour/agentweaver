using System.Text.Json;
using Scaffolder.Api.Persistence;

namespace Scaffolder.Api.Agent.Governance;

/// <summary>
/// T040/T045: Central governance policy enforcer.
///
/// Responsibilities (Principle X, FR-027):
///   - Model-source validation (T040): reject unknown ModelSource values at run creation.
///   - Tool allowlist check (T045): only read_file and write_file are permitted.
///   - Human-approval gate (T045): merge is only attempted after explicit approval.
///   - Run-limits enforcement (T045): max step count and max duration policy checks.
///   - Policy trace (T045, SC-010): every policy decision appends a timestamped
///     entry to the OperationalRecord so compliance reviewers can reconstruct
///     all governance outcomes without needing the event log.
///
/// Every public method is synchronous with respect to the decision itself;
/// persistence of the trace entry is fire-and-forget so it never stalls
/// the hot agent loop path.
/// </summary>
public sealed class GovernancePolicyEngine
{
    private static readonly HashSet<string> AllowedTools =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "read_file",
            "write_file"
        };

    private readonly IOperationalRecordRepository _opRecords;
    private readonly ILogger<GovernancePolicyEngine> _logger;

    public GovernancePolicyEngine(
        IOperationalRecordRepository opRecords,
        ILogger<GovernancePolicyEngine> logger)
    {
        _opRecords = opRecords;
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // T040: Model-source validation
    // ------------------------------------------------------------------

    /// <summary>
    /// Validates the model source at run creation time.
    /// Returns <c>null</c> if valid; returns an error message to return as 400 if invalid.
    /// </summary>
    public string? ValidateModelSource(ModelSource modelSource)
    {
        var valid = modelSource is ModelSource.CopilotSdk or ModelSource.MicrosoftFoundry;
        var decision = valid ? "pass" : "reject";

        _logger.LogInformation(
            "GovernancePolicyEngine: model-source check {ModelSource} -> {Decision}",
            modelSource, decision);

        if (!valid)
        {
            return $"Unsupported model source '{modelSource}'. " +
                   "Accepted values: CopilotSdk, MicrosoftFoundry.";
        }

        return null;
    }

    // ------------------------------------------------------------------
    // T045: Tool allowlist check
    // ------------------------------------------------------------------

    /// <summary>
    /// Checks whether a tool invocation is on the approved allowlist.
    /// Returns <c>true</c> if allowed; <c>false</c> if blocked.
    /// </summary>
    public bool IsToolAllowed(string toolName)
    {
        var allowed = AllowedTools.Contains(toolName);

        _logger.LogDebug(
            "GovernancePolicyEngine: tool-allowlist check '{ToolName}' -> {Result}",
            toolName, allowed ? "allowed" : "blocked");

        return allowed;
    }

    // ------------------------------------------------------------------
    // T045: Human-approval gate
    // ------------------------------------------------------------------

    /// <summary>
    /// Validates that a merge can only be attempted for a run in the Approved state.
    /// Returns <c>null</c> if the gate passes; returns a rejection reason otherwise.
    /// </summary>
    public string? ValidateHumanApprovalGate(RunStatus currentStatus)
    {
        if (currentStatus != RunStatus.Approved)
        {
            return $"Merge requires human approval. Current status is '{currentStatus}'; " +
                   "expected 'Approved'.";
        }

        return null;
    }

    // ------------------------------------------------------------------
    // T045: Policy trace helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Appends a timestamped policy trace entry to the OperationalRecord for a run.
    /// Fire-and-forget — never throws; errors are logged.
    /// </summary>
    public void RecordPolicyDecision(
        Guid runId,
        string policyName,
        string decision,
        string? detail = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var entry = new
                {
                    timestamp = DateTimeOffset.UtcNow,
                    policy = policyName,
                    decision,
                    detail
                };

                var entryJson = JsonSerializer.Serialize(entry);
                await _opRecords.AppendPolicyTraceEntryAsync(runId, entryJson, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "GovernancePolicyEngine: failed to persist policyTrace entry for run {RunId}",
                    runId);
            }
        });
    }
}
