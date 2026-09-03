using Agentweaver.Api.Auth;

namespace Agentweaver.Api.Security;

public sealed class CallerContext
{
    public required string User { get; init; }
    public string? EntraObjectId { get; init; }
    public string? EntraTenantId { get; init; }
    public IReadOnlyList<string> PlatformRoles { get; init; } = [];
    public IReadOnlyList<string> RawPlatformRoles { get; init; } = [];
    public string? PrimaryPlatformRole { get; init; }
    public string? GitHubLogin { get; init; }
    public string? DisplayName { get; init; }
    public string? Email { get; init; }
    public string? AuthenticationScheme { get; init; }
    public bool IsInternalService { get; init; }
    public bool IsOAuthJwt =>
        string.Equals(
            AuthenticationScheme,
            AgentweaverAuthenticationSchemes.BrokerBearer,
            StringComparison.Ordinal);
    public string? Org { get; init; }

    public bool Owns(string? ownerUser) =>
        ownerUser is not null &&
        (string.Equals(User, ownerUser, StringComparison.Ordinal) ||
         (GitHubLogin is not null && string.Equals(GitHubLogin, ownerUser, StringComparison.Ordinal)));
}
