using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Agentweaver.Projects.Config;

internal static class PlatformPolicySourceEndpoints
{
    private const int MaximumResponseBytes = 5_242_880;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly object DeadlineTokenKey = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static void UsePlatformPolicySourceGuard(this IApplicationBuilder app, int listenerPort)
    {
        app.Use(async (context, next) =>
        {
            var isSourceListener = context.Connection.LocalPort == listenerPort;
            var policySourceArea = context.Request.Path.StartsWithSegments(
                "/internal/policy-source",
                StringComparison.OrdinalIgnoreCase);
            var exactPath = string.Equals(
                context.Request.Path.Value,
                PlatformPolicySourceTls.RoutePath,
                StringComparison.Ordinal);
            if (!policySourceArea)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            context.Response.Headers.CacheControl = "no-store";
            if (!isSourceListener || !exactPath)
            {
                await WriteErrorAsync(
                    context,
                    StatusCodes.Status404NotFound,
                    "not_found",
                    context.RequestAborted).ConfigureAwait(false);
                return;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            deadline.CancelAfter(RequestTimeout);
            context.Items[DeadlineTokenKey] = deadline.Token;
            var transport = context.Response.Body;
            try
            {
                var protocol = context.Features.Get<ITlsHandshakeFeature>()?.Protocol;
                var certificate = context.Features.Get<ITlsConnectionFeature>()?.ClientCertificate;
                var tls = context.RequestServices.GetRequiredService<PlatformPolicySourceTlsIdentity>();
                if (!context.Request.IsHttps ||
                    protocol is not (SslProtocols.Tls12 or SslProtocols.Tls13) ||
                    !await tls.IsEnvironmentPeerAsync(certificate, deadline.Token).ConfigureAwait(false))
                {
                    await WriteErrorAsync(
                        context,
                        StatusCodes.Status403Forbidden,
                        "policy_source_peer_denied",
                        context.RequestAborted).ConfigureAwait(false);
                    return;
                }

                using var buffer = new BoundedMemoryStream(MaximumResponseBytes);
                context.Response.Body = buffer;
                if (context.Request.Headers.ContainsKey("Authorization"))
                {
                    await WriteErrorAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        "policy_source_request_invalid",
                        context.RequestAborted).ConfigureAwait(false);
                }
                else if (!HttpMethods.IsGet(context.Request.Method))
                {
                    context.Response.Headers.Allow = "GET";
                    await WriteErrorAsync(context, StatusCodes.Status405MethodNotAllowed,
                        "policy_source_request_invalid", deadline.Token).ConfigureAwait(false);
                }
                else
                {
                    await next(context).ConfigureAwait(false);
                }

                context.Response.Body = transport;
                if (!await tls.IsEnvironmentPeerAsync(
                        context.Features.Get<ITlsConnectionFeature>()?.ClientCertificate,
                        deadline.Token).ConfigureAwait(false))
                {
                    context.Response.Clear();
                    await WriteErrorAsync(context, StatusCodes.Status403Forbidden,
                        "policy_source_peer_denied", context.RequestAborted).ConfigureAwait(false);
                    return;
                }
                deadline.Token.ThrowIfCancellationRequested();
                await transport.WriteAsync(
                    buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)),
                    deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                context.Response.Body = transport;
                await WriteFailureAsync(context).ConfigureAwait(false);
            }
            catch (Exception)
            {
                context.Response.Body = transport;
                await WriteFailureAsync(context).ConfigureAwait(false);
            }
            finally
            {
                context.Response.Body = transport;
                context.Items.Remove(DeadlineTokenKey);
            }
        });
    }

    internal static void MapPlatformPolicySource(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(PlatformPolicySourceTls.RoutePath, HandleGetAsync)
            .WithName("ReadPlatformPolicySource");
    }

    private static async Task HandleGetAsync(
        HttpContext context,
        NativeAcsPolicyStore policyStore)
    {
        context.Response.Headers.CacheControl = "no-store";
        var cancellationToken = context.Items.TryGetValue(DeadlineTokenKey, out var value) &&
            value is CancellationToken deadlineToken
                ? deadlineToken
                : context.RequestAborted;
        try
        {
            if (context.Request.QueryString.HasValue ||
                context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true ||
                context.Request.ContentLength is > 0 ||
                context.Request.ContentType is not null ||
                context.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                await WriteErrorAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "policy_source_request_invalid",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await policyStore.ReadPlatformPolicySourceAsync(cancellationToken)
                .ConfigureAwait(false);
            if (result is null)
            {
                await WriteErrorAsync(
                    context,
                    StatusCodes.Status404NotFound,
                    "native_acs_policy_not_found",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var response = new PlatformPolicySourceResponse(
                result.ScopeType,
                result.ScopeId,
                result.PolicyId,
                result.Revision,
                result.ContentSha256,
                result.UpdatedAt,
                result.Artifact,
                new PlatformPolicyAdmission("pending", null));
            using var buffer = new BoundedMemoryStream(MaximumResponseBytes);
            try
            {
                await JsonSerializer.SerializeAsync(buffer, response, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ResponseLimitExceededException)
            {
                await WriteErrorAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "native_acs_policy_corrupt",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = buffer.Length;
            await context.Response.Body.WriteAsync(
                buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ProjectConfigException exception)
        {
            await WriteErrorAsync(
                context,
                exception.StatusCode,
                exception.Code,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "policy_source_internal_error",
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsSourceUnavailable(exception))
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "native_acs_policy_source_unavailable",
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "policy_source_internal_error",
                context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static async Task WriteFailureAsync(HttpContext context)
    {
        if (context.RequestAborted.IsCancellationRequested)
            return;
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }
        context.Response.Clear();
        await WriteErrorAsync(context, StatusCodes.Status500InternalServerError,
            "policy_source_internal_error", context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string code,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new PolicySourceError(code), JsonOptions);
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsSourceUnavailable(Exception exception) =>
        exception is NpgsqlException npgsql &&
            (npgsql.InnerException is IOException or SocketException or TimeoutException ||
             npgsql is PostgresException { SqlState: "57P01" or "57P02" or "57P03" }) ||
        exception is InvalidOperationException
            { InnerException: NpgsqlException inner } &&
            (inner.InnerException is IOException or SocketException or TimeoutException ||
             inner is PostgresException { SqlState: "57P01" or "57P02" or "57P03" });

    private sealed record PlatformPolicySourceResponse(
        NativeAcsPolicyScopeType ScopeType,
        string ScopeId,
        string PolicyId,
        long Revision,
        string ContentSha256,
        DateTimeOffset UpdatedAt,
        Agentweaver.Abstractions.NativeAcsArtifact Artifact,
        PlatformPolicyAdmission Admission);

    private sealed record PlatformPolicyAdmission(string State, long? Checkpoint);

    private sealed record PolicySourceError(string Code);

    private sealed class BoundedMemoryStream(int maximumLength) : MemoryStream
    {
        private void EnsureWriteFits(int count)
        {
            if (count > maximumLength - Position)
                throw new ResponseLimitExceededException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureWriteFits(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureWriteFits(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            EnsureWriteFits(1);
            base.WriteByte(value);
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            EnsureWriteFits(count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnsureWriteFits(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class ResponseLimitExceededException : Exception { }
}
