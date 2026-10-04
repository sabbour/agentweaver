using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAddressedMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "addressed_messages",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Sender = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SenderIdentity = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Recipient = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SourceRunId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    TargetRunId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ThreadId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ReplyToId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ReferenceKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ReferenceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ClaimedUntil = table.Column<long>(type: "INTEGER", nullable: true),
                    ClaimOwner = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Fence = table.Column<long>(type: "INTEGER", nullable: false),
                    DeliveredAt = table.Column<long>(type: "INTEGER", nullable: true),
                    AcknowledgedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FailureReason = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_addressed_messages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_addressed_messages_ProjectId_SenderIdentity_IdempotencyKey",
                table: "addressed_messages",
                columns: new[] { "ProjectId", "SenderIdentity", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_addressed_messages_ProjectId_TargetRunId_Status_CreatedAt",
                table: "addressed_messages",
                columns: new[] { "ProjectId", "TargetRunId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_addressed_messages_ProjectId_ThreadId_CreatedAt",
                table: "addressed_messages",
                columns: new[] { "ProjectId", "ThreadId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "addressed_messages");
        }
    }
}
