using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agentweaver.Environment.Migrations;

[DbContext(typeof(EnvironmentDbContext))]
[Migration("20261010041000_SandboxBuildTestCommands")]
public sealed class SandboxBuildTestCommands : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_environment_owner_effects_kind",
            schema: EnvironmentDbContext.Schema,
            table: "owner_effects");
        migrationBuilder.AddCheckConstraint(
            name: "ck_environment_owner_effects_kind",
            schema: EnvironmentDbContext.Schema,
            table: "owner_effects",
            sql: "effect_kind IN ('NetworkPolicy', 'WorkspaceVolume', 'BuildTestCommand')");
        migrationBuilder.AddCheckConstraint(
            name: "ck_environment_owner_effects_buildtest_command",
            schema: EnvironmentDbContext.Schema,
            table: "owner_effects",
            sql: """
                effect_kind <> 'BuildTestCommand' OR (
                    operation = 'Execute'
                    AND resource_id IS NOT NULL
                    AND expected_provider_seam = 'Sandbox'
                    AND expected_provider_id IS NOT NULL
                    AND expected_provider_resource_id IS NOT NULL
                    AND target_provider_seam IS NULL
                    AND target_provider_id IS NULL
                    AND target_provider_resource_id IS NULL
                    AND policy_generation IS NULL
                    AND expected_previous_policy_generation IS NULL
                    AND expected_transition_revision IS NULL
                    AND expected_resource_generation IS NULL
                    AND expected_data_generation IS NULL
                    AND target_transition_revision IS NULL
                    AND target_resource_generation IS NULL
                    AND target_data_generation IS NULL
                    AND target_volume_state IS NULL
                    AND specification_json IS NOT NULL
                    AND expected_provider_binding_json IS NOT NULL
                    AND target_provider_binding_json IS NOT NULL
                )
                """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_environment_owner_effects_buildtest_command",
            schema: EnvironmentDbContext.Schema,
            table: "owner_effects");
        migrationBuilder.DropCheckConstraint(
            name: "ck_environment_owner_effects_kind",
            schema: EnvironmentDbContext.Schema,
            table: "owner_effects");
        migrationBuilder.AddCheckConstraint(
            name: "ck_environment_owner_effects_kind",
            schema: EnvironmentDbContext.Schema,
            table: "owner_effects",
            sql: "effect_kind IN ('NetworkPolicy', 'WorkspaceVolume')");
    }
}
