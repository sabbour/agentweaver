using Agentweaver.Api.Coordinator;

namespace Agentweaver.Tests.Helpers;

public sealed class FakeWorkflowSelectionModel : IWorkflowSelectionModel
{
    public Func<WorkflowSelectionContext, string?>? Override { get; set; }

    public Task<string?> CompleteAsync(
        string prompt,
        WorkflowSelectionContext context,
        CancellationToken ct) =>
        Task.FromResult(Override?.Invoke(context));
}
