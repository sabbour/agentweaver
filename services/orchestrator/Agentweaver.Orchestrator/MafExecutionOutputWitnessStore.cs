using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Stores the root execution binding, canonical output set, owner evidence, and proof digest for a completed plan.
/// </summary>
internal sealed record MafExecutionOutputWitnessRecord(
    SessionIdentity RootIdentity,
    long ExecutionFence,
    string WorkPlanId,
    string CheckpointId,
    long CheckpointRevision,
    long DecisionStateVersion,
    string AcceptedSelectionHash,
    ImmutableArray<byte> OutputSetCanonicalBytes,
    string OutputSetSha256,
    JsonElement OwnerEvidenceJson,
    string ProofSha256);

/// <summary>
/// Appends and reads immutable output-witness proofs in the caller's checkpoint transaction.
/// Each proof is bound to the current completed checkpoint, plan, selection, decision, and execution fence.
/// </summary>
internal sealed partial class MafExecutionOutputWitnessStore
{
    private const short ContractVersion = 1;
    private const int MaximumOwnerEvidenceBytes = 1_048_576;
    private const string CheckpointStoreName = MafExecutionCheckpointContract.StoreName;
    private static readonly Regex SchemaPattern = new(
        "^[a-z][a-z0-9_]{0,62}\\z", RegexOptions.CultureInvariant);
    private readonly string _witnesses;

    /// <summary>
    /// Creates a witness store for a validated non-reserved PostgreSQL schema.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the schema is invalid or reserved.</exception>
    internal MafExecutionOutputWitnessStore(string schema)
    {
        if (schema is null || !SchemaPattern.IsMatch(schema) ||
            schema is "public" or "pg_catalog" or "information_schema" ||
            schema.StartsWith("pg_", StringComparison.Ordinal))
            throw new ArgumentException("A non-reserved lowercase schema identifier is required.", nameof(schema));
        _witnesses = $"\"{schema}\".maf_execution_output_witnesses";
    }

    /// <summary>
    /// Persists or reuses the output witness after validating the plan and locking the current checkpoint.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when the plan is incomplete, checkpoint is stale, or an existing witness conflicts.</exception>
    internal async Task<MafExecutionOutputWitnessRecord> AppendOutputWitnessInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgresMafCheckpointStore checkpointStore,
        MafCheckpointBinding checkpointBinding,
        SessionIdentity root,
        long executionFence,
        WorkPlanSnapshot plan,
        MafExecutionCheckpointSnapshot checkpoint,
        long decisionStateVersion,
        string acceptedSelectionHash,
        MafBacklogOutputSetSnapshot outputSet,
        JsonElement ownerEvidenceJson,
        CancellationToken cancellationToken,
        CoordinatorDecisionCurrentState? currentDecision = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        ArgumentNullException.ThrowIfNull(checkpointBinding);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ValidateBinding(root, executionFence, checkpoint, decisionStateVersion, acceptedSelectionHash, outputSet);
        ValidateCompletedPlan(
            root, executionFence, plan, checkpoint, decisionStateVersion, acceptedSelectionHash, outputSet,
            currentDecision);
        await RequireCurrentCheckpointInTransactionAsync(
            connection, transaction, checkpointStore, checkpointBinding, root, executionFence, checkpoint,
            requireCompletedRootEvidence: false, cancellationToken).ConfigureAwait(false);

