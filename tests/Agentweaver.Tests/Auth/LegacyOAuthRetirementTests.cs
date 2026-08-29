using System.Net;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Tests.Auth;

public sealed class LegacyOAuthRetirementTests
{
    [Theory]
    [InlineData("/api/auth/github/device")]
    [InlineData("/api/auth/github/status")]
    [InlineData("/oauth/authorize")]
    [InlineData("/oauth/token")]
    public async Task LegacyOAuthEndpoints_AreNotMapped(string path)
    {
        await using var factory = new EntraWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient(PlatformRoles.ProjectCreator);

        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SQLiteMigration_RemovesLegacyOAuthTablesAndSelectionDiscriminator()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsAssembly(typeof(Program).Assembly.GetName().Name))
            .Options;
        await using var db = new MemoryDbContext(options);

        await db.Database.MigrateAsync();

        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                tables.Add(reader.GetString(0));
        }

        tables.Should().NotContain([
            "auth_mode_epochs",
            "github_account_link_states",
            "McpAuthorizationCodes",
            "McpClientRegistrations",
            "McpPendingAuthorizations",
            "McpRefreshTokens",
            "McpRevokedJtis",
            "OAuthStates",
            "project_github_identity_overrides",
        ]);

        await using var columnCommand = connection.CreateCommand();
        columnCommand.CommandText = "PRAGMA table_info(github_repository_selection_codes);";
        await using var columnReader = await columnCommand.ExecuteReaderAsync();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await columnReader.ReadAsync())
            columns.Add(columnReader.GetString(1));
        columns.Should().NotContain("credential_kind");
    }
}
