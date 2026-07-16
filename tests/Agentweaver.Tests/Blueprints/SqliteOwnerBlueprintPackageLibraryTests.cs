using System.Security.Cryptography;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain.BlueprintPackages;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Agentweaver.Tests.Blueprints;

public sealed class SqliteOwnerBlueprintPackageLibraryTests
{
    [Fact]
    public async Task Persist_RoundTripsExactRawBytes_AndIsIdempotent()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteOwnerBlueprintPackageLibrary(testDb.Db, new Owner("owner-a"));
        var package = Package(raw: [0, 255, 13], payload: [8, 0, 9]);

        (await store.PersistAsync(package)).Disposition.Should().Be(BlueprintPackagePersistDisposition.Created);
        (await store.PersistAsync(package)).Disposition.Should().Be(BlueprintPackagePersistDisposition.Idempotent);
        var saved = await store.GetVersionAsync("engineering", "1.0.0");

        saved!.RawManifest.Should().Equal(package.RawManifest);
        saved.Payloads.Single().Bytes.Should().Equal(package.Payloads.Single().Bytes);
        saved.ContentDigest.Should().Be(package.ContentDigest);
        saved.PayloadSetDigest.Should().Be(package.PayloadSetDigest);
        saved.RawManifestSha256.Should().Be(package.RawManifestSha256);
    }

    [Fact]
    public async Task Persist_SameVersionDifferentIdentity_ReturnsImmutableConflict()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteOwnerBlueprintPackageLibrary(testDb.Db, new Owner("owner-a"));
        await store.PersistAsync(Package(payload: [1]));

        var result = await store.PersistAsync(Package(payload: [2]));

        result.Disposition.Should().Be(BlueprintPackagePersistDisposition.ImmutableConflict);
    }

    [Fact]
    public async Task Persist_ConcurrentSameIdentity_IsCreatedOnceAndOtherwiseIdempotent()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var package = Package();
        var first = new SqliteOwnerBlueprintPackageLibrary(testDb.Db, new Owner("owner-a"));
        var second = new SqliteOwnerBlueprintPackageLibrary(testDb.Db, new Owner("owner-a"));

        var outcomes = await Task.WhenAll(first.PersistAsync(package), second.PersistAsync(package));

        outcomes.Select(x => x.Disposition).Should().Contain(BlueprintPackagePersistDisposition.Created);
        outcomes.Select(x => x.Disposition).Should().NotContain(BlueprintPackagePersistDisposition.ImmutableConflict);
    }

    [Fact]
    public async Task Persist_RacingDeleteAfterVersionRead_ReturnsCompleteIdempotentAggregate()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var package = AggregatePackage();
        var seed = new SqliteOwnerBlueprintPackageLibrary(testDb.Db, new Owner("owner-a"));
        await seed.PersistAsync(package);

        var versionRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueAggregateRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persister = new SqliteOwnerBlueprintPackageLibrary(
            testDb.Db,
            new Owner("owner-a"),
            _ =>
            {
                versionRead.TrySetResult();
                return continueAggregateRead.Task;
            });
        var deleter = new SqliteOwnerBlueprintPackageLibrary(testDb.Db, new Owner("owner-a"));

        var persist = persister.PersistAsync(package);
        await versionRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var deleteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delete = Task.Run(async () =>
        {
            deleteStarted.TrySetResult();
            return await deleter.DeletePackageAsync(package.PackageId);
        });
        await deleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        continueAggregateRead.SetResult();

        var result = await persist;
        var deleteFailure = await Record.ExceptionAsync(() => delete);
        if (deleteFailure is not null)
            deleteFailure.Should().BeOfType<SqliteException>().Which.SqliteErrorCode.Should().Be(6);

        result.Disposition.Should().Be(BlueprintPackagePersistDisposition.Idempotent);
        result.Version.Should().NotBeNull();
        AssertComplete(result.Version!, package);
    }

    [Fact]
    public async Task OwnerScope_HidesAndCannotDeleteAnotherOwnersPackage()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var first = new SqliteOwnerBlueprintPackageLibrary(testDb.Db, new Owner("owner-a"));
        var second = new SqliteOwnerBlueprintPackageLibrary(testDb.Db, new Owner("owner-b"));
        await first.PersistAsync(Package());

        (await second.GetVersionAsync("engineering", "1.0.0")).Should().BeNull();
        (await second.ListAsync()).Should().BeEmpty();
        (await second.DeletePackageAsync("engineering")).Should().BeFalse();
        (await first.GetVersionAsync("engineering", "1.0.0")).Should().NotBeNull();
    }

    [Fact]
    public async Task List_OrdersArbitrarilyLargeSemVerWithoutNumericTruncation()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteOwnerBlueprintPackageLibrary(testDb.Db, new Owner("owner-a"));
        var low = new string('9', 300) + ".0.0";
        var high = "1" + new string('0', 300) + ".0.0";
        await store.PersistAsync(Package(version: low, payload: [1]));
        await store.PersistAsync(Package(version: high, payload: [2]));

        var versions = (await store.ListAsync()).Single().Versions;

        versions.Select(x => x.CanonicalVersion).Should().Equal(high, low);
    }

    [Fact]
    public async Task Persist_RejectsCredentialLikeProvenanceBeforeWrite()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteOwnerBlueprintPackageLibrary(testDb.Db, new Owner("owner-a"));
        var package = Package() with { Acquisitions = [new("import", Repository: "https://user:password@example.invalid/repo")] };

        var action = () => store.PersistAsync(package);

        await action.Should().ThrowAsync<ArgumentException>();
        (await store.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Persist_RejectsPayloadBytesThatDoNotMatchTheSuppliedDigestBeforeWrite()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteOwnerBlueprintPackageLibrary(testDb.Db, new Owner("owner-a"));
        var valid = Package(payload: [4, 5, 6]);
        var altered = valid with { Payloads = [new(valid.Payloads.Single().Path, [4, 5, 7])] };

        Action validate = () => BlueprintPackageLibraryLimits.Validate(altered);
        var persist = () => store.PersistAsync(altered);

        validate.Should().Throw<ArgumentException>().WithParameterName(nameof(BlueprintPackageWrite.PayloadSetDigest));
        await persist.Should().ThrowAsync<ArgumentException>();
        (await store.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task GetAndList_RacingDelete_ReturnOnlyCompleteAggregateOrNotFound()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteOwnerBlueprintPackageLibrary(testDb.Db, new Owner("owner-a"));
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var package = Package(packageId: $"race-{attempt}");
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
                AssertCompleteOrNotFound(entry.Versions.Single(), package);
            }
        }
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

    private static BlueprintPackageWrite Package(string version = "1.0.0", byte[]? raw = null, byte[]? payload = null, string packageId = "engineering")
    {
        raw ??= [1, 2, 3];
        payload ??= [4, 5, 6];
        var payloads = new[] { new BlueprintPackagePayload("definitions/blueprints/engineering.json", payload) };
        return new(packageId, version, raw, payloads,
            Digest([9]), BlueprintPackagePayloadSetDigest.Calculate(payloads), Digest(raw), null, [new("validated")]);
    }

    private static BlueprintPackageWrite AggregatePackage() =>
        new(
            "persist-delete-" + Guid.NewGuid().ToString("N"),
            "1.0.0",
            [0, 255, 13],
            [
                new("definitions/blueprints/a.json", [1, 2, 3]),
                new("definitions/blueprints/b.json", [4, 5, 6]),
            ],
            Digest([9]),
            BlueprintPackagePayloadSetDigest.Calculate(
                [
                    new("definitions/blueprints/a.json", [1, 2, 3]),
                    new("definitions/blueprints/b.json", [4, 5, 6]),
                ]),
            Digest([0, 255, 13]),
            Digest([7]),
            [new("validated", "producer-a", "repo-a", "one"), new("imported", "producer-b", "repo-b", "two")]);

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private sealed record Owner(string OwnerId) : IAuthenticatedOwnerContext;
}
