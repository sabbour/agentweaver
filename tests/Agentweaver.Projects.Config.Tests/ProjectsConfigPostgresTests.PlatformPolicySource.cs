using System.Collections.Immutable;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Text.Json;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task PrivatePlatformPolicySourceReturnsTheCompletePendingArtifactWithoutWrites()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "policy-source-reader-" + suffix;
        var tenantId = "policy-source-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, subject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var artifact = HistoryArtifact("private policy source " + suffix);
        var receipt = await WritePlatformPolicyAsync(
            caller, artifact, "policy-source-write-" + suffix, "policy-source-correlation-" + suffix);
        var expected = await CreateNativeAcsStore().ReadAsync(
            NativeAcsPolicyScopeType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            CancellationToken.None);
        Assert.NotNull(expected);
        var stateBeforeReads = await CapturePolicyTableStateAsync();

        using var certificates = new PlatformPolicySourceTlsTests.PolicySourceCertificates();
        using var host = await StartPlatformPolicySourceEndpointHostAsync(
            fixture.DataSource,
            certificates.CreateIdentity(),
            certificates.EnvironmentClient);
        using var client = host.GetTestClient();

        using var response = await client.GetAsync(PlatformPolicySourceTls.RoutePath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using (var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            var root = document.RootElement;
            Assert.Equal("platform", root.GetProperty("scopeType").GetString());
            Assert.Equal(ProjectAuthorizationOwner.PlatformResourceId, root.GetProperty("scopeId").GetString());
            Assert.Equal(receipt.PolicyId, root.GetProperty("policyId").GetString());
            Assert.Equal(receipt.Revision, root.GetProperty("revision").GetInt64());
            Assert.Equal(receipt.ContentSha256, root.GetProperty("contentSha256").GetString());
            Assert.Equal(receipt.ContentSha256,
                root.GetProperty("artifact").GetProperty("contentSha256").GetString());
            Assert.Equal(expected.UpdatedAt, root.GetProperty("updatedAt").GetDateTimeOffset());
            Assert.Equal("pending", root.GetProperty("admission").GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null,
                root.GetProperty("admission").GetProperty("checkpoint").ValueKind);
            Assert.Equal(
                artifact.ManifestBytes.ToArray(),
                root.GetProperty("artifact").GetProperty("manifestBytes")
                    .EnumerateArray().Select(value => value.GetByte()).ToArray());
            var actualFiles = root.GetProperty("artifact").GetProperty("files").EnumerateArray().ToArray();
            var expectedFiles = artifact.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
            Assert.Equal(expectedFiles.Length, actualFiles.Length);
            for (var index = 0; index < expectedFiles.Length; index++)
            {
                Assert.Equal(expectedFiles[index].RelativePath,
                    actualFiles[index].GetProperty("relativePath").GetString());
                Assert.Equal(expectedFiles[index].Bytes.ToArray(),
                    actualFiles[index].GetProperty("bytes").EnumerateArray()
                        .Select(value => value.GetByte()).ToArray());
            }
        }

        using var query = await client.GetAsync(PlatformPolicySourceTls.RoutePath + "?scope=project");
        Assert.Equal(HttpStatusCode.BadRequest, query.StatusCode);
        Assert.Equal("policy_source_request_invalid", await ReadErrorCodeAsync(query));

        using var bodyRequest = new HttpRequestMessage(HttpMethod.Get, PlatformPolicySourceTls.RoutePath)
        {
            Content = new StringContent("request body"),
        };
        using var bodyResponse = await client.SendAsync(bodyRequest);
        Assert.Equal(HttpStatusCode.BadRequest, bodyResponse.StatusCode);
        Assert.Equal("policy_source_request_invalid", await ReadErrorCodeAsync(bodyResponse));

        using var unsupportedMethod = await client.PostAsync(PlatformPolicySourceTls.RoutePath, content: null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, unsupportedMethod.StatusCode);
        Assert.True(unsupportedMethod.Headers.CacheControl?.NoStore);

        Assert.Equal(stateBeforeReads, await CapturePolicyTableStateAsync());
    }

    [Fact]
    public async Task PrivatePlatformPolicySourceUsesOneRepeatableReadSnapshot()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "policy-source-snapshot-" + suffix;
        var tenantId = "policy-source-snapshot-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, subject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var firstArtifact = HistoryArtifact("snapshot first " + suffix);
        var firstReceipt = await WritePlatformPolicyAsync(
            caller, firstArtifact, "policy-source-first-" + suffix, "policy-source-first-correlation-" + suffix);
        var secondArtifact = HistoryArtifact("snapshot second " + suffix);
        NativeAcsPolicyWriteReceipt? secondReceipt = null;
        var advancedHead = 0;
        var interceptor = new PolicyReadInterceptor(async cancellationToken =>
        {
            if (Interlocked.Exchange(ref advancedHead, 1) == 0)
                secondReceipt = await WritePlatformPolicyAsync(
                    caller,
                    secondArtifact,
                    "policy-source-second-" + suffix,
                    "policy-source-second-correlation-" + suffix);
        }, "native_acs_policy_revisions");

        using var certificates = new PlatformPolicySourceTlsTests.PolicySourceCertificates();
        using var host = await StartPlatformPolicySourceEndpointHostAsync(
            fixture.DataSource,
            certificates.CreateIdentity(),
            certificates.EnvironmentClient,
            interceptor);
        using var client = host.GetTestClient();

        using var firstResponse = await client.GetAsync(PlatformPolicySourceTls.RoutePath);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        using (var first = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(firstReceipt.Revision, first.RootElement.GetProperty("revision").GetInt64());
            Assert.Equal(firstReceipt.ContentSha256, first.RootElement.GetProperty("contentSha256").GetString());
            Assert.Equal(firstReceipt.ContentSha256,
                first.RootElement.GetProperty("artifact").GetProperty("contentSha256").GetString());
        }

        Assert.NotNull(secondReceipt);
        var stateBeforeNextRead = await CapturePolicyTableStateAsync();
        using var secondResponse = await client.GetAsync(PlatformPolicySourceTls.RoutePath);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        using (var second = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(secondReceipt.Revision, second.RootElement.GetProperty("revision").GetInt64());
            Assert.Equal(secondReceipt.ContentSha256, second.RootElement.GetProperty("contentSha256").GetString());
            Assert.Equal(secondReceipt.ContentSha256,
                second.RootElement.GetProperty("artifact").GetProperty("contentSha256").GetString());
        }
        Assert.Equal(stateBeforeNextRead, await CapturePolicyTableStateAsync());
    }

    [Fact]
    public async Task PrivatePlatformPolicySourceDoesNotUseBearerAuthenticationAsPeerProof()
    {
        using var certificates = new PlatformPolicySourceTlsTests.PolicySourceCertificates();
        using var host = await StartPlatformPolicySourceEndpointHostAsync(
            fixture.DataSource,
            certificates.CreateIdentity(),
            clientCertificate: null);
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-client-certificate");

        using var response = await client.GetAsync(PlatformPolicySourceTls.RoutePath);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("policy_source_peer_denied", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task PrivatePlatformPolicySourceRejectsAuthorizationBeforeAuthentication()
    {
        using var certificates = new PlatformPolicySourceTlsTests.PolicySourceCertificates();
        var authenticationCalls = 0;
        using var host = await StartPlatformPolicySourceEndpointHostAsync(
            fixture.DataSource,
            certificates.CreateIdentity(),
            certificates.EnvironmentClient,
            onAuthentication: () => Interlocked.Increment(ref authenticationCalls));
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-accepted");

        using var response = await client.GetAsync(PlatformPolicySourceTls.RoutePath);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("policy_source_request_invalid", await ReadErrorCodeAsync(response));
        Assert.Equal(0, authenticationCalls);
    }

    [Theory]
    [InlineData("pin-retirement")]
    [InlineData("ca-change")]
    [InlineData("revocation")]
    public async Task PrivatePlatformPolicySourceDeniesPeerChangesDuringStoreRead(string change)
    {
        var artifact = HistoryArtifact("peer changed during source read " + change);
        var receipt = await SeedPlatformPolicySourceArtifactAsync(change, artifact);
        var stateBeforeReads = await CapturePolicyTableStateAsync();
        using var certificates = new PlatformPolicySourceTlsTests.PolicySourceCertificates();
        var now = DateTimeOffset.UtcNow;
        var retirement = change == "pin-retirement" ? now.AddSeconds(5) : now.AddDays(2);
        var clock = new MutableTimeProvider(now);
        var identity = certificates.CreateIdentity(retireAtUtc: retirement, timeProvider: clock);
        var crlRequestsBeforeMutation = 0;
        if (change == "revocation")
            certificates.SetUnrevokedCrl(TimeSpan.FromSeconds(2));
        var interceptor = new PolicyReadInterceptor(async cancellationToken =>
        {
            if (change == "pin-retirement")
                clock.SetUtcNow(retirement);
            else if (change == "ca-change")
                File.WriteAllText(certificates.CaPath, certificates.OtherCa.ExportCertificatePem());
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                crlRequestsBeforeMutation = certificates.CrlRequestCount;
                certificates.Revoke(certificates.EnvironmentClient);
            }
        });
        using var host = await StartPlatformPolicySourceEndpointHostAsync(
            fixture.DataSource,
            identity,
            certificates.EnvironmentClient,
            interceptor);
        using var client = host.GetTestClient();

        using var response = await client.GetAsync(PlatformPolicySourceTls.RoutePath);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("policy_source_peer_denied", await ReadErrorCodeAsync(response));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(receipt.ContentSha256, body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"artifact\"", body, StringComparison.Ordinal);
        Assert.True(clock.GetUtcNow() < certificates.EnvironmentClient.NotAfter.ToUniversalTime());
        if (change == "revocation")
            Assert.True(certificates.CrlRequestCount > crlRequestsBeforeMutation);
        Assert.Equal(stateBeforeReads, await CapturePolicyTableStateAsync());
    }

    [Fact]
    public async Task PrivatePlatformPolicySourceReturnsNotFoundWhenHeadIsMissing()
    {
        var emptyFixture = new ProjectsConfigPostgresFixture();
        try
        {
            await emptyFixture.InitializeAsync();
            using var certificates = new PlatformPolicySourceTlsTests.PolicySourceCertificates();
            using var host = await StartPlatformPolicySourceEndpointHostAsync(
                emptyFixture.DataSource,
                certificates.CreateIdentity(),
                certificates.EnvironmentClient);
            using var client = host.GetTestClient();

            using var response = await client.GetAsync(PlatformPolicySourceTls.RoutePath);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("native_acs_policy_not_found", await ReadErrorCodeAsync(response));
        }
        finally
        {
            await emptyFixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task PrivatePlatformPolicySourceDoesNotReturnAfterRequestAbort()
    {
        var artifact = HistoryArtifact("aborted private policy source read");
        _ = await SeedPlatformPolicySourceArtifactAsync("request-abort", artifact);
        var readStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readCanceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new PolicyReadInterceptor(async cancellationToken =>
        {
            readStarted.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                readCanceled.TrySetResult(true);
                throw;
            }
        });
        using var certificates = new PlatformPolicySourceTlsTests.PolicySourceCertificates();
        using var host = await StartPlatformPolicySourceEndpointHostAsync(
            fixture.DataSource,
            certificates.CreateIdentity(),
            certificates.EnvironmentClient,
            interceptor);
        using var client = host.GetTestClient();
        using var requestCancellation = new CancellationTokenSource();

        var request = client.GetAsync(PlatformPolicySourceTls.RoutePath, requestCancellation.Token);
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        requestCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.True(await readCanceled.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task PrivatePlatformPolicySourceDeadlineReturnsSanitizedError()
    {
        var artifact = HistoryArtifact("deadline private policy source read");
        _ = await SeedPlatformPolicySourceArtifactAsync("deadline", artifact);
        var readStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readCanceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new PolicyReadInterceptor(async cancellationToken =>
        {
            readStarted.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                readCanceled.TrySetResult(true);
                throw;
            }
        });
        using var certificates = new PlatformPolicySourceTlsTests.PolicySourceCertificates();
        using var host = await StartPlatformPolicySourceEndpointHostAsync(
            fixture.DataSource,
            certificates.CreateIdentity(),
            certificates.EnvironmentClient,
            interceptor);
        using var client = host.GetTestClient();
        var responseTask = client.GetAsync(PlatformPolicySourceTls.RoutePath);

        Assert.True(await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        using var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(await readCanceled.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("policy_source_internal_error", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task PrivatePlatformPolicySourceEnforcesArtifactSizeBoundariesBeforeBlobReads()
    {
        var fileSizes = Enumerable.Repeat(11_442, 64).ToArray();
        fileSizes[0] = 262_144;
        fileSizes[1] = 11_492;
        var boundaryArtifact = CreatePolicySourceArtifact(
            new byte[65_536],
            Enumerable.Range(0, 64)
                .Select(index => ($"policies/boundary-{index:D2}.rego", new byte[fileSizes[index]])));
        _ = await SeedPlatformPolicySourceArtifactAsync("bounds-boundary", boundaryArtifact);
        var blobReads = 0;
        var blobReadObserver = new Action<string>(commandText =>
        {
            var fromIndex = commandText.IndexOf("FROM", StringComparison.OrdinalIgnoreCase);
            if (fromIndex < 0)
                return;
            var projection = commandText[..fromIndex];
            var bytesColumn = projection.IndexOf("\"bytes\"", StringComparison.OrdinalIgnoreCase);
            var afterBytesColumn = bytesColumn + "\"bytes\"".Length;
            if (bytesColumn >= 0 &&
                (afterBytesColumn == projection.Length ||
                 char.IsWhiteSpace(projection[afterBytesColumn]) ||
                 projection[afterBytesColumn] == ','))
                Interlocked.Increment(ref blobReads);
        });
        using var certificates = new PlatformPolicySourceTlsTests.PolicySourceCertificates();
        using var host = await StartPlatformPolicySourceEndpointHostAsync(
            fixture.DataSource,
            certificates.CreateIdentity(),
            certificates.EnvironmentClient,
            new PolicyReadInterceptor(
                _ => Task.CompletedTask,
                "native_acs_policy_artifact_files",
                blobReadObserver));
        using var client = host.GetTestClient();

        using (var response = await client.GetAsync(PlatformPolicySourceTls.RoutePath))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            Assert.Equal(boundaryArtifact.ContentSha256, root.GetProperty("contentSha256").GetString());
            Assert.Equal(65_536, root.GetProperty("artifact").GetProperty("manifestBytes").GetArrayLength());
            var files = root.GetProperty("artifact").GetProperty("files").EnumerateArray().ToArray();
            Assert.Equal(64, files.Length);
            Assert.Equal(262_144, files[0].GetProperty("bytes").GetArrayLength());
            Assert.Equal(983_040, files.Sum(file => file.GetProperty("bytes").GetArrayLength()));
        }
        Assert.Equal(1, blobReads);

        var oversizedArtifacts = new[]
        {
            CreatePolicySourceArtifact(new byte[65_537], [("policies/manifest.rego", new byte[1])]),
            CreatePolicySourceArtifact(
                new byte[1],
                Enumerable.Range(0, 65).Select(index => ($"policies/count-{index:D2}.rego", new byte[1]))),
            CreatePolicySourceArtifact(new byte[1], [("policies/file.rego", new byte[262_145])]),
            CreatePolicySourceArtifact(
                new byte[1],
                Enumerable.Range(0, 4).Select(index => ($"policies/total-{index:D2}.rego", new byte[262_144]))),
        };
        var overLimitCases = new[] { "manifest", "file-count", "file-size", "total-size" };
        for (var index = 0; index < oversizedArtifacts.Length; index++)
        {
            var artifact = oversizedArtifacts[index];
            _ = await SeedPlatformPolicySourceArtifactAsync(overLimitCases[index], artifact);
            var blobReadsBefore = blobReads;
            using var response = await client.GetAsync(PlatformPolicySourceTls.RoutePath);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("native_acs_policy_corrupt", await ReadErrorCodeAsync(response));
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(artifact.ContentSha256, body, StringComparison.Ordinal);
            Assert.DoesNotContain("\"artifact\"", body, StringComparison.Ordinal);
            Assert.Equal(blobReadsBefore, blobReads);
        }
    }

    [Fact]
    public async Task PrivatePlatformPolicySourceRejectsCanonicalHashCorruption()
    {
        var artifact = HistoryArtifact("private policy source hash corruption");
        var receipt = await SeedPlatformPolicySourceArtifactAsync("hash-corruption", artifact);
        byte[] originalBytes;
        await using (var read = CreateDbContext(fixture.DataSource))
            originalBytes = (await read.NativeAcsPolicyArtifactFiles.AsNoTracking().SingleAsync(item =>
                item.PolicyId == receipt.PolicyId && item.Revision == receipt.Revision)).Bytes.ToArray();
        var corruptedBytes = originalBytes.ToArray();
        corruptedBytes[0] ^= 0xff;
        var stateBeforeCorruption = await CapturePolicyTableStateAsync();
        await WithArtifactFilesImmutableTriggerDisabledAsync(
            () => SetArtifactFileBytesAsync(receipt.PolicyId, receipt.Revision, corruptedBytes));
        using var certificates = new PlatformPolicySourceTlsTests.PolicySourceCertificates();
        using var host = await StartPlatformPolicySourceEndpointHostAsync(
            fixture.DataSource,
            certificates.CreateIdentity(),
            certificates.EnvironmentClient);
        using var client = host.GetTestClient();
        try
        {
            using var response = await client.GetAsync(PlatformPolicySourceTls.RoutePath);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("native_acs_policy_corrupt", await ReadErrorCodeAsync(response));
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(receipt.ContentSha256, body, StringComparison.Ordinal);
            Assert.DoesNotContain("\"artifact\"", body, StringComparison.Ordinal);
        }
        finally
        {
            await WithArtifactFilesImmutableTriggerDisabledAsync(
                () => SetArtifactFileBytesAsync(receipt.PolicyId, receipt.Revision, originalBytes));
        }

        Assert.Equal(stateBeforeCorruption, await CapturePolicyTableStateAsync());
    }

    [Fact]
    public async Task PrivatePlatformPolicySourceReturnsSanitizedSourceUnavailable()
    {
        var stateBeforeReads = await CapturePolicyTableStateAsync();
        using var certificates = new PlatformPolicySourceTlsTests.PolicySourceCertificates();
        using var host = await StartPlatformPolicySourceEndpointHostAsync(
            fixture.DataSource,
            certificates.CreateIdentity(),
            certificates.EnvironmentClient,
            new PolicyReadInterceptor(_ => Task.FromException(
                new InvalidOperationException(
                    "provider connection failure detail",
                    new Npgsql.NpgsqlException(
                        "provider connection failure detail",
                        new IOException("provider socket detail"))))));
        using var client = host.GetTestClient();

        using var response = await client.GetAsync(PlatformPolicySourceTls.RoutePath);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("native_acs_policy_source_unavailable", await ReadErrorCodeAsync(response));
        Assert.DoesNotContain("provider connection failure detail", body, StringComparison.Ordinal);
        Assert.DoesNotContain("provider socket detail", body, StringComparison.Ordinal);
        Assert.Equal(stateBeforeReads, await CapturePolicyTableStateAsync());
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
    }

    private static NativeAcsArtifact CreatePolicySourceArtifact(
        byte[] manifest,
        IEnumerable<(string RelativePath, byte[] Bytes)> files)
    {
        var artifact = new NativeAcsArtifact(
            ImmutableArray.CreateRange(manifest),
            ImmutableArray.CreateRange(files.Select(file => new NativeAcsArtifactFile(
                file.RelativePath,
                ImmutableArray.CreateRange(file.Bytes)))),
            string.Empty);
        return artifact with { ContentSha256 = NativeAcsArtifactHash.Compute(artifact) };
    }

    private async Task<NativeAcsPolicyWriteReceipt> SeedPlatformPolicySourceArtifactAsync(
        string label,
        NativeAcsArtifact artifact)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "policy-source-tenant-" + suffix;
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var caller = await SeedCallerAsync(
            db, authorityStore, "policy-source-" + label + "-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        return await WritePlatformPolicyAsync(
            caller, artifact, "policy-source-" + label + "-" + suffix, "policy-source-correlation-" + suffix);
    }

    private static async Task<IHost> StartPlatformPolicySourceEndpointHostAsync(
        Npgsql.NpgsqlDataSource dataSource,
        PlatformPolicySourceTlsIdentity identity,
        System.Security.Cryptography.X509Certificates.X509Certificate2? clientCertificate,
        DbCommandInterceptor? commandInterceptor = null,
        Action? onAuthentication = null) =>
        await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton(identity);
                    services.AddSingleton(TimeProvider.System);
                    services.AddDbContextFactory<ProjectsConfigDbContext>(options =>
                    {
                        options.UseNpgsql(
                            dataSource,
                            npgsql => npgsql.MigrationsHistoryTable(
                                "__ef_migrations_history", ProjectsConfigDbContext.Schema));
                        if (commandInterceptor is not null)
                            options.AddInterceptors(commandInterceptor);
                    });
                    services.AddScoped<NativeAcsPolicyStore>();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.Use(async (context, next) =>
                    {
                        context.Connection.LocalPort = 8445;
                        context.Request.Scheme = "https";
                        context.Features.Set<ITlsHandshakeFeature>(new TestTlsHandshakeFeature());
                        context.Features.Set<ITlsConnectionFeature>(
                            new TestTlsConnectionFeature { ClientCertificate = clientCertificate });
                        await next();
                    });
                    app.UsePrivateListenerGuard();
                    app.Use(async (context, next) =>
                    {
                        onAuthentication?.Invoke();
                        await next();
                    });
                    app.UseEndpoints(endpoints => endpoints.MapPlatformPolicySource());
                });
            })
            .StartAsync();

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
                return _utcNow;
        }

        internal void SetUtcNow(DateTimeOffset value)
        {
            lock (_gate)
                _utcNow = value;
        }
    }
}
