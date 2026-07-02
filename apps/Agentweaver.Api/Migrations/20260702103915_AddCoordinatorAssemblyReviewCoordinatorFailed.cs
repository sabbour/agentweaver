using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCoordinatorAssemblyReviewCoordinatorFailed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CoordinatorFailedAt",
                table: "AssemblyReviews",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CoordinatorFailureReason",
                table: "AssemblyReviews",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoordinatorFailedAt",
                table: "AssemblyReviews");

            migrationBuilder.DropColumn(
                name: "CoordinatorFailureReason",
                table: "AssemblyReviews");
        }
    }
}
