using System.Collections.Immutable;
using System.Data.Common;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    private static readonly byte[] PolicySourceTestSigningKey = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public async Task PrivatePlatformPolicySourceKeepsRealAgentHostPublicationAndBearerBoundaries()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "shared-tls-tenant-" + suffix;
        var subject = "shared-tls-owner-" + suffix;
        var owner = await SeedCallerAsync(db, authorityStore, subject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            tenantId, ProjectAuthorityRole.TenantAdmin);
        var service = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System,
            modelBindings: ProjectModelChoicesTests.SampleBindings());
        var project = await service.CreateProjectAsync(owner, "Shared TLS publication", CancellationToken.None);
        var connectionId = Guid.NewGuid();
        await service.UpdateProjectConfigurationAsync(owner, project.ProjectId, project.ConfigurationRevision,
            new ProjectConfiguration
            {
                ModelSelection = new ModelSelectionSettings("hosted-reasoning",
                    SourceMode: ModelSourceMode.HostedCopilot, ConnectionId: connectionId),
            }, CancellationToken.None);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = TestIssuer,
            Audience = "shared-listener-tests",
            Subject = new ClaimsIdentity([new Claim("sub", subject), new Claim("scope", "api.read projects.admin")]),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(PolicySourceTestSigningKey),
                SecurityAlgorithms.HmacSha256),
        });
        using var certificates = new PlatformPolicySourceTlsTests.PolicySourceCertificates();
        await using var host = await StartRealPlatformPolicySourceHostAsync(certificates, includeProjectRoutes: true);
        var address = new Uri(Assert.Single(host.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses));
        using var environmentClient = Client(certificates.EnvironmentClient);
        using var agentHostClient = Client(certificates.AgentHostClient);
        using var bearerOnlyClient = Client(null);
        var publicationPath = $"/api/projects/{project.ProjectId}/configuration/hosted-model-reasoning-catalog";
        var json = $$"""{"bindingsRevision":"models-r7","reference":"hosted-reasoning","connectionId":"{{connectionId}}","supportedReasoningEfforts":["high"],"verifiedAt":"{{DateTimeOffset.UtcNow:O}}"}""";

        using var environmentPublication = await environmentClient.PostAsync(publicationPath,
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, environmentPublication.StatusCode);
        using var agentHostSource = await agentHostClient.GetAsync(PlatformPolicySourceTls.RoutePath);
        Assert.Equal(HttpStatusCode.Forbidden, agentHostSource.StatusCode);
        using var bearerSource = await bearerOnlyClient.GetAsync(PlatformPolicySourceTls.RoutePath);
        Assert.Equal(HttpStatusCode.Forbidden, bearerSource.StatusCode);
        using var publication = await agentHostClient.PostAsync(publicationPath,
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, publication.StatusCode);
        agentHostClient.DefaultRequestHeaders.Authorization = null;
        using var noBearerPublication = await agentHostClient.PostAsync(publicationPath,
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, noBearerPublication.StatusCode);

        HttpClient Client(System.Security.Cryptography.X509Certificates.X509Certificate2? certificate)
        {
            var handler = new HttpClientHandler
            {
                ClientCertificateOptions = ClientCertificateOption.Manual,
                ServerCertificateCustomValidationCallback = (_, peer, _, _) =>
                    peer is not null && peer.RawData.AsSpan().SequenceEqual(certificates.ServerCertificate.RawData),
            };
            if (certificate is not null)
                handler.ClientCertificates.Add(certificate);
            var client = new HttpClient(handler) { BaseAddress = address };
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            client.DefaultRequestHeaders.Add(ProjectAuthorizationOwner.TenantSelectorHeader, tenantId);
            return client;
        }
    }

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
        await using var host = await StartRealPlatformPolicySourceHostAsync(certificates);
        using var handler = new HttpClientHandler
        {
            ClientCertificateOptions = ClientCertificateOption.Manual,
            ServerCertificateCustomValidationCallback = (_, peer, _, _) =>
                peer is not null && peer.RawData.AsSpan().SequenceEqual(certificates.ServerCertificate.RawData),
        };
        handler.ClientCertificates.Add(certificates.EnvironmentClient);
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(Assert.Single(host.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses)),
        };

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
        using var revokedRecheckClient = change == "revocation"
            ? certificates.CreateClient(
                PlatformPolicySourceTls.EnvironmentDnsName, clientAuthentication: true)
            : null;
        var now = DateTimeOffset.UtcNow;
        var retirement = change == "pin-retirement" ? now.AddSeconds(5) : now.AddDays(2);
        var clock = new MutableTimeProvider(now);
        var identity = revokedRecheckClient is null
            ? certificates.CreateIdentity(retireAtUtc: retirement, timeProvider: clock)
            : certificates.CreateIdentity([revokedRecheckClient], retireAtUtc: retirement, timeProvider: clock);
        TestTlsConnectionFeature? tlsConnectionFeature = null;
        var interceptor = new PolicyReadInterceptor(cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (change == "pin-retirement")
                clock.SetUtcNow(retirement);
            else if (change == "ca-change")
                File.WriteAllText(certificates.CaPath, certificates.OtherCa.ExportCertificatePem());
            else
            {
                // TestServer switches the peer after the read so the final check sees the revoked leaf.
                tlsConnectionFeature!.ClientCertificate = revokedRecheckClient;
            }
            return Task.CompletedTask;
        });
        using var host = await StartPlatformPolicySourceEndpointHostAsync(
            fixture.DataSource,
            identity,
            certificates.EnvironmentClient,
            interceptor,
            onTlsConnectionFeature: feature => tlsConnectionFeature = feature);
        using var client = host.GetTestClient();
        if (revokedRecheckClient is not null)
            certificates.Revoke(revokedRecheckClient);

        using var response = await client.GetAsync(PlatformPolicySourceTls.RoutePath);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("policy_source_peer_denied", await ReadErrorCodeAsync(response));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(receipt.ContentSha256, body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"artifact\"", body, StringComparison.Ordinal);
        Assert.True(clock.GetUtcNow() < certificates.EnvironmentClient.NotAfter.ToUniversalTime());
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
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("{\"code\":\"policy_source_internal_error\"}", await response.Content.ReadAsStringAsync());
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
        var blobReadObserver = new Action<DbDataReader>(reader =>
        {
            for (var index = 0; index < reader.FieldCount; index++)
            {
                if (!string.Equals(reader.GetName(index), "bytes", StringComparison.OrdinalIgnoreCase))
                    continue;
                Interlocked.Increment(ref blobReads);
                break;
            }
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
        string relativePath;
        await using (var read = CreateDbContext(fixture.DataSource))
        {
            var file = await read.NativeAcsPolicyArtifactFiles.AsNoTracking()
                .Where(item => item.PolicyId == receipt.PolicyId && item.Revision == receipt.Revision)
                .OrderBy(item => item.RelativePath)
                .FirstAsync();
            relativePath = file.RelativePath;
            originalBytes = file.Bytes.ToArray();
        }
        var corruptedBytes = originalBytes.ToArray();
        corruptedBytes[0] ^= 0xff;
        var stateBeforeCorruption = await CapturePolicyTableStateAsync();
        await WithArtifactFilesImmutableTriggerDisabledAsync(
            () => SetArtifactFileBytesAsync(receipt.PolicyId, receipt.Revision, relativePath, corruptedBytes));
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
                () => SetArtifactFileBytesAsync(receipt.PolicyId, receipt.Revision, relativePath, originalBytes));
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

    private async Task<WebApplication> StartRealPlatformPolicySourceHostAsync(
        PlatformPolicySourceTlsTests.PolicySourceCertificates certificates,
        bool includeProjectRoutes = false)
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var values = certificates.Configuration();
        values["urls"] = $"https://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(values);
        BrokerRunBindingTlsIdentity.Configure(builder, true, applyHttpsDefaults: false);
        if (includeProjectRoutes)
        {
            builder.Configuration[$"{HostedModelCatalogPublicationTls.SectionName}:AgentHostCertificateSha256"] =
                certificates.AgentHostPin;
            HostedModelCatalogPublicationTls.Configure(builder, certificates.CaPath, applyHttpsDefaults: false);
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = TestIssuer,
                    ValidAudience = "shared-listener-tests",
                    IssuerSigningKey = new SymmetricSecurityKey(PolicySourceTestSigningKey),
                    ValidateIssuerSigningKey = true,
                };
            });
            builder.Services.AddAuthorization();
            builder.Services.AddSingleton(CreateProviderCatalog());
            builder.Services.AddSingleton(ProjectModelChoicesTests.SampleBindings());
            builder.Services.AddSingleton(new ProjectsConfigIdentityOptions(TestIssuer));
            builder.Services.AddScoped<ProjectAuthorizationOwner>();
            builder.Services.AddScoped<ProjectsConfigService>();
            builder.Services.AddScoped<ModelProviderDraftService>();
            builder.Services.AddScoped<ProjectSetupAuthorityAuditService>();
            builder.Services.AddHttpClient<KnowledgeAgentHistoryClient>(client =>
                client.BaseAddress = new Uri("https://knowledge.test/"));
        }
        PlatformPolicySourceTls.Configure(builder, certificates.CaPath);
        builder.Services.AddDbContextFactory<ProjectsConfigDbContext>(options =>
            options.UseNpgsql(fixture.DataSource,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", ProjectsConfigDbContext.Schema)));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<NativeAcsPolicyStore>();
        var app = builder.Build();
        app.UsePlatformPolicySourceGuard(port);
        if (includeProjectRoutes)
        {
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapProjectConfigEndpoints();
        }
        app.MapPlatformPolicySource();
        await app.StartAsync();
        return app;
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
        Action? onAuthentication = null,
        Action<TestTlsConnectionFeature>? onTlsConnectionFeature = null) =>
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
                        context.Connection.LocalPort = 8443;
                        context.Request.Scheme = "https";
                        context.Features.Set<ITlsHandshakeFeature>(new TestTlsHandshakeFeature());
                        var tlsConnectionFeature = new TestTlsConnectionFeature
                        {
                            ClientCertificate = clientCertificate,
                        };
                        context.Features.Set<ITlsConnectionFeature>(tlsConnectionFeature);
                        onTlsConnectionFeature?.Invoke(tlsConnectionFeature);
                        await next();
                    });
                    app.UsePlatformPolicySourceGuard(8443);
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
