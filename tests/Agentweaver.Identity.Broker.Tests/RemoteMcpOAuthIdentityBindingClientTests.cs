using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class RemoteMcpOAuthIdentityBindingClientTests
{
    [Fact]
    public async Task LinkUsesTheExactEnvironmentBindingAndValidatesItsReceipt()
    {
        var projectId = "project-1";
        var connectionId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var operationId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var reference = Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff").ToString("N");
        var input = new LinkRemoteMcpIdentityBindingRequest(4, new string('a', 64), reference, operationId);
        var receipt = new RemoteMcpIdentityBindingReceipt(
            projectId, connectionId, operationId, 5, new string('b', 64), reference);
        HttpRequestMessage? observed = null;
        using var client = new RemoteMcpOAuthIdentityBindingClient(
            new Uri("https://environment.test/"),
            new Handler(async request =>
            {
                observed = request;
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal(
                    $"https://environment.test/api/projects/{projectId}/remote-mcp/connections/{connectionId:D}/identity-binding",
                    request.RequestUri!.AbsoluteUri);
                Assert.Equal("actor-token", request.Headers.Authorization?.Parameter);
                Assert.Equal("tenant-1", Assert.Single(request.Headers.GetValues("X-Agentweaver-Tenant")));
                Assert.True(request.Headers.CacheControl?.NoStore);
                Assert.True(request.Headers.CacheControl?.NoCache);
                var json = await request.Content!.ReadAsStringAsync();
                Assert.DoesNotContain("actor-token", json);
                Assert.Equal(input, JsonSerializer.Deserialize<LinkRemoteMcpIdentityBindingRequest>(
                    json, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                return Success(request, receipt);
            }));

        var actual = await client.LinkAsync(
            Actor(), projectId, connectionId, input, CancellationToken.None);

        Assert.Equal(receipt, actual);
        Assert.NotNull(observed);
    }

    [Fact]
    public async Task LinkMapsRevisionConflictWithoutAcceptingTheReceipt()
    {
        using var client = Client(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                RequestMessage = request,
                Content = new StringContent("{}")
            };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return Task.FromResult(response);
        });

        var error = await Assert.ThrowsAsync<RemoteMcpOAuthEnvironmentException>(() =>
            client.LinkAsync(Actor(), "project-1", ConnectionId(),
                Input(), CancellationToken.None));

        Assert.Equal("remote_mcp_connection_revision_conflict", error.Code);
        Assert.Equal(StatusCodes.Status409Conflict, error.StatusCode);
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("cacheable")]
    [InlineData("bad-receipt")]
    public async Task LinkRejectsRedirectsCacheableResponsesAndMismatchedReceipts(string failure)
    {
        using var client = Client(request =>
        {
            var response = Success(request, Receipt() with
            {
                ConnectionId = failure == "bad-receipt" ? Guid.NewGuid() : ConnectionId()
            });
            if (failure == "redirect")
            {
                response.StatusCode = HttpStatusCode.TemporaryRedirect;
                response.Headers.Location = new Uri("https://foreign.test/");
            }
            else if (failure == "cacheable")
                response.Headers.CacheControl = null;
            return Task.FromResult(response);
        });

        var error = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            client.LinkAsync(Actor(), "project-1", ConnectionId(), Input(), CancellationToken.None));

        Assert.Equal(failure switch
        {
            "redirect" => "runtime_owner_redirect_rejected",
            "cacheable" => "runtime_owner_cache_contract_invalid",
            _ => "runtime_owner_contract_invalid"
        }, error.Code);
    }

    private static RemoteMcpOAuthIdentityBindingClient Client(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send) =>
        new(new Uri("https://environment.test/"), new Handler(send));

    private static RuntimeActorAuthorization Actor() =>
        new(new SecretCredential("actor-token", DateTimeOffset.UtcNow.AddMinutes(2)), "tenant-1");

    private static Guid ConnectionId() =>
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private static LinkRemoteMcpIdentityBindingRequest Input() =>
        new(4, new string('a', 64),
            Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff").ToString("N"),
            Guid.Parse("11111111-2222-3333-4444-555555555555"));

    private static RemoteMcpIdentityBindingReceipt Receipt() =>
        new("project-1", ConnectionId(), Input().OperationId, 5, new string('b', 64),
            Input().IdentityBindingReference);

    private static HttpResponseMessage Success(
        HttpRequestMessage request, RemoteMcpIdentityBindingReceipt receipt) => new(HttpStatusCode.OK)
    {
        RequestMessage = request,
        Content = JsonContent.Create(receipt),
        Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
    };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
