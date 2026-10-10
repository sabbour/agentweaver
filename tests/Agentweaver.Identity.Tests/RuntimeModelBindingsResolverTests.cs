using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Xunit;

namespace Agentweaver.Identity.Tests;

public sealed class RuntimeModelBindingsResolverTests
{
    [Fact]
    public void ConcretePromptCapacityChangesTheModelPinWithoutChangingLegacyNullHashes()
    {
        var original = Resolver();
        var model = new RuntimeModelBinding("concrete-model", ModelSourceMode.HostedCopilot);
        var explicitNull = new RuntimeModelBindingsResolver("model-bindings-v1",
            new Dictionary<string, RuntimeModelBinding>
            {
                ["accepted-reference"] = model with { PromptCapacityTokens = null }
            });
        Assert.Equal(original.ConfigurationHash, explicitNull.ConfigurationHash);
        var limited = new RuntimeModelBindingsResolver("model-bindings-v1",
            new Dictionary<string, RuntimeModelBinding>
            {
                ["accepted-reference"] = model with { PromptCapacityTokens = 16000 }
            });
        Assert.NotEqual(original.ConfigurationHash, limited.ConfigurationHash);
        Assert.Equal("runtime_model_binding_pin_mismatch",
            Assert.Throws<RuntimeAuthorizationException>(() => limited.Resolve(
                "accepted-reference", ModelSourceMode.HostedCopilot,
                original.Pin("accepted-reference", ModelSourceMode.HostedCopilot))).Code);
        Assert.Throws<ArgumentException>(() => new RuntimeModelBindingsResolver("model-bindings-v1",
            new Dictionary<string, RuntimeModelBinding>
            {
                ["accepted-reference"] = model with { PromptCapacityTokens = 0 }
            }));
    }

    [Fact]
    public void ResolvesTheAcceptedReferenceRatherThanTreatingItAsAModelId()
    {
        var resolver = Resolver();
        var pin = resolver.Pin("accepted-reference", ModelSourceMode.HostedCopilot);

        Assert.Equal("concrete-model", pin.ModelId);
        Assert.Equal("accepted-reference", pin.ModelSelectionReference);
        Assert.Equal("model-bindings-v1", pin.ConfigurationRevision);
        Assert.Equal(resolver.ConfigurationHash, pin.ConfigurationHash);
        Assert.Equal("concrete-model",
            resolver.Resolve("accepted-reference", ModelSourceMode.HostedCopilot, pin).ModelId);
        Assert.Equal("runtime_model_reference_unavailable",
            Assert.Throws<RuntimeAuthorizationException>(() =>
                resolver.Resolve("concrete-model", ModelSourceMode.HostedCopilot)).Code);
    }

    [Theory]
    [InlineData("reference")]
    [InlineData("model")]
    [InlineData("mode")]
    [InlineData("revision")]
    [InlineData("hash")]
    public void RejectsAnyChangedAcceptedBindingPin(string field)
    {
        var resolver = Resolver();
        var pin = resolver.Pin("accepted-reference", ModelSourceMode.HostedCopilot);
        pin = field switch
        {
            "reference" => pin with { ModelSelectionReference = "other-reference" },
            "model" => pin with { ModelId = "other-model" },
            "mode" => pin with { SourceMode = ModelSourceMode.Byok },
            "revision" => pin with { ConfigurationRevision = "model-bindings-v2" },
            _ => pin with { ConfigurationHash = new string('0', 64) }
        };
        Assert.Equal("runtime_model_binding_pin_mismatch",
            Assert.Throws<RuntimeAuthorizationException>(() =>
                resolver.Resolve("accepted-reference", ModelSourceMode.HostedCopilot, pin)).Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ModelSourceMode.Byok)]
    [InlineData((ModelSourceMode)99)]
    public void DoesNotDefaultOrSwitchTheAcceptedSourceMode(ModelSourceMode? mode)
    {
        Assert.Equal("runtime_model_source_mode_mismatch",
            Assert.Throws<RuntimeAuthorizationException>(() =>
                Resolver().Resolve("accepted-reference", mode)).Code);
    }

