using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.SourceControl;
using Xunit;

namespace Agentweaver.SourceControl.Tests;

public sealed class GitHubAppInstallationTokenIssuerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-08T09:00:00Z");

    [Fact]
    public async Task MintsOneRepositoryTokenWithExpectedJwtPermissionCeilingAndProviderExpiry()
    {
        using var rsa = RSA.Create(2048);
        var handler = new StubHandler(async (request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "/app/installations/456/access_tokens",
                request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("2022-11-28", request.Headers.GetValues("X-GitHub-Api-Version").Single());
            var jwtParts = request.Headers.Authorization.Parameter!.Split('.');
            Assert.Equal(3, jwtParts.Length);
            Assert.Equal("RS256", DecodeJson(jwtParts[0]).GetProperty("alg").GetString());
            var claims = DecodeJson(jwtParts[1]);
            Assert.Equal("123", claims.GetProperty("iss").GetString());
            Assert.Equal(Now.ToUnixTimeSeconds() - 60, claims.GetProperty("iat").GetInt64());
            Assert.Equal(Now.AddMinutes(9).ToUnixTimeSeconds(), claims.GetProperty("exp").GetInt64());
            Assert.True(rsa.VerifyData(
                Encoding.ASCII.GetBytes($"{jwtParts[0]}.{jwtParts[1]}"),
                DecodeBase64Url(jwtParts[2]),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1));

            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var requestBody = body.RootElement;
            Assert.Equal(new long[] { 789 }, requestBody.GetProperty("repository_ids").EnumerateArray()
                .Select(value => value.GetInt64()).ToArray());
            var requestedPermissions = requestBody.GetProperty("permissions");
            Assert.Equal(2, requestedPermissions.EnumerateObject().Count());
            Assert.Equal("write", requestedPermissions.GetProperty("contents").GetString());
            Assert.Equal("write", requestedPermissions.GetProperty("pull_requests").GetString());

            return JsonResponse(
                "{\"token\":\"installation-token\",\"expires_at\":\"2026-10-08T09:47:00Z\"," +
                "\"permissions\":{\"contents\":\"write\",\"metadata\":\"read\"," +
                "\"pull_requests\":\"write\"}}");
        });
        var time = new FrozenTimeProvider(Now);
        var key = new SecretCredential(rsa.ExportPkcs8PrivateKeyPem(), Now.AddHours(1), time);
        var issuer = CreateIssuer(handler, time);

        var issued = await issuer.MintAsync(456, 789, key, CancellationToken.None);

        Assert.Equal(Now.AddMinutes(47), issued.Credential.ExpiresAt);
        Assert.Equal("installation-token", issued.Credential.GetValue());
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                "github-app-permissions-v1\ncontents:write\nmetadata:read\npull_requests:write"))),
            issued.PermissionDigest);
        Assert.False(key.IsUsable());
        Assert.DoesNotContain("installation-token", issued.Credential.ToString());
    }

    [Fact]
    public async Task RejectsAdditionalPermissionsAndInvalidatesPrivateKey()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(
            "{\"token\":\"installation-token\",\"expires_at\":\"2026-10-08T09:47:00Z\"," +
            "\"permissions\":{\"contents\":\"write\",\"administration\":\"write\"," +
            "\"pull_requests\":\"write\"}}")));
        var time = new FrozenTimeProvider(Now);
        var key = NewKey(time);
        var issuer = CreateIssuer(handler, time);

        var exception = await Assert.ThrowsAsync<SourceControlOperationException>(
            () => issuer.MintAsync(456, 789, key, CancellationToken.None));

        Assert.Equal(SourceControlFailureCode.RemoteOutcomeUncertain, exception.Code);
        Assert.False(key.IsUsable());
        Assert.DoesNotContain("installation-token", exception.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, SourceControlFailureCode.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, SourceControlFailureCode.RemoteOutcomeUncertain)]
    public async Task SurfacesRateLimitAndUncertainProviderFailuresWithoutRetry(
        HttpStatusCode statusCode,
        SourceControlFailureCode expectedCode)
    {
        var calls = 0;
        var handler = new StubHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(statusCode));
        });
        var time = new FrozenTimeProvider(Now);
        var key = NewKey(time);
        var issuer = CreateIssuer(handler, time);

        var exception = await Assert.ThrowsAsync<SourceControlOperationException>(
            () => issuer.MintAsync(456, 789, key, CancellationToken.None));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(1, calls);
        Assert.False(key.IsUsable());
    }

    [Fact]
    public async Task DoesNotMakeRequestForInvalidRepositoryId()
    {
        var calls = 0;
        var handler = new StubHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(JsonResponse("{}"));
        });
        var time = new FrozenTimeProvider(Now);
        var key = NewKey(time);
        var issuer = CreateIssuer(handler, time);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => issuer.MintAsync(456, 0, key, CancellationToken.None));

        Assert.Equal(0, calls);
        Assert.True(key.IsUsable());
    }

    private static GitHubAppInstallationTokenIssuer CreateIssuer(
        HttpMessageHandler handler,
        TimeProvider time) =>
        new(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.github.com/")
        }, 123, time);

    private static SecretCredential NewKey(TimeProvider time)
    {
        using var rsa = RSA.Create(2048);
        return new SecretCredential(rsa.ExportPkcs8PrivateKeyPem(), Now.AddHours(1), time);
    }

    private static JsonElement DecodeJson(string base64Url)
    {
        using var document = JsonDocument.Parse(DecodeBase64Url(base64Url));
        return document.RootElement.Clone();
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        return Convert.FromBase64String(base64);
    }

    private static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
