extern alias EnvironmentService;

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Policy.Acs;
using Env = EnvironmentService::Agentweaver.Environment;
using EnvironmentProgram = EnvironmentService::Program;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task ProductionEnvironmentReadsRealPendingSourceWithoutAdmissionOrWorkers()
    {
        var firstArtifact = FiniteNetworkArtifact("approved.example.com");
        var firstReceipt = await SeedPlatformPolicySourceArtifactAsync("environment-client-first", firstArtifact);
        using var certificates = new PlatformPolicySourceTlsTests.PolicySourceCertificates();
        var sourceRequests = 0;
        await using var projectsHost = await StartRealPlatformPolicySourceHostAsync(
            certificates,
            onSourceRequest: () => Interlocked.Increment(ref sourceRequests));
        var projectsAddress = new Uri(Assert.Single(
            projectsHost.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses));

        var environmentMigrationOptions = new DbContextOptionsBuilder<Env.EnvironmentDbContext>()
            .UseNpgsql(fixture.DataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", Env.EnvironmentDbContext.Schema))
            .Options;
        await Env.EnvironmentMigrator.MigrateAsync(fixture.DataSource, environmentMigrationOptions);

        var runtimeRole = "aw_environment_source_" + Guid.NewGuid().ToString("N");
        var runtimePassword = Guid.NewGuid().ToString("N");
        var roleCreated = false;
        async Task ExecuteOwnerSqlAsync(string sql)
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        NpgsqlDataSource? runtimeDataSource = null;
        var contentRoot = Path.Combine(
            Directory.GetCurrentDirectory(),
            ".environment-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        try
        {
            WriteEnvironmentProductionSettings(contentRoot);
            await ExecuteOwnerSqlAsync(
                $"CREATE ROLE \"{runtimeRole}\" LOGIN PASSWORD '{runtimePassword}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT");
            roleCreated = true;
            await ExecuteOwnerSqlAsync($"GRANT USAGE ON SCHEMA environment TO \"{runtimeRole}\"");
            await ExecuteOwnerSqlAsync(
                $"GRANT SELECT ON environment.__ef_migrations_history TO \"{runtimeRole}\"");
            await ExecuteOwnerSqlAsync($"GRANT SELECT, INSERT, UPDATE ON environment.owners TO \"{runtimeRole}\"");
            await ExecuteOwnerSqlAsync(
                $"GRANT SELECT, INSERT ON environment.lifecycle_operations TO \"{runtimeRole}\"");
            await ExecuteOwnerSqlAsync($"GRANT SELECT, INSERT, UPDATE ON environment.owner_effects TO \"{runtimeRole}\"");
            await ExecuteOwnerSqlAsync(
                $"GRANT SELECT, INSERT, UPDATE ON environment.sandbox_leases TO \"{runtimeRole}\"");
            await ExecuteOwnerSqlAsync(
                $"GRANT SELECT, INSERT, UPDATE ON environment.sandbox_provider_attempts TO \"{runtimeRole}\"");
            await ExecuteOwnerSqlAsync(
                $"GRANT SELECT, INSERT, UPDATE ON environment.sandbox_late_resource_cleanups TO \"{runtimeRole}\"");
            await ExecuteOwnerSqlAsync(
                $"GRANT SELECT, INSERT, UPDATE ON environment.remote_mcp_connections TO \"{runtimeRole}\"");
            await ExecuteOwnerSqlAsync(
                $"GRANT SELECT, INSERT ON environment.remote_mcp_connection_configurations TO \"{runtimeRole}\"");
            await ExecuteOwnerSqlAsync(
                $"GRANT SELECT, INSERT ON environment.remote_mcp_catalog_snapshots TO \"{runtimeRole}\"");
            await ExecuteOwnerSqlAsync(
                $"GRANT SELECT, INSERT ON environment.remote_mcp_connection_idempotency TO \"{runtimeRole}\"");
            await ExecuteOwnerSqlAsync(
                $"GRANT SELECT, INSERT, UPDATE ON environment.p1_creation_links TO \"{runtimeRole}\"");
            await ExecuteOwnerSqlAsync(
                $"GRANT SELECT, INSERT ON environment.platform_policy_apply_reservations TO \"{runtimeRole}\"");

            var runtimeConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                Username = runtimeRole,
                Password = runtimePassword,
                Pooling = false,
                SslMode = SslMode.Disable,
            }.ConnectionString;
            runtimeDataSource = NpgsqlDataSource.Create(runtimeConnectionString);
            using var factory = new WebApplicationFactory<EnvironmentProgram>().WithWebHostBuilder(builder =>
            {
                UseEnvironmentSourceSettings(builder, contentRoot, CreateEnvironmentSourceConfiguration(
                    projectsAddress,
                    certificates,
                    runtimeConnectionString));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<NpgsqlDataSource>();
                    services.AddSingleton(runtimeDataSource);
                });
            });
            using var hostClient = factory.CreateClient();
            var services = factory.Services;
            var sourceClient = services.GetRequiredService<Env.PlatformPolicySourceHttpClient>();
            var enabledHostedServices = services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
                .Select(service => service.GetType())
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(0, Volatile.Read(ref sourceRequests));
            Assert.NotNull(services.GetService<Env.PlatformPolicySourceHttpClient>());
            Assert.NotNull(services.GetService<Env.PlatformPolicySourceClientTlsIdentity>());
            Assert.Null(services.GetService<IPlatformEgressPolicySource>());
            Assert.Null(services.GetService<IPlatformEgressProvider>());
            Assert.Null(services.GetService<Env.PlatformEgressReconciler>());
            Assert.Null(services.GetService<Env.CiliumPlatformEgressProvider>());
            Assert.DoesNotContain(enabledHostedServices,
                type => type.FullName?.Contains("PlatformEgress", StringComparison.Ordinal) == true);

            var disabledRuntimeDataSource = NpgsqlDataSource.Create(runtimeConnectionString);
            using (var disabledFactory = new WebApplicationFactory<EnvironmentProgram>().WithWebHostBuilder(builder =>
            {
                UseEnvironmentSourceSettings(builder, contentRoot, CreateEnvironmentSourceConfiguration(
                    projectsAddress,
                    certificates,
                    runtimeConnectionString,
                    sourceEnabled: false));
                builder.ConfigureTestServices(testServices =>
                {
                    testServices.RemoveAll<NpgsqlDataSource>();
                    testServices.AddSingleton(disabledRuntimeDataSource);
                });
            }))
            using (disabledFactory.CreateClient())
            {
                var disabledServices = disabledFactory.Services;
                var disabledHostedServices = disabledServices
                    .GetServices<Microsoft.Extensions.Hosting.IHostedService>()
                    .Select(service => service.GetType())
                    .OrderBy(type => type.FullName, StringComparer.Ordinal)
                    .ToArray();

                Assert.Null(disabledServices.GetService<Env.PlatformPolicySourceHttpClient>());
                Assert.Null(disabledServices.GetService<Env.PlatformPolicySourceClientTlsIdentity>());
                Assert.Null(disabledServices.GetService<IPlatformEgressPolicySource>());
                Assert.Null(disabledServices.GetService<IPlatformEgressProvider>());
                Assert.Null(disabledServices.GetService<Env.PlatformEgressReconciler>());
                Assert.Null(disabledServices.GetService<Env.CiliumPlatformEgressProvider>());
                Assert.Equal(enabledHostedServices, disabledHostedServices);
                Assert.Equal(0, Volatile.Read(ref sourceRequests));
            }
            await disabledRuntimeDataSource.DisposeAsync();

            var firstRead = await sourceClient.ReadAsync();
            Assert.True(firstRead.IsSuccess);
            var first = firstRead.Observation!;
            Assert.Equal($"projects-config:platform:default:{firstReceipt.PolicyId}", first.SourceId);
            Assert.Equal(firstReceipt.Revision, first.Revision);
            Assert.Equal(firstReceipt.ContentSha256, first.ContentSha256);
            Assert.Equal("pending", first.AdmissionState);
            Assert.Null(first.AdmissionCheckpoint);
            var firstCandidate = Env.PlatformEgressAcsCompiler.CompileCandidate(
                first.Artifact, first.SourceId, first.SourceRevision);
            Assert.True(firstCandidate.IsSuccess);
            var candidate = firstCandidate.Candidate!;
            Assert.Equal(PlatformEgressMode.PublicHttps, candidate.Mode);
            Assert.Equal(first.ContentSha256, candidate.SourceHash);
            Assert.Equal(Env.PlatformEgressAcsCompiler.CompilerVersion,
                candidate.CompilerVersion);
            Assert.Contains(candidate.ApprovedDestinations,
                destination => destination.Host == "approved.example.com");
            Assert.Contains(candidate.SystemDestinations,
                destination => destination.Host == "api.github.com");
            var firstAdmission = Env.PlatformEgressAcsCompiler.Compile(
                first.Artifact, first.SourceId, first.SourceRevision,
                first.AdmissionCheckpoint?.ToString()!);
            Assert.False(firstAdmission.IsSuccess);
            Assert.Null(firstAdmission.Snapshot);
            Assert.Equal("source_metadata_invalid", firstAdmission.Failure?.Code);
            Assert.Equal(1, Volatile.Read(ref sourceRequests));

            var replacementReceipt = await SeedPlatformPolicySourceArtifactAsync(
                "environment-client-replacement",
                FiniteNetworkArtifact());
            Assert.True(replacementReceipt.Revision > firstReceipt.Revision);
            var replacementRead = await sourceClient.ReadAsync();
            Assert.True(replacementRead.IsSuccess);
            var replacement = replacementRead.Observation!;
            Assert.Equal(replacementReceipt.Revision, replacement.Revision);
            Assert.NotEqual(first.SourceRevision, replacement.SourceRevision);
            var replacementCandidate = Env.PlatformEgressAcsCompiler.CompileCandidate(
                replacement.Artifact, replacement.SourceId, replacement.SourceRevision);
            Assert.True(replacementCandidate.IsSuccess);
            Assert.DoesNotContain(replacementCandidate.Candidate!.ApprovedDestinations,
                destination => destination.Host == "approved.example.com");
            var replacementAdmission = Env.PlatformEgressAcsCompiler.Compile(
                replacement.Artifact, replacement.SourceId, replacement.SourceRevision,
                replacement.AdmissionCheckpoint?.ToString()!);
            Assert.False(replacementAdmission.IsSuccess);
            Assert.Null(replacementAdmission.Snapshot);
            Assert.Equal(2, Volatile.Read(ref sourceRequests));

            var unsupportedReceipt = await SeedPlatformPolicySourceArtifactAsync(
                "environment-client-unsupported",
                UnsupportedFiniteNetworkArtifact());
            var unsupportedRead = await sourceClient.ReadAsync();
            Assert.True(unsupportedRead.IsSuccess);
            Assert.Equal(unsupportedReceipt.Revision, unsupportedRead.Observation!.Revision);
            var unsupportedCandidate = Env.PlatformEgressAcsCompiler.CompileCandidate(
                unsupportedRead.Observation.Artifact,
                unsupportedRead.Observation.SourceId,
                unsupportedRead.Observation.SourceRevision);
            Assert.False(unsupportedCandidate.IsSuccess);
            Assert.Null(unsupportedCandidate.Candidate);
            var unsupportedAdmission = Env.PlatformEgressAcsCompiler.Compile(
                unsupportedRead.Observation.Artifact,
                unsupportedRead.Observation.SourceId,
                unsupportedRead.Observation.SourceRevision,
                unsupportedRead.Observation.AdmissionCheckpoint?.ToString()!);
            Assert.False(unsupportedAdmission.IsSuccess);
            Assert.Null(unsupportedAdmission.Snapshot);
            Assert.Equal(3, Volatile.Read(ref sourceRequests));
            Assert.Null(services.GetService<IPlatformEgressPolicySource>());
            Assert.Null(services.GetService<IPlatformEgressProvider>());
            Assert.Null(services.GetService<Env.PlatformEgressReconciler>());
            Assert.Null(services.GetService<Env.CiliumPlatformEgressProvider>());

            await projectsHost.StopAsync();
            var unavailable = await sourceClient.ReadAsync();
            Assert.False(unavailable.IsSuccess);
            Assert.Null(unavailable.Observation);
            Assert.Equal("policy_source_unavailable", unavailable.FailureCode);
            Assert.Equal(3, Volatile.Read(ref sourceRequests));
        }
        finally
        {
            try
            {
                if (runtimeDataSource is not null)
                    await runtimeDataSource.DisposeAsync();
            }
            finally
            {
                try
                {
                    if (roleCreated)
                    {
                        await ExecuteOwnerSqlAsync($"DROP OWNED BY \"{runtimeRole}\"");
                        await ExecuteOwnerSqlAsync($"DROP ROLE \"{runtimeRole}\"");
                    }
                }
                finally
                {
                    var productionSettingsPath = Path.Combine(
                        contentRoot, "appsettings.Production.json");
                    try
                    {
                        if (File.Exists(productionSettingsPath))
                            File.Delete(productionSettingsPath);
                    }
                    finally
                    {
                        Directory.Delete(contentRoot);
                    }
                }
            }
        }
    }

    private static void UseEnvironmentSourceSettings(
        IWebHostBuilder builder,
        string contentRoot,
        IReadOnlyDictionary<string, string?> values)
    {
        // Program validates these settings before WebApplicationFactory applies ConfigureAppConfiguration.
        builder.UseContentRoot(contentRoot);
        builder.UseEnvironment("Production");
        foreach (var (key, value) in values)
            if (value is not null)
                builder.UseSetting(key, value);
    }

    private static void WriteEnvironmentProductionSettings(string contentRoot)
    {
        var ciliumConfigPath = Path.Combine(
            AppContext.BaseDirectory, "runtime-configs", "environment-cilium-config.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(ciliumConfigPath));
        var settings = document.RootElement.GetProperty("data")
            .GetProperty("appsettings.Production.json").GetString()!;
        File.WriteAllText(Path.Combine(contentRoot, "appsettings.Production.json"), settings);
    }

    private static Dictionary<string, string?> CreateEnvironmentSourceConfiguration(
        Uri projectsAddress,
        PlatformPolicySourceTlsTests.PolicySourceCertificates certificates,
        string runtimeConnectionString,
        bool sourceEnabled = true)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var runtimeConfigPath = Path.Combine(
            AppContext.BaseDirectory, "runtime-configs", "environment-runtime-config.json");
        using (var document = JsonDocument.Parse(File.ReadAllBytes(runtimeConfigPath)))
        {
            foreach (var item in document.RootElement.GetProperty("data").EnumerateObject())
                values[item.Name.Replace("__", ":", StringComparison.Ordinal)] = item.Value.GetString();
        }

        values.Remove("Environment:RuntimeBootstrap:OrchestratorOwnerAddress");
        values.Remove("Environment:RuntimeBootstrap:BrokerOwnerAddress");
        values.Remove("Environment:RuntimeBootstrap:EventsOwnerAddress");
        values.Remove("Environment:RuntimeBootstrap:DefaultSourceProfileId");

        values["ConnectionStrings:Environment"] = runtimeConnectionString;
        values["Environment:Database:WorkloadIdentity:TenantId"] = "00000000-0000-0000-0000-000000000001";
        values["Environment:Database:WorkloadIdentity:ClientId"] = "00000000-0000-0000-0000-000000000002";
        values["Environment:Database:WorkloadIdentity:TokenFilePath"] = Path.Combine(
            Directory.GetCurrentDirectory(), "unused-test-token");
        values["Environment:Authentication:Authority"] = "https://identity.test/";
        values["Environment:Authentication:Audience"] = "https://identity.test/";
        values["ProjectsConfig:BaseAddress"] = projectsAddress.AbsoluteUri;
        values["Environment:NetworkPolicy:Cilium:AdapterVersion"] = "1.0.0";
        values["Environment:NetworkPolicy:Cilium:OptionsRevision"] = "source-client-test";
        values["Environment:Sandbox:AgentSandbox:OptionsRevision"] = "source-client-test";
        values["Environment:Sandbox:AgentSandbox:ContainerImage"] =
            "ghcr.io/agentweaver/agenthost@sha256:" + new string('a', 64);
        values["Environment:Sandbox:AgentSandbox:ContainerImagePlatform"] = "linux/amd64";
        values["Environment:Sandbox:AgentSandbox:ContainerImageCompressedPullBytes"] = "1";
        values["Environment:Storage:AzureFiles:OptionsRevision"] = "source-client-test";
        values["Environment:PlatformEgress:Source:Enabled"] = sourceEnabled.ToString();
        values["Environment:PlatformEgress:Source:Tls:ClientCertificatePemPath"] =
            certificates.EnvironmentClientCertificatePath;
        values["Environment:PlatformEgress:Source:Tls:ClientPrivateKeyPemPath"] =
            certificates.EnvironmentClientPrivateKeyPath;
        values["Environment:PlatformEgress:Source:Tls:ClientCertificateSha256"] =
            certificates.EnvironmentPin;
        values["Environment:PlatformEgress:Source:Tls:BackendCaPemPath"] = certificates.CaPath;
        values["Environment:PlatformEgress:Source:Tls:BackendCaSha256"] =
            Convert.ToHexString(SHA256.HashData(certificates.BackendCa.RawData));
        values["Environment:PlatformEgress:Source:Tls:ProjectsPins:0:Sha256"] = certificates.ServerPin;
        values["Environment:PlatformEgress:Source:Tls:ProjectsPins:0:RetireAtUtc"] =
            DateTimeOffset.UtcNow.AddDays(2).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        return values;
    }

    private static NativeAcsArtifact FiniteNetworkArtifact(params string[] approvedHosts)
    {
        var data = JsonSerializer.Serialize(new
        {
            agentweaver = new
            {
                platform_egress = new
                {
                    profile = NativeAcsFiniteNetworkProfile.ProfileId,
                    mode = "PublicHttps",
                    approved = approvedHosts.Select(host => new { host, port = 443 }).ToArray(),
                    system = new
                    {
                        catalog = "hosted-copilot-v1",
                        destinations = new[]
                        {
                            new { host = "api.business.githubcopilot.com", port = 443 },
                            new { host = "api.enterprise.githubcopilot.com", port = 443 },
                            new { host = "api.github.com", port = 443 },
                            new { host = "api.githubcopilot.com", port = 443 },
                            new { host = "api.individual.githubcopilot.com", port = 443 },
                        },
                    },
                },
            },
        });
        return NativeAcsFiniteNetworkProfile.CreateArtifact(data);
    }

    private static NativeAcsArtifact UnsupportedFiniteNetworkArtifact()
    {
        var artifact = FiniteNetworkArtifact();
        var dataIndex = Array.FindIndex(
            artifact.Files.ToArray(), file => file.RelativePath == "policy/data.json");
        var data = Encoding.UTF8.GetString(artifact.Files[dataIndex].Bytes.AsSpan())
            .Replace(NativeAcsFiniteNetworkProfile.ProfileId, "finite-network-v2", StringComparison.Ordinal);
        var changed = artifact with
        {
            Files = artifact.Files.SetItem(dataIndex, artifact.Files[dataIndex] with
            {
                Bytes = Encoding.UTF8.GetBytes(data).ToImmutableArray(),
            }),
        };
        return changed with { ContentSha256 = NativeAcsArtifactHash.Compute(changed) };
    }
}
