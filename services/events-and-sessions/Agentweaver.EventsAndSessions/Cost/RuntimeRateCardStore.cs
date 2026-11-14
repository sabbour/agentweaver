using System.Text.Json;
using System.Text.RegularExpressions;
using Agentweaver.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.EventsAndSessions.Cost;

public sealed class RuntimeRateCardConflictException(string message) : InvalidOperationException(message)
{
}

public sealed class RuntimeRateCardStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex SchemaPattern = new(
        "^[a-z][a-z0-9_]{0,62}\\z", RegexOptions.CultureInvariant);
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _quotedSchema;

    public RuntimeRateCardStore(NpgsqlDataSource dataSource, string schema)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ValidateSchema(schema);
        _dataSource = dataSource;
        _quotedSchema = $"\"{schema}\"";
    }

    public async Task<bool> SaveAsync(
        RuntimeRateCard runtimeRateCard,
        CancellationToken cancellationToken = default)
    {
        RuntimeRateCardContract.Validate(runtimeRateCard);
        var card = runtimeRateCard.RateCard;
        var canonicalCard = CostRateCardContract.SerializeCanonical(card);
        var canonicalRuntimeCard = RuntimeRateCardContract.SerializeCanonical(runtimeRateCard);
        var multipliers = JsonSerializer.Serialize(card.ModelMultipliers, JsonOptions);

        // A rate-card ID and version are immutable; accept retries only when the stored content matches.
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_quotedSchema}.usage_rate_cards (
                card_id, version, meter_source, unit, nano_units_per_unit,
                model_multipliers, canonical_input, payload)
            VALUES (
                @card_id, @version, @meter_source, @unit, @nano_units_per_unit,
                @model_multipliers, @canonical_input, @payload)
            ON CONFLICT DO NOTHING
            """, connection))
        {
            insert.Parameters.AddWithValue("card_id", NpgsqlDbType.Varchar, card.Id);
            insert.Parameters.AddWithValue("version", NpgsqlDbType.Varchar, card.Version);
            insert.Parameters.AddWithValue("meter_source", NpgsqlDbType.Varchar, card.MeterSource);
            insert.Parameters.AddWithValue("unit", NpgsqlDbType.Varchar, card.Unit);
            insert.Parameters.AddWithValue("nano_units_per_unit", NpgsqlDbType.Numeric, card.NanoUnitsPerUnit);
            insert.Parameters.AddWithValue("model_multipliers", NpgsqlDbType.Jsonb, multipliers);
            insert.Parameters.AddWithValue("canonical_input", NpgsqlDbType.Text, canonicalCard);
            insert.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, canonicalRuntimeCard);
            var inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
            var stored = await ReadStoredAsync(connection, card.Id, card.Version, cancellationToken)
                ?? throw new InvalidOperationException("The saved runtime rate card could not be read.");
            var storedRateCard = RuntimeRateCardContract.DeserializeRuntimePayload(stored.Payload);
            if (storedRateCard is null ||
                !string.Equals(stored.CanonicalCard, canonicalCard, StringComparison.Ordinal) ||
                !string.Equals(
                    RuntimeRateCardContract.SerializeCanonical(storedRateCard),
                    canonicalRuntimeCard,
                    StringComparison.Ordinal))
                throw new RuntimeRateCardConflictException(
                    "A runtime rate-card ID and version cannot be reused with different immutable content.");
            return inserted == 1;
        }
    }

    public async Task<RuntimeRateCard?> GetAsync(
        string id,
        string version,
        CancellationToken cancellationToken = default)
    {
        RuntimeRateCardContract.ValidateIdentity(id, version);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var stored = await ReadStoredAsync(connection, id, version, cancellationToken);
        if (stored is null)
            return null;

        var runtimeRateCard = RuntimeRateCardContract.DeserializeRuntimePayload(stored.Payload);
        if (runtimeRateCard is null)
            return null;
        if (runtimeRateCard.RateCard.Id != id ||
            runtimeRateCard.RateCard.Version != version ||
            !string.Equals(
                stored.CanonicalCard,
                CostRateCardContract.SerializeCanonical(runtimeRateCard.RateCard),
                StringComparison.Ordinal))
            throw new InvalidOperationException("The stored runtime rate card does not match its key or canonical content.");
        return runtimeRateCard;
    }

    private async Task<StoredRateCard?> ReadStoredAsync(
        NpgsqlConnection connection,
        string id,
        string version,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT canonical_input, payload::text
            FROM {_quotedSchema}.usage_rate_cards
            WHERE card_id = @card_id AND version = @version
            """, connection);
        command.Parameters.AddWithValue("card_id", NpgsqlDbType.Varchar, id);
        command.Parameters.AddWithValue("version", NpgsqlDbType.Varchar, version);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StoredRateCard(reader.GetString(0), reader.GetString(1))
            : null;
    }

    private static void ValidateSchema(string schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (!SchemaPattern.IsMatch(schema) || schema is "public" or "pg_catalog" or "information_schema" ||
            schema.StartsWith("pg_", StringComparison.Ordinal))
            throw new ArgumentException("A non-reserved lowercase schema identifier is required.", nameof(schema));
    }

    private sealed record StoredRateCard(string CanonicalCard, string Payload);
}
