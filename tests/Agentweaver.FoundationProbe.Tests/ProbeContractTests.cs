using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.FoundationProbe;
using Xunit;

namespace Agentweaver.FoundationProbe.Tests;

public sealed class ProbeContractTests
{
    [Fact]
    public void AcceptsExactDedicatedDeploymentOutputsAndSourceBinding()
    {
        var target = ProbeFixtures.Target();
        ProbeTargetValidator.Validate(target, ProbeFixtures.Source);

        var pins = ProbeProviderBindings.ResolveAndPin(target, new string('e', 32));
        Assert.Equal(new[] { "Secrets", "ObjectStore", "Telemetry" }, pins.Select(pin => pin.Seam));
        Assert.Equal(target.FoundationResources.KeyVaultId, pins[0].ResourceId);
        Assert.Equal(target.FoundationResources.BlobContainerId, pins[1].ResourceId);
        Assert.Equal(target.FoundationResources.AppInsightsResourceId, pins[2].ResourceId);
    }

    [Fact]
    public void ProbeBlobReceiptUsesTheNativeCamelCaseEtagField()
    {
        var evidence = new BlobEvidence(
            "container", "https://example.invalid/container", "foundation-probe/nonce/roundtrip.json",
            "nonce", "\"owned-etag\"", new string('a', 64), CleanupConfirmed: true);
        var json = JsonSerializer.Serialize(evidence, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        Assert.Contains("\"eTag\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsMissingTargetPropertiesUnknownFieldsAndMalformedSource()
    {
        var valid = JsonSerializer.Serialize(ProbeFixtures.Target(), new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        var missing = JsonNode.Parse(valid)!.AsObject();
        missing.Remove("aksOidcIssuerUrl");
        Assert.Equal("target_invalid", Assert.Throws<ProbeException>(() => { _ = Read(missing.ToJsonString()); }).Code);

        var unknown = JsonNode.Parse(valid)!.AsObject();
        unknown["callerDigest"] = "sha256:caller-claim";
        Assert.Equal("target_invalid", Assert.Throws<ProbeException>(() => { _ = Read(unknown.ToJsonString()); }).Code);

        var malformed = ProbeFixtures.Target() with { SourceHash = new string('C', 64) };
        Assert.Equal("target_missing", Assert.Throws<ProbeException>(
            () => ProbeTargetValidator.Validate(malformed, ProbeFixtures.Source)).Code);
    }

    [Fact]
    public void RejectsSourceTreeResourceIssuerAndRuntimeTargetMismatches()
    {
        var target = ProbeFixtures.Target();
        Assert.Equal("source_mismatch", Assert.Throws<ProbeException>(
            () => ProbeTargetValidator.Validate(target with { SourceTree = new string('f', 40) }, ProbeFixtures.Source)).Code);
        Assert.Equal("target_mismatch", Assert.Throws<ProbeException>(
            () => ProbeTargetValidator.Validate(target with { ResourceGroupId = "/subscriptions/other/resourceGroups/aw-v1-p0" },
                ProbeFixtures.Source)).Code);
        Assert.Equal("resource_mismatch", Assert.Throws<ProbeException>(
            () => ProbeTargetValidator.Validate(target with
            {
                FoundationResources = target.FoundationResources with { KeyVaultId = "/subscriptions/other/vault" },
            }, ProbeFixtures.Source)).Code);
        Assert.Equal("issuer_mismatch", Assert.Throws<ProbeException>(
            () => ProbeTargetValidator.Validate(target with { AksOidcIssuerUrl = "http://issuer.invalid/" },
                ProbeFixtures.Source)).Code);
        Assert.Equal("runtime_target_mismatch", Assert.Throws<ProbeException>(
            () => ProbeTargetValidator.Validate(target with
            {
                Runtime = target.Runtime with { KeyVaultSecretVersion = "latest" },
            }, ProbeFixtures.Source)).Code);
    }

    [Fact]
    public void MonitorConfigurationEvidenceIsValidatedAndContainsNoConnectionSecret()
    {
        var target = ProbeFixtures.Target();
        var evidence = ProbeMonitorConfiguration.Validate(
            "InstrumentationKey=66666666-6666-6666-6666-666666666666;" +
            "IngestionEndpoint=https://eastus-1.in.applicationinsights.azure.com/",
            target);

        Assert.Equal(target.FoundationResources.AppInsightsResourceId, evidence.AppInsightsResourceId);
        Assert.Equal("https://eastus-1.in.applicationinsights.azure.com/", evidence.IngestionEndpoint);
        Assert.True(evidence.InstrumentationKeyConfigured);
        Assert.DoesNotContain("66666666", JsonSerializer.Serialize(evidence), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "monitor_configuration_missing")]
    [InlineData("", "monitor_configuration_missing")]
    [InlineData("InstrumentationKey=not-a-guid;IngestionEndpoint=https://eastus-1.in.applicationinsights.azure.com/",
        "monitor_configuration_invalid")]
    [InlineData("InstrumentationKey=66666666-6666-6666-6666-666666666666;IngestionEndpoint=http://eastus-1.in.applicationinsights.azure.com/",
        "monitor_configuration_invalid")]
    [InlineData("InstrumentationKey=66666666-6666-6666-6666-666666666666;IngestionEndpoint=https://collector.invalid/",
        "monitor_configuration_invalid")]
    [InlineData("InstrumentationKey=66666666-6666-6666-6666-666666666666;InstrumentationKey=77777777-7777-7777-7777-777777777777;IngestionEndpoint=https://eastus-1.in.applicationinsights.azure.com/",
        "monitor_configuration_invalid")]
    public void RejectsMissingOrUnsafeMonitorConfiguration(string? connectionString, string expectedCode)
    {
        Assert.Equal(expectedCode, Assert.Throws<ProbeException>(
            () => ProbeMonitorConfiguration.Validate(connectionString, ProbeFixtures.Target())).Code);
    }

    [Fact]
    public async Task ReadsProjectedTokenClaimsAndRejectsCredentialOrIssuerMismatch()
    {
        var target = ProbeFixtures.Target();
        var path = WriteToken(ProbeFixtures.Token(issuer: target.AksOidcIssuerUrl));
        try
        {
            var evidence = await WorkloadIdentityEvidence.ReadAndValidateAsync(
                target, path, target.FoundationProbeIdentity.ClientId, target.TenantId, CancellationToken.None);
            Assert.Equal(target.AksOidcIssuerUrl, evidence.Issuer);
            Assert.Equal("system:serviceaccount:agentweaver-v1-p0:foundation-probe", evidence.Subject);
            Assert.Equal("api://AzureADTokenExchange", evidence.Audience);

            var mismatch = await Assert.ThrowsAsync<ProbeException>(() =>
                WorkloadIdentityEvidence.ReadAndValidateAsync(target, path, "wrong-client", target.TenantId,
                    CancellationToken.None));
            Assert.Equal("workload_identity_configuration_mismatch", mismatch.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("wrong-issuer", "system:serviceaccount:agentweaver-v1-p0:foundation-probe", "api://AzureADTokenExchange")]
    [InlineData(null, "system:serviceaccount:other:probe", "api://AzureADTokenExchange")]
    [InlineData(null, "system:serviceaccount:agentweaver-v1-p0:foundation-probe", "wrong-audience")]
    public async Task RejectsProjectedTokenClaimMismatch(string? issuer, string subject, string audience)
    {
        var target = ProbeFixtures.Target();
        var token = ProbeFixtures.Token(
            issuer: issuer == "wrong-issuer" ? "https://wrong.example/" : issuer ?? target.AksOidcIssuerUrl,
            subject,
            audience);
        var path = WriteToken(token);
        try
        {
            var exception = await Assert.ThrowsAsync<ProbeException>(() =>
                WorkloadIdentityEvidence.ReadAndValidateAsync(
                    target, path, target.FoundationProbeIdentity.ClientId, target.TenantId, CancellationToken.None));
            Assert.Equal("workload_identity_claim_mismatch", exception.Code);
            Assert.DoesNotContain(token, exception.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MissingOrCancelledProjectedTokenFailsExplicitly()
    {
        var target = ProbeFixtures.Target();
        var missing = await Assert.ThrowsAsync<ProbeException>(() =>
            WorkloadIdentityEvidence.ReadAndValidateAsync(
                target, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                target.FoundationProbeIdentity.ClientId, target.TenantId, CancellationToken.None));
        Assert.Equal("projected_token_unavailable", missing.Code);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var path = WriteToken(ProbeFixtures.Token());
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                WorkloadIdentityEvidence.ReadAndValidateAsync(
                    target, path, target.FoundationProbeIdentity.ClientId, target.TenantId, cancellation.Token));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DefaultCommandIsNonMutatingPlanAndExecutionRequiresTarget()
    {
        Assert.IsType<ProbeCommand.Plan>(ProbeCommand.Parse([]));
        Assert.IsType<ProbeCommand.Plan>(ProbeCommand.Parse(["--plan"]));
        Assert.IsType<ProbeCommand.Execute>(ProbeCommand.Parse(["--execute", "--target", "target.json"]));
        Assert.Equal("usage", Assert.Throws<ProbeException>(
            () => ProbeCommand.Parse(["--execute"])).Code);
        Assert.Equal("usage", Assert.Throws<ProbeException>(
            () => ProbeCommand.Parse(["--target", "target.json"])).Code);
    }

    private static ProbeTarget Read(string json)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            return ProbeTarget.Read(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteToken(string token)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, token);
        return path;
    }
}
