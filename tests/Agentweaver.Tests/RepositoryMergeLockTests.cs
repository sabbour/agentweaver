using Agentweaver.Api.Git;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests;

public sealed class RepositoryMergeLockTests
{
    [Fact]
    public async Task SymlinkAliases_MapToSameLockIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), $"merge-lock-{Guid.NewGuid():N}");
        var realRepo = Path.Combine(root, "realrepo");
        var aliasRepo = Path.Combine(root, "aliasrepo");
        Directory.CreateDirectory(realRepo);

        try
        {
            Directory.CreateSymbolicLink(aliasRepo, realRepo);
        }
        catch (UnauthorizedAccessException)
        {
            Directory.Delete(root, recursive: true);
            return;
        }
        catch (IOException)
        {
            Directory.Delete(root, recursive: true);
            return;
        }

        try
        {
            var mergeLock = new RepositoryMergeLock(
                new ConfigurationBuilder().Build(),
                NullLogger<RepositoryMergeLock>.Instance);

            using var first = await mergeLock.TryAcquireAsync(realRepo, TimeSpan.FromSeconds(1), CancellationToken.None);
            first.Should().NotBeNull();

            var second = await mergeLock.TryAcquireAsync(aliasRepo, TimeSpan.FromMilliseconds(100), CancellationToken.None);
            second.Should().BeNull("symlink and junction aliases for the same repository must serialize on one lock");
        }
        finally
        {
            try { Directory.Delete(aliasRepo); } catch { }
            Directory.Delete(root, recursive: true);
        }
    }
}
