using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations;

[DbContext(typeof(MemoryDbContext))]
[Migration("20260716105000_AddOwnerBlueprintPackageLibrary")]
public partial class AddOwnerBlueprintPackageLibrary : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "blueprint_package_library",
            columns: table => new
            {
                owner_id = table.Column<string>(nullable: false),
                package_id = table.Column<string>(nullable: false),
                created_at = table.Column<DateTimeOffset>(nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_blueprint_package_library", x => new { x.owner_id, x.package_id }));
        migrationBuilder.CreateTable(
            name: "blueprint_package_versions",
            columns: table => new
            {
                owner_id = table.Column<string>(nullable: false),
                package_id = table.Column<string>(nullable: false),
                canonical_version = table.Column<string>(nullable: false),
                content_digest = table.Column<string>(nullable: false),
                payload_set_digest = table.Column<string>(nullable: false),
                raw_manifest_sha256 = table.Column<string>(nullable: false),
                container_sha256 = table.Column<string>(nullable: true),
                raw_manifest = table.Column<byte[]>(nullable: false),
                created_at = table.Column<DateTimeOffset>(nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_blueprint_package_versions", x => new { x.owner_id, x.package_id, x.canonical_version });
                table.ForeignKey("FK_blueprint_package_versions_blueprint_package_library_owner_id_package_id",
                    x => new { x.owner_id, x.package_id }, "blueprint_package_library",
                    new[] { "owner_id", "package_id" }, onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateTable(
            name: "blueprint_package_payloads",
            columns: table => new
            {
                owner_id = table.Column<string>(nullable: false),
                package_id = table.Column<string>(nullable: false),
                canonical_version = table.Column<string>(nullable: false),
                path = table.Column<string>(nullable: false),
                bytes = table.Column<byte[]>(nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_blueprint_package_payloads", x => new { x.owner_id, x.package_id, x.canonical_version, x.path });
                table.ForeignKey("FK_blueprint_package_payloads_blueprint_package_versions_owner_id_package_id_canonical_version",
                    x => new { x.owner_id, x.package_id, x.canonical_version }, "blueprint_package_versions",
                    new[] { "owner_id", "package_id", "canonical_version" }, onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateTable(
            name: "blueprint_package_acquisitions",
            columns: table => new
            {
                owner_id = table.Column<string>(nullable: false),
                package_id = table.Column<string>(nullable: false),
                canonical_version = table.Column<string>(nullable: false),
                ordinal = table.Column<int>(nullable: false),
                source = table.Column<string>(nullable: false),
                producer = table.Column<string>(nullable: true),
                repository = table.Column<string>(nullable: true),
                revision = table.Column<string>(nullable: true),
                acquired_at = table.Column<DateTimeOffset>(nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_blueprint_package_acquisitions", x => new { x.owner_id, x.package_id, x.canonical_version, x.ordinal });
                table.ForeignKey("FK_blueprint_package_acquisitions_blueprint_package_versions_owner_id_package_id_canonical_version",
                    x => new { x.owner_id, x.package_id, x.canonical_version }, "blueprint_package_versions",
                    new[] { "owner_id", "package_id", "canonical_version" }, onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.Sql(
            """
            CREATE FUNCTION reject_blueprint_package_update() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'blueprint package records are immutable'; END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER trg_blueprint_package_versions_no_update BEFORE UPDATE ON blueprint_package_versions
            FOR EACH ROW EXECUTE FUNCTION reject_blueprint_package_update();
            CREATE TRIGGER trg_blueprint_package_payloads_no_update BEFORE UPDATE ON blueprint_package_payloads
            FOR EACH ROW EXECUTE FUNCTION reject_blueprint_package_update();
            CREATE TRIGGER trg_blueprint_package_acquisitions_no_update BEFORE UPDATE ON blueprint_package_acquisitions
            FOR EACH ROW EXECUTE FUNCTION reject_blueprint_package_update();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP TRIGGER IF EXISTS trg_blueprint_package_acquisitions_no_update ON blueprint_package_acquisitions;
            DROP TRIGGER IF EXISTS trg_blueprint_package_payloads_no_update ON blueprint_package_payloads;
            DROP TRIGGER IF EXISTS trg_blueprint_package_versions_no_update ON blueprint_package_versions;
            DROP FUNCTION IF EXISTS reject_blueprint_package_update();
            """);
        migrationBuilder.DropTable(name: "blueprint_package_acquisitions");
        migrationBuilder.DropTable(name: "blueprint_package_payloads");
        migrationBuilder.DropTable(name: "blueprint_package_versions");
        migrationBuilder.DropTable(name: "blueprint_package_library");
    }
}
