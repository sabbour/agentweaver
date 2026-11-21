using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using Npgsql;

namespace Agentweaver.EventsAndSessions.Cost;

/// <summary>Maps the platform-admin manual runtime rate-card write route.</summary>
public static partial class RuntimeRateCardManualWriteEndpoint
{
    public const string Route = "/api/platform/model-pricing/rate-cards/{id}/{version}";
    public const int MaximumRequestBodyBytes = 64 * 1024;
    public const int MaximumResponseBytes = 256 * 1024;

    private static readonly Regex IdPattern = LiteralKeyRegex();
    private static readonly Regex VersionPattern = LiteralKeyRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex LiteralKeyRegex();

    /// <summary>Registers the authenticated manual runtime rate-card write route.</summary>
    public static IEndpointRouteBuilder MapRuntimeRateCardManualWriteEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut(Route, WriteAsync).RequireAuthorization();
        return app;
    }

    internal static async Task<IResult> WriteAsync(
        string id,
        string version,
        HttpContext context,
        ProjectsAuthorizationContextClient projects,
        RuntimeRateCardStore store,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";

        if (!IsValidRouteRequest(context, id, version))
            return Problem(StatusCodes.Status400BadRequest, "rate_card_request_invalid");

        try
        {
            var audience = configuration["Identity:Audience"]
                ?? throw new InvalidOperationException("Missing Events audience.");
            await projects.AuthorizePlatformModelPricingRateCardWriteAsync(
                context, audience, cancellationToken).ConfigureAwait(false);
        }
        catch (ProjectsAuthorizationContextException exception)
        {
            return Problem((int)exception.StatusCode, exception.Code);
        }

        if (!IsSupportedJsonContentType(context.Request.ContentType))
            return Problem(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type");

        if (context.Request.ContentLength is > MaximumRequestBodyBytes)
            return Problem(StatusCodes.Status413PayloadTooLarge, "request_body_too_large");

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeLimit)
            sizeLimit.MaxRequestBodySize = MaximumRequestBodyBytes;

        byte[] payload;
        try
        {
            payload = await ReadBoundedAsync(context.Request.Body, cancellationToken).ConfigureAwait(false);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, "request_body_too_large");
        }
        catch (InvalidDataException)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, "request_body_too_large");
        }

        RuntimeRateCard runtimeRateCard;
        string canonical;
        try
        {
            var parsed = RuntimeRateCardReadContract.Deserialize(payload);
            if (parsed is null ||
                parsed.Provenance is not ManualRuntimeRateCardReadProvenance manual ||
                !string.Equals(parsed.RateCard.Id, id, StringComparison.Ordinal) ||
                !string.Equals(parsed.RateCard.Version, version, StringComparison.Ordinal))
                return Problem(StatusCodes.Status400BadRequest, "rate_card_invalid");

            runtimeRateCard = new RuntimeRateCard(
                parsed.RateCard,
                new ManualRateCardProvenance(manual.SourceUrl, manual.RetrievedOn));
            RuntimeRateCardContract.Validate(runtimeRateCard);
            canonical = RuntimeRateCardContract.SerializeCanonical(runtimeRateCard);
            if (Encoding.UTF8.GetByteCount(canonical) > MaximumResponseBytes)
                return Problem(StatusCodes.Status400BadRequest, "rate_card_too_large");
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            return Problem(StatusCodes.Status400BadRequest, "rate_card_invalid");
        }

        bool inserted;
        try
        {
            inserted = await store.SaveAsync(runtimeRateCard, cancellationToken).ConfigureAwait(false);
        }
        catch (RuntimeRateCardConflictException)
        {
            return Problem(StatusCodes.Status409Conflict, "rate_card_conflict");
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "rate_card_storage_unavailable");
        }

        var canonicalBytes = Encoding.UTF8.GetBytes(canonical);
        var hash = Convert.ToHexString(SHA256.HashData(canonicalBytes)).ToLowerInvariant();
        context.Response.Headers.ETag = new EntityTagHeaderValue($"\"{hash}\"").ToString();
        return Results.Text(
            canonical,
            "application/json",
            Encoding.UTF8,
            inserted ? StatusCodes.Status201Created : StatusCodes.Status200OK);
    }

    private static bool IsValidRouteRequest(HttpContext context, string id, string version)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 128 || !IdPattern.IsMatch(id) ||
            string.IsNullOrEmpty(version) || version.Length > 64 || !VersionPattern.IsMatch(version))
            return false;

        if (context.Request.Query.Count != 0 || context.Request.QueryString.HasValue)
            return false;

        var expectedPath = $"{context.Request.PathBase.Value}/api/platform/model-pricing/rate-cards/{id}/{version}";
        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (!string.IsNullOrEmpty(rawTarget))
            return string.Equals(rawTarget, expectedPath, StringComparison.Ordinal);

        return string.Equals(context.Request.Path.Value, expectedPath, StringComparison.Ordinal);
    }

    private static bool IsSupportedJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType) ||
            !System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var parsed) ||
            !string.Equals(parsed.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            return false;

        return string.IsNullOrEmpty(parsed.CharSet) ||
            string.Equals(parsed.CharSet.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream body, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var read = await body.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, MaximumRequestBodyBytes - output.Length + 1)),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    return output.ToArray();
                if (output.Length + read > MaximumRequestBodyBytes)
                    throw new InvalidDataException("Request body exceeds the 64 KiB limit.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static IResult Problem(int statusCode, string code) =>
        Results.Problem(
            statusCode: statusCode,
            title: code,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}
