using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Agentweaver.Projects.Config;

internal static class ModelProviderCredentialPublicationV2Endpoints
{
    internal const string BaseRoute = "/internal/byok/credential-publications/v2";
    internal const string Purpose = "credential-publication-v2";
    private const int MaximumRequestBodyBytes = 8 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static bool IsV2Path(PathString path) =>
        path.Value?.StartsWith(BaseRoute, StringComparison.OrdinalIgnoreCase) == true;

    internal static IEndpointRouteBuilder MapModelProviderCredentialPublicationV2Endpoints(
        this IEndpointRouteBuilder app,
        bool enabled)
    {
        if (!enabled)
            return app;

        var group = app.MapGroup(BaseRoute)
            .RequireAuthorization(ProjectAuthorizationOwner.BrokerCredentialPublicationAuthorizationPolicy);

        group.MapPost("/preflight", (
                HttpContext context,
                ModelProviderCredentialPublicationV2Service service,
                CancellationToken cancellationToken) =>
            HandleAsync<ModelProviderCredentialPublicationPreflightRequest, ModelProviderCredentialPublicationPreflightResponse>(
                context,
                req => req.IsValid && MatchesToken(context.User, req.OperationId, req.Scope, req.ProjectId, req.ProviderId, req.ActorId, req.Issuer),
                req => service.PreflightAsync(req, cancellationToken),
                cancellationToken))
            .WithName("PreflightBrokerModelProviderCredentialV2");

        group.MapPost("/publish", (
                HttpContext context,
                ModelProviderCredentialPublicationV2Service service,
                CancellationToken cancellationToken) =>
            HandleAsync<ModelProviderCredentialPublicationV2Request, ModelProviderCredentialPublicationReceipt>(
                context,
                req => req.IsValid && MatchesToken(context.User, req.OperationId, req.Scope, req.ProjectId, req.ProviderId, req.ActorId, req.Issuer),
                req => service.PublishAsync(req, cancellationToken),
                cancellationToken))
            .WithName("PublishBrokerModelProviderCredentialV2");

        group.MapPost("/cancel", (
                HttpContext context,
                ModelProviderCredentialPublicationV2Service service,
                CancellationToken cancellationToken) =>
            HandleAsync<ModelProviderCredentialCancelRequest, ModelProviderCredentialCancelResponse>(
                context,
                req => req.IsValid && MatchesToken(context.User, req.OperationId, req.Scope, req.ProjectId, req.ProviderId, req.ActorId, req.Issuer),
                req => service.CancelAsync(req, cancellationToken),
                cancellationToken))
            .WithName("CancelBrokerModelProviderCredentialV2");

        group.MapPost("/disable", (
                HttpContext context,
                ModelProviderCredentialPublicationV2Service service,
                CancellationToken cancellationToken) =>
            HandleAsync<ModelProviderCredentialDisableRequest, ModelProviderCredentialDisableReceipt>(
                context,
                req => req.IsValid && MatchesToken(context.User, req.OperationId, req.Scope, req.ProjectId, req.ProviderId, req.ActorId, req.Issuer),
                req => service.DisableAsync(req, cancellationToken),
                cancellationToken))
            .WithName("DisableBrokerModelProviderCredentialV2");

        group.MapPost("/read-operation", (
                HttpContext context,
                ModelProviderCredentialPublicationV2Service service,
                CancellationToken cancellationToken) =>
            HandleAsync<ModelProviderCredentialOperationReadRequest, ModelProviderCredentialOperationReadResponse>(
                context,
                req => req.IsValid && MatchesToken(context.User, req.OperationId, req.Scope, req.ProjectId, req.ProviderId, req.ActorId, req.Issuer),
                req => service.ReadOperationAsync(req, cancellationToken),
                cancellationToken))
            .WithName("ReadBrokerModelProviderCredentialOperationV2");

        group.MapPost("/status-check", (
                HttpContext context,
                ModelProviderCredentialPublicationV2Service service,
                CancellationToken cancellationToken) =>
            HandleAsync<ModelProviderCredentialStatusCheckRequest, ModelProviderCredentialStatusCheckResponse>(
                context,
                req => req.IsValid && MatchesToken(context.User, req.OperationId, req.Scope, req.ProjectId, req.ProviderId, req.ActorId, req.Issuer),
                req => service.CheckStatusAsync(req, cancellationToken),
                cancellationToken))
            .WithName("CheckBrokerModelProviderCredentialStatusV2");

        return app;
    }

    private static async Task<IResult> HandleAsync<TRequest, TResponse>(
        HttpContext context,
        Func<TRequest, bool> validate,
        Func<TRequest, Task<TResponse>> execute,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        try
        {
            if (!BrokerRunBindingTlsIdentity.IsNativeHttps(context) ||
                context.Request.Query.Count != 0 ||
                !string.Equals(
                    context.Request.ContentType?.Split(';', 2)[0],
                    "application/json",
                    StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { code = "credential_publication_request_invalid" }, statusCode: 400);

            var body = await ReadBoundedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            if (body is null)
                return Results.Json(new { code = "request_body_too_large" }, statusCode: 413);
            if (body.Length == 0)
                return Results.Json(new { code = "credential_publication_request_invalid" }, statusCode: 400);

            TRequest? request;
            try
            {
                using var document = JsonDocument.Parse(
                    body, new JsonDocumentOptions { MaxDepth = JsonOptions.MaxDepth });
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    HasDuplicateProperties(document.RootElement))
                    return Results.Json(new { code = "credential_publication_request_invalid" }, statusCode: 400);
                request = JsonSerializer.Deserialize<TRequest>(document.RootElement.GetRawText(), JsonOptions);
            }
            catch (JsonException)
            {
                return Results.Json(new { code = "credential_publication_request_invalid" }, statusCode: 400);
            }

            if (request is null || !validate(request))
                return Results.Json(new { code = "credential_publication_request_invalid" }, statusCode: 400);

            return Results.Ok(await execute(request).ConfigureAwait(false));
        }
        catch (ModelProviderCredentialPublicationV2Exception exception)
        {
            return Results.Json(new { code = exception.Code }, statusCode: exception.StatusCode);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return Results.StatusCode(499);
        }
    }

    private static bool MatchesToken(
        System.Security.Claims.ClaimsPrincipal principal,
        Guid operationId,
        ModelProviderBindingScope scope,
        string? projectId,
        string providerId,
        string actorId,
        string issuer) =>
        ReadSingle(principal, "sub") == ProjectAuthorizationOwner.BrokerCredentialPublicationServiceSubject &&
        ReadSingle(principal, "purpose") == Purpose &&
        ReadSingle(principal, "operation_id") == operationId.ToString("D") &&
        ReadSingle(principal, "scope") == JsonNamingPolicy.CamelCase.ConvertName(scope.ToString()) &&
        ReadSingle(principal, "project_id") == (projectId ?? string.Empty) &&
        ReadSingle(principal, "provider_id") == providerId &&
        ReadSingle(principal, "actor_id") == actorId &&
        ReadSingle(principal, "actor_iss") == issuer;

    private static bool HasDuplicateProperties(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in element.EnumerateObject())
        {
            if (!names.Add(prop.Name))
                return true;
            if (prop.Value.ValueKind == JsonValueKind.Object && HasDuplicateProperties(prop.Value))
                return true;
        }
        return false;
    }

    private static string? ReadSingle(System.Security.Claims.ClaimsPrincipal principal, string claimType)
    {
        var claims = principal.FindAll(claimType).Take(2).ToArray();
        return claims.Length == 1 ? claims[0].Value : null;
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
}
