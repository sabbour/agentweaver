using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Agentweaver.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed partial class ProjectsConfigBrokerAuthorizationTests
{
    [Fact]
    public async Task ProductionGatewayAcceptsOnlyGenuineBrokerTokensForItsAudience()
    {
        var validBrokerToken = await IssueTokenForAudienceBrokerAsync(
            "https://api.test/", "gateway-production-openiddict");
        var wrongAudienceBrokerToken = await IssueTokenForAudienceBrokerAsync(
            "https://other-api.test/", "gateway-wrong-audience");
        Assert.Equal(
            "https://api.test/",
            Assert.Single(new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
                .ReadJwtToken(validBrokerToken).Audiences));
        Assert.Equal(
            "https://other-api.test/",
            Assert.Single(new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
                .ReadJwtToken(wrongAudienceBrokerToken).Audiences));

        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath,
            _signingCertificate.Password,
            X509KeyStorageFlags.EphemeralKeySet);
        var forwardedBearerTokens = new List<string?>();
        await using var gateway = GatewayProductionResourceServer.Start(
            new X509SecurityKey(certificate),
            (request, _) =>
            {
                forwardedBearerTokens.Add(request.Headers.Authorization?.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json"),
                });
            });
        using var validRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/projects");
        validRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", validBrokerToken);
        using var validResponse = await gateway.Client.SendAsync(validRequest);
        Assert.Equal(HttpStatusCode.OK, validResponse.StatusCode);
        Assert.Equal("[]", await validResponse.Content.ReadAsStringAsync());
        Assert.Equal(["Bearer " + validBrokerToken], forwardedBearerTokens);

        using var wrongAudienceRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/projects");
        wrongAudienceRequest.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", wrongAudienceBrokerToken);
        using var wrongAudienceResponse = await gateway.Client.SendAsync(wrongAudienceRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongAudienceResponse.StatusCode);
        Assert.Equal("application/problem+json", wrongAudienceResponse.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await wrongAudienceResponse.Content.ReadAsStringAsync());
        Assert.Equal("unauthenticated", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(["Bearer " + validBrokerToken], forwardedBearerTokens);
    }

    [Fact]
    public async Task ProductionGatewayStopsSseWhenBrokerTokenExpiresDuringProjectsBodyRead()
    {
        var token = await IssueTokenForAudienceBrokerAsync(
            "https://api.test/",
            "gateway-project-body-expiry",
            TimeSpan.FromSeconds(15));
        var validTo = new DateTimeOffset(
            new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
                .ReadJwtToken(token).ValidTo,
            TimeSpan.Zero);
        Assert.InRange(
            validTo - DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(25));

        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath,
            _signingCertificate.Password,
            X509KeyStorageFlags.EphemeralKeySet);
        var projectBodyReadStarted = new TaskCompletionSource<DateTimeOffset>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var projectBodyReadCompleted = new TaskCompletionSource<DateTimeOffset>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSseEvent = new TaskCompletionSource<(string Data, DateTimeOffset WrittenAt)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSseEvent = new TaskCompletionSource<(string Data, DateTimeOffset WrittenAt)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sseEventCount = 0;
        var projectRequestCount = 0;
        var firstEventId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        await using var gateway = GatewayProductionResourceServer.Start(
            new X509SecurityKey(certificate),
            (_, _) =>
            {
                var requestNumber = Interlocked.Increment(ref projectRequestCount);
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = requestNumber == 4
                        ? new StreamContent(new ExpiringOwnerBodyStream(
                            validTo, projectBodyReadStarted, projectBodyReadCompleted))
                        : new StringContent("{}"),
                };
                response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                return Task.FromResult(response);
            },
            (request, _) =>
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(
                    request.RequestUri?.Query ?? string.Empty);
                var cursor = query.TryGetValue("cursor", out var cursorValues)
                    ? cursorValues.SingleOrDefault()
                    : null;
                var page = cursor is null
                    ? """{"events":[{"eventId":"00000000-0000-0000-0000-000000000001"}],"nextCursor":"expiry-cursor-1"}"""
                    : cursor == "expiry-cursor-1"
                        ? """{"events":[{"eventId":"00000000-0000-0000-0000-000000000002"}],"nextCursor":"expiry-cursor-2"}"""
                        : $$"""{"events":[],"nextCursor":"{{cursor}}"}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(page, Encoding.UTF8, "application/json"),
                });
            },
            ownerRequestTimeoutSeconds: 30,
            observeSseData: data =>
            {
                var observed = (data, DateTimeOffset.UtcNow);
                switch (Interlocked.Increment(ref sseEventCount))
                {
                    case 1:
                        firstSseEvent.TrySetResult(observed);
                        break;
                    case 2:
                        secondSseEvent.TrySetResult(observed);
                        break;
                }
            });

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/projects/expiry-project/runs/expiry-run/events/live");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var responseTask = gateway.Client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        var first = await firstSseEvent.Task.WaitAsync(cancellation.Token);
        Assert.True(first.WrittenAt < validTo);
        using (var firstEventData = JsonDocument.Parse(first.Data))
            Assert.Equal(firstEventId, firstEventData.RootElement.GetProperty("eventId").GetGuid());
        var readStartedAt = await projectBodyReadStarted.Task.WaitAsync(cancellation.Token);
        Assert.True(readStartedAt < validTo);
        var readCompletedAt = await projectBodyReadCompleted.Task.WaitAsync(cancellation.Token);
        Assert.True(readCompletedAt > validTo);
        try
        {
            using var stoppedResponse = await responseTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.OK, stoppedResponse.StatusCode);
        }
        catch (HttpRequestException)
        {
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            cancellation.Cancel();
        }

        Assert.Equal(4, Volatile.Read(ref projectRequestCount));
        Assert.Equal(1, Volatile.Read(ref sseEventCount));
        Assert.False(secondSseEvent.Task.IsCompleted);
    }

    private async Task<string> IssueTokenForAudienceBrokerAsync(
        string audience,
        string subject,
        TimeSpan? accessTokenLifetime = null,
        string? additionalScopes = null,
        IReadOnlyList<string>? roles = null)
    {
        var database = await postgres.CreateMigratedDatabaseAsync();
        var extraScopes = (additionalScopes ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        await using var brokerFactory = new IdentityBrokerWebApplicationFactory(
            database,
            _fakeIdp,
            signingCertificate: _signingCertificate,
            configure: settings =>
            {
                settings["IdentityBroker__Clients__0__Resources__0"] = audience;
                settings["IdentityBroker__SecretRedemption__Audience"] = audience;
                for (var i = 0; i < extraScopes.Length; i++)
                    settings[$"IdentityBroker__Clients__0__Scopes__{i + IdentityBrokerWebApplicationFactory.TestClientScopes.Length}"] =
                        extraScopes[i];
            },
            configureServices: accessTokenLifetime is { } lifetime
                ? services => services.PostConfigure<OpenIddictServerOptions>(
                    options => options.AccessTokenLifetime = lifetime)
                : null);

        _fakeIdp.Subject = subject;
        _fakeIdp.TenantIds = [TenantId];
        _fakeIdp.Roles = roles?.ToArray() ?? ["orchestrator"];
        using var broker = brokerFactory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://broker.test.local"),
        });
        var (verifier, challenge) = Pkce.Create();
        var scope = "openid profile email api.read offline_access";
        if (extraScopes.Length > 0)
            scope += " " + string.Join(' ', extraScopes);
        var code = await BrokerFlowDriver.AuthorizeWithConsentAsync(
            broker,
            _fakeIdpClient,
            IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            scope,
            challenge);
        var tokens = await BrokerFlowDriver.ExchangeCodeForTokensAsync(
            broker,
            IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri,
            code,
            verifier);
        return tokens.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("The Identity broker did not return an access token.");
    }

    private sealed class ExpiringOwnerBodyStream(
        DateTimeOffset expiresAt,
        TaskCompletionSource<DateTimeOffset> readStarted,
        TaskCompletionSource<DateTimeOffset> readCompleted) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            readStarted.TrySetResult(DateTimeOffset.UtcNow);
            var delay = expiresAt.AddMilliseconds(250) - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);
            readCompleted.TrySetResult(DateTimeOffset.UtcNow);
            return 0;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
