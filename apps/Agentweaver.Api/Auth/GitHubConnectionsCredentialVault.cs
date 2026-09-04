using System.Text.Json;

namespace Agentweaver.Api.Auth;

/// <summary>Opaque, vault-owned locator for reserved GitHub connections credential material.</summary>
internal sealed record GitHubConnectionsCredentialLocator
{
    private GitHubConnectionsCredentialLocator(string key) => Key = key;

    internal string Key { get; }

    internal static GitHubConnectionsCredentialLocator ForRepoAppUser(string credentialReference) =>
        Create(credentialReference, "repo-app-user-credential-");

    internal static GitHubConnectionsCredentialLocator ForCopilotProject(string credentialReference) =>
        Create(credentialReference, "copilot-app-project-");

    internal static GitHubConnectionsCredentialLocator ForCopilotBinding(string credentialReference)
    {
        if (string.IsNullOrWhiteSpace(credentialReference) ||
            (!credentialReference.StartsWith("copilot-app-project-", StringComparison.Ordinal) &&
             !credentialReference.StartsWith("copilot-app-platform-default-", StringComparison.Ordinal) &&
             !credentialReference.StartsWith("copilot-app-user-", StringComparison.Ordinal)))
            throw new ArgumentException("Credential reference is not a reserved GitHub connections locator.", nameof(credentialReference));
        return new(credentialReference);
    }

    private static GitHubConnectionsCredentialLocator Create(string credentialReference, string requiredPrefix)
    {
        if (string.IsNullOrWhiteSpace(credentialReference) ||
            !credentialReference.StartsWith(requiredPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Credential reference is not a reserved GitHub connections locator.", nameof(credentialReference));
        return new(credentialReference);
    }
}

internal interface IGitHubConnectionsCredentialVault
{
    Task<SecretGetResult> ReadCurrentAsync(GitHubConnectionsCredentialLocator locator, CancellationToken ct = default);
    Task WriteAsync(GitHubConnectionsCredentialLocator locator, string value, CancellationToken ct = default);
    Task TombstoneAndDeleteAsync(GitHubConnectionsCredentialLocator locator, CancellationToken ct = default);
}

/// <summary>
/// The sole GitHub connections authority allowed to bridge reserved credential locators to generic secret
/// storage. Reads are current-version only and tombstones cannot be treated as a credential.
/// </summary>
internal sealed class GitHubConnectionsCredentialVault(ISecretStore secretStore) : IGitHubConnectionsCredentialVault
{
    private const string Tombstone = """{"status":"revoked"}""";

    public async Task<SecretGetResult> ReadCurrentAsync(GitHubConnectionsCredentialLocator locator, CancellationToken ct = default)
    {
        var result = await secretStore.GetSecretAsync(locator.Key, ct).ConfigureAwait(false);
        return !result.Found || IsTombstone(result.Value) ? SecretGetResult.NotFound : result;
    }

    public async Task WriteAsync(GitHubConnectionsCredentialLocator locator, string value, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(value) || IsTombstone(value))
            throw new ArgumentException("The vault cannot write empty or tombstone credential material.", nameof(value));
        await secretStore.SetSecretAsync(locator.Key, value, ct: ct).ConfigureAwait(false);
    }

    public async Task TombstoneAndDeleteAsync(GitHubConnectionsCredentialLocator locator, CancellationToken ct = default)
    {
        await secretStore.SetSecretAsync(locator.Key, Tombstone, ct: ct).ConfigureAwait(false);
        await secretStore.DeleteSecretAsync(locator.Key, ct).ConfigureAwait(false);
    }

    private static bool IsTombstone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("status", out var status) &&
                   status.ValueKind == JsonValueKind.String &&
                   string.Equals(status.GetString(), "revoked", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }
}
