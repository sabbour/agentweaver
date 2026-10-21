using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Npgsql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

[Collection("Coordination PostgreSQL")]
public sealed class GuardrailsEvidenceStorePostgresTests(CoordinationPostgresFixture fixture) : IAsyncLifetime
{
    private readonly string _schema = "coordination_" + Guid.NewGuid().ToString("N");
    private GuardrailsEvidenceStore _store = null!;

    public async Task InitializeAsync()
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        _store = new GuardrailsEvidenceStore(fixture.DataSource, _schema);
    }

    public async Task DisposeAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task AppendsOnceAndAcceptsAnIdenticalReplay()
    {
        var evidence = Evidence(Guid.NewGuid());

        Assert.Equal(GuardrailsJournalAppendStatus.Appended, await _store.AppendAsync(evidence, default));
        Assert.Equal(GuardrailsJournalAppendStatus.Appended, await _store.AppendAsync(evidence, default));
        Assert.Equal(1L, await ScalarAsync<long>(
            $"SELECT count(*) FROM \"{_schema}\".guardrails_evaluations WHERE operation_id = '{evidence.OperationId}'"));
    }

    [Fact]
    public async Task DifferentEvidenceForTheSameOperationIsDuplicate()
    {
        var evidence = Evidence(Guid.NewGuid());
        await _store.AppendAsync(evidence, default);

        var status = await _store.AppendAsync(
            evidence with { ReasonCode = GuardrailsReasonCode.PolicyDenied }, default);

        Assert.Equal(GuardrailsJournalAppendStatus.Duplicate, status);
    }

    [Fact]
    public async Task TransientClassifierFailureIsNotRecordedSoARetryCanReachTheFinalDecision()
    {
        var evidence = Evidence(Guid.NewGuid());

        Assert.Equal(GuardrailsJournalAppendStatus.Appended, await _store.AppendAsync(
            evidence with { ReasonCode = GuardrailsReasonCode.ClassifierTimedOut }, default));
        Assert.Equal(GuardrailsJournalAppendStatus.Appended, await _store.AppendAsync(
            evidence with { ReasonCode = GuardrailsReasonCode.ProviderUnavailable }, default));
        Assert.Equal(GuardrailsJournalAppendStatus.Appended, await _store.AppendAsync(evidence, default));
        Assert.Equal("Allowed", await ScalarAsync<string>(
            $"SELECT reason_code FROM \"{_schema}\".guardrails_evaluations WHERE operation_id = '{evidence.OperationId}'"));
    }

    [Fact]
    public async Task RecordedEvidenceIsImmutable()
    {
        var evidence = Evidence(Guid.NewGuid());
        await _store.AppendAsync(evidence, default);

        await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync<int>(
            $"UPDATE \"{_schema}\".guardrails_evaluations SET reason_code = 'Allowed' RETURNING 1"));
        await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync<int>(
            $"DELETE FROM \"{_schema}\".guardrails_evaluations RETURNING 1"));
    }

    [Fact]
    public async Task UnavailableDatabaseReturnsUnavailable()
    {
        var store = new GuardrailsEvidenceStore(fixture.DataSource, "missing_" + Guid.NewGuid().ToString("N"));

        Assert.Equal(GuardrailsJournalAppendStatus.Unavailable, await store.AppendAsync(Evidence(Guid.NewGuid()), default));
    }

    [Fact]
    public void ReservedSchemaIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new GuardrailsEvidenceStore(fixture.DataSource, "public"));
        Assert.Throws<ArgumentException>(() => new GuardrailsEvidenceStore(fixture.DataSource, "Bad-Schema"));
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static GuardrailsEvaluationEvidence Evidence(Guid operationId) => new(
        operationId,
        "model-turn-1",
        new string('b', 64),
        new string('C', 64),
        "https://identity.test/",
        "actor-1",
        "tenant-1",
        "project-1",
        "run-1",
        "session-1",
        "model.turn",
        "runtime-action",
        "1.1.1",
        new string('a', 64),
        "grant-1",
        "1",
        3,
        GuardrailsContentStage.UserInput,
        GuardrailsContentClass.Text,
        [new GuardrailsCheckEvidence(0, "azure-prompt-shields", "1.0.0", 1, "options-1",
            GuardrailsCheckOutcome.Clear, 0)],
        PolicyEvaluationOutcome.Allow,
        default,
        PolicyEvaluationOutcome.Allow,
        default,
        GuardrailsReasonCode.Allowed);
}
