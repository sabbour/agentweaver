using Agentweaver.Api.Coordinator;

namespace Agentweaver.Tests.Helpers;

public sealed class FakeStoryIndependenceClassifier : IStoryIndependenceClassifier
{
    public StoryIndependenceClassificationContext? LastContext { get; private set; }

    public int CallCount { get; private set; }

    public Func<StoryIndependenceClassificationContext, StoryIndependenceClassificationResult?>? Override { get; set; }

    public Task<StoryIndependenceClassificationResult?> ClassifyAsync(
        StoryIndependenceClassificationContext context,
        CancellationToken ct)
    {
        LastContext = context;
        CallCount++;

        if (Override is not null)
            return Task.FromResult(Override(context));

        return Task.FromResult<StoryIndependenceClassificationResult?>(
            new(false, "Default test classifier keeps components inline."));
    }
}
