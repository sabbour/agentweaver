using System.Globalization;
using Agentweaver.Api.Memory;
using Agentweaver.Domain.BlueprintPackages;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Tools;

/// <summary>
/// Migrates data from the two SQLite databases (agentweaver.db and memory.db) into Postgres
/// (or any non-SQLite MemoryDbContext provider) via EF Core.
///
/// Invoke via the <c>--migrate-data</c> CLI flag or call <see cref="RunAsync"/> directly.
/// Operations are idempotent: rows that already exist (by primary key) are skipped.
/// </summary>
public sealed class SqliteToPostgresMigrator
{
    private readonly IDbContextFactory<MemoryDbContext> _factory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SqliteToPostgresMigrator> _logger;
    private readonly Func<CancellationToken, Task>? _beforeTwoAppCommit;

    public SqliteToPostgresMigrator(
        IDbContextFactory<MemoryDbContext> factory,
        IConfiguration configuration,
        ILogger<SqliteToPostgresMigrator> logger,
        Func<CancellationToken, Task>? beforeTwoAppCommit = null)
    {
        _factory = factory;
        _configuration = configuration;
        _logger = logger;
        _beforeTwoAppCommit = beforeTwoAppCommit;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var agentweaverDbPath = GetSqlitePath("Database:Path", "agentweaver.db");
        var memoryDbPath = Infrastructure.SqliteMemoryDbPathResolver.Resolve(_configuration);

        _logger.LogInformation("Starting SQLite → Postgres migration");
        _logger.LogInformation("  agentweaver.db: {Path}", agentweaverDbPath);
        _logger.LogInformation("  memory.db:      {Path}", memoryDbPath);

        await using var db = await _factory.CreateDbContextAsync(ct);

        // Migrate agentweaver.db tables
        await MigrateAgentweaverDbAsync(agentweaverDbPath, db, ct);

        await MigrateTwoAppRecordsAsync(memoryDbPath, db, ct);

        _logger.LogInformation("Migration complete.");
    }

