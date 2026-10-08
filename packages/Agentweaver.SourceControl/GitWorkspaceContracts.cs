using System.Collections.Immutable;
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
    DiffTooLarge,
    CaptureTooLarge,
    WorkspaceUnavailable
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

public sealed record GitWorkspaceCaptureRequest(
    PinnedProviderBinding Binding,
    SourceControlRepositoryIdentity Repository,
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
    string Path,
    Guid WorkspaceIncarnationId);

public sealed record GitWorkspaceDiff(
    string WorkspaceId,
    string BaseSha,
    string HeadSha,
    string Status,
    string Patch);

public sealed record GitWorkspaceCapturedFile(
    string Path,
    string Mode,
    string Sha256,
    long ByteLength,
    ImmutableArray<byte> Content);

public sealed record GitWorkspaceCapture(
    string WorkspaceId,
    string RunId,
    string RepositoryId,
    long ResourceGeneration,
    Guid WorkspaceIncarnationId,
    string BaseSha,
    string OutputTreeSha,
    string Patch,
    ImmutableArray<GitWorkspaceCapturedFile> Files);

internal interface IGitRepositoryRemote
{
    Uri GetCloneUri(SourceControlRepositoryIdentity repository);
}
