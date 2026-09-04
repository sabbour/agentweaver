using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Tests.Auth;

public sealed class UserModelProviderSettingsServiceTests
{
    [Fact]
    public async Task SetByok_WritesAndReadsBackCredentialBeforePersistingMetadata()
    {
        await using var db = await OpenDatabaseAsync();
        var secrets = new MissingReadBackSecretStore();
        var service = new UserModelProviderSettingsService(db, secrets);

        var action = () => service.SetByokAsync("user", Provider(), CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*could not be verified*");
        db.ChangeTracker.Clear();
        (await db.UserModelProviderSettings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SetByok_PersistsOnlyMetadataAndReturnsTheVerifiedProvider()
    {
        await using var db = await OpenDatabaseAsync();
        var service = new UserModelProviderSettingsService(db, new InMemorySecretStore());

        var provider = await service.SetByokAsync("user", Provider(), CancellationToken.None);

        provider.ApiKey.Should().Be("secret");
        var record = await db.UserModelProviderSettings.SingleAsync();
        record.Preference.Should().Be(UserModelProviderPreference.Byok);
        record.ByokCredentialReference.Should().StartWith("user-byok-");
        (await service.GetActiveByokAsync("user")).Should().Be(provider);
    }

    [Fact]
    public async Task SetPreference_RejectsByokUntilAPersonalProviderExists()
    {
        await using var db = await OpenDatabaseAsync();
        var service = new UserModelProviderSettingsService(db, new InMemorySecretStore());

        var action = () => service.SetPreferenceAsync(
            "user", UserModelProviderPreference.Byok, CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Configure a personal provider before selecting it.");
        (await db.UserModelProviderSettings.CountAsync()).Should().Be(0);
    }

    private static ByokProviderConfiguration Provider() =>
        new("", "Personal OpenAI", "openai", "https://api.openai.com/v1", "gpt-test", "secret");

    private static async Task<MemoryDbContext> OpenDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new MemoryDbContext(
            new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class MissingReadBackSecretStore : ISecretStore
    {
        public Task<SecretGetResult> GetSecretAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(SecretGetResult.NotFound);
        public Task<string> SetSecretAsync(
            string key, string value, string? etag = null, CancellationToken ct = default) =>
            Task.FromResult("etag");
        public Task DeleteSecretAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }
}
