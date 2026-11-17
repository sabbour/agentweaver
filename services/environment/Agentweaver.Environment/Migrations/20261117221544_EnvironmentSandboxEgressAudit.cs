using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Environment.Migrations
{
    /// <inheritdoc />
    public partial class EnvironmentSandboxEgressAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sandbox_egress_flows_unattributed",
                schema: "environment",
                columns: table => new
                {
                    flow_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    host = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    address = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    port = table.Column<int>(type: "integer", nullable: false),
                    verdict = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    first_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    flow_count = table.Column<long>(type: "bigint", nullable: false),
                    workload_ref = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sandbox_egress_flows_unattributed", x => x.flow_id);
                    table.CheckConstraint("ck_environment_sandbox_egress_unattributed_count", "flow_count > 0");
                    table.CheckConstraint("ck_environment_sandbox_egress_unattributed_port", "port BETWEEN 1 AND 65535");
                    table.CheckConstraint("ck_environment_sandbox_egress_unattributed_times", "last_seen >= first_seen");
                    table.CheckConstraint("ck_environment_sandbox_egress_unattributed_verdict", "verdict IN ('Allowed', 'Denied')");
                });

            migrationBuilder.CreateTable(
                name: "sandbox_owner_mappings",
                schema: "environment",
                columns: table => new
                {
                    owner_fingerprint = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    project_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    environment_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sandbox_owner_mappings", x => x.owner_fingerprint);
                    table.CheckConstraint("ck_environment_sandbox_owner_mappings_fingerprint", "owner_fingerprint ~ '^[0-9a-f]{40}$'");
                });

            migrationBuilder.CreateTable(
                name: "sandbox_egress_flows_attributed",
                schema: "environment",
                columns: table => new
                {
                    flow_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    owner_fingerprint = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    host = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    address = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    port = table.Column<int>(type: "integer", nullable: false),
                    verdict = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    first_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    flow_count = table.Column<long>(type: "bigint", nullable: false),
                    workload_ref = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sandbox_egress_flows_attributed", x => x.flow_id);
                    table.CheckConstraint("ck_environment_sandbox_egress_attributed_count", "flow_count > 0");
                    table.CheckConstraint("ck_environment_sandbox_egress_attributed_port", "port BETWEEN 1 AND 65535");
                    table.CheckConstraint("ck_environment_sandbox_egress_attributed_times", "last_seen >= first_seen");
                    table.CheckConstraint("ck_environment_sandbox_egress_attributed_verdict", "verdict IN ('Allowed', 'Denied')");
                    table.ForeignKey(
                        name: "FK_sandbox_egress_flows_attributed_sandbox_owner_mappings_owne~",
                        column: x => x.owner_fingerprint,
                        principalSchema: "environment",
                        principalTable: "sandbox_owner_mappings",
                        principalColumn: "owner_fingerprint",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sandbox_egress_flows_attributed_owner_fingerprint_first_seen",
                schema: "environment",
                table: "sandbox_egress_flows_attributed",
                columns: new[] { "owner_fingerprint", "first_seen" });

            migrationBuilder.CreateIndex(
                name: "IX_sandbox_egress_flows_unattributed_first_seen",
                schema: "environment",
                table: "sandbox_egress_flows_unattributed",
                column: "first_seen");

            migrationBuilder.CreateIndex(
                name: "IX_sandbox_owner_mappings_tenant_id_project_id_run_id_environm~",
                schema: "environment",
                table: "sandbox_owner_mappings",
                columns: new[] { "tenant_id", "project_id", "run_id", "environment_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sandbox_egress_flows_attributed",
                schema: "environment");

            migrationBuilder.DropTable(
                name: "sandbox_egress_flows_unattributed",
                schema: "environment");

            migrationBuilder.DropTable(
                name: "sandbox_owner_mappings",
                schema: "environment");
        }
    }
}
