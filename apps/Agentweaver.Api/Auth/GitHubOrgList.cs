namespace Agentweaver.Api.Auth;

/// <summary>
/// Shared parser for the <c>Auth:GitHub:AllowedOrg</c> configuration value, which now accepts a
/// delimited LIST of GitHub orgs (a user is authorized if they are a member of ANY listed org).
///
/// Parsing rule (single source of truth — reused by the authorization service, the org-authorization
/// middleware, and the API-key middleware so the split logic is never duplicated):
///   • split on both ',' and ';'
///   • <c>.Trim()</c> each entry
///   • drop empty entries
///   • de-duplicate case-insensitively, preserving first-seen order
///
/// Empty/whitespace config => empty list (fail-closed: the middleware treats an empty list as
/// NotConfigured and blocks every non-exempt request).
/// </summary>
public static class GitHubOrgList
{
    private static readonly char[] Separators = [',', ';'];

    /// <summary>Parses the delimited allowed-org config value into an ordered, de-duplicated list.</summary>
    public static IReadOnlyList<string> Parse(string? configuredValue)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
            return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var raw in configuredValue.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var org = raw.Trim();
            if (org.Length == 0)
                continue;
            if (seen.Add(org))
                result.Add(org);
        }

        return result;
    }
}
