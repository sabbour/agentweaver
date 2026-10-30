using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Cli;
using Agentweaver.Cli.Auth;
using Agentweaver.Cli.Vault;

namespace Agentweaver.Cli.Tests;

public sealed class CliAuthTests
{
    private const string Issuer = "https://broker.test/";
    private const string ClientId = "agentweaver-cli";
    private const string CallbackUri = "http://127.0.0.1:49173/oauth/callback";
    private static readonly string[] Scopes = ["openid", "api.read", "offline_access"];

    [Fact]
    public void IdentityBindingSortsScopesAndSeparatesProjectRunCredentials()
    {
        var target = Target();
        var unbound = CliAuthIdentity.Create(new CliIdentity(target, Scopes));
        var reordered = CliAuthIdentity.Create(new CliIdentity(target, ["offline_access", "openid", "api.read"]));
        var noSlashIssuer = CliAuthIdentity.Create(new CliIdentity(
            target with { Issuer = new Uri("https://broker.test") }, Scopes));
        var run = CliAuthIdentity.Create(new CliIdentity(
            target, ["openid", "api.read", "offline_access", "projects.orchestrator"], "project-a", "run-a"));
        var otherRun = CliAuthIdentity.Create(new CliIdentity(
            target, ["openid", "api.read", "offline_access", "projects.orchestrator"], "project-a", "run-b"));

        Assert.Equal(unbound.Account, reordered.Account);
        Assert.Equal(unbound.Account, noSlashIssuer.Account);
        Assert.Equal(Issuer, noSlashIssuer.Issuer);
        Assert.NotEqual(unbound.Account, run.Account);
        Assert.NotEqual(run.Account, otherRun.Account);
    }

    [Fact]
    public void IdentityBindingRejectsUnpairedOrMismatchedProjectScopes()
    {
        var target = Target();

        Assert.Throws<ArgumentException>(() =>
            CliAuthIdentity.Create(new CliIdentity(target, Scopes, "project-a")));
        Assert.Throws<ArgumentException>(() =>
            CliAuthIdentity.Create(new CliIdentity(
                target, ["openid", "api.read", "offline_access"], "project-a", "run-a")));
        Assert.Throws<ArgumentException>(() =>
            CliAuthIdentity.Create(new CliIdentity(
                target, ["openid", "api.read", "offline_access", "projects.admin"], "project-a", "run-a")));
    }

    [Fact]
    public void RunBoundAuthorizationCarriesExactProjectAndRunSelection()
    {
        var bound = CliAuthIdentity.Create(new CliIdentity(
            Target(),
            ["openid", "api.read", "offline_access", "projects.orchestrator"],
            "project-a",
            "run-b"));
        var metadata = new OidcMetadata(
            Issuer,
            new Uri("https://broker.test/connect/authorize"),
            new Uri("https://broker.test/connect/token"),
            new Uri("https://broker.test/.well-known/jwks"));

        var authorization = OidcProtocol.CreateAuthorizationUri(
            metadata, bound, new Uri(CallbackUri), "state", "nonce", "challenge");
        var query = ParseQuery(authorization.Query);

        Assert.Equal("project-a", query["project_id"]);
        Assert.Equal("run-b", query["run_id"]);
        Assert.Equal("S256", query["code_challenge_method"]);
    }

    [Fact]
    public void CallbackParserAcceptsOnlyExpectedLoopbackCallback()
    {
        var result = OAuthCallbackParser.Parse(
            "/oauth/callback?code=one-time-code&state=expected&iss=https%3A%2F%2Fbroker.test%2F",
            "127.0.0.1:49173", "expected", Issuer);

        Assert.Equal("one-time-code", result.Code);
        Assert.Null(result.Error);
    }

