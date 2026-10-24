using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class PlatformRuntimeDefaultsInputTests
{
    [Fact]
    public void ReviewedInputDeserializesAndPassesTheRuntimeValidator()
    {
        var json = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "platform-runtime-defaults.json"));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

        var parsed = JsonSerializer.Deserialize<PlatformRuntimeDefaults>(json, options);
        Assert.NotNull(parsed);
        var defaults = ProjectConfigurationValidator.Validate(parsed);

        Assert.Null(defaults.ModelSelection);
        Assert.Empty(defaults.EgressBaseline);
        Assert.Equal(
            ModelTaskSelectionDefaults.Create().Select(item => item.TaskKind),
            defaults.ModelTaskDefaults.Select(item => item.TaskKind));
        var availableReferences = new HashSet<string>(
            ["reasoning", "coding", "gpt-6.1-sol", "gpt-6-luna"], StringComparer.Ordinal);
        Assert.All(defaults.ModelTaskDefaults, item =>
            Assert.Contains(item.Selection.Reference!, availableReferences));
        Assert.Equal(12, defaults.RunLimits.MaxModelTurns);
        Assert.Equal(100, defaults.RunLimits.MaxToolCalls);
        Assert.Equal(4, defaults.RunLimits.MaxChildren);
        Assert.Equal(2, defaults.RunLimits.MaxConcurrentChildren);
        Assert.Equal(3600, defaults.RunLimits.MaxWallTimeSeconds);
        Assert.Equal(20000, defaults.RunLimits.MaxPromptTokens);
    }
}
