using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddRunModelProviderSnapshotOwnersPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "run_model_provider_snapshot_owners",
                columns: table => new
                {
                    run_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    secret_reference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_run_model_provider_snapshot_owners", x => x.run_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "run_model_provider_snapshot_owners");
        }
    }
}
