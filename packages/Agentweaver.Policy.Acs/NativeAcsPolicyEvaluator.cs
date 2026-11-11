using System.Text.Json;
using System.Text.Json.Serialization;
using AgentControlSpecification;
using Agentweaver.Abstractions;

namespace Agentweaver.Policy.Acs;

public sealed class NativeAcsPolicyEvaluator
{
    private readonly NativeAcsArtifactValidator _validator;
    private readonly string _workRoot;

    public NativeAcsPolicyEvaluator(NativeAcsArtifactValidator validator, string workRoot)
    {
        ArgumentNullException.ThrowIfNull(validator);
        if (string.IsNullOrWhiteSpace(workRoot) || !Path.IsPathFullyQualified(workRoot))
            throw new ArgumentException("A full caller-owned work root is required.", nameof(workRoot));

        _validator = validator;
        _workRoot = Path.GetFullPath(workRoot);
    }

    public async Task<NativeAcsEvaluationResult> EvaluateAsync(
        NativeAcsArtifact artifact,
        InterventionPoint interventionPoint,
        NativeAcsEvaluationSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (cancellationToken.IsCancellationRequested)
            return Error("unavailable", "evaluation_cancelled");

        var validation = _validator.Validate(artifact);
        if (!validation.IsValid)
            return Error("unavailable", validation.Diagnostics.FirstOrDefault()?.Code ?? "artifact_invalid");

        var artifactHash = artifact.ContentSha256;

        if (!IsValidSnapshot(snapshot))
            return Error(artifactHash, "snapshot_invalid");

        if (!Enum.IsDefined(interventionPoint))
            return Error(artifactHash, "intervention_point_invalid");

        var configuredOpaPath = Environment.GetEnvironmentVariable("ACS_OPA_PATH");
        if (!IsSamePath(configuredOpaPath, _validator.OpaPath))
            return Error(artifactHash, "opa_path_mismatch");

        var workDirectory = Path.Combine(_workRoot, Guid.NewGuid().ToString("N"));
        var cleanupFailed = false;
        var evaluation = Error(artifactHash, "native_evaluation_failed");
        try
        {
            Directory.CreateDirectory(workDirectory);
            WriteArtifact(workDirectory, artifact);

            var manifestPath = Path.Combine(workDirectory, "manifest.yaml");
            var control = AgentControl.FromPath(manifestPath);
            using var snapshotDocument = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(CreateSnapshot(snapshot)));
            var result = await control.EvaluateInterventionPointAsync(
                interventionPoint,
                snapshotDocument.RootElement,
                EnforcementMode.Enforce,
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (result.Verdict.Decision == Decision.Allow)
                evaluation = new NativeAcsEvaluationResult(
                    PolicyEvaluationOutcome.Allow,
                    PolicyEvaluationReasonCode.Allowed,
                    artifactHash);
            else if (result.Verdict.Decision == Decision.Deny)
                evaluation = cancellationToken.IsCancellationRequested
                    ? Error(artifactHash, "evaluation_cancelled")
                    : result.Verdict.Reason?.StartsWith("runtime_error:", StringComparison.Ordinal) == true
                        ? Error(artifactHash, "native_policy_runtime_error")
                        : new NativeAcsEvaluationResult(
                            PolicyEvaluationOutcome.Deny,
                            PolicyEvaluationReasonCode.DefaultDeny,
                            artifactHash);
            else
                evaluation = Error(artifactHash, "unsupported_verdict");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            evaluation = Error(
                artifactHash,
                exception is OperationCanceledException
                    ? cancellationToken.IsCancellationRequested ? "evaluation_cancelled" : "evaluation_timeout"
                    : "native_evaluation_failed");
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDirectory))
                    Directory.Delete(workDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                cleanupFailed = true;
            }
        }

        return cleanupFailed ? Error(artifactHash, "artifact_cleanup_failed") : evaluation;
    }

    private static bool IsValidSnapshot(NativeAcsEvaluationSnapshot snapshot)
    {
        if (!IsIdentifier(snapshot.Identity.ProjectId) ||
            !IsIdentifier(snapshot.Identity.RunId) ||
            !IsIdentifier(snapshot.Identity.SessionId) ||
            !IsIdentifier(snapshot.TenantId) ||
            !IsIdentifier(snapshot.AgentId) ||
            !IsIdentifier(snapshot.ActionId))
            return false;

        return snapshot.VerifiedHumanIdentity is not { } human ||
            (IsIdentifier(human.TenantId) &&
             IsIdentifier(human.ObjectId) &&
             string.Equals(human.TenantId, snapshot.TenantId, StringComparison.Ordinal));
    }

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    private static NativeAcsSnapshotDocument CreateSnapshot(NativeAcsEvaluationSnapshot snapshot)
    {
        var verifiedHuman = snapshot.VerifiedHumanIdentity;
        return new NativeAcsSnapshotDocument(
            new NativeAcsModelRequest(
                snapshot.TenantId,
                snapshot.Identity.ProjectId,
                snapshot.Identity.RunId,
                snapshot.Identity.SessionId,
                snapshot.AgentId,
                snapshot.ActionId,
                verifiedHuman is not null,
                verifiedHuman?.ObjectId,
                verifiedHuman?.TenantId));
    }

    private static void WriteArtifact(string workDirectory, NativeAcsArtifact artifact)
    {
        File.WriteAllBytes(Path.Combine(workDirectory, "manifest.yaml"), artifact.ManifestBytes.ToArray());
        foreach (var file in artifact.Files)
        {
            var destination = Path.Combine(workDirectory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(file.Bytes.AsSpan());
        }
    }

    private static bool IsSamePath(string? left, string right) =>
        !string.IsNullOrWhiteSpace(left) &&
        Path.IsPathFullyQualified(left) &&
        string.Equals(
            Path.GetFullPath(left),
            right,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static NativeAcsEvaluationResult Error(string artifactHash, string code) =>
        new(
            PolicyEvaluationOutcome.Error,
            PolicyEvaluationReasonCode.EvaluationFailed,
            artifactHash,
            code);

    private sealed record NativeAcsSnapshotDocument(
        [property: JsonPropertyName("model_request")] NativeAcsModelRequest ModelRequest);

    private sealed record NativeAcsModelRequest(
        [property: JsonPropertyName("tenant_id")] string TenantId,
        [property: JsonPropertyName("project_id")] string ProjectId,
        [property: JsonPropertyName("run_id")] string RunId,
        [property: JsonPropertyName("session_id")] string SessionId,
        [property: JsonPropertyName("agent_id")] string AgentId,
        [property: JsonPropertyName("action_id")] string ActionId,
        [property: JsonPropertyName("human_available")] bool HumanAvailable,
        [property: JsonPropertyName("human_oid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? HumanObjectId,
        [property: JsonPropertyName("human_tenant_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? HumanTenantId);
}