    private async Task MigrateTwoAppRecordsAsync(string memoryDbPath, MemoryDbContext destination, CancellationToken ct)
    {
        if (!File.Exists(memoryDbPath))
            return;

        var sourceOptions = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(
                $"Data Source={memoryDbPath}",
                sqlite => sqlite.MigrationsAssembly(typeof(SqliteToPostgresMigrator).Assembly.GetName().Name))
            .Options;
        await using var source = new MemoryDbContext(sourceOptions);
        await PrepareTwoAppSourceSchemaAsync(source, ct).ConfigureAwait(false);

        List<GitHubAuthorizationRecord> authorizations;
        List<GitHubInstallationRecord> installations;
        List<GitHubRepositoryGrantRecord> grants;
        List<ProjectCopilotBindingRecord> bindings;
        List<AutomationActivationRecord> activations;
        List<AutomationInvocationRecord> invocations;
        List<GitHubLifecycleDeliveryRecord> lifecycleDeliveries;
        List<RunGitHubIdentitySnapshotRecord> snapshots;
        List<RunGitHubCapabilitySnapshotRecord> capabilitySnapshots;
        List<GitHubInteractiveBrowseAuthorityRecord> browseAuthorities;
        List<GitHubBrowseSelectionRecord> browseSelections;
        List<GitHubAppAuthorizationRecord> appAuthorizations;
        List<GitHubAuditRecord> audits;
        try
        {
            authorizations = await source.GitHubAuthorizations.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
            installations = await source.GitHubInstallations.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
            grants = await source.GitHubRepositoryGrants.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
            bindings = await source.ProjectCopilotBindings.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
            activations = await source.AutomationActivations.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
            invocations = await source.AutomationInvocations.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
            lifecycleDeliveries = await source.GitHubLifecycleDeliveries.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
            snapshots = await source.RunGitHubIdentitySnapshots.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
            capabilitySnapshots = await HasTableAsync(source, "run_github_capability_snapshots", ct).ConfigureAwait(false)
                ? await source.RunGitHubCapabilitySnapshots.AsNoTracking().ToListAsync(ct).ConfigureAwait(false)
                : [];
            browseAuthorities = await HasTableAsync(source, "github_interactive_browse_authorities", ct).ConfigureAwait(false)
                ? await source.GitHubInteractiveBrowseAuthorities.AsNoTracking().ToListAsync(ct).ConfigureAwait(false)
                : [];
            browseSelections = await HasTableAsync(source, "github_browse_selections", ct).ConfigureAwait(false)
                ? await source.GitHubBrowseSelections.AsNoTracking().ToListAsync(ct).ConfigureAwait(false)
                : [];
            appAuthorizations = await source.GitHubAppAuthorizations.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
            audits = await source.GitHubAuditRecords.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 &&
                                        ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("SQLite source predates two-App persistence; no two-App records to migrate.");
            return;
        }

        if (authorizations.Count + installations.Count + grants.Count + bindings.Count + activations.Count +
            invocations.Count + lifecycleDeliveries.Count + snapshots.Count + capabilitySnapshots.Count +
            browseAuthorities.Count + browseSelections.Count + appAuthorizations.Count + audits.Count == 0)
            return;

        var projectIds = authorizations.Where(x => x.ProjectId is not null).Select(x => x.ProjectId!)
            .Concat(installations.Where(x => x.ProjectId is not null).Select(x => x.ProjectId!))
            .Concat(grants.Select(x => x.ProjectId))
            .Concat(bindings.Select(x => x.ProjectId))
            .Concat(activations.Select(x => x.ProjectId))
            .Concat(invocations.Select(x => x.ProjectId))
            .Concat(snapshots.Select(x => x.ProjectId))
            .Concat(capabilitySnapshots.Select(x => x.ProjectId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var targetProjectIds = await destination.Projects.AsNoTracking()
            .Where(x => projectIds.Contains(x.ProjectId))
            .Select(x => x.ProjectId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (targetProjectIds.Count != projectIds.Length)
            throw new InvalidOperationException(
                "Two-App persistence transfer aborted: a project is missing from the destination, so no binding can become usable partially.");

        await using var transaction = await destination.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var item in authorizations)
                if (!await destination.GitHubAuthorizations.AnyAsync(x => x.State == item.State, ct).ConfigureAwait(false))
                    destination.GitHubAuthorizations.Add(item);
            foreach (var item in installations)
                if (!await destination.GitHubInstallations.AnyAsync(x => x.InstallationId == item.InstallationId, ct).ConfigureAwait(false))
                    destination.GitHubInstallations.Add(item);
            foreach (var item in grants)
                if (!await destination.GitHubRepositoryGrants.AnyAsync(x => x.InstallationId == item.InstallationId && x.RepositoryId == item.RepositoryId, ct).ConfigureAwait(false))
                    destination.GitHubRepositoryGrants.Add(item);
            foreach (var item in bindings)
                if (!await destination.ProjectCopilotBindings.AnyAsync(x => x.Id == item.Id, ct).ConfigureAwait(false))
                    destination.ProjectCopilotBindings.Add(item);
            foreach (var item in activations)
                if (!await destination.AutomationActivations.AnyAsync(x => x.Id == item.Id, ct).ConfigureAwait(false))
                    destination.AutomationActivations.Add(item);
            foreach (var item in invocations)
                if (!await destination.AutomationInvocations.AnyAsync(x => x.Id == item.Id, ct).ConfigureAwait(false))
                    destination.AutomationInvocations.Add(item);
            foreach (var item in lifecycleDeliveries)
                if (!await destination.GitHubLifecycleDeliveries.AnyAsync(x => x.DeliveryId == item.DeliveryId, ct).ConfigureAwait(false))
                    destination.GitHubLifecycleDeliveries.Add(item);
            foreach (var item in snapshots)
                if (!await destination.RunGitHubIdentitySnapshots.AnyAsync(x => x.RunId == item.RunId, ct).ConfigureAwait(false))
                    destination.RunGitHubIdentitySnapshots.Add(item);
            foreach (var item in capabilitySnapshots)
            {
                var byReference = await destination.RunGitHubCapabilitySnapshots.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.SnapshotRef == item.SnapshotRef, ct).ConfigureAwait(false);
                var byRunPurpose = await destination.RunGitHubCapabilitySnapshots.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.RunId == item.RunId && x.Purpose == item.Purpose, ct).ConfigureAwait(false);
                if (byReference is null && byRunPurpose is null)
                {
                    destination.RunGitHubCapabilitySnapshots.Add(item);
                    continue;
                }
                if (!CapabilitySnapshotsMatch(item, byReference) || !CapabilitySnapshotsMatch(item, byRunPurpose))
                    throw new InvalidOperationException(
                        "Two-App persistence transfer aborted: immutable capability snapshot conflict.");
            }
            foreach (var item in browseAuthorities)
            {
                var existing = await destination.GitHubInteractiveBrowseAuthorities.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.AuthorityRef == item.AuthorityRef, ct).ConfigureAwait(false);
                if (existing is null)
                    destination.GitHubInteractiveBrowseAuthorities.Add(item);
                else if (!BrowseAuthoritiesMatch(item, existing))
                    throw new InvalidOperationException(
                        "Two-App persistence transfer aborted: browse-authority conflict.");
            }
            foreach (var item in browseSelections)
            {
                var existing = await destination.GitHubBrowseSelections.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.SelectionRef == item.SelectionRef, ct).ConfigureAwait(false);
                if (existing is null)
                    destination.GitHubBrowseSelections.Add(item);
                else if (!BrowseSelectionsMatch(item, existing))
                    throw new InvalidOperationException(
                        "Two-App persistence transfer aborted: browse-selection conflict.");
            }
            foreach (var item in appAuthorizations)
                if (!await destination.GitHubAppAuthorizations.AnyAsync(x => x.Id == item.Id, ct).ConfigureAwait(false))
                    destination.GitHubAppAuthorizations.Add(item);
            foreach (var item in audits)
                if (!await destination.GitHubAuditRecords.AnyAsync(x => x.Id == item.Id, ct).ConfigureAwait(false))
                    destination.GitHubAuditRecords.Add(item);

            await destination.SaveChangesAsync(ct).ConfigureAwait(false);
            if (destination.Database.IsNpgsql() && audits.Count > 0)
            {
                await destination.Database.ExecuteSqlRawAsync(
                    "SELECT setval(pg_get_serial_sequence('github_audit_records', 'id'), " +
                    "(SELECT COALESCE(MAX(id), 1) FROM github_audit_records), true);", ct)
                    .ConfigureAwait(false);
            }

            if (_beforeTwoAppCommit is not null)
                await _beforeTwoAppCommit(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static bool CapabilitySnapshotsMatch(
        RunGitHubCapabilitySnapshotRecord source,
        RunGitHubCapabilitySnapshotRecord? destination) =>
        destination is not null &&
        source.SnapshotRef == destination.SnapshotRef &&
        source.RunId == destination.RunId &&
        source.Purpose == destination.Purpose &&
        source.AppKind == destination.AppKind &&
        source.SourceKind == destination.SourceKind &&
        source.ProjectId == destination.ProjectId &&
        source.EntraObjectId == destination.EntraObjectId &&
        source.SourceAuthorizationId == destination.SourceAuthorizationId &&
        source.SourceBindingId == destination.SourceBindingId &&
        source.InstallationId == destination.InstallationId &&
        source.RepositoryId == destination.RepositoryId &&
        source.CredentialReference == destination.CredentialReference &&
        source.CredentialVersion == destination.CredentialVersion &&
        source.GrantDigest == destination.GrantDigest &&
        source.CapturedAt == destination.CapturedAt &&
        source.SnapshotExpiresAt == destination.SnapshotExpiresAt;

    private static bool BrowseAuthoritiesMatch(
        GitHubInteractiveBrowseAuthorityRecord source,
        GitHubInteractiveBrowseAuthorityRecord destination) =>
        source.AuthorityRef == destination.AuthorityRef &&
        source.EntraObjectId == destination.EntraObjectId &&
        source.SourceAuthorizationId == destination.SourceAuthorizationId &&
        source.CredentialReference == destination.CredentialReference &&
        source.CredentialVersion == destination.CredentialVersion &&
        source.GrantDigest == destination.GrantDigest &&
        source.CreatedAt == destination.CreatedAt &&
        source.ExpiresAt == destination.ExpiresAt;

    private static bool BrowseSelectionsMatch(
        GitHubBrowseSelectionRecord source,
        GitHubBrowseSelectionRecord destination) =>
        source.SelectionRef == destination.SelectionRef &&
        source.AuthorityRef == destination.AuthorityRef &&
        source.RepositoryId == destination.RepositoryId &&
        source.FullNameDisplay == destination.FullNameDisplay &&
        source.Status == destination.Status &&
        source.CreatedAt == destination.CreatedAt &&
        source.ConsumedAt == destination.ConsumedAt;

    private static async Task PrepareTwoAppSourceSchemaAsync(MemoryDbContext source, CancellationToken ct)
    {
        if (await HasMigrationHistoryAsync(source, ct).ConfigureAwait(false))
        {
            await source.Database.MigrateAsync(ct).ConfigureAwait(false);
            return;
        }

        if (!await HasTableAsync(source, "github_authorizations", ct).ConfigureAwait(false))
            return;

        // EnsureCreated sources have no migration history. Upgrade only the missing #955
        // and #962 projection fields so the existing two-App records can be read and transferred.
        if (!await HasColumnAsync(source, "github_authorizations", "external_transaction_id", ct).ConfigureAwait(false))
        {
            await source.Database.ExecuteSqlRawAsync(
                "ALTER TABLE github_authorizations ADD COLUMN external_transaction_id TEXT NOT NULL DEFAULT '';",
                ct).ConfigureAwait(false);
            await source.Database.ExecuteSqlRawAsync(
                "UPDATE github_authorizations SET external_transaction_id = lower(hex(randomblob(32))) WHERE external_transaction_id = '';",
                ct).ConfigureAwait(false);
        }
        if (await HasTableAsync(source, "github_audit_records", ct).ConfigureAwait(false))
        {
            if (!await HasColumnAsync(source, "github_audit_records", "capability_purpose", ct).ConfigureAwait(false))
                await source.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE github_audit_records ADD COLUMN capability_purpose INTEGER NULL;",
                    ct).ConfigureAwait(false);
            if (!await HasColumnAsync(source, "github_audit_records", "grant_digest", ct).ConfigureAwait(false))
                await source.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE github_audit_records ADD COLUMN grant_digest TEXT NULL;",
                    ct).ConfigureAwait(false);
        }
        await source.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_github_authorizations_external_transaction_id ON github_authorizations(external_transaction_id);",
            ct).ConfigureAwait(false);
        await source.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS github_lifecycle_deliveries (
                delivery_id TEXT NOT NULL CONSTRAINT PK_github_lifecycle_deliveries PRIMARY KEY,
                event_name TEXT NOT NULL,
                installation_id INTEGER NULL,
                repository_id INTEGER NULL,
                received_at TEXT NOT NULL
            );
            """, ct).ConfigureAwait(false);
    }

    private static async Task<bool> HasMigrationHistoryAsync(MemoryDbContext source, CancellationToken ct) =>
        await HasTableAsync(source, "__EFMigrationsHistory", ct).ConfigureAwait(false);

    private static async Task<bool> HasTableAsync(MemoryDbContext source, string tableName, CancellationToken ct)
    {
        await source.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = source.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                SELECT 1
                FROM sqlite_master
                WHERE type = 'table' AND name = $tableName;
                """;
            var tableNameParameter = command.CreateParameter();
            tableNameParameter.ParameterName = "$tableName";
            tableNameParameter.Value = tableName;
            command.Parameters.Add(tableNameParameter);
            return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
        }
        finally
        {
            await source.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static async Task<bool> HasColumnAsync(
        MemoryDbContext source,
        string tableName,
        string columnName,
        CancellationToken ct)
    {
        await source.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = source.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"PRAGMA table_info({tableName});";
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
        finally
        {
            await source.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private async Task MigrateAgentweaverDbAsync(string dbPath, MemoryDbContext db, CancellationToken ct)
    {
        if (!File.Exists(dbPath))
        {
            _logger.LogWarning("agentweaver.db not found at {Path}; skipping agentweaver.db migration.", dbPath);
            return;
        }

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        await using var conn = new SqliteConnection(cs);
        await conn.OpenAsync(ct);

        var projects = await ReadProjectsAsync(conn, ct);
        _logger.LogInformation("Migrating {Count} projects...", projects.Count);
        var projMigrated = 0;
        foreach (var rec in projects)
        {
            if (!await db.Projects.AnyAsync(p => p.ProjectId == rec.ProjectId, ct))
            {
                db.Projects.Add(rec);
                projMigrated++;
            }
        }
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("  Projects: {Migrated}/{Total} migrated, {Skipped} skipped.",
            projMigrated, projects.Count, projects.Count - projMigrated);

        var runs = await ReadRunsAsync(conn, ct);
        _logger.LogInformation("Migrating {Count} runs...", runs.Count);
        var runsMigrated = 0;
        foreach (var rec in runs)
        {
            if (!await db.Runs.AnyAsync(r => r.RunId == rec.RunId, ct))
            {
                db.Runs.Add(rec);
                runsMigrated++;
            }
        }
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("  Runs: {Migrated}/{Total} migrated, {Skipped} skipped.",
            runsMigrated, runs.Count, runs.Count - runsMigrated);

        var revisions = await ReadRunRevisionsAsync(conn, ct);
        _logger.LogInformation("Migrating {Count} run revisions...", revisions.Count);
        var revMigrated = 0;
        foreach (var rec in revisions)
        {
            if (!await db.RunRevisions.AnyAsync(r => r.RunId == rec.RunId && r.RevisionNumber == rec.RevisionNumber, ct))
            {
                db.RunRevisions.Add(rec);
                revMigrated++;
            }
        }
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("  RunRevisions: {Migrated}/{Total} migrated, {Skipped} skipped.",
            revMigrated, revisions.Count, revisions.Count - revMigrated);

        var workflowRuns = await ReadWorkflowRunsAsync(conn, ct);
        _logger.LogInformation("Migrating {Count} workflow runs...", workflowRuns.Count);
        var wfMigrated = 0;
        foreach (var rec in workflowRuns)
        {
            if (!await db.WorkflowRuns.AnyAsync(w => w.WorkflowRunId == rec.WorkflowRunId, ct))
            {
                db.WorkflowRuns.Add(rec);
                wfMigrated++;
            }
        }
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("  WorkflowRuns: {Migrated}/{Total} migrated, {Skipped} skipped.",
            wfMigrated, workflowRuns.Count, workflowRuns.Count - wfMigrated);

        var backlogTasks = await ReadBacklogTasksAsync(conn, ct);
        _logger.LogInformation("Migrating {Count} backlog tasks...", backlogTasks.Count);
        var btMigrated = 0;
        foreach (var rec in backlogTasks)
        {
            if (!await db.BacklogTasks.AnyAsync(t => t.TaskId == rec.TaskId, ct))
            {
                db.BacklogTasks.Add(rec);
                btMigrated++;
            }
        }
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("  BacklogTasks: {Migrated}/{Total} migrated, {Skipped} skipped.",
            btMigrated, backlogTasks.Count, backlogTasks.Count - btMigrated);

        var backlogDependencies = await ReadBacklogTaskDependenciesAsync(conn, ct);
        _logger.LogInformation("Migrating {Count} backlog task dependencies...", backlogDependencies.Count);
        var depMigrated = 0;
        foreach (var rec in backlogDependencies)
        {
            if (!await db.BacklogTaskDependencies.AnyAsync(
                d => d.TaskId == rec.TaskId && d.DependsOnTaskId == rec.DependsOnTaskId, ct))
            {
                db.BacklogTaskDependencies.Add(rec);
                depMigrated++;
            }
        }
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("  BacklogTaskDependencies: {Migrated}/{Total} migrated, {Skipped} skipped.",
            depMigrated, backlogDependencies.Count, backlogDependencies.Count - depMigrated);

        try
        {
            await MigrateBlueprintPackagesAsync(conn, db, ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (
            ex.SqliteErrorCode == 1 &&
            ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Could not migrate owner Blueprint packages because the source database predates the package library.");
        }

        // cast_proposals might not exist on older databases
        try
        {
            var castProposals = await ReadCastProposalsAsync(conn, ct);
            _logger.LogInformation("Migrating {Count} cast proposals...", castProposals.Count);
            var cpMigrated = 0;
            foreach (var rec in castProposals)
            {
                if (!await db.CastProposals.AnyAsync(p => p.Id == rec.Id, ct))
                {
                    db.CastProposals.Add(rec);
                    cpMigrated++;
                }
            }
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("  CastProposals: {Migrated}/{Total} migrated, {Skipped} skipped.",
                cpMigrated, castProposals.Count, castProposals.Count - cpMigrated);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not migrate cast_proposals (table may not exist in older database).");
        }
    }

    private async Task MigrateBlueprintPackagesAsync(
        SqliteConnection source,
        MemoryDbContext db,
        CancellationToken ct)
    {
        var libraries = await ReadBlueprintPackageLibrariesAsync(source, ct).ConfigureAwait(false);
        var versions = await ReadBlueprintPackageVersionsAsync(source, ct).ConfigureAwait(false);
        var payloads = await ReadBlueprintPackagePayloadsAsync(source, ct).ConfigureAwait(false);
        var acquisitions = await ReadBlueprintPackageAcquisitionsAsync(source, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Migrating owner Blueprint packages: {Libraries} libraries, {Versions} versions, {Payloads} payloads, {Acquisitions} acquisitions...",
            libraries.Count,
            versions.Count,
            payloads.Count,
            acquisitions.Count);

        var migratedLibraries = 0;
        var migratedVersions = 0;
        var migratedPayloads = 0;
        var migratedAcquisitions = 0;
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var record in libraries)
            {
                if (!await db.BlueprintPackageLibrary.AnyAsync(
                        row => row.OwnerId == record.OwnerId && row.PackageId == record.PackageId,
                        ct).ConfigureAwait(false))
                {
                    db.BlueprintPackageLibrary.Add(record);
                    migratedLibraries++;
                }
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            foreach (var record in versions)
            {
                var existing = await db.BlueprintPackageVersions.AsNoTracking().SingleOrDefaultAsync(
                    row => row.OwnerId == record.OwnerId &&
                           row.PackageId == record.PackageId &&
                           row.CanonicalVersionKey == record.CanonicalVersionKey,
                    ct).ConfigureAwait(false);
                if (existing is null)
                {
                    db.BlueprintPackageVersions.Add(record);
                    migratedVersions++;
                }
                else if (!SameImmutableIdentity(existing, record))
                {
                    throw new InvalidOperationException(
                        "An immutable Blueprint package version conflicts with the migration source.");
                }
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            foreach (var record in payloads)
            {
                if (!await db.BlueprintPackagePayloads.AnyAsync(
                        row => row.OwnerId == record.OwnerId &&
                               row.PackageId == record.PackageId &&
                               row.CanonicalVersionKey == record.CanonicalVersionKey &&
                               row.Path == record.Path,
                        ct).ConfigureAwait(false))
                {
                    db.BlueprintPackagePayloads.Add(record);
                    migratedPayloads++;
                }
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            foreach (var record in acquisitions)
            {
                if (!await db.BlueprintPackageAcquisitions.AnyAsync(
                        row => row.OwnerId == record.OwnerId &&
                               row.PackageId == record.PackageId &&
                               row.CanonicalVersionKey == record.CanonicalVersionKey &&
                               row.Ordinal == record.Ordinal,
                        ct).ConfigureAwait(false))
                {
                    db.BlueprintPackageAcquisitions.Add(record);
                    migratedAcquisitions++;
                }
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        _logger.LogInformation(
            "  Owner Blueprint packages migrated: {Libraries} libraries, {Versions} versions, {Payloads} payloads, {Acquisitions} acquisitions.",
            migratedLibraries,
            migratedVersions,
            migratedPayloads,
            migratedAcquisitions);
    }

    private static bool SameImmutableIdentity(
        BlueprintPackageVersionRecord existing,
        BlueprintPackageVersionRecord source) =>
        string.Equals(existing.CanonicalVersion, source.CanonicalVersion, StringComparison.Ordinal) &&
        string.Equals(existing.ContentDigest, source.ContentDigest, StringComparison.Ordinal) &&
        string.Equals(existing.PayloadSetDigest, source.PayloadSetDigest, StringComparison.Ordinal) &&
        string.Equals(existing.RawManifestSha256, source.RawManifestSha256, StringComparison.Ordinal) &&
        string.Equals(existing.ContainerSha256, source.ContainerSha256, StringComparison.Ordinal);

    private static async Task<List<BlueprintPackageLibraryRecord>> ReadBlueprintPackageLibrariesAsync(
        SqliteConnection conn,
        CancellationToken ct)
    {
        var results = new List<BlueprintPackageLibraryRecord>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT owner_id, package_id, created_at FROM blueprint_package_library;";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new BlueprintPackageLibraryRecord
            {
                OwnerId = reader.GetString(0),
                PackageId = reader.GetString(1),
                CreatedAt = ParseTs(reader.GetString(2)),
            });
        }
        return results;
    }

    private static async Task<List<BlueprintPackageVersionRecord>> ReadBlueprintPackageVersionsAsync(
        SqliteConnection conn,
        CancellationToken ct)
    {
        var results = new List<BlueprintPackageVersionRecord>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT owner_id, package_id, canonical_version, content_digest, payload_set_digest,
                   raw_manifest_sha256, container_sha256, raw_manifest, created_at
              FROM blueprint_package_versions;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var version = reader.GetString(2);
            results.Add(new BlueprintPackageVersionRecord
            {
                OwnerId = reader.GetString(0),
                PackageId = reader.GetString(1),
                CanonicalVersionKey = BlueprintPackageLibraryLimits.CanonicalVersionKey(version),
                CanonicalVersion = version,
                ContentDigest = reader.GetString(3),
                PayloadSetDigest = reader.GetString(4),
                RawManifestSha256 = reader.GetString(5),
                ContainerSha256 = reader.IsDBNull(6) ? null : reader.GetString(6),
                RawManifest = reader.GetFieldValue<byte[]>(7).ToArray(),
                CreatedAt = ParseTs(reader.GetString(8)),
            });
        }
        return results;
    }

    private static async Task<List<BlueprintPackagePayloadRecord>> ReadBlueprintPackagePayloadsAsync(
        SqliteConnection conn,
        CancellationToken ct)
    {
        var results = new List<BlueprintPackagePayloadRecord>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT owner_id, package_id, canonical_version, path, bytes FROM blueprint_package_payloads;";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var version = reader.GetString(2);
            results.Add(new BlueprintPackagePayloadRecord
            {
                OwnerId = reader.GetString(0),
                PackageId = reader.GetString(1),
                CanonicalVersionKey = BlueprintPackageLibraryLimits.CanonicalVersionKey(version),
                CanonicalVersion = version,
                Path = reader.GetString(3),
                Bytes = reader.GetFieldValue<byte[]>(4).ToArray(),
            });
        }
        return results;
    }

    private static async Task<List<BlueprintPackageAcquisitionRecord>> ReadBlueprintPackageAcquisitionsAsync(
        SqliteConnection conn,
        CancellationToken ct)
    {
        var results = new List<BlueprintPackageAcquisitionRecord>();
        var requestedRef = await HasColumnAsync(
            conn,
            "blueprint_package_acquisitions",
            "requested_ref",
            ct).ConfigureAwait(false)
            ? "requested_ref"
            : "NULL AS requested_ref";
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"""
            SELECT owner_id, package_id, canonical_version, ordinal, source, producer,
                   repository, revision, acquired_at, {requestedRef}
              FROM blueprint_package_acquisitions;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var version = reader.GetString(2);
            results.Add(new BlueprintPackageAcquisitionRecord
            {
                OwnerId = reader.GetString(0),
                PackageId = reader.GetString(1),
                CanonicalVersionKey = BlueprintPackageLibraryLimits.CanonicalVersionKey(version),
                CanonicalVersion = version,
                Ordinal = reader.GetInt32(3),
                Source = reader.GetString(4),
                Producer = reader.IsDBNull(5) ? null : reader.GetString(5),
                Repository = reader.IsDBNull(6) ? null : reader.GetString(6),
                Revision = reader.IsDBNull(7) ? null : reader.GetString(7),
                AcquiredAt = reader.IsDBNull(8) ? null : ParseTs(reader.GetString(8)),
                RequestedRef = reader.IsDBNull(9) ? null : reader.GetString(9),
            });
        }
        return results;
    }

    private static async Task<bool> HasColumnAsync(
        SqliteConnection conn,
        string table,
        string column,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\");";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static async Task<List<ProjectRecord>> ReadProjectsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var results = new List<ProjectRecord>();
        var teamRevision = await HasColumnAsync(conn, "projects", "team_revision", ct)
            ? "team_revision"
            : "0 AS team_revision";
        var webhookSecret = await HasColumnAsync(conn, "projects", "webhook_secret", ct)
            ? "webhook_secret"
            : "NULL AS webhook_secret";
        var previewApprovalTimeout = await HasColumnAsync(conn, "projects", "preview_approval_timeout_minutes", ct)
            ? "COALESCE(preview_approval_timeout_minutes,30)"
            : "30 AS preview_approval_timeout_minutes";
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"""
            SELECT project_id, name, origin_kind, source_repository, working_directory,
                   COALESCE(default_branch,'main'), owner, default_provider,
                   default_model_copilot, default_model_foundry,
                   COALESCE(state,'active'), created_at, updated_at,
                   COALESCE(max_ready_per_heartbeat,3), COALESCE(pickup_autopilot,1),
                   COALESCE(pickup_auto_approve_tools,0),
                   {previewApprovalTimeout},
                   default_workflow_id, active_review_policy_name, sandbox_profile,
                   source_blueprint_id, source_blueprint_type,
                   blueprint_generation_model, workflow_generation_model, outcome_spec_generation_model,
                   allowed_workflow_ids, {webhookSecret}, {teamRevision}
              FROM projects;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ProjectRecord
            {
                ProjectId = reader.GetString(0),
                Name = reader.GetString(1),
                OriginKind = reader.GetString(2),
                SourceRepository = reader.IsDBNull(3) ? null : reader.GetString(3),
                WorkingDirectory = reader.GetString(4),
                DefaultBranch = reader.GetString(5),
                Owner = reader.GetString(6),
                DefaultProvider = reader.GetString(7),
                DefaultModelCopilot = reader.IsDBNull(8) ? null : reader.GetString(8),
                DefaultModelFoundry = reader.IsDBNull(9) ? null : reader.GetString(9),
                State = reader.GetString(10),
                CreatedAt = ParseTs(reader.GetString(11)),
                UpdatedAt = ParseTs(reader.GetString(12)),
                MaxReadyPerHeartbeat = reader.GetInt32(13),
                PickupAutopilot = reader.GetInt32(14) != 0,
                PickupAutoApproveTools = reader.GetInt32(15) != 0,
                PreviewApprovalTimeoutMinutes = reader.GetInt32(16),
                DefaultWorkflowId = reader.IsDBNull(17) ? null : reader.GetString(17),
                ActiveReviewPolicyName = reader.IsDBNull(18) ? null : reader.GetString(18),
                SandboxProfile = reader.IsDBNull(19) ? null : reader.GetString(19),
                SourceBlueprintId = reader.IsDBNull(20) ? null : reader.GetString(20),
                SourceBlueprintType = reader.IsDBNull(21) ? null : reader.GetString(21),
                BlueprintGenerationModel = reader.IsDBNull(22) ? null : reader.GetString(22),
                WorkflowGenerationModel = reader.IsDBNull(23) ? null : reader.GetString(23),
                OutcomeSpecGenerationModel = reader.IsDBNull(24) ? null : reader.GetString(24),
                AllowedWorkflowIds = reader.IsDBNull(25) ? null : reader.GetString(25),
                WebhookSecret = reader.IsDBNull(26) ? null : reader.GetString(26),
                TeamRevision = reader.GetInt64(27),
            });
        }
        return results;
    }

    private static async Task<List<RunRecord>> ReadRunsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var results = new List<RunRecord>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT run_id, repository_path, originating_branch, model_source, task,
                  submitting_user, status, started_at, ended_at, result,
                  worktree_path, worktree_branch, tree_hash, diff,
                  merge_conflicts, project_id, model_id, agent_name, agent_charter,
                  reviewed_by, workflow_run_id, merged_commit_hash, parent_run_id, subtask_id,
                  COALESCE(origin,'interactive'), retried_from, review_ready_at, archived_at,
                  sandbox_backend, sandbox_claim_name, sandbox_pod_name, sandbox_namespace
              FROM runs;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new RunRecord
            {
                RunId = reader.GetString(0),
                RepositoryPath = reader.GetString(1),
                OriginatingBranch = reader.GetString(2),
                ModelSource = reader.GetString(3),
                Task = reader.GetString(4),
                SubmittingUser = reader.GetString(5),
                Status = reader.GetString(6),
                StartedAt = ParseTs(reader.GetString(7)),
                EndedAt = reader.IsDBNull(8) ? null : ParseTs(reader.GetString(8)),
                Result = reader.IsDBNull(9) ? null : reader.GetString(9),
                WorktreePath = reader.IsDBNull(10) ? null : reader.GetString(10),
                WorktreeBranch = reader.IsDBNull(11) ? null : reader.GetString(11),
                TreeHash = reader.IsDBNull(12) ? null : reader.GetString(12),
                Diff = reader.IsDBNull(13) ? null : reader.GetString(13),
                MergeConflicts = reader.IsDBNull(14) ? null : reader.GetString(14),
                ProjectId = reader.IsDBNull(15) ? null : reader.GetString(15),
                ModelId = reader.IsDBNull(16) ? null : reader.GetString(16),
                AgentName = reader.IsDBNull(17) ? null : reader.GetString(17),
                AgentCharter = reader.IsDBNull(18) ? null : reader.GetString(18),
                ReviewedBy = reader.IsDBNull(19) ? null : reader.GetString(19),
                WorkflowRunId = reader.IsDBNull(20) ? null : reader.GetString(20),
                MergedCommitHash = reader.IsDBNull(21) ? null : reader.GetString(21),
                ParentRunId = reader.IsDBNull(22) ? null : reader.GetString(22),
                SubtaskId = reader.IsDBNull(23) ? null : reader.GetString(23),
                Origin = reader.GetString(24),
                RetriedFrom = reader.IsDBNull(25) ? null : reader.GetString(25),
                ReviewReadyAt = reader.IsDBNull(26) ? null : ParseTs(reader.GetString(26)),
                ArchivedAt = reader.IsDBNull(27) ? null : ParseTs(reader.GetString(27)),
                SandboxBackend = reader.IsDBNull(28) ? null : reader.GetString(28),
                SandboxClaimName = reader.IsDBNull(29) ? null : reader.GetString(29),
                SandboxPodName = reader.IsDBNull(30) ? null : reader.GetString(30),
                SandboxNamespace = reader.IsDBNull(31) ? null : reader.GetString(31),
            });
        }
        return results;
    }

    private static async Task<List<RunRevisionRecord>> ReadRunRevisionsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var results = new List<RunRevisionRecord>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT run_id, revision_number, reviewer_user, created_at, raw_comment, sanitized_comment, previous_tree_hash FROM run_revisions;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new RunRevisionRecord
            {
                RunId = reader.GetString(0),
                RevisionNumber = reader.GetInt32(1),
                ReviewerUser = reader.GetString(2),
                CreatedAt = ParseTs(reader.GetString(3)),
                RawComment = reader.GetString(4),
                SanitizedComment = reader.GetString(5),
                PreviousTreeHash = reader.GetString(6),
            });
        }
        return results;
    }

    private static async Task<List<WorkflowRunRecord>> ReadWorkflowRunsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var results = new List<WorkflowRunRecord>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT workflow_run_id, project_id, task, submitting_user, started_at, orchestration_worktree_path FROM workflow_runs;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new WorkflowRunRecord
            {
                WorkflowRunId = reader.GetString(0),
                ProjectId = reader.GetString(1),
                Task = reader.GetString(2),
                SubmittingUser = reader.GetString(3),
                StartedAt = ParseTs(reader.GetString(4)),
                OrchestrationWorktreePath = reader.IsDBNull(5) ? null : reader.GetString(5),
            });
        }
        return results;
    }

    private static async Task<List<BacklogTaskRecord>> ReadBacklogTasksAsync(SqliteConnection conn, CancellationToken ct)
    {
        var results = new List<BacklogTaskRecord>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT task_id, project_id, title, description, state, order_key,
                   captured_by, captured_by_user_id, created_at, committed_at, claimed_at, run_id,
                   workflow_override_id, archived_at, source_file_path,
                   parent_prd_run_id, promotion_key, promotion_reason
              FROM backlog_tasks;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new BacklogTaskRecord
            {
                TaskId = reader.GetString(0),
                ProjectId = reader.GetString(1),
                Title = reader.GetString(2),
                Description = reader.IsDBNull(3) ? null : reader.GetString(3),
                State = reader.GetString(4),
                OrderKey = reader.GetString(5),
                CapturedBy = reader.GetString(6),
                CapturedByUserId = reader.IsDBNull(7) ? null : reader.GetString(7),
                CreatedAt = ParseTs(reader.GetString(8)),
                CommittedAt = reader.IsDBNull(9) ? null : ParseTs(reader.GetString(9)),
                ClaimedAt = reader.IsDBNull(10) ? null : ParseTs(reader.GetString(10)),
                RunId = reader.IsDBNull(11) ? null : reader.GetString(11),
                WorkflowOverrideId = reader.IsDBNull(12) ? null : reader.GetString(12),
                ArchivedAt = reader.IsDBNull(13) ? null : ParseTs(reader.GetString(13)),
                SourceFilePath = reader.IsDBNull(14) ? null : reader.GetString(14),
                ParentPrdRunId = reader.IsDBNull(15) ? null : reader.GetString(15),
                PromotionKey = reader.IsDBNull(16) ? null : reader.GetString(16),
                PromotionReason = reader.IsDBNull(17) ? null : reader.GetString(17),
            });
        }
        return results;
    }

    private static async Task<List<BacklogTaskDependencyRecord>> ReadBacklogTaskDependenciesAsync(SqliteConnection conn, CancellationToken ct)
    {
        var results = new List<BacklogTaskDependencyRecord>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT project_id, task_id, depends_on_task_id, created_at
              FROM backlog_task_dependencies;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new BacklogTaskDependencyRecord
            {
                ProjectId = reader.GetString(0),
                TaskId = reader.GetString(1),
                DependsOnTaskId = reader.GetString(2),
                CreatedAt = ParseTs(reader.GetString(3)),
            });
        }
        return results;
    }

    private static async Task<List<CastProposalRecord>> ReadCastProposalsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var results = new List<CastProposalRecord>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, project_id, owner, created_at, expires_at, proposal_json FROM cast_proposals;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new CastProposalRecord
            {
                Id = reader.GetString(0),
                ProjectId = reader.GetString(1),
                Owner = reader.GetString(2),
                CreatedAt = ParseTs(reader.GetString(3)),
                ExpiresAt = ParseTs(reader.GetString(4)),
                ProposalJson = reader.GetString(5),
            });
        }
        return results;
    }

    private string GetSqlitePath(string configKey, string defaultFilename)
    {
        var configured = _configuration[configKey];
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        var baseDir = _configuration["Database:Path"] is string p && !string.IsNullOrWhiteSpace(p)
            ? Path.GetDirectoryName(Path.GetFullPath(p))!
            : Infrastructure.AppPaths.DataDirectory;

        return Path.Combine(baseDir, defaultFilename);
    }

    private static DateTimeOffset ParseTs(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
