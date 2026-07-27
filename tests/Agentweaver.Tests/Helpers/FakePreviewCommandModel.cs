using Agentweaver.Api.Sandbox.Preview;

namespace Agentweaver.Tests.Helpers;

/// <summary>
/// Test double for <see cref="IPreviewCommandModel"/>. Records calls and returns a caller-supplied
/// proposal (or <see langword="null"/> to simulate the model declining/being unavailable) WITHOUT
/// making a real model call — the LLM preview fallback must be exercised deterministically in unit
/// tests.
/// </summary>
public sealed class FakePreviewCommandModel : IPreviewCommandModel
{
    public int CallCount { get; private set; }
    public PreviewCommandModelContext? LastContext { get; private set; }
    public Func<PreviewCommandModelContext, PreviewCommandProposal?>? Override { get; set; }
    public Exception? Exception { get; set; }

    public Task<PreviewCommandProposal?> ProposeCommandAsync(PreviewCommandModelContext context, CancellationToken ct)
    {
        CallCount++;
        LastContext = context;
        if (Exception is not null)
            return Task.FromException<PreviewCommandProposal?>(Exception);
        return Task.FromResult(Override?.Invoke(context));
    }
}
