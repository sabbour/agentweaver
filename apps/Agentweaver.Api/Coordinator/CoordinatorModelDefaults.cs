namespace Agentweaver.Api.Coordinator;

/// <summary>
/// Shared model defaults for the coordinator's model resolution.
/// </summary>
/// <remarks>
/// <para>
/// Model IDs are free-text passthrough to the GitHub Copilot CLI (validated only by a permissive
/// <c>^(gpt|claude|o)[a-z0-9._-]*$</c> prefix regex), so there is intentionally no hardcoded allowlist.
/// The current Copilot CLI catalog callers may choose from includes:
/// </para>
/// <list type="bullet">
///   <item><description>OpenAI: gpt-5.6-sol, gpt-5.6-terra, gpt-5.6-luna, gpt-5.5, gpt-5.4, gpt-5.3-codex, gpt-5.4-mini, gpt-5-mini</description></item>
///   <item><description>Claude: claude-opus-4.8, claude-opus-4.7, claude-opus-4.6, claude-sonnet-5, claude-sonnet-4.6, claude-sonnet-4.5, claude-haiku-4.5</description></item>
/// </list>
/// <para>
/// Model selection precedence (see <see cref="CoordinatorOrchestratorExecutor"/> SelectModel):
/// explicit run/project pin wins for every subtask (global override); else the per-role default_model
/// (the coordinator "picks per task" default); else <see cref="DefaultCopilotModel"/> as the last resort.
/// </para>
/// </remarks>
public static class CoordinatorModelDefaults
{
    /// <summary>
    /// Last-resort default Copilot model when neither an explicit pin, a role default, nor the
    /// <c>Providers:GitHubCopilot:Model</c> config value is supplied. A general-purpose all-rounder
    /// (matches the codebase-wide default in dev appsettings, CastingService, and most role defaults).
    /// </summary>
    public const string DefaultCopilotModel = "claude-sonnet-4.6";
}
