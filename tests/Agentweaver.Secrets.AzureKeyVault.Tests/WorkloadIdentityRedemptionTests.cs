using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Secrets.AzureKeyVault;
using Azure.Core.Diagnostics;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Xunit;

namespace Agentweaver.Secrets.AzureKeyVault.Tests;

public sealed class WorkloadIdentityRedemptionTests
{
    private static readonly Uri VaultUri = new("https://workload-test.vault.azure.net/");
    private const string Tenant = "00000000-0000-0000-0000-000000000001";
    private const string Client = "00000000-0000-0000-0000-000000000002";
    private const string Assertion = "nonsensitive-generated-projected-assertion";
    private const string Secret = "nonsensitive-secret-response-do-not-log";

    [Fact]
    public async Task ExchangesProjectedAssertionAndRetrievesExactVersion()
    {
        using var projected = new ProjectedTokenFile();
        var identity = new IdentityHandler((_, _) => Task.FromResult(TokenResponse()));
        var vault = new VaultHandler((_, _) => Task.FromResult(SecretResponse()));
        var messages = new ConcurrentQueue<string>();
        using var diagnostics = new AzureEventSourceListener(
            (_, message) => messages.Enqueue(message), EventLevel.Verbose);
        using var redemption = Create(projected.Path, identity, vault);
        Assert.Equal(0, identity.Calls);
        Assert.Equal(0, vault.AuthenticatedCalls);

        var credential = await redemption.RedeemAsync(Request(), CancellationToken.None);

        Assert.Equal(Secret, credential.GetValue());
        Assert.Equal(1, identity.Calls);
        Assert.Equal(1, vault.AuthenticatedCalls);
        Assert.Equal($"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token",
            identity.RequestUri!.GetLeftPart(UriPartial.Path));
        var form = ParseForm(identity.Body!);
        Assert.Equal(identity.ClientId, form["client_id"]);
        Assert.Equal(Assertion, form["client_assertion"]);
        Assert.Equal("urn:ietf:params:oauth:client-assertion-type:jwt-bearer", form["client_assertion_type"]);
        Assert.Equal("client_credentials", form["grant_type"]);
        Assert.Equal("https://vault.azure.net/.default", form["scope"]);
        Assert.Equal("/secrets/opaque/exact-version", vault.AuthenticatedPath);
        Assert.DoesNotContain(Assertion, string.Join("\n", messages));
        Assert.DoesNotContain(Secret, string.Join("\n", messages));
        Assert.DoesNotContain("nonsensitive-issued-access-token", string.Join("\n", messages));
    }

    [Fact]
    public async Task CachedSdkTokenDoesNotRequireProjectedFileOnSubsequentRedemption()
    {
        using var projected = new ProjectedTokenFile();
        var identity = new IdentityHandler((_, _) => Task.FromResult(TokenResponse()));
        var vault = new VaultHandler((_, _) => Task.FromResult(SecretResponse()));
        using var redemption = Create(projected.Path, identity, vault);

        Assert.Equal(Secret, (await redemption.RedeemAsync(Request(), CancellationToken.None)).GetValue());
        projected.Dispose();
        Assert.Equal(Secret, (await redemption.RedeemAsync(Request(), CancellationToken.None)).GetValue());
        Assert.Equal(1, identity.Calls);
        Assert.Equal(2, vault.AuthenticatedCalls);
    }