        var outputBytes = MafExecutionOutputWitness.SerializeCanonicalOutputSet(outputSet);
        var evidenceBytes = SerializeCanonicalOwnerEvidence(ownerEvidenceJson);
        var proofSha256 = ComputeProofSha256(
            root,
            executionFence,
            checkpoint,
            decisionStateVersion,
            acceptedSelectionHash,
            outputSet.Digest,
            ownerEvidenceJson);
        await using (var command = new NpgsqlCommand($"""
            INSERT INTO {_witnesses} (
                project_id, run_id, root_session_id, execution_fence, work_plan_id,
                checkpoint_store_name, checkpoint_id, checkpoint_revision, decision_state_version,
                accepted_selection_hash, contract_version, output_set_canonical_bytes,
                output_set_sha256, owner_evidence_json, proof_sha256
            ) VALUES (
                @project, @run, @root, @fence, @workPlan,
                @storeName, @checkpoint, @checkpointRevision, @decisionVersion,
                @selectionHash, @contractVersion, @outputBytes,
                @outputHash, @ownerEvidence, @proofHash
            )
            ON CONFLICT (project_id, run_id, root_session_id, execution_fence, work_plan_id, checkpoint_id)
                DO NOTHING
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, root.ProjectId);
            command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, root.RunId);
            command.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, root.SessionId);
            command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, executionFence);
            command.Parameters.AddWithValue("workPlan", NpgsqlDbType.Varchar, checkpoint.State.WorkPlanId);
            command.Parameters.AddWithValue("storeName", NpgsqlDbType.Varchar, CheckpointStoreName);
            command.Parameters.AddWithValue("checkpoint", NpgsqlDbType.Varchar, checkpoint.Info.CheckpointId);
            command.Parameters.AddWithValue("checkpointRevision", NpgsqlDbType.Bigint, checkpoint.State.Revision);
            command.Parameters.AddWithValue("decisionVersion", NpgsqlDbType.Bigint, decisionStateVersion);
            command.Parameters.AddWithValue("selectionHash", NpgsqlDbType.Char, acceptedSelectionHash);
            command.Parameters.AddWithValue("contractVersion", NpgsqlDbType.Smallint, ContractVersion);
            command.Parameters.AddWithValue("outputBytes", NpgsqlDbType.Bytea, outputBytes);
            command.Parameters.AddWithValue("outputHash", NpgsqlDbType.Char, outputSet.Digest);
            command.Parameters.AddWithValue(
                "ownerEvidence", NpgsqlDbType.Jsonb, Encoding.UTF8.GetString(evidenceBytes));
            command.Parameters.AddWithValue("proofHash", NpgsqlDbType.Char, proofSha256);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var saved = await ReadByKeyInTransactionAsync(
            connection, transaction, root, executionFence, checkpoint, cancellationToken).ConfigureAwait(false);
        if (saved is null ||
            saved.CheckpointRevision != checkpoint.State.Revision ||
            saved.DecisionStateVersion != decisionStateVersion ||
            saved.AcceptedSelectionHash != acceptedSelectionHash ||
            saved.OutputSetSha256 != outputSet.Digest ||
            !saved.OutputSetCanonicalBytes.AsSpan().SequenceEqual(outputBytes) ||
            !SerializeCanonicalOwnerEvidence(saved.OwnerEvidenceJson).AsSpan().SequenceEqual(evidenceBytes) ||
            saved.ProofSha256 != proofSha256)
            throw new CoordinationException(
                "maf_execution_output_witness_conflict", StatusCodes.Status409Conflict);
        return saved;
    }

    /// <summary>
    /// Reads a witness only when its plan, output set, and completed-root checkpoint evidence are current.
    /// </summary>
    /// <returns>The matching witness, or <see langword="null"/> when none has been stored.</returns>
    /// <exception cref="CoordinationException">Thrown when the binding is stale or a stored witness does not match the expected output set.</exception>
    internal async Task<MafExecutionOutputWitnessRecord?> ReadOutputWitnessInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgresMafCheckpointStore checkpointStore,
        MafCheckpointBinding checkpointBinding,
        SessionIdentity root,
        long executionFence,
        WorkPlanSnapshot plan,
        MafExecutionCheckpointSnapshot checkpoint,
        long decisionStateVersion,
        string acceptedSelectionHash,
        MafBacklogOutputSetSnapshot outputSet,
        CancellationToken cancellationToken,
        CoordinatorDecisionCurrentState? currentDecision = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        ArgumentNullException.ThrowIfNull(checkpointBinding);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ValidateBinding(root, executionFence, checkpoint, decisionStateVersion, acceptedSelectionHash, outputSet);
        ValidateCompletedPlan(
            root, executionFence, plan, checkpoint, decisionStateVersion, acceptedSelectionHash, outputSet,
            currentDecision);
        await RequireCurrentCheckpointInTransactionAsync(
            connection, transaction, checkpointStore, checkpointBinding, root, executionFence, checkpoint,
            requireCompletedRootEvidence: true, cancellationToken).ConfigureAwait(false);

        var expectedBytes = MafExecutionOutputWitness.SerializeCanonicalOutputSet(outputSet);
        var witness = await ReadByKeyInTransactionAsync(
            connection, transaction, root, executionFence, checkpoint, cancellationToken).ConfigureAwait(false);
        if (witness is null)
            return null;
        if (witness.CheckpointRevision != checkpoint.State.Revision ||
            witness.DecisionStateVersion != decisionStateVersion ||
            witness.AcceptedSelectionHash != acceptedSelectionHash ||
            witness.OutputSetSha256 != outputSet.Digest ||
            !witness.OutputSetCanonicalBytes.AsSpan().SequenceEqual(expectedBytes))
            throw new CoordinationException(
                "maf_execution_output_witness_stale", StatusCodes.Status409Conflict);
        return witness;
    }

    /// <summary>
    /// Requires the supplied checkpoint to be the exact current checkpoint within the given owner transaction.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when root binding, checkpoint identity, revision, or serialized state is stale.</exception>
    private static async Task RequireCurrentCheckpointInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgresMafCheckpointStore checkpointStore,
        MafCheckpointBinding checkpointBinding,
        SessionIdentity root,
        long executionFence,
        MafExecutionCheckpointSnapshot checkpoint,
        bool requireCompletedRootEvidence,
        CancellationToken cancellationToken)
    {
        if (!MafExecutionCheckpointContract.IsBoundTo(checkpointBinding, root, executionFence))
            throw new CoordinationException(
                "maf_execution_output_witness_stale", StatusCodes.Status409Conflict);
        var latest = requireCompletedRootEvidence
            ? await checkpointStore.ReadLatestCheckpointForCompletedRootEvidenceInTransactionAsync(
                connection, transaction, checkpointBinding, cancellationToken).ConfigureAwait(false)
            : await checkpointStore.ReadLatestCheckpointInTransactionAsync(
                connection, transaction, checkpointBinding, cancellationToken).ConfigureAwait(false);
        if (latest is null ||
            !string.Equals(
                latest.Value.Info.SessionId, checkpoint.Info.SessionId, StringComparison.Ordinal) ||
            !string.Equals(
                latest.Value.Info.CheckpointId, checkpoint.Info.CheckpointId, StringComparison.Ordinal))
            throw new CoordinationException(
                "maf_execution_output_witness_stale", StatusCodes.Status409Conflict);
        var current = MafExecutionCheckpointContract.Deserialize(latest.Value.Value);
        var expectedBytes = SerializeCanonicalJson(MafExecutionCheckpointContract.Serialize(checkpoint.State));
        var currentBytes = SerializeCanonicalJson(MafExecutionCheckpointContract.Serialize(current));
        if (current.Revision != checkpoint.State.Revision ||
            current.DecisionStateVersion != checkpoint.State.DecisionStateVersion ||
            !expectedBytes.AsSpan().SequenceEqual(currentBytes))
            throw new CoordinationException(
                "maf_execution_output_witness_stale", StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Reads a witness by its root, fence, work-plan, and checkpoint key and verifies its stored digests.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when the stored witness, canonical bytes, or proof digest is invalid.</exception>
    private async Task<MafExecutionOutputWitnessRecord?> ReadByKeyInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity root,
        long executionFence,
        MafExecutionCheckpointSnapshot checkpoint,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT checkpoint_revision, decision_state_version, accepted_selection_hash,
                   contract_version, output_set_canonical_bytes, output_set_sha256,
                   owner_evidence_json::text, proof_sha256
            FROM {_witnesses}
            WHERE project_id = @project AND run_id = @run AND root_session_id = @root
              AND execution_fence = @fence AND work_plan_id = @workPlan
              AND checkpoint_id = @checkpoint
            FOR KEY SHARE
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, root.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, root.RunId);
        command.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, root.SessionId);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, executionFence);
        command.Parameters.AddWithValue("workPlan", NpgsqlDbType.Varchar, checkpoint.State.WorkPlanId);
        command.Parameters.AddWithValue("checkpoint", NpgsqlDbType.Varchar, checkpoint.Info.CheckpointId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var checkpointRevision = reader.GetInt64(0);
        var decisionStateVersion = reader.GetInt64(1);
        var acceptedSelectionHash = reader.GetString(2);
        var contractVersion = reader.GetInt16(3);
        var outputBytes = reader.GetFieldValue<byte[]>(4);
        var outputSetSha256 = reader.GetString(5);
        var evidenceJson = reader.GetString(6);
        var proofSha256 = reader.GetString(7);
        using var evidenceDocument = JsonDocument.Parse(evidenceJson, new JsonDocumentOptions { MaxDepth = 16 });
        var ownerEvidence = evidenceDocument.RootElement.Clone();
        var evidenceBytes = SerializeCanonicalOwnerEvidence(ownerEvidence);
        if (checkpointRevision < 1 || decisionStateVersion < 1 ||
            !IsSha256(acceptedSelectionHash) ||
            contractVersion != ContractVersion ||
            outputBytes.Length is < 1 or > 1_048_576 ||
            !IsLowerSha256(outputSetSha256) ||
            Convert.ToHexStringLower(SHA256.HashData(outputBytes)) != outputSetSha256 ||
            !IsLowerSha256(proofSha256) ||
            ComputeProofSha256(
                root,
                executionFence,
                checkpoint.State.WorkPlanId,
                checkpoint.Info.CheckpointId,
                checkpointRevision,
                decisionStateVersion,
                acceptedSelectionHash,
                outputSetSha256,
                ownerEvidence) != proofSha256)
            throw new CoordinationException(
                "maf_execution_output_witness_invalid", StatusCodes.Status409Conflict);

        return new MafExecutionOutputWitnessRecord(
            root,
            executionFence,
            checkpoint.State.WorkPlanId,
            checkpoint.Info.CheckpointId,
            checkpointRevision,
            decisionStateVersion,
            acceptedSelectionHash,
            ImmutableArray.CreateRange(outputBytes),
            outputSetSha256,
            ownerEvidence,
            proofSha256);
    }

    /// <summary>
    /// Validates the root, fence, checkpoint, decision, accepted selection hash, and output-set binding.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when any required identity, revision, or hash is invalid or stale.</exception>
    internal static void ValidateBinding(
        SessionIdentity root,
        long executionFence,
        MafExecutionCheckpointSnapshot checkpoint,
        long decisionStateVersion,
        string acceptedSelectionHash,
        MafBacklogOutputSetSnapshot outputSet)
    {
        ArgumentNullException.ThrowIfNull(outputSet);
        if (string.IsNullOrWhiteSpace(root.ProjectId) || root.ProjectId.Length > 256 ||
            root.ProjectId.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(root.RunId) || root.RunId.Length > 256 ||
            root.RunId.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(root.SessionId) || root.SessionId.Length > 256 ||
            root.SessionId.Any(char.IsControl) ||
            executionFence < 1 || decisionStateVersion < 1 ||
            checkpoint.Info.SessionId != root.SessionId ||
            checkpoint.State.Revision < 1 ||
            checkpoint.State.DecisionStateVersion != decisionStateVersion ||
            checkpoint.State.WorkPlanId != outputSet.WorkPlanId ||
            string.IsNullOrWhiteSpace(checkpoint.Info.CheckpointId) ||
            checkpoint.Info.CheckpointId.Length > 64 ||
            checkpoint.Info.CheckpointId.Any(char.IsControl) ||
            !IsSha256(acceptedSelectionHash))
            throw new CoordinationException(
                "maf_execution_output_witness_stale", StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Requires every planned work item, fixed association, and required platform step to have completed successfully.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when expected outputs differ or any plan work remains incomplete or mismatched.</exception>
    internal static void ValidateCompletedPlan(
        SessionIdentity root,
        long executionFence,
        WorkPlanSnapshot plan,
        MafExecutionCheckpointSnapshot checkpoint,
        long decisionStateVersion,
        string acceptedSelectionHash,
        MafBacklogOutputSetSnapshot outputSet,
        CoordinatorDecisionCurrentState? currentDecision = null)
    {
        var created = MafExecutionOutputWitness.CreateCompletePlanOutputSet(plan, checkpoint, root);
        if (!created.MissingFixedAssociationIds.IsEmpty ||
            !OutputSetsMatch(created.OutputSet, outputSet))
            throw new CoordinationException(
                "maf_execution_output_witness_plan_incomplete", StatusCodes.Status409Conflict);

        var state = checkpoint.State;
        // Completed actions may keep their original version only when the decision owner records review-only transitions.
        bool HasCurrentDecisionVersion(long recordedVersion) =>
            recordedVersion == decisionStateVersion ||
            currentDecision is not null &&
            currentDecision.StateVersion == decisionStateVersion &&
            currentDecision.SelectionHash == acceptedSelectionHash &&
            MafIndependentReviewContract.AllowsDecisionVersionAdvance(
                state, currentDecision, recordedVersion);
        if (state.PendingDispatches.Count != 0 ||
            state.Progress.WorkItems.Values.Any(status => status != MafExecutionTaskStatus.Succeeded) ||
            state.Progress.FixedWorkItems.Values.Any(status => status != MafExecutionTaskStatus.Succeeded) ||
            state.Progress.NonModelSteps.Values.Any(status => status != MafExecutionTaskStatus.Succeeded) ||
            plan.Plan.Items.Any(item =>
                state.Progress.WorkItems.GetValueOrDefault(item.Id) != MafExecutionTaskStatus.Succeeded ||
                !state.Results.ContainsKey(item.Id)) ||
            state.FixedWorkAssociations.Values.Any(association =>
                association.WorkPlanId != plan.Plan.Id ||
                association.AcceptedSelectionHash != acceptedSelectionHash ||
                association.ExecutionFence != executionFence ||
                !HasCurrentDecisionVersion(association.DecisionStateVersion) ||
                state.Progress.FixedWorkItems.GetValueOrDefault(association.AssociationId) !=
                    MafExecutionTaskStatus.Succeeded ||
                !state.Results.ContainsKey(association.AssociationId)) ||
             state.BuildTestIntents.Values.Any(intent =>
                intent.Identity != root || intent.WorkPlanId != plan.Plan.Id ||
                intent.AcceptedSelectionHash != acceptedSelectionHash ||
                intent.ExecutionFence != executionFence ||
                !HasCurrentDecisionVersion(intent.DecisionStateVersion) ||
                !MafBuildTestCommandContract.MatchesProvider(
                    plan.IsolationProviderBinding, intent.ExpectedBinding, root.RunId) ||
                !plan.Workflow.Definition.Steps.Any(step =>
                    step.Id == intent.StepId && MafBuildTestCommandContract.IsExecutable(step) &&
                    JsonElement.DeepEquals(
                        JsonSerializer.SerializeToElement(step.BuildTestCommand),
                        JsonSerializer.SerializeToElement(intent.Command)))) ||
            plan.Workflow.Definition.Steps.Any(step =>
                step.Mode == WorkflowStepMode.Platform &&
                step.Cardinality.Minimum > 0 &&
                state.Progress.NonModelSteps.GetValueOrDefault(step.Id) != MafExecutionTaskStatus.Succeeded))
            throw new CoordinationException(
                "maf_execution_output_witness_plan_incomplete", StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Compares output sets by work-plan ID, digest, and ordered obligations.
    /// </summary>
    private static bool OutputSetsMatch(
        MafBacklogOutputSetSnapshot expected,
        MafBacklogOutputSetSnapshot actual) =>
        expected.WorkPlanId == actual.WorkPlanId &&
        expected.Digest == actual.Digest &&
        expected.Obligations.SequenceEqual(actual.Obligations);

    /// <summary>
    /// Serializes owner evidence as a bounded canonical JSON array.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when evidence is not an array or exceeds its size limit.</exception>
    private static byte[] SerializeCanonicalOwnerEvidence(JsonElement ownerEvidence)
    {
        if (ownerEvidence.ValueKind != JsonValueKind.Array)
            throw new CoordinationException(
                "maf_execution_output_witness_evidence_invalid", StatusCodes.Status409Conflict);
        var bytes = SerializeCanonicalJson(ownerEvidence);
        if (bytes.Length is < 2 or > MaximumOwnerEvidenceBytes)
            throw new CoordinationException(
                "maf_execution_output_witness_evidence_invalid", StatusCodes.Status409Conflict);
        return bytes;
    }

    /// <summary>
    /// Serializes JSON with recursively sorted object properties and stable number representations.
    /// </summary>
    private static byte[] SerializeCanonicalJson(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonicalJson(writer, value);
        return stream.ToArray();
    }

    /// <summary>
    /// Computes the witness digest over its root, checkpoint, decision, selection, output, and owner evidence.
    /// </summary>
    private static string ComputeProofSha256(
        SessionIdentity root,
        long executionFence,
        MafExecutionCheckpointSnapshot checkpoint,
        long decisionStateVersion,
        string acceptedSelectionHash,
        string outputSetSha256,
        JsonElement ownerEvidence) =>
        ComputeProofSha256(
            root, executionFence, checkpoint.State.WorkPlanId, checkpoint.Info.CheckpointId,
            checkpoint.State.Revision, decisionStateVersion, acceptedSelectionHash,
            outputSetSha256, ownerEvidence);

    /// <summary>
    /// Computes the proof digest from its explicit immutable binding fields.
    /// </summary>
    private static string ComputeProofSha256(
        SessionIdentity root,
        long executionFence,
        string workPlanId,
        string checkpointId,
        long checkpointRevision,
        long decisionStateVersion,
        string acceptedSelectionHash,
        string outputSetSha256,
        JsonElement ownerEvidence)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", ContractVersion);
            writer.WriteString("projectId", root.ProjectId);
            writer.WriteString("runId", root.RunId);
            writer.WriteString("rootSessionId", root.SessionId);
            writer.WriteNumber("executionFence", executionFence);
            writer.WriteString("workPlanId", workPlanId);
            writer.WriteString("checkpointId", checkpointId);
            writer.WriteNumber("checkpointRevision", checkpointRevision);
            writer.WriteNumber("decisionStateVersion", decisionStateVersion);
            writer.WriteString("acceptedSelectionHash", acceptedSelectionHash);
            writer.WriteString("outputSetSha256", outputSetSha256);
            writer.WritePropertyName("ownerEvidence");
            WriteCanonicalJson(writer, ownerEvidence);
            writer.WriteEndObject();
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    /// <summary>
    /// Writes JSON recursively with ordinal property ordering and rejects duplicate or unsupported values.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when an object has duplicate names or a JSON number cannot be normalized.</exception>
    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
            {
                writer.WriteStartObject();
                var properties = value.EnumerateObject().ToArray();
                if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() !=
                    properties.Length)
                    throw new CoordinationException(
                        "maf_execution_output_witness_evidence_invalid", StatusCodes.Status409Conflict);
                foreach (var property in properties.OrderBy(
                    property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            }
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteCanonicalJson(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                if (value.TryGetInt64(out var integer))
                    writer.WriteNumberValue(integer);
                else if (value.TryGetDecimal(out var number))
                    writer.WriteNumberValue(number);
                else
                    throw new CoordinationException(
                        "maf_execution_output_witness_evidence_invalid", StatusCodes.Status409Conflict);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new CoordinationException(
                    "maf_execution_output_witness_evidence_invalid", StatusCodes.Status409Conflict);
        }
    }

    /// <summary>
    /// Returns whether a value is a 64-character hexadecimal SHA-256 digest.
    /// </summary>
    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    /// <summary>
    /// Returns whether a value is a lowercase 64-character hexadecimal SHA-256 digest.
    /// </summary>
    private static bool IsLowerSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');
}
