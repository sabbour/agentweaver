using System.Security.Cryptography;
using Agentweaver.Api.Blueprints;
using Agentweaver.Api.Infrastructure.Ef;
using Agentweaver.Domain.BlueprintPackages;
using Agentweaver.Tests.Blueprints;
using FluentAssertions;
using Npgsql;

namespace Agentweaver.Tests.PostgresIntegration;

[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class EfOwnerBlueprintPackageLibraryTests(PostgresFixture pg)
{
    [Fact]
    public async Task GitHubImport_PersistsValidatedPackageThroughPostgresWithoutSkip()
    {
        var owner = new Owner("owner-github-import-" + Guid.NewGuid().ToString("N"));
        var library = new EfOwnerBlueprintPackageLibrary(pg.Factory, owner);
        var service = new GitHubBlueprintPackageImportService(ImportTestSupport.ValidClient(), library);

        var result = await service.ImportAsync(
            new GitHubBlueprintPackageLocator(
                "octo", "token-service-blueprints", "package", "feature/token-refresh"));
        var stored = await library.GetVersionAsync(result.PackageId, result.CanonicalVersion);

        result.Disposition.Should().Be(BlueprintPackagePersistDisposition.Created);
        stored!.Acquisitions.Should().ContainSingle().Which.RequestedRef.Should().Be("feature/token-refresh");
        stored.Acquisitions.Single().Repository.Should().Be("https://github.com/octo/token-service-blueprints");
    }

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
    public async Task Persist_RacingDeleteAfterVersionRead_ReturnsCompleteIdempotentAggregate()
    {
        var package = AggregatePackage("persist-delete-" + Guid.NewGuid().ToString("N"));
        var seed = new EfOwnerBlueprintPackageLibrary(pg.Factory, new Owner("owner-persist-delete"));
        await seed.PersistAsync(package);

        var versionRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueAggregateRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persister = new EfOwnerBlueprintPackageLibrary(
            pg.Factory,
            new Owner("owner-persist-delete"),
            _ =>
            {
                versionRead.TrySetResult();
                return continueAggregateRead.Task;
            });
        var deleter = new EfOwnerBlueprintPackageLibrary(pg.Factory, new Owner("owner-persist-delete"));

        var persist = persister.PersistAsync(package);
        await versionRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await deleter.DeletePackageAsync(package.PackageId);
        continueAggregateRead.SetResult();

        var result = await persist;

        result.Disposition.Should().Be(BlueprintPackagePersistDisposition.Idempotent);
        result.Version.Should().NotBeNull();
        AssertComplete(result.Version!, package);
    }

    [PostgresFact]
    public async Task Persist_ConcurrentDifferentVersionsForNewPackage_CreatesBothCompleteAggregates()
    {
        var id = "concurrent-" + Guid.NewGuid().ToString("N");
        var first = Package(id, version: "1.0.0", payload: [1, 2, 3]);
        var second = Package(id, version: "2.0.0", payload: [4, 5, 6]);
        var one = new EfOwnerBlueprintPackageLibrary(pg.Factory, new Owner("owner-concurrent"));
        var two = new EfOwnerBlueprintPackageLibrary(pg.Factory, new Owner("owner-concurrent"));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new[]
        {
            AfterRelease(release.Task, () => one.PersistAsync(first)),
            AfterRelease(release.Task, () => two.PersistAsync(second)),
        };

        release.SetResult();
        var outcomes = await Task.WhenAll(writes);

        outcomes.Select(x => x.Disposition).Should().OnlyContain(x => x == BlueprintPackagePersistDisposition.Created);
        var entries = await one.ListAsync();
        entries.Should().ContainSingle();
        entries.Single().Versions.Should().HaveCount(2);
        AssertComplete(entries.Single().Versions.Single(x => x.CanonicalVersion == first.CanonicalVersion), first);
        AssertComplete(entries.Single().Versions.Single(x => x.CanonicalVersion == second.CanonicalVersion), second);
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
    public async Task Persist_RejectsPayloadBytesThatDoNotMatchTheSuppliedDigestBeforeWrite()
    {
        var store = new EfOwnerBlueprintPackageLibrary(pg.Factory, new Owner("owner-payload-digest"));
        var id = "payload-digest-" + Guid.NewGuid().ToString("N");
        var valid = Package(id, payload: [4, 5, 6]);
        var altered = valid with { Payloads = [new(valid.Payloads.Single().Path, [4, 5, 7])] };

        var persist = () => store.PersistAsync(altered);

        await persist.Should().ThrowAsync<ArgumentException>();
        (await store.ListAsync()).Should().BeEmpty();
    }

    [PostgresFact]
    public async Task GetAndList_RacingDelete_ReturnOnlyCompleteAggregateOrNotFound()
    {
        var store = new EfOwnerBlueprintPackageLibrary(pg.Factory, new Owner("owner-read-delete"));
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var package = Package("read-delete-" + Guid.NewGuid().ToString("N"));
            await store.PersistAsync(package);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var get = AfterRelease(release.Task, () => store.GetVersionAsync(package.PackageId, package.CanonicalVersion));
            var list = AfterRelease(release.Task, () => store.ListAsync());
            var delete = AfterRelease(release.Task, () => store.DeletePackageAsync(package.PackageId));

            release.SetResult();
            await Task.WhenAll(get, list, delete);

            AssertCompleteOrNotFound(await get, package);
            var entry = (await list).SingleOrDefault(x => x.PackageId == package.PackageId);
            if (entry is not null)
            {
                entry.Versions.Should().ContainSingle();
                AssertComplete(entry.Versions.Single(), package);
            }
        }
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
    public async Task Persist_MaximumLengthVersion_UsesBoundedPostgresIndexKey()
    {
        var store = new EfOwnerBlueprintPackageLibrary(pg.Factory, new Owner("owner-max-version"));
        var version = new string('9', BlueprintPackageLibraryLimits.MaximumVersionLength - 4) + ".0.0";
        var package = Package("max-version-" + Guid.NewGuid().ToString("N"), version, payload: [7, 8, 9]);

        var persisted = await store.PersistAsync(package);
        var loaded = await store.GetVersionAsync(package.PackageId, version);

        persisted.Disposition.Should().Be(BlueprintPackagePersistDisposition.Created);
        loaded.Should().NotBeNull();
        loaded!.CanonicalVersion.Should().Be(version);
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
        var payloads = new[] { new BlueprintPackagePayload("definitions/blueprints/x.json", payload) };
        return new(id, version, raw, payloads,
            Digest([2]), BlueprintPackagePayloadSetDigest.Calculate(payloads), Digest(raw), null, [new("validated")]);
    }

    private static async Task<T> AfterRelease<T>(Task release, Func<Task<T>> operation)
    {
        await release;
        return await operation();
    }

    private static void AssertCompleteOrNotFound(OwnerBlueprintPackageVersion? saved, BlueprintPackageWrite package)
    {
        if (saved is null) return;
        AssertComplete(saved, package);
    }

    private static void AssertComplete(OwnerBlueprintPackageVersion saved, BlueprintPackageWrite package)
    {
        saved.RawManifest.Should().Equal(package.RawManifest);
        saved.Payloads.Select(x => x.Path).Should().Equal(package.Payloads.Select(x => x.Path));
        foreach (var (actual, expected) in saved.Payloads.Zip(package.Payloads))
            actual.Bytes.Should().Equal(expected.Bytes);
        saved.Acquisitions.Should().Equal(package.Acquisitions);
        saved.ContentDigest.Should().Be(package.ContentDigest);
        saved.PayloadSetDigest.Should().Be(package.PayloadSetDigest);
        saved.RawManifestSha256.Should().Be(package.RawManifestSha256);
        saved.ContainerSha256.Should().Be(package.ContainerSha256);
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static BlueprintPackageWrite AggregatePackage(string id)
    {
        var raw = new byte[] { 0, 255, 13 };
        var payloads = new[]
        {
            new BlueprintPackagePayload("definitions/blueprints/a.json", [1, 2, 3]),
            new BlueprintPackagePayload("definitions/blueprints/b.json", [4, 5, 6]),
        };
        return new(
            id,
            "1.0.0",
            raw,
            payloads,
            Digest([2]),
            BlueprintPackagePayloadSetDigest.Calculate(payloads),
            Digest(raw),
            Digest([7]),
            [new("validated", "producer-a", "repo-a", "one"), new("imported", "producer-b", "repo-b", "two")]);
    }

    private sealed record Owner(string OwnerId) : IAuthenticatedOwnerContext;
}
