using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Identity;

namespace Agentweaver.Environment.Tests;

public sealed class EnvironmentP1InvocationBindingHttpClientTests
{
    [Fact]
    public async Task CurrentBindingReadUsesExactAuthenticatedNoStoreOwnerRoute()
    {
        var binding = Binding();
        var handler = new ResponseHandler(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(binding, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    System.Text.Encoding.UTF8,
                    "application/json"),
                RequestMessage = request
            };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return response;
        });
        using var client = new HttpClient(handler);
        var credential = new SecretCredential(
            "run-bound.jwt.token",
            DateTimeOffset.UtcNow.AddMinutes(2),
            TimeProvider.System);
        var actor = new RuntimeActorAuthorization(credential, "tenant-a");
        var owner = new EnvironmentRuntimeOwnerContextClient(
            client,
            new EnvironmentRuntimeBootstrapDeliveryOptions(
                new Uri("https://orchestrator.example/"),
                new Uri("https://broker.example/")));

        try
        {
            var result = await owner.ReadCurrentAsync(
                actor, "project-a", "run-a", CancellationToken.None);

            Assert.Equal(binding, result);
            Assert.NotNull(handler.Request);
            Assert.Equal(HttpMethod.Get, handler.Request!.Method);
            Assert.Equal(
                "https://orchestrator.example/internal/projects/project-a/runs/run-a/p1-invocation-binding",
                handler.Request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", handler.Request.Headers.Authorization?.Scheme);
            Assert.Equal("run-bound.jwt.token", handler.Request.Headers.Authorization?.Parameter);
            Assert.Equal("tenant-a", Assert.Single(handler.Request.Headers.GetValues("X-Agentweaver-Tenant")));
            Assert.True(handler.Request.Headers.CacheControl?.NoStore);
            Assert.True(handler.Request.Headers.CacheControl?.NoCache);
        }
        finally
        {
            credential.Invalidate();
        }
    }

    [Fact]
    public async Task CurrentBindingReadRejectsCoreAuthorizationDenial()
    {
        var handler = new ResponseHandler(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                RequestMessage = request,
                Content = new StringContent("{}")
            };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return response;
        });
        using var client = new HttpClient(handler);
        var credential = new SecretCredential(
            "run-bound.jwt.token",
            DateTimeOffset.UtcNow.AddMinutes(2),
            TimeProvider.System);
        var actor = new RuntimeActorAuthorization(credential, "tenant-a");
        var owner = new EnvironmentRuntimeOwnerContextClient(
            client,
            new EnvironmentRuntimeBootstrapDeliveryOptions(
                new Uri("https://orchestrator.example/"),
                new Uri("https://broker.example/")));

        try
        {
            var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                owner.ReadCurrentAsync(actor, "project-a", "run-a", CancellationToken.None));

            Assert.Equal("runtime_owner_denied", failure.Code);
            Assert.Equal(
                "/internal/projects/project-a/runs/run-a/p1-invocation-binding",
                handler.Request!.RequestUri!.AbsolutePath);
        }
        finally
        {
            credential.Invalidate();
        }
    }

    private static P1InvocationBinding Binding() =>
        new(
            1,
            Guid.NewGuid(),
            Guid.NewGuid(),
            new string('a', 64),
            "tenant-a",
            "project-a",
            "run-a",
            "root-session-a",
            "https://identity.example",
            "actor-a",
            Guid.NewGuid(),
            1,
            new string('b', 64),
            3,
            1,
            DateTimeOffset.UtcNow);

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(respond(request));
        }
    }
}
