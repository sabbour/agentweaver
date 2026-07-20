using System.Globalization;
using Agentweaver.Domain.BlueprintPackages;
using Microsoft.Data.Sqlite;

namespace Agentweaver.Api.Infrastructure;

/// <summary>SQLite implementation of the owner-private immutable Blueprint package library.</summary>
public sealed class SqliteOwnerBlueprintPackageLibrary : IOwnerBlueprintPackageLibrary
{
    private readonly SqliteDb _db;
    private readonly IAuthenticatedOwnerContext _ownerContext;
    private readonly Func<CancellationToken, Task>? _afterVersionRead;

    public SqliteOwnerBlueprintPackageLibrary(SqliteDb db, IAuthenticatedOwnerContext ownerContext)
        : this(db, ownerContext, null)
    {
    }

    internal SqliteOwnerBlueprintPackageLibrary(
        SqliteDb db,
        IAuthenticatedOwnerContext ownerContext,
        Func<CancellationToken, Task>? afterVersionRead)
    {
        _db = db;
        _ownerContext = ownerContext;
        _afterVersionRead = afterVersionRead;
    }

    public async Task<BlueprintPackagePersistResult> PersistAsync(BlueprintPackageWrite package, CancellationToken ct = default)
    {
        BlueprintPackageLibraryLimits.Validate(package);
        var version = BlueprintPackageLibraryLimits.CanonicalSemanticVersion.Normalize(package.CanonicalVersion);
        var owner = Owner();
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        try
        {
            var existing = await ReadVersionAsync(connection, (SqliteTransaction)transaction, owner, package.PackageId, version, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                return SameIdentity(existing, package)
                    ? new(BlueprintPackagePersistDisposition.Idempotent, existing)
                    : new(BlueprintPackagePersistDisposition.ImmutableConflict);
            }

            await ExecuteAsync(connection, (SqliteTransaction)transaction,
                "INSERT INTO blueprint_package_library (owner_id, package_id, created_at) VALUES ($owner, $package, $created) ON CONFLICT DO NOTHING;",
                [("$owner", owner), ("$package", package.PackageId), ("$created", Timestamp(now))], ct).ConfigureAwait(false);
            await ExecuteAsync(connection, (SqliteTransaction)transaction,
                """
                INSERT INTO blueprint_package_versions (owner_id, package_id, canonical_version, content_digest,
                    payload_set_digest, raw_manifest_sha256, container_sha256, raw_manifest, created_at)
                VALUES ($owner, $package, $version, $content, $payload, $rawDigest, $container, $raw, $created);
                """,
                [("$owner", owner), ("$package", package.PackageId), ("$version", version), ("$content", package.ContentDigest),
                    ("$payload", package.PayloadSetDigest), ("$rawDigest", package.RawManifestSha256),
                    ("$container", package.ContainerSha256), ("$raw", package.RawManifest), ("$created", Timestamp(now))], ct).ConfigureAwait(false);
            foreach (var payload in package.Payloads)
                await ExecuteAsync(connection, (SqliteTransaction)transaction,
                    "INSERT INTO blueprint_package_payloads (owner_id, package_id, canonical_version, path, bytes) VALUES ($owner, $package, $version, $path, $bytes);",
                    [("$owner", owner), ("$package", package.PackageId), ("$version", version), ("$path", payload.Path), ("$bytes", payload.Bytes)], ct).ConfigureAwait(false);
            for (var ordinal = 0; ordinal < package.Acquisitions.Count; ordinal++)
            {
                var source = package.Acquisitions[ordinal];
                await ExecuteAsync(connection, (SqliteTransaction)transaction,
                    """
                    INSERT INTO blueprint_package_acquisitions (owner_id, package_id, canonical_version, ordinal, source, producer, repository, revision, acquired_at, requested_ref)
                    VALUES ($owner, $package, $version, $ordinal, $source, $producer, $repository, $revision, $acquired, $requestedRef);
                    """,
                    [("$owner", owner), ("$package", package.PackageId), ("$version", version), ("$ordinal", ordinal),
                        ("$source", source.Source), ("$producer", source.Producer), ("$repository", source.Repository),
                        ("$revision", source.Revision), ("$acquired", source.AcquiredAt is null ? null : Timestamp(source.AcquiredAt.Value)),
                        ("$requestedRef", source.RequestedRef)], ct).ConfigureAwait(false);
            }
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return new(BlueprintPackagePersistDisposition.Created, ToVersion(package, version, now));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
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
        var owner = Owner();
        var version = BlueprintPackageLibraryLimits.CanonicalSemanticVersion.Normalize(canonicalVersion);
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var result = await ReadVersionAsync(connection, transaction, owner, packageId, version, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<OwnerBlueprintPackageEntry>> ListAsync(CancellationToken ct = default)
    {
        var owner = Owner();
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT package_id, created_at FROM blueprint_package_library WHERE owner_id = $owner ORDER BY package_id;";
        command.Parameters.AddWithValue("$owner", owner);
        var keys = new List<(string PackageId, DateTimeOffset CreatedAt)>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            keys.Add((reader.GetString(0), ParseTimestamp(reader.GetString(1))));
        await reader.DisposeAsync().ConfigureAwait(false);
        var entries = new List<OwnerBlueprintPackageEntry>();
        foreach (var (packageId, createdAt) in keys)
            entries.Add(new(packageId, createdAt, await ReadVersionsAsync(connection, transaction, owner, packageId, ct).ConfigureAwait(false)));
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return entries;
    }

    public async Task<bool> DeletePackageAsync(string packageId, CancellationToken ct = default)
    {
        var owner = Owner();
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM blueprint_package_library WHERE owner_id = $owner AND package_id = $package;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$package", packageId);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    private async Task<IReadOnlyList<OwnerBlueprintPackageVersion>> ReadVersionsAsync(SqliteConnection connection, SqliteTransaction transaction, string owner, string packageId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT canonical_version FROM blueprint_package_versions WHERE owner_id = $owner AND package_id = $package;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$package", packageId);
        var keys = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) keys.Add(reader.GetString(0));
        var versions = new List<OwnerBlueprintPackageVersion>();
        foreach (var key in keys)
            versions.Add((await ReadVersionAsync(connection, transaction, owner, packageId, key, ct).ConfigureAwait(false))!);
        return versions.OrderByDescending(x => x.CanonicalVersion, Comparer<string>.Create(BlueprintPackageLibraryLimits.CanonicalSemanticVersion.Compare))
            .ThenByDescending(x => x.CanonicalVersion, StringComparer.Ordinal).ToArray();
    }

    private async Task<OwnerBlueprintPackageVersion?> ReadVersionAsync(SqliteConnection connection, SqliteTransaction? transaction, string owner, string packageId, string version, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT content_digest, payload_set_digest, raw_manifest_sha256, container_sha256, raw_manifest, created_at
            FROM blueprint_package_versions WHERE owner_id = $owner AND package_id = $package AND canonical_version = $version;
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$package", packageId);
        command.Parameters.AddWithValue("$version", version);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        var content = reader.GetString(0);
        var payloadDigest = reader.GetString(1);
        var rawDigest = reader.GetString(2);
        var container = reader.IsDBNull(3) ? null : reader.GetString(3);
        var raw = reader.GetFieldValue<byte[]>(4).ToArray();
        var created = ParseTimestamp(reader.GetString(5));
        await reader.DisposeAsync().ConfigureAwait(false);
        if (_afterVersionRead is not null)
            await _afterVersionRead(ct).ConfigureAwait(false);
        var payloads = await ReadPayloadsAsync(connection, transaction, owner, packageId, version, ct).ConfigureAwait(false);
        var acquisitions = await ReadAcquisitionsAsync(connection, transaction, owner, packageId, version, ct).ConfigureAwait(false);
        return new(packageId, version, raw, payloads, content, payloadDigest, rawDigest, container, acquisitions, created);
    }

    private static async Task<IReadOnlyList<BlueprintPackagePayload>> ReadPayloadsAsync(SqliteConnection connection, SqliteTransaction? transaction, string owner, string package, string version, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT path, bytes FROM blueprint_package_payloads WHERE owner_id=$owner AND package_id=$package AND canonical_version=$version ORDER BY path;";
        command.Parameters.AddWithValue("$owner", owner); command.Parameters.AddWithValue("$package", package); command.Parameters.AddWithValue("$version", version);
        var result = new List<BlueprintPackagePayload>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) result.Add(new(reader.GetString(0), reader.GetFieldValue<byte[]>(1).ToArray()));
        return result;
    }

