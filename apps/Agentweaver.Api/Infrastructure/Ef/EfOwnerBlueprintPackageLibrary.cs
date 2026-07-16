using Agentweaver.Api.Memory;
using Agentweaver.Domain.BlueprintPackages;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agentweaver.Api.Infrastructure.Ef;

/// <summary>PostgreSQL EF implementation of the owner-private immutable Blueprint package library.</summary>
public sealed class EfOwnerBlueprintPackageLibrary(
    IDbContextFactory<MemoryDbContext> factory,
    IAuthenticatedOwnerContext ownerContext) : IOwnerBlueprintPackageLibrary
{
    private readonly IDbContextFactory<MemoryDbContext> _factory = factory;
    private readonly IAuthenticatedOwnerContext _ownerContext = ownerContext;

    public async Task<BlueprintPackagePersistResult> PersistAsync(BlueprintPackageWrite package, CancellationToken ct = default)
    {
        BlueprintPackageLibraryLimits.Validate(package);
        var version = BlueprintPackageLibraryLimits.CanonicalSemanticVersion.Normalize(package.CanonicalVersion);
        var owner = Owner();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var existing = await ReadVersionAsync(db, owner, package.PackageId, version, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return SameIdentity(existing, package)
                ? new(BlueprintPackagePersistDisposition.Idempotent, existing)
                : new(BlueprintPackagePersistDisposition.ImmutableConflict);
        }

        var created = DateTimeOffset.UtcNow;
        if (!await db.BlueprintPackageLibrary.AnyAsync(x => x.OwnerId == owner && x.PackageId == package.PackageId, ct).ConfigureAwait(false))
            db.BlueprintPackageLibrary.Add(new BlueprintPackageLibraryRecord { OwnerId = owner, PackageId = package.PackageId, CreatedAt = created });
        db.BlueprintPackageVersions.Add(new BlueprintPackageVersionRecord
        {
            OwnerId = owner, PackageId = package.PackageId, CanonicalVersion = version,
            ContentDigest = package.ContentDigest, PayloadSetDigest = package.PayloadSetDigest,
            RawManifestSha256 = package.RawManifestSha256, ContainerSha256 = package.ContainerSha256,
            RawManifest = package.RawManifest.ToArray(), CreatedAt = created,
        });
        db.BlueprintPackagePayloads.AddRange(package.Payloads.Select(x => new BlueprintPackagePayloadRecord
        {
            OwnerId = owner, PackageId = package.PackageId, CanonicalVersion = version, Path = x.Path, Bytes = x.Bytes.ToArray(),
        }));
        db.BlueprintPackageAcquisitions.AddRange(package.Acquisitions.Select((x, index) => new BlueprintPackageAcquisitionRecord
        {
            OwnerId = owner, PackageId = package.PackageId, CanonicalVersion = version, Ordinal = index,
            Source = x.Source, Producer = x.Producer, Repository = x.Repository, Revision = x.Revision, AcquiredAt = x.AcquiredAt,
        }));
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return new(BlueprintPackagePersistDisposition.Created, ToVersion(package, version, created));
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            var raced = await GetVersionAsync(package.PackageId, version, ct).ConfigureAwait(false);
            if (raced is not null)
                return SameIdentity(raced, package)
                    ? new(BlueprintPackagePersistDisposition.Idempotent, raced)
                    : new(BlueprintPackagePersistDisposition.ImmutableConflict);
            throw;
        }
    }

    public async Task<OwnerBlueprintPackageVersion?> GetVersionAsync(string packageId, string canonicalVersion, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await ReadVersionAsync(db, Owner(), packageId, BlueprintPackageLibraryLimits.CanonicalSemanticVersion.Normalize(canonicalVersion), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OwnerBlueprintPackageEntry>> ListAsync(CancellationToken ct = default)
    {
        var owner = Owner();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entries = await db.BlueprintPackageLibrary.AsNoTracking().Where(x => x.OwnerId == owner)
            .OrderBy(x => x.PackageId).ToListAsync(ct).ConfigureAwait(false);
        var result = new List<OwnerBlueprintPackageEntry>();
        foreach (var entry in entries)
        {
            var versions = await db.BlueprintPackageVersions.AsNoTracking()
                .Where(x => x.OwnerId == owner && x.PackageId == entry.PackageId).ToListAsync(ct).ConfigureAwait(false);
            var mapped = new List<OwnerBlueprintPackageVersion>();
            foreach (var version in versions)
                mapped.Add((await ReadVersionAsync(db, owner, entry.PackageId, version.CanonicalVersion, ct).ConfigureAwait(false))!);
            result.Add(new(entry.PackageId, entry.CreatedAt, mapped
                .OrderByDescending(x => x.CanonicalVersion, Comparer<string>.Create(BlueprintPackageLibraryLimits.CanonicalSemanticVersion.Compare))
                .ThenByDescending(x => x.CanonicalVersion, StringComparer.Ordinal).ToArray()));
        }
        return result;
    }

    public async Task<bool> DeletePackageAsync(string packageId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.BlueprintPackageLibrary.Where(x => x.OwnerId == Owner() && x.PackageId == packageId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false) > 0;
    }

    private static async Task<OwnerBlueprintPackageVersion?> ReadVersionAsync(MemoryDbContext db, string owner, string packageId, string version, CancellationToken ct)
    {
        var record = await db.BlueprintPackageVersions.AsNoTracking().FirstOrDefaultAsync(x =>
            x.OwnerId == owner && x.PackageId == packageId && x.CanonicalVersion == version, ct).ConfigureAwait(false);
        if (record is null) return null;
        var payloads = await db.BlueprintPackagePayloads.AsNoTracking().Where(x =>
            x.OwnerId == owner && x.PackageId == packageId && x.CanonicalVersion == version).OrderBy(x => x.Path).ToListAsync(ct).ConfigureAwait(false);
        var acquisitions = await db.BlueprintPackageAcquisitions.AsNoTracking().Where(x =>
            x.OwnerId == owner && x.PackageId == packageId && x.CanonicalVersion == version).OrderBy(x => x.Ordinal).ToListAsync(ct).ConfigureAwait(false);
        return new(packageId, version, record.RawManifest.ToArray(),
            payloads.Select(x => new BlueprintPackagePayload(x.Path, x.Bytes.ToArray())).ToArray(),
            record.ContentDigest, record.PayloadSetDigest, record.RawManifestSha256, record.ContainerSha256,
            acquisitions.Select(x => new BlueprintPackageAcquisition(x.Source, x.Producer, x.Repository, x.Revision, x.AcquiredAt)).ToArray(),
            record.CreatedAt);
    }

    private string Owner() => string.IsNullOrWhiteSpace(_ownerContext.OwnerId)
        ? throw new InvalidOperationException("An authenticated owner is required.") : _ownerContext.OwnerId;

    private static bool SameIdentity(OwnerBlueprintPackageVersion existing, BlueprintPackageWrite incoming) =>
        existing.ContentDigest == incoming.ContentDigest && existing.PayloadSetDigest == incoming.PayloadSetDigest &&
        existing.RawManifestSha256 == incoming.RawManifestSha256 && existing.ContainerSha256 == incoming.ContainerSha256;

    private static OwnerBlueprintPackageVersion ToVersion(BlueprintPackageWrite write, string version, DateTimeOffset created) =>
        new(write.PackageId, version, write.RawManifest.ToArray(), write.Payloads.Select(x => new BlueprintPackagePayload(x.Path, x.Bytes.ToArray())).ToArray(),
            write.ContentDigest, write.PayloadSetDigest, write.RawManifestSha256, write.ContainerSha256, write.Acquisitions.ToArray(), created);
}
