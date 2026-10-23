using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.Identity;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class RuntimeHostedModelReasoningCatalogHttpClientTests
{
    [Fact]
    public async Task PublishesOnlyVerifiedLevelsAndUsesCallerAuthorization()
    {
        var projectId = Guid.NewGuid().ToString("N");
        var connectionId = Guid.NewGuid();
        var verifiedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        string? body = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                $"/api/projects/{projectId}/configuration/hosted-model-reasoning-catalog",
                request.RequestUri?.AbsolutePath);
            Assert.Equal("caller-token", request.Headers.Authorization?.Parameter);
            Assert.Equal("tenant", Assert.Single(request.Headers.GetValues("X-Agentweaver-Tenant")));
            Assert.True(request.Headers.CacheControl?.NoStore);
            body = await request.Content!.ReadAsStringAsync();
            var response = new HttpResponseMessage(HttpStatusCode.NoContent);
            response.RequestMessage = request;
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return response;
        }));
        var actor = new RuntimeActorAuthorization(
            new SecretCredential("caller-token", DateTimeOffset.UtcNow.AddMinutes(2)), "tenant");
        var publisher = new RuntimeHostedModelReasoningCatalogHttpClient(
            http, new("https://projects-config.test/"), actor);

        var status = await publisher.PublishAsync(
            projectId,
            new("bindings-r4", "hosted-reasoning", connectionId, ["low", "high"], verifiedAt),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.NoContent, status);
        using var payload = JsonDocument.Parse(body!);
        Assert.Equal("bindings-r4", payload.RootElement.GetProperty("bindingsRevision").GetString());
        Assert.Equal("hosted-reasoning", payload.RootElement.GetProperty("reference").GetString());
        Assert.Equal(connectionId, payload.RootElement.GetProperty("connectionId").GetGuid());
        Assert.Equal(verifiedAt, payload.RootElement.GetProperty("verifiedAt").GetDateTimeOffset());
        Assert.Equal(
            ["low", "high"],
            payload.RootElement.GetProperty("supportedReasoningEfforts").EnumerateArray()
                .Select(item => item.GetString()!).ToArray());
        Assert.Equal(
            ["bindingsRevision", "connectionId", "reference", "supportedReasoningEfforts", "verifiedAt"],
            payload.RootElement.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("modelId", body!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hosted-token", body!, StringComparison.Ordinal);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
