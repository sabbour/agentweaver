using System.Security.Cryptography;
using Agentweaver.Api.Infrastructure.Ef;
using Agentweaver.Domain.BlueprintPackages;
using FluentAssertions;
using Npgsql;

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

    [PostgresFact]
    public async Task Persist_IsIdempotentForSameIdentity_AndConflictsForDifferentIdentity()
    {
        var store = new EfOwnerBlueprintPackageLibrary(pg.Factory, new Owner("owner-identity"));
        var id = "identity-" + Guid.NewGuid().ToString("N");
        var initial = Package(id, payload: [1, 5, 9]);

        (await store.PersistAsync(initial)).Disposition.Should().Be(BlueprintPackagePersistDisposition.Created);
        (await store.PersistAsync(initial)).Disposition.Should().Be(BlueprintPackagePersistDisposition.Idempotent);
        (await store.PersistAsync(Package(id, payload: [2, 6, 5]))).Disposition.Should().Be(BlueprintPackagePersistDisposition.ImmutableConflict);
    }

    [PostgresFact]
    public async Task Persist_RawBytesRoundTrip_AndInvalidInputLeavesNoPartialRows()
    {
        var store = new EfOwnerBlueprintPackageLibrary(pg.Factory, new Owner("owner-raw"));
        var id = "raw-" + Guid.NewGuid().ToString("N");
        var invalid = Package(id, raw: [0, 255, 13]) with { RawManifestSha256 = Digest([1]) };

        var invalidWrite = () => store.PersistAsync(invalid);
        await invalidWrite.Should().ThrowAsync<ArgumentException>();
        (await store.ListAsync()).Should().BeEmpty();

        var valid = Package(id, raw: [0, 255, 13], payload: [9, 0, 8]);
        await store.PersistAsync(valid);
        var saved = await store.GetVersionAsync(id, "1.0.0");
        saved!.RawManifest.Should().Equal(valid.RawManifest);
        saved.Payloads.Single().Bytes.Should().Equal(valid.Payloads.Single().Bytes);
    }

    [PostgresFact]
    public async Task List_OrdersLargeSemVerWithoutNumericTruncation()
    {
        var store = new EfOwnerBlueprintPackageLibrary(pg.Factory, new Owner("owner-semver"));
        var id = "semver-" + Guid.NewGuid().ToString("N");
        var lower = new string('9', 300) + ".0.0";
        var higher = "1" + new string('0', 300) + ".0.0";
        await store.PersistAsync(Package(id, version: lower, payload: [1]));
        await store.PersistAsync(Package(id, version: higher, payload: [2]));

        (await store.ListAsync()).Single().Versions.Select(x => x.CanonicalVersion).Should().Equal(higher, lower);
    }

    [PostgresFact]
    public async Task Migration_CreatesAllOwnerLibraryTables()
    {
        var expected = new[]
        {
            "blueprint_package_library", "blueprint_package_versions",
            "blueprint_package_payloads", "blueprint_package_acquisitions",
        };
        await using var connection = new NpgsqlConnection(pg.ConnectionString);
        await connection.OpenAsync();
        foreach (var table in expected)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = $1;";
            command.Parameters.AddWithValue(table);
            ((long)(await command.ExecuteScalarAsync())!).Should().Be(1);
        }
    }

    private static BlueprintPackageWrite Package(string id, string version = "1.0.0", byte[]? raw = null, byte[]? payload = null)
    {
        raw ??= [3, 1, 4];
        payload ??= [1, 5, 9];
        return new(id, version, raw, [new("definitions/blueprints/x.json", payload)],
            Digest([2]), Digest(payload), Digest(raw), null, [new("validated")]);
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private sealed record Owner(string OwnerId) : IAuthenticatedOwnerContext;
}
