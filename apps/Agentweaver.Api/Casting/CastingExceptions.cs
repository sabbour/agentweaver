namespace Agentweaver.Api.Casting;

/// <summary>
/// Raised when a model-assisted casting run produces output that cannot be
/// parsed into a valid role selection. No proposal is stored and no files are
/// written. Surfaces as HTTP 409 with code "model_run_failed".
/// </summary>
public sealed class ModelRunFailedException : Exception
{
    public ModelRunFailedException(string reason) : base(reason) { }
}
