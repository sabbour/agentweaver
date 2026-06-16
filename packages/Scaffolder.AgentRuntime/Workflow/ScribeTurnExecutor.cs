using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Scaffolder.Domain;

namespace Scaffolder.AgentRuntime.Workflow;

/// <summary>
/// Runs Scribe as a real agent turn after a project run completes.
/// Scribe receives a structured task and uses memory API tools to review
/// the inbox, merge learnings, and export to .squad/.
/// Best-effort: exceptions never abort the workflow.
/// </summary>
public sealed class ScribeTurnExecutor : Executor<ScribeTurnInput, ScribeTurnInput>
{
    private readonly IAgentRunner _agentRunner;
    private readonly ILogger<ScribeTurnExecutor> _logger;

    public ScribeTurnExecutor(
        IAgentRunner agentRunner,
        ILogger<ScribeTurnExecutor> logger,
        string name = "scribe-turn")
        : base(name)
    {
        _agentRunner = agentRunner;
        _logger = logger;
    }

    public override async ValueTask<ScribeTurnInput> HandleAsync(
        ScribeTurnInput input, IWorkflowContext context, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(input.ProjectId) || string.IsNullOrEmpty(input.AgentName))
        {
            _logger.LogDebug("Scribe skipped for run {RunId} — no project/agent context", input.RunId);
            return input;
        }

        try
        {
            var task = $$"""
                You are Scribe. A project run has just completed.

                Run: {{input.RunId}}
                Project: {{input.ProjectId}}
                Agent: {{input.AgentName}}
                Run started at: {{input.RunStartedAt:O}}

                Your post-run tasks — use the memory API tools available to you:

                1. GET /api/projects/{{input.ProjectId}}/inbox?agent={{input.AgentName}}&status=pending
                2. For each `learning`, `pattern`, or `update` entry created since {{input.RunStartedAt:O}}:
                   POST /api/projects/{{input.ProjectId}}/inbox/{id}/merge
                3. Leave `architectural` and `scope` entries as pending (coordinator must review these).
                4. POST /api/projects/{{input.ProjectId}}/memory/export
                5. PUT /api/projects/{{input.ProjectId}}/sessions/current  — append a one-sentence summary of what {{input.AgentName}} accomplished.

                Complete this post-run Scribe pass now. Be systematic and concise.
                """;

            var charter = """
                You are Scribe — the silent memory keeper for this agent team.
                You do not write code or make design decisions.
                You only manage memory: merge, archive, and export.
                Act systematically. Complete every step. Never skip the export.
                """;

            await _agentRunner.ExecuteAsync(
                task,
                workingDirectory: input.RepositoryPath,
                repositoryPath: input.RepositoryPath,
                ModelSourceExtensions.FromApiString(input.ModelSource),
                runId: input.RunId + "-scribe",
                modelId: input.ModelId,
                stream: null,
                ct,
                systemPromptContext: charter).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Scribe agent turn failed for run {RunId} — workflow proceeds normally", input.RunId);
        }

        return input;
    }
}
