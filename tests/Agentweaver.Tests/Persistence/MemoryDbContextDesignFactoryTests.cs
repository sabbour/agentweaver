using Agentweaver.Api;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Tests.Persistence;

public sealed class MemoryDbContextDesignFactoryTests
{
    [Fact]
    public void PostgresMigrations_UsesInjectedPostgresConnectionString()
    {
        const string connectionString =
            "Host=postgres-service;Database=agentweaver;Username=agentweaver;Password=test-only";

        WithEnvironmentVariable("ConnectionStrings__Postgres", connectionString, () =>
        {
            using var context = new MemoryDbContextDesignFactory()
                .CreateDbContext(["--postgres-migrations"]);

            context.Database.ProviderName.Should().Be("Npgsql.EntityFrameworkCore.PostgreSQL");
            context.Database.GetDbConnection().ConnectionString.Should().Be(connectionString);
        });
    }

    [Fact]
    public void DefaultDesignTimeWorkflow_RemainsLocalSqlite()
    {
        using var context = new MemoryDbContextDesignFactory().CreateDbContext([]);

        context.Database.ProviderName.Should().Be("Microsoft.EntityFrameworkCore.Sqlite");
        context.Database.GetDbConnection().ConnectionString.Should().Be("Data Source=agentweaver-design.db");
    }

    [Fact]
    public void PostgresMigrations_WithoutConnectionString_DoesNotFallBackToSqlite()
    {
        WithEnvironmentVariables(
            new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
                ["ConnectionStrings__Postgres"] = null,
                ["ConnectionStrings__MemoryDb"] = null,
                ["Database__ConnectionString"] = null,
            },
            () =>
            {
                var create = () => new MemoryDbContextDesignFactory()
                    .CreateDbContext(["--postgres-migrations"]);

                create.Should().Throw<InvalidOperationException>()
                    .WithMessage("*ConnectionStrings:Postgres*");
            });
    }

    [Fact]
    public void Factory_DoesNotEmbedLocalhostPostgresConfiguration()
    {
        var source = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "apps", "Agentweaver.Api", "MemoryDbContextDesignFactory.cs"));

        source.Should().NotContain("Host=localhost", "production migration configuration must be injected");
        source.Should().Contain("AddEnvironmentVariables");
        source.Should().Contain("AddUserSecrets");
    }

    private static void WithEnvironmentVariable(string name, string value, Action assertion)
        => WithEnvironmentVariables(new Dictionary<string, string?> { [name] = value }, assertion);

    private static void WithEnvironmentVariables(
        IReadOnlyDictionary<string, string?> values,
        Action assertion)
    {
        var previous = values.Keys.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);
        try
        {
            foreach (var (name, value) in values)
                Environment.SetEnvironmentVariable(name, value);

            assertion();
        }
        finally
        {
            foreach (var (name, value) in previous)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "agentweaver.sln")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
