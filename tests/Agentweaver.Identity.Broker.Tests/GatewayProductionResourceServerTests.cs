using System.Net;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class GatewayProductionResourceServerTests
{
    [Fact]
    public async Task InMemoryOwnerHandlersReplaceProductionBackendCaHandlers()
    {
        var ownerCalls = new List<Uri?>();
        Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken _)
        {
            ownerCalls.Add(request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        await using var gateway = GatewayProductionResourceServer.Start(
            new SymmetricSecurityKey(new byte[32]),
            Respond,
            Respond,
            Respond,
            orchestratorOwner: Respond);

        foreach (var (name, address) in new[]
                 {
                     ("Projects", new Uri("https://projects.test/")),
                     ("Events", new Uri("https://events.test/")),
                     ("Knowledge", new Uri("https://knowledge.test/")),
                     ("Orchestrator", new Uri("https://orchestrator.test/")),
                 })
        {
            using var client = gateway.CreateOwnerClient(name);
            using var response = await client.GetAsync(address);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(4, ownerCalls.Count);
    }
}
