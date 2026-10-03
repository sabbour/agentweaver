using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Secrets.AzureKeyVault;
using Azure.Core;
using Azure.Core.Diagnostics;
using Azure.Core.Pipeline;
using Azure.Security.KeyVault.Secrets;
using Xunit;

namespace Agentweaver.Secrets.AzureKeyVault.Tests;

public sealed class AzureKeyVaultSecretRedemptionTests
{
    private static readonly Uri VaultUri = new("https://unit.vault.azure.net/");
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string SensitiveValue = "test-credential-value-do-not-log";
    private readonly TestClock _clock = new(Now);

    [Theory]
    [InlineData("http://unit.vault.azure.net/")]
    [InlineData("https://unit.example.com/")]
    [InlineData("https://unit.vault.azure.net/extra")]
    [InlineData("https://user:pass@unit.vault.azure.net/")]
    [InlineData("https://unit.vault.azure.net/?query=1")]
    [InlineData("https://unit.vault.azure.net/#fragment")]
    [InlineData("https://unit.vault.azure.net:8443/")]
    [InlineData("https://nested.unit.vault.azure.net/")]
    [InlineData("https://-.vault.azure.net/")]
    [InlineData("https://un--it.vault.azure.net/")]
    [InlineData("https://1unit.vault.azure.net/")]
    [InlineData("https://abcdefghijklmnopqrstuvwxyz.vault.azure.net/")]
    public void ConfigurationRejectsNonVaultOrUnsafeEndpoints(string uri)
    {
        Assert.Throws<ArgumentException>(() => new AzureKeyVaultConfiguration(new Uri(uri)));
    }

    [Theory]
    [InlineData("https://unit.vault.azure.net/")]
    [InlineData("https://unit.vault.usgovcloudapi.net/")]
    [InlineData("https://unit.vault.azure.cn/")]
    public void ConfigurationAcceptsAzureCloudVaults(string uri)
    {
        Assert.Equal(new Uri(uri), new AzureKeyVaultConfiguration(new Uri(uri)).VaultUri);
    }

    [Fact]
    public void ClientInjectionRequiresMatchingConfiguredVault()
    {
        var client = new SecretClient(new Uri("https://other.vault.azure.net/"), new TestCredential());
        Assert.Throws<ArgumentException>(() =>
            new AzureKeyVaultSecretRedemption(new AzureKeyVaultConfiguration(VaultUri), client));
        Assert.Throws<ArgumentNullException>(() =>
            new AzureKeyVaultSecretRedemption(new AzureKeyVaultConfiguration(VaultUri), (TokenCredential)null!));
        var unsafeOptions = new SecretClientOptions();
        unsafeOptions.Diagnostics.IsLoggingContentEnabled = true;
        Assert.Throws<ArgumentException>(() => new AzureKeyVaultSecretRedemption(
            new AzureKeyVaultConfiguration(VaultUri), new TestCredential(), unsafeOptions));
    }

