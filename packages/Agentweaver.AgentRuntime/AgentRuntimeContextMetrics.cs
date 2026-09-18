using System.Text.Json;
using Microsoft.Extensions.AI;
using Agentweaver.Domain.Skills;

namespace Agentweaver.AgentRuntime;

internal sealed record AgentRuntimeContextMetrics(
    string Provider,
    string RunId,
    string? ProjectId,
    int BaseCharacters,
    int RunContextCharacters,
    int SkillCharacters,
    int SeparatorCharacters,
    int TaskCharacters,
    int ToolDeclarationCharacters,
    string SkillDeliveryMode,
    int TotalCharacters,
    int EstimatedTokens);

internal static class AgentRuntimeContextMetricsComposer
{
    private const string ContextSeparator = "\n\n";
    private const string SkillSeparator = "\n\n---\n\n";

    internal static AgentRuntimeContextMetrics Compose(
        string provider,
        string runId,
        string? projectId,
        string task,
        string? systemPromptContext,
        IReadOnlyList<string> registeredToolNames,
        IReadOnlyList<AIFunctionDeclaration> toolDeclarations)
    {
        var baseCharacters = AgentBasePrompt.Build(registeredToolNames).Length;
        var (runContextCharacters, skillCharacters, skillSeparatorCharacters, skillDeliveryMode) =
            MeasureRunContext(systemPromptContext);
        var contextSeparatorCharacters = string.IsNullOrEmpty(systemPromptContext) ? 0 : ContextSeparator.Length;
        var taskCharacters = task.Length;
        var toolDeclarationCharacters = JsonSerializer.Serialize(toolDeclarations).Length;
        var separatorCharacters = contextSeparatorCharacters + skillSeparatorCharacters;
        var totalCharacters = baseCharacters + runContextCharacters + skillCharacters + separatorCharacters +
                              taskCharacters + toolDeclarationCharacters;

        return new AgentRuntimeContextMetrics(
            provider,
            runId,
            projectId,
            baseCharacters,
            runContextCharacters,
            skillCharacters,
            separatorCharacters,
            taskCharacters,
            toolDeclarationCharacters,
            skillDeliveryMode,
            totalCharacters,
            (totalCharacters + 3) / 4);
    }

    private static (int RunContextCharacters, int SkillCharacters, int SkillSeparatorCharacters, string SkillDeliveryMode)
        MeasureRunContext(string? systemPromptContext)
    {
        if (string.IsNullOrEmpty(systemPromptContext))
            return (0, 0, 0, "none");

        var skillStart = systemPromptContext.IndexOf(SkillPromptMarkers.SectionHeading, StringComparison.Ordinal);
        if (skillStart < 0)
            return (systemPromptContext.Length, 0, 0, "none");

        var separatorStart = systemPromptContext.LastIndexOf(SkillSeparator, skillStart, StringComparison.Ordinal);
        var skillStartIndex = separatorStart >= 0 ? separatorStart + SkillSeparator.Length : skillStart;
        var skillCharacters = systemPromptContext.Length - skillStartIndex;
        var runContextCharacters = separatorStart >= 0 ? separatorStart : skillStart;
        var skillBlock = systemPromptContext[skillStartIndex..];
        var hasFileDelivery = skillBlock.Contains("/SKILL.md`", StringComparison.Ordinal);
        var hasInlineDelivery = skillBlock.Contains("Full instructions (inlined", StringComparison.Ordinal);
        var deliveryMode = hasFileDelivery && hasInlineDelivery ? "mixed"
            : hasFileDelivery ? "file"
            : hasInlineDelivery ? "inline"
            : "none";

        return (runContextCharacters, skillCharacters, separatorStart >= 0 ? SkillSeparator.Length : 0, deliveryMode);
    }
}