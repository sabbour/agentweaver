using Agentweaver.Environment;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Agentweaver.Environment.Tests;

public sealed class EnvironmentMigratorTests
{
    [Fact]
    public void MigrationCatalogMatchesTheReviewedOrderedSchemaVersions()
    {
        var options = new DbContextOptionsBuilder<EnvironmentDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time;Username=design_time")
            .Options;

        Assert.Equal(Enumerable.Range(1, 11), EnvironmentMigrator.ValidateMigrationCatalog(options));
    }
}
