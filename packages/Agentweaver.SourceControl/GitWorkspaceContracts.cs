using Agentweaver.Abstractions;

namespace Agentweaver.SourceControl;

public enum GitWorkspaceFailureCode
{
    InvalidRequest,
    CapabilityUnavailable,
    PathConflict,
    CorruptWorkspace,
    GitUnavailable,
    GitFailed,
    DiffTooLarge
}

public sealed class GitWorkspaceException(
    GitWorkspaceFailureCode code,
    string message,
    int? exitCode = null,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public GitWorkspaceFailureCode Code { get; } = code;
    public int? ExitCode { get; } = exitCode;
}

public sealed record GitWorkspaceRequest(
    SourceControlOperationContext Context,
    string WorkspaceId,
    string BaseSha,
    string BranchName);

public sealed record GitWorkspace(
    string WorkspaceId,
    string RunId,
    string RepositoryId,
    long ResourceGeneration,
    string BaseSha,
    string BranchName,
    string Path);

public sealed record GitWorkspaceDiff(
    string WorkspaceId,
    string BaseSha,
    string HeadSha,
    string Status,
    string Patch);

internal interface IGitRepositoryRemote
{
    Uri GetCloneUri(SourceControlRepositoryIdentity repository);
}
