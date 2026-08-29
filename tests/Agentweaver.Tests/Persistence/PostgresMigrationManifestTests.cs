using System.Text.RegularExpressions;
using FluentAssertions;

namespace Agentweaver.Tests.Persistence;

public sealed class PostgresMigrationManifestTests
{
    [Theory]
    [InlineData("api-deployment.yaml")]
    [InlineData("worker-deployment.yaml")]
    public void InitContainer_ForwardsPostgresMigrationsArgument(string fileName)
    {
        var manifest = File.ReadAllText(Path.Combine(RepositoryRoot(), "k8s", "base", fileName));

        var command = Regex.Match(
            manifest,
            @"(?s)- name: migrate-memory-db\s+image:.*?command:\s+(?<command>- /app/efbundle\s+- --verbose\s+- --\s+- --postgres-migrations)\s+env:");

        command.Success.Should().BeTrue($"{fileName} must select the Postgres EF migrations assembly");
        manifest.Should().MatchRegex(
            @"(?s)- name: ConnectionStrings__Postgres\s+valueFrom:\s+secretKeyRef:\s+name: agentweaver-postgres\s+key: connectionstring");
    }

    [Fact]
    public void ReferencePostgresMigrationJob_ForwardsPostgresMigrationsArgument()
    {
        var manifest = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "k8s", "reference", "job-ef-migrate-postgres.yaml"));

        manifest.Should().MatchRegex(
            @"(?s)command:\s+- /app/efbundle\s+- --verbose\s+- --\s+- --postgres-migrations\s+env:");
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
