namespace Agentweaver.Api.Workflows;

/// <summary>
/// Server-side seam that turns a natural-language description into a validated
/// <see cref="WorkflowDefinition"/> draft (Feature 015 US10, FR-056–FR-061). Implementations build the
/// generation prompt, invoke the GitHub Copilot model, validate the output against the same rules the
/// runtime loader enforces, and perform exactly one correction pass on invalid output before failing.
/// The result is a DRAFT — it is never persisted by the generator; the caller (endpoint/editor) decides
/// whether to save. Isolating the model call behind this seam keeps the prompt/validation/correction
/// logic exercisable without the live model.
/// </summary>
public interface IWorkflowGenerator
{
    /// <summary>
    /// Generates a WorkflowDefinition from a natural language description.
    /// Returns a draft — not yet persisted. Caller decides whether to save.
    /// </summary>
    Task<WorkflowGenerationResult> GenerateAsync(
        WorkflowGenerationRequest request,
        CancellationToken ct = default);
}

/// <summary>Inputs for a single workflow-generation pass.</summary>
public record WorkflowGenerationRequest(
    string Description,
    string? ProjectId = null,              // for role context
    IReadOnlyList<string>? TeamRoles = null, // cast roles available in the project
    string? UserId = null                  // submitting user — required for Copilot token resolution
);

/// <summary>The outcome of a successful generation: a validated draft plus the YAML used to render it.</summary>
public record WorkflowGenerationResult(
    WorkflowDefinition Workflow,
    string GeneratedYaml,   // the YAML string (for opening in editor)
    bool WasCorrected       // true if one correction pass was needed
);

/// <summary>
/// Thrown when generation fails: the model's output is invalid and the single correction pass
/// (FR-060) could not produce a schema-valid workflow. The message names the unresolved validation
/// problem so the endpoint can surface a structured 400 rather than a broken draft.
/// </summary>
public sealed class WorkflowGenerationException : Exception
{
    public WorkflowGenerationException(string message) : base(message) { }
    public WorkflowGenerationException(string message, Exception inner) : base(message, inner) { }
}
