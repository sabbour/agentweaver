using System.Text.Json;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

namespace Agentweaver.Api.Runs;

public sealed class DurableRunOptionsStore : IRunOptionsStore
{
    private const string LaunchPolicySet = "run.approval_policy_selected";
    private const string OptionsSet = "run.options_set";
    private const string OptionsCleared = "run.options_cleared";

    private readonly DurableRunControlState _state;
    private readonly IRunStore? _runStore;

    public DurableRunOptionsStore(DurableRunControlState state, IRunStore? runStore = null)
    {
        _state = state;
        _runStore = runStore;
    }

    public void Set(string runId, RunOptions options)
    {
        if (!_state.Load(runId, LaunchPolicySet).Any())
            _state.Append(runId, LaunchPolicySet, RunApprovalPolicy.FromOptions(options));
        _state.Append(runId, OptionsSet, options);
    }

    public RunOptions Get(string runId)
    {
        var launchOptions = GetLaunchPolicy(runId).ToRunOptions();
        var options = launchOptions;
        foreach (var evt in _state.Load(runId, OptionsSet, OptionsCleared))
        {
            if (evt.EventType == OptionsCleared)
            {
                options = launchOptions;
                continue;
            }

            options = JsonSerializer.Deserialize<RunOptions>(evt.PayloadJson, JsonDefaults.Options) ?? options;
        }

        return options;
    }

    public RunApprovalPolicy GetLaunchPolicy(string runId)
    {
        var selected = _state.Load(runId, LaunchPolicySet).FirstOrDefault();
        if (selected is not null)
            return JsonSerializer.Deserialize<RunApprovalPolicy>(selected.PayloadJson, JsonDefaults.Options)
                ?? new RunApprovalPolicy();

        if (_runStore is not null && RunId.TryParse(runId, out var parsedRunId))
        {
            var run = _runStore.GetAsync(parsedRunId).GetAwaiter().GetResult();
            if (run?.GetApprovalPolicySnapshot() is { } persisted)
                return persisted.Policy;
        }

        // Compatibility for runs launched before the immutable policy event existed.
        var firstOptions = _state.Load(runId, OptionsSet).FirstOrDefault();
        var options = firstOptions is null
            ? new RunOptions()
            : JsonSerializer.Deserialize<RunOptions>(firstOptions.PayloadJson, JsonDefaults.Options)
                ?? new RunOptions();
        return RunApprovalPolicy.FromOptions(options);
    }

    public void SetAutoApproveTools(string runId, bool enabled)
    {
        var current = Get(runId);
        Set(runId, current with { AutoApproveTools = enabled });
    }

    public void SetAutopilot(string runId, bool enabled)
    {
        var current = Get(runId);
        Set(runId, current with { Autopilot = enabled });
    }

    public void Clear(string runId) =>
        _state.Append(runId, OptionsCleared, new { });
}
