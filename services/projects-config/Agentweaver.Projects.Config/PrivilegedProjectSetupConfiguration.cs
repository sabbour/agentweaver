using Microsoft.Extensions.Configuration;

namespace Agentweaver.Projects.Config;

internal sealed record PrivilegedProjectSetupApproval(
    Uri Authority,
    string Audience,
    string ApprovedSubject,
    string ApprovedTenantId)
{
    public static PrivilegedProjectSetupApproval Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var authentication = configuration.GetSection("ProjectsConfig:Authentication");
        var authority = RequireHttpsUri(
            authentication["Authority"],
            "ProjectsConfig:Authentication:Authority");
        var audience = authentication["Audience"];
        if (string.IsNullOrWhiteSpace(audience))
            throw new InvalidOperationException(
                "Missing required configuration 'ProjectsConfig:Authentication:Audience'.");

        var setup = configuration.GetSection("ProjectsConfig:PrivilegedSetup");
        var approvedIssuer = RequireHttpsUri(
            setup["ApprovedIssuer"],
            "ProjectsConfig:PrivilegedSetup:ApprovedIssuer");
        var approvedSubject = RequireIdentifier(
            setup["ApprovedSubject"],
            "ProjectsConfig:PrivilegedSetup:ApprovedSubject");
        var approvedTenantId = RequireIdentifier(
            setup["ApprovedTenantId"],
            "ProjectsConfig:PrivilegedSetup:ApprovedTenantId");
        if (!string.Equals(authority.AbsoluteUri, approvedIssuer.AbsoluteUri, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The privileged setup issuer must match the configured Projects & Config authentication authority.");

        return new PrivilegedProjectSetupApproval(
            authority,
            audience,
            approvedSubject,
            approvedTenantId);
    }

    public void RequireApprovedCaller(ProjectAuthorizationContext caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!string.Equals(caller.Issuer, Authority.AbsoluteUri, StringComparison.Ordinal) ||
            !string.Equals(caller.ActorId, ApprovedSubject, StringComparison.Ordinal) ||
            !string.Equals(caller.TenantId, ApprovedTenantId, StringComparison.Ordinal))
            throw ProjectConfigException.Forbidden();
    }

    private static Uri RequireHttpsUri(string? value, string setting)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException(
                $"'{setting}' requires an absolute HTTPS URI without user information, query, or fragment.");
        return uri;
    }

    private static string RequireIdentifier(string? value, string setting)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            !value.All(character => char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or '-' or ':'))
            throw new InvalidOperationException(
                $"'{setting}' requires a nonempty opaque identifier.");
        return value;
    }
}
