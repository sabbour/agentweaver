using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Persistence.Postgres;

/// <summary>One planned table grant for a runtime role, copied from the reviewed P1 PostgreSQL plan.</summary>
public sealed record PlannedTableGrant(
    [property: JsonPropertyName("table")] string Table,
    [property: JsonPropertyName("privileges")] string[] Privileges,
    [property: JsonPropertyName("lockColumn")] string? LockColumn);

/// <summary>One planned function ACL for a runtime role, copied from the reviewed P1 PostgreSQL plan.</summary>
public sealed record PlannedFunctionGrant(
    [property: JsonPropertyName("signature")] string Signature,
    [property: JsonPropertyName("execute")] bool Execute);

/// <summary>The reviewed runtime grants and migration versions for one service schema.</summary>
public sealed record PlannedServiceGrants(
    string PlanSha256,
    string Service,
    string Schema,
    string RuntimeRole,
    IReadOnlyList<int> Versions,
    IReadOnlyList<PlannedTableGrant> Tables,
    IReadOnlyList<PlannedFunctionGrant> Functions,
    IReadOnlyList<string> OwnerOnlyTables,
    IReadOnlyList<string> OwnerOnlySequences);

/// <summary>
/// Applies and reads back the reviewed runtime grants for a migrated P1 service schema.
/// The plan is the same JSON the P1 operator initializer hashes, embedded at build time,
/// so a migration Job can only grant what the reviewed plan names.
/// </summary>
public static class PlannedRuntimeGrants
{
    /// <summary>Printed after a successful apply and readback; the maintenance runner requires it.</summary>
    public const string Marker = "P1_POSTGRES_RUNTIME_GRANTS_VERIFIED";

    private const string ResourceName = "Agentweaver.Persistence.Postgres.p1-postgres-plan.json";
    private static readonly string[] TablePrivileges =
        ["SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER"];
    private static readonly string[] SequencePrivileges = ["USAGE", "SELECT", "UPDATE"];

    /// <summary>Returns the exact embedded plan bytes, which the plan hash covers.</summary>
    public static byte[] ReadEmbeddedPlan()
    {
        using var stream = typeof(PlannedRuntimeGrants).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The reviewed P1 PostgreSQL plan is not embedded in this build.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Selects one service from the embedded plan after checking every gate that keeps a
    /// migration Job inside the reviewed plan. Any mismatch throws before a connection opens.
    /// </summary>
    public static PlannedServiceGrants Load(
        string service,
        string schema,
        string runtimeRole,
        string expectedPlanSha256,
        IReadOnlyList<int> supportedVersions) =>
        Select(ReadEmbeddedPlan(), service, schema, runtimeRole, expectedPlanSha256, supportedVersions);

    /// <summary>
    /// Validates raw plan bytes against the expected hash, the configured service identity and
    /// schema, and the compiled migration catalog. Kept separate from the embedded resource so
    /// tests can prove each gate with a changed plan.
    /// </summary>
    public static PlannedServiceGrants Select(
        byte[] planBytes,
        string service,
        string schema,
        string runtimeRole,
        string expectedPlanSha256,
        IReadOnlyList<int> supportedVersions)
    {
        // Step 1: the hash covers the exact bytes, so any edit to the embedded plan, even whitespace,
        // stops the Job. The runner passes the hash it computed from the same reviewed source.
        var actual = Convert.ToHexString(SHA256.HashData(planBytes)).ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(expectedPlanSha256 ?? "", "^[0-9a-f]{64}$") ||
            actual != expectedPlanSha256)
            throw new InvalidOperationException(
                $"The embedded P1 PostgreSQL plan hash {actual} differs from the expected reviewed plan hash.");

        using var document = JsonDocument.Parse(planBytes);
        var root = document.RootElement;
        var entry = root.GetProperty("services").EnumerateArray()
            .SingleOrDefault(item => item.GetProperty("service").GetString() == service);
        if (entry.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"The reviewed P1 PostgreSQL plan has no {service} service.");

        // Step 2: configuration may not redirect grants. The schema and runtime role must be exactly
        // the planned ones, so a wrong environment value fails instead of granting to another role.
        if (entry.GetProperty("schema").GetString() != schema)
            throw new InvalidOperationException($"The configured {service} schema differs from the reviewed plan.");
        if (entry.GetProperty("role").GetString() != runtimeRole)
            throw new InvalidOperationException($"The configured {service} runtime role differs from the reviewed plan.");

        // Step 3: the compiled migrator always applies every version it knows. Requiring its catalog to
        // equal the planned list means an image with an extra (unreviewed) migration stops here, before
        // migrating, and an image missing a planned one stops too.
        var versionsProperty = service switch
        {
            "orchestrator" => "coreVersions",
            "events-and-sessions" => "eventsVersions",
            "environment" => "environmentVersions",
            _ => throw new InvalidOperationException($"The {service} service has no planned integer migrations."),
        };
        var versions = root.GetProperty(versionsProperty).EnumerateArray().Select(item => item.GetInt32()).ToArray();
        if (!versions.SequenceEqual(supportedVersions))
            throw new InvalidOperationException(
                $"The {service} migration catalog [{string.Join(',', supportedVersions)}] differs from the reviewed plan [{string.Join(',', versions)}].");

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return new PlannedServiceGrants(
            actual,
            service,
            schema,
            runtimeRole,
            versions,
            entry.GetProperty("tables").Deserialize<PlannedTableGrant[]>(options) ?? [],
            entry.GetProperty("functions").Deserialize<PlannedFunctionGrant[]>(options) ?? [],
            entry.GetProperty("ownerOnlyTables").Deserialize<string[]>(options) ?? [],
            entry.GetProperty("ownerOnlySequences").Deserialize<string[]>(options) ?? []);
    }

