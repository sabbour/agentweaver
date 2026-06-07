namespace Scaffolder.Api.Agent.ModelSources;

/// <summary>
/// T037: Adapter interface for the two supported model sources.
///
/// Implementations:
///   - <see cref="CopilotSdkAdapter"/> — GitHub Copilot SDK
///   - <see cref="MicrosoftFoundryAdapter"/> — Microsoft Azure AI Foundry
///
/// Every model output MUST pass through <see cref="ApplyContentSafetyCheckAsync"/>
/// before being written to the event log or relayed to any SSE subscriber (FR-025).
/// </summary>
public interface IModelSourceAdapter
{
    /// <summary>
    /// Submits a prompt to the model and returns the model's text response.
    /// Throws <see cref="InvalidOperationException"/> on provider-level errors.
    /// </summary>
    Task<string> SubmitPromptAsync(string prompt, CancellationToken ct = default);

    /// <summary>
    /// Runs a content-safety check on the given model output.
    /// Returns <c>null</c> if the content is safe to relay.
    /// Returns a non-null failure reason string if the content must be withheld
    /// (the caller MUST NOT write it to the event log or SSE stream and MUST
    /// transition the run to a failed terminal state per FR-025 / SC-008).
    /// </summary>
    Task<string?> ApplyContentSafetyCheckAsync(string content, CancellationToken ct = default);
}
