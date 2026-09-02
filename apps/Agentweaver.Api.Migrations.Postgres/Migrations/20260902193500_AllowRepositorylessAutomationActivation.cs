using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations;

[DbContext(typeof(MemoryDbContext))]
[Migration("20260902193500_AllowRepositorylessAutomationActivation")]
public partial class AllowRepositorylessAutomationActivation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<long>(
            name: "repository_id",
            table: "automation_activations",
            type: "bigint",
            nullable: true,
            oldClrType: typeof(long),
            oldType: "bigint");

        migrationBuilder.AlterColumn<long>(
            name: "installation_id",
            table: "automation_activations",
            type: "bigint",
            nullable: true,
            oldClrType: typeof(long),
            oldType: "bigint");

        migrationBuilder.Sql("""
            ALTER TABLE automation_activations
                DROP CONSTRAINT "CK_automation_activations_snapshot_tuple";

            ALTER TABLE automation_activations
                ADD CONSTRAINT "CK_automation_activations_snapshot_tuple"
                CHECK (status <> 0 OR (
                    (
                        (installation_id IS NULL AND repository_id IS NULL AND
                            repository_grant_digest IS NULL)
                        OR
                        (installation_id IS NOT NULL AND installation_id > 0 AND
                            repository_id IS NOT NULL AND repository_id > 0 AND
                            repository_grant_digest IS NOT NULL AND repository_grant_digest <> '')
                    ) AND (
                        (model_provider_source = 1 AND
                            byok_provider_id IS NOT NULL AND byok_provider_id <> '' AND
                            (copilot_binding_id IS NULL OR copilot_binding_id = '') AND
                            (copilot_binding_grant_digest IS NULL OR copilot_binding_grant_digest = ''))
                        OR
                        (model_provider_source <> 1 AND
                            copilot_binding_id IS NOT NULL AND copilot_binding_id <> '' AND
                            copilot_binding_grant_digest IS NOT NULL AND copilot_binding_grant_digest <> '' AND
                            (byok_provider_id IS NULL OR byok_provider_id = ''))
                    )));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Repository-less activation schema changes are forward-only.");
}