    /// <summary>
    /// Applies the planned runtime grants in one transaction and reads every planned privilege back
    /// before committing. The connection must belong to the schema owner (the migration role).
    /// GRANT and REVOKE are idempotent, so a rerun on an up-to-date schema changes nothing.
    /// </summary>
    public static async Task ApplyAndVerifyAsync(
        NpgsqlConnection connection,
        PlannedServiceGrants plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(plan);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var schema = Quote(plan.Schema);
        var role = Quote(plan.RuntimeRole);

        // The runtime role must already exist. The migration Job never creates roles or grants CONNECT;
        // those belong to the one-time operator initialization.
        if (await ScalarAsync(connection, transaction,
                "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = @role)", cancellationToken,
                ("role", plan.RuntimeRole)) is not true)
            throw new InvalidOperationException($"The {plan.Service} runtime role does not exist.");

        await ExecuteAsync(connection, transaction, $"GRANT USAGE ON SCHEMA {schema} TO {role}", cancellationToken);
        foreach (var table in plan.Tables)
        {
            var relation = $"{schema}.{Quote(table.Table)}";
            // Only the closed privilege names are accepted, so the plan can never inject SQL text.
            foreach (var privilege in table.Privileges)
                if (!TablePrivileges.Contains(privilege, StringComparer.Ordinal))
                    throw new InvalidOperationException($"The plan names an unsupported privilege on {relation}.");
            if (table.Privileges.Length != 0)
                await ExecuteAsync(connection, transaction,
                    $"GRANT {string.Join(", ", table.Privileges)} ON TABLE {relation} TO {role}", cancellationToken);
            // Row-lock tables also let the runtime role update only the lock column.
            if (table.LockColumn is not null)
                await ExecuteAsync(connection, transaction,
                    $"GRANT UPDATE ({Quote(table.LockColumn)}) ON TABLE {relation} TO {role}", cancellationToken);
        }
        foreach (var function in plan.Functions)
        {
            var oid = await ScalarAsync(connection, transaction,
                "SELECT pg_catalog.to_regprocedure(@signature)::oid", cancellationToken,
                ("signature", $"{plan.Schema}.{function.Signature}"));
            if (oid is not uint)
                throw new InvalidOperationException($"The planned function {plan.Schema}.{function.Signature} does not exist.");
            // Bind the catalog OID explicitly because Npgsql exposes PostgreSQL oid as UInt32.
            await using var resolve = new NpgsqlCommand(
                "SELECT @oid::oid::regprocedure::text", connection, transaction);
            resolve.Parameters.AddWithValue("oid", NpgsqlDbType.Oid, oid);
            // to_regprocedure resolved the signature, so its canonical regprocedure text is safe to inline.
            var signature = (string)(await resolve.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            // PostgreSQL grants EXECUTE to PUBLIC by default. Remove it, then grant only what the plan allows.
            await ExecuteAsync(connection, transaction, $"REVOKE ALL ON FUNCTION {signature} FROM PUBLIC", cancellationToken);
            if (function.Execute)
                await ExecuteAsync(connection, transaction, $"GRANT EXECUTE ON FUNCTION {signature} TO {role}", cancellationToken);
        }

        await VerifyAsync(connection, transaction, plan, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads back schema usage, every planned table privilege (granted and not granted), the single
    /// lock column, owner-only objects, and function EXECUTE. Any difference throws, so the caller's
    /// transaction is never committed with a partial or broader grant set.
    /// </summary>
    private static async Task VerifyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PlannedServiceGrants plan,
        CancellationToken cancellationToken)
    {
        if (await ScalarAsync(connection, transaction,
                "SELECT has_schema_privilege(@role, @schema, 'USAGE') AND NOT has_schema_privilege(@role, @schema, 'CREATE')",
                cancellationToken, ("role", plan.RuntimeRole), ("schema", plan.Schema)) is not true)
            throw new InvalidOperationException($"The {plan.Service} runtime role does not have exactly USAGE on {plan.Schema}.");
        foreach (var table in plan.Tables)
        {
            var relation = $"{Quote(plan.Schema)}.{Quote(table.Table)}";
            foreach (var privilege in TablePrivileges)
            {
                // Privileges outside the plan must be absent too, not only the planned ones present.
                var granted = await ScalarAsync(connection, transaction,
                    "SELECT has_table_privilege(@role, @table, @privilege)", cancellationToken,
                    ("role", plan.RuntimeRole), ("table", relation), ("privilege", privilege));
                if (!Equals(granted, table.Privileges.Contains(privilege, StringComparer.Ordinal)))
                    throw new InvalidOperationException($"Unexpected {privilege} authority on {plan.Schema}.{table.Table}.");
            }
            if (table.LockColumn is not null &&
                await ScalarAsync(connection, transaction, """
                    SELECT has_column_privilege(@role, @table, @column, 'UPDATE')
                      AND NOT EXISTS (
                        SELECT 1 FROM pg_catalog.pg_attribute a
                        WHERE a.attrelid = pg_catalog.to_regclass(@table)
                          AND a.attnum > 0 AND NOT a.attisdropped AND a.attname <> @column
                          AND pg_catalog.has_column_privilege(@role, a.attrelid, a.attnum, 'UPDATE'))
                    """, cancellationToken,
                    ("role", plan.RuntimeRole), ("table", relation), ("column", table.LockColumn)) is not true)
                throw new InvalidOperationException(
                    $"The {plan.Service} runtime role does not have exactly its planned UPDATE column on {plan.Schema}.{table.Table}.");
        }
        foreach (var table in plan.OwnerOnlyTables)
            foreach (var privilege in TablePrivileges)
                if (await ScalarAsync(connection, transaction,
                        "SELECT has_table_privilege(@role, @table, @privilege)", cancellationToken,
                        ("role", plan.RuntimeRole), ("table", $"{Quote(plan.Schema)}.{Quote(table)}"),
                        ("privilege", privilege)) is true)
                    throw new InvalidOperationException($"Unexpected {privilege} authority on owner-only table {plan.Schema}.{table}.");
        foreach (var sequence in plan.OwnerOnlySequences)
            foreach (var privilege in SequencePrivileges)
                if (await ScalarAsync(connection, transaction,
                        "SELECT has_sequence_privilege(@role, @sequence, @privilege)", cancellationToken,
                        ("role", plan.RuntimeRole), ("sequence", $"{Quote(plan.Schema)}.{Quote(sequence)}"),
                        ("privilege", privilege)) is true)
                    throw new InvalidOperationException($"Unexpected {privilege} authority on owner-only sequence {plan.Schema}.{sequence}.");
        foreach (var function in plan.Functions)
        {
            // PUBLIC must never keep EXECUTE; the runtime role has it only when planned.
            var state = await ScalarAsync(connection, transaction, """
                SELECT pg_catalog.has_function_privilege(@role, p.oid, 'EXECUTE') = @execute
                  AND NOT EXISTS (
                    SELECT 1 FROM pg_catalog.aclexplode(coalesce(p.proacl, pg_catalog.acldefault('f', p.proowner))) a
                    WHERE a.grantee = 0)
                FROM pg_catalog.pg_proc p
                WHERE p.oid = pg_catalog.to_regprocedure(@signature)
                """, cancellationToken, ("role", plan.RuntimeRole), ("execute", function.Execute),
                ("signature", $"{plan.Schema}.{function.Signature}"));
            if (state is not true)
                throw new InvalidOperationException($"Unexpected EXECUTE authority on {plan.Schema}.{function.Signature}.");
        }
    }

    /// <summary>Reads an integer migration ledger in ascending order for the Job's final report.</summary>
    public static async Task<int[]> ReadVersionsAsync(
        NpgsqlConnection connection, string schema, string table, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT version FROM {Quote(schema)}.{Quote(table)} ORDER BY version", connection);
        var versions = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            versions.Add(reader.GetInt32(0));
        return [.. versions];
    }

    /// <summary>Formats the single success line the maintenance runner parses from Job logs.</summary>
    public static string FormatMarker(PlannedServiceGrants plan, IReadOnlyList<int> applied) =>
        $"{Marker} service={plan.Service} schema={plan.Schema} role={plan.RuntimeRole} " +
        $"planSha256={plan.PlanSha256} versions={string.Join(',', applied)}";

    /// <summary>Quotes a PostgreSQL identifier by doubling embedded quotes.</summary>
    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object?> ScalarAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }
}
