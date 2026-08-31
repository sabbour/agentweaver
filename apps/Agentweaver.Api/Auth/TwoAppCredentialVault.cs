using System.Text.Json;

namespace Agentweaver.Api.Auth;

/// <summary>Opaque, vault-owned locator for reserved two-App credential material.</summary>
internal sealed record TwoAppCredentialLocator
{
    private TwoAppCredentialLocator(string key) => Key = key;

    internal string Key { get; }

    internal static TwoAppCredentialLocator ForRepoAppUser(string credentialReference) =>
        Create(credentialReference, "repo-app-user-credential-");

    internal static TwoAppCredentialLocator ForCopilotProject(string credentialReference) =>
        Create(credentialReference, "copilot-app-project-");

    private static TwoAppCredentialLocator Create(string credentialReference, string requiredPrefix)
    {
        if (string.IsNullOrWhiteSpace(credentialReference) ||
            !credentialReference.StartsWith(requiredPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Credential reference is not a reserved two-App locator.", nameof(credentialReference));
        return new(credentialReference);
    }
}

internal interface ITwoAppCredentialVault
{
    Task<SecretGetResult> ReadCurrentAsync(TwoAppCredentialLocator locator, CancellationToken ct = default);
    Task WriteAsync(TwoAppCredentialLocator locator, string value, CancellationToken ct = default);
    Task TombstoneAndDeleteAsync(TwoAppCredentialLocator locator, CancellationToken ct = default);
}

/// <summary>
/// The sole two-App authority allowed to bridge reserved credential locators to generic secret
/// storage. Reads are current-version only and tombstones cannot be treated as a credential.
/// </summary>
internal sealed class TwoAppCredentialVault(ISecretStore secretStore) : ITwoAppCredentialVault
{
    private const string Tombstone = """{"status":"revoked"}""";

    public async Task<SecretGetResult> ReadCurrentAsync(TwoAppCredentialLocator locator, CancellationToken ct = default)
    {
        var result = await secretStore.GetSecretAsync(locator.Key, ct).ConfigureAwait(false);
        return !result.Found || IsTombstone(result.Value) ? SecretGetResult.NotFound : result;
    }

    public async Task WriteAsync(TwoAppCredentialLocator locator, string value, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(value) || IsTombstone(value))
            throw new ArgumentException("The vault cannot write empty or tombstone credential material.", nameof(value));
        await secretStore.SetSecretAsync(locator.Key, value, ct: ct).ConfigureAwait(false);
    }

    public async Task TombstoneAndDeleteAsync(TwoAppCredentialLocator locator, CancellationToken ct = default)
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
            return document.RootElement.TryGetProperty("status", out var status) &&
                   string.Equals(status.GetString(), "revoked", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
