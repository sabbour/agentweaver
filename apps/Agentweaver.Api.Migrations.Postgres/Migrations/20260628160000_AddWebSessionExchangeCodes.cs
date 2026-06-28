using System;
using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <summary>
    /// Adds the replica-safe web sign-in one-time code exchange table
    /// (<c>WebSessionExchangeCodes</c>). Backs the exchange-code broker with Postgres so that the
    /// POST <c>/api/auth/session/exchange</c> can land on ANY replica and still redeem a code
    /// issued by a DIFFERENT replica — fixing the ~50% redirect loop at <c>replicas:2</c> with no
    /// session affinity. At-most-once redemption is enforced across replicas via a conditional
    /// <c>ExecuteDeleteAsync</c> on <c>Code</c> (string PK, no identity). ExpiresAt index supports
    /// opportunistic purge of expired rows.
    /// </summary>
    [DbContext(typeof(MemoryDbContext))]
    [Migration("20260628160000_AddWebSessionExchangeCodes")]
    public partial class AddWebSessionExchangeCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WebSessionExchangeCodes",
                columns: table => new
                {
                    Code = table.Column<string>(nullable: false),
                    AccessToken = table.Column<string>(nullable: false),
                    Login = table.Column<string>(nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_WebSessionExchangeCodes", x => x.Code));

            migrationBuilder.CreateIndex("IX_WebSessionExchangeCodes_ExpiresAt", "WebSessionExchangeCodes", "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable("WebSessionExchangeCodes");
        }
    }
}
