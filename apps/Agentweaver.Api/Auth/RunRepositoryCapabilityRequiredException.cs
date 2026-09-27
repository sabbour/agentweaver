namespace Agentweaver.Api.Auth;

internal sealed class RunRepositoryCapabilityRequiredException()
    : Exception("The project's run-bound repository capability is unavailable. Reconnect the GitHub Repo App and grant this repository before starting a run.")
{
    public const string ErrorCode = "repo_app_repository_grant_required";
}
