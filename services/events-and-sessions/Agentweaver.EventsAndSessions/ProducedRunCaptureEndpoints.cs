using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Azure;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace Agentweaver.EventsAndSessions;

internal sealed class ProducedRunCaptureIntegrityException(string code, Exception? innerException = null)
    : Exception(code, innerException)
{
    public string Code { get; } = code;
}

internal sealed class ProducedRunCaptureRequestException(string code, int statusCode) : Exception(code)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

internal sealed class ProducedRunCaptureApplicationService(
    IProjectsAuthorizationContextClient projects,
    ICoordinationOwnerClient owner,
    PostgresSessionsJournal journal,
    IObjectStore objects)
{
    internal async Task<ProducedRunCaptureAcknowledgment> WriteAsync(
        HttpContext context,
        string sessionId,
        string captureId,
        byte[] packageBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageBytes);
        var identity = RequireIdentity(context, sessionId);
        var authority = await ReadAuthorityAsync(context, identity, cancellationToken).ConfigureAwait(false);
        var proof = await owner.ReadProducedRunCaptureProofAsync(
            context, identity, captureId, cancellationToken).ConfigureAwait(false);
        RequireProofAuthority(proof, identity, captureId, authority, requireActorMatch: true);
        VerifyPackageBytes(proof, packageBytes);
        var package = ProducedRunCaptureContractValidation.CreatePackageReference(proof);
        var payload = new ProducedRunCaptureSessionPayload(proof, package);

        async Task ValidateCurrentAsync(CancellationToken token)
        {
            var current = await ReadAuthorityAsync(context, identity, token).ConfigureAwait(false);
            RequireProofAuthority(proof, identity, captureId, current, requireActorMatch: true);
            var currentProof = await owner.ReadProducedRunCaptureProofAsync(
                context, identity, captureId, token).ConfigureAwait(false);
            if (currentProof != proof)
                throw new RuntimeAuthorizationException("source_control_output_capture_authority_changed");
        }

        async Task PersistPackageAsync(CancellationToken token)
        {
            await ValidateCurrentAsync(token).ConfigureAwait(false);
            using var existing = await objects.ReadAsync(package.Key, token).ConfigureAwait(false);
            if (existing is null)
            {
                using var content = new MemoryStream(packageBytes, writable: false);
                try
                {
                    await objects.WriteAsync(package.Key, content, token).ConfigureAwait(false);
                }
                catch (RequestFailedException exception) when (exception.Status is 409 or 412)
                {
                    using var raced = await objects.ReadAsync(package.Key, token).ConfigureAwait(false)
                        ?? throw new ProducedRunCaptureIntegrityException(
                            "source_control_output_capture_object_conflict", exception);
                    await VerifyStoredPackageAsync(raced, proof, token).ConfigureAwait(false);
                }
            }
            else
            {
                await VerifyStoredPackageAsync(existing, proof, token).ConfigureAwait(false);
            }
            await ValidateCurrentAsync(token).ConfigureAwait(false);
        }

        var acknowledgment = await journal.AppendProducedRunCaptureAsync(
            context.User,
            sessionId,
            payload,
            PersistPackageAsync,
            ValidateCurrentAsync,
            cancellationToken).ConfigureAwait(false);
        await ValidateCurrentAsync(cancellationToken).ConfigureAwait(false);
        return acknowledgment;
    }

    internal async Task<ProducedRunCaptureContentResult> ReadAsync(
        HttpContext context,
        string sessionId,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        var identity = RequireIdentity(context, sessionId);
        var authority = await ReadAuthorityAsync(context, identity, cancellationToken).ConfigureAwait(false);
        var entry = await journal.ReadProducedRunCaptureEventAsync(
            context.User, sessionId, eventId, cancellationToken).ConfigureAwait(false);
        var proof = await owner.ReadProducedRunCaptureProofAsync(
            context, identity, entry.Capture.CaptureId, cancellationToken).ConfigureAwait(false);
        RequireProofAuthority(proof, identity, entry.Capture.CaptureId, authority, requireActorMatch: false);
        if (proof != entry.Capture || entry.Position < 1)
            throw new ProducedRunCaptureIntegrityException(
                "source_control_output_capture_journal_conflict");

        var package = ProducedRunCaptureContractValidation.CreatePackageReference(proof);
        using var download = await objects.ReadAsync(package.Key, cancellationToken).ConfigureAwait(false)
            ?? throw new ProducedRunCaptureIntegrityException(
                "source_control_output_capture_object_missing");
        var bytes = await ReadVerifiedPackageAsync(download, proof, cancellationToken).ConfigureAwait(false);

        var current = await ReadAuthorityAsync(context, identity, cancellationToken).ConfigureAwait(false);
        RequireProofAuthority(proof, identity, entry.Capture.CaptureId, current, requireActorMatch: false);
        var currentProof = await owner.ReadProducedRunCaptureProofAsync(
            context, identity, entry.Capture.CaptureId, cancellationToken).ConfigureAwait(false);
        if (currentProof != proof)
            throw new RuntimeAuthorizationException("source_control_output_capture_authority_changed");
        return new(entry, bytes);
    }

    private async Task<ProjectsAuthorizationContextResponse> ReadAuthorityAsync(
        HttpContext context,
        SessionIdentity identity,
        CancellationToken cancellationToken)
    {
        var authority = await projects.GetCurrentAsync(context, cancellationToken).ConfigureAwait(false);
        NativeUsageApplicationService.RequireReadAuthority(authority);
        if (authority.BoundProjectId != identity.ProjectId || authority.BoundRunId != identity.RunId)
            throw new RuntimeAuthorizationException("source_control_output_capture_scope_invalid");
        return authority;
    }

    private static SessionIdentity RequireIdentity(HttpContext context, string sessionId)
    {
        if (!SessionIdentityClaims.TryGetScope(context.User, out var scope) || scope is null)
            throw new SessionAuthenticationException();
        return scope.Value.ForSession(sessionId);
    }

    private static void RequireProofAuthority(
        ProducedRunCaptureProof proof,
        SessionIdentity identity,
        string captureId,
        ProjectsAuthorizationContextResponse authority,
        bool requireActorMatch)
    {
        if (proof.Identity != identity || proof.CaptureId != captureId ||
            proof.TenantId != authority.TenantId ||
            requireActorMatch &&
            (proof.ActorIssuer != authority.Issuer || proof.ActorSubject != authority.ActorId))
            throw new RuntimeAuthorizationException("source_control_output_capture_scope_invalid");
    }

    private static void VerifyPackageBytes(ProducedRunCaptureProof proof, byte[] packageBytes)
    {
        if (packageBytes.LongLength != proof.PackageByteLength ||
            Hash(packageBytes) != proof.PackageSha256)
            throw new ProducedRunCaptureIntegrityException(
                "source_control_output_capture_package_invalid");
    }

    private static async Task VerifyStoredPackageAsync(
        ObjectRead download,
        ProducedRunCaptureProof proof,
        CancellationToken cancellationToken)
    {
        _ = await ReadVerifiedPackageAsync(download, proof, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadVerifiedPackageAsync(
        ObjectRead download,
        ProducedRunCaptureProof proof,
        CancellationToken cancellationToken)
    {
        if (download.Length != proof.PackageByteLength ||
            download.Length is < ProducedRunCaptureLimits.PackageHeaderBytes or
                > ProducedRunCaptureLimits.MaximumPackageBytes)
            throw new ProducedRunCaptureIntegrityException(
                "source_control_output_capture_object_length_invalid");
        var bytes = new byte[checked((int)download.Length)];
        try
        {
            await download.Content.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException exception)
        {
            throw new ProducedRunCaptureIntegrityException(
                "source_control_output_capture_object_length_invalid", exception);
        }
        if (await download.Content.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0 ||
            Hash(bytes) != proof.PackageSha256)
            throw new ProducedRunCaptureIntegrityException(
                "source_control_output_capture_object_digest_invalid");
        return bytes;
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));
}

