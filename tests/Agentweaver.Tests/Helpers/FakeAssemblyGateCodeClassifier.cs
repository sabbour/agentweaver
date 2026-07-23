using Agentweaver.Api.Coordinator;

namespace Agentweaver.Tests.Helpers;

public sealed class FakeAssemblyGateCodeClassifier : IAssemblyGateCodeClassifier
{
    public AssemblyGateCodeClassificationContext? LastContext { get; private set; }
    public int CallCount { get; private set; }
    public Func<AssemblyGateCodeClassificationContext, bool?>? Override { get; set; }

    public Task<bool?> ClassifyAsync(
        AssemblyGateCodeClassificationContext context,
        CancellationToken ct)
    {
        LastContext = context;
        CallCount++;
        return Task.FromResult<bool?>(Override?.Invoke(context) ?? true);
    }
}
