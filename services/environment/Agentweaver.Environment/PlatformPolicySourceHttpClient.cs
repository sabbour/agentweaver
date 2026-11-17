using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Environment;

internal sealed class PlatformPolicySourceHttpClient
{
    private const int MaximumResponseBytes = 5_242_880;
    private const int MaximumErrorBytes = 4_096;
    private const int MaximumManifestBytes = 65_536;
    private const int MaximumFiles = 64;
    private const int MaximumFileBytes = 262_144;
    private const int MaximumArtifactBytes = 1_048_576;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly HashSet<string> SourceErrorCodes = new(StringComparer.Ordinal)
    {
        "not_found",
        "policy_source_peer_denied",
        "policy_source_request_invalid",
        "native_acs_policy_not_found",
        "native_acs_policy_corrupt",
        "native_acs_policy_source_unavailable",
        "policy_source_internal_error",
    };

    private readonly Uri _projectsBaseAddress;
    private readonly Func<HttpMessageHandler> _handlerFactory;
    private int _readActive;

    internal PlatformPolicySourceHttpClient(
        Uri projectsBaseAddress,
        PlatformPolicySourceClientTlsIdentity tlsIdentity)
        : this(projectsBaseAddress, tlsIdentity.CreateHandler)
    {
    }

    internal PlatformPolicySourceHttpClient(
        Uri projectsBaseAddress,
        Func<HttpMessageHandler> handlerFactory)
    {
        if (projectsBaseAddress.Scheme != Uri.UriSchemeHttps ||
            projectsBaseAddress.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(projectsBaseAddress.UserInfo) ||
            !string.IsNullOrEmpty(projectsBaseAddress.Query) ||
            !string.IsNullOrEmpty(projectsBaseAddress.Fragment))
            throw new ArgumentException("The Projects address must be an HTTPS origin.", nameof(projectsBaseAddress));
        ArgumentNullException.ThrowIfNull(handlerFactory);
        _projectsBaseAddress = projectsBaseAddress;
        _handlerFactory = handlerFactory;
    }

