namespace Agentweaver.Api.Runs;

/// <summary>
/// Thrown when a caller-supplied value on run submission fails a precondition
/// (e.g., repository path is not a git repository or the originating branch
/// does not exist). Maps to HTTP 400 Bad Request at the API boundary.
/// </summary>
public sealed class RunSubmissionValidationException : Exception
{
    public RunSubmissionValidationException(string message)
        : base(message) { }

    public RunSubmissionValidationException(string message, Exception innerException)
        : base(message, innerException) { }
}
