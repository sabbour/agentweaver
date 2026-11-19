using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Projects.Config;

internal static class ModelProviderCredentialPublicationEndpoints
{
    internal const int MaximumRequestBodyBytes = 8 * 1024;
    internal const string Route = "/internal/byok/credential-publications";

    internal static bool IsLegacyPath(PathString path) =>
        string.Equals(path.Value?.TrimEnd('/'), Route, StringComparison.OrdinalIgnoreCase);

    internal static void SuppressLegacyRequestLogs(IServiceCollection services, IHttpContextAccessor accessor) =>
        services.PostConfigure<LoggerFilterOptions>(options =>
        {
            bool Allowed(string? category) =>
                category != "Microsoft.AspNetCore.Hosting.Diagnostics" ||
                accessor.HttpContext is not { } context || !IsLegacyPath(context.Request.Path);
            for (var index = 0; index < options.Rules.Count; index++)
            {
                var rule = options.Rules[index];
                var original = rule.Filter;
                options.Rules[index] = new LoggerFilterRule(rule.ProviderName, rule.CategoryName, rule.LogLevel,
                    (provider, category, level) =>
                        Allowed(category) && (original is null || original(provider, category, level)));
            }
            options.Rules.Insert(0, new LoggerFilterRule(null, null, null,
                (_, category, _) => Allowed(category)));
        });

    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };

    internal static IApplicationBuilder UseModelProviderCredentialPublicationNoStore(
        this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!IsLegacyPath(context.Request.Path))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            SetNoStore(context);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "application/json";
            if (!HttpMethods.IsHead(context.Request.Method))
                await context.Response.WriteAsync("""{"code":"credential_publication_unavailable"}""")
                    .ConfigureAwait(false);
        });

    internal static IEndpointRouteBuilder MapModelProviderCredentialPublicationEndpoints(
        this IEndpointRouteBuilder app,
        bool enabled)
    {
        if (!enabled)
            return app;

        app.MapPost(Route, async (
                HttpContext context,
                ModelProviderDraftService service,
                CancellationToken cancellationToken) =>
            {
                SetNoStore(context);
                try
                {
                    if (!BrokerRunBindingTlsIdentity.IsNativeHttps(context) ||
                        context.Request.Query.Count != 0 ||
                        !string.Equals(
                            context.Request.ContentType?.Split(';', 2)[0],
                            "application/json",
                            StringComparison.OrdinalIgnoreCase) ||
                        !TryReadBoundRequest(context.User, out var actorId))
                        return Error("credential_publication_request_invalid", StatusCodes.Status400BadRequest);

                    var body = await ReadBoundedAsync(context.Request, cancellationToken).ConfigureAwait(false);
                    if (body is null)
                        return Error("request_body_too_large", StatusCodes.Status413PayloadTooLarge);
                    if (body.Length == 0)
                        return Error("credential_publication_request_invalid", StatusCodes.Status400BadRequest);

                    ModelProviderCredentialPublicationRequest? request;
                    try
                    {
                        using var document = JsonDocument.Parse(
                            body, new JsonDocumentOptions { MaxDepth = RequestJsonOptions.MaxDepth });
                        if (document.RootElement.ValueKind != JsonValueKind.Object ||
                            document.RootElement.EnumerateObject()
                                .Select(property => property.Name)
                                .Distinct(StringComparer.Ordinal)
                                .Count() != document.RootElement.EnumerateObject().Count())
                            return Error(
                                "credential_publication_request_invalid",
                                StatusCodes.Status400BadRequest);
                        request = JsonSerializer.Deserialize<ModelProviderCredentialPublicationRequest>(
                            document.RootElement.GetRawText(), RequestJsonOptions);
                    }
                    catch (JsonException)
                    {
                        return Error("credential_publication_request_invalid", StatusCodes.Status400BadRequest);
                    }

                    if (request is null || !request.IsValid ||
                        !MatchesTokenBinding(context.User, request))
                        return Error("credential_publication_request_invalid", StatusCodes.Status400BadRequest);

                    return Results.Ok(await service.PublishCredentialAsync(request, actorId!, cancellationToken)
                        .ConfigureAwait(false));
                }
                catch (ModelProviderDraftException exception)
                {
                    return Error(exception.Code, exception.Code switch
                    {
                        "model_provider_draft_not_found" => StatusCodes.Status404NotFound,
                        "model_provider_draft_conflict" => StatusCodes.Status409Conflict,
                        _ => StatusCodes.Status400BadRequest,
                    });
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    return Results.StatusCode(499);
                }
            })
            .RequireAuthorization(ProjectAuthorizationOwner.BrokerCredentialPublicationAuthorizationPolicy)
            .WithName("PublishBrokerModelProviderCredential");

        return app;
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        await using var bounded = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var available = MaximumRequestBodyBytes + 1 - (int)bounded.Length;
            var count = await request.Body.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, available)),
                cancellationToken).ConfigureAwait(false);
            if (count == 0)
                return bounded.ToArray();
            if (bounded.Length + count > MaximumRequestBodyBytes)
                return null;
            await bounded.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
    }

    internal static bool TryReadBoundRequest(
        System.Security.Claims.ClaimsPrincipal principal,
        out string? actorId)
    {
        actorId = null;
        if (ReadSingle(principal, "sub") != ProjectAuthorizationOwner.BrokerCredentialPublicationServiceSubject ||
            ReadSingle(principal, "purpose") != ProjectAuthorizationOwner.BrokerCredentialPublicationPurpose ||
            ReadSingle(principal, "operation_id") is not { } operationId ||
            !Guid.TryParseExact(operationId, "D", out _) ||
            ReadSingle(principal, "expected_provider_revision") is not { } expectedRevision ||
            !long.TryParse(expectedRevision, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
            revision <= 0 ||
            ReadSingle(principal, "scope") is not ("platform" or "project") ||
            ReadSingle(principal, "project_id") is null ||
            ReadSingle(principal, "provider_id") is not { Length: > 0 and <= 128 } ||
            ReadSingle(principal, "credential_version") is not { Length: 32 } ||
            ReadSingle(principal, "credential_slot_id") is not { } slotId ||
            !Guid.TryParseExact(slotId, "D", out var credentialSlotId) || credentialSlotId == Guid.Empty ||
            ReadSingle(principal, "credential_secret_id") is not { Length: > 0 and <= 256 } ||
            ReadSingle(principal, "actor_id") is not { Length: > 0 and <= ModelProviderDraftLimits.ActorId } actor ||
            actor.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-' or ':')))
            return false;

        actorId = actor;
        return true;
    }

    internal static bool MatchesTokenBinding(
        System.Security.Claims.ClaimsPrincipal principal,
        ModelProviderCredentialPublicationRequest request) =>
        ReadSingle(principal, "operation_id") == request.OperationId.ToString("D") &&
        ReadSingle(principal, "expected_provider_revision") ==
            request.ExpectedProviderRevision.ToString(CultureInfo.InvariantCulture) &&
        ReadSingle(principal, "scope") ==
            JsonNamingPolicy.CamelCase.ConvertName(request.Scope.ToString()) &&
        ReadSingle(principal, "project_id") == (request.ProjectId ?? string.Empty) &&
        ReadSingle(principal, "provider_id") == request.ProviderId &&
        ReadSingle(principal, "credential_version") == request.CredentialVersion &&
        ReadSingle(principal, "credential_slot_id") ==
            request.CredentialIdentity.CredentialSlotId.ToString("D") &&
        ReadSingle(principal, "credential_secret_id") == request.CredentialReference.Id;

    private static string? ReadSingle(System.Security.Claims.ClaimsPrincipal principal, string claimType)
    {
        var claims = principal.FindAll(claimType).Take(2).ToArray();
        return claims.Length == 1 ? claims[0].Value : null;
    }

    private static IResult Error(string code, int statusCode) =>
        Results.Json(new { code }, statusCode: statusCode);

    private static void SetNoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }
}
