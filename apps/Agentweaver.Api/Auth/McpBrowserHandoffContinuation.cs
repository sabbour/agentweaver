namespace Agentweaver.Api.Auth;

/// <summary>
/// An opaque, server-validated continuation for resuming an MCP browser handoff after Entra sign-in.
/// </summary>
internal static class McpBrowserHandoffContinuation
{
    private const string RepoPrefix = "mcp-repo-";
    private const string CopilotPrefix = "mcp-copilot-";

    public static string CreateRepo(string transactionId) =>
        IsTransactionId(transactionId)
            ? RepoPrefix + transactionId
            : throw new ArgumentException("Invalid MCP handoff transaction ID.", nameof(transactionId));

    public static bool TryParseRepo(string? value, out string transactionId)
    {
        transactionId = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(RepoPrefix, StringComparison.Ordinal))
            return false;

        var candidate = value[RepoPrefix.Length..];
        if (!IsTransactionId(candidate))
            return false;

        transactionId = candidate;
        return true;
    }

    public static string CreateCopilot(string transactionId) =>
        IsTransactionId(transactionId)
            ? CopilotPrefix + transactionId
            : throw new ArgumentException("Invalid MCP handoff transaction ID.", nameof(transactionId));

    public static bool TryParseCopilot(string? value, out string transactionId)
    {
        transactionId = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(CopilotPrefix, StringComparison.Ordinal))
            return false;

        var candidate = value[CopilotPrefix.Length..];
        if (!IsTransactionId(candidate))
            return false;

        transactionId = candidate;
        return true;
    }

    private static bool IsTransactionId(string value) =>
        value.Length == 43 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
