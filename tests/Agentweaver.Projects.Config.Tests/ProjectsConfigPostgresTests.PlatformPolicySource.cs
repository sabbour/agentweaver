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
using Microsoft.AspNetCore.DataProtection;
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
        var proposedArtifact = HistoryArtifact("uncommitted preparation " + suffix);
        var preparation = await CreateNativeAcsStore().PrepareAsync(
            caller,
            new NativeAcsPolicyWriteRequest(
                NativeAcsPolicyScopeType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId,
                receipt.PolicyId,
                receipt.Revision,
                proposedArtifact,
                "policy-source-preparation-" + suffix,
                "Prepare policy update",
                "policy-source-preparation-correlation-" + suffix),
            CancellationToken.None);
        Assert.Equal("pending", preparation.State);
        Assert.Null(preparation.Checkpoint);
        Assert.NotEqual(receipt.ContentSha256, preparation.ProposedContentSha256);
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
        bool includeProjectRoutes = false,
        Action? onSourceRequest = null)
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
        builder.Services.AddSingleton<IDataProtectionProvider>(CreateTestDataProtectionProvider());
        builder.Services.AddScoped<NativeAcsPolicyStore>();
        var app = builder.Build();
        app.UsePlatformPolicySourceGuard(port);
        if (onSourceRequest is not null)
        {
            app.Use(async (context, next) =>
            {
                if (context.Request.Path == PlatformPolicySourceTls.RoutePath)
                    onSourceRequest();
                await next(context);
            });
        }
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
                    services.AddSingleton<IDataProtectionProvider>(CreateTestDataProtectionProvider());
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

    [Fact]
    public async Task PrivatePlatformPolicyPreparationReadReturnsExactImmutableRowAcrossHeadChangesAndNullExpectedHead()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "prep-read-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, "prep-read-admin-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var store = CreateNativeAcsStore();
        var existingHead = await store.ReadAsync(
            NativeAcsPolicyScopeType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            CancellationToken.None);

        var prep0Artifact = CreatePolicySourceArtifact(
            "{\"version\":\"prep0\"}"u8.ToArray(),
            [("policies/egress.yaml", "allow: [api.github.com]"u8.ToArray())]);
        var prep0Receipt = await store.PrepareAsync(caller, new NativeAcsPolicyWriteRequest(
            NativeAcsPolicyScopeType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            existingHead?.PolicyId ?? "platform-egress",
            existingHead?.Revision,
            prep0Artifact,
            "idem-prep0-" + suffix,
            "initial prep",
            "corr-prep0-" + suffix), CancellationToken.None);

        var head1Artifact = CreatePolicySourceArtifact(
            "{\"version\":\"head1\"}"u8.ToArray(),
            [("policies/egress.yaml", "allow: [head1.example.com]"u8.ToArray())]);
        var head1Receipt = await WritePlatformPolicyAsync(
            caller, head1Artifact, "head1-" + suffix, "corr-head1-" + suffix);

        var prep1Artifact = CreatePolicySourceArtifact(
            "{\"version\":\"prep1\"}"u8.ToArray(),
            [("policies/egress.yaml", "allow: [prep1.example.com]"u8.ToArray())]);
        var prep1Receipt = await store.PrepareAsync(caller, new NativeAcsPolicyWriteRequest(
            NativeAcsPolicyScopeType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            head1Receipt.PolicyId,
            head1Receipt.Revision,
            prep1Artifact,
            "idem-prep1-" + suffix,
            "second prep",
            "corr-prep1-" + suffix), CancellationToken.None);

        var head2Artifact = CreatePolicySourceArtifact(
            "{\"version\":\"head2\"}"u8.ToArray(),
            [("policies/egress.yaml", "allow: [head2.example.com]"u8.ToArray())]);
        var head2Receipt = await WritePlatformPolicyAsync(
            caller, head2Artifact, "head2-" + suffix, "corr-head2-" + suffix);

        var nullHeadPrepId = Guid.NewGuid();
        var nullHeadArtifactJson = JsonSerializer.Serialize(
            prep0Artifact, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO projects_config.native_acs_policy_preparations
            (preparation_id, scope_digest, scope_type, scope_id, policy_id, actor_issuer, actor_id,
             membership_id, membership_revision, role_assignment_id, role_assignment_revision,
             idempotency_key, request_digest, proposed_content_sha256, artifact, reason, correlation_id, created_at)
            VALUES ({nullHeadPrepId}, {Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("null-head-" + suffix))).ToLowerInvariant()},
                    'Platform', 'default', 'platform-egress', 'https://issuer.example', 'actor',
                    {Guid.NewGuid()}, 1, {Guid.NewGuid()}, 1,
                    {"idem-null-" + suffix}, {new string('9', 64)}, {prep0Artifact.ContentSha256},
                    CAST({nullHeadArtifactJson} AS jsonb), 'null expected head', 'corr-null', NOW())
            """);
        try
        {
            using var certificates = new PlatformPolicySourceTlsTests.PolicySourceCertificates();
            var identity = certificates.CreateIdentity(workerPins: [certificates.PreparationWorkerClient]);
            using var workerHost = await StartPlatformPolicySourceEndpointHostAsync(
                fixture.DataSource, identity, certificates.PreparationWorkerClient);
            using var workerClient = workerHost.GetTestClient();

            using var nullHeadResponse = await workerClient.GetAsync(
                $"{PlatformPolicySourceTls.PreparationRoutePrefix}{nullHeadPrepId:D}");
            Assert.Equal(HttpStatusCode.OK, nullHeadResponse.StatusCode);
            Assert.True(nullHeadResponse.Headers.CacheControl?.NoStore);
            using var nullHeadJson = JsonDocument.Parse(await nullHeadResponse.Content.ReadAsStringAsync());
            Assert.Equal(
                ["artifact", "expectedHead", "proposedContentSha256"],
                nullHeadJson.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.Equal(JsonValueKind.Null, nullHeadJson.RootElement.GetProperty("expectedHead").ValueKind);
            Assert.Equal(prep0Artifact.ContentSha256, nullHeadJson.RootElement.GetProperty("proposedContentSha256").GetString());

            using var prep0Response = await workerClient.GetAsync(
                $"{PlatformPolicySourceTls.PreparationRoutePrefix}{prep0Receipt.PreparationId:D}");
            Assert.Equal(HttpStatusCode.OK, prep0Response.StatusCode);
            using var prep0Json = JsonDocument.Parse(await prep0Response.Content.ReadAsStringAsync());
            AssertArtifactEquals(prep0Artifact, prep0Json.RootElement.GetProperty("artifact"));

            using var prep1Response = await workerClient.GetAsync(
                $"{PlatformPolicySourceTls.PreparationRoutePrefix}{prep1Receipt.PreparationId:D}");
            Assert.Equal(HttpStatusCode.OK, prep1Response.StatusCode);
            Assert.True(prep1Response.Headers.CacheControl?.NoStore);
            using var prep1Json = JsonDocument.Parse(await prep1Response.Content.ReadAsStringAsync());
            Assert.Equal(
                ["artifact", "expectedHead", "proposedContentSha256"],
                prep1Json.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            AssertArtifactEquals(prep1Artifact, prep1Json.RootElement.GetProperty("artifact"));
            var expectedHead = prep1Json.RootElement.GetProperty("expectedHead");
            Assert.Equal(
                ["contentSha256", "policyId", "revision"],
                expectedHead.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.Equal(head1Receipt.PolicyId, expectedHead.GetProperty("policyId").GetString());
            Assert.Equal(head1Receipt.Revision, expectedHead.GetProperty("revision").GetInt64());
            Assert.Equal(head1Receipt.ContentSha256, expectedHead.GetProperty("contentSha256").GetString());
            Assert.Equal(prep1Artifact.ContentSha256, prep1Json.RootElement.GetProperty("proposedContentSha256").GetString());

            using var envHost = await StartPlatformPolicySourceEndpointHostAsync(
                fixture.DataSource, identity, certificates.EnvironmentClient);
            using var envClient = envHost.GetTestClient();
            using var headResponse = await envClient.GetAsync(PlatformPolicySourceTls.RoutePath);
            Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
            using var headJson = JsonDocument.Parse(await headResponse.Content.ReadAsStringAsync());
            Assert.Equal(head2Receipt.Revision, headJson.RootElement.GetProperty("revision").GetInt64());
            Assert.Equal(head2Receipt.ContentSha256, headJson.RootElement.GetProperty("contentSha256").GetString());
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE projects_config.native_acs_policy_preparations DISABLE TRIGGER native_acs_policy_preparations_immutable");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM projects_config.native_acs_policy_preparations WHERE preparation_id = {nullHeadPrepId}");
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE projects_config.native_acs_policy_preparations ENABLE TRIGGER native_acs_policy_preparations_immutable");
        }
    }

    [Fact]
    public async Task PrivatePlatformPolicyPreparationReadRejectsMalformedIdsMissingRowsWrongScopeAndQueryOrBody()
    {
        using var certificates = new PlatformPolicySourceTlsTests.PolicySourceCertificates();
        var identity = certificates.CreateIdentity(workerPins: [certificates.PreparationWorkerClient]);
        var dbQueries = 0;
        var countingInterceptor = new PolicyReadInterceptor(_ =>
        {
            Interlocked.Increment(ref dbQueries);
            return Task.CompletedTask;
        }, "native_acs_policy_preparations");
        using var host = await StartPlatformPolicySourceEndpointHostAsync(
            fixture.DataSource, identity, certificates.PreparationWorkerClient, countingInterceptor);
        using var client = host.GetTestClient();

        var validUuid = Guid.NewGuid().ToString("D");
        foreach (var badId in new[] { "not-a-uuid", Guid.Empty.ToString("D"), Guid.NewGuid().ToString("N"), $"{validUuid}%20", $"%20{validUuid}" })
        {
            using var badResponse = await client.GetAsync($"{PlatformPolicySourceTls.PreparationRoutePrefix}{badId}");
            Assert.Equal(HttpStatusCode.BadRequest, badResponse.StatusCode);
            Assert.Equal("{\"code\":\"policy_source_request_invalid\"}", await badResponse.Content.ReadAsStringAsync());
        }
        Assert.Equal(0, dbQueries);

        using var missingResponse = await client.GetAsync($"{PlatformPolicySourceTls.PreparationRoutePrefix}{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
        Assert.Equal("{\"code\":\"native_acs_policy_not_found\"}", await missingResponse.Content.ReadAsStringAsync());

        var validPath = $"{PlatformPolicySourceTls.PreparationRoutePrefix}{Guid.NewGuid():D}";
        using var queryResponse = await client.GetAsync($"{validPath}?x=1");
        Assert.Equal(HttpStatusCode.BadRequest, queryResponse.StatusCode);
        Assert.Equal("{\"code\":\"policy_source_request_invalid\"}", await queryResponse.Content.ReadAsStringAsync());

        using var bodyRequest = new HttpRequestMessage(HttpMethod.Get, validPath)
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        using var bodyResponse = await client.SendAsync(bodyRequest);
        Assert.Equal(HttpStatusCode.BadRequest, bodyResponse.StatusCode);
        Assert.Equal("{\"code\":\"policy_source_request_invalid\"}", await bodyResponse.Content.ReadAsStringAsync());

        // Insert a non-Platform scope row by temporarily relaxing the scope check constraint.
        var foreignId = Guid.NewGuid();
        var artifact = CreatePolicySourceArtifact("{\"v\":1}"u8.ToArray(), []);
        var artifactJson = JsonSerializer.Serialize(artifact, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await using var adminDb = CreateDbContext();
        await adminDb.Database.ExecuteSqlRawAsync(
            "ALTER TABLE projects_config.native_acs_policy_preparations DROP CONSTRAINT ck_native_acs_policy_preparations_scope");
        try
        {
            await adminDb.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO projects_config.native_acs_policy_preparations
                (preparation_id, scope_digest, scope_type, scope_id, policy_id, actor_issuer, actor_id,
                 membership_id, membership_revision, role_assignment_id, role_assignment_revision,
                 idempotency_key, request_digest, proposed_content_sha256, artifact, reason, correlation_id, created_at)
                VALUES ({foreignId}, {new string('a', 64)}, 'Project', {Guid.NewGuid().ToString("N")}, 'p',
                        'https://issuer.example', 'actor', {Guid.NewGuid()}, 1, {Guid.NewGuid()}, 1,
                        'idem', {new string('b', 64)}, {artifact.ContentSha256}, CAST({artifactJson} AS jsonb),
                        'wrong scope', 'corr', NOW())
                """);
            using var wrongScopeResponse = await client.GetAsync($"{PlatformPolicySourceTls.PreparationRoutePrefix}{foreignId:D}");
            Assert.Equal(HttpStatusCode.NotFound, wrongScopeResponse.StatusCode);
            Assert.Equal("{\"code\":\"native_acs_policy_not_found\"}", await wrongScopeResponse.Content.ReadAsStringAsync());
        }
        finally
        {
            await adminDb.Database.ExecuteSqlRawAsync(
                "ALTER TABLE projects_config.native_acs_policy_preparations DISABLE TRIGGER native_acs_policy_preparations_immutable");
            await adminDb.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM projects_config.native_acs_policy_preparations WHERE preparation_id = {foreignId}");
            await adminDb.Database.ExecuteSqlRawAsync(
                "ALTER TABLE projects_config.native_acs_policy_preparations ENABLE TRIGGER native_acs_policy_preparations_immutable");
            await adminDb.Database.ExecuteSqlRawAsync(
                "ALTER TABLE projects_config.native_acs_policy_preparations ADD CONSTRAINT ck_native_acs_policy_preparations_scope CHECK (scope_type = 'Platform' AND scope_id = 'default')");
        }
    }

    [Fact]
    public async Task PrivatePlatformPolicyPreparationReadSucceedsAtMaxDecodedArtifactBoundary()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "prep-max-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, "prep-max-admin-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var store = CreateNativeAcsStore();
        var existingHead = await store.ReadAsync(
            NativeAcsPolicyScopeType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            CancellationToken.None);

        // Fill 1,048,576 bytes with 3-digit byte values (255) across 65,536 manifest + 64 files (15,360 bytes each)
        // so PostgreSQL jsonb::text exceeds 5,242,880 bytes while staying under the storage cap and HTTP response cap.
        var maxManifest = Enumerable.Repeat((byte)255, 65_536).ToArray();
        var perFileBytes = Enumerable.Repeat((byte)255, 15_360).ToArray();
        var maxFiles = Enumerable.Range(0, 64)
            .Select(i => ($"policies/file-{i:D2}.yaml", perFileBytes))
            .ToArray();
        var maxArtifact = CreatePolicySourceArtifact(maxManifest, maxFiles);

        var receipt = await store.PrepareAsync(caller, new NativeAcsPolicyWriteRequest(
            NativeAcsPolicyScopeType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            existingHead?.PolicyId ?? "platform-egress",
            existingHead?.Revision,
            maxArtifact,
            "idem-max-" + suffix,
            "max boundary prep",
            "corr-max-" + suffix), CancellationToken.None);

        using var certificates = new PlatformPolicySourceTlsTests.PolicySourceCertificates();
        var identity = certificates.CreateIdentity(workerPins: [certificates.PreparationWorkerClient]);
        using var host = await StartPlatformPolicySourceEndpointHostAsync(
            fixture.DataSource, identity, certificates.PreparationWorkerClient);
        using var client = host.GetTestClient();

        using var response = await client.GetAsync(
            $"{PlatformPolicySourceTls.PreparationRoutePrefix}{receipt.PreparationId:D}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(maxArtifact.ContentSha256, json.RootElement.GetProperty("proposedContentSha256").GetString());
        AssertArtifactEquals(maxArtifact, json.RootElement.GetProperty("artifact"));
    }

    [Fact]
    public async Task PrivatePlatformPolicyPreparationReadRejectsCorruptDigestAndOversizedArtifact()
    {
        using var certificates = new PlatformPolicySourceTlsTests.PolicySourceCertificates();
        var identity = certificates.CreateIdentity(workerPins: [certificates.PreparationWorkerClient]);
        var interceptor = new PreparationQueryCaptureInterceptor();
        using var host = await StartPlatformPolicySourceEndpointHostAsync(
            fixture.DataSource, identity, certificates.PreparationWorkerClient, interceptor);
        using var client = host.GetTestClient();

        var corruptDigestId = Guid.NewGuid();
        var oversizedId = Guid.NewGuid();
        var oversizedJsonId = Guid.NewGuid();
        var validArtifact = CreatePolicySourceArtifact("{\"v\":1}"u8.ToArray(), []);
        var validArtifactJson = JsonSerializer.Serialize(validArtifact, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var oversizedArtifact = CreatePolicySourceArtifact(new byte[65_537], []);
        var oversizedJson = JsonSerializer.Serialize(oversizedArtifact, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var oversizedJsonPayload = $$"""{"manifestBytes":"{{Convert.ToBase64String("{\"v\":1}"u8.ToArray())}}","files":[],"contentSha256":"{{validArtifact.ContentSha256}}","padding":"{{new string('x', 5_550_000)}}"}""";

        await using var adminDb = CreateDbContext();
        try
        {
            await adminDb.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO projects_config.native_acs_policy_preparations
                (preparation_id, scope_digest, scope_type, scope_id, policy_id, actor_issuer, actor_id,
                 membership_id, membership_revision, role_assignment_id, role_assignment_revision,
                 idempotency_key, request_digest, proposed_content_sha256, artifact, reason, correlation_id, created_at)
                VALUES ({corruptDigestId}, {new string('c', 64)}, 'Platform', 'default', 'p',
                        'https://issuer.example', 'actor', {Guid.NewGuid()}, 1, {Guid.NewGuid()}, 1,
                        'idem-corrupt', {new string('d', 64)}, {new string('e', 64)}, CAST({validArtifactJson} AS jsonb),
                        'corrupt digest', 'corr', NOW())
                """);
            await adminDb.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO projects_config.native_acs_policy_preparations
                (preparation_id, scope_digest, scope_type, scope_id, policy_id, actor_issuer, actor_id,
                 membership_id, membership_revision, role_assignment_id, role_assignment_revision,
                 idempotency_key, request_digest, proposed_content_sha256, artifact, reason, correlation_id, created_at)
                VALUES ({oversizedId}, {new string('f', 64)}, 'Platform', 'default', 'p',
                        'https://issuer.example', 'actor', {Guid.NewGuid()}, 1, {Guid.NewGuid()}, 1,
                        'idem-oversized', {new string('1', 64)}, {oversizedArtifact.ContentSha256}, CAST({oversizedJson} AS jsonb),
                        'oversized', 'corr', NOW())
                """);
            await adminDb.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO projects_config.native_acs_policy_preparations
                (preparation_id, scope_digest, scope_type, scope_id, policy_id, actor_issuer, actor_id,
                 membership_id, membership_revision, role_assignment_id, role_assignment_revision,
                 idempotency_key, request_digest, proposed_content_sha256, artifact, reason, correlation_id, created_at)
                VALUES ({oversizedJsonId}, {new string('2', 64)}, 'Platform', 'default', 'p',
                        'https://issuer.example', 'actor', {Guid.NewGuid()}, 1, {Guid.NewGuid()}, 1,
                        'idem-oversized-json', {new string('3', 64)}, {validArtifact.ContentSha256}, CAST({oversizedJsonPayload} AS jsonb),
                        'oversized json', 'corr', NOW())
                """);

            var metaBeforeCorrupt = interceptor.MetadataQueries;
            var fetchBeforeCorrupt = interceptor.ArtifactFetchQueries;
            using var corruptResponse = await client.GetAsync($"{PlatformPolicySourceTls.PreparationRoutePrefix}{corruptDigestId:D}");
            Assert.Equal(HttpStatusCode.InternalServerError, corruptResponse.StatusCode);
            var corruptBody = await corruptResponse.Content.ReadAsStringAsync();
            Assert.Equal("{\"code\":\"native_acs_policy_corrupt\"}", corruptBody);
            Assert.Equal(metaBeforeCorrupt + 1, interceptor.MetadataQueries);
            Assert.Equal(fetchBeforeCorrupt + 1, interceptor.ArtifactFetchQueries);

            using var oversizedResponse = await client.GetAsync($"{PlatformPolicySourceTls.PreparationRoutePrefix}{oversizedId:D}");
            Assert.Equal(HttpStatusCode.InternalServerError, oversizedResponse.StatusCode);
            var oversizedBody = await oversizedResponse.Content.ReadAsStringAsync();
            Assert.Equal("{\"code\":\"native_acs_policy_corrupt\"}", oversizedBody);

            var metaBeforeJsonCap = interceptor.MetadataQueries;
            var fetchBeforeJsonCap = interceptor.ArtifactFetchQueries;
            using var oversizedJsonResponse = await client.GetAsync($"{PlatformPolicySourceTls.PreparationRoutePrefix}{oversizedJsonId:D}");
            Assert.Equal(HttpStatusCode.InternalServerError, oversizedJsonResponse.StatusCode);
            Assert.Equal("{\"code\":\"native_acs_policy_corrupt\"}", await oversizedJsonResponse.Content.ReadAsStringAsync());
            // Only the metadata length query ran; the artifact-only fetch query never executed.
            Assert.Equal(metaBeforeJsonCap + 1, interceptor.MetadataQueries);
            Assert.Equal(fetchBeforeJsonCap, interceptor.ArtifactFetchQueries);
        }
        finally
        {
            await adminDb.Database.ExecuteSqlRawAsync(
                "ALTER TABLE projects_config.native_acs_policy_preparations DISABLE TRIGGER native_acs_policy_preparations_immutable");
            await adminDb.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM projects_config.native_acs_policy_preparations WHERE preparation_id IN ({corruptDigestId}, {oversizedId}, {oversizedJsonId})");
            await adminDb.Database.ExecuteSqlRawAsync(
                "ALTER TABLE projects_config.native_acs_policy_preparations ENABLE TRIGGER native_acs_policy_preparations_immutable");
        }
    }

    [Fact]
    public async Task PrivatePlatformPolicyPreparationReadRejectsWrongPeerInBothDirectionsAndPostBufferRevocation()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "prep-peer-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, "prep-peer-admin-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var store = CreateNativeAcsStore();
        var existingHead = await store.ReadAsync(
            NativeAcsPolicyScopeType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            CancellationToken.None);
        var prepArtifact = CreatePolicySourceArtifact(
            "{\"version\":\"peer-check\"}"u8.ToArray(),
            [("policies/egress.yaml", "allow: [peer-check.example.com]"u8.ToArray())]);
        var prepReceipt = await store.PrepareAsync(caller, new NativeAcsPolicyWriteRequest(
            NativeAcsPolicyScopeType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            existingHead?.PolicyId ?? "platform-egress",
            existingHead?.Revision,
            prepArtifact,
            "idem-peer-" + suffix,
            "peer check prep",
            "corr-peer-" + suffix), CancellationToken.None);

        using var certificates = new PlatformPolicySourceTlsTests.PolicySourceCertificates();
        var identity = certificates.CreateIdentity(
            workerPins: [certificates.PreparationWorkerClient]);
        var prepPath = $"{PlatformPolicySourceTls.PreparationRoutePrefix}{prepReceipt.PreparationId:D}";

        // Environment cert cannot read preparation route.
        using (var envHost = await StartPlatformPolicySourceEndpointHostAsync(
            fixture.DataSource, identity, certificates.EnvironmentClient))
        using (var envClient = envHost.GetTestClient())
        using (var envOnPrep = await envClient.GetAsync(prepPath))
        {
            Assert.Equal(HttpStatusCode.Forbidden, envOnPrep.StatusCode);
            Assert.Equal("policy_source_peer_denied", await ReadErrorCodeAsync(envOnPrep));
            Assert.DoesNotContain(prepArtifact.ContentSha256, await envOnPrep.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // Worker cert cannot read head route.
        using (var workerHost = await StartPlatformPolicySourceEndpointHostAsync(
            fixture.DataSource, identity, certificates.PreparationWorkerClient))
        using (var workerClient = workerHost.GetTestClient())
        using (var workerOnHead = await workerClient.GetAsync(PlatformPolicySourceTls.RoutePath))
        {
            Assert.Equal(HttpStatusCode.Forbidden, workerOnHead.StatusCode);
            Assert.Equal("policy_source_peer_denied", await ReadErrorCodeAsync(workerOnHead));
        }

        // Post-buffer CRL revocation of the same leaf mid-request denies before sending bytes.
        var revokedMidRequest = 0;
        var revocationPublished = 0;
        var interceptor = new PolicyReadInterceptor(async cancellationToken =>
        {
            if (Interlocked.Exchange(ref revokedMidRequest, 1) == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(3100), cancellationToken);
                certificates.Revoke(certificates.PreparationWorkerClient);
                Volatile.Write(ref revocationPublished, 1);
            }
        }, "native_acs_policy_preparations");
        using (var recheckHost = await StartPlatformPolicySourceEndpointHostAsync(
            fixture.DataSource,
            identity,
            certificates.PreparationWorkerClient,
            interceptor))
        using (var recheckClient = recheckHost.GetTestClient())
        {
            certificates.SetUnrevokedCrl(TimeSpan.FromSeconds(3));
            using var revokedResponse = await recheckClient.GetAsync(prepPath);
            Assert.Equal(1, Volatile.Read(ref revokedMidRequest));
            Assert.Equal(1, Volatile.Read(ref revocationPublished));
            Assert.Equal(HttpStatusCode.Forbidden, revokedResponse.StatusCode);
            Assert.Equal("policy_source_peer_denied", await ReadErrorCodeAsync(revokedResponse));
            var body = await revokedResponse.Content.ReadAsStringAsync();
            Assert.DoesNotContain(prepArtifact.ContentSha256, body, StringComparison.Ordinal);
            Assert.DoesNotContain("\"artifact\"", body, StringComparison.Ordinal);
        }
    }

    private sealed class PreparationQueryCaptureInterceptor : DbCommandInterceptor
    {
        private int _metadataQueries;
        private int _artifactFetchQueries;

        internal int MetadataQueries => Volatile.Read(ref _metadataQueries);
        internal int ArtifactFetchQueries => Volatile.Read(ref _artifactFetchQueries);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("native_acs_policy_preparations", StringComparison.Ordinal))
            {
                if (command.CommandText.Contains("octet_length", StringComparison.Ordinal))
                    Interlocked.Increment(ref _metadataQueries);
                else
                    Interlocked.Increment(ref _artifactFetchQueries);
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static void AssertArtifactEquals(NativeAcsArtifact expected, JsonElement actual)
    {
        var actualArtifact = actual.Deserialize<NativeAcsArtifact>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(actualArtifact);
        Assert.Equal(expected.ManifestBytes.ToArray(), actualArtifact.ManifestBytes.ToArray());
        Assert.Equal(expected.ContentSha256, actualArtifact.ContentSha256);
        Assert.Equal(expected.Files.Length, actualArtifact.Files.Length);
        for (var i = 0; i < expected.Files.Length; i++)
        {
            Assert.Equal(expected.Files[i].RelativePath, actualArtifact.Files[i].RelativePath);
            Assert.Equal(expected.Files[i].Bytes.ToArray(), actualArtifact.Files[i].Bytes.ToArray());
        }
    }

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
