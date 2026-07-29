namespace Agentweaver.Api.Auth;

/// <summary>
/// A single parsed rule from the <c>Auth:GitHub:AllowedOrg</c> config value under the new
/// mixed-list model. A rule is one of:
///
///   • <c>org</c>            — <see cref="TeamSlug"/> is null; bare-org membership satisfies.
///   • <c>org/*</c>          — parser canonicalizes to bare-org (<see cref="TeamSlug"/> null).
///   • <c>org/team-slug</c>  — <see cref="TeamSlug"/> is the (slugified) team slug; only that team's membership satisfies.
///
/// A caller is authorized if they satisfy ANY one entity in the configured list.
///
/// <see cref="RuleString"/> is the canonical, lowercased serialized form of the entity used
/// as both the JWT <c>org</c> claim value (so the middleware fast-path can re-verify a
/// team-scoped rule without hitting GitHub) and the string persisted alongside refresh
/// tokens. Case-insensitive comparisons throughout the authz stack use it.
/// </summary>
public sealed record AllowedGitHubEntity(string Org, string? TeamSlug)
{
    /// <summary>True when this rule requires membership of a specific team (not just the org).</summary>
    public bool IsTeamScoped => TeamSlug is not null;

    /// <summary>
    /// Canonical serialized form:
    ///   • bare org  → <c>"org"</c>          (lowercased)
    ///   • team rule → <c>"org/team-slug"</c> (both parts lowercased)
    ///
    /// This is what we stamp into the JWT <c>org</c> claim, persist in <c>McpRefreshToken.Org</c>,
    /// and compare (Ordinal, already-lowercase) in the middleware fast-path.
    /// </summary>
    public string RuleString => TeamSlug is null
        ? Org.ToLowerInvariant()
        : $"{Org.ToLowerInvariant()}/{TeamSlug.ToLowerInvariant()}";

    /// <summary>Case-insensitive equality on <see cref="RuleString"/>.</summary>
    public bool Matches(AllowedGitHubEntity other) =>
        string.Equals(RuleString, other.RuleString, StringComparison.OrdinalIgnoreCase);
}
