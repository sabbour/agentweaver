using Microsoft.Extensions.AI;
using Agentweaver.AgentTools;

namespace Agentweaver.AgentRuntime;

/// <summary>
/// Wraps an <see cref="AIFunction"/> and injects
/// <see cref="CopilotTool.OverridesBuiltInToolKey"/> into <see cref="AITool.AdditionalProperties"/>
/// so the Copilot SDK accepts tools whose names match a native built-in.
/// </summary>
internal sealed class CopilotOverrideAIFunction(AIFunction inner) : AIFunction
{
    // The key expected by the Copilot SDK in AITool.AdditionalProperties.
    // CopilotTool.OverridesBuiltInToolKey is internal in the SDK package;
    // the string value is confirmed by the SDK's own error message:
    // "Set overridesBuiltInTool: true to explicitly override it."
    private const string OverridesBuiltInToolKey = "overridesBuiltInTool";

    private readonly IReadOnlyDictionary<string, object?> _additionalProperties =
        new Dictionary<string, object?>(inner.AdditionalProperties)
        {
            [OverridesBuiltInToolKey] = true,
        };

    public override string Name => inner.Name;
    public override string Description => inner.Description;
    public override IReadOnlyDictionary<string, object?> AdditionalProperties => _additionalProperties;

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        inner.InvokeAsync(arguments, cancellationToken);
}
