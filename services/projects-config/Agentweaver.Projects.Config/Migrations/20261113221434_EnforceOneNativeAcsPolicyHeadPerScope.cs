using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class EnforceOneNativeAcsPolicyHeadPerScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Stop, without changing any record, when an older database has more than one head for a scope.
            // The lock blocks policy writes until this migration transaction ends.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    duplicate_heads text;
                BEGIN
                    LOCK TABLE projects_config.native_acs_policy_heads IN SHARE ROW EXCLUSIVE MODE;
                    SELECT string_agg(
                        format('%s/%s (policy IDs: %s)', scope_type, scope_id, policy_ids),
                        '; ' ORDER BY scope_type, scope_id)
                    INTO duplicate_heads
                    FROM (
                        SELECT scope_type, scope_id, string_agg(policy_id, ', ' ORDER BY policy_id) AS policy_ids
                        FROM projects_config.native_acs_policy_heads
                        GROUP BY scope_type, scope_id
                        HAVING COUNT(*) > 1
                    ) AS duplicates;
                    IF duplicate_heads IS NOT NULL THEN
                        RAISE EXCEPTION 'native ACS policy migration stopped: more than one policy head exists for a scope. No record was changed. Use a reviewed recovery plan before retrying. Affected scopes: %', duplicate_heads;
                    END IF;
                END
                $$;
                """);

            migrationBuilder.CreateIndex(
                name: "ux_native_acs_policy_heads_scope",
                schema: "projects_config",
                table: "native_acs_policy_heads",
                columns: new[] { "scope_type", "scope_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_native_acs_policy_heads_scope",
                schema: "projects_config",
                table: "native_acs_policy_heads");
        }
    }
}
