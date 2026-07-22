using Agentweaver.Api.Coordinator;

namespace Agentweaver.Tests.Helpers;

public sealed class FakePreviewClassifier : IPreviewClassifier
{
    public PreviewApplicabilityClassificationContext? LastApplicabilityContext { get; private set; }
    public PreviewFeedbackClassificationContext? LastFeedbackContext { get; private set; }
    public int ApplicabilityCallCount { get; private set; }
    public int FeedbackCallCount { get; private set; }
    public Func<PreviewApplicabilityClassificationContext, bool?>? ApplicabilityOverride { get; set; }
    public Func<PreviewFeedbackClassificationContext, bool?>? FeedbackOverride { get; set; }
    public Exception? ApplicabilityException { get; set; }
    public Exception? FeedbackException { get; set; }

    public Task<bool?> ClassifyApplicabilityAsync(PreviewApplicabilityClassificationContext context, CancellationToken ct)
    {
        LastApplicabilityContext = context;
        ApplicabilityCallCount++;
        if (ApplicabilityException is not null) return Task.FromException<bool?>(ApplicabilityException);
        return Task.FromResult<bool?>(ApplicabilityOverride?.Invoke(context) ?? true);
    }

    public Task<bool?> ClassifyPreviewOnlyFeedbackAsync(PreviewFeedbackClassificationContext context, CancellationToken ct)
    {
        LastFeedbackContext = context;
        FeedbackCallCount++;
        if (FeedbackException is not null) return Task.FromException<bool?>(FeedbackException);
        return Task.FromResult<bool?>(FeedbackOverride?.Invoke(context) ?? false);
    }
}