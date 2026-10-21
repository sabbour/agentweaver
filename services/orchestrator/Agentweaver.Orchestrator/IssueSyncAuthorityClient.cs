using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Orchestrator;

internal interface IIssueSyncAuthority
{
    Task<IssueSyncCredentialResponse> MintAsync(Guid grantId, CancellationToken ct);
    Task<IssueSyncGrantReceipt> ValidateAsync(Guid grantId, CancellationToken ct);
}

internal sealed class IssueSyncAuthorityClient(IServiceProvider services) : IIssueSyncAuthority
{
    internal const string Channel = "IssueSyncIdentity";

    public Task<IssueSyncCredentialResponse> MintAsync(Guid grantId, CancellationToken ct) =>
        IssueSyncServiceTransport.SendAsync<IssueSyncCredentialResponse>(services, Channel,
            $"/internal/issue-sync/grants/{grantId:D}/token", new { }, ct);

    public Task<IssueSyncGrantReceipt> ValidateAsync(Guid grantId, CancellationToken ct) =>
        IssueSyncServiceTransport.SendAsync<IssueSyncGrantReceipt>(services, Channel,
            $"/internal/issue-sync/grants/{grantId:D}/validate", new { }, ct);
}
