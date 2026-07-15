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

    /// <summary>
    /// Default model for the outcome-spec reply (confirm-vs-revise) classifier. This runs on the
    /// synchronous <c>POST /steer</c> path, so it deliberately defaults to a small/fast model rather
    /// than the frontier generation model — it is a trivial binary intent classification, not a
    /// generation task, and must not add frontier-model latency to a steering request.
    /// </summary>
    public const string DefaultReplyClassificationModel = "gpt-5-mini";

    private static readonly Regex AllowedModelRegex =
        new("^(gpt|claude|o)[a-z0-9._-]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Shared model for generation flows unless a per-flow override is set.</summary>
    public string? Model { get; set; } = DefaultModel;

    public string? BlueprintModel { get; set; }
    public string? WorkflowModel { get; set; }
    public string? OutcomeSpecModel { get; set; }

    /// <summary>Override for the outcome-spec reply (confirm-vs-revise) classifier model.</summary>
    public string? ReplyClassificationModel { get; set; }

    public string ResolveBlueprintModel(string? projectModel = null) => Resolve(projectModel, BlueprintModel, Model);
    public string ResolveWorkflowModel(string? projectModel = null) => Resolve(projectModel, WorkflowModel, Model);
    public string ResolveOutcomeSpecModel(string? projectModel = null) => Resolve(projectModel, OutcomeSpecModel, Model);

    /// <summary>
    /// Resolves the reply-classifier model. Unlike the other flows this does NOT fall back to the
    /// shared frontier <see cref="Model"/>; when no override is configured it uses the lightweight
    /// <see cref="DefaultReplyClassificationModel"/> so classification stays fast/cheap by default.
    /// </summary>
    public string ResolveReplyClassificationModel(string? projectModel = null) =>
        Resolve(projectModel, ReplyClassificationModel, DefaultReplyClassificationModel);

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
        && IsAllowedModelId(options.OutcomeSpecModel)
        && IsAllowedModelId(options.ReplyClassificationModel);

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
