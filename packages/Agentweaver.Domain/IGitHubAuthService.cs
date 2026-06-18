namespace Agentweaver.Domain;

public sealed record GitHubDeviceFlowStart(
    string UserCode,
    string VerificationUri,
    int ExpiresIn,
    int Interval);

public enum GitHubDeviceFlowPollResult { Pending, Success, Expired, Denied }

public sealed record GitHubDeviceFlowPollResponse(
    GitHubDeviceFlowPollResult Result,
    string? Login);

public interface IGitHubAuthService
{
    Task<GitHubDeviceFlowStart> StartDeviceFlowAsync(GitHubTokenScope scope, CancellationToken ct = default);
    Task<GitHubDeviceFlowPollResponse> PollDeviceFlowAsync(GitHubTokenScope scope, CancellationToken ct = default);
    Task SignOutAsync(GitHubTokenScope scope, CancellationToken ct = default);
}