    private static async Task<IReadOnlyList<BlueprintPackageAcquisition>> ReadAcquisitionsAsync(SqliteConnection connection, SqliteTransaction? transaction, string owner, string package, string version, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT source, producer, repository, revision, acquired_at, requested_ref FROM blueprint_package_acquisitions WHERE owner_id=$owner AND package_id=$package AND canonical_version=$version ORDER BY ordinal;";
        command.Parameters.AddWithValue("$owner", owner); command.Parameters.AddWithValue("$package", package); command.Parameters.AddWithValue("$version", version);
        var result = new List<BlueprintPackageAcquisition>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : ParseTimestamp(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        return result;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, (string Name, object? Value)[] parameters, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private string Owner() => string.IsNullOrWhiteSpace(_ownerContext.OwnerId)
        ? throw new InvalidOperationException("An authenticated owner is required.") : _ownerContext.OwnerId;

    private static bool SameIdentity(OwnerBlueprintPackageVersion existing, BlueprintPackageWrite incoming) =>
        existing.ContentDigest == incoming.ContentDigest && existing.PayloadSetDigest == incoming.PayloadSetDigest &&
        existing.RawManifestSha256 == incoming.RawManifestSha256 && existing.ContainerSha256 == incoming.ContainerSha256;

    private static OwnerBlueprintPackageVersion ToVersion(BlueprintPackageWrite write, string version, DateTimeOffset now) =>
        new(write.PackageId, version, write.RawManifest.ToArray(), write.Payloads.Select(x => new BlueprintPackagePayload(x.Path, x.Bytes.ToArray())).ToArray(),
            write.ContentDigest, write.PayloadSetDigest, write.RawManifestSha256, write.ContainerSha256, write.Acquisitions.ToArray(), now);

    private static string Timestamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
