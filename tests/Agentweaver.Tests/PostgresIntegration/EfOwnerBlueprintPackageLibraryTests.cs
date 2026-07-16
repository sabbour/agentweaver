using System.Security.Cryptography;
using Agentweaver.Api.Infrastructure.Ef;
using Agentweaver.Domain.BlueprintPackages;
using FluentAssertions;

namespace Agentweaver.Tests.PostgresIntegration;

[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class EfOwnerBlueprintPackageLibraryTests(PostgresFixture pg)
{
    [PostgresFact]
    public async Task Persist_ConcurrentSameIdentity_IsCreatedOnceAndIdempotentThereafter()
    {
        var package = Package("race-" + Guid.NewGuid().ToString("N"));
        var first = new EfOwnerBlueprintPackageLibrary(pg.Factory, new Owner("owner-race"));
        var second = new EfOwnerBlueprintPackageLibrary(pg.Factory, new Owner("owner-race"));

        var outcomes = await Task.WhenAll(first.PersistAsync(package), second.PersistAsync(package));

        outcomes.Select(x => x.Disposition).Should().Contain(BlueprintPackagePersistDisposition.Created);
        outcomes.Select(x => x.Disposition).Should().NotContain(BlueprintPackagePersistDisposition.ImmutableConflict);
    }

    [PostgresFact]
    public async Task GetAndDelete_AreAlwaysOwnerScoped()
    {
        var package = Package("private-" + Guid.NewGuid().ToString("N"));
        var ownerA = new EfOwnerBlueprintPackageLibrary(pg.Factory, new Owner("owner-a"));
        var ownerB = new EfOwnerBlueprintPackageLibrary(pg.Factory, new Owner("owner-b"));
        await ownerA.PersistAsync(package);

        (await ownerB.GetVersionAsync(package.PackageId, package.CanonicalVersion)).Should().BeNull();
        (await ownerB.DeletePackageAsync(package.PackageId)).Should().BeFalse();
        (await ownerA.GetVersionAsync(package.PackageId, package.CanonicalVersion)).Should().NotBeNull();
    }

    private static BlueprintPackageWrite Package(string id)
    {
        var raw = new byte[] { 3, 1, 4 };
        var payload = new byte[] { 1, 5, 9 };
        return new(id, "1.0.0", raw, [new("definitions/blueprints/x.json", payload)],
            Digest([2]), Digest(payload), Digest(raw), null, [new("validated")]);
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private sealed record Owner(string OwnerId) : IAuthenticatedOwnerContext;
}
