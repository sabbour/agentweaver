using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using AgentGovernance.Security;
using Agentweaver.Abstractions;

namespace Agentweaver.Projects.Config;

/// <summary>One owner-provided item in a bounded prompt scan set.</summary>
public sealed record PromptContentScanInput(
    PromptContentScanInputKind InputKind,
    PromptContentScanSourceV1 Source,
    ReadOnlyMemory<byte> ExactInput);

/// <summary>Runs the pinned local AGT evaluator over one bounded, owner-provided UTF-8 input.</summary>
public static class PromptContentScanner
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly PromptDefenseEvaluator Evaluator = new();

    private sealed record ValidatedInput(
        PromptContentScanInputKind InputKind,
        PromptContentScanSourceV1 Source,
        byte[] Bytes,
        string Text,
        string Sha256);

    public static PromptContentScanReportV1 Scan(
        PromptContentScanInputKind inputKind,
        PromptContentScanSourceV1 source,
        ReadOnlyMemory<byte> exactInput) =>
        Scan(inputKind, source, exactInput, Evaluator.Evaluate);

    public static ImmutableArray<PromptContentScanReportV1> ScanSet(
        ImmutableArray<PromptContentScanInput> inputs) =>
        ScanSet(inputs, Evaluator.Evaluate);

    internal static PromptContentScanReportV1 Scan(
        PromptContentScanInputKind inputKind,
        PromptContentScanSourceV1 source,
        ReadOnlyMemory<byte> exactInput,
        Func<string, PromptDefenseReport> evaluate) =>
        ScanSet(
            ImmutableArray.Create(new PromptContentScanInput(inputKind, source, exactInput)),
            evaluate)[0];

    internal static ImmutableArray<PromptContentScanReportV1> ScanSet(
        ImmutableArray<PromptContentScanInput> inputs,
        Func<string, PromptDefenseReport> evaluate)
    {
        ArgumentNullException.ThrowIfNull(evaluate);
        ValidateScanSet(inputs);
        var validatedInputs = inputs.Select(ValidateInput).ToImmutableArray();
        return validatedInputs
            .Select(input => ScanOne(input, evaluate))
            .ToImmutableArray();
    }

    private static void ValidateScanSet(ImmutableArray<PromptContentScanInput> inputs)
    {
        if (inputs.IsDefaultOrEmpty)
            throw new ArgumentException("A prompt scan set must contain an input.", nameof(inputs));

        var first = inputs[0] ?? throw new ArgumentException("A prompt scan input is required.", nameof(inputs));
        PromptContentScanContract.ValidateSource(first.InputKind, first.Source);
        var isCharterSet = first.InputKind == PromptContentScanInputKind.ProjectCharter;
        if ((isCharterSet && inputs.Length != 1) ||
            (!isCharterSet && inputs.Length > PromptContentScanContract.MaxSkillScanSetResources + 2))
            throw new ArgumentOutOfRangeException(nameof(inputs), "Prompt scan set exceeds its item limit.");

        var paths = new HashSet<string>(StringComparer.Ordinal);
        var resourceCount = 0;
        long totalBytes = 0;
        long resourceBytes = 0;
        var markdownCount = 0;
        var instructionsCount = 0;
        foreach (var input in inputs)
        {
            ArgumentNullException.ThrowIfNull(input);
            PromptContentScanContract.ValidateSource(input.InputKind, input.Source);
            if (isCharterSet != (input.InputKind == PromptContentScanInputKind.ProjectCharter) ||
                !SameOwner(first.Source, input.Source) ||
                !paths.Add(input.Source.RelativePath))
                throw new ArgumentException("Prompt scan inputs must share one owner and use unique paths.", nameof(inputs));

            var maximumBytes = PromptContentScanContract.MaximumInputBytes(input.InputKind);
            if (input.ExactInput.Length > maximumBytes)
                throw new ArgumentOutOfRangeException(nameof(inputs), "Prompt scan input exceeds its owner limit.");

            totalBytes += input.ExactInput.Length;
            if (!isCharterSet && totalBytes > PromptContentScanContract.MaxSkillScanSetBytes)
                throw new ArgumentOutOfRangeException(nameof(inputs), "Skill scan set exceeds its aggregate size limit.");

            if ((input.InputKind == PromptContentScanInputKind.SkillMarkdown && ++markdownCount > 1) ||
                (input.InputKind == PromptContentScanInputKind.SkillInstructions && ++instructionsCount > 1))
                throw new ArgumentException("A skill scan set can contain one markdown file and one instruction body.", nameof(inputs));

            if (input.InputKind == PromptContentScanInputKind.SkillResource)
            {
                resourceCount++;
                resourceBytes += input.ExactInput.Length;
                if (resourceCount > PromptContentScanContract.MaxSkillScanSetResources ||
                    resourceBytes > PromptContentScanContract.MaxSkillScanSetResourceBytes)
                    throw new ArgumentOutOfRangeException(nameof(inputs), "Skill scan set exceeds its resource limits.");
            }
        }
    }

    private static bool SameOwner(PromptContentScanSourceV1 left, PromptContentScanSourceV1 right) =>
        left.OwnerKind == right.OwnerKind &&
        left.OwnerId == right.OwnerId &&
        left.OwnerRevision == right.OwnerRevision &&
        left.Provenance == right.Provenance &&
        left.SourceRevision == right.SourceRevision &&
        left.ResolvedCommitSha == right.ResolvedCommitSha;

    private static ValidatedInput ValidateInput(PromptContentScanInput input)
    {
        var snapshot = input.ExactInput.ToArray();
        string text;
        try
        {
            text = StrictUtf8.GetString(snapshot);
        }
        catch (DecoderFallbackException error)
        {
            throw new ArgumentException("Prompt scan input must be valid UTF-8.", nameof(input), error);
        }

        if (input.InputKind == PromptContentScanInputKind.ProjectCharter &&
            text.Length > PromptContentScanContract.MaxProjectCharterCharacters)
            throw new ArgumentOutOfRangeException(nameof(input), "Project charter exceeds its owner limit.");

        return new ValidatedInput(
            input.InputKind,
            input.Source,
            snapshot,
            text,
            PromptContentScanContract.ComputeInputSha256(snapshot));
    }

    private static PromptContentScanReportV1 ScanOne(
        ValidatedInput input,
        Func<string, PromptDefenseReport> evaluate)
    {
        PromptDefenseReport result;
        try
        {
            result = evaluate(input.Text);
        }
        catch (RegexMatchTimeoutException)
        {
            return ErrorReport(input.InputKind, input.Source, input.Bytes, "evaluator_timeout");
        }

        if (!string.Equals(result.PromptHash, input.Sha256, StringComparison.Ordinal))
            return ErrorReport(input.InputKind, input.Source, input.Bytes, "input_hash_mismatch");
        if (result.VectorCount != PromptContentScanContract.ExpectedVectorCount ||
            result.Results is null ||
            result.Results.Count != PromptContentScanContract.ExpectedVectorCount)
            return ErrorReport(input.InputKind, input.Source, input.Bytes, "unsupported_vector_set");

        var vectorIds = new HashSet<string>(StringComparer.Ordinal);
        if (result.Results.Any(vector =>
                vector is null ||
                !PromptContentScanContract.IsKnownVector(vector.VectorId, vector.OwaspCategory) ||
                !vectorIds.Add(vector.VectorId)) ||
            vectorIds.Count != PromptContentScanContract.ExpectedVectorCount)
            return ErrorReport(input.InputKind, input.Source, input.Bytes, "unsupported_vector_set");

        var report = new PromptContentScanReportV1(
            PromptContentScanContract.CurrentVersion,
            input.InputKind,
            input.Source,
            input.Sha256,
            input.Bytes.Length,
            PromptContentScanContract.ScannerPackage,
            PromptContentScanContract.ScannerPackageVersion,
            PromptContentScanContract.VectorSetRevision,
            PromptContentScanContract.PolicyRevision,
            new DateTimeOffset(result.EvaluatedAt.ToUniversalTime()),
            PromptContentScanStatus.Completed,
            result.Score,
            result.Grade,
            result.CoveredCount,
            result.VectorCount,
            result.Passes,
            result.Results
                .Where(vector => !vector.Covered)
                .Select(vector => new PromptContentScanFindingV1(vector.VectorId, vector.OwaspCategory))
                .ToImmutableArray(),
            null);
        return PromptContentScanContract.ValidateReport(
            report, input.InputKind, input.Source, input.Bytes);
    }

    private static PromptContentScanReportV1 ErrorReport(
        PromptContentScanInputKind inputKind,
        PromptContentScanSourceV1 source,
        byte[] exactInput,
        string errorCode)
    {
        var report = new PromptContentScanReportV1(
            PromptContentScanContract.CurrentVersion,
            inputKind,
            source,
            PromptContentScanContract.ComputeInputSha256(exactInput),
            exactInput.Length,
            PromptContentScanContract.ScannerPackage,
            PromptContentScanContract.ScannerPackageVersion,
            PromptContentScanContract.VectorSetRevision,
            PromptContentScanContract.PolicyRevision,
            DateTimeOffset.UtcNow,
            PromptContentScanStatus.EvaluatorError,
            null,
            null,
            null,
            null,
            null,
            [],
            errorCode);
        return PromptContentScanContract.ValidateReport(
            report, inputKind, source, exactInput);
    }
}