    [Fact]
    public async Task RetrievesExactVersionThroughAuthenticatedSdkTransportWithoutNetwork()
    {
        var events = new ConcurrentQueue<string>();
        using var diagnostics = new AzureEventSourceListener(
            (_, message) => events.Enqueue(message), EventLevel.Verbose);
        var credential = new TestCredential();
        var handler = new VaultHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/secrets/opaque/v2", request.RequestUri!.AbsolutePath);
            Assert.Equal("application/json", request.Headers.Accept.Single().MediaType);
            return Task.FromResult(VaultSecret("opaque", "v2", SensitiveValue));
        });
        using var redemption = Create(handler, credential);
        ISecretRedemption provider = redemption;

        var result = await provider.RedeemAsync(
            new SecretRedemptionRequest(new SecretRef("opaque", "v2"), "source-control.checkout", "run-1"),
            CancellationToken.None);

        Assert.Equal(SensitiveValue, result.GetValue());
        Assert.Equal(Now.AddMinutes(5), result.ExpiresAt);
        Assert.Equal(1, credential.Calls);
        Assert.Equal(1, handler.AuthenticatedCalls);
        Assert.DoesNotContain(SensitiveValue, result.ToString());
        Assert.DoesNotContain(SensitiveValue, JsonSerializer.Serialize(result));
        Assert.DoesNotContain(SensitiveValue, JsonSerializer.Serialize(redemption));
        Assert.NotEmpty(events);
        Assert.DoesNotContain(SensitiveValue, string.Join(Environment.NewLine, events));
    }

    [Fact]
    public async Task ReturnsTextUnchangedAndHonorsEarlierVaultExpiry()
    {
        const string encodedBinary = "AP+AgA==";
        var handler = new VaultHandler((_, _) => Task.FromResult(VaultSecret(
            "opaque", "v1", encodedBinary, Now.AddSeconds(30))));
        using var redemption = Create(handler);

        var result = await redemption.RedeemAsync(Request(), CancellationToken.None);
        Assert.Equal(encodedBinary, result.GetValue());
        Assert.Equal(Now.AddSeconds(30), result.ExpiresAt);
        _clock.UtcNow = Now.AddSeconds(30);
        Assert.Throws<InvalidOperationException>(() => result.GetValue());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "SecretNotFound", AzureKeyVaultSecretFailure.NotFound)]
    [InlineData(HttpStatusCode.Forbidden, "SecretDisabled", AzureKeyVaultSecretFailure.Disabled)]
    [InlineData(HttpStatusCode.Forbidden, "Forbidden", AzureKeyVaultSecretFailure.AccessDenied)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable", AzureKeyVaultSecretFailure.ServiceFailure)]
    [InlineData(HttpStatusCode.Conflict, "Conflict", AzureKeyVaultSecretFailure.ServiceFailure)]
    public async Task DifferentiatesFailuresWithoutLeakingServiceDiagnostics(
        HttpStatusCode status, string errorCode, AzureKeyVaultSecretFailure failure)
    {
        var handler = new VaultHandler((_, _) => Task.FromResult(VaultError(status, errorCode)));
        using var redemption = Create(handler);

        var exception = await Assert.ThrowsAsync<AzureKeyVaultSecretException>(
            () => redemption.RedeemAsync(Request(), CancellationToken.None));

        Assert.Equal(failure, exception.Failure);
        Assert.Equal((int)status, exception.Status);
        Assert.DoesNotContain(SensitiveValue, exception.ToString());
        Assert.Equal(1, handler.AuthenticatedCalls);
    }

    [Theory]
    [InlineData(false, "value", AzureKeyVaultSecretFailure.Disabled)]
    [InlineData(true, "", AzureKeyVaultSecretFailure.InvalidValue)]
    [InlineData(true, "value", AzureKeyVaultSecretFailure.InvalidValue)]
    public async Task RejectsDisabledEmptyOrExpiredResponses(
        bool enabled, string value, AzureKeyVaultSecretFailure failure)
    {
        var expiry = enabled && value.Length > 0 ? Now : Now.AddMinutes(1);
        var handler = new VaultHandler((_, _) => Task.FromResult(
            VaultSecret("opaque", "v1", value, expiry, enabled)));
        using var redemption = Create(handler);

        var exception = await Assert.ThrowsAsync<AzureKeyVaultSecretException>(
            () => redemption.RedeemAsync(Request(), CancellationToken.None));
        Assert.Equal(failure, exception.Failure);
    }

    [Fact]
    public async Task RejectsWrongVersionAndInvalidKeyVaultNameWithoutFallback()
    {
        var handler = new VaultHandler((_, _) => Task.FromResult(VaultSecret("opaque", "other", SensitiveValue)));
        using var redemption = Create(handler);
        var mismatch = await Assert.ThrowsAsync<AzureKeyVaultSecretException>(
            () => redemption.RedeemAsync(Request(), CancellationToken.None));
        Assert.Equal(AzureKeyVaultSecretFailure.InvalidValue, mismatch.Failure);

        await Assert.ThrowsAsync<ArgumentException>(() => redemption.RedeemAsync(
            new SecretRedemptionRequest(new SecretRef("not:a-vault-name", "v1"), "purpose", "run"),
            CancellationToken.None));
        Assert.Equal(1, handler.AuthenticatedCalls);
    }

    [Fact]
    public async Task RejectsResponseForDifferentNameOrFutureNotBefore()
    {
        var handler = new VaultHandler((_, _) => Task.FromResult(VaultSecret("different", "v1", SensitiveValue)));
        using var redemption = Create(handler);
        var wrongName = await Assert.ThrowsAsync<AzureKeyVaultSecretException>(
            () => redemption.RedeemAsync(Request(), CancellationToken.None));
        Assert.Equal(AzureKeyVaultSecretFailure.InvalidValue, wrongName.Failure);

        handler.Respond = (_, _) => Task.FromResult(VaultSecret(
            "opaque", "v1", SensitiveValue, notBefore: Now.AddSeconds(30)));
        var notYetActive = await Assert.ThrowsAsync<AzureKeyVaultSecretException>(
            () => redemption.RedeemAsync(Request(), CancellationToken.None));
        Assert.Equal(AzureKeyVaultSecretFailure.InvalidValue, notYetActive.Failure);
    }

    [Fact]
    public async Task PropagatesCancellationAndDoesNotReturnCredentialAfterCancellation()
    {
        using var before = new CancellationTokenSource();
        before.Cancel();
        var handler = new VaultHandler((_, _) => Task.FromResult(VaultSecret("opaque", "v1", SensitiveValue)));
        using var redemption = Create(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => redemption.RedeemAsync(Request(), before.Token));
        Assert.Equal(0, handler.AuthenticatedCalls);

        using var during = new CancellationTokenSource();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Respond = async (_, cancellationToken) =>
        {
            pending.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return VaultSecret("opaque", "v1", SensitiveValue);
        };
        var task = redemption.RedeemAsync(Request(), during.Token);
        await pending.Task.WaitAsync(TimeSpan.FromSeconds(10));
        during.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        using var afterResponse = new CancellationTokenSource();
        handler.Respond = (_, _) =>
        {
            afterResponse.Cancel();
            return Task.FromResult(VaultSecret("opaque", "v1", SensitiveValue));
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => redemption.RedeemAsync(Request(), afterResponse.Token));
    }

    [Fact]
    public async Task DisposalDuringInFlightFetchCannotIssueCredential()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new VaultHandler(async (_, _) =>
        {
            pending.TrySetResult();
            await release.Task;
            return VaultSecret("opaque", "v1", SensitiveValue);
        });
        using var redemption = Create(handler);
        var request = redemption.RedeemAsync(Request(), CancellationToken.None);
        await pending.Task.WaitAsync(TimeSpan.FromSeconds(10));
        redemption.Dispose();
        release.TrySetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => request);
    }

    [Fact]
    public async Task DisposalInvalidatesIssuedCredentialsAndRejectsFutureRequests()
    {
        var handler = new VaultHandler((_, _) => Task.FromResult(VaultSecret("opaque", "v1", SensitiveValue)));
        var redemption = Create(handler);
        var result = await redemption.RedeemAsync(Request(), CancellationToken.None);
        redemption.Dispose();
        redemption.Dispose();

        Assert.DoesNotContain(SensitiveValue, Assert.Throws<InvalidOperationException>(() => result.GetValue()).ToString());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => redemption.RedeemAsync(Request(), CancellationToken.None));
    }

    private AzureKeyVaultSecretRedemption Create(VaultHandler handler, TestCredential? credential = null)
    {
        var http = new HttpClient(handler) { BaseAddress = VaultUri };
        var options = new SecretClientOptions
        {
            Transport = new HttpClientTransport(http),
            Retry = { MaxRetries = 0 },
        };
        return new AzureKeyVaultSecretRedemption(
            new AzureKeyVaultConfiguration(VaultUri), credential ?? new TestCredential(), options, _clock);
    }

    private static SecretRedemptionRequest Request() =>
        new(new SecretRef("opaque", "v1"), "source-control.checkout", "run-1");

    private static HttpResponseMessage VaultSecret(
        string name, string version, string value, DateTimeOffset? expiry = null,
        bool enabled = true, DateTimeOffset? notBefore = null)
    {
        var attributes = new Dictionary<string, object> { ["enabled"] = enabled };
        if (expiry is { } expiresAt)
            attributes["exp"] = expiresAt.ToUnixTimeSeconds();
        if (notBefore is { } activeAt)
            attributes["nbf"] = activeAt.ToUnixTimeSeconds();
        var response = new
        {
            id = $"{VaultUri}secrets/{name}/{version}",
            value,
            attributes,
        };
        return JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(response));
    }

    private static HttpResponseMessage VaultError(HttpStatusCode status, string code) =>
        JsonResponse(status, JsonSerializer.Serialize(new
        {
            error = new { code, message = SensitiveValue },
        }));

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class TestClock(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class TestCredential : TokenCredential
    {
        public int Calls { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            return new AccessToken("local-test-token", Now.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new AccessToken("local-test-token", Now.AddHours(1)));
        }
    }

    private sealed class VaultHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = respond;
        public int AuthenticatedCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.Authorization is null)
            {
                var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                challenge.Headers.WwwAuthenticate.Add(
                    AuthenticationHeaderValue.Parse(
                        "Bearer authorization=\"https://login.microsoftonline.com/test-tenant\", resource=\"https://vault.azure.net\""));
                return challenge;
            }

            Assert.Equal("Bearer", request.Headers.Authorization.Scheme);
            Assert.Equal("local-test-token", request.Headers.Authorization.Parameter);
            AuthenticatedCalls++;
            return await Respond(request, cancellationToken);
        }
    }
}