    [Theory]
    [InlineData("", Client, "token", "tenant")]
    [InlineData(Tenant, "", "token", "client")]
    [InlineData(Tenant, Client, "", "path")]
    public void RequiresExplicitIdentityConfiguration(string tenant, string client, string path, string reason)
    {
        using var projected = new ProjectedTokenFile();
        var options = new WorkloadIdentityCredentialOptions
        {
            TenantId = tenant,
            ClientId = client,
            TokenFilePath = path,
        };
        var error = Assert.Throws<ArgumentException>(() =>
            AzureKeyVaultSecretRedemption.CreateWithWorkloadIdentity(
                new AzureKeyVaultConfiguration(VaultUri), options));
        Assert.Contains(reason, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsBothIdentityAndSecretContentLogging()
    {
        using var projected = new ProjectedTokenFile();
        var identity = new WorkloadIdentityCredentialOptions
        {
            TenantId = Tenant, ClientId = Client, TokenFilePath = projected.Path,
        };
        identity.Diagnostics.IsLoggingContentEnabled = true;
        Assert.Throws<ArgumentException>(() => AzureKeyVaultSecretRedemption.CreateWithWorkloadIdentity(
            new AzureKeyVaultConfiguration(VaultUri), identity));
        identity.Diagnostics.IsLoggingContentEnabled = false;
        var vault = new SecretClientOptions();
        vault.Diagnostics.IsLoggingContentEnabled = true;
        Assert.Throws<ArgumentException>(() => AzureKeyVaultSecretRedemption.CreateWithWorkloadIdentity(
            new AzureKeyVaultConfiguration(VaultUri), identity, vault));
    }

    [Fact]
    public async Task MissingAndUnreadableProjectedFilesFailWithoutExchangeOrFallback()
    {
        using var projected = new ProjectedTokenFile();
        var identity = new IdentityHandler((_, _) => Task.FromResult(TokenResponse()));
        var vault = new VaultHandler((_, _) => Task.FromResult(SecretResponse()));
        projected.Dispose();
        using var missing = Create(projected.Path, identity, vault);
        var missingError = await Assert.ThrowsAsync<AzureKeyVaultWorkloadIdentityException>(
            () => missing.RedeemAsync(Request(), CancellationToken.None));
        Assert.Equal(AzureKeyVaultWorkloadIdentityFailure.TokenFileUnavailable, missingError.Failure);
        Assert.Equal(0, missingError.Status);
        Assert.DoesNotContain(projected.Path, missingError.ToString());

        using var unreadable = Create(System.IO.Path.GetDirectoryName(projected.Path)!, identity, vault);
        var unreadableError = await Assert.ThrowsAsync<AzureKeyVaultWorkloadIdentityException>(
            () => unreadable.RedeemAsync(Request(), CancellationToken.None));
        Assert.Equal(AzureKeyVaultWorkloadIdentityFailure.TokenFileUnavailable, unreadableError.Failure);
        Assert.Equal(0, unreadableError.Status);
        Assert.Equal(0, identity.Calls);
        Assert.Equal(0, vault.AuthenticatedCalls);
    }

    [Fact]
    public async Task RejectedExchangeAndDiagnosticsDoNotDiscloseAssertionOrResponse()
    {
        using var projected = new ProjectedTokenFile();
        var messages = new ConcurrentQueue<string>();
        using var diagnostics = new AzureEventSourceListener(
            (_, message) => messages.Enqueue(message), EventLevel.Verbose);
        var identity = new IdentityHandler((_, _) => Task.FromResult(JsonResponse(
            HttpStatusCode.Unauthorized,
            JsonSerializer.Serialize(new { error = "invalid_client", error_description = "exchange rejected" }))));
        var vault = new VaultHandler((_, _) => Task.FromResult(SecretResponse()));
        using var redemption = Create(projected.Path, identity, vault);

        var error = await Assert.ThrowsAsync<AzureKeyVaultWorkloadIdentityException>(
            () => redemption.RedeemAsync(Request(), CancellationToken.None));

        Assert.Equal(AzureKeyVaultWorkloadIdentityFailure.ExchangeRejected, error.Failure);
        Assert.DoesNotContain(Assertion, error.ToString());
        Assert.DoesNotContain(projected.Path, error.ToString());
        Assert.Equal(1, identity.Calls);
        Assert.Equal(0, vault.AuthenticatedCalls);
        Assert.DoesNotContain(Assertion, string.Join("\n", messages));
        Assert.DoesNotContain(Secret, string.Join("\n", messages));
    }

    [Fact]
    public async Task RejectedExchangeDoesNotIncludeServerEchoInPublicException()
    {
        using var projected = new ProjectedTokenFile();
        var messages = new ConcurrentQueue<string>();
        using var diagnostics = new AzureEventSourceListener(
            (_, message) => messages.Enqueue(message), EventLevel.Verbose);
        var identity = new IdentityHandler((_, _) => Task.FromResult(JsonResponse(
            HttpStatusCode.Unauthorized,
            JsonSerializer.Serialize(new { error = "invalid_client", error_description = Assertion }))));
        var vault = new VaultHandler((_, _) => Task.FromResult(SecretResponse()));
        using var redemption = Create(projected.Path, identity, vault);

        var error = await Assert.ThrowsAsync<AzureKeyVaultWorkloadIdentityException>(
            () => redemption.RedeemAsync(Request(), CancellationToken.None));

        Assert.Equal(AzureKeyVaultWorkloadIdentityFailure.ExchangeRejected, error.Failure);
        Assert.Equal(401, error.Status);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Assertion, error.ToString());
        Assert.Equal(0, vault.AuthenticatedCalls);
        Assert.NotEmpty(messages);
        Assert.DoesNotContain(Assertion, string.Join("\n", messages));
    }

    [Fact]
    public async Task CancellationInterruptsIdentityExchangeWithoutVaultAccess()
    {
        using var projected = new ProjectedTokenFile();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var identity = new IdentityHandler(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return TokenResponse();
        });
        var vault = new VaultHandler((_, _) => Task.FromResult(SecretResponse()));
        using var redemption = Create(projected.Path, identity, vault);
        var task = redemption.RedeemAsync(Request(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, vault.AuthenticatedCalls);
    }

    private static AzureKeyVaultSecretRedemption Create(
        string tokenPath, IdentityHandler identity, VaultHandler vault)
    {
        var identityOptions = new WorkloadIdentityCredentialOptions
        {
            TenantId = Tenant,
            ClientId = identity.ClientId,
            TokenFilePath = tokenPath,
            Transport = new HttpClientTransport(new HttpClient(identity)),
            Retry = { MaxRetries = 0 },
        };
        var vaultOptions = new SecretClientOptions
        {
            Transport = new HttpClientTransport(new HttpClient(vault)),
            Retry = { MaxRetries = 0 },
        };
        return AzureKeyVaultSecretRedemption.CreateWithWorkloadIdentity(
            new AzureKeyVaultConfiguration(VaultUri), identityOptions, vaultOptions);
    }

    private static SecretRedemptionRequest Request() =>
        new(new SecretRef("opaque", "exact-version"), "trusted-purpose", "run-1");

    private static Dictionary<string, string> ParseForm(string form) =>
        form.Split('&').Select(part => part.Split('=', 2))
            .ToDictionary(part => WebUtility.UrlDecode(part[0]), part => WebUtility.UrlDecode(part[1]));

    private static HttpResponseMessage TokenResponse() =>
        JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            access_token = "nonsensitive-issued-access-token", token_type = "Bearer", expires_in = 3600,
        }));

    private static HttpResponseMessage SecretResponse() =>
        JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            id = $"{VaultUri}secrets/opaque/exact-version",
            value = Secret,
            attributes = new { enabled = true },
        }));

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class ProjectedTokenFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
                $".projected-{Guid.NewGuid():N}.txt"));

        public ProjectedTokenFile() => File.WriteAllText(Path, Assertion);
        public void Dispose() => File.Delete(Path);
    }

    private sealed class IdentityHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public string ClientId { get; } = Guid.NewGuid().ToString();
        public int Calls { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
                return JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    tenant_discovery_endpoint = $"https://login.microsoftonline.com/{Tenant}/v2.0/.well-known/openid-configuration",
                    token_endpoint = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token",
                    authorization_endpoint = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize",
                    issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0",
                }));
            Calls++;
            RequestUri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return await respond(request, cancellationToken);
        }
    }

    private sealed class VaultHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int AuthenticatedCalls { get; private set; }
        public string? AuthenticatedPath { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.Authorization is null)
            {
                var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                challenge.Headers.WwwAuthenticate.Add(AuthenticationHeaderValue.Parse(
                    $"Bearer authorization=\"https://login.microsoftonline.com/{Tenant}\", resource=\"https://vault.azure.net\""));
                return challenge;
            }

            Assert.Equal("Bearer", request.Headers.Authorization.Scheme);
            Assert.Equal("nonsensitive-issued-access-token", request.Headers.Authorization.Parameter);
            AuthenticatedCalls++;
            AuthenticatedPath = request.RequestUri!.AbsolutePath;
            return await respond(request, cancellationToken);
        }
    }
}
