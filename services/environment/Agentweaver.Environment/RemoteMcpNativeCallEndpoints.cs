using System.Net.Http.Headers;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agentweaver.Environment;

public static class RemoteMcpNativeCallEndpoints
{
    public static IEndpointRouteBuilder MapRemoteMcpNativeCallDispatch(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(RemoteMcpNativeCallContract.DispatchPath, async (
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                RemoteMcpTrustedServiceTlsComposition.RequireOrchestratorPeer(context);
                if (context.Request.ContentLength > RemoteMcpNativeCallContract.MaximumEnvelopeBytes)
                    return RequestTooLarge();
                if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType) ||
                    !string.Equals(contentType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                    return Results.Json(new { code = "remote_mcp_native_call_content_type_invalid" },
                        statusCode: StatusCodes.Status415UnsupportedMediaType);

                var body = await ReadBoundedBodyAsync(
                    context, RemoteMcpNativeCallContract.MaximumEnvelopeBytes, cancellationToken)
                    .ConfigureAwait(false);
                if (body.Length > RemoteMcpNativeCallContract.MaximumEnvelopeBytes)
                    return RequestTooLarge();

                var serializerOptions = context.RequestServices.GetRequiredService<IOptions<JsonOptions>>()
                    .Value.SerializerOptions;
                var request = JsonSerializer.Deserialize<RemoteMcpNativeCallRequest>(body, serializerOptions)
                    ?? throw new ArgumentException("A Remote MCP native call request is required.");
                RemoteMcpCurrentCallContract.Validate(request.CurrentCall);
                var requestBytes = RemoteMcpNativeCallContract.CopyRequestBytes(request);
                var ownedRequest = request with { RequestBytes = requestBytes };
                using var outbound = CreateOutboundRequest(ownedRequest, requestBytes);
                RemoteMcpNativeCallContract.ValidateRequest(
                    ownedRequest,
                    outbound.Method.Method,
                    outbound.RequestUri!.AbsoluteUri,
                    requestBytes);

                var verifier = context.RequestServices.GetRequiredService<RemoteMcpCurrentCallVerificationService>();
                _ = await verifier.VerifyAsync(context, ownedRequest.CurrentCall, cancellationToken).ConfigureAwait(false);

                return Results.Json(new { code = "remote_mcp_native_transport_unavailable" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (RuntimeAuthorizationException failure)
            {
                var status = failure.Code is
                    "remote_mcp_forced_network_enforcement_unavailable" or
                    "remote_mcp_current_verification_route_unavailable" or
                    "remote_mcp_native_call_route_unavailable" or
                    "remote_mcp_native_transport_unavailable" or
                    "runtime_owner_unavailable" or
                    "runtime_owner_contract_invalid"
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status403Forbidden;
                return Results.Json(new { code = failure.Code }, statusCode: status);
            }
            catch (ProjectsConfigApiException failure)
            {
                return EnvironmentEndpoints.ToProjectAuthorizationResult(failure);
            }
            catch (EnvironmentLifecycleException failure)
            {
                return Results.Conflict(new { code = failure.Code });
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new { code = "remote_mcp_native_call_invalid" });
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { code = "remote_mcp_native_call_invalid" });
            }
            catch (HttpRequestException)
            {
                return Results.Json(new { code = "remote_mcp_current_verification_owner_unavailable" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Results.Json(new { code = "remote_mcp_current_verification_owner_timeout" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireAuthorization();
        return endpoints;
    }

    internal static async Task<byte[]> ReadBoundedBodyAsync(
        HttpContext context,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = maximumBytes + 1;

        var body = new byte[maximumBytes + 1];
        var bodyLength = 0;
        while (bodyLength < body.Length)
        {
            var read = await context.Request.Body.ReadAsync(body.AsMemory(bodyLength), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            bodyLength += read;
        }
        return body.AsSpan(0, bodyLength).ToArray();
    }

    internal static HttpRequestMessage CreateOutboundRequest(
        RemoteMcpNativeCallRequest request,
        byte[] ownedRequestBytes)
    {
        if (!Uri.TryCreate(request.CurrentCall.CallBinding.CanonicalTarget, UriKind.Absolute, out var target))
            throw new ArgumentException("A canonical reviewed MCP target is required.", nameof(request));
        var outbound = new HttpRequestMessage(
            new HttpMethod(request.CurrentCall.CallBinding.Method), target)
        {
            Content = new ByteArrayContent(ownedRequestBytes)
        };
        outbound.Content.Headers.ContentType = new("application/json");
        return outbound;
    }

    private static IResult RequestTooLarge() =>
        Results.Json(new { code = "remote_mcp_native_call_request_too_large" },
            statusCode: StatusCodes.Status413PayloadTooLarge);
}
