using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Json;
using System.Data.Common;
using Agentweaver.Abstractions;
using Agentweaver.Policy.Acs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task NativeAcsPlatformPolicyReadAllowsReadOnlyPlatformAdmin()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "acs-platform-reader-" + suffix;
        var tenantId = "acs-platform-reader-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, subject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var artifact = Artifact("platform policy read " + suffix);
        var written = await WritePlatformPolicyAsync(
            caller, artifact, "acs-platform-read-" + suffix, "acs-platform-read-correlation-" + suffix);

        using var host = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath: null);
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Test-Scope", "api.read");
        client.DefaultRequestHeaders.Add(ProjectAuthorizationOwner.TenantSelectorHeader, tenantId);

        using var response = await client.GetAsync("/api/policies/platform/");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(written.PolicyId, root.GetProperty("policyId").GetString());
        Assert.Equal(written.Revision, root.GetProperty("revision").GetInt64());
        Assert.Equal(artifact.ContentSha256, root.GetProperty("contentSha256").GetString());
        Assert.Equal(
            artifact.ContentSha256,
            root.GetProperty("artifact").GetProperty("contentSha256").GetString());
        var manifest = root.GetProperty("artifact").GetProperty("manifestBytes")
            .EnumerateArray().Select(value => value.GetByte()).ToArray();
        Assert.Equal(artifact.ManifestBytes.ToArray(), manifest);
    }

    [Fact]
    public async Task NativeAcsPlatformPolicyHistoryPagesAtOneSnapshotAndReadsExactArtifactBytes()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "acs-history-reader-" + suffix;
        var tenantId = "acs-history-reader-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, subject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var artifacts = new[]
        {
            HistoryArtifact("history revision one " + suffix),
            HistoryArtifact("history revision two " + suffix),
            HistoryArtifact("history revision three " + suffix),
        };
        var receipts = new List<NativeAcsPolicyWriteReceipt>();
        for (var index = 0; index < artifacts.Length; index++)
            receipts.Add(await WritePlatformPolicyAsync(
                caller,
                artifacts[index],
                $"acs-history-{suffix}-{index}",
                $"acs-history-correlation-{suffix}-{index}"));

        using var host = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath: null);
        using var client = CreatePlatformPolicyReader(host, subject, tenantId);
        var stateBeforeFirstPage = await CapturePolicyTableStateAsync();
        using var firstResponse = await client.GetAsync("/api/policies/platform/revisions?pageSize=2");
        Assert.Equal(System.Net.HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.True(firstResponse.Headers.CacheControl?.NoStore);
        using var first = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        var firstRoot = first.RootElement;
        var snapshot = firstRoot.GetProperty("snapshotRevision").GetInt64();
        var policyId = firstRoot.GetProperty("policyId").GetString();
        var items = firstRoot.GetProperty("items").EnumerateArray().ToList();
        var cursor = firstRoot.GetProperty("nextCursor").GetString();
        Assert.Equal(receipts[^1].Revision, snapshot);
        Assert.Equal(receipts[0].PolicyId, policyId);
        Assert.Equal(2, items.Count);
        Assert.Equal(stateBeforeFirstPage, await CapturePolicyTableStateAsync());
        var expectedRevisions = await db.NativeAcsPolicyRevisions.AsNoTracking()
            .Where(item => item.PolicyId == policyId && item.Revision <= snapshot)
            .OrderByDescending(item => item.Revision)
            .Select(item => item.Revision)
            .ToArrayAsync();

        var appended = await WritePlatformPolicyAsync(
            caller, HistoryArtifact("history appended after page one " + suffix),
            "acs-history-appended-" + suffix, "acs-history-appended-correlation-" + suffix);
        var stateBeforeReads = await CapturePolicyTableStateAsync();
        using var continuationHost = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath: null);
        using var continuationClient = CreatePlatformPolicyReader(continuationHost, subject, tenantId);
        if (cursor is not null)
        {
            var retryPath = "/api/policies/platform/revisions?pageSize=2&cursor=" +
                Uri.EscapeDataString(cursor);
            using var retryOne = await continuationClient.GetAsync(retryPath);
            using var retryTwo = await continuationClient.GetAsync(retryPath);
            Assert.Equal(await retryOne.Content.ReadAsStringAsync(), await retryTwo.Content.ReadAsStringAsync());
        }

        var all = items.Select(item => item.GetProperty("revision").GetInt64()).ToList();
        var exhaustedPageSize = 0;
        while (cursor is not null)
        {
            using var pageResponse = await continuationClient.GetAsync(
                "/api/policies/platform/revisions?pageSize=2&cursor=" + Uri.EscapeDataString(cursor));
            Assert.Equal(System.Net.HttpStatusCode.OK, pageResponse.StatusCode);
            Assert.True(pageResponse.Headers.CacheControl?.NoStore);
            using var page = JsonDocument.Parse(await pageResponse.Content.ReadAsStringAsync());
            Assert.Equal(snapshot, page.RootElement.GetProperty("snapshotRevision").GetInt64());
            Assert.Equal(policyId, page.RootElement.GetProperty("policyId").GetString());
            var pageItems = page.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("revision").GetInt64()).ToArray();
            exhaustedPageSize = pageItems.Length;
            all.AddRange(pageItems);
            cursor = page.RootElement.GetProperty("nextCursor").GetString();
        }
        Assert.InRange(exhaustedPageSize, 1, 2);
        Assert.DoesNotContain(appended.Revision, all);
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal(all.OrderByDescending(value => value), all);
        Assert.Equal(expectedRevisions, all);
        foreach (var receipt in receipts)
            Assert.Contains(receipt.Revision, all);

        var expected = receipts[0];
        using var exactResponse = await continuationClient.GetAsync(
            $"/api/policies/platform/revisions/{expected.Revision}");
        Assert.Equal(System.Net.HttpStatusCode.OK, exactResponse.StatusCode);
        Assert.True(exactResponse.Headers.CacheControl?.NoStore);
        using var exact = JsonDocument.Parse(await exactResponse.Content.ReadAsStringAsync());
        Assert.Equal(policyId, exact.RootElement.GetProperty("policyId").GetString());
        Assert.Equal(expected.Revision, exact.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal(expected.ContentSha256, exact.RootElement.GetProperty("contentSha256").GetString());
        Assert.Equal(expected.ContentSha256,
            exact.RootElement.GetProperty("artifact").GetProperty("contentSha256").GetString());
        Assert.Equal(artifacts[0].ManifestBytes.ToArray(),
            exact.RootElement.GetProperty("artifact").GetProperty("manifestBytes")
                .EnumerateArray().Select(value => value.GetByte()).ToArray());
        var exactFiles = exact.RootElement.GetProperty("artifact").GetProperty("files")
            .EnumerateArray().ToArray();
        var expectedFiles = artifacts[0].Files
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedFiles.Length, exactFiles.Length);
        for (var index = 0; index < exactFiles.Length; index++)
        {
            Assert.Equal(expectedFiles[index].RelativePath,
                exactFiles[index].GetProperty("relativePath").GetString());
            Assert.Equal(expectedFiles[index].Bytes.ToArray(),
                exactFiles[index].GetProperty("bytes").EnumerateArray()
                    .Select(value => value.GetByte()).ToArray());
        }

        await using var revisionDb = CreateDbContext();
        var storedCreatedAt = await revisionDb.NativeAcsPolicyRevisions.AsNoTracking()
            .Where(item => item.PolicyId == policyId && item.Revision == expected.Revision)
            .Select(item => item.CreatedAt)
            .SingleAsync();
        Assert.Equal(storedCreatedAt, exact.RootElement.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(stateBeforeReads, await CapturePolicyTableStateAsync());
    }

    private static NativeAcsArtifact HistoryArtifact(string text)
    {
        var artifact = Artifact(text);
        artifact = artifact with
        {
            Files = artifact.Files
                .Add(new NativeAcsArtifactFile(
                    "data/rules.json",
                    System.Collections.Immutable.ImmutableArray.CreateRange(Encoding.UTF8.GetBytes($"{{\"name\":\"{text}\"}}"))))
                .Add(new NativeAcsArtifactFile(
                    "policies/helpers.rego",
                    System.Collections.Immutable.ImmutableArray.CreateRange(Encoding.UTF8.GetBytes($"package helper\n# {text}\n")))),
        };
        return artifact with { ContentSha256 = NativeAcsArtifactHash.Compute(artifact) };
    }

    [Fact]
    public async Task NativeAcsPolicyHistoryHandlesEmptyAndNonCanonicalLineage()
    {
        var emptyFixture = new ProjectsConfigPostgresFixture();
        try
        {
            await emptyFixture.InitializeAsync();
            var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                .UseNpgsql(
                    emptyFixture.DataSource,
                    npgsql => npgsql.MigrationsHistoryTable(
                        "__ef_migrations_history", ProjectsConfigDbContext.Schema))
                .Options;
            var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
            var suffix = Guid.NewGuid().ToString("N");
            var subject = "acs-empty-history-" + suffix;
            var tenantId = "acs-empty-history-tenant-" + suffix;
            await using var db = new ProjectsConfigDbContext(options);
            var caller = await SeedCallerAsync(
                db, authorityStore, subject, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
            using var host = await StartNativeAcsPolicyEndpointHostAsync(emptyFixture.DataSource, opaPath: null);
            using var client = CreatePlatformPolicyReader(host, subject, tenantId);

            var stateBeforeEmptyReads = await CapturePolicyTableStateAsync(emptyFixture.DataSource);
            using var emptyHistory = await client.GetAsync("/api/policies/platform/revisions");
            Assert.Equal(System.Net.HttpStatusCode.OK, emptyHistory.StatusCode);
            using (var empty = JsonDocument.Parse(await emptyHistory.Content.ReadAsStringAsync()))
            {
                Assert.Null(empty.RootElement.GetProperty("policyId").GetString());
                Assert.Equal(JsonValueKind.Null, empty.RootElement.GetProperty("snapshotRevision").ValueKind);
                Assert.Empty(empty.RootElement.GetProperty("items").EnumerateArray());
                Assert.Equal(JsonValueKind.Null, empty.RootElement.GetProperty("nextCursor").ValueKind);
            }
            using var missing = await client.GetAsync("/api/policies/platform/revisions/1");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal(stateBeforeEmptyReads, await CapturePolicyTableStateAsync(emptyFixture.DataSource));

            var store = new NativeAcsPolicyStore(
                new TestDbContextFactory(options), TimeProvider.System);
            var policyId = "stable-lineage-" + suffix;
            var receipts = new List<NativeAcsPolicyWriteReceipt>();
            for (var index = 0; index < 3; index++)
            {
                var current = await store.ReadAsync(
                    NativeAcsPolicyScopeType.Platform,
                    ProjectAuthorizationOwner.PlatformResourceId,
                    CancellationToken.None);
                receipts.Add(await store.WriteAsync(
                    caller,
                    new NativeAcsPolicyWriteRequest(
                        NativeAcsPolicyScopeType.Platform,
                        ProjectAuthorizationOwner.PlatformResourceId,
                        policyId,
                        current?.Revision,
                        HistoryArtifact($"noncanonical lineage {suffix} {index}"),
                        $"acs-noncanonical-{suffix}-{index}",
                        "Test history",
                        $"acs-noncanonical-correlation-{suffix}-{index}"),
                    CancellationToken.None));
            }

            var stateBeforeReads = await CapturePolicyTableStateAsync(emptyFixture.DataSource);
            using var firstPage = await client.GetAsync("/api/policies/platform/revisions?pageSize=2");
            Assert.Equal(System.Net.HttpStatusCode.OK, firstPage.StatusCode);
            using var first = JsonDocument.Parse(await firstPage.Content.ReadAsStringAsync());
            Assert.Equal(policyId, first.RootElement.GetProperty("policyId").GetString());
            Assert.Equal(receipts[^1].Revision, first.RootElement.GetProperty("snapshotRevision").GetInt64());
            var cursor = first.RootElement.GetProperty("nextCursor").GetString();
            Assert.NotNull(cursor);
            using var lastPage = await client.GetAsync(
                "/api/policies/platform/revisions?pageSize=2&cursor=" + Uri.EscapeDataString(cursor!));
            using var last = JsonDocument.Parse(await lastPage.Content.ReadAsStringAsync());
            Assert.Equal(policyId, last.RootElement.GetProperty("policyId").GetString());
            Assert.Null(last.RootElement.GetProperty("nextCursor").GetString());

            using var exact = await client.GetAsync("/api/policies/platform/revisions/1");
            Assert.Equal(System.Net.HttpStatusCode.OK, exact.StatusCode);
            using var exactBody = JsonDocument.Parse(await exact.Content.ReadAsStringAsync());
            Assert.Equal(policyId, exactBody.RootElement.GetProperty("policyId").GetString());
            Assert.Equal(3, exactBody.RootElement.GetProperty("artifact").GetProperty("files").GetArrayLength());
            using var future = await client.GetAsync("/api/policies/platform/revisions/4");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, future.StatusCode);
            Assert.Equal(stateBeforeReads, await CapturePolicyTableStateAsync(emptyFixture.DataSource));

            var fileTriggerEnabled = false;
            var revisionTriggerEnabled = false;
            try
            {
                await SetImmutableTriggerEnabledAsync(
                    emptyFixture.DataSource,
                    "native_acs_policy_artifact_files",
                    "native_acs_policy_artifact_files_immutable",
                    false);
                fileTriggerEnabled = true;
                await SetImmutableTriggerEnabledAsync(
                    emptyFixture.DataSource,
                    "native_acs_policy_revisions",
                    "native_acs_policy_revisions_immutable",
                    false);
                revisionTriggerEnabled = true;
                await using var connection = await emptyFixture.DataSource.OpenConnectionAsync();
                await using (var deleteFiles = new NpgsqlCommand(
                    """
                    DELETE FROM projects_config.native_acs_policy_artifact_files
                    WHERE scope_type = 'Platform' AND scope_id = 'default'
                      AND policy_id = @policy_id AND revision = 2
                    """,
                    connection))
                {
                    deleteFiles.Parameters.AddWithValue("policy_id", policyId);
                    await deleteFiles.ExecuteNonQueryAsync();
                }
                await using var deleteRevision = new NpgsqlCommand(
                    """
                    DELETE FROM projects_config.native_acs_policy_revisions
                    WHERE scope_type = 'Platform' AND scope_id = 'default'
                      AND policy_id = @policy_id AND revision = 2
                    """,
                    connection);
                deleteRevision.Parameters.AddWithValue("policy_id", policyId);
                await deleteRevision.ExecuteNonQueryAsync();
            }
            finally
            {
                try
                {
                    if (revisionTriggerEnabled)
                        await SetImmutableTriggerEnabledAsync(
                            emptyFixture.DataSource,
                            "native_acs_policy_revisions",
                            "native_acs_policy_revisions_immutable",
                            true);
                }
                finally
                {
                    if (fileTriggerEnabled)
                        await SetImmutableTriggerEnabledAsync(
                            emptyFixture.DataSource,
                            "native_acs_policy_artifact_files",
                            "native_acs_policy_artifact_files_immutable",
                            true);
                }
            }

            var stateBeforeMissingRead = await CapturePolicyTableStateAsync(emptyFixture.DataSource);
            using var missingCommitted = await client.GetAsync("/api/policies/platform/revisions/2");
            Assert.Equal(System.Net.HttpStatusCode.InternalServerError, missingCommitted.StatusCode);
            using var missingCommittedBody = JsonDocument.Parse(await missingCommitted.Content.ReadAsStringAsync());
            Assert.Equal("native_acs_policy_corrupt", missingCommittedBody.RootElement.GetProperty("code").GetString());
            Assert.Equal(stateBeforeMissingRead, await CapturePolicyTableStateAsync(emptyFixture.DataSource));
        }
        finally
        {
            await emptyFixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task NativeAcsPolicyExactRevisionRejectsCorruptStoredBytes()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "acs-corrupt-reader-" + suffix;
        var tenantId = "acs-corrupt-reader-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, subject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var artifact = Artifact("corrupt exact read " + suffix);
        var receipt = await WritePlatformPolicyAsync(
            caller, artifact, "acs-corrupt-exact-" + suffix, "acs-corrupt-correlation-" + suffix);
        var stateBeforeCorruption = await CapturePolicyTableStateAsync();
        byte[] originalBytes;
        await using (var read = CreateDbContext())
            originalBytes = (await read.NativeAcsPolicyArtifactFiles.AsNoTracking().SingleAsync(item =>
                item.PolicyId == receipt.PolicyId && item.Revision == receipt.Revision)).Bytes.ToArray();

        try
        {
            await WithArtifactFilesImmutableTriggerDisabledAsync(
                () => SetArtifactFileBytesAsync(receipt.PolicyId, receipt.Revision, [.. originalBytes, 0x7f]));

            using var host = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath: null);
            using var client = CreatePlatformPolicyReader(host, subject, tenantId);
            using var response = await client.GetAsync(
                $"/api/policies/platform/revisions/{receipt.Revision}");
            Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.Equal("native_acs_policy_corrupt", document.RootElement.GetProperty("code").GetString());
            Assert.DoesNotContain(Convert.ToBase64String(originalBytes), body, StringComparison.Ordinal);
        }
        finally
        {
            await WithArtifactFilesImmutableTriggerDisabledAsync(
                () => SetArtifactFileBytesAsync(receipt.PolicyId, receipt.Revision, originalBytes));
        }
        Assert.Equal(stateBeforeCorruption, await CapturePolicyTableStateAsync());
    }

    [Theory]
    [InlineData("/api/policies/platform/revisions?unknown=x", "native_acs_policy_history_request_invalid")]
    [InlineData("/api/policies/platform/revisions?pageSize=1&pageSize=2", "native_acs_policy_history_request_invalid")]
    [InlineData("/api/policies/platform/revisions/1?cursor=x", "native_acs_policy_revision_request_invalid")]
    public async Task NativeAcsPolicyHistoryRoutesRejectUndeclaredAndDuplicateQueryValues(
        string path,
        string expectedCode)
    {
        using var host = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath: null);
        using var client = CreatePlatformPolicyReader(host, "malformed-query-subject", "malformed-query-tenant");

        using var response = await client.GetAsync(path);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCode, body.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("/api/policies/platform/revisions", "native_acs_policy_history_request_invalid")]
    [InlineData("/api/policies/platform/revisions/1", "native_acs_policy_revision_request_invalid")]
    public async Task NativeAcsPolicyHistoryRoutesRejectRequestBodies(string path, string expectedCode)
    {
        using var host = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath: null);
        using var client = CreatePlatformPolicyReader(host, "malformed-body-subject", "malformed-body-tenant");
        using var request = new HttpRequestMessage(HttpMethod.Get, path)
        {
            Content = new ByteArrayContent([1]),
        };

        using var response = await client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCode, body.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("/api/policies/platform/revisions")]
    [InlineData("/api/policies/platform/revisions/1")]
    public async Task NativeAcsPolicyHistoryRoutesRequireCurrentUnboundPlatformAuthority(string path)
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "acs-authority-tenant-" + suffix;
        var tenantAdminSubject = "acs-tenant-admin-" + suffix;
        var tenantAdmin = await SeedCallerAsync(
            db, authorityStore, tenantAdminSubject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        Assert.False(tenantAdmin.IsPlatformAdmin);
        var project = await new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System)
            .CreateProjectAsync(tenantAdmin, "Policy history authority test", CancellationToken.None);
        var projectOwnerSubject = "acs-project-owner-" + suffix;
        _ = await SeedCallerAsync(
            db, authorityStore, projectOwnerSubject, tenantId,
            ["api.read"], ProjectAuthorityResourceType.Project, project.ProjectId, ProjectAuthorityRole.Owner);

        var staleSubject = "acs-stale-admin-" + suffix;
        var stale = await SeedCallerAsync(
            db, authorityStore, staleSubject, tenantId,
            ["api.read"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var staleGrant = stale.Grants.Single(grant =>
            grant.ResourceType == ProjectAuthorityResourceType.Platform &&
            grant.Role == ProjectAuthorityRole.PlatformAdmin);
        await authorityStore.RevokeRoleAssignmentAsync(
            staleGrant.AssignmentId, staleGrant.Revision, "fixture", CancellationToken.None);

        var validSubject = "acs-bound-admin-" + suffix;
        _ = await SeedCallerAsync(
            db, authorityStore, validSubject, tenantId,
            ["api.read"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);

        using var host = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath: null);
        using var tenantAdminClient = CreatePlatformPolicyReader(host, tenantAdminSubject, tenantId);
        using var projectOwnerClient = CreatePlatformPolicyReader(host, projectOwnerSubject, tenantId);
        using var staleClient = CreatePlatformPolicyReader(host, staleSubject, tenantId);
        using var wrongTenantClient = CreatePlatformPolicyReader(host, validSubject, "another-tenant-" + suffix);
        using var projectBoundClient = CreatePlatformPolicyReader(host, validSubject, tenantId);
        projectBoundClient.DefaultRequestHeaders.Add("X-Test-Project", "bound-project-" + suffix);
        using var runBoundClient = CreatePlatformPolicyReader(host, validSubject, tenantId);
        runBoundClient.DefaultRequestHeaders.Add("X-Test-Project", "bound-project-" + suffix);
        runBoundClient.DefaultRequestHeaders.Add("X-Test-Run", "bound-run-" + suffix);
        using var purposeBoundClient = CreatePlatformPolicyReader(host, validSubject, tenantId);
        purposeBoundClient.DefaultRequestHeaders.Add("X-Test-Purpose", "policy.read");
        var stateBeforeDeniedReads = await CapturePolicyTableStateAsync();

        foreach (var client in new[]
        {
            tenantAdminClient,
            projectOwnerClient,
            staleClient,
            wrongTenantClient,
            projectBoundClient,
            runBoundClient,
            purposeBoundClient,
        })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
        Assert.Equal(stateBeforeDeniedReads, await CapturePolicyTableStateAsync());
    }

    [Theory]
    [InlineData("/api/policies/platform/revisions")]
    [InlineData("/api/policies/platform/revisions/1")]
    public async Task NativeAcsPolicyHistoryRoutesRejectRevokedMembership(string path)
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "acs-revoked-membership-" + suffix;
        var tenantId = "acs-revoked-membership-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, subject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var receipt = await WritePlatformPolicyAsync(
            caller, HistoryArtifact("revoked membership route " + suffix),
            "acs-revoked-membership-" + suffix, "acs-revoked-membership-correlation-" + suffix);
        using var host = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath: null);
        using var client = CreatePlatformPolicyReader(host, subject, tenantId);
        await authorityStore.RevokeMembershipAsync(
            caller.MembershipId, 1, "fixture", CancellationToken.None);

        using var response = await client.GetAsync(path);

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(receipt.PolicyId, body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"policyId\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"artifact\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeAcsPolicyHistoryContinuationRejectsRevokedMembership()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "acs-revoked-history-" + suffix;
        var tenantId = "acs-revoked-history-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, subject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        for (var index = 0; index < 2; index++)
            await WritePlatformPolicyAsync(
                caller, HistoryArtifact($"revoked continuation {suffix} {index}"),
                $"acs-revoked-history-{suffix}-{index}", $"acs-revoked-history-correlation-{suffix}-{index}");

        using var host = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath: null);
        using var client = CreatePlatformPolicyReader(host, subject, tenantId);
        using var first = await client.GetAsync("/api/policies/platform/revisions?pageSize=1");
        Assert.Equal(System.Net.HttpStatusCode.OK, first.StatusCode);
        using var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var cursor = firstBody.RootElement.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);

        var stateBeforeContinuation = await CapturePolicyTableStateAsync();
        await authorityStore.RevokeMembershipAsync(
            caller.MembershipId, 1, "fixture", CancellationToken.None);
        using var continuation = await client.GetAsync(
            "/api/policies/platform/revisions?pageSize=1&cursor=" + Uri.EscapeDataString(cursor!));

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, continuation.StatusCode);
        Assert.True(continuation.Headers.CacheControl?.NoStore);
        var body = await continuation.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"policyId\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"artifact\"", body, StringComparison.Ordinal);
        Assert.Equal(stateBeforeContinuation, await CapturePolicyTableStateAsync());
    }

    [Theory]
    [InlineData("/api/policies/platform/revisions")]
    [InlineData("/api/policies/platform/revisions/1")]
    public async Task NativeAcsPolicyHistoryRoutesRecheckAuthorityAfterRetrieval(string path)
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "acs-read-revoked-during-query-" + suffix;
        var tenantId = "acs-read-revoked-during-query-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, subject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var receipt = await WritePlatformPolicyAsync(
            caller, HistoryArtifact("revoked while reading " + suffix),
            "acs-read-revoked-query-" + suffix, "acs-read-revoked-query-correlation-" + suffix);
        var stateBeforeRead = await CapturePolicyTableStateAsync();
        var grant = caller.Grants.Single(item =>
            item.ResourceType == ProjectAuthorityResourceType.Platform &&
            item.Role == ProjectAuthorityRole.PlatformAdmin);
        var revoked = 0;
        using var host = await StartNativeAcsPolicyEndpointHostAsync(
            fixture.DataSource,
            opaPath: null,
            commandInterceptor: new PolicyReadInterceptor(async cancellationToken =>
            {
                if (Interlocked.Exchange(ref revoked, 1) == 0)
                    await authorityStore.RevokeRoleAssignmentAsync(
                        grant.AssignmentId, grant.Revision, "fixture", cancellationToken);
            }, "native_acs_policy_revisions"));
        using var client = CreatePlatformPolicyReader(host, subject, tenantId);
        var requestPath = path.EndsWith("/revisions", StringComparison.Ordinal)
            ? path
            : $"/api/policies/platform/revisions/{receipt.Revision}";

        using var response = await client.GetAsync(requestPath);

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(receipt.PolicyId, body, StringComparison.Ordinal);
        Assert.Equal(stateBeforeRead, await CapturePolicyTableStateAsync());
    }

    [Theory]
    [InlineData("/api/policies/platform/revisions", "unavailable", 503, "native_acs_policy_source_unavailable")]
    [InlineData("/api/policies/platform/revisions/1", "unavailable", 503, "native_acs_policy_source_unavailable")]
    [InlineData("/api/policies/platform/revisions", "corrupt", 500, "native_acs_policy_corrupt")]
    [InlineData("/api/policies/platform/revisions/1", "corrupt", 500, "native_acs_policy_corrupt")]
    public async Task NativeAcsPolicyHistoryReadsClassifyControlledDatabaseFailures(
        string path,
        string failure,
        int status,
        string code)
    {
        var (subject, tenantId) = await SeedPlatformPolicyReadCallerAsync();
        var stateBeforeReads = await CapturePolicyTableStateAsync();
        Exception injected = failure switch
        {
            "unavailable" => new InvalidOperationException(
                "EF wrapped a provider connection failure",
                new NpgsqlException(
                    "provider connection failure detail",
                    new IOException("provider socket detail"))),
            _ => new ProjectConfigException(
                "native_acs_policy_corrupt",
                "Stored source failed integrity validation.",
                500),
        };
        using var host = await StartNativeAcsPolicyEndpointHostAsync(
            fixture.DataSource, opaPath: null, commandInterceptor: new PolicyReadInterceptor(_ =>
                Task.FromException(injected)));
        using var client = CreatePlatformPolicyReader(host, subject, tenantId);

        using var response = await client.GetAsync(path);

        Assert.Equal(status, (int)response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        if (failure == "unavailable")
        {
            Assert.DoesNotContain("provider connection failure detail", body, StringComparison.Ordinal);
            Assert.DoesNotContain("provider socket detail", body, StringComparison.Ordinal);
        }
        Assert.Equal(stateBeforeReads, await CapturePolicyTableStateAsync());
    }

    [Theory]
    [InlineData("/api/policies/platform/revisions")]
    [InlineData("/api/policies/platform/revisions/1")]
    public async Task NativeAcsPolicyHistoryReadsPropagateCancellation(string path)
    {
        var (subject, tenantId) = await SeedPlatformPolicyReadCallerAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = await StartNativeAcsPolicyEndpointHostAsync(
            fixture.DataSource,
            opaPath: null,
            commandInterceptor: new PolicyReadInterceptor(async cancellationToken =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }));
        using var client = CreatePlatformPolicyReader(host, subject, tenantId);
        using var cancellation = new CancellationTokenSource();
        var request = client.GetAsync(path, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Theory]
    [InlineData("/api/policies/platform/revisions")]
    [InlineData("/api/policies/platform/revisions/1")]
    public async Task NativeAcsPolicyHistoryReadsDoNotClassifyUnrelatedExceptionsAsOutages(string path)
    {
        var (subject, tenantId) = await SeedPlatformPolicyReadCallerAsync();
        using var host = await StartNativeAcsPolicyEndpointHostAsync(
            fixture.DataSource,
            opaPath: null,
            commandInterceptor: new PolicyReadInterceptor(_ =>
                Task.FromException(new InvalidOperationException("controlled unrelated failure"))));
        using var client = CreatePlatformPolicyReader(host, subject, tenantId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync(path));
    }

    private async Task<(string Subject, string TenantId)> SeedPlatformPolicyReadCallerAsync()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "acs-read-failure-" + suffix;
        var tenantId = "acs-read-failure-tenant-" + suffix;
        _ = await SeedCallerAsync(
            db, authorityStore, subject, tenantId,
            ["api.read"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        return (subject, tenantId);
    }

    [Fact]
    public async Task NativeAcsPolicyValidationSeparatesDependencyFailureAndRateLimitsCaller()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "acs-policy-validator-" + suffix;
        var tenantId = "acs-policy-validator-tenant-" + suffix;
        await using (var db = CreateDbContext())
        {
            var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
            _ = await SeedCallerAsync(
                db, authorityStore, subject, tenantId,
                ["api.read"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        }

        using var host = await StartNativeAcsPolicyEndpointHostAsync(
            fixture.DataSource,
            Path.Combine(AppContext.BaseDirectory, "missing-opa-" + suffix));
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Test-Scope", "api.read");
        client.DefaultRequestHeaders.Add(ProjectAuthorizationOwner.TenantSelectorHeader, tenantId);
        var request = new NativeAcsPolicyValidationRequest { Artifact = Artifact("validation dependency") };

        for (var index = 0; index < 6; index++)
        {
            using var response = await client.PostAsJsonAsync("/api/policies/platform/validate", request);
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(
                "native_acs_dependency_unavailable",
                document.RootElement.GetProperty("code").GetString());
            Assert.Equal("opa", document.RootElement.GetProperty("dependency").GetString());
        }

        using var limited = await client.PostAsJsonAsync("/api/policies/platform/validate", request);
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(60), limited.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task NativeAcsPolicyValidationReportsProvenInvalidArtifactAsUnprocessable()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "acs-policy-invalid-" + suffix;
        var tenantId = "acs-policy-invalid-tenant-" + suffix;
        await using (var db = CreateDbContext())
        {
            var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
            _ = await SeedCallerAsync(
                db, authorityStore, subject, tenantId,
                ["api.read"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        }

        using var host = await StartNativeAcsPolicyEndpointHostAsync(
            fixture.DataSource,
            Path.Combine(AppContext.BaseDirectory, "missing-opa-" + suffix));
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Test-Scope", "api.read");
        client.DefaultRequestHeaders.Add(ProjectAuthorizationOwner.TenantSelectorHeader, tenantId);
        var request = new NativeAcsPolicyValidationRequest
        {
            Artifact = Artifact("hash mismatch") with { ContentSha256 = new string('0', 64) },
        };

        using var response = await client.PostAsJsonAsync("/api/policies/platform/validate", request);

        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("native_acs_policy_invalid", document.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(true, "native_acs_dependency_unavailable")]
    [InlineData(false, "native_acs_policy_writes_unavailable")]
    public async Task NativeAcsPolicyWriteDoesNotPersistWhenWritesOrValidationAreUnavailable(
        bool headWritesEnabled,
        string expectedCode)
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "acs-policy-writer-" + suffix;
        var tenantId = "acs-policy-writer-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, subject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var headBefore = await CreateNativeAcsStore().ReadAsync(
            NativeAcsPolicyScopeType.Platform, ProjectAuthorizationOwner.PlatformResourceId, CancellationToken.None);

        using var host = await StartNativeAcsPolicyEndpointHostAsync(
            fixture.DataSource, opaPath: null, headWritesEnabled);
        using var client = CreatePlatformPolicyWriter(host, subject, tenantId);
        var request = new NativeAcsPolicyWriteBody
        {
            ExpectedRevision = headBefore?.Revision,
            Artifact = Artifact("write unavailable " + suffix),
            IdempotencyKey = "acs-policy-write-" + suffix,
            Reason = "Test write",
            CorrelationId = "acs-policy-write-correlation-" + suffix,
        };

        using var response = await client.PutAsJsonAsync("/api/policies/platform/", request);

        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCode, document.RootElement.GetProperty("code").GetString());
        var headAfter = await CreateNativeAcsStore().ReadAsync(
            NativeAcsPolicyScopeType.Platform, ProjectAuthorizationOwner.PlatformResourceId, CancellationToken.None);
        Assert.Equal(headBefore?.Revision, headAfter?.Revision);
        Assert.Equal(headBefore?.ContentSha256, headAfter?.ContentSha256);
        await using var verify = CreateDbContext();
        Assert.False(await verify.NativeAcsPolicyIdempotency.AsNoTracking()
            .AnyAsync(item => item.IdempotencyKey == request.IdempotencyKey));
        Assert.False(await verify.NativeAcsPolicyRevisions.AsNoTracking()
            .AnyAsync(item => item.CreatedByActorId == caller.ActorId));
        Assert.False(await verify.NativeAcsPolicyChangeAudit.AsNoTracking()
            .AnyAsync(item => item.ActorId == caller.ActorId));
    }

    [Fact]
    public async Task NativeAcsPolicyWriteReplaysReceiptBeforeWriteGateAndValidation()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "acs-policy-replay-" + suffix;
        var tenantId = "acs-policy-replay-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, subject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var expectedRevision = (await CreateNativeAcsStore().ReadAsync(
            NativeAcsPolicyScopeType.Platform, ProjectAuthorizationOwner.PlatformResourceId, CancellationToken.None))
            ?.Revision;
        var artifact = Artifact("lost response " + suffix);
        var key = "acs-policy-replay-key-" + suffix;
        var correlation = "acs-policy-replay-correlation-" + suffix;
        var committed = await WritePlatformPolicyAsync(caller, artifact, key, correlation, "Test write");

        using var host = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath: null);
        using var client = CreatePlatformPolicyWriter(host, subject, tenantId);
        using var response = await client.PutAsJsonAsync("/api/policies/platform/", new NativeAcsPolicyWriteBody
        {
            ExpectedRevision = expectedRevision,
            Artifact = artifact,
            IdempotencyKey = key,
            Reason = "Test write",
            CorrelationId = correlation,
        });

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(committed.PolicyId, document.RootElement.GetProperty("policyId").GetString());
        Assert.Equal(committed.Revision, document.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal(committed.AuditId, document.RootElement.GetProperty("auditId").GetGuid());
    }

    private async Task<NativeAcsPolicyWriteReceipt> WritePlatformPolicyAsync(
        ProjectAuthorizationContext caller,
        NativeAcsArtifact artifact,
        string idempotencyKey,
        string correlationId,
        string reason = "Create test policy")
    {
        var store = CreateNativeAcsStore();
        var current = await store.ReadAsync(
            NativeAcsPolicyScopeType.Platform, ProjectAuthorizationOwner.PlatformResourceId, CancellationToken.None);
        return await store.WriteAsync(
            caller,
            new NativeAcsPolicyWriteRequest(
                NativeAcsPolicyScopeType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId,
                current?.PolicyId ?? "canonical",
                current?.Revision,
                artifact,
                idempotencyKey,
                reason,
                correlationId),
            CancellationToken.None);
    }

    private static HttpClient CreatePlatformPolicyWriter(IHost host, string subject, string tenantId)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Test-Scope", "api.read projects.admin");
        client.DefaultRequestHeaders.Add(ProjectAuthorizationOwner.TenantSelectorHeader, tenantId);
        return client;
    }

    private static HttpClient CreatePlatformPolicyReader(IHost host, string subject, string tenantId)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Test-Scope", "api.read");
        client.DefaultRequestHeaders.Add(ProjectAuthorizationOwner.TenantSelectorHeader, tenantId);
        return client;
    }

    private ProjectsConfigDbContext CreateDbContext(NpgsqlDataSource dataSource) =>
        new(new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(dataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options);

    private async Task<(int Heads, int Revisions, int Files, int Idempotency, int Audit, int Outbox,
        long? HeadRevision, string RetainedBytes)>
        CapturePolicyTableStateAsync(NpgsqlDataSource? dataSource = null)
    {
        await using var db = dataSource is null ? CreateDbContext() : CreateDbContext(dataSource);
        var revisions = await db.NativeAcsPolicyRevisions.AsNoTracking()
            .OrderBy(item => item.ScopeType).ThenBy(item => item.ScopeId)
            .ThenBy(item => item.PolicyId).ThenBy(item => item.Revision)
            .Select(item => new
            {
                item.ScopeType,
                item.ScopeId,
                item.PolicyId,
                item.Revision,
                item.ManifestBytes,
            }).ToArrayAsync();
        var files = await db.NativeAcsPolicyArtifactFiles.AsNoTracking()
            .OrderBy(item => item.ScopeType).ThenBy(item => item.ScopeId)
            .ThenBy(item => item.PolicyId).ThenBy(item => item.Revision).ThenBy(item => item.RelativePath)
            .Select(item => new
            {
                item.ScopeType,
                item.ScopeId,
                item.PolicyId,
                item.Revision,
                item.RelativePath,
                item.Bytes,
            }).ToArrayAsync();
        return (
            await db.NativeAcsPolicyHeads.AsNoTracking().CountAsync(),
            await db.NativeAcsPolicyRevisions.AsNoTracking().CountAsync(),
            await db.NativeAcsPolicyArtifactFiles.AsNoTracking().CountAsync(),
            await db.NativeAcsPolicyIdempotency.AsNoTracking().CountAsync(),
            await db.NativeAcsPolicyChangeAudit.AsNoTracking().CountAsync(),
            await db.NativeAcsPolicyOutbox.AsNoTracking().CountAsync(),
            await db.NativeAcsPolicyHeads.AsNoTracking().Where(item =>
                    item.ScopeType == NativeAcsPolicyScopeType.Platform &&
                    item.ScopeId == ProjectAuthorizationOwner.PlatformResourceId)
                .Select(item => (long?)item.CurrentRevision)
                .SingleOrDefaultAsync(),
            JsonSerializer.Serialize(new { revisions, files }));
    }

    private async Task SetArtifactFilesImmutableTriggerEnabledAsync(bool enabled)
        => await SetImmutableTriggerEnabledAsync(
            fixture.DataSource,
            "native_acs_policy_artifact_files",
            "native_acs_policy_artifact_files_immutable",
            enabled);

    private static async Task SetImmutableTriggerEnabledAsync(
        NpgsqlDataSource dataSource,
        string tableName,
        string triggerName,
        bool enabled)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"ALTER TABLE projects_config.{tableName} {(enabled ? "ENABLE" : "DISABLE")} TRIGGER {triggerName}",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task WithArtifactFilesImmutableTriggerDisabledAsync(Func<Task> action)
    {
        try
        {
            await SetArtifactFilesImmutableTriggerEnabledAsync(false);
            await action();
        }
        finally
        {
            await SetArtifactFilesImmutableTriggerEnabledAsync(true);
        }
    }

    private async Task SetArtifactFileBytesAsync(string policyId, long revision, byte[] bytes)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE projects_config.native_acs_policy_artifact_files
            SET bytes = @bytes
            WHERE scope_type = 'Platform' AND scope_id = 'default'
              AND policy_id = @policy_id AND revision = @revision
            """,
            connection);
        command.Parameters.AddWithValue("bytes", bytes);
        command.Parameters.AddWithValue("policy_id", policyId);
        command.Parameters.AddWithValue("revision", revision);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task NativeAcsPolicyRequestRejectsUnknownNestedArtifactFields()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "acs-policy-strict-" + suffix;
        var tenantId = "acs-policy-strict-tenant-" + suffix;
        await using (var db = CreateDbContext())
        {
            var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
            _ = await SeedCallerAsync(
                db, authorityStore, subject, tenantId,
                ["api.read"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        }

        using var host = await StartNativeAcsPolicyEndpointHostAsync(
            fixture.DataSource,
            Path.Combine(AppContext.BaseDirectory, "missing-opa-" + suffix));
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Test-Scope", "api.read");
        client.DefaultRequestHeaders.Add(ProjectAuthorizationOwner.TenantSelectorHeader, tenantId);
        using var content = new StringContent(
            """{"artifact":{"manifestBytes":[],"files":[],"contentSha256":"","unexpected":true}}""",
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync("/api/policies/platform/validate", content);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<IHost> StartNativeAcsPolicyEndpointHostAsync(
        Npgsql.NpgsqlDataSource dataSource,
        string? opaPath,
        bool headWritesEnabled = false,
        DbCommandInterceptor? commandInterceptor = null) =>
        await new HostBuilder()
            .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [NativeAcsPolicyEndpoints.HeadWritesEnabledSetting] = headWritesEnabled ? "true" : "false",
                }))
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthentication(ProjectSummaryTestAuthenticationHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, ProjectSummaryTestAuthenticationHandler>(
                            ProjectSummaryTestAuthenticationHandler.SchemeName, _ => { });
                    services.AddAuthorization();
                    services.AddDbContextFactory<ProjectsConfigDbContext>(options =>
                    {
                        options.UseNpgsql(
                            dataSource,
                            npgsql => npgsql.MigrationsHistoryTable(
                                "__ef_migrations_history", ProjectsConfigDbContext.Schema));
                        if (commandInterceptor is not null)
                            options.AddInterceptors(commandInterceptor);
                    });
                    services.AddSingleton(CreateProviderCatalog());
                    services.AddSingleton(TimeProvider.System);
                    services.AddSingleton(new ProjectsConfigIdentityOptions(TestIssuer));
                    if (opaPath is not null)
                        services.AddSingleton(new NativeAcsArtifactValidator(opaPath));
                    services.AddMemoryCache();
                    services.AddSingleton<NativeAcsPolicyRequestGate>();
                    services.AddScoped<ProjectAuthorizationOwner>();
                    services.AddScoped<ProjectsConfigService>();
                    services.AddScoped<NativeAcsPolicyStore>();
                    services.ConfigureHttpJsonOptions(options =>
                        options.SerializerOptions.Converters.Add(
                            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapNativeAcsPolicyEndpoints());
                });
            })
            .StartAsync();

    private sealed class PolicyReadInterceptor(
        Func<CancellationToken, Task> intercept,
        string targetTable = "native_acs_policy_heads") : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(targetTable, StringComparison.OrdinalIgnoreCase))
                await intercept(cancellationToken).ConfigureAwait(false);
            return result;
        }
    }
}
