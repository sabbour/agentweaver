using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace Agentweaver.Projects.Config;

internal static partial class ProjectsConfigRuntimeGrantPlan
{
    private const string MigrationOwner = "agentweaver-id-projects-config-migration";
    private const string ResourceName = "Agentweaver.Projects.Config.Resources.P1PostgresPlan.json";
    private static readonly string[] TablePrivileges =
        ["SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER"];
    private static readonly string[] ColumnPrivileges = ["SELECT", "INSERT", "UPDATE", "REFERENCES"];
    private static readonly Lazy<ServicePlan> Plan = new(LoadPlan);

    internal static async Task ApplyAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ApplyAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task ApplyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var plan = Plan.Value;
        await VerifyMigrationOwnerAsync(connection, transaction, plan, cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction,
            $"GRANT USAGE ON SCHEMA {Quote(plan.Schema)} TO {Quote(plan.Role)}", cancellationToken).ConfigureAwait(false);
        foreach (var table in plan.Tables)
        {
            var relation = $"{Quote(plan.Schema)}.{Quote(table.Name)}";
            if (table.Privileges.Length > 0)
                await ExecuteAsync(connection, transaction,
                    $"GRANT {string.Join(", ", table.Privileges)} ON TABLE {relation} TO {Quote(plan.Role)}",
                    cancellationToken).ConfigureAwait(false);
            if (table.UpdateColumns.Length > 0)
                await ExecuteAsync(connection, transaction,
                    $"GRANT UPDATE ({string.Join(", ", table.UpdateColumns.Select(Quote))}) ON TABLE {relation} TO {Quote(plan.Role)}",
                    cancellationToken).ConfigureAwait(false);
        }
        foreach (var function in plan.Functions)
        {
            var signature = $"{Quote(plan.Schema)}.{function.Signature}";
            await ExecuteAsync(connection, transaction,
                $"REVOKE ALL ON FUNCTION {signature} FROM PUBLIC", cancellationToken).ConfigureAwait(false);
            if (function.Execute)
                await ExecuteAsync(connection, transaction,
                    $"GRANT EXECUTE ON FUNCTION {signature} TO {Quote(plan.Role)}", cancellationToken).ConfigureAwait(false);
        }
        await VerifyRuntimeGrantsAsync(connection, transaction, plan, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task VerifyRuntimeGrantsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var plan = Plan.Value;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await VerifyRuntimeGrantsAsync(connection, transaction, plan, cancellationToken).ConfigureAwait(false);
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyMigrationOwnerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ServicePlan plan,
        CancellationToken cancellationToken)
    {
        var tables = plan.Tables.Select(table => table.Name).Concat(plan.OwnerOnlyTables)
            .Order(StringComparer.Ordinal).ToArray();
        var functions = plan.Functions.Select(function => function.Signature).Order(StringComparer.Ordinal).ToArray();
        await using var command = new NpgsqlCommand("""
            SELECT current_user = @owner
               AND EXISTS (
                   SELECT 1 FROM pg_catalog.pg_roles
                   WHERE rolname = current_user AND rolcanlogin AND NOT rolsuper AND NOT rolcreatedb
                     AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls
               )
               AND NOT has_database_privilege(current_user, current_database(), 'CREATE')
               AND NOT EXISTS (
                   SELECT 1 FROM pg_catalog.pg_auth_members
                   WHERE member = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = current_user)
               )
               AND EXISTS (
                   SELECT 1 FROM pg_catalog.pg_namespace AS n
                   JOIN pg_catalog.pg_roles AS owner ON owner.oid = n.nspowner
                   WHERE n.nspname = @schema
                     AND owner.rolname = @owner
                     AND coalesce((
                         SELECT array_agg(c.relname::text ORDER BY c.relname)
                         FROM pg_catalog.pg_class AS c
                         WHERE c.relnamespace = n.oid AND c.relkind IN ('r', 'p', 'f')
                     ), ARRAY[]::text[]) = @tables
                     AND coalesce((
                         SELECT array_agg(c.relname::text ORDER BY c.relname)
                         FROM pg_catalog.pg_class AS c
                         WHERE c.relnamespace = n.oid AND c.relkind = 'S'
                     ), ARRAY[]::text[]) = @sequences
                     AND (SELECT count(*) FROM pg_catalog.pg_proc AS p
                          WHERE p.pronamespace = n.oid AND p.prokind IN ('f', 'p')) = cardinality(@functions)
                     AND NOT EXISTS (
                         SELECT 1 FROM pg_catalog.pg_proc AS p
                         WHERE p.pronamespace = n.oid AND p.proowner <> owner.oid
                     )
                     AND NOT EXISTS (
                         SELECT 1 FROM unnest(@functions) AS signature
                         WHERE pg_catalog.to_regprocedure(format('%I.%s', @schema, signature)) IS NULL
                     )
                     AND NOT EXISTS (
                         SELECT 1 FROM pg_catalog.pg_class AS c
                         WHERE c.relnamespace = n.oid AND c.relkind IN ('v', 'm')
                     )
                     AND NOT EXISTS (
                         SELECT 1 FROM pg_catalog.pg_class AS c
                         WHERE c.relnamespace = n.oid AND c.relowner <> owner.oid
                     )
               )
            """, connection, transaction);
        command.Parameters.AddWithValue("owner", MigrationOwner);
        command.Parameters.AddWithValue("schema", plan.Schema);
        command.Parameters.AddWithValue("tables", tables);
        command.Parameters.AddWithValue("sequences", plan.OwnerOnlySequences.Order(StringComparer.Ordinal).ToArray());
        command.Parameters.AddWithValue("functions", functions);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            throw new InvalidOperationException(
                "Projects & Config migrations require the dedicated schema owner and the exact reviewed object inventory; no runtime grants were applied.");
    }

    private static async Task VerifyRuntimeGrantsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ServicePlan plan,
        CancellationToken cancellationToken)
    {
        await using (var schema = new NpgsqlCommand("""
            SELECT has_schema_privilege(@role, @schema, 'USAGE')
               AND NOT has_schema_privilege(@role, @schema, 'CREATE')
            """, connection, transaction))
        {
            schema.Parameters.AddWithValue("role", plan.Role);
            schema.Parameters.AddWithValue("schema", plan.Schema);
            if (await schema.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                throw new InvalidOperationException("The Projects & Config runtime schema grants differ from the reviewed plan.");
        }

        foreach (var table in plan.Tables)
        {
            var expected = table.Privileges.ToHashSet(StringComparer.Ordinal);
            foreach (var privilege in TablePrivileges)
                await AssertPrivilegeAsync(connection, transaction,
                    """
                    SELECT pg_catalog.has_table_privilege(@role, relation.oid, @privilege)
                    FROM pg_catalog.pg_class AS relation
                    JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
                    WHERE namespace.nspname = @schema AND relation.relname = @relation
                    """,
                    plan.Role, plan.Schema, table.Name, privilege,
                    expected.Contains(privilege), cancellationToken).ConfigureAwait(false);

            var columns = await ReadColumnsAsync(connection, transaction, plan.Schema, table.Name, cancellationToken)
                .ConfigureAwait(false);
            var updateColumns = table.UpdateColumns.ToHashSet(StringComparer.Ordinal);
            foreach (var column in columns)
                foreach (var privilege in ColumnPrivileges)
                {
                    var expectedColumn = expected.Contains(privilege) ||
                        (privilege == "UPDATE" && updateColumns.Contains(column));
                    await AssertPrivilegeAsync(connection, transaction,
                        """
                        SELECT pg_catalog.has_column_privilege(@role, relation.oid, @column, @privilege)
                        FROM pg_catalog.pg_class AS relation
                        JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
                        WHERE namespace.nspname = @schema AND relation.relname = @relation
                        """,
                        plan.Role, plan.Schema, table.Name, privilege,
                        expectedColumn, cancellationToken, column).ConfigureAwait(false);
                }
        }

        foreach (var table in plan.OwnerOnlyTables)
            foreach (var privilege in TablePrivileges)
                await AssertPrivilegeAsync(connection, transaction,
                    """
                    SELECT pg_catalog.has_table_privilege(@role, relation.oid, @privilege)
                    FROM pg_catalog.pg_class AS relation
                    JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
                    WHERE namespace.nspname = @schema AND relation.relname = @relation
                    """,
                    plan.Role, plan.Schema, table, privilege, false, cancellationToken)
                    .ConfigureAwait(false);

        await using (var columnAcl = new NpgsqlCommand("""
            SELECT format('%I.%I %s to %s', relation.relname, attribute.attname,
                acl.privilege_type,
                CASE WHEN acl.grantee = 0 THEN 'PUBLIC'
                     ELSE coalesce(pg_catalog.quote_ident(grantee.rolname), acl.grantee::text) END)
            FROM pg_catalog.pg_class AS relation
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
            JOIN pg_catalog.pg_attribute AS attribute ON attribute.attrelid = relation.oid
            CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) AS acl
            LEFT JOIN pg_catalog.pg_roles AS grantee ON grantee.oid = acl.grantee
            WHERE namespace.nspname = @schema
              AND relation.relname = ANY(@tables)
              AND attribute.attnum > 0 AND NOT attribute.attisdropped
              AND acl.grantee <> relation.relowner
            ORDER BY relation.relname, attribute.attname, acl.grantee, acl.privilege_type
            LIMIT 1
            """, connection, transaction))
        {
            columnAcl.Parameters.AddWithValue("schema", plan.Schema);
            columnAcl.Parameters.AddWithValue("tables", plan.OwnerOnlyTables);
            if (await columnAcl.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string unexpected)
                throw new InvalidOperationException(
                    $"The owner-only Projects & Config receipt has an unexpected column privilege ({unexpected}); no grants were committed.");
        }

        foreach (var sequence in plan.OwnerOnlySequences)
            foreach (var privilege in new[] { "USAGE", "SELECT", "UPDATE" })
                await AssertPrivilegeAsync(connection, transaction,
                    """
                    SELECT pg_catalog.has_sequence_privilege(@role, relation.oid, @privilege)
                    FROM pg_catalog.pg_class AS relation
                    JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
                    WHERE namespace.nspname = @schema AND relation.relname = @relation
                      AND relation.relkind = 'S'
                    """,
                    plan.Role, plan.Schema, sequence, privilege, false, cancellationToken)
                    .ConfigureAwait(false);

        foreach (var function in plan.Functions)
        {
            var signature = $"{Quote(plan.Schema)}.{function.Signature}";
            await AssertPrivilegeAsync(connection, transaction,
                """
                SELECT pg_catalog.has_function_privilege(@role, routine.oid, 'EXECUTE')
                FROM pg_catalog.pg_proc AS routine
                JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = routine.pronamespace
                WHERE namespace.nspname = @schema
                  AND format('%I(%s)', routine.proname,
                      pg_catalog.oidvectortypes(routine.proargtypes)) = @relation
                """,
                plan.Role, plan.Schema, function.Signature, null, function.Execute, cancellationToken)
                .ConfigureAwait(false);
            await using var publicGrant = new NpgsqlCommand("""
                SELECT NOT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_proc AS p
                    JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = p.pronamespace
                    CROSS JOIN LATERAL pg_catalog.aclexplode(
                        coalesce(p.proacl, pg_catalog.acldefault('f', p.proowner))) AS acl
                    WHERE namespace.nspname = @schema
                      AND format('%I(%s)', p.proname,
                          pg_catalog.oidvectortypes(p.proargtypes)) = @signature
                      AND acl.grantee = 0 AND acl.privilege_type = 'EXECUTE'
                )
                """, connection, transaction);
            publicGrant.Parameters.AddWithValue("schema", plan.Schema);
            publicGrant.Parameters.AddWithValue("signature", function.Signature);
            if (await publicGrant.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                throw new InvalidOperationException(
                    $"PUBLIC can execute the Projects & Config function {function.Signature}; no grants were committed.");
        }
    }

    private static async Task AssertPrivilegeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        string role,
        string schema,
        string relation,
        string? privilege,
        bool expected,
        CancellationToken cancellationToken,
        string? column = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("relation", relation);
        if (privilege is not null) command.Parameters.AddWithValue("privilege", privilege);
        if (column is not null) command.Parameters.AddWithValue("column", column);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not bool actual ||
            actual != expected)
            throw new InvalidOperationException(
                $"The Projects & Config runtime grants differ from the reviewed plan for {schema}.{relation} {privilege ?? column ?? "EXECUTE"}; no grants were committed.");
    }

    private static async Task<string[]> ReadColumnsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT a.attname::text
            FROM pg_catalog.pg_attribute AS a
            JOIN pg_catalog.pg_class AS c ON c.oid = a.attrelid
            JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @table
              AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attname
            """, connection, transaction);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            columns.Add(reader.GetString(0));
        return [.. columns];
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ServicePlan LoadPlan()
    {
        using var stream = typeof(ProjectsConfigRuntimeGrantPlan).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The reviewed PostgreSQL grant plan is missing from this build.");
        using var document = JsonDocument.Parse(stream);
        var services = document.RootElement.GetProperty("services").EnumerateArray();
        var service = services.SingleOrDefault(item =>
            item.GetProperty("service").GetString() == "projects-config");
        if (service.ValueKind == JsonValueKind.Undefined ||
            service.GetProperty("schema").GetString() != "projects_config" ||
            service.GetProperty("role").GetString() != "agentweaver-id-projects-config")
            throw new InvalidOperationException("The reviewed PostgreSQL grant plan has no approved Projects & Config entry.");

        var tables = service.GetProperty("tables").EnumerateArray().Select(table => new TablePlan(
            RequiredString(table, "table"),
            ReadStrings(table.GetProperty("privileges")),
            table.TryGetProperty("updateColumns", out var columns) ? ReadStrings(columns) : []))
            .ToArray();
        var ownerOnlyTables = ReadStrings(service.GetProperty("ownerOnlyTables"));
        var ownerOnlySequences = ReadStrings(service.GetProperty("ownerOnlySequences"));
        var functions = service.GetProperty("functions").EnumerateArray().Select(function =>
            new FunctionPlan(RequiredString(function, "signature"), function.GetProperty("execute").GetBoolean()))
            .ToArray();
        var allTables = tables.Select(table => table.Name).Concat(ownerOnlyTables).ToArray();
        if (allTables.Distinct(StringComparer.Ordinal).Count() != allTables.Length ||
            tables.Any(table => table.Privileges.Any(privilege => !TablePrivileges.Contains(privilege, StringComparer.Ordinal)) ||
                table.UpdateColumns.Any(column => !IdentifierPattern().IsMatch(column))) ||
            allTables.Any(table => !IdentifierPattern().IsMatch(table)) ||
            ownerOnlySequences.Any(sequence => !IdentifierPattern().IsMatch(sequence)) ||
            functions.Any(function => !FunctionSignaturePattern().IsMatch(function.Signature)))
            throw new InvalidOperationException("The reviewed Projects & Config grant plan contains an invalid object or privilege.");

        return new ServicePlan("projects_config", "agentweaver-id-projects-config", tables,
            ownerOnlyTables, ownerOnlySequences, functions);
    }

    private static string RequiredString(JsonElement element, string property) =>
        element.GetProperty(property).GetString() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"The reviewed Projects & Config grant plan is missing {property}.");

    private static string[] ReadStrings(JsonElement element) =>
        element.EnumerateArray().Select(value => value.GetString()
            ?? throw new InvalidOperationException("The reviewed Projects & Config grant plan contains a null value."))
            .ToArray();

    private static string Quote(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    [GeneratedRegex("^[a-z_][a-z0-9_]*$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("^[a-z_][a-z0-9_]*\\([a-z0-9_ ,]*\\)$")]
    private static partial Regex FunctionSignaturePattern();

    private sealed record TablePlan(string Name, string[] Privileges, string[] UpdateColumns);
    private sealed record FunctionPlan(string Signature, bool Execute);
    private sealed record ServicePlan(
        string Schema,
        string Role,
        TablePlan[] Tables,
        string[] OwnerOnlyTables,
        string[] OwnerOnlySequences,
        FunctionPlan[] Functions);
}
