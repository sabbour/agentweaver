using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class RuntimeOwnerHttpTransportTests
{
    [Fact]
    public async Task PendingVerifierForwardsOnlyTheProtectedActorAndExactTransientProof()
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(1);
        var grant = Guid.NewGuid();
        var runtime = Guid.NewGuid();
        var operation = Guid.NewGuid();
        var nonce = new string('a', 64);
        var actor = new RuntimeActorAuthorization(
            new SecretCredential("protected-actor-token", expires), "tenant");
        var audience = new Uri("https://runtime.test/configure");
        var receipt = new RuntimeGrantReceipt(
            grant, runtime, 1, 1, "https://broker.test/", RuntimeCredentialPurpose.Configure,
            audience, RuntimeCredentialState.Active, new string('b', 64), expires, DateTimeOffset.UtcNow);
        var handler = new RecordingHandler(async request =>
        {
            Assert.Equal(new Uri("https://broker.test/internal/runtime/bootstrap/verify-pending"), request.RequestUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("protected-actor-token", request.Headers.Authorization?.Parameter);
            Assert.Equal("tenant", Assert.Single(request.Headers.GetValues("X-Agentweaver-Tenant")));
            var content = request.Content ?? throw new InvalidOperationException("The pending proof body is missing.");
            var body = await content.ReadFromJsonAsync<PendingBootstrapVerificationRequest>();
            Assert.NotNull(body);
            Assert.Equal(nonce, body.CredentialValue);
            Assert.Equal(operation, body.DeliveryOperationId);
            Assert.DoesNotContain(nonce, body.ToString());
            var json = await content.ReadAsStringAsync();
            Assert.DoesNotContain("protected-actor-token", json);
            Assert.DoesNotContain("purpose", json);
            return Response(request, receipt);
        });
        using var client = new HttpClient(handler);
        var verifier = new RuntimePendingBootstrapHttpClient(client, new Uri("https://broker.test/"), actor);
        Assert.Equal(receipt, await verifier.VerifyPendingBootstrapDeliveryAsync(
            new RuntimeCredentialProof(grant, runtime, 1, RuntimeCredentialPurpose.Configure,
                audience, receipt.ConfigurationHash, new SecretCredential(nonce, expires)),
            operation, default));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("foreign-response")]
    [InlineData("cacheable")]
    [InlineData("denied")]
    [InlineData("unknown-field")]
    public async Task FixedOwnerTransportRejectsRedirectsCachingDenialAndMalformedResponses(string failure)
    {
        var handler = new RecordingHandler(request =>
        {
            var response = Response(request, new RuntimeBootstrapRequest(
                Guid.NewGuid(), Guid.NewGuid(), new string('a', 64)));
            if (failure == "redirect")
            {
                response.StatusCode = HttpStatusCode.TemporaryRedirect;
                response.Headers.Location = new Uri("https://foreign.test/");
            }
            if (failure == "foreign-response")
                response.RequestMessage = new HttpRequestMessage(HttpMethod.Post, "https://foreign.test/");
            if (failure == "cacheable")
                response.Headers.CacheControl = null;
            if (failure == "denied")
                response.StatusCode = HttpStatusCode.Forbidden;
            if (failure == "unknown-field")
                response.Content = new StringContent("""{"runtimeInstanceId":null,"callerTrusted":true}""");
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            RuntimeOwnerHttpTransport.SendAsync<RuntimeBootstrapRequest>(
                client, new Uri("https://broker.test/"), "/internal/runtime/bootstrap/request",
                new RuntimeActorAuthorization(
                    new SecretCredential("actor", DateTimeOffset.UtcNow.AddMinutes(1)), null),
                new RuntimeBootstrapRequest(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64)), default));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("http://broker.test/")]
    [InlineData("https://user@broker.test/")]
    [InlineData("https://broker.test/?target=foreign")]
    [InlineData("https://broker.test/path")]
    public void OwnerAddressCannotSelectAnArbitraryDeliveryTarget(string address)
    {
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeOwnerHttpTransport.RequireOwnerAddress(new Uri(address)));
        using var handler = RuntimeOwnerHttpTransport.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task RegistrationClientAcceptsTheOrchestratorCanonicalStringEnumContract()
    {
        var registration = new RuntimeRegistration(Guid.NewGuid(), 1,
            new RuntimeBinding("https://broker.test/", Guid.NewGuid().ToString("D"), "tenant", "project", "run",
                "session", "agent", "turn", 1, 1, 1, "context:1", new string('a', 64), 1, "environment",
                "placement", 1, "profile", new Uri("https://runtime.test/configure"),
                new Uri("https://orchestrator.test/internal/runtime/observations"))
            {
                EnvironmentCurrentFencingGeneration = 2,
                EnvironmentProviderFencingGeneration = 3
            }, RuntimeRegistrationState.Active, DateTimeOffset.UtcNow.AddMinutes(1));
        var handler = new RecordingHandler(request =>
        {
            var response = Response(request, registration);
            response.Content = JsonContent.Create(registration, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
            });
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        var owner = new RuntimeRegistrationHttpClient(client, new Uri("https://orchestrator.test/"));
        Assert.Equal(registration, await owner.ReadCurrentAsync(registration.RuntimeInstanceId,
            new RuntimeActorAuthorization(new SecretCredential("actor", DateTimeOffset.UtcNow.AddMinutes(2)), "tenant"),
            default));
    }

    private static HttpResponseMessage Response<T>(HttpRequestMessage request, T body) => new(HttpStatusCode.OK)
    {
        RequestMessage = request,
        Content = JsonContent.Create(body),
        Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
    };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return send(request);
        }
    }
}
