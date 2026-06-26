using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Agentweaver.Api.Memory;

namespace Agentweaver.Api.Auth.OAuth;

/// <summary>Result of a Dynamic Client Registration (RFC 7591 / T5).</summary>
public sealed record ClientRegistrationResult(
    string ClientId,
    IReadOnlyList<string> RedirectUris,
    long ClientIdIssuedAt);

/// <summary>
/// Persistent store for dynamically-registered OAuth public clients (RFC 7591 / T5).
///
/// Scoped (per-request) because it depends on the scoped <see cref="MemoryDbContext"/>; resolved
/// directly by the minimal-API OAuth handlers.
/// </summary>
public sealed class McpClientStore
{
    private readonly MemoryDbContext _db;

    public McpClientStore(MemoryDbContext db) => _db = db;

    /// <summary>
    /// Registers a public client with the given exact redirect URIs and issues an ephemeral
    /// non-secret <c>client_id</c>. Callers MUST validate the redirect URIs before calling.
    /// </summary>
    public async Task<ClientRegistrationResult> RegisterAsync(
        IReadOnlyList<string> redirectUris, string? clientName, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var clientId = GenerateClientId();
        _db.McpClientRegistrations.Add(new McpClientRegistration
        {
            ClientId = clientId,
            RedirectUris = string.Join('\n', redirectUris),
            ClientName = clientName,
            CreatedAt = now,
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new ClientRegistrationResult(clientId, redirectUris, now.ToUnixTimeSeconds());
    }

    /// <summary>
    /// Returns the registered exact redirect URIs for <paramref name="clientId"/>, or <c>null</c>
    /// when the client is not registered (e.g. a loopback native client that skipped DCR).
    /// </summary>
    public async Task<IReadOnlyList<string>?> GetRedirectUrisAsync(string clientId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return null;

        var row = await _db.McpClientRegistrations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ClientId == clientId, ct)
            .ConfigureAwait(false);

        return row is null
            ? null
            : row.RedirectUris.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string GenerateClientId()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return "mcp_" + Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
