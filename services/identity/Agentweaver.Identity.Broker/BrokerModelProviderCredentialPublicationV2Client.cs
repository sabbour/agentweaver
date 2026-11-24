using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace Agentweaver.Identity.Broker;

internal sealed class BrokerModelProviderCredentialPublicationV2Client(
    IdentityBrokerOptions brokerOptions,
    X509Certificate2 signingCertificate,
    HttpClient projectsClient,
    TimeProvider timeProvider)
{
    private const string Purpose = "credential-publication-v2";
    private const int MaximumResponseBytes = 16 * 1024;
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal Task<ModelProviderCredentialPublicationPreflightResponse> PreflightAsync(
        ModelProviderCredentialPublicationPreflightRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<ModelProviderCredentialPublicationPreflightRequest, ModelProviderCredentialPublicationPreflightResponse>(
            "/internal/byok/credential-publications/v2/preflight",
            request,
            request.OperationId,
            request.Scope,
            request.ProjectId,
            request.ProviderId,
            request.ActorId,
            request.Issuer,
            response => response.Request == request && response.PreflightId != Guid.Empty,
            cancellationToken);

    internal Task<ModelProviderCredentialPublicationReceipt> PublishAsync(
        ModelProviderCredentialPublicationV2Request request,
        CancellationToken cancellationToken) =>
        SendAsync<ModelProviderCredentialPublicationV2Request, ModelProviderCredentialPublicationReceipt>(
            "/internal/byok/credential-publications/v2/publish",
            request,
            request.OperationId,
            request.Scope,
            request.ProjectId,
            request.ProviderId,
            request.ActorId,
            request.Issuer,
            receipt => receipt.IsValid &&
                       receipt.OperationId == request.OperationId &&
                       receipt.Scope == request.Scope &&
                       receipt.ProjectId == request.ProjectId &&
                       receipt.ProviderId == request.ProviderId &&
                       receipt.ProviderRevision == request.ExpectedProviderRevision + 1 &&
                       receipt.CredentialVersion == request.CredentialVersion &&
                       receipt.CredentialIdentity == request.CredentialIdentity &&
                       receipt.CredentialReference == request.CredentialReference &&
                       receipt.ProviderState is null or "Draft" or "Available",
            cancellationToken);

    internal Task<ModelProviderCredentialCancelResponse> CancelAsync(
        ModelProviderCredentialCancelRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<ModelProviderCredentialCancelRequest, ModelProviderCredentialCancelResponse>(
            "/internal/byok/credential-publications/v2/cancel",
            request,
            request.OperationId,
            request.Scope,
            request.ProjectId,
            request.ProviderId,
            request.ActorId,
            request.Issuer,
            response => response.OperationId == request.OperationId &&
                        (response.Outcome == "cancelled" && response.Receipt is null ||
                         response.Outcome == "published" && response.Receipt is { IsValid: true } r &&
                         r.OperationId == request.OperationId &&
                         r.Scope == request.Scope &&
                         r.ProjectId == request.ProjectId &&
                         r.ProviderId == request.ProviderId),
            cancellationToken);

    internal Task<ModelProviderCredentialDisableReceipt> DisableAsync(
        ModelProviderCredentialDisableRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<ModelProviderCredentialDisableRequest, ModelProviderCredentialDisableReceipt>(
            "/internal/byok/credential-publications/v2/disable",
            request,
            request.OperationId,
            request.Scope,
            request.ProjectId,
            request.ProviderId,
            request.ActorId,
            request.Issuer,
            receipt => receipt.OperationId == request.OperationId &&
                       receipt.Scope == request.Scope &&
                       receipt.ProjectId == request.ProjectId &&
                       receipt.ProviderId == request.ProviderId &&
                       receipt.OriginalProviderRevision > 0 &&
                       receipt.ProviderRevision == receipt.OriginalProviderRevision + 1 &&
                       receipt.OriginalCredentialRevision == request.OriginalCredentialRevision &&
                       receipt.CredentialSlotId == request.ExpectedCredentialSlotId,
            cancellationToken);

    internal Task<ModelProviderCredentialOperationReadResponse> ReadOperationAsync(
        ModelProviderCredentialOperationReadRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<ModelProviderCredentialOperationReadRequest, ModelProviderCredentialOperationReadResponse>(
            "/internal/byok/credential-publications/v2/read-operation",
            request,
            request.OperationId,
            request.Scope,
            request.ProjectId,
            request.ProviderId,
            request.ActorId,
            request.Issuer,
            response => response.Request == request &&
                        !string.IsNullOrWhiteSpace(response.ResolvedTenant) &&
                        response.Outcome is null or "published" or "cancelled" or "disabled",
            cancellationToken);

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        string path,
        TRequest payload,
        Guid operationId,
        ModelProviderBindingScope scope,
        string? projectId,
        string providerId,
        string actorId,
        string actorIssuer,
        Func<TResponse, bool> validateResponse,
        CancellationToken cancellationToken)
    {
        if (!BrokerRunBindingAuthority.TryGetSettings(brokerOptions, out var ownerAddress, out var audience))
            throw new BrokerCredentialPublicationException(null);

        var endpoint = new Uri(ownerAddress, path);
        var token = CreateToken(audience, operationId, scope, projectId, providerId, actorId, actorIssuer);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, NoCache = true };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(projectsClient.Timeout);
            using var response = await projectsClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri != endpoint ||
                (int)response.StatusCode is >= 300 and <= 399 ||
                !response.IsSuccessStatusCode)
            {
                string? errorCode = null;
                if (response.Content.Headers.ContentType?.MediaType?.Equals("application/json", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var errBytes = await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false);
                    if (errBytes is not null)
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(errBytes);
                            if (doc.RootElement.TryGetProperty("code", out var codeProp) &&
                                codeProp.ValueKind == JsonValueKind.String)
                                errorCode = codeProp.GetString();
                        }
                        catch (JsonException) { }
                    }
                }
                throw new BrokerCredentialPublicationV2Exception(response.StatusCode, errorCode);
            }

            if (response.Headers.CacheControl?.NoStore != true ||
                !string.Equals(
                    response.Content.Headers.ContentType?.MediaType,
                    "application/json",
                    StringComparison.OrdinalIgnoreCase) ||
                response.Content.Headers.ContentLength is { } length &&
                (length <= 0 || length > MaximumResponseBytes))
                throw new BrokerCredentialPublicationV2Exception(null, null);

            var bytes = await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false);
            if (bytes is null)
                throw new BrokerCredentialPublicationV2Exception(null, null);

            TResponse? result;
            try
            {
                result = JsonSerializer.Deserialize<TResponse>(bytes, JsonOptions);
            }
            catch (JsonException)
            {
                throw new BrokerCredentialPublicationV2Exception(null, null);
            }

            if (result is null || !validateResponse(result))
                throw new BrokerCredentialPublicationV2Exception(null, null);

            return result;
        }
        catch (BrokerCredentialPublicationV2Exception)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw new BrokerCredentialPublicationV2Exception(null, null);
        }
        catch (IOException)
        {
            throw new BrokerCredentialPublicationV2Exception(null, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BrokerCredentialPublicationV2Exception(null, null);
        }
    }

    private string CreateToken(
        string audience,
        Guid operationId,
        ModelProviderBindingScope scope,
        string? projectId,
        string providerId,
        string actorId,
        string actorIssuer)
    {
        var now = timeProvider.GetUtcNow();
        var claims = new[]
        {
            new Claim("sub", "identity-broker"),
            new Claim("purpose", Purpose),
            new Claim("actor_id", actorId),
            new Claim("actor_iss", actorIssuer),
            new Claim("operation_id", operationId.ToString("D")),
            new Claim("scope", scope.ToString().ToLowerInvariant()),
            new Claim("project_id", projectId ?? string.Empty),
            new Claim("provider_id", providerId),
            new Claim(
                JwtRegisteredClaimNames.Iat,
                now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64),
            new Claim("jti", Guid.NewGuid().ToString("N")),
        };
        var token = new JwtSecurityToken(
            new Uri(brokerOptions.Issuer).AbsoluteUri,
            audience,
            claims,
            now.UtcDateTime.AddSeconds(-5),
            now.Add(TokenLifetime).UtcDateTime,
            new SigningCredentials(new X509SecurityKey(signingCertificate), SecurityAlgorithms.RsaSha256));
        token.Header["typ"] = BrokerModelProviderCredentialPublicationClient.JwtType;
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var available = MaximumResponseBytes + 1 - (int)bytes.Length;
            var count = await stream.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, available)),
                cancellationToken).ConfigureAwait(false);
            if (count == 0)
                return bytes.ToArray();
            if (bytes.Length + count > MaximumResponseBytes)
                return null;
            await bytes.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
    }
}

internal sealed class BrokerCredentialPublicationV2Exception(HttpStatusCode? statusCode, string? errorCode)
    : Exception("The Projects credential publication v2 request failed.")
{
    internal HttpStatusCode? StatusCode { get; } = statusCode;
    internal string? ErrorCode { get; } = errorCode;
}
