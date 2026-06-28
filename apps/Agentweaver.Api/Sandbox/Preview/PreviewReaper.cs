using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Agentweaver.Api.Sandbox.Preview;

/// <summary>Why the reaper would (or would not) delete a preview.</summary>
public enum PreviewReapReason
{
    /// <summary>Preview is healthy and should be kept.</summary>
    Alive,

    /// <summary>Past its idle expiry (<c>preview-expires-at</c>); not kept alive recently.</summary>
    ExpiredIdle,

    /// <summary>Past its hard lifetime cap (<c>preview-max-until</c>).</summary>
    ExpiredMax,

    /// <summary>The backing sandbox pod no longer exists.</summary>
    Orphan,
}

/// <summary>
/// Pure decision logic for the preview reaper and shared label helpers. Kept free of any
/// Kubernetes client dependency so it can be unit-tested without a live cluster — the reaper
/// reads annotations off each HTTPRoute and feeds them here together with a pod-exists flag.
/// </summary>
public static class PreviewReaper
{
    /// <summary>Annotation/label keys (read by both replicas — annotations are the source of truth).</summary>
    public const string AnnotationExpiresAt = "agentweaver.dev/preview-expires-at";
    public const string AnnotationMaxUntil = "agentweaver.dev/preview-max-until";
    public const string AnnotationRun = "agentweaver.dev/preview-run";
    public const string AnnotationToken = "agentweaver.dev/preview-token";
    public const string AnnotationOwner = "agentweaver.dev/preview-owner";

    public const string LabelPartOf = "app.kubernetes.io/part-of";
    public const string LabelPartOfValue = "agentweaver";
    public const string LabelToken = "agentweaver.dev/preview-token";
    public const string LabelRun = "agentweaver.dev/preview-run";

    /// <summary>Pod label used to wire the per-run Service selector to the bound sandbox pod.</summary>
    public const string PodPreviewRunLabel = "agentweaver.dev/preview-run";

    private static readonly Regex InvalidLabelChars = new(
        "[^a-z0-9-]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Decides whether a preview should be reaped. Pure: depends only on the supplied clock,
    /// the two expiry timestamps (parsed from annotations, may be <c>null</c> if missing/invalid),
    /// and whether the backing pod still exists. Replica-safe because every input is derived from
    /// cluster state, never from in-memory bookkeeping.
    /// </summary>
    public static PreviewReapReason Decide(
        DateTimeOffset now,
        DateTimeOffset? expiresAt,
        DateTimeOffset? maxUntil,
        bool podExists)
    {
        if (!podExists)
            return PreviewReapReason.Orphan;
        if (maxUntil is { } max && now > max)
            return PreviewReapReason.ExpiredMax;
        if (expiresAt is { } idle && now > idle)
            return PreviewReapReason.ExpiredIdle;
        return PreviewReapReason.Alive;
    }

    /// <summary>Convenience: <c>true</c> when <see cref="Decide"/> would delete the preview.</summary>
    public static bool ShouldReap(
        DateTimeOffset now, DateTimeOffset? expiresAt, DateTimeOffset? maxUntil, bool podExists) =>
        Decide(now, expiresAt, maxUntil, podExists) != PreviewReapReason.Alive;

    /// <summary>
    /// Parses an RFC3339/ISO-8601 timestamp annotation. Returns <c>null</c> (treated as
    /// "no constraint") when the value is missing or unparseable, so a malformed annotation
    /// never causes a spurious reap on the idle/max axis (orphan check still applies).
    /// </summary>
    public static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts)
            ? ts
            : null;

    /// <summary>
    /// Sanitizes an arbitrary run ID into a valid Kubernetes label value: lowercase,
    /// invalid characters replaced with <c>-</c>, trimmed to a leading/trailing alphanumeric,
    /// max 63 chars. Empty input maps to <c>"run"</c>.
    /// </summary>
    public static string SanitizeLabel(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return "run";

        var lowered = runId.ToLowerInvariant();
        var replaced = InvalidLabelChars.Replace(lowered, "-");
        var trimmed = replaced.Trim('-');
        if (trimmed.Length > 63)
            trimmed = trimmed[..63].Trim('-');

        return string.IsNullOrEmpty(trimmed) ? "run" : trimmed;
    }

    /// <summary>
    /// Builds a DNS-1123-safe Service name from the token: <c>preview-{token}</c>, truncated
    /// to 63 chars without a trailing hyphen.
    /// </summary>
    public static string ServiceName(string token)
    {
        var name = $"preview-{token}";
        if (name.Length > 63)
            name = name[..63].TrimEnd('-');
        return name;
    }
}
