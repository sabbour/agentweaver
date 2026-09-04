using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using IPNetwork = System.Net.IPNetwork;

namespace Agentweaver.Api.Auth.OAuth;

public sealed record OAuthServerConfiguration(
    Uri PublicOrigin,
    Uri Resource,
    IReadOnlyList<OAuthStaticClient> StaticClients,
    bool EnableClaudeHostedClient,
    int DynamicRegistrationsPerDay,
    int DynamicRegistrationsTotal,
    TimeSpan DynamicRegistrationLifetime,
    IReadOnlyList<IPNetwork> TrustedProxyNetworks)
{
    public const string McpScope = "mcp:invoke";
    public static readonly TimeSpan RefreshTokenFamilyLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan RefreshReplayRetention = RefreshTokenFamilyLifetime + TimeSpan.FromDays(7);

    public static OAuthServerConfiguration Resolve(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var value = configuration["Auth:OAuth:PublicOrigin"];
        if (string.IsNullOrWhiteSpace(value))
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException("Auth:OAuth:PublicOrigin is required outside Development.");
            value = "http://localhost:5000";
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.IsWellFormedOriginalString()
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath != "/"
            || (!environment.IsDevelopment() && uri.Scheme != Uri.UriSchemeHttps)
            || (environment.IsDevelopment()
                && uri.Scheme != Uri.UriSchemeHttps
                && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            throw new InvalidOperationException(
                "Auth:OAuth:PublicOrigin must be an HTTPS origin with no path, query, fragment, or userinfo " +
                "(HTTP loopback is allowed only in Development).");
        }

        var origin = new Uri(uri.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
        var resource = new Uri(origin, "/mcp");
        var enableClaudeHostedClient = configuration.GetValue(
            "Auth:OAuth:EnableClaudeHostedClient", true);
        var configuredClients = configuration.GetSection("Auth:OAuth:Clients")
            .Get<OAuthStaticClient[]>() ?? [];
        var clients = enableClaudeHostedClient
            ? configuredClients.Prepend(new OAuthStaticClient
            {
                ClientId = OAuthKnownClients.ClaudeHostedClientId,
                DisplayName = "Claude hosted connectors",
                RedirectUris = [OAuthKnownClients.ClaudeHostedRedirectUri],
            }).ToArray()
            : configuredClients;
        foreach (var client in clients)
            client.Validate();
        if (clients.Select(client => client.ClientId).Distinct(StringComparer.Ordinal).Count() != clients.Length)
            throw new InvalidOperationException("Static OAuth client IDs must be unique.");
        if (clients.SelectMany(client => client.RedirectUris).Distinct(StringComparer.Ordinal).Count()
            != clients.Sum(client => client.RedirectUris.Length))
            throw new InvalidOperationException("Static OAuth client redirect URIs must be unique.");

        var perDay = configuration.GetValue("Auth:OAuth:DynamicRegistration:PerSourcePerDay", 20);
        var total = configuration.GetValue("Auth:OAuth:DynamicRegistration:MaximumActive", 1000);
        var lifetimeDays = configuration.GetValue("Auth:OAuth:DynamicRegistration:LifetimeDays", 30);
        if (perDay is < 1 or > 100 || total is < 1 or > 10000 || lifetimeDays is < 1 or > 365)
            throw new InvalidOperationException("OAuth dynamic-registration quotas are outside supported bounds.");

        var trustedNetworksValue = configuration["Auth:OAuth:ForwardedHeaders:TrustedNetworks"];
        if (string.IsNullOrWhiteSpace(trustedNetworksValue))
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException(
                    "Auth:OAuth:ForwardedHeaders:TrustedNetworks is required outside Development.");
            trustedNetworksValue = "127.0.0.0/8,::1/128";
        }

        var trustedNetworks = trustedNetworksValue
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseTrustedProxyNetwork)
            .Distinct()
            .ToArray();
        if (trustedNetworks.Length == 0)
            throw new InvalidOperationException("At least one trusted OAuth proxy network is required.");

        return new(
            origin,
            resource,
            clients,
            enableClaudeHostedClient,
            perDay,
            total,
            TimeSpan.FromDays(lifetimeDays),
            trustedNetworks);
    }

    private static IPNetwork ParseTrustedProxyNetwork(string value)
    {
        if (!IPNetwork.TryParse(value, out var network)
            || !IsPrivateOrLoopback(network.BaseAddress)
            || (network.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && network.PrefixLength < 8)
            || (network.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                && network.PrefixLength < 7))
        {
            throw new InvalidOperationException(
                $"OAuth trusted proxy network '{value}' must be a bounded private or loopback CIDR.");
        }

        return network;
    }

    private static bool IsPrivateOrLoopback(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        var bytes = address.GetAddressBytes();
        return bytes.Length switch
        {
            4 => bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168),
            16 => (bytes[0] & 0xFE) == 0xFC,
            _ => false,
        };
    }
}