public static class ProducedRunCaptureEndpoints
{
    public static void AddProducedRunCaptureOwner(this IServiceCollection services) =>
        services.AddScoped<ProducedRunCaptureApplicationService>();

    public static void MapProducedRunCaptureEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
            "/internal/sessions/{sessionId}/produced-run-captures/{captureId}",
            async (HttpContext context, string sessionId, string captureId,
                [FromServices] ProducedRunCaptureApplicationService service, CancellationToken token) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (!string.Equals(context.Request.ContentType, "application/octet-stream",
                        StringComparison.OrdinalIgnoreCase))
                    return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
                if (context.Request.ContentLength is > ProducedRunCaptureLimits.MaximumPackageBytes)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                    limit.MaxRequestBodySize = ProducedRunCaptureLimits.MaximumPackageBytes;
                try
                {
                    var bytes = await ReadBoundedAsync(context.Request.Body, token).ConfigureAwait(false);
                    return await ExecuteAsync(context, () =>
                        service.WriteAsync(context, sessionId, captureId, bytes, token)).ConfigureAwait(false);
                }
                catch (ProducedRunCaptureRequestException exception)
                {
                    return Results.Json(new { error = exception.Code }, statusCode: exception.StatusCode);
                }
            })
            .RequireAuthorization();

        endpoints.MapGet(
            "/internal/sessions/{sessionId}/produced-run-captures/events/{eventId:guid}",
            (HttpContext context, string sessionId, Guid eventId,
                [FromServices] ProducedRunCaptureApplicationService service, CancellationToken token) =>
                ExecuteAsync(
                    context,
                    () => service.ReadAsync(context, sessionId, eventId, token),
                    result =>
                    {
                        context.Response.Headers["X-Session-Event-Position"] =
                            result.Entry.Position.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        return Results.Bytes(result.PackageBytes, "application/octet-stream");
                    }))
            .RequireAuthorization();
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream body, CancellationToken cancellationToken)
    {
        await using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (output.Length + read > ProducedRunCaptureLimits.MaximumPackageBytes)
                    throw new ProducedRunCaptureRequestException(
                        "source_control_output_capture_package_too_large", StatusCodes.Status413PayloadTooLarge);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            return output.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<IResult> ExecuteAsync<T>(
        HttpContext context,
        Func<Task<T>> action,
        Func<T, IResult>? render = null)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var result = await action().ConfigureAwait(false);
            if (render is not null)
                return render(result);
            return Results.Json(
                result,
                statusCode: result is ProducedRunCaptureAcknowledgment acknowledgment &&
                    !acknowledgment.IsDuplicate
                    ? StatusCodes.Status201Created
                    : StatusCodes.Status200OK);
        }
        catch (RuntimeAuthorizationException exception)
        {
            return Results.Json(new { error = exception.Code }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (ProjectsAuthorizationContextException exception)
        {
            return Results.Json(new { error = exception.Code }, statusCode: (int)exception.StatusCode);
        }
        catch (CoordinationOwnerClientException exception)
        {
            return Results.Json(new { error = exception.Code }, statusCode: exception.StatusCode);
        }
        catch (SessionEventConflictException)
        {
            return Results.Conflict(new { error = "source_control_output_capture_conflict" });
        }
        catch (SessionNotFoundException)
        {
            return Results.NotFound(new { error = "source_control_output_capture_not_found" });
        }
        catch (SessionAuthenticationException)
        {
            return Results.Unauthorized();
        }
        catch (SessionAccessDeniedException)
        {
            return Results.Json(new { error = "source_control_output_capture_access_denied" },
                statusCode: StatusCodes.Status403Forbidden);
        }
        catch (ProducedRunCaptureIntegrityException exception)
        {
            return Results.Json(new { error = exception.Code },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (ProducedRunCaptureRequestException exception)
        {
            return Results.Json(new { error = exception.Code }, statusCode: exception.StatusCode);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { error = "source_control_output_capture_request_invalid" });
        }
        catch (RequestFailedException)
        {
            return Results.Json(new { error = "source_control_output_capture_storage_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (HttpRequestException)
        {
            return Results.Json(new { error = "source_control_output_capture_owner_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            return Results.Json(new { error = "source_control_output_capture_owner_timeout" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