    [Fact]
    public void DisabledBindingCannotBeResolvedOrPinned()
    {
        var resolver = new RuntimeModelBindingsResolver("model-bindings-v1",
            new Dictionary<string, RuntimeModelBinding>
            {
                ["accepted-reference"] = new("concrete-model", ModelSourceMode.HostedCopilot) { Enabled = false }
            });
        Assert.Equal("runtime_model_disabled",
            Assert.Throws<RuntimeAuthorizationException>(() =>
                resolver.Pin("accepted-reference", ModelSourceMode.HostedCopilot)).Code);
    }

    [Fact]
    public void ConfigurationHashIsIndependentOfMappingAndHeaderInsertionOrder()
    {
        var first = ByokProvider([new("x-region", "west"), new("x-version", "one")]);
        var second = ByokProvider([new("x-version", "one"), new("x-region", "west")]);
        var hosted = new RuntimeModelBinding("hosted-model", ModelSourceMode.HostedCopilot);
        var byok = new RuntimeModelBinding("byok-model", ModelSourceMode.Byok, first);
        var left = new RuntimeModelBindingsResolver("model-bindings-v1",
            new Dictionary<string, RuntimeModelBinding> { ["hosted"] = hosted, ["byok"] = byok });
        var right = new RuntimeModelBindingsResolver("model-bindings-v1",
            new Dictionary<string, RuntimeModelBinding>
            {
                ["byok"] = byok with { Provider = second }, ["hosted"] = hosted
            });

        Assert.Equal(left.ConfigurationHash, right.ConfigurationHash);
        Assert.Equal(left.Pin("byok", ModelSourceMode.Byok), right.Pin("byok", ModelSourceMode.Byok));
        var changed = new RuntimeModelBindingsResolver("model-bindings-v1",
            new Dictionary<string, RuntimeModelBinding>
            {
                ["hosted"] = hosted,
                ["byok"] = byok with { Provider = ByokProvider([new("x-region", "east"), new("x-version", "one")]) }
            });
        Assert.NotEqual(left.ConfigurationHash, changed.ConfigurationHash);
        Assert.Equal("runtime_model_binding_pin_mismatch",
            Assert.Throws<RuntimeAuthorizationException>(() =>
                changed.Resolve("byok", ModelSourceMode.Byok, left.Pin("byok", ModelSourceMode.Byok))).Code);
    }

    [Fact]
    public void SnapshotsTheServerOwnedMapAndRejectsARevisionChange()
    {
        var bindings = new Dictionary<string, RuntimeModelBinding>
        {
            ["accepted-reference"] = new("concrete-model", ModelSourceMode.HostedCopilot)
        };
        var original = new RuntimeModelBindingsResolver("model-bindings-v1", bindings);
        var pin = original.Pin("accepted-reference", ModelSourceMode.HostedCopilot);
        bindings["accepted-reference"] = new("replacement-model", ModelSourceMode.HostedCopilot);
        Assert.Equal("concrete-model", original.Resolve("accepted-reference", ModelSourceMode.HostedCopilot).ModelId);
        var revised = new RuntimeModelBindingsResolver("model-bindings-v2",
            new Dictionary<string, RuntimeModelBinding>
            {
                ["accepted-reference"] = new("concrete-model", ModelSourceMode.HostedCopilot)
            });
        Assert.Equal(original.ConfigurationHash, revised.ConfigurationHash);
        Assert.Equal("runtime_model_binding_pin_mismatch",
            Assert.Throws<RuntimeAuthorizationException>(() =>
                revised.Resolve("accepted-reference", ModelSourceMode.HostedCopilot, pin)).Code);
    }

    [Theory]
    [InlineData("", "model")]
    [InlineData("revision", "")]
    public void RejectsInvalidConfigurationIdentity(string revision, string modelId)
    {
        Assert.Throws<RuntimeAuthorizationException>(() =>
            new RuntimeModelBindingsResolver(revision, new Dictionary<string, RuntimeModelBinding>
            {
                ["accepted-reference"] = new(modelId, ModelSourceMode.HostedCopilot)
            }));
    }

    private static RuntimeModelBindingsResolver Resolver() =>
        new("model-bindings-v1", new Dictionary<string, RuntimeModelBinding>
        {
            ["accepted-reference"] = new("concrete-model", ModelSourceMode.HostedCopilot)
        });

    private static RuntimeByokProvider ByokProvider(KeyValuePair<string, string>[] headers) =>
        new("openai", new Uri("https://byok.test/v1"))
        {
            Headers = headers.ToImmutableDictionary(StringComparer.Ordinal)
        };
}
