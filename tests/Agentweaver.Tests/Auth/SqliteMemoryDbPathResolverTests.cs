using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Agentweaver.Api.Infrastructure;

namespace Agentweaver.Tests.Auth;

public sealed class SqliteMemoryDbPathResolverTests
{
    [Fact]
    public void Resolve_PreservesLegacyDefault_ForAgentweaverDb()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = Path.Combine("C:\\repo\\data", "agentweaver.db"),
            })
            .Build();

        SqliteMemoryDbPathResolver.Resolve(config)
            .Should().Be(Path.GetFullPath(Path.Combine("C:\\repo\\data", "memory.db")));
    }

    [Fact]
    public void Resolve_UsesPerDatabaseCompanionFile_ForCustomDatabasePaths()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = Path.Combine(Path.GetTempPath(), "agentweaver-entra-123.db"),
            })
            .Build();

        SqliteMemoryDbPathResolver.Resolve(config)
            .Should().Be(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "agentweaver-entra-123.memory.db")));
    }
}