    [Fact]
    public void CallbackParserEnforcesRequestSizeLimit()
    {
        var oversized = "/oauth/callback?code=" + new string('a', 8193) + "&state=expected";

        Assert.Throws<InvalidOperationException>(() =>
            OAuthCallbackParser.Parse(oversized, "127.0.0.1:49173", "expected", Issuer));
    }

    [Fact]
    public void LoopbackRequestParserRejectsDuplicateHostHeaders()
    {
        var header = Encoding.ASCII.GetBytes(
            "GET /oauth/callback?code=one-time-code&state=expected HTTP/1.1\r\n" +
            "Host: 127.0.0.1:49173\r\nHost: 127.0.0.1:49173");

        Assert.Throws<InvalidOperationException>(() =>
            LoopbackOAuthCallbackListener.ParseRequestHeader(header));
    }

    [Theory]
    [InlineData("Content-Length: 0")]
    [InlineData("Transfer-Encoding: chunked")]
    public void LoopbackRequestParserRejectsRequestBodyFraming(string framingHeader)
    {
        var header = Encoding.ASCII.GetBytes(
            "GET /oauth/callback?code=one-time-code&state=expected HTTP/1.1\r\n" +
            "Host: 127.0.0.1:49173\r\n" + framingHeader);

        Assert.Throws<InvalidOperationException>(() =>
            LoopbackOAuthCallbackListener.ParseRequestHeader(header));
    }

    [Theory]
    [InlineData("/oauth/callback?code=one-time-code&state=wrong", "127.0.0.1:49173", "expected", "https://broker.test/")]
    [InlineData("/oauth/callback?code=one-time-code&state=expected&state=expected", "127.0.0.1:49173", "expected", "https://broker.test/")]
    [InlineData("/oauth/callback?code=one-time-code&state=expected&iss=https%3A%2F%2Fevil.test%2F", "127.0.0.1:49173", "expected", "https://broker.test/")]
    [InlineData("/other?code=one-time-code&state=expected", "127.0.0.1:49173", "expected", "https://broker.test/")]
    [InlineData("/oauth/callback?code=one-time-code&state=expected", "localhost:49173", "expected", "https://broker.test/")]
    [InlineData("/oauth/callback?code=one-time-code&state=expected&access_token=secret", "127.0.0.1:49173", "expected", "https://broker.test/")]
    public void CallbackParserRejectsMismatchedOrUnexpectedCallbackData(
        string requestTarget, string host, string expectedState, string expectedIssuer)
    {
        Assert.Throws<InvalidOperationException>(() =>
            OAuthCallbackParser.Parse(requestTarget, host, expectedState, expectedIssuer));
    }

