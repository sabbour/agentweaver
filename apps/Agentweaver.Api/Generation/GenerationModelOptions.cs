using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Api.Generation;

/// <summary>
/// Model selection for one-shot generation flows. Runtime agent execution keeps using project/run
/// model selection; these settings cover server-authored blueprint, workflow, and outcome-spec drafts.
/// </summary>
public sealed class GenerationModelOptions
{
    public const string SectionName = "Generation";
    public const string DefaultModel = "gpt-5.6-sol";

    private static readonly Regex AllowedModelRegex =
        new("^(gpt|claude|o)[a-z0-9._-]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Shared model for generation flows unless a per-flow override is set.</summary>
    public string? Model { get; set; } = DefaultModel;

    public string? BlueprintModel { get; set; }
    public string? WorkflowModel { get; set; }
    public string? OutcomeSpecModel { get; set; }

    public string ResolveBlueprintModel(string? projectModel = null) => Resolve(projectModel, BlueprintModel, Model);
    public string ResolveWorkflowModel(string? projectModel = null) => Resolve(projectModel, WorkflowModel, Model);
    public string ResolveOutcomeSpecModel(string? projectModel = null) => Resolve(projectModel, OutcomeSpecModel, Model);

    public static GenerationModelOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new GenerationModelOptions();
        configuration.GetSection(SectionName).Bind(options);
        return options;
    }

    public static bool IsValid(GenerationModelOptions options) =>
        IsAllowedModelId(options.Model)
        && IsAllowedModelId(options.BlueprintModel)
        && IsAllowedModelId(options.WorkflowModel)
        && IsAllowedModelId(options.OutcomeSpecModel);

    private static bool IsAllowedModelId(string? modelId) =>
        string.IsNullOrWhiteSpace(modelId) || AllowedModelRegex.IsMatch(modelId.Trim());

    private static string Resolve(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate.Trim();
        }
        return DefaultModel;
    }
}
