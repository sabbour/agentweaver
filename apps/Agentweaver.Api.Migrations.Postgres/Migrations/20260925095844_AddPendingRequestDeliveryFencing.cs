using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingRequestDeliveryFencing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DecisionIdentity",
                table: "PendingRequests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeliveredAt",
                table: "PendingRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryClaimOwner",
                table: "PendingRequests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeliveryClaimedAt",
                table: "PendingRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryKind",
                table: "PendingRequests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryState",
                table: "PendingRequests",
                type: "text",
                nullable: false,
                defaultValue: "waiting");

            migrationBuilder.AddColumn<string>(
                name: "RequestId",
                table: "PendingRequests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponseJson",
                table: "PendingRequests",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingRequests_DeliveryState_DeliveryClaimedAt",
                table: "PendingRequests",
                columns: new[] { "DeliveryState", "DeliveryClaimedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingRequests_RunId_RequestId_DecisionIdentity",
                table: "PendingRequests",
                columns: new[] { "RunId", "RequestId", "DecisionIdentity" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PendingRequests_DeliveryState_DeliveryClaimedAt",
                table: "PendingRequests");

            migrationBuilder.DropIndex(
                name: "IX_PendingRequests_RunId_RequestId_DecisionIdentity",
                table: "PendingRequests");

            migrationBuilder.DropColumn(
                name: "DecisionIdentity",
                table: "PendingRequests");

            migrationBuilder.DropColumn(
                name: "DeliveredAt",
                table: "PendingRequests");

            migrationBuilder.DropColumn(
                name: "DeliveryClaimOwner",
                table: "PendingRequests");

            migrationBuilder.DropColumn(
                name: "DeliveryClaimedAt",
                table: "PendingRequests");

            migrationBuilder.DropColumn(
                name: "DeliveryKind",
                table: "PendingRequests");

            migrationBuilder.DropColumn(
                name: "DeliveryState",
                table: "PendingRequests");

            migrationBuilder.DropColumn(
                name: "RequestId",
                table: "PendingRequests");

            migrationBuilder.DropColumn(
                name: "ResponseJson",
                table: "PendingRequests");
        }
    }
}
