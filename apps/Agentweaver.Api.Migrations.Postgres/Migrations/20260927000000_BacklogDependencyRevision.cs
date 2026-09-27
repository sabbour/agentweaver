using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations;

[DbContext(typeof(MemoryDbContext))]
[Migration("20260927000000_BacklogDependencyRevision")]
public partial class BacklogDependencyRevision : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>("backlog_graph_revision", "projects", type: "bigint", nullable: false, defaultValue: 0L);
        migrationBuilder.AddColumn<long>("claimed_graph_revision", "backlog_tasks", type: "bigint", nullable: true);
        migrationBuilder.AddColumn<string>("claimed_prerequisites_json", "backlog_tasks", type: "text", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("claimed_prerequisites_json", "backlog_tasks");
        migrationBuilder.DropColumn("claimed_graph_revision", "backlog_tasks");
        migrationBuilder.DropColumn("backlog_graph_revision", "projects");
    }
}
