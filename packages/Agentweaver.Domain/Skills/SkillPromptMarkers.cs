namespace Agentweaver.Domain.Skills;

/// <summary>
/// Stable, well-known markers for the progressive-disclosure skill block that the API composes into
/// an agent's system-prompt context. Shared between the writer (the API's skill prompt composer) and
/// the agent-runtime readers that emit observability signals, so the two can never silently drift.
///
/// <para>The <see cref="SectionHeading"/> is what makes a delivered skill block detectable downstream:
/// the runtime sets a <c>skillsContextIncluded</c> flag on the <c>agent.system_prompt</c> event by
/// looking for this heading in the assembled context. That flag is the direct, harness-observable
/// signal for the "skills assigned but not delivered to the agent" class of bug (issue #336).</para>
/// </summary>
public static class SkillPromptMarkers
{
    /// <summary>Heading that opens the assigned-skills progressive-disclosure block.</summary>
    public const string SectionHeading = "## Available Skills";

    /// <summary>
    /// True when <paramref name="systemPromptContext"/> contains a composed assigned-skills block.
    /// Null/empty context is treated as "no skills delivered".
    /// </summary>
    public static bool ContainsSkillContext(string? systemPromptContext) =>
        !string.IsNullOrEmpty(systemPromptContext)
        && systemPromptContext.Contains(SectionHeading, System.StringComparison.Ordinal);
}
