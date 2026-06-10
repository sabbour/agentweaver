using System.Text.RegularExpressions;

namespace Scaffolder.SandboxExec;

/// <summary>
/// Redacts secrets and optionally PII from sandbox command output before
/// the content is written to logs or event streams. Thread-safe and stateless
/// after construction.
/// </summary>
public sealed class SandboxOutputRedactor
{
    private static readonly Regex[] DefaultSecretPatterns =
    [
        // Authorization / Bearer tokens
        new Regex(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)),
        // AWS access key IDs
        new Regex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled, TimeSpan.FromSeconds(2)),
        // GitHub personal access tokens (classic and fine-grained)
        new Regex(@"\bghp_[A-Za-z0-9]{36}\b", RegexOptions.Compiled, TimeSpan.FromSeconds(2)),
        new Regex(@"\bgho_[A-Za-z0-9]{36}\b", RegexOptions.Compiled, TimeSpan.FromSeconds(2)),
        new Regex(@"\bghs_[A-Za-z0-9]{36}\b", RegexOptions.Compiled, TimeSpan.FromSeconds(2)),
        new Regex(@"\bghx_[A-Za-z0-9]{36}\b", RegexOptions.Compiled, TimeSpan.FromSeconds(2)),
        new Regex(@"\bgithub_pat_[A-Za-z0-9_]{82}\b", RegexOptions.Compiled, TimeSpan.FromSeconds(2)),
        // PEM private key headers
        new Regex(@"-----BEGIN [A-Z ]+ PRIVATE KEY-----",
            RegexOptions.Compiled, TimeSpan.FromSeconds(2)),
        // Connection string password fragments
        new Regex(@"(?:password|passwd|pwd)\s*=\s*[^\s;&,""']+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)),
        // Generic API key / secret assignments (key=value patterns)
        new Regex(@"(?:api[_-]?key|apikey|secret[_-]?key|access[_-]?token)\s*[=:]\s*[^\s;&,""']+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)),
    ];

    private static readonly Regex EmailPattern = new(
        @"\b[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));

    private static readonly Regex Ipv4Pattern = new(
        @"\b(?:(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\.){3}(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\b",
        RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    // Matches full-form IPv6 (8 groups of 4 hex digits separated by colons).
    private static readonly Regex Ipv6Pattern = new(
        @"\b(?:[0-9a-fA-F]{1,4}:){7}[0-9a-fA-F]{1,4}\b",
        RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    private readonly IReadOnlyList<Regex> _patterns;
    private readonly bool _redactPii;
    private readonly TimeSpan _matchTimeout = TimeSpan.FromSeconds(2);

    public static SandboxOutputRedactor Default { get; } = CreateDefault();

    public SandboxOutputRedactor(IReadOnlyList<Regex>? additionalPatterns, bool redactPii = true)
    {
        _redactPii = redactPii;
        if (additionalPatterns is { Count: > 0 })
        {
            var combined = new List<Regex>(DefaultSecretPatterns.Length + additionalPatterns.Count);
            combined.AddRange(DefaultSecretPatterns);
            combined.AddRange(additionalPatterns);
            _patterns = combined;
        }
        else
        {
            _patterns = DefaultSecretPatterns;
        }
    }

    public static SandboxOutputRedactor CreateDefault(bool redactPii = true) =>
        new(null, redactPii);

    public string Redact(string data)
    {
        var result = data;
        foreach (var pattern in _patterns)
        {
            try { result = pattern.Replace(result, "[REDACTED]"); }
            catch (RegexMatchTimeoutException) { /* pattern timed out — skip, don't hang */ }
        }

        if (_redactPii)
        {
            try { result = EmailPattern.Replace(result, "[REDACTED]"); }
            catch (RegexMatchTimeoutException) { }
            try { result = Ipv4Pattern.Replace(result, "[REDACTED]"); }
            catch (RegexMatchTimeoutException) { }
            try { result = Ipv6Pattern.Replace(result, "[REDACTED]"); }
            catch (RegexMatchTimeoutException) { }
        }

        return result;
    }
}