    internal async Task<PlatformPolicySourceReadResult> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _readActive, 1, 0) != 0)
            return Failed("policy_source_read_in_progress");

        HttpMessageHandler? handler = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(RequestTimeout);
            handler = _handlerFactory();
            using var client = new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = _projectsBaseAddress,
                Timeout = Timeout.InfiniteTimeSpan,
            };
            using var request = new HttpRequestMessage(
                HttpMethod.Get, PlatformPolicySourceClientTls.RoutePath);
            using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    deadline.Token)
                .ConfigureAwait(false);

            if (response.Headers.CacheControl?.NoStore != true)
                return Failed("policy_source_protocol_invalid");
            if (EnvironmentHttpTransport.IsRedirect(response))
                return Failed("policy_source_protocol_invalid");
            if (response.StatusCode != HttpStatusCode.OK)
                return await ReadSourceFailureAsync(response, deadline.Token).ConfigureAwait(false);
            if (!IsJson(response.Content.Headers.ContentType))
                return Failed("policy_source_protocol_invalid");

            var bytes = await ReadLimitedAsync(
                    response.Content,
                    MaximumResponseBytes,
                    deadline.Token)
                .ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            var result = ParseObservation(bytes);
            deadline.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failed("policy_source_timeout");
        }
        catch (Exception exception) when (exception is AuthenticationException or CryptographicException)
        {
            return Failed("policy_source_tls_denied");
        }
        catch (HttpRequestException exception) when (HasTlsFailure(exception))
        {
            return Failed("policy_source_tls_denied");
        }
        catch (InvalidOperationException) when (handler is null)
        {
            return Failed("policy_source_tls_denied");
        }
        catch (InvalidDataException)
        {
            return Failed("policy_source_protocol_invalid");
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or SocketException or
                InvalidOperationException or UnauthorizedAccessException)
        {
            return Failed("policy_source_unavailable");
        }
        finally
        {
            if (handler is IPlatformPolicySourceHandlerLifetime lifetime)
            {
                try
                {
                    handler.Dispose();
                }
                finally
                {
                    ReleaseReadGateWhenCompleted(lifetime.ValidationCompleted);
                }
            }
            else
            {
                try
                {
                    handler?.Dispose();
                }
                finally
                {
                    Volatile.Write(ref _readActive, 0);
                }
            }
        }
    }

    private void ReleaseReadGateWhenCompleted(Task validationCompleted)
    {
        if (validationCompleted.IsCompleted)
        {
            _ = validationCompleted.Exception;
            Volatile.Write(ref _readActive, 0);
            return;
        }

        _ = validationCompleted.ContinueWith(
            static (completed, state) =>
            {
                _ = completed.Exception;
                var client = (PlatformPolicySourceHttpClient)state!;
                Volatile.Write(ref client._readActive, 0);
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task<PlatformPolicySourceReadResult> ReadSourceFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!IsJson(response.Content.Headers.ContentType))
            return Failed("policy_source_protocol_invalid");
        var bytes = await ReadLimitedAsync(response.Content, MaximumErrorBytes, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (HasDuplicateProperties(root) ||
                !HasExactProperties(root, "code") ||
                !TryGetString(root, "code", out var code) ||
                !SourceErrorCodes.Contains(code))
                return Failed("policy_source_protocol_invalid");
            return Failed(code);
        }
        catch (JsonException)
        {
            return Failed("policy_source_protocol_invalid");
        }
    }

    private static PlatformPolicySourceReadResult ParseObservation(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (HasDuplicateProperties(root) ||
                !HasExactProperties(
                    root,
                    "scopeType",
                    "scopeId",
                    "policyId",
                    "revision",
                    "contentSha256",
                    "updatedAt",
                    "artifact",
                    "admission") ||
                !TryGetString(root, "scopeType", out var scopeType) ||
                scopeType != "platform" ||
                !TryGetString(root, "scopeId", out var scopeId) ||
                scopeId != "default" ||
                !TryGetString(root, "policyId", out var policyId) ||
                !IsPolicyId(policyId) ||
                !root.TryGetProperty("revision", out var revisionElement) ||
                revisionElement.ValueKind != JsonValueKind.Number ||
                !revisionElement.TryGetInt64(out var revision) ||
                revision <= 0 ||
                !TryGetString(root, "contentSha256", out var contentSha256) ||
                !TryGetDateTimeOffset(root, "updatedAt", out var updatedAt) ||
                !TryReadArtifact(root.GetProperty("artifact"), out var artifact) ||
                !TryReadAdmission(root.GetProperty("admission")))
                return Failed("policy_source_protocol_invalid");

            var computedHash = NativeAcsArtifactHash.Compute(artifact);
            if (!string.Equals(contentSha256, computedHash, StringComparison.Ordinal) ||
                !string.Equals(artifact.ContentSha256, computedHash, StringComparison.Ordinal))
                return Failed("policy_source_protocol_invalid");

            var observation = new PlatformPolicySourceObservation(
                $"projects-config:platform:default:{policyId}",
                revision.ToString(CultureInfo.InvariantCulture),
                policyId,
                revision,
                contentSha256,
                updatedAt,
                artifact,
                "pending",
                null);
            return new PlatformPolicySourceReadResult(observation, null);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or OverflowException)
        {
            return Failed("policy_source_protocol_invalid");
        }
    }

    private static bool TryReadArtifact(JsonElement element, out NativeAcsArtifact artifact)
    {
        artifact = null!;
        if (!HasExactProperties(element, "manifestBytes", "files", "contentSha256") ||
            !element.TryGetProperty("manifestBytes", out var manifestElement) ||
            !TryReadByteArray(manifestElement, MaximumManifestBytes, out var manifestBytes) ||
            manifestBytes.Length is < 1 or > MaximumManifestBytes ||
            !element.TryGetProperty("files", out var filesElement) ||
            filesElement.ValueKind != JsonValueKind.Array ||
            filesElement.GetArrayLength() > MaximumFiles ||
            !TryGetString(element, "contentSha256", out var artifactHash))
            return false;

        var files = ImmutableArray.CreateBuilder<NativeAcsArtifactFile>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = manifestBytes.Length;
        foreach (var fileElement in filesElement.EnumerateArray())
        {
            if (!HasExactProperties(fileElement, "relativePath", "bytes") ||
                !TryGetString(fileElement, "relativePath", out var relativePath) ||
                !IsSafeRelativePath(relativePath) ||
                !paths.Add(relativePath) ||
                !fileElement.TryGetProperty("bytes", out var fileBytesElement) ||
                !TryReadByteArray(fileBytesElement, MaximumFileBytes, out var fileBytes))
                return false;
            totalBytes += fileBytes.Length;
            if (totalBytes > MaximumArtifactBytes)
                return false;
            files.Add(new NativeAcsArtifactFile(relativePath, fileBytes.ToImmutableArray()));
        }

        artifact = new NativeAcsArtifact(
            manifestBytes.ToImmutableArray(),
            files.ToImmutable(),
            artifactHash);
        return true;
    }

    private static bool TryReadAdmission(JsonElement element) =>
        HasExactProperties(element, "state", "checkpoint") &&
        TryGetString(element, "state", out var state) &&
        state == "pending" &&
        element.TryGetProperty("checkpoint", out var checkpoint) &&
        checkpoint.ValueKind == JsonValueKind.Null;

    private static bool TryReadByteArray(JsonElement element, int maximumLength, out byte[] bytes)
    {
        bytes = [];
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > maximumLength)
            return false;
        var result = new byte[element.GetArrayLength()];
        var index = 0;
        foreach (var value in element.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Number ||
                !value.TryGetByte(out result[index]))
                return false;
            index++;
        }
        bytes = result;
        return true;
    }

    private static async Task<byte[]> ReadLimitedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var contentLength = content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > maximumBytes)
            throw new InvalidDataException("The Projects policy response exceeds its size limit.");

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(contentLength.HasValue
            ? (int)Math.Min(contentLength.Value, maximumBytes)
            : 0);
        var buffer = ArrayPool<byte>.Shared.Rent(16_384);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    return output.ToArray();
                if (read > maximumBytes - output.Length)
                    throw new InvalidDataException("The Projects policy response exceeds its size limit.");
                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool HasTlsFailure(HttpRequestException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is AuthenticationException or CryptographicException)
                return true;
        return false;
    }

    private static bool IsJson(MediaTypeHeaderValue? contentType) =>
        string.Equals(contentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetDateTimeOffset(
        JsonElement element,
        string name,
        out DateTimeOffset value)
    {
        value = default;
        return element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            property.TryGetDateTimeOffset(out value);
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString()!;
        return true;
    }

    private static bool HasExactProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var actual = element.EnumerateObject().Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal);
        return actual.SequenceEqual(expected.OrderBy(name => name, StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                    return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (HasDuplicateProperties(item))
                    return true;
        }
        return false;
    }

    private static bool IsPolicyId(string value) =>
        value.Length is >= 1 and <= 128 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsSafeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.StartsWith('/') || value.Contains('\\'))
            return false;
        foreach (var segment in value.Split('/', StringSplitOptions.None))
        {
            if (segment.Length == 0 || segment is "." or ".." ||
                segment.Contains(':') ||
                segment.Any(char.IsControl) ||
                segment.EndsWith(' ') || segment.EndsWith('.') ||
                !segment.IsNormalized(NormalizationForm.FormC) ||
                IsWindowsDeviceName(segment))
                return false;
        }
        return true;
    }

    private static bool IsWindowsDeviceName(string segment)
    {
        var name = segment.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" ||
            name.Length == 4 &&
            (name.StartsWith("COM", StringComparison.Ordinal) ||
             name.StartsWith("LPT", StringComparison.Ordinal)) &&
            name[3] is >= '1' and <= '9';
    }

    private static PlatformPolicySourceReadResult Failed(string code) =>
        new(null, code);
}

internal sealed record PlatformPolicySourceObservation(
    string SourceId,
    string SourceRevision,
    string PolicyId,
    long Revision,
    string ContentSha256,
    DateTimeOffset UpdatedAt,
    NativeAcsArtifact Artifact,
    string AdmissionState,
    long? AdmissionCheckpoint);

internal sealed record PlatformPolicySourceReadResult(
    PlatformPolicySourceObservation? Observation,
    string? FailureCode)
{
    internal bool IsSuccess => Observation is not null && FailureCode is null;
}
