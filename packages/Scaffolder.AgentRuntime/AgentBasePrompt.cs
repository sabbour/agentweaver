namespace Scaffolder.AgentRuntime;

/// <summary>
/// Minimal base system prompt injected for every agent run.
/// Covers only the universal runtime contract — report_intent/report_outcome tooling
/// and the working-directory safety constraint. Agent identity, working style, and
/// tool-usage guidance belong in the agent's charter (<c>systemPromptContext</c>),
/// which is appended after this base when present.
/// </summary>
internal static class AgentBasePrompt
{
    internal const string Base =
        """
        Complete the given task using the available tools.
        Only write files within the current working directory.

        Call report_intent(intent) before each major step.
        Call report_outcome(achieved, reason) as your final tool call.
        If any tool call was blocked by the sandbox, call report_outcome(achieved=false, reason=<what was blocked and why>).
        Do not ask clarifying questions — proceed with best judgement.
        """;
}
