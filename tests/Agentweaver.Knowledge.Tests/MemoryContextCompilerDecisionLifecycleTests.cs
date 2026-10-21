using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Knowledge;
using Xunit;

namespace Agentweaver.Knowledge.Tests;

public sealed class MemoryContextCompilerDecisionLifecycleTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RetiredAndUnapprovedDecisionsAreExcludedFromCurrentContext()
    {
        var current = Decision("current", KnowledgeRecordState.Active, KnowledgeTrustState.Approved);
        var replacement = Decision("replacement", KnowledgeRecordState.Active, KnowledgeTrustState.Approved);
        var superseded = Decision("superseded", KnowledgeRecordState.Superseded, KnowledgeTrustState.Approved)
            with { SupersededByRecordId = replacement.RecordId };
        var archived = Decision("archived", KnowledgeRecordState.Archived, KnowledgeTrustState.Approved);
        var corrected = Decision("corrected", KnowledgeRecordState.Active, KnowledgeTrustState.Pending);

        var compilation = Compile(current, replacement, superseded, archived, corrected);

        Assert.NotNull(compilation);
        Assert.Equal(
            [current.RecordId, replacement.RecordId],
            compilation.RevisionReferences.Select(reference => reference.RecordId));
        Assert.DoesNotContain("superseded", compilation.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("archived", compilation.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("corrected", compilation.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CorrectionProducesNewReferenceWithoutChangingAnEarlierCompilation()
    {
        var accepted = Decision("accepted wording", KnowledgeRecordState.Active, KnowledgeTrustState.Approved);
        var earlier = Compile(accepted)!;

        var corrected = accepted with
        {
            Content = "corrected wording",
            Revision = 3,
            PreviousRevisionId = Guid.NewGuid(),
            RevisionId = Guid.NewGuid()
        };
        var later = Compile(corrected)!;

        var earlierReference = Assert.Single(earlier.RevisionReferences);
        var laterReference = Assert.Single(later.RevisionReferences);
        Assert.Equal(accepted.RecordId, laterReference.RecordId);
        Assert.Equal((1, accepted.RevisionId), (earlierReference.Revision, earlierReference.RevisionId));
        Assert.Equal((3, corrected.RevisionId), (laterReference.Revision, laterReference.RevisionId));
        Assert.Contains("accepted wording", earlier.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("corrected wording", earlier.Text, StringComparison.Ordinal);
        Assert.Contains("corrected wording", later.Text, StringComparison.Ordinal);
    }

    private static MemoryContextCompilation? Compile(params KnowledgeRecord[] records) =>
        new MemoryContextCompiler().Compile(
            new KnowledgeContextCandidates([.. records]),
            "project-a",
            "agent-a",
            "run-b",
            relevanceText: null,
            maximumItems: 10,
            maximumTokens: 4_000);

    private static int _sequence;

    private static KnowledgeRecord Decision(
        string content,
        KnowledgeRecordState state,
        KnowledgeTrustState trustState)
    {
        var order = Interlocked.Increment(ref _sequence);
        return new KnowledgeRecord(
            Guid.NewGuid(),
            "project-a",
            "agent-a",
            KnowledgeRecordKind.Decision,
            "architectural",
            "decision",
            content,
            null,
            "medium",
            ImmutableArray.Create("lifecycle"),
            state,
            trustState,
            1,
            Guid.NewGuid(),
            null,
            "run-a",
            null,
            null,
            Created.AddMinutes(order),
            Created.AddMinutes(order));
    }
}
