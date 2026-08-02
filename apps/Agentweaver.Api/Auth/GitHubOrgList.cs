using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Shared parser for the <c>Auth:GitHub:AllowedOrg</c> configuration value.
///
/// Under the team-membership authz model this key accepts a mixed delimited list where each
/// entry is one of:
///   • <c>*</c>              — global wildcard (any authenticated GitHub identity).
///   • <c>org</c>            — bare org (satisfied by any-form org membership).
///   • <c>org/*</c>          — explicit wildcard, canonicalized to bare org (identical semantics).
///   • <c>org/team-slug</c>  — team-scoped rule (satisfied only by that team's membership).
///
/// A caller is authorized if they satisfy ANY one entity in the parsed list (pure OR).
///
/// Parsing rule (single source of truth — reused by the authorization service, the org-authorization
/// middleware, and the API-key middleware so the split logic is never duplicated):
///   • split on both ',' and ';'
///   • <c>.Trim()</c> each entry
///   • drop empty entries
///   • split each entry on the first '/' into (org, team) — trailing team of "*" or empty is treated as bare org
///   • defensively slugify a team part that contains a space or uppercase letter (GitHub team slugs are
///     lowercase-hyphenated; a display-name like "AKS PM" is slugified to "aks-pm"), and warn
///   • de-duplicate case-insensitively on the CANONICAL rule string, preserving first-seen order
///
/// Empty/whitespace config => empty list (fail-closed: the middleware treats an empty list as
/// NotConfigured and blocks every non-exempt request).
/// </summary>
public static class GitHubOrgList
{
    private static readonly char[] Separators = [',', ';'];

    /// <summary>
    /// Parses the delimited allowed-org config value into an ordered, de-duplicated list of
    /// entities. This is the primary parser used by the authorization service.
    /// </summary>
    public static IReadOnlyList<AllowedGitHubEntity> ParseEntities(string? configuredValue, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
            return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AllowedGitHubEntity>();

        foreach (var raw in configuredValue.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
                continue;

            var slashIndex = trimmed.IndexOf('/');
            string org;
            string? teamSlug;

            if (slashIndex < 0)
            {
                org = trimmed;
                teamSlug = null;
            }
            else
            {
                org = trimmed[..slashIndex].Trim();
                var team = trimmed[(slashIndex + 1)..].Trim();

                if (org.Length == 0)
                {
                    logger?.LogWarning(
                        "Auth:GitHub:AllowedOrg entry '{Entry}' has no org before the '/'; skipping.",
                        trimmed);
                    continue;
                }

                if (team.Length == 0 || team == "*")
                {
                    // `org/*` and `org/` are canonicalized to bare-org (any team within the org
                    // means "any org member" — same as a plain org rule).
                    teamSlug = null;
                }
                else
                {
                    teamSlug = SlugifyTeam(team, org, logger);
                    if (teamSlug is null)
                    {
                        logger?.LogWarning(
                            "Auth:GitHub:AllowedOrg entry '{Entry}' has an unusable team part after slugification; skipping.",
                            trimmed);
                        continue;
                    }
                }
            }

            var entity = new AllowedGitHubEntity(org, teamSlug);
            if (seen.Add(entity.RuleString))
                result.Add(entity);
        }

        return result;
    }

    /// <summary>
    /// Legacy shape used by callers that only need the distinct org NAMES (order preserved) —
    /// specifically the internal-API-key path in <c>ApiKeyAuthMiddleware</c>. Team-scoped entries
    /// still contribute their org name; wildcards and bare orgs are unchanged.
    /// </summary>
    public static IReadOnlyList<string> Parse(string? configuredValue)
    {
        var entities = ParseEntities(configuredValue);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var e in entities)
        {
            if (seen.Add(e.Org))
                result.Add(e.Org);
        }
        return result;
    }

    /// <summary>
    /// Defensive team-slug normalization: GitHub team slugs are lowercase and hyphen-separated.
    /// If the operator configured a display name like "AKS PM" we slugify it (lowercase + spaces
    /// to hyphens) and log a warning so the mis-configuration is visible in logs but not silently
    /// fatal. Returns null only if the result is empty (which should be unreachable).
    /// </summary>
    private static string? SlugifyTeam(string rawTeam, string org, ILogger? logger)
    {
        var needsSlugification = false;
        var buf = new System.Text.StringBuilder(rawTeam.Length);
        foreach (var c in rawTeam)
        {
            if (c == ' ')
            {
                needsSlugification = true;
                buf.Append('-');
            }
            else if (c is >= 'A' and <= 'Z')
            {
                needsSlugification = true;
                buf.Append((char)(c + 32));
            }
            else
            {
                buf.Append(c);
            }
        }
        var slug = buf.ToString();
        if (slug.Length == 0)
            return null;

        if (needsSlugification)
        {
            logger?.LogWarning(
                "Auth:GitHub:AllowedOrg team entry '{Org}/{Raw}' was not in GitHub slug form; using '{Org}/{Slug}'. " +
                "Please update the config to use the lowercase-hyphenated slug directly.",
                org, rawTeam, org, slug);
        }
        return slug;
    }
}
