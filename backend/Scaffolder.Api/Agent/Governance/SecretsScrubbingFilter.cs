using System.Text.RegularExpressions;

namespace Scaffolder.Api.Agent.Governance;

/// <summary>
/// T047: Secrets-scrubbing filter.
///
/// Scans event payloads, OperationalRecord fields, and API response bodies for
/// secrets, credentials, and personal data patterns before persistence or
/// serialization, and redacts any matches (FR-026, SC-009).
///
/// Redaction replaces the matched value with [REDACTED] so the position of
/// the sensitive data is visible but the actual value is not stored.
///
/// Patterns detected (heuristic; extend as needed):
///   - Bearer tokens (Authorization header values)
///   - API keys / tokens (common patterns)
///   - Passwords in key=value pairs
///   - GitHub personal access tokens (ghp_, github_pat_)
///   - Azure connection strings
///   - AWS access key ids / secret keys
/// </summary>
public sealed partial class SecretsScrubbingFilter
{
    private readonly ILogger<SecretsScrubbingFilter> _logger;

    // Pre-compiled patterns for performance
    private static readonly Regex[] ScrubPatterns =
    [
        // GitHub PATs
        GitHubPatRegex(),
        // Azure connection strings
        AzureConnectionStringRegex(),
        // Generic API keys / tokens in JSON key:value pairs
        ApiKeyRegex(),
        // AWS access key IDs
        AwsAccessKeyRegex(),
        // AWS secret access keys
        AwsSecretKeyRegex(),
        // Bearer tokens
        BearerTokenRegex(),
        // Password fields
        PasswordFieldRegex()
    ];

    public SecretsScrubbingFilter(ILogger<SecretsScrubbingFilter> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Scans the input string and replaces any detected secrets/PII with [REDACTED].
    /// Returns the scrubbed string. Logs a warning if any secrets are found.
    /// </summary>
    public string Scrub(string input, string context = "")
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var result = input;
        var redactionCount = 0;

        foreach (var pattern in ScrubPatterns)
        {
            var replaced = pattern.Replace(result, m =>
            {
                redactionCount++;
                // Keep the key/prefix and replace only the value
                return m.Groups["prefix"].Success
                    ? m.Groups["prefix"].Value + "[REDACTED]"
                    : "[REDACTED]";
            });

            if (!ReferenceEquals(replaced, result))
            {
                result = replaced;
            }
        }

        if (redactionCount > 0)
        {
            _logger.LogWarning(
                "SecretsScrubbingFilter: redacted {Count} secret(s) from {Context}",
                redactionCount, string.IsNullOrEmpty(context) ? "payload" : context);
        }

        return result;
    }

    // ------------------------------------------------------------------
    // Source-generated compiled regex patterns (NFR-002 compliant — no emojis)
    // ------------------------------------------------------------------

    [GeneratedRegex(
        @"(?i)(ghp_|github_pat_|gho_|ghs_)[A-Za-z0-9_]{10,}",
        RegexOptions.Compiled)]
    private static partial Regex GitHubPatRegex();

    [GeneratedRegex(
        @"(?i)(?<prefix>AccountKey=)[A-Za-z0-9+/=]{20,}",
        RegexOptions.Compiled)]
    private static partial Regex AzureConnectionStringRegex();

    [GeneratedRegex(
        @"(?i)(?<prefix>(?:api[_-]?key|api[_-]?token|access[_-]?token|client[_-]?secret)[""']?\s*[:=]\s*[""']?)[A-Za-z0-9\-_]{16,}",
        RegexOptions.Compiled)]
    private static partial Regex ApiKeyRegex();

    [GeneratedRegex(
        @"\bAKIA[A-Z0-9]{16}\b",
        RegexOptions.Compiled)]
    private static partial Regex AwsAccessKeyRegex();

    [GeneratedRegex(
        @"(?i)(?<prefix>aws[_-]?secret[_-]?access[_-]?key[""']?\s*[:=]\s*[""']?)[A-Za-z0-9+/]{40}",
        RegexOptions.Compiled)]
    private static partial Regex AwsSecretKeyRegex();

    [GeneratedRegex(
        @"(?i)(?<prefix>Bearer\s+)[A-Za-z0-9\-._~+/]+=*",
        RegexOptions.Compiled)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(
        @"(?i)(?<prefix>(?:password|passwd|secret)[""']?\s*[:=]\s*[""']?)[^\s,;""']+",
        RegexOptions.Compiled)]
    private static partial Regex PasswordFieldRegex();
}
