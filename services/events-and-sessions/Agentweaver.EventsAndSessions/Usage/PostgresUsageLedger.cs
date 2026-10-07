using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentweaver.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.EventsAndSessions;

public sealed class UsageLedgerConflictException(string message) : InvalidOperationException(message)
{
}

public sealed class PostgresUsageLedger : IUsageLedger
{
    private const short ContractVersion = 1;
    private static readonly Regex SchemaPattern = new(
        "^[a-z][a-z0-9_]{0,62}\\z", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _quotedSchema;

    public PostgresUsageLedger(NpgsqlDataSource dataSource, string schema)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ValidateSchema(schema);
        _dataSource = dataSource;
        _quotedSchema = $"\"{schema}\"";
    }

    public async Task<UsageIngestionResult> AppendAsync(
        UsageSubmission submission,
        CostBinding? binding,
        CostPrice price,
        CancellationToken cancellationToken = default)
    {
        UsageLedgerValidation.Validate(submission, binding, price);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var result = await AppendWithinTransactionAsync(
            connection, transaction, submission, binding, price, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    internal async Task<UsageIngestionResult> AppendWithinTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        UsageSubmission submission, CostBinding? binding, CostPrice price,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction.Connection != connection)
            throw new ArgumentException("The usage transaction must belong to the supplied connection.");
        UsageLedgerValidation.Validate(submission, binding, price);
        var canonical = UsageLedgerCanonicalizer.Serialize(submission, binding, price);
        var payloadHash = HashCanonicalInput(canonical);
        var payload = JsonSerializer.Serialize(
            new StoredUsage(submission, binding, price), JsonOptions);

        if (binding is not null)
            await EnsureRateCardAsync(connection, transaction, binding.RateCard, cancellationToken);

        var existing = await ReadByEventIdAsync(
            connection, transaction, submission.EventId, cancellationToken);
        if (existing is not null)
        {
            var matchedExisting = MatchDuplicate(existing, canonical);
            return matchedExisting;
        }

        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_quotedSchema}.usage_ledger (
                contract_version, tenant_id, project_id, run_id, session_id, event_id, occurred_at,
                agent_id, model_reference, model_id, meter_source, selection_revision,
                input_tokens, output_tokens, cached_tokens, cache_write_tokens, reasoning_tokens, request_count,
                provider_units, provider_unit, duration_milliseconds, price_disposition,
                price_amount, price_unit, unpriced_reason, rate_card_id, rate_card_version, rate_card_unit,
                cost_binding, canonical_input, canonical_input_hash, payload)
            VALUES (
                @contract_version, @tenant_id, @project_id, @run_id, @session_id, @event_id, @occurred_at,
                @agent_id, @model_reference, @model_id, @meter_source, @selection_revision,
                @input_tokens, @output_tokens, @cached_tokens, @cache_write_tokens, @reasoning_tokens, @request_count,
                @provider_units, @provider_unit, @duration_milliseconds, @price_disposition,
                @price_amount, @price_unit, @unpriced_reason, @rate_card_id, @rate_card_version, @rate_card_unit,
                @cost_binding, @canonical_input, @canonical_input_hash, @payload)
            ON CONFLICT (event_id) DO NOTHING
            RETURNING recorded_at
            """, connection, transaction);
        AddInsertParameters(insert, submission, binding, price, canonical, payload);
        insert.Parameters.AddWithValue("canonical_input_hash", NpgsqlDbType.Varchar, payloadHash);
        DateTimeOffset? recordedAt = null;
        await using (var reader = await insert.ExecuteReaderAsync(cancellationToken))
            if (await reader.ReadAsync(cancellationToken))
                recordedAt = reader.GetFieldValue<DateTimeOffset>(0);
        if (recordedAt is not null)
        {
            return new UsageIngestionResult(
                new UsageLedgerEntry(
                    submission, binding, price, recordedAt.Value, payloadHash), IsDuplicate: false);
        }

        existing = await ReadByEventIdAsync(
            connection, transaction, submission.EventId, cancellationToken);
        if (existing is null)
            throw new InvalidOperationException("The usage event identity conflicted without a stored row.");
        var duplicate = MatchDuplicate(existing, canonical);
        return duplicate;
    }

    public async Task<UsageRunTotals> GetRunTotalsAsync(
        string tenantId,
        string projectId,
        string runId,
        CancellationToken cancellationToken = default)
    {
        UsageLedgerValidation.ValidateRunScope(tenantId, projectId, runId);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT l.agent_id, l.meter_source, l.price_unit, r.unit,
                   l.input_tokens, l.output_tokens, l.cached_tokens, l.reasoning_tokens,
                   l.duration_milliseconds, l.request_count, l.price_amount, l.price_disposition,
                   l.cache_write_tokens
            FROM {_quotedSchema}.usage_ledger l
            LEFT JOIN {_quotedSchema}.usage_rate_cards r
                ON r.card_id = l.rate_card_id AND r.version = l.rate_card_version
            WHERE l.tenant_id = @tenant_id AND l.project_id = @project_id AND l.run_id = @run_id
            ORDER BY l.agent_id, l.event_id
            """, connection);
        command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Varchar, tenantId);
        command.Parameters.AddWithValue("project_id", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run_id", NpgsqlDbType.Varchar, runId);

        var agents = new Dictionary<string, AgentAccumulator>(StringComparer.Ordinal);
        var runAmounts = new Dictionary<(string MeterSource, string Unit), AmountAccumulator>();
        var events = 0L;
        var fullyPriced = true;
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var agentId = reader.GetString(0);
                if (!agents.TryGetValue(agentId, out var agent))
                {
                    agent = new AgentAccumulator(agentId);
                    agents.Add(agentId, agent);
                }

                events = checked(events + 1);
                var disposition = Enum.Parse<CostDisposition>(reader.GetString(11), ignoreCase: false);
                var priced = disposition != CostDisposition.Unpriced;
                fullyPriced &= priced;
                var meterSource = reader.GetString(1);
                var unit = reader.IsDBNull(2)
                    ? reader.IsDBNull(3) ? null : reader.GetString(3)
                    : reader.GetString(2);
                var amount = reader.IsDBNull(10) ? (decimal?)null : reader.GetDecimal(10);

                agent.AddUsage(
                    reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : reader.GetInt64(7),
                    reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                    reader.IsDBNull(9) ? null : reader.GetInt64(9),
                    reader.IsDBNull(12) ? null : reader.GetInt64(12),
                    priced);

                if (unit is not null)
                {
                    AddAmount(agent.Amounts, meterSource, unit, priced, amount);
                    AddAmount(runAmounts, meterSource, unit, priced, amount);
                }
            }
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException(
                "Usage totals exceed the exact numeric range supported by the usage contract.", exception);
        }

        var agentTotals = agents.Values
            .OrderBy(agent => agent.AgentId, StringComparer.Ordinal)
            .Select(agent => agent.ToTotals())
            .ToImmutableArray();
        return new UsageRunTotals(
            tenantId,
            projectId,
            runId,
            events,
            fullyPriced,
            agentTotals,
            ToAmountTotals(runAmounts));
    }

    private async Task EnsureRateCardAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CostRateCard card,
        CancellationToken cancellationToken)
    {
        var canonical = UsageLedgerCanonicalizer.SerializeRateCard(card);
        var payload = JsonSerializer.Serialize(card, JsonOptions);
        var multipliers = JsonSerializer.Serialize(card.ModelMultipliers, JsonOptions);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_quotedSchema}.usage_rate_cards (
                card_id, version, meter_source, unit, nano_units_per_unit,
                model_multipliers, canonical_input, payload)
            VALUES (@card_id, @version, @meter_source, @unit, @nano_units_per_unit,
                @model_multipliers, @canonical_input, @payload)
            ON CONFLICT DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("card_id", NpgsqlDbType.Varchar, card.Id);
            insert.Parameters.AddWithValue("version", NpgsqlDbType.Varchar, card.Version);
            insert.Parameters.AddWithValue("meter_source", NpgsqlDbType.Varchar, card.MeterSource);
            insert.Parameters.AddWithValue("unit", NpgsqlDbType.Varchar, card.Unit);
            insert.Parameters.AddWithValue("nano_units_per_unit", NpgsqlDbType.Numeric, card.NanoUnitsPerUnit);
            insert.Parameters.AddWithValue("model_multipliers", NpgsqlDbType.Jsonb, multipliers);
            insert.Parameters.AddWithValue("canonical_input", NpgsqlDbType.Text, canonical);
            insert.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var read = new NpgsqlCommand($"""
            SELECT canonical_input
            FROM {_quotedSchema}.usage_rate_cards
            WHERE card_id = @card_id AND version = @version
            FOR SHARE
            """, connection, transaction);
        read.Parameters.AddWithValue("card_id", NpgsqlDbType.Varchar, card.Id);
        read.Parameters.AddWithValue("version", NpgsqlDbType.Varchar, card.Version);
        var stored = (string?)await read.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("The pinned usage rate card was not persisted.");
        if (!string.Equals(stored, canonical, StringComparison.Ordinal))
            throw new UsageLedgerConflictException(
                "A rate card ID and version cannot be reused with different immutable content.");
    }

    private async Task<StoredEntry?> ReadByEventIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT canonical_input, payload, recorded_at, canonical_input_hash
            FROM {_quotedSchema}.usage_ledger
            WHERE event_id = @event_id
            FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        var canonical = reader.GetString(0);
        var payloadJson = reader.GetString(1);
        var recordedAt = reader.GetFieldValue<DateTimeOffset>(2);
        var payloadHash = reader.GetString(3);
        if (!string.Equals(payloadHash, HashCanonicalInput(canonical), StringComparison.Ordinal))
            throw new InvalidOperationException("The stored usage payload hash does not match its canonical input.");
        StoredUsage payload;
        try
        {
            payload = JsonSerializer.Deserialize<StoredUsage>(payloadJson, JsonOptions)
                ?? throw new JsonException("Usage payload was empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The stored usage payload is not readable.", exception);
        }
        return new StoredEntry(
            canonical,
            new UsageLedgerEntry(payload.Usage, payload.CostBinding, payload.Price, recordedAt, payloadHash));
    }

    private static UsageIngestionResult MatchDuplicate(StoredEntry existing, string canonical)
    {
        if (!string.Equals(existing.CanonicalInput, canonical, StringComparison.Ordinal))
            throw new UsageLedgerConflictException(
                "A usage event ID cannot be reused with changed scope, usage, model, binding, or pricing.");
        return new UsageIngestionResult(existing.Entry, IsDuplicate: true);
    }

    private static string HashCanonicalInput(string canonical) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    private void AddInsertParameters(
        NpgsqlCommand command,
        UsageSubmission submission,
        CostBinding? binding,
        CostPrice price,
        string canonical,
        string payload)
    {
        var attribution = submission.Attribution;
        var model = submission.ModelBinding;
        var measurement = submission.Measurement;
        var card = binding?.RateCard;
        command.Parameters.AddWithValue("contract_version", NpgsqlDbType.Smallint, ContractVersion);
        command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Varchar, attribution.TenantId);
        command.Parameters.AddWithValue("project_id", NpgsqlDbType.Varchar, attribution.ProjectId);
        command.Parameters.AddWithValue("run_id", NpgsqlDbType.Varchar, attribution.RunId);
        command.Parameters.AddWithValue("session_id", NpgsqlDbType.Varchar, attribution.SessionId);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, submission.EventId);
        command.Parameters.AddWithValue("occurred_at", NpgsqlDbType.TimestampTz, submission.OccurredAt);
        command.Parameters.AddWithValue("agent_id", NpgsqlDbType.Varchar, attribution.AgentId);
        command.Parameters.AddWithValue("model_reference", NpgsqlDbType.Varchar, model.ModelReference);
        command.Parameters.AddWithValue("model_id", NpgsqlDbType.Varchar, model.ModelId);
        command.Parameters.AddWithValue("meter_source", NpgsqlDbType.Varchar, model.MeterSource);
        command.Parameters.AddWithValue("selection_revision", NpgsqlDbType.Varchar, model.SelectionRevision);
        AddNullable(command, "input_tokens", NpgsqlDbType.Bigint, measurement.InputTokens);
        AddNullable(command, "output_tokens", NpgsqlDbType.Bigint, measurement.OutputTokens);
        AddNullable(command, "cached_tokens", NpgsqlDbType.Bigint, measurement.CachedTokens);
        AddNullable(command, "cache_write_tokens", NpgsqlDbType.Bigint, measurement.CacheWriteTokens);
        AddNullable(command, "reasoning_tokens", NpgsqlDbType.Bigint, measurement.ReasoningTokens);
        AddNullable(command, "request_count", NpgsqlDbType.Bigint, measurement.RequestCount);
        AddNullable(command, "provider_units", NpgsqlDbType.Numeric, measurement.ProviderUnits);
        AddNullable(command, "provider_unit", NpgsqlDbType.Varchar, measurement.ProviderUnit);
        AddNullable(command, "duration_milliseconds", NpgsqlDbType.Numeric, measurement.DurationMilliseconds);
        command.Parameters.AddWithValue("price_disposition", NpgsqlDbType.Varchar, price.Disposition.ToString());
        AddNullable(command, "price_amount", NpgsqlDbType.Numeric, price.Amount);
        AddNullable(command, "price_unit", NpgsqlDbType.Varchar, price.Unit);
        AddNullable(command, "unpriced_reason", NpgsqlDbType.Varchar, price.UnpricedReason);
        AddNullable(command, "rate_card_id", NpgsqlDbType.Varchar, card?.Id);
        AddNullable(command, "rate_card_version", NpgsqlDbType.Varchar, card?.Version);
        AddNullable(command, "rate_card_unit", NpgsqlDbType.Varchar, card?.Unit);
        AddNullable(command, "cost_binding", NpgsqlDbType.Jsonb,
            binding is null ? null : JsonSerializer.Serialize(binding, JsonOptions));
        command.Parameters.AddWithValue("canonical_input", NpgsqlDbType.Text, canonical);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
    }

    private static void AddNullable(NpgsqlCommand command, string name, NpgsqlDbType type, object? value)
    {
        var parameter = command.Parameters.Add(name, type);
        parameter.Value = value ?? DBNull.Value;
    }

    private static void AddAmount(
        Dictionary<(string MeterSource, string Unit), AmountAccumulator> amounts,
        string meterSource,
        string unit,
        bool priced,
        decimal? amount)
    {
        var key = (meterSource, unit);
        if (!amounts.TryGetValue(key, out var total))
        {
            total = new AmountAccumulator();
            amounts.Add(key, total);
        }
        total.Add(priced, amount);
    }

    private static ImmutableArray<UsageAmountTotal> ToAmountTotals(
        Dictionary<(string MeterSource, string Unit), AmountAccumulator> amounts) =>
        amounts
            .Where(entry => entry.Value.PricedEvents > 0)
            .OrderBy(entry => entry.Key.MeterSource, StringComparer.Ordinal)
            .ThenBy(entry => entry.Key.Unit, StringComparer.Ordinal)
            .Select(entry => new UsageAmountTotal(
                entry.Key.MeterSource,
                entry.Key.Unit,
                entry.Value.Amount.ToDecimal(),
                entry.Value.PricedEvents,
                entry.Value.UnpricedEvents))
            .ToImmutableArray();

    private static void ValidateSchema(string schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (!SchemaPattern.IsMatch(schema) || schema is "public" or "pg_catalog" or "information_schema" ||
            schema.StartsWith("pg_", StringComparison.Ordinal))
            throw new ArgumentException("A non-reserved lowercase schema identifier is required.", nameof(schema));
    }

    private sealed record StoredUsage(
        UsageSubmission Usage,
        CostBinding? CostBinding,
        CostPrice Price);

    private sealed record StoredEntry(string CanonicalInput, UsageLedgerEntry Entry);

    private sealed class AgentAccumulator(string agentId)
    {
        public string AgentId { get; } = agentId;
        private long _events;
        private readonly NullableLongAccumulator _requestCount = new();
        private bool _fullyPriced = true;
        private readonly NullableLongAccumulator _inputTokens = new();
        private readonly NullableLongAccumulator _outputTokens = new();
        private readonly NullableLongAccumulator _cachedTokens = new();
        private readonly NullableLongAccumulator _cacheWriteTokens = new();
        private readonly NullableLongAccumulator _reasoningTokens = new();
        private readonly NullableDecimalAccumulator _duration = new();
        public Dictionary<(string MeterSource, string Unit), AmountAccumulator> Amounts { get; } = [];

        public void AddUsage(
            long? inputTokens,
            long? outputTokens,
            long? cachedTokens,
            long? reasoningTokens,
            decimal? duration,
            long? requestCount,
            long? cacheWriteTokens,
            bool priced)
        {
            _events = checked(_events + 1);
            _requestCount.Add(requestCount);
            _fullyPriced &= priced;
            _inputTokens.Add(inputTokens);
            _outputTokens.Add(outputTokens);
            _cachedTokens.Add(cachedTokens);
            _cacheWriteTokens.Add(cacheWriteTokens);
            _reasoningTokens.Add(reasoningTokens);
            _duration.Add(duration);
        }

        public UsageAgentTotals ToTotals() => new(
            AgentId,
            _events,
            _requestCount.Value,
            _inputTokens.Value,
            _outputTokens.Value,
            _cachedTokens.Value,
            _reasoningTokens.Value,
            _duration.Value,
            _fullyPriced,
            ToAmountTotals(Amounts))
        {
            CacheWriteTokens = _cacheWriteTokens.Value
        };
    }

    private sealed class NullableLongAccumulator
    {
        private long _sum;
        private bool _complete = true;

        public long? Value => _complete ? _sum : null;

        public void Add(long? value)
        {
            if (value is null)
            {
                _complete = false;
                return;
            }
            _sum = checked(_sum + value.Value);
        }
    }

    private sealed class NullableDecimalAccumulator
    {
        private readonly ExactDecimalAccumulator _sum = new();
        private bool _complete = true;

        public decimal? Value => _complete ? _sum.ToDecimal() : null;

        public void Add(decimal? value)
        {
            if (value is null)
            {
                _complete = false;
                return;
            }
            _sum.Add(value.Value);
        }
    }

    private sealed class AmountAccumulator
    {
        public ExactDecimalAccumulator Amount { get; } = new();
        public long PricedEvents { get; private set; }
        public long UnpricedEvents { get; private set; }

        public void Add(bool priced, decimal? amount)
        {
            if (priced)
            {
                if (amount is null)
                    throw new InvalidOperationException("A priced usage row is missing its amount.");
                Amount.Add(amount.Value);
                PricedEvents = checked(PricedEvents + 1);
            }
            else
                UnpricedEvents = checked(UnpricedEvents + 1);
        }
    }

    private sealed class ExactDecimalAccumulator
    {
        private BigInteger _coefficient;
        private int _scale;
        private bool _hasValue;

        public void Add(decimal value)
        {
            var bits = decimal.GetBits(value);
            var coefficient = (BigInteger)(uint)bits[0] |
                ((BigInteger)(uint)bits[1] << 32) |
                ((BigInteger)(uint)bits[2] << 64);
            if ((bits[3] & int.MinValue) != 0)
                coefficient = BigInteger.Negate(coefficient);
            var scale = (bits[3] >> 16) & 0x7f;
            if (!_hasValue)
            {
                _coefficient = coefficient;
                _scale = scale;
                _hasValue = true;
                return;
            }

            if (scale > _scale)
            {
                _coefficient *= BigInteger.Pow(10, scale - _scale);
                _scale = scale;
            }
            else if (scale < _scale)
                coefficient *= BigInteger.Pow(10, _scale - scale);
            _coefficient += coefficient;
        }

        public decimal ToDecimal()
        {
            var coefficient = _coefficient;
            var scale = _scale;
            while (scale > 0 && coefficient % 10 == 0)
            {
                coefficient /= 10;
                scale--;
            }

            var maximum = (BigInteger.One << 96) - 1;
            if (scale > 28 || coefficient < 0 || coefficient > maximum)
                throw new OverflowException("The exact decimal total cannot be represented by System.Decimal.");

            var low = unchecked((int)(uint)(coefficient & uint.MaxValue));
            var middle = unchecked((int)(uint)((coefficient >> 32) & uint.MaxValue));
            var high = unchecked((int)(uint)((coefficient >> 64) & uint.MaxValue));
            return new decimal(low, middle, high, isNegative: false, (byte)scale);
        }
    }
}