public static class OAuthForwardedHeaders
{
    public static void Configure(
        ForwardedHeadersOptions options,
        OAuthServerConfiguration configuration)
    {
        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor |
            ForwardedHeaders.XForwardedProto |
            ForwardedHeaders.XForwardedHost;
        options.ForwardLimit = 1;
        options.RequireHeaderSymmetry = false;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var network in configuration.TrustedProxyNetworks)
            options.KnownIPNetworks.Add(network);
        options.AllowedHosts.Clear();
        options.AllowedHosts.Add(configuration.PublicOrigin.Host);
    }
}

public sealed class OAuthStaticClient
{
    public required string ClientId { get; init; }
    public required string DisplayName { get; init; }
    public required string[] RedirectUris { get; init; }
    public string[] Scopes { get; init; } = [OAuthServerConfiguration.McpScope];

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ClientId) || ClientId.Length > 100
            || string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 200
            || RedirectUris is not { Length: > 0 and <= 10 }
            || RedirectUris.Any(uri => !OAuthRedirectUriValidator.IsValid(uri, allowDynamicLoopbackPort: false))
            || Scopes.Length == 0
            || Scopes.Any(scope => scope is not OAuthServerConfiguration.McpScope and not "offline_access"))
        {
            throw new InvalidOperationException($"Invalid static OAuth client '{ClientId}'.");
        }

        if (string.Equals(ClientId, OAuthKnownClients.ClaudeHostedClientId, StringComparison.Ordinal)
            && (RedirectUris.Length != 1
                || !string.Equals(
                    RedirectUris[0],
                    OAuthKnownClients.ClaudeHostedRedirectUri,
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Static OAuth client '{OAuthKnownClients.ClaudeHostedClientId}' must use the exact trusted Claude callback.");
        }
    }
}

public static class OAuthKnownClients
{
    public const string ClaudeHostedClientId = "agentweaver-claude";
    public const string ClaudeHostedRedirectUri = "https://claude.ai/api/mcp/auth_callback";
}

public static class OAuthRedirectUriValidator
{
    public static bool IsValid(
        string? value,
        bool allowDynamicLoopbackPort,
        bool allowHttps = true)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 2048
            || value.Contains('*', StringComparison.Ordinal)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.IsWellFormedOriginalString()
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
            return false;

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            if (!allowHttps)
                return false;
            return !string.IsNullOrWhiteSpace(uri.Host);
        }

        if (!IsNativePrivateUseScheme(uri.Scheme) && uri.Scheme != Uri.UriSchemeHttp)
            return false;

        if (IsNativePrivateUseScheme(uri.Scheme))
            return string.IsNullOrEmpty(uri.Host)
                && uri.AbsolutePath is not "" and not "/";

        if (!IsLiteralLoopback(uri.Host) || !HasLiteralLoopbackAuthority(value))
            return false;

        return allowDynamicLoopbackPort || !uri.IsDefaultPort;
    }

    private static bool IsNativePrivateUseScheme(string scheme)
    {
        if (string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        var labels = scheme.Split('.');
        return labels.Length >= 3
            && labels.All(label =>
                label.Length is > 0 and <= 63
                && char.IsAsciiLetterOrDigit(label[0])
                && char.IsAsciiLetterOrDigit(label[^1])
                && label.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-'));
    }

    private static bool IsLiteralLoopback(string host) =>
        string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
        || string.Equals(host, "[::1]", StringComparison.Ordinal)
        || string.Equals(host, "::1", StringComparison.Ordinal);

    private static bool HasLiteralLoopbackAuthority(string value) =>
        value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)
        || value.StartsWith("http://127.0.0.1/", StringComparison.Ordinal)
        || value.StartsWith("http://[::1]:", StringComparison.Ordinal)
        || value.StartsWith("http://[::1]/", StringComparison.Ordinal);
}

