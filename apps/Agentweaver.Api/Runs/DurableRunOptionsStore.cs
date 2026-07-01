using System.Text.Json;
using Agentweaver.Api.Contracts;
using Agentweaver.Domain;

namespace Agentweaver.Api.Runs;

public sealed class DurableRunOptionsStore(DurableRunControlState state) : IRunOptionsStore
{
    private const string OptionsSet = "run.options_set";
    private const string OptionsCleared = "run.options_cleared";

    private readonly DurableRunControlState _state = state;

    public void Set(string runId, RunOptions options) =>
        _state.Append(runId, OptionsSet, options);

    public RunOptions Get(string runId)
    {
        var options = new RunOptions();
        foreach (var evt in _state.Load(runId, OptionsSet, OptionsCleared))
        {
            if (evt.EventType == OptionsCleared)
            {
                options = new RunOptions();
                continue;
            }

            options = JsonSerializer.Deserialize<RunOptions>(evt.PayloadJson, JsonDefaults.Options) ?? options;
        }

        return options;
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
