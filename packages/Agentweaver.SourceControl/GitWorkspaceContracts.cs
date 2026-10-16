using System.Collections.Immutable;
using Agentweaver.Abstractions;

namespace Agentweaver.SourceControl;

/// <summary>Stable failure categories returned by Git workspace operations.</summary>
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

/// <summary>Git workspace failure with an optional underlying process exit code.</summary>
public sealed class GitWorkspaceException(
    GitWorkspaceFailureCode code,
    string message,
    int? exitCode = null,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>Gets the stable workspace failure category.</summary>
    public GitWorkspaceFailureCode Code { get; } = code;
    /// <summary>Gets the Git process exit code when one is available.</summary>
    public int? ExitCode { get; } = exitCode;
}

/// <summary>Request to create or recover a run-owned Git workspace from a fixed base commit.</summary>
public sealed record GitWorkspaceRequest(
    SourceControlOperationContext Context,
    string WorkspaceId,
    string BaseSha,
    string BranchName);

/// <summary>Request to capture an existing workspace using a pinned Source Control provider binding.</summary>
public sealed record GitWorkspaceCaptureRequest(
    PinnedProviderBinding Binding,
    SourceControlRepositoryIdentity Repository,
    string WorkspaceId,
    string BaseSha,
    string BranchName);

/// <summary>Identity and filesystem location of one fenced Git workspace incarnation.</summary>
public sealed record GitWorkspace(
    string WorkspaceId,
    string RunId,
    string RepositoryId,
    long ResourceGeneration,
    string BaseSha,
    string BranchName,
    string Path,
    Guid WorkspaceIncarnationId);

/// <summary>Validated branch state and patch produced by a workspace diff.</summary>
public sealed record GitWorkspaceDiff(
    string WorkspaceId,
    string BaseSha,
    string HeadSha,
    string Status,
    string Patch);

/// <summary>One captured file with its repository mode, digest, length, and bytes.</summary>
public sealed record GitWorkspaceCapturedFile(
    string Path,
    string Mode,
    string Sha256,
    long ByteLength,
    ImmutableArray<byte> Content);

/// <summary>Complete run-bound snapshot of workspace files and their resulting tree and patch.</summary>
public sealed record GitWorkspaceCapture(
    string WorkspaceId,
    string RunId,
    string RepositoryId,
    long ResourceGeneration,
    Guid WorkspaceIncarnationId,
    string BaseSha,
    string OutputTreeSha,
    string Patch,
    ImmutableArray<GitWorkspaceCapturedFile> Files,
    string BranchName);

/// <summary>Source for a remote clone URI associated with a validated repository identity.</summary>
internal interface IGitRepositoryRemote
{
    /// <summary>Returns the clone URI for the requested repository.</summary>
    Uri GetCloneUri(SourceControlRepositoryIdentity repository);
}