public sealed record OAuthCertificateSet(
    IReadOnlyList<SecurityKey> SigningKeys,
    IReadOnlyList<SecurityKey> EncryptionKeys,
    IReadOnlyList<X509Certificate2> Certificates);

public static class OAuthCertificateLoader
{
    public static async Task<OAuthCertificateSet> LoadAsync(
        IConfiguration configuration,
        IHostEnvironment environment,
        SecretClient? client,
        CancellationToken ct = default)
    {
        var signingName = configuration["Auth:OAuth:Certificates:SigningName"];
        var encryptionName = configuration["Auth:OAuth:Certificates:EncryptionName"];
        if (client is null || string.IsNullOrWhiteSpace(signingName) || string.IsNullOrWhiteSpace(encryptionName))
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException(
                    "Durable OAuth signing and encryption certificate names in Azure Key Vault are required.");
            return CreateDevelopmentKeys();
        }

        var certificates = new List<X509Certificate2>();
        var signing = await LoadVersionsAsync(client, signingName, certificates, ct).ConfigureAwait(false);
        var encryption = await LoadVersionsAsync(client, encryptionName, certificates, ct).ConfigureAwait(false);
        if (signing.Count == 0 || encryption.Count == 0)
            throw new InvalidOperationException("No usable OAuth signing or encryption certificate version was found.");

        return new(signing, encryption, certificates);
    }

    private static async Task<IReadOnlyList<SecurityKey>> LoadVersionsAsync(
        SecretClient client,
        string name,
        List<X509Certificate2> certificates,
        CancellationToken ct)
    {
        var versions = new List<(DateTimeOffset Created, string Version)>();
        await foreach (var properties in client.GetPropertiesOfSecretVersionsAsync(name, ct))
        {
            var now = DateTimeOffset.UtcNow;
            if (properties.Enabled != false
                && (properties.NotBefore is null || properties.NotBefore <= now)
                && (properties.ExpiresOn is null || properties.ExpiresOn > now))
                versions.Add((properties.CreatedOn ?? DateTimeOffset.MinValue, properties.Version));
        }

        var keys = new List<SecurityKey>();
        foreach (var (_, version) in versions.OrderByDescending(x => x.Created).Take(2))
        {
            var secret = await client.GetSecretAsync(name, version, ct).ConfigureAwait(false);
            var certificate = LoadCertificate(secret.Value.Value);
            using var rsa = certificate.GetRSAPrivateKey();
            if (!certificate.HasPrivateKey
                || rsa is null
                || rsa.KeySize < 2048
                || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow
                || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            {
                certificate.Dispose();
                continue;
            }
            certificates.Add(certificate);
            keys.Add(CreateSecurityKey(certificate));
        }
        return keys;
    }

    private static X509Certificate2 LoadCertificate(string value)
    {
        if (value.Contains("BEGIN", StringComparison.Ordinal))
            return X509Certificate2.CreateFromPem(value, value);
        return X509CertificateLoader.LoadPkcs12(
            Convert.FromBase64String(value), password: null,
            X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
    }

    private static OAuthCertificateSet CreateDevelopmentKeys()
    {
        static X509SecurityKey Create(string usage, List<X509Certificate2> certificates)
        {
            using var rsa = RSA.Create(3072);
            var request = new CertificateRequest(
                $"CN=Agentweaver development OAuth {usage}", rsa,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(7));
            certificates.Add(certificate);
            return CreateSecurityKey(certificate);
        }

        var certificates = new List<X509Certificate2>();
        return new([Create("signing", certificates)], [Create("encryption", certificates)], certificates);
    }

    internal static string ComputeKid(X509Certificate2 certificate) =>
        Base64UrlEncoder.Encode(SHA256.HashData(certificate.RawData));

    internal static X509SecurityKey CreateSecurityKey(X509Certificate2 certificate) =>
        new X509SecurityKey(certificate) { KeyId = ComputeKid(certificate) };

    internal static string HashOpaque(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
