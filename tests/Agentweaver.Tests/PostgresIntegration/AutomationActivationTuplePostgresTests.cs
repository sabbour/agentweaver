using Agentweaver.Tests.Persistence;

namespace Agentweaver.Tests.PostgresIntegration;

[Collection("PostgresIntegration")]
public sealed class AutomationActivationTuplePostgresTests(PostgresFixture fixture)
{
    [Theory]
    [MemberData(
        nameof(AutomationActivationTupleSqliteTests.RepositoryTupleCases),
        MemberType = typeof(AutomationActivationTupleSqliteTests))]
    public async Task Active_activation_requires_complete_or_absent_repository_tuple(
        bool hasInstallation,
        bool hasRepository,
        string? repositoryGrantDigest,
        bool isValid)
    {
        await using var db = await fixture.CreateDbContextAsync();
        await AutomationActivationTupleTestData.AssertInsertAsync(
            db, hasInstallation, hasRepository, repositoryGrantDigest, isValid);
    }
}
