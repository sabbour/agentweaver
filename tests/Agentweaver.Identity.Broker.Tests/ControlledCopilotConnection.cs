using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Secrets.AzureKeyVault;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

internal sealed class ControlledCopilotConnection : IDisposable
{
    private const string Vault = "https://identity-test-vault.vault.azure.net/";
    private readonly Dictionary<SecretRef, (string Value, DateTimeOffset ExpiresAt)> _versions = [];
    private readonly HttpClient _vault;
    private readonly AzureKeyVaultSecretRedemption _reader;
    private readonly AzureKeyVaultSecretVersionWriter _writer;
    private int _version;
    public int Exchanges { get; private set; }
    public int Writes { get; private set; }
    public bool InstallationToken { get; set; }
    public HttpStatusCode ExchangeStatus { get; set; } = HttpStatusCode.OK;
    public string? ExchangeError { get; set; }
    public bool LoseExchangeResponse { get; set; }
    public long GitHubUserId { get; set; } = 42;
    public string AccessToken { get; private set; } = UserAccessToken;
    public string RefreshToken { get; private set; } = UserRefreshToken;
    public Func<CancellationToken, Task>? BeforeExchangeResponse { get; set; }
    public Func<CancellationToken, Task>? BeforeWriteResponse { get; set; }
    public List<SecretRedemptionRequest> Reads { get; } = [];
    public const string UserAccessToken = "ghu_controlled-user-access";
    public const string UserRefreshToken = "ghr_controlled-user-refresh";
    public const string ClientSecret = "controlled-oauth-client-secret";

    public ControlledCopilotConnection()
    {
        _versions.Add(new SecretRef("copilot-oauth-client", "v1"),
            (ClientSecret, DateTimeOffset.UtcNow.AddDays(1)));
        _vault = new HttpClient(new Handler(VaultAsync));
        var configuration = new AzureKeyVaultConfiguration(new(Vault));
        var credential = new Credential();
        _reader = new AzureKeyVaultSecretRedemption(configuration, credential,
            new SecretClientOptions { Transport = new HttpClientTransport(_vault), Retry = { MaxRetries = 0 } });
        _writer = new AzureKeyVaultSecretVersionWriter(configuration, credential,
            new SecretClientOptions { Transport = new HttpClientTransport(_vault), Retry = { MaxRetries = 0 } });
    }

    public void ConfigureSettings(Dictionary<string, string?> settings)
    {
        settings["IdentityBroker__CopilotConnection__ProjectsOwnerAddress"] = "https://projects.test/";
        settings["IdentityBroker__CopilotConnection__ClientId"] = "controlled-github-oauth";
        settings["IdentityBroker__CopilotConnection__CallbackUri"] = "https://client.test/copilot-callback";
        settings["IdentityBroker__CopilotConnection__ClientSecretReference__Id"] = "copilot-oauth-client";
        settings["IdentityBroker__CopilotConnection__ClientSecretReference__Version"] = "v1";
    }

    public void ConfigureServices(IServiceCollection services, Func<HttpMessageHandler> projects)
    {
        services.RemoveAll<ISecretRedemption>();
        services.RemoveAll<ISecretVersionWriter>();
        services.AddSingleton<ISecretRedemption>(new RecordingReader(this));
        services.AddSingleton<ISecretVersionWriter>(_writer);
        services.AddHttpClient("CopilotConnectionProjects").ConfigurePrimaryHttpMessageHandler(projects);
        services.AddHttpClient("CopilotConnectionGitHub")
            .ConfigurePrimaryHttpMessageHandler(() => new Handler(GitHubAsync));
    }

    private async Task<HttpResponseMessage> GitHubAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.Host == "github.com")
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/login/oauth/access_token", request.RequestUri.AbsolutePath);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.Contains("client_id=controlled-github-oauth", body);
            Assert.Contains($"client_secret={ClientSecret}", body);
            Assert.True(body.Contains("code=controlled-code", StringComparison.Ordinal) &&
                body.Contains("code_verifier=", StringComparison.Ordinal) ||
                body.Contains($"refresh_token={RefreshToken}", StringComparison.Ordinal));
            Exchanges++;
            if (BeforeExchangeResponse is { } wait)
                await wait(cancellationToken);
            if (LoseExchangeResponse)
                throw new HttpRequestException("The controlled rotation response was lost.");
            if (ExchangeError is not null || ExchangeStatus != HttpStatusCode.OK)
                return new(ExchangeStatus)
                {
                    RequestMessage = request, Content = JsonContent.Create(new { error = ExchangeError })
                };
            AccessToken = Exchanges == 1 ? UserAccessToken : $"{UserAccessToken}-{Exchanges}";
            RefreshToken = Exchanges == 1 ? UserRefreshToken : $"{UserRefreshToken}-{Exchanges}";
            return Json(new
            {
                access_token = InstallationToken ? "ghs_controlled-installation" : AccessToken,
                token_type = "bearer", expires_in = 3600,
                refresh_token = RefreshToken, refresh_token_expires_in = 86400
            }, request);
        }
        Assert.Equal("api.github.com", request.RequestUri.Host);
        Assert.Equal("/user", request.RequestUri.AbsolutePath);
        Assert.Equal(AccessToken, request.Headers.Authorization?.Parameter);
        return Json(new { id = GitHubUserId, type = "User" }, request);
    }

    private async Task<HttpResponseMessage> VaultAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization is null)
        {
            var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = request };
            challenge.Headers.TryAddWithoutValidation("WWW-Authenticate",
                """Bearer authorization="https://login.windows.net/test", resource="https://vault.azure.net" """);
            return challenge;
        }
        var path = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("secrets", path[0]);
        SecretRef reference;
        (string Value, DateTimeOffset ExpiresAt) secret;
        if (request.Method == HttpMethod.Put)
        {
            Assert.Equal(2, path.Length);
            Assert.Empty(request.Headers.IfMatch);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            reference = new SecretRef(path[1], $"version-{++_version}");
            secret = (body.RootElement.GetProperty("value").GetString()!,
                DateTimeOffset.FromUnixTimeSeconds(body.RootElement.GetProperty("attributes").GetProperty("exp").GetInt64()));
            _versions.Add(reference, secret);
            Writes++;
            if (BeforeWriteResponse is { } wait)
                await wait(cancellationToken);
        }
        else
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(3, path.Length);
            reference = new SecretRef(path[1], path[2]);
            secret = _versions[reference];
        }
        return Json(new
        {
            id = $"{Vault}secrets/{reference.Id}/{reference.Version}",
            value = secret.Value,
            attributes = new { enabled = true, exp = secret.ExpiresAt.ToUnixTimeSeconds() }
        }, request);
    }

    private static HttpResponseMessage Json(object value, HttpRequestMessage request) => new(HttpStatusCode.OK)
    {
        RequestMessage = request, Content = JsonContent.Create(value)
    };

    public void Dispose()
    {
        _reader.Dispose();
        _vault.Dispose();
    }

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken) =>
            new("controlled-vault-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(context, cancellationToken));
    }

    private sealed class Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken);
    }

    private sealed class RecordingReader(ControlledCopilotConnection connection) : ISecretRedemption
    {
        public Task<SecretCredential> RedeemAsync(SecretRedemptionRequest request, CancellationToken cancellationToken)
        {
            connection.Reads.Add(request);
            return connection._reader.RedeemAsync(request, cancellationToken);
        }
    }
}
