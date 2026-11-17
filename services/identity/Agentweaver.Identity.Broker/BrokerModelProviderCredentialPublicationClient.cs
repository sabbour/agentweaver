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

internal sealed class BrokerModelProviderCredentialPublicationClient(
    IdentityBrokerOptions brokerOptions,
    X509Certificate2 signingCertificate,
    HttpClient projectsClient,
    TimeProvider timeProvider)
{
    internal const string JwtType = "agentweaver-credential-publication+jwt";
    internal const string Purpose = "credential-publication";
    private const int MaximumResponseBytes = 16 * 1024;
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };

    internal async Task<ModelProviderCredentialPublicationReceipt> PublishAsync(
        ModelProviderCredentialPublicationRequest publication,
        string actorId,
        CancellationToken cancellationToken)
    {
        if (publication is null ||
            !publication.IsValid ||
            string.IsNullOrWhiteSpace(actorId) ||
            actorId.Length > 256 ||
            actorId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-' or ':')) ||
            !BrokerRunBindingAuthority.TryGetSettings(brokerOptions, out var ownerAddress, out var audience))
            throw new BrokerCredentialPublicationException(null);

        var endpoint = new Uri(ownerAddress, "/internal/byok/credential-publications");
        var token = CreateToken(audience, publication, actorId);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(publication, JsonOptions)),
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
                throw new BrokerCredentialPublicationException(response.StatusCode);

            if (response.Headers.CacheControl?.NoStore != true ||
                !string.Equals(
                    response.Content.Headers.ContentType?.MediaType,
                    "application/json",
                    StringComparison.OrdinalIgnoreCase) ||
                response.Content.Headers.ContentLength is { } length &&
                (length <= 0 || length > MaximumResponseBytes))
                throw new BrokerCredentialPublicationException(null);

            var bytes = await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false);
            if (bytes is null)
                throw new BrokerCredentialPublicationException(null);

            ModelProviderCredentialPublicationReceipt? receipt;
            try
            {
                receipt = JsonSerializer.Deserialize<ModelProviderCredentialPublicationReceipt>(bytes, JsonOptions);
            }
            catch (JsonException)
            {
                throw new BrokerCredentialPublicationException(null);
            }

            if (receipt is null ||
                !receipt.IsValid ||
                receipt.OperationId != publication.OperationId ||
                receipt.Scope != publication.Scope ||
                receipt.ProjectId != publication.ProjectId ||
                receipt.ProviderId != publication.ProviderId ||
                receipt.ProviderRevision != publication.ExpectedProviderRevision + 1 ||
                receipt.CredentialVersion != publication.CredentialVersion)
                throw new BrokerCredentialPublicationException(null);

            return receipt;
        }
        catch (BrokerCredentialPublicationException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw new BrokerCredentialPublicationException(null);
        }
        catch (IOException)
        {
            throw new BrokerCredentialPublicationException(null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BrokerCredentialPublicationException(null);
        }
    }

    private string CreateToken(
        string audience,
        ModelProviderCredentialPublicationRequest publication,
        string actorId)
    {
        var now = timeProvider.GetUtcNow();
        var claims = new[]
        {
            new Claim("sub", "identity-broker"),
            new Claim("purpose", Purpose),
            new Claim("actor_id", actorId),
            new Claim("operation_id", publication.OperationId.ToString("D")),
            new Claim(
                "expected_provider_revision",
                publication.ExpectedProviderRevision.ToString(CultureInfo.InvariantCulture)),
            new Claim("scope", publication.Scope.ToString().ToLowerInvariant()),
            new Claim("project_id", publication.ProjectId ?? string.Empty),
            new Claim("provider_id", publication.ProviderId),
            new Claim("credential_version", publication.CredentialVersion),
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
        token.Header["typ"] = JwtType;
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

internal sealed class BrokerCredentialPublicationException(HttpStatusCode? statusCode)
    : Exception("The Projects credential publication failed.")
{
    internal HttpStatusCode? StatusCode { get; } = statusCode;
}
