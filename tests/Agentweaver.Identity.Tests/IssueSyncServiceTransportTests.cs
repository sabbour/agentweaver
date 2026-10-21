using Agentweaver.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Agentweaver.Identity.Tests;

public sealed class IssueSyncServiceTransportTests
{
    [Fact]
    public void MissingProfileDoesNotEnableBackgroundAuthority()
    {
        var builder = WebApplication.CreateBuilder();
        Assert.False(IssueSyncServiceTransport.AddChannel(
            builder, "Sync", "Test:IssueSync", "Orchestrator", "IdentityBroker", false));
        Assert.DoesNotContain(builder.Services, service => service.ServiceType == typeof(RuntimeServiceTlsIdentity));
    }

    [Theory]
    [InlineData("missing-listener")]
    [InlineData("http-listener")]
    [InlineData("wrong-port")]
    [InlineData("shared-port")]
    [InlineData("same-certificate")]
    public void ListenerRequiresExplicitDistinctPinnedServiceProfile(string fault)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Test:IssueSync:LocalServiceName"] = "IdentityBroker",
            ["Test:IssueSync:PeerServiceName"] = "Orchestrator",
            ["Test:IssueSync:LocalServicePrincipalId"] = Guid.NewGuid().ToString("D"),
            ["Test:IssueSync:PeerServicePrincipalId"] = Guid.NewGuid().ToString("D"),
            ["Test:IssueSync:LocalCertificatePemPath"] = Path.GetFullPath("sync-test-certificate.pem"),
            ["Test:IssueSync:LocalPrivateKeyPemPath"] = Path.GetFullPath("sync-test-key.pem"),
            ["Test:IssueSync:LocalCertificateSha256"] = new('a', 64),
            ["Test:IssueSync:PeerCertificateSha256Pins:0"] = new('b', 64),
            ["Test:IssueSync:PeerOwnerAddress"] = "https://orchestrator.internal/",
            ["Test:IssueSync:ListenerPort"] = "9443",
            ["Kestrel:Endpoints:Sync:Url"] = "https://localhost:9443"
        };
        switch (fault)
        {
            case "missing-listener": settings.Remove("Kestrel:Endpoints:Sync:Url"); break;
            case "http-listener": settings["Kestrel:Endpoints:Sync:Url"] = "http://localhost:9443"; break;
            case "wrong-port": settings["Kestrel:Endpoints:Sync:Url"] = "https://localhost:9444"; break;
            case "shared-port": settings["Kestrel:Endpoints:Public:Url"] = "https://localhost:9443"; break;
            case "same-certificate": settings["Test:IssueSync:PeerCertificateSha256Pins:0"] = new('a', 64); break;
        }
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        Assert.Throws<InvalidOperationException>(() => IssueSyncServiceTransport.AddChannel(
            builder, "Sync", "Test:IssueSync", "IdentityBroker", "Orchestrator", true));
    }
}
