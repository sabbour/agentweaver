using System.Security.Claims;
using Agentweaver.Api.Assistant;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace Agentweaver.Api.Auth.OAuth;

public interface IOperatorAssistantBrokerTokenIssuer
{
    Task<string> IssueAsync(
        CallerContext caller,
        string runId,
        string? projectId,
        CancellationToken ct);
}

public sealed class OperatorAssistantBrokerTokenIssuer(
    IServiceScopeFactory scopeFactory,
    OAuthServerConfiguration configuration,
    IHostEnvironment environment,
    IConfiguration appConfiguration,
    ILogger<OperatorAssistantBrokerTokenIssuer> logger) : IOperatorAssistantBrokerTokenIssuer
{
    internal static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);

    public async Task<string> IssueAsync(
        CallerContext caller,
        string runId,
        string? projectId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var testBypass = environment.IsDevelopment()
            && appConfiguration.GetValue<bool>("Testing:BypassGitHubTokenAuth")
            && string.Equals(
                caller.AuthenticationScheme,
                AgentweaverAuthenticationSchemes.TestBypass,
                StringComparison.Ordinal);
        if (!testBypass
            && !string.Equals(
                caller.AuthenticationScheme,
                AgentweaverAuthenticationSchemes.Entra,
                StringComparison.Ordinal))
        {
            throw new AssistantRunHttpException(
                StatusCodes.Status403Forbidden,
                "broker_token_forbidden",
                "Only an authenticated platform user may invoke the operator assistant.");
        }

        var subject = caller.EntraObjectId ?? caller.User;
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new AssistantRunHttpException(
                StatusCodes.Status403Forbidden,
                "broker_token_forbidden",
                "The authenticated platform user has no usable subject.");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            if (!ProjectId.TryParse(projectId, out var parsedProjectId)
                || await scope.ServiceProvider.GetRequiredService<IProjectStore>()
                    .GetAsync(parsedProjectId, ct).ConfigureAwait(false) is null)
            {
                throw new AssistantRunHttpException(
                    StatusCodes.Status404NotFound,
                    "project_not_found",
                    "The assistant project context no longer exists.");
            }

            if (!await scope.ServiceProvider.GetRequiredService<IProjectRoleAuthorizationService>()
                    .HasRoleAsync(caller, parsedProjectId, ProjectRole.Viewer, ct)
                    .ConfigureAwait(false))
            {
                throw new AssistantRunHttpException(
                    StatusCodes.Status403Forbidden,
                    "forbidden",
                    "You no longer have access to this assistant project context.");
            }
        }

        var identity = new ClaimsIdentity(
            authenticationType: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            nameType: Claims.Name,
            roleType: Claims.Role);
        identity.SetClaim(Claims.Subject, subject);
        identity.SetClaim(Claims.Name, caller.DisplayName ?? subject);
        identity.SetClaim("agentweaver_run_id", runId);
        if (!string.IsNullOrWhiteSpace(projectId))
            identity.SetClaim("agentweaver_project_id", projectId);

        var principal = new ClaimsPrincipal(identity);
        var now = DateTimeOffset.UtcNow;
        principal.SetCreationDate(now);
        principal.SetExpirationDate(now.Add(TokenLifetime));
        principal.SetScopes(OAuthServerConfiguration.McpScope);
        principal.SetResources(configuration.Resource.AbsoluteUri);
        principal.SetAudiences(configuration.Resource.AbsoluteUri);
        principal.SetClaim(Claims.Private.Issuer, configuration.PublicOrigin.AbsoluteUri);
        principal.SetTokenType(TokenTypeIdentifiers.AccessToken);
        foreach (var claim in principal.Claims)
            claim.SetDestinations(Destinations.AccessToken);

        var factory = scope.ServiceProvider.GetRequiredService<IOpenIddictServerFactory>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IOpenIddictServerDispatcher>();
        var transaction = await factory.CreateTransactionAsync().ConfigureAwait(false);
        transaction.CancellationToken = ct;

        var context = new GenerateTokenContext(transaction)
        {
            BaseUri = configuration.PublicOrigin,
            CancellationToken = ct,
            CreateTokenEntry = true,
            IsReferenceToken = false,
            PersistTokenPayload = false,
            Principal = principal,
            TokenFormat = TokenFormats.Private.JsonWebToken,
            TokenType = TokenTypeIdentifiers.AccessToken,
        };
        await dispatcher.DispatchAsync(context).ConfigureAwait(false);
        if (context.IsRejected || string.IsNullOrWhiteSpace(context.Token))
            throw new InvalidOperationException(
                $"OpenIddict rejected the operator assistant broker token issuance: " +
                $"{context.Error ?? "token_not_generated"} ({context.ErrorDescription ?? "no description"}).");

        logger.LogInformation(
            "Issued a five-minute operator assistant MCP broker token for run {RunId}, subject {Subject}, project {ProjectId}.",
            runId,
            subject,
            projectId);
        return context.Token;
    }
}
