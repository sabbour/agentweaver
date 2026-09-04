using Agentweaver.Api.Memory;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Agentweaver.Api.Auth.OAuth;

public sealed class OAuthBrokerTransactionService(
    MemoryDbContext db,
    IOpenIddictApplicationManager applications)
{
    public async Task<string?> CompleteErrorAsync(
        string handle,
        string error,
        string description,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(handle)
            || error is not Errors.AccessDenied and not Errors.ServerError)
            return null;

        var hash = OAuthCertificateLoader.HashOpaque(handle);
        var transaction = await db.OAuthAuthorizationTransactions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.HandleHash == hash, ct).ConfigureAwait(false);
        if (transaction is null
            || transaction.ExpiresAt <= DateTimeOffset.UtcNow
            || transaction.ConsumedAt is not null)
            return null;

        var application = await applications.FindByClientIdAsync(transaction.ClientId, ct).ConfigureAwait(false);
        if (application is null)
            return null;
        var redirects = await applications.GetRedirectUrisAsync(application, ct).ConfigureAwait(false);
        if (!redirects.Contains(transaction.RedirectUri, StringComparer.Ordinal))
            return null;

        var claimed = await db.OAuthAuthorizationTransactions
            .Where(x => x.HandleHash == hash && x.ConsumedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.ConsumedAt, DateTimeOffset.UtcNow),
                ct).ConfigureAwait(false);
        if (claimed != 1)
            return null;

        var parameters = new Dictionary<string, string?>
        {
            ["error"] = error,
            ["error_description"] = description,
            ["state"] = transaction.ClientState,
        };
        return QueryHelpers.AddQueryString(transaction.RedirectUri, parameters);
    }
}
