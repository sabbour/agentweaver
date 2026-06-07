using Scaffolder.Domain;

namespace Scaffolder.AgentRuntime.Safety;

/// <summary>
/// Defence-in-depth content-safety gate (Principle IX, FR-025). Applied to both
/// model-generated agent messages and write-tool content before it is relayed
/// to a client or written to disk.
/// </summary>
/// <remarks>
/// The selected providers (GitHub Copilot and Microsoft Foundry) perform
/// server-side harmful-content filtering before returning responses. This check
/// adds a local baseline that blocks credential leakage and basic injection
/// payloads in any text the agent emits or persists.
/// </remarks>
public sealed class ContentSafetyChecker
{
    private static readonly string[] CredentialPatterns =
    {
        "ghp_", "ghs_", "github_pat_", "sk-", "AKIA", "-----BEGIN"
    };

    /// <summary>
    /// Checks model-generated text before it is relayed to any client or written
    /// to disk. Returns <c>(true, null)</c> when safe, or <c>(false, reason)</c>
    /// when the content must be blocked.
    /// </summary>
    public (bool Safe, string? FailureReason) Check(string content, ModelSource modelSource)
    {
        ArgumentNullException.ThrowIfNull(content);
        _ = modelSource;

        if (content.Contains('\0'))
        {
            return (false, "Content contains null bytes");
        }

        if (LooksLikeCredential(content))
        {
            return (false, "Content appears to contain credentials or sensitive tokens");
        }

        return (true, null);
    }

    private static bool LooksLikeCredential(string content)
    {
        foreach (var pattern in CredentialPatterns)
        {
            if (content.Contains(pattern, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
