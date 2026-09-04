using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Auth;

public sealed record UserModelProviderSettings(
    UserModelProviderPreference Preference,
    ByokProviderConfiguration? ByokProvider);

public sealed class UserModelProviderSettingsService(
    MemoryDbContext db,
    ISecretStore secretStore)
{
    public async Task<UserModelProviderSettings> GetAsync(string entraObjectId, CancellationToken ct = default)
    {
        var record = await db.UserModelProviderSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EntraObjectId == entraObjectId, ct)
            .ConfigureAwait(false);
        if (record is null)
            return new(UserModelProviderPreference.GitHubCopilot, null);

        return new(record.Preference, await ReadByokAsync(record, ct).ConfigureAwait(false));
    }

    public async Task<ByokProviderConfiguration?> GetActiveByokAsync(
        string entraObjectId,
        CancellationToken ct = default)
    {
        var record = await db.UserModelProviderSettings.AsNoTracking()
            .SingleOrDefaultAsync(x =>
                x.EntraObjectId == entraObjectId &&
                x.Preference == UserModelProviderPreference.Byok, ct)
            .ConfigureAwait(false);
        return record is null ? null : await ReadByokAsync(record, ct).ConfigureAwait(false);
    }

    public async Task SetPreferenceAsync(
        string entraObjectId,
        UserModelProviderPreference preference,
        CancellationToken ct = default)
    {
        var record = await db.UserModelProviderSettings
            .SingleOrDefaultAsync(x => x.EntraObjectId == entraObjectId, ct)
            .ConfigureAwait(false);
        if (preference == UserModelProviderPreference.Byok &&
            (record is null || string.IsNullOrWhiteSpace(record.ByokCredentialReference)))
            throw new InvalidOperationException("Configure a personal provider before selecting it.");
        if (record is null)
        {
            record = new UserModelProviderSettingsRecord
            {
                EntraObjectId = entraObjectId,
                Preference = preference,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.UserModelProviderSettings.Add(record);
        }
        else
        {
            record.Preference = preference;
            record.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<ByokProviderConfiguration> SetByokAsync(
        string entraObjectId,
        ByokProviderConfiguration input,
        CancellationToken ct = default)
    {
        Validate(input);
        var existing = await db.UserModelProviderSettings
            .SingleOrDefaultAsync(x => x.EntraObjectId == entraObjectId, ct)
            .ConfigureAwait(false);
        var existingConfiguration = existing is null
            ? null
            : await ReadByokAsync(existing, ct).ConfigureAwait(false);
        var apiKey = string.IsNullOrWhiteSpace(input.ApiKey)
            ? existingConfiguration?.ApiKey
            : input.ApiKey;
        var configuration = input with
        {
            Id = existing?.ByokProviderId ?? Guid.NewGuid().ToString("n"),
            ApiKey = apiKey ?? string.Empty,
        };
        Validate(configuration);

        var version = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var reference = $"user-byok-{SubjectDigest(entraObjectId)}-{version}";
        var secretValue = JsonSerializer.Serialize(configuration);
        await secretStore.SetSecretAsync(reference, secretValue, ct: ct).ConfigureAwait(false);
        var persisted = await secretStore.GetSecretAsync(reference, ct).ConfigureAwait(false);
        if (!persisted.Found || !string.Equals(persisted.Value, secretValue, StringComparison.Ordinal))
        {
            await TryDeleteAsync(reference).ConfigureAwait(false);
            throw new InvalidOperationException("The personal provider credential could not be verified after writing.");
        }

        var previousReference = existing?.ByokCredentialReference;
        if (existing is null)
        {
            existing = new UserModelProviderSettingsRecord { EntraObjectId = entraObjectId };
            db.UserModelProviderSettings.Add(existing);
        }
        existing.Preference = UserModelProviderPreference.Byok;
        existing.ByokProviderId = configuration.Id;
        existing.ByokName = configuration.Name;
        existing.ByokType = configuration.Type;
        existing.ByokBaseUrl = configuration.BaseUrl;
        existing.ByokModel = configuration.Model;
        existing.ByokWireApi = configuration.WireApi;
        existing.ByokHeadersJson = configuration.Headers is null
            ? null
            : JsonSerializer.Serialize(configuration.Headers);
        existing.ByokAzureApiVersion = configuration.AzureApiVersion;
        existing.ByokCredentialReference = reference;
        existing.UpdatedAt = DateTimeOffset.UtcNow;

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            db.ChangeTracker.Clear();
            await TryDeleteAsync(reference).ConfigureAwait(false);
            throw;
        }

        if (!string.IsNullOrWhiteSpace(previousReference) &&
            !string.Equals(previousReference, reference, StringComparison.Ordinal))
            await TryDeleteAsync(previousReference).ConfigureAwait(false);
        return configuration;
    }

    public async Task RemoveByokAsync(string entraObjectId, CancellationToken ct = default)
    {
        var record = await db.UserModelProviderSettings
            .SingleOrDefaultAsync(x => x.EntraObjectId == entraObjectId, ct)
            .ConfigureAwait(false);
        if (record is null || string.IsNullOrWhiteSpace(record.ByokCredentialReference))
            return;

        var reference = record.ByokCredentialReference;
        record.Preference = UserModelProviderPreference.GitHubCopilot;
        record.ByokProviderId = null;
        record.ByokName = null;
        record.ByokType = null;
        record.ByokBaseUrl = null;
        record.ByokModel = null;
        record.ByokWireApi = null;
        record.ByokHeadersJson = null;
        record.ByokAzureApiVersion = null;
        record.ByokCredentialReference = null;
        record.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await TryDeleteAsync(reference).ConfigureAwait(false);
    }

    private async Task<ByokProviderConfiguration?> ReadByokAsync(
        UserModelProviderSettingsRecord record,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(record.ByokCredentialReference))
            return null;
        var secret = await secretStore.GetSecretAsync(record.ByokCredentialReference, ct).ConfigureAwait(false);
        if (!secret.Found || string.IsNullOrWhiteSpace(secret.Value))
            return null;
        try
        {
            var configuration = JsonSerializer.Deserialize<ByokProviderConfiguration>(secret.Value);
            if (configuration is null)
                return null;
            Validate(configuration);
            return configuration;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task TryDeleteAsync(string reference)
    {
        try
        {
            await secretStore.DeleteSecretAsync(reference, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The durable row no longer points at this version. Secret cleanup is best effort.
        }
    }

    private static string SubjectDigest(string entraObjectId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entraObjectId)))
            .ToLowerInvariant()[..20];

    private static void Validate(ByokProviderConfiguration configuration)
    {
        if (configuration.Type is not ("openai" or "azure" or "anthropic"))
            throw new ArgumentException("Provider type must be openai, azure, or anthropic.");
        if (string.IsNullOrWhiteSpace(configuration.Name))
            throw new ArgumentException("Provider display name is required.");
        if (!Uri.TryCreate(configuration.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Provider base URL must be an HTTPS URL.");
        if (configuration.Type == "azure" &&
            (!string.IsNullOrEmpty(baseUri.PathAndQuery.Trim('/')) || !string.IsNullOrEmpty(baseUri.Fragment)))
            throw new ArgumentException("Azure provider base URL must be its HTTPS host without an API path.");
        if (string.IsNullOrWhiteSpace(configuration.Model))
            throw new ArgumentException("Provider model is required.");
        if (configuration.Type != "openai" && string.IsNullOrWhiteSpace(configuration.ApiKey))
            throw new ArgumentException("Provider API key is required.");
        if (configuration.WireApi is not (null or "completions" or "responses"))
            throw new ArgumentException("Provider wire API must be completions or responses.");
    }
}
