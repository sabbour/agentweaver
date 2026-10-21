namespace Agentweaver.Abstractions;

/// <summary>Private single-cycle token response. Never persist or log this payload.</summary>
public sealed record IssueSyncCredentialResponse(IssueSyncGrantReceipt Grant, string AccessToken,
    DateTimeOffset ExpiresAt, bool IssueWriteGranted = false)
{
    public override string ToString() => nameof(IssueSyncCredentialResponse) + " [REDACTED]";
}
