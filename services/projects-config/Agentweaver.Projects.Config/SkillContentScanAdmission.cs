using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;

namespace Agentweaver.Projects.Config;

internal static class SkillContentScanAdmission
{
    private const string ScanUnavailableCode = "skill_scan_unavailable";
    private const string ScanBlockedCode = "skill_scan_blocked";

    internal static string StoredOwnerId(string projectId, string skillId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{projectId}\0{skillId}")))
            .ToLowerInvariant();

    internal static async Task<ValidatedSkillContent> ReadVerifiedContentAsync(
        SkillContentObjectStore? objects,
        ProjectSkillContentRevisionRecord revision,
        CancellationToken cancellationToken)
    {
        if (objects is null)
            throw UnavailableException();

        var verified = await objects.ReadVerifiedAsync(
            new ObjectKey(revision.ObjectKey),
            revision.ContentDigest,
            cancellationToken).ConfigureAwait(false);
        if (verified.Name != revision.Name ||
            verified.Description != revision.Description ||
            verified.Resources.Length != revision.ResourceCount ||
            SkillContentService.SemanticByteCount(verified) != revision.TotalBytes)
            throw new SkillContentObjectStoreException(
                SkillContentObjectStoreException.IntegrityErrorCode,
                "The immutable skill revision metadata does not match its stored content.");
        return verified;
    }

    internal static ImmutableArray<PromptContentScanInput> CreateInputs(
        ValidatedSkillContent content,
        string ownerId,
        long ownerRevision,
        PromptContentScanProvenance provenance = PromptContentScanProvenance.Authored,
        string? sourceRevision = null,
        string? resolvedCommitSha = null)
    {
        var source = new PromptContentScanSourceV1(
            PromptContentScanOwnerKind.SkillContent,
            ownerId,
            ownerRevision,
            "SKILL.md",
            provenance,
            sourceRevision,
            resolvedCommitSha);
        var inputs = ImmutableArray.CreateBuilder<PromptContentScanInput>(content.Resources.Length + 1);
        inputs.Add(new PromptContentScanInput(
            PromptContentScanInputKind.SkillMarkdown,
            source,
            SkillContentValidator.ToCanonicalMarkdown(content)));
        foreach (var resource in content.Resources)
        {
            inputs.Add(new PromptContentScanInput(
                PromptContentScanInputKind.SkillResource,
                source with { RelativePath = resource.RelativePath },
                resource.Content.ToArray()));
        }
        return inputs.ToImmutable();
    }

    internal static void Scan(
        ValidatedSkillContent content,
        string ownerId,
        long ownerRevision,
        TimeProvider timeProvider)
        => ScanReports(content, ownerId, ownerRevision, timeProvider);

    internal static ImmutableArray<PromptContentScanReportV1> ScanReports(
        ValidatedSkillContent content,
        string ownerId,
        long ownerRevision,
        TimeProvider timeProvider,
        PromptContentScanProvenance provenance = PromptContentScanProvenance.Authored,
        string? sourceRevision = null,
        string? resolvedCommitSha = null)
    {
        var inputs = CreateInputs(
            content, ownerId, ownerRevision, provenance, sourceRevision, resolvedCommitSha);
        var scanStartedAt = timeProvider.GetUtcNow();
        ImmutableArray<PromptContentScanReportV1> reports;
        try
        {
            reports = PromptContentScanner.ScanSet(inputs);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw UnavailableException();
        }

        RequirePassingReports(inputs, reports, scanStartedAt, timeProvider.GetUtcNow());
        return reports;
    }

    internal static void RequirePassingReports(
        ImmutableArray<PromptContentScanInput> inputs,
        ImmutableArray<PromptContentScanReportV1> reports,
        DateTimeOffset scanStartedAt,
        DateTimeOffset scanCompletedAt)
    {
        if (inputs.IsDefaultOrEmpty || reports.IsDefault || reports.Length != inputs.Length)
            throw UnavailableException();

        var blocked = false;
        for (var index = 0; index < inputs.Length; index++)
        {
            PromptContentScanReportV1 report;
            try
            {
                var input = inputs[index];
                report = PromptContentScanContract.ValidateReport(
                    reports[index], input.InputKind, input.Source, input.ExactInput.Span);
            }
            catch (ArgumentException)
            {
                throw UnavailableException();
            }

            if (report.EvaluatedAt < scanStartedAt || report.EvaluatedAt > scanCompletedAt ||
                report.Status != PromptContentScanStatus.Completed)
                throw UnavailableException();

            blocked |= report.MeetsRecommendedThreshold != true;
        }

        if (blocked)
            throw new SkillContentServiceException(
                ScanBlockedCode,
                "The skill content did not pass the required scan.",
                StatusCodes.Status422UnprocessableEntity);
    }

    internal static SkillContentServiceException UnavailableException() =>
        new(
            ScanUnavailableCode,
            "The skill content scan did not produce a current, valid report.",
            StatusCodes.Status503ServiceUnavailable);
}
