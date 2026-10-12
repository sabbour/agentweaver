extern alias GatewayHost;

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class GatewayRuntimeConfigurationProjectionTests
{
    [Theory]
    [InlineData("exact")]
    [InlineData("bytes")]
    [InlineData("reference")]
    [InlineData("private-reference")]
    [InlineData("event-kind")]
    public void RuntimeConfigurationEventsCannotExposePrivateBytesOrObjectReferences(string fault)
    {
        var envelope = JsonNode.Parse("""
            {
              "schemaVersion": 1, "eventVersion": 1,
              "eventId": "00000000-0000-0000-0000-000000000001",
              "identity": { "projectId": "project", "runId": "run", "sessionId": "session" },
              "position": 1, "occurredAt": "2026-10-11T12:00:00Z",
              "kind": "runtimeConfiguration",
              "payload": { "kind": "runtime_configuration", "configuration": null },
              "objectReferences": []
            }
            """)!;
        if (fault == "bytes")
            envelope["payload"]!["configuration"] = JsonNode.Parse("""{"server":"private"}""");
        if (fault is "reference" or "private-reference")
        {
            envelope["objectReferences"] = JsonSerializer.SerializeToNode(new[]
            {
                new
                {
                    reference = new
                    {
                        key = "configuration-object",
                        purpose = fault == "private-reference" ? "runtime-configuration" : "public-reference",
                        byteLength = 2
                    },
                    retention = new { ownerId = "session", retainUntil = "2026-10-12T12:00:00Z" }
                }
            });
        }
        if (fault == "event-kind")
            envelope["kind"] = "turn";
        var projection = typeof(GatewayHost::Agentweaver.Gateway.GatewayOwnerClient).Assembly
            .GetType("Agentweaver.Gateway.GatewayRunEventProjection", throwOnError: true)!
            .GetMethod("ProjectEvent", BindingFlags.Public | BindingFlags.Static)!;
        var input = JsonSerializer.SerializeToElement(envelope);
        if (fault == "exact")
        {
            var projected = Assert.IsType<JsonElement>(projection.Invoke(null, [input]));
            Assert.True(JsonElement.DeepEquals(input, projected));
            Assert.Equal(JsonValueKind.Null, projected.GetProperty("payload").GetProperty("configuration").ValueKind);
            Assert.Empty(projected.GetProperty("objectReferences").EnumerateArray());
        }
        else
        {
            var failure = Assert.Throws<TargetInvocationException>(() => projection.Invoke(null, [input]));
            Assert.Equal("events_page_contract_invalid", failure.InnerException!.Message);
        }
    }
}
