using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Agentweaver.Environment.Tests;

public sealed class PlatformPolicySourceClientCompositionTests
{
    [Theory]
    [InlineData(RemoteMcpTrustedServiceTlsComposition.IdentitySectionName, "IdentityBroker")]
    [InlineData(RemoteMcpTrustedServiceTlsComposition.OrchestratorSectionName, "Orchestrator")]
    public void RejectsSourceCertificateReuseByEveryRemoteMcpLocalProfile(
        string sectionName,
        string peerName)
    {
        using var certificates = new PlatformPolicySourceClientCertificateFixture();
        var values = certificates.Configuration();
        certificates.AddRemoteMcpProfile(
            values,
            sectionName,
            peerName,
            certificates.ClientFiles,
            PlatformPolicySourceClientCertificateFixture.Fingerprint(certificates.Client).ToUpperInvariant());
        AddListenerWhenRequired(values, sectionName);
        var builder = CreateBuilder(values);

        builder.AddRemoteMcpTrustedServiceTls();

        Assert.Throws<InvalidOperationException>(() =>
            builder.AddPlatformPolicySourceClient(new Uri("https://localhost/")));
    }

    [Fact]
    public void RejectsReusedCertificateLoadedFromDifferentPemPaths()
    {
        using var certificates = new PlatformPolicySourceClientCertificateFixture();
        var values = certificates.Configuration();
        var copyDirectory = Path.GetDirectoryName(certificates.ClientFiles.CertificatePath)!;
        var copyCertificatePath = Path.Combine(copyDirectory, "source-copy.pem");
        var copyKeyPath = Path.Combine(copyDirectory, "source-copy-key.pem");
        File.Copy(certificates.ClientFiles.CertificatePath, copyCertificatePath);
        File.Copy(certificates.ClientFiles.PrivateKeyPath, copyKeyPath);
        certificates.AddRemoteMcpProfile(
            values,
            RemoteMcpTrustedServiceTlsComposition.OrchestratorSectionName,
            "Orchestrator",
            (copyCertificatePath, copyKeyPath));
        var builder = CreateBuilder(values);
        builder.AddRemoteMcpTrustedServiceTls();

        Assert.Throws<InvalidOperationException>(() =>
            builder.AddPlatformPolicySourceClient(new Uri("https://localhost/")));
    }

    [Fact]
    public void RejectsOverlapWithAnyProjectsPeerPin()
    {
        using var certificates = new PlatformPolicySourceClientCertificateFixture();
        var values = certificates.Configuration(
            PlatformPolicySourceClientCertificateFixture.Fingerprint(certificates.Client));
        var builder = CreateBuilder(values);

        Assert.Throws<InvalidOperationException>(() =>
            builder.AddPlatformPolicySourceClient(new Uri("https://localhost/")));
    }

    [Fact]
    public void RejectsRemoteProfileWhoseDeclaredFingerprintDoesNotMatchItsCertificate()
    {
        using var certificates = new PlatformPolicySourceClientCertificateFixture();
        var values = certificates.Configuration();
        certificates.AddRemoteMcpProfile(
            values,
            RemoteMcpTrustedServiceTlsComposition.OrchestratorSectionName,
            "Orchestrator",
            localPin: new string('A', 64));
        var builder = CreateBuilder(values);
        builder.AddRemoteMcpTrustedServiceTls();

        Assert.Throws<InvalidOperationException>(() =>
            builder.AddPlatformPolicySourceClient(new Uri("https://localhost/")));
    }

    [Fact]
    public void RegistersOnlyClientTlsWhenEnabledAndNothingWhenDisabled()
    {
        using var certificates = new PlatformPolicySourceClientCertificateFixture();
        var enabled = CreateBuilder(certificates.Configuration());

        Assert.True(enabled.AddPlatformPolicySourceClient(new Uri("https://localhost/")));
        Assert.Contains(enabled.Services, descriptor =>
            descriptor.ServiceType == typeof(PlatformPolicySourceHttpClient));
        Assert.Contains(enabled.Services, descriptor =>
            descriptor.ServiceType == typeof(PlatformPolicySourceClientTlsIdentity));
        Assert.DoesNotContain(enabled.Services, descriptor =>
            descriptor.ServiceType == typeof(IPlatformEgressPolicySource) ||
            descriptor.ServiceType == typeof(IPlatformEgressProvider) ||
            descriptor.ServiceType == typeof(CiliumPlatformEgressProvider) ||
            descriptor.ServiceType == typeof(PlatformEgressReconciler) ||
            descriptor.ServiceType == typeof(IHostedService));
        Assert.Null(enabled.Configuration["Kestrel:Endpoints:PlatformPolicySource:Url"]);
        Assert.DoesNotContain(
            enabled.Configuration.AsEnumerable(),
            pair => pair.Key.StartsWith(
                PlatformPolicySourceClientTls.SourceSectionName + ":",
                StringComparison.OrdinalIgnoreCase) &&
                pair.Key.EndsWith("PrincipalId", StringComparison.OrdinalIgnoreCase));

        var disabledValues = certificates.Configuration();
        disabledValues["Environment:PlatformEgress:Source:Enabled"] = "false";
        var disabled = CreateBuilder(disabledValues);
        Assert.False(disabled.AddPlatformPolicySourceClient(new Uri("https://localhost/")));
        Assert.DoesNotContain(disabled.Services, descriptor =>
            descriptor.ServiceType == typeof(PlatformPolicySourceHttpClient) ||
            descriptor.ServiceType == typeof(PlatformPolicySourceClientTlsIdentity));
    }

    [Theory]
    [InlineData("enabled")]
    [InlineData("")]
    public void RejectsMalformedEnabledSetting(string value)
    {
        using var certificates = new PlatformPolicySourceClientCertificateFixture();
        var values = certificates.Configuration();
        values["Environment:PlatformEgress:Source:Enabled"] = value;
        var builder = CreateBuilder(values);

        Assert.Throws<InvalidOperationException>(() =>
            builder.AddPlatformPolicySourceClient(new Uri("https://localhost/")));
    }

    private static WebApplicationBuilder CreateBuilder(IDictionary<string, string?> values)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(values);
        return builder;
    }

    private static void AddListenerWhenRequired(
        IDictionary<string, string?> values,
        string sectionName)
    {
        if (sectionName != RemoteMcpTrustedServiceTlsComposition.IdentitySectionName)
            return;
        values["Kestrel:Endpoints:RemoteMcpCredentialUse:Url"] = "https://localhost:8444";
    }
}
