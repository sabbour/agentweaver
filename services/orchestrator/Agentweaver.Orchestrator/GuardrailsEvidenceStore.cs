using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Orchestrator.Core;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Trusted Orchestrator writer for Guardrails evaluation evidence. Each operation has one immutable row.
/// </summary>
/// <remarks>
/// A replay of identical evidence for the same operation returns <see cref="GuardrailsJournalAppendStatus.Appended"/>,
/// so a runtime retry after response loss reaches the same decision. Different evidence for an existing operation
/// returns <see cref="GuardrailsJournalAppendStatus.Duplicate"/>, and the gate withholds content.
/// Transient classifier failures aren't recorded, so the gate still withholds the content and a retry can reach
/// the final decision. Database failures return <see cref="GuardrailsJournalAppendStatus.Unavailable"/>.
/// </remarks>
internal sealed class GuardrailsEvidenceStore : IGuardrailsEvidenceJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _table;

    /// <summary>Creates the store using a validated non-reserved PostgreSQL schema.</summary>
    public GuardrailsEvidenceStore(NpgsqlDataSource dataSource, string schema)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        if (schema is null ||
            !System.Text.RegularExpressions.Regex.IsMatch(
                schema, "^[a-z][a-z0-9_]{0,62}\\z",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            schema is "public" or "pg_catalog" or "information_schema" ||
            schema.StartsWith("pg_", StringComparison.Ordinal))
            throw new ArgumentException("A non-reserved lowercase schema identifier is required.", nameof(schema));
        _dataSource = dataSource;
        _table = $"\"{schema}\".guardrails_evaluations";
    }

    /// <summary>Inserts the evidence row, or compares it with the row already recorded for the operation.</summary>
    public async Task<GuardrailsJournalAppendStatus> AppendAsync(
        GuardrailsEvaluationEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        // A transient classifier failure isn't the operation's decision. Recording it would make the retry that
        // the 503 response invites fail as a duplicate, so only the final decision is recorded.
        if (evidence.ReasonCode is GuardrailsReasonCode.ProviderUnavailable or GuardrailsReasonCode.ClassifierTimedOut)
            return GuardrailsJournalAppendStatus.Appended;
        var document = JsonSerializer.Serialize(evidence, JsonOptions);
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand($"""
                WITH inserted AS (
                    INSERT INTO {_table}
                        (operation_id, project_id, run_id, session_id, action_id, grant_id, grant_revision,
                         execution_fence, stage, content_class, content_digest, reason_code, evidence_json)
                    VALUES
                        (@operation, @project, @run, @session, @action, @grant, @grantRevision,
                         @fence, @stage, @contentClass, @digest, @reason, @evidence)
                    ON CONFLICT (operation_id) DO NOTHING
                    RETURNING true AS appended
                )
                SELECT
                    EXISTS (SELECT 1 FROM inserted),
                    (SELECT evidence_json = @evidence FROM {_table} WHERE operation_id = @operation)
                """, connection);
            command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, evidence.OperationId);
            command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, evidence.ProjectId);
            command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, evidence.RunId);
            command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, evidence.SessionId);
            command.Parameters.AddWithValue("action", NpgsqlDbType.Varchar, evidence.ActionId);
            command.Parameters.AddWithValue("grant", NpgsqlDbType.Varchar, evidence.GrantId);
            command.Parameters.AddWithValue("grantRevision", NpgsqlDbType.Varchar, evidence.GrantRevision);
            command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, evidence.ExecutionFence);
            command.Parameters.AddWithValue("stage", NpgsqlDbType.Varchar, evidence.Stage.ToString());
            command.Parameters.AddWithValue("contentClass", NpgsqlDbType.Varchar, evidence.ContentClass.ToString());
            command.Parameters.AddWithValue("digest", NpgsqlDbType.Char, evidence.ContentDigest);
            command.Parameters.AddWithValue("reason", NpgsqlDbType.Varchar, evidence.ReasonCode.ToString());
            command.Parameters.AddWithValue("evidence", NpgsqlDbType.Jsonb, document);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return GuardrailsJournalAppendStatus.Unavailable;
            if (reader.GetBoolean(0))
                return GuardrailsJournalAppendStatus.Appended;
            // The CTE snapshot can't see a row inserted concurrently by another statement; treat that as unavailable.
            if (reader.IsDBNull(1))
                return GuardrailsJournalAppendStatus.Unavailable;
            return reader.GetBoolean(1)
                ? GuardrailsJournalAppendStatus.Appended
                : GuardrailsJournalAppendStatus.Duplicate;
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            return GuardrailsJournalAppendStatus.Unavailable;
        }
    }
}
