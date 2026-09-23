namespace Agentweaver.Domain.Skills;

/// <summary>
/// Stable, well-known markers for the progressive-disclosure skill block that the API composes into
/// an agent's system-prompt context. Shared between the writer (the API's skill prompt composer) and
/// the agent-runtime readers that emit observability signals, so the two can never silently drift.
///
/// <para>The <see cref="SectionHeading"/> makes a delivered skill block measurable downstream
/// without exposing its name or content in runtime-context observability.</para>
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