    [Fact]
    public async Task NeverInteractionDoesNotLaunchBrowserOrPersistAnything()
    {
        var vault = new MemoryVault();
        var browser = new TestBrowser();
        var directory = CreateTestDirectory();
        try
        {
            using var http = new HttpClient(new DelegateHandler((_, _) =>
                throw new InvalidOperationException("No Broker request is expected.")));
            using var service = CreateService(http, vault, browser, directory);

            await Assert.ThrowsAsync<CliAuthNeedsLoginException>(() =>
                service.GetBearerAsync(Identity(), InteractionPolicy.Never, CancellationToken.None));

            Assert.False(browser.WasOpened);
            Assert.Empty(vault.Values);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CallbackWaitTimeoutIsReportedWithoutSavingCredentials()
    {
        var vault = new MemoryVault();
        var browser = new TestBrowser();
        var directory = CreateTestDirectory();
        try
        {
            using var http = new HttpClient(new DelegateHandler((_, _) =>
                Task.FromResult(DiscoveryResponse())));
            using var service = CreateService(
                http, vault, browser, directory, new WaitingCallbackFactory(), TimeSpan.FromMilliseconds(50));

            var error = await Assert.ThrowsAsync<TimeoutException>(() =>
                service.LoginAsync(Identity(), CancellationToken.None));

            Assert.Contains("Timed out", error.Message, StringComparison.Ordinal);
            Assert.True(browser.WasOpened);
            Assert.Empty(vault.Values);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CallerCancellationStopsPendingCallbackWithoutSavingCredentials()
    {
        var vault = new MemoryVault();
        var browser = new TestBrowser();
        var directory = CreateTestDirectory();
        try
        {
            using var http = new HttpClient(new DelegateHandler((_, _) =>
                Task.FromResult(DiscoveryResponse())));
            using var service = CreateService(http, vault, browser, directory, new WaitingCallbackFactory());
            using var cancellation = new CancellationTokenSource();

            var login = service.LoginAsync(Identity(), cancellation.Token);
            await browser.Opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);

            Assert.Empty(vault.Values);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoginUsesS256AndValidatesIdentityBeforeSavingRefreshToken()
    {
        using var signingKey = RSA.Create(2048);
        var browser = new TestBrowser();
        var callbackFactory = new TestCallbackFactory(browser);
        var vault = new MemoryVault();
        var directory = CreateTestDirectory();
        try
        {
            using var http = new HttpClient(new OidcHandler(signingKey, browser));
            using var service = CreateService(http, vault, browser, directory, callbackFactory);

            var status = await service.LoginAsync(Identity(), CancellationToken.None);

            Assert.True(status.IsSignedIn);
            Assert.Equal("actor-123", status.ActorId);
            Assert.Equal("rotated-login-token", Assert.Single(vault.Values.Values));
            var authorization = Assert.IsType<Uri>(browser.AuthorizationUri);
            var query = ParseQuery(authorization.Query);
            Assert.Equal("code", query["response_type"]);
            Assert.Equal("S256", query["code_challenge_method"]);
            Assert.Equal(CallbackUri, query["redirect_uri"]);
            Assert.Equal("agentweaver-cli", query["client_id"]);
            Assert.False(query.ContainsKey("code_verifier"));

            var freshProcess = CreateService(http, vault, browser, directory);
            using (freshProcess as IDisposable)
            {
                var freshStatus = await freshProcess.StatusAsync(Identity(), CancellationToken.None);
                Assert.True(freshStatus.IsSignedIn);
                Assert.Null(freshStatus.ActorId);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoginRejectsInvalidIdTokenSignatureBeforeSavingRefreshToken()
    {
        using var signingKey = RSA.Create(2048);
        var browser = new TestBrowser();
        var vault = new MemoryVault();
        var directory = CreateTestDirectory();
        try
        {
            using var http = new HttpClient(new OidcHandler(signingKey, browser, corruptSignature: true));
            using var service = CreateService(http, vault, browser, directory);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.LoginAsync(Identity(), CancellationToken.None));

            Assert.Contains("signature is invalid", error.Message, StringComparison.Ordinal);
            Assert.Empty(vault.Values);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RefreshPersistsRotatedTokenBeforeReturningAccessToken()
    {
        var vault = new MemoryVault();
        vault.Set(CliAuthIdentity.Create(Identity()).Account, "old-refresh-token");
        var browser = new TestBrowser();
        var directory = CreateTestDirectory();
        try
        {
            using var http = new HttpClient(new DelegateHandler(async (request, ct) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith(".well-known/openid-configuration", StringComparison.Ordinal))
                    return DiscoveryResponse();
                if (request.RequestUri.AbsolutePath == "/connect/token")
                {
                    var fields = await ReadFormAsync(request, ct);
                    Assert.Equal("refresh_token", fields["grant_type"]);
                    Assert.Equal("old-refresh-token", fields["refresh_token"]);
                    return JsonResponse("""{"access_token":"access-token","refresh_token":"rotated-refresh-token","token_type":"Bearer","expires_in":3600}""");
                }
                throw new InvalidOperationException("Unexpected Broker request.");
            }));
            using var service = CreateService(http, vault, browser, directory);

            var token = await service.GetBearerAsync(Identity(), InteractionPolicy.Never, CancellationToken.None);

            Assert.Equal("access-token", token);
            Assert.Equal("rotated-refresh-token", vault.Get(CliAuthIdentity.Create(Identity()).Account));
            Assert.False(browser.WasOpened);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("""{"access_token":"access-token","token_type":"Bearer","expires_in":3600}""")]
    [InlineData("""{"access_token":"access-token","refresh_token":"old-refresh-token","token_type":"Bearer","expires_in":3600}""")]
    [InlineData("""{"access_token":"access-token","refresh_token":"","token_type":"Bearer","expires_in":3600}""")]
    public async Task RefreshRequiresDistinctReplacementAndClearsConsumedCredential(string tokenResponse)
    {
        var vault = new MemoryVault();
        var account = CliAuthIdentity.Create(Identity()).Account;
        vault.Set(account, "old-refresh-token");
        var tokenRequests = 0;
        var directory = CreateTestDirectory();
        try
        {
            using var http = new HttpClient(new DelegateHandler((request, _) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith(".well-known/openid-configuration", StringComparison.Ordinal))
                    return Task.FromResult(DiscoveryResponse());
                if (request.RequestUri.AbsolutePath == "/connect/token")
                {
                    Interlocked.Increment(ref tokenRequests);
                    return Task.FromResult(JsonResponse(tokenResponse));
                }
                throw new InvalidOperationException("Unexpected Broker request.");
            }));
            using var service = CreateService(http, vault, new TestBrowser(), directory);

            await Assert.ThrowsAsync<CliAuthNeedsLoginException>(() =>
                service.GetBearerAsync(Identity(), InteractionPolicy.Never, CancellationToken.None));
            await Assert.ThrowsAsync<CliAuthNeedsLoginException>(() =>
                service.GetBearerAsync(Identity(), InteractionPolicy.Never, CancellationToken.None));

            Assert.Equal(1, tokenRequests);
            Assert.Null(vault.Get(account));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.refreshing"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RefreshBodyDeadlineReconcilesAndReleasesCredentialLock()
    {
        var vault = new MemoryVault();
        var account = CliAuthIdentity.Create(Identity()).Account;
        vault.Set(account, "old-refresh-token");
        var tokenRequests = 0;
        var directory = CreateTestDirectory();
        try
        {
            using var http = new HttpClient(new DelegateHandler((request, _) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith(".well-known/openid-configuration", StringComparison.Ordinal))
                    return Task.FromResult(DiscoveryResponse());
                if (request.RequestUri.AbsolutePath == "/connect/token")
                {
                    Interlocked.Increment(ref tokenRequests);
                    return Task.FromResult(StalledJsonResponse());
                }
                throw new InvalidOperationException("Unexpected Broker request.");
            }))
            {
                Timeout = TimeSpan.FromMilliseconds(200),
            };
            using var service = CreateService(http, vault, new TestBrowser(), directory);

            await Assert.ThrowsAsync<CliAuthNeedsLoginException>(() =>
                service.GetBearerAsync(Identity(), InteractionPolicy.Never, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal(1, tokenRequests);
            Assert.Null(vault.Get(account));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.refreshing"));
            Assert.False((await service.StatusAsync(Identity(), CancellationToken.None)).IsSignedIn);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CallerCancellationDuringRefreshBodyReadClearsStateAndPropagates()
    {
        var vault = new MemoryVault();
        var account = CliAuthIdentity.Create(Identity()).Account;
        vault.Set(account, "old-refresh-token");
        var bodyReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var directory = CreateTestDirectory();
        try
        {
            using var http = new HttpClient(new DelegateHandler((request, _) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith(".well-known/openid-configuration", StringComparison.Ordinal))
                    return Task.FromResult(DiscoveryResponse());
                if (request.RequestUri.AbsolutePath == "/connect/token")
                    return Task.FromResult(StalledJsonResponse(bodyReadStarted));
                throw new InvalidOperationException("Unexpected Broker request.");
            }));
            using var service = CreateService(http, vault, new TestBrowser(), directory);
            using var cancellation = new CancellationTokenSource();

            var refresh = service.GetBearerAsync(Identity(), InteractionPolicy.Never, cancellation.Token);
            await bodyReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                refresh.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Null(vault.Get(account));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.refreshing"));
            Assert.False((await service.StatusAsync(Identity(), CancellationToken.None)).IsSignedIn);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RefreshRejectsNonOpaqueTokensAndClearsConsumedCredential()
    {
        var vault = new MemoryVault();
        var account = CliAuthIdentity.Create(Identity()).Account;
        vault.Set(account, "old-refresh-token");
        var directory = CreateTestDirectory();
        try
        {
            using var http = new HttpClient(new DelegateHandler((request, _) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith(".well-known/openid-configuration", StringComparison.Ordinal))
                    return Task.FromResult(DiscoveryResponse());
                if (request.RequestUri.AbsolutePath == "/connect/token")
                    return Task.FromResult(JsonResponse(
                        """{"access_token":"invalid token","refresh_token":"rotated-token","token_type":"Bearer","expires_in":3600}"""));
                throw new InvalidOperationException("Unexpected Broker request.");
            }));
            using var service = CreateService(http, vault, new TestBrowser(), directory);

            await Assert.ThrowsAsync<CliAuthNeedsLoginException>(() =>
                service.GetBearerAsync(Identity(), InteractionPolicy.Never, CancellationToken.None));

            Assert.Null(vault.Get(account));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.refreshing"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UncertainRefreshClearsSavedTokenAndIsNotRetried()
    {
        var vault = new MemoryVault();
        var account = CliAuthIdentity.Create(Identity()).Account;
        vault.Set(account, "possibly-consumed-token");
        var browser = new TestBrowser();
        var tokenRequests = 0;
        var directory = CreateTestDirectory();
        try
        {
            using var http = new HttpClient(new DelegateHandler((request, _) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith(".well-known/openid-configuration", StringComparison.Ordinal))
                    return Task.FromResult(DiscoveryResponse());
                if (request.RequestUri.AbsolutePath == "/connect/token")
                {
                    Interlocked.Increment(ref tokenRequests);
                    throw new HttpRequestException("simulated lost refresh response");
                }
                throw new InvalidOperationException("Unexpected Broker request.");
            }));
            using var service = CreateService(http, vault, browser, directory);

            await Assert.ThrowsAsync<CliAuthNeedsLoginException>(() =>
                service.GetBearerAsync(Identity(), InteractionPolicy.Never, CancellationToken.None));
            await Assert.ThrowsAsync<CliAuthNeedsLoginException>(() =>
                service.GetBearerAsync(Identity(), InteractionPolicy.Never, CancellationToken.None));

            Assert.Equal(1, tokenRequests);
            Assert.Null(vault.Get(account));
            Assert.False(browser.WasOpened);
            Assert.DoesNotContain(Directory.EnumerateFiles(directory), path => path.EndsWith(".refreshing", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InterruptedRefreshMarkerClearsSavedCredentialBeforeStatusReturns()
    {
        var vault = new MemoryVault();
        var account = CliAuthIdentity.Create(Identity()).Account;
        vault.Set(account, "possibly-consumed-token");
        var directory = CreateTestDirectory();
        try
        {
            await new CliCredentialLock(directory).RunAsync(account, (intent, _) =>
            {
                intent.Mark();
                return Task.FromResult(true);
            }, CancellationToken.None);
            using var http = new HttpClient(new DelegateHandler((_, _) =>
                throw new InvalidOperationException("Recovery must not contact the Broker.")));
            using var service = CreateService(http, vault, new TestBrowser(), directory);

            var status = await service.StatusAsync(Identity(), CancellationToken.None);

            Assert.False(status.IsSignedIn);
            Assert.Null(vault.Get(account));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.refreshing"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FailedCleanupOverridesUncertainRefreshAndPreventsAnotherAttempt()
    {
        var vault = new MemoryVault { FailDelete = true };
        var account = CliAuthIdentity.Create(Identity()).Account;
        vault.Set(account, "possibly-consumed-token");
        var tokenRequests = 0;
        var directory = CreateTestDirectory();
        try
        {
            using var http = new HttpClient(new DelegateHandler((request, _) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith(".well-known/openid-configuration", StringComparison.Ordinal))
                    return Task.FromResult(DiscoveryResponse());
                if (request.RequestUri.AbsolutePath == "/connect/token")
                {
                    Interlocked.Increment(ref tokenRequests);
                    throw new HttpRequestException("simulated lost refresh response");
                }
                throw new InvalidOperationException("Unexpected Broker request.");
            }));
            using var service = CreateService(http, vault, new TestBrowser(), directory);

            await Assert.ThrowsAsync<CliAuthCleanupException>(() =>
                service.GetBearerAsync(Identity(), InteractionPolicy.Never, CancellationToken.None));
            await Assert.ThrowsAsync<CliAuthCleanupException>(() =>
                service.GetBearerAsync(Identity(), InteractionPolicy.Never, CancellationToken.None));

            Assert.Equal(1, tokenRequests);
            Assert.True(Directory.EnumerateFiles(directory, "*.refreshing").Any());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static CliTarget Target() => new(
        new Uri("https://gateway.test/"),
        new Uri(Issuer),
        ClientId,
        "https://api.test",
        "tenant-123");

    private static CliIdentity Identity() => new(Target(), Scopes);

    private static string CreateTestDirectory()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "auth-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static CliAuthService CreateService(
        HttpClient http,
        ICliCredentialVault vault,
        TestBrowser browser,
        string directory,
        IOAuthCallbackListenerFactory? callbacks = null,
        TimeSpan? callbackTimeout = null) =>
        new(http, vault, browser, callbacks ?? new TestCallbackFactory(browser),
            new CliCredentialLock(directory, TimeSpan.FromSeconds(5)),
            callbackTimeout: callbackTimeout);

    private static HttpResponseMessage DiscoveryResponse() => JsonResponse(
        """{"issuer":"https://broker.test/","authorization_endpoint":"https://broker.test/connect/authorize","token_endpoint":"https://broker.test/connect/token","jwks_uri":"https://broker.test/.well-known/jwks"}""");

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage StalledJsonResponse(
        TaskCompletionSource? readStarted = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StalledReadStream(readStarted)),
        };
        response.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return response;
    }

    private static async Task<Dictionary<string, string>> ReadFormAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var content = await request.Content!.ReadAsStringAsync(ct);
        return content.Split('&').Select(pair => pair.Split('=', 2))
            .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1]));
    }

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1]));

    private sealed class MemoryVault : ICliCredentialVault
    {
        private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, string> Values => _values;
        public bool FailDelete { get; init; }
        public string? Get(string account) => _values.GetValueOrDefault(account);
        public void Set(string account, string refreshToken) => _values[account] = refreshToken;
        public void Delete(string account)
        {
            if (FailDelete) throw new CliVaultException("simulated vault cleanup failure");
            _values.TryRemove(account, out _);
        }
    }

    private sealed class TestBrowser : ICliSystemBrowser
    {
        public Uri? AuthorizationUri { get; private set; }
        public bool WasOpened { get; private set; }
        public TaskCompletionSource<Uri> Opened { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task OpenAsync(Uri authorizationUri, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            WasOpened = true;
            AuthorizationUri = authorizationUri;
            Opened.TrySetResult(authorizationUri);
            return Task.CompletedTask;
        }
    }

    private sealed class WaitingCallbackFactory : IOAuthCallbackListenerFactory
    {
        public IOAuthCallbackListener Start() => new WaitingCallbackListener();
    }

    private sealed class WaitingCallbackListener : IOAuthCallbackListener
    {
        public Uri RedirectUri { get; } = new(CallbackUri);

        public async Task<OAuthCallback> WaitAsync(
            string expectedState, string expectedIssuer, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("The callback wait should have been canceled.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestCallbackFactory(TestBrowser browser) : IOAuthCallbackListenerFactory
    {
        public IOAuthCallbackListener Start() => new TestCallbackListener(browser);
    }

    private sealed class TestCallbackListener(TestBrowser browser) : IOAuthCallbackListener
    {
        public Uri RedirectUri { get; } = new(CallbackUri);

        public async Task<OAuthCallback> WaitAsync(
            string expectedState, string expectedIssuer, CancellationToken ct)
        {
            while (browser.AuthorizationUri is null)
                await Task.Delay(1, ct);
            var query = ParseQuery(browser.AuthorizationUri.Query);
            if (query["state"] != expectedState)
                throw new InvalidOperationException("The generated OAuth state changed.");
            if (expectedIssuer != Issuer)
                throw new InvalidOperationException("The expected issuer changed.");
            return new OAuthCallback("single-use-code", null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class OidcHandler(RSA signingKey, TestBrowser browser, bool corruptSignature = false) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".well-known/openid-configuration", StringComparison.Ordinal))
                return DiscoveryResponse();
            if (request.RequestUri.AbsolutePath == "/.well-known/jwks")
            {
                var parameters = signingKey.ExportParameters(false);
                return JsonResponse(JsonSerializer.Serialize(new
                {
                    keys = new[]
                    {
                        new
                        {
                            kid = "test-key",
                            kty = "RSA",
                            use = "sig",
                            alg = "RS256",
                            n = Base64Url(parameters.Modulus!),
                            e = Base64Url(parameters.Exponent!),
                        },
                    },
                }));
            }
            if (request.RequestUri.AbsolutePath == "/connect/token")
            {
                var fields = await ReadFormAsync(request, ct);
                if (fields["grant_type"] != "authorization_code" || fields["code"] != "single-use-code")
                    throw new InvalidOperationException("The CLI did not exchange the expected single-use code.");
                var authorization = ParseQuery(browser.AuthorizationUri!.Query);
                var verifier = fields["code_verifier"];
                var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
                if (challenge != authorization["code_challenge"] ||
                    fields["redirect_uri"] != CallbackUri ||
                    authorization["code_challenge_method"] != "S256")
                    throw new InvalidOperationException("The CLI did not bind the code exchange to S256 PKCE.");
                var nonce = authorization["nonce"];
                var idToken = CreateIdToken(signingKey, nonce);
                if (corruptSignature)
                {
                    var parts = idToken.Split('.');
                    parts[2] = (parts[2][0] == 'A' ? 'B' : 'A') + parts[2][1..];
                    idToken = string.Join('.', parts);
                }
                return JsonResponse(JsonSerializer.Serialize(new
                {
                    access_token = "memory-only-access-token",
                    refresh_token = "rotated-login-token",
                    id_token = idToken,
                    token_type = "Bearer",
                    expires_in = 3600,
                }));
            }
            throw new InvalidOperationException("Unexpected Broker request.");
        }
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            send(request, ct);
    }

    private sealed class StalledReadStream(TaskCompletionSource? readStarted = null) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            readStarted?.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private static string CreateIdToken(RSA key, string nonce)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"RS256","kid":"test-key","typ":"JWT"}"""));
        var payload = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            iss = Issuer,
            sub = "actor-123",
            aud = ClientId,
            nonce,
            iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1,
            exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600,
        })));
        var signingInput = Encoding.ASCII.GetBytes(header + "." + payload);
        var signature = key.SignData(signingInput, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return header + "." + payload + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
