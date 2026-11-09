using Agentweaver.Environment;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Environment.Tests;

public sealed class KubernetesApiServerAddressTests
{
    [Theory]
    [InlineData(null, "10.0.0.1", "443", "https://10.0.0.1/")]
    [InlineData(null, "172.20.0.1", "6443", "https://172.20.0.1:6443/")]
    [InlineData(null, "fd00::1", "443", "https://[fd00::1]/")]
    [InlineData(null, null, null, "https://kubernetes.default.svc/")]
    [InlineData("https://api.example/", "10.0.0.1", "443", "https://api.example/")]
    public void UsesExplicitSettingThenInjectedServiceAddressThenServiceName(
        string? configured, string? host, string? port, string expected)
    {
        var address = EnvironmentHttpTransport.ResolveKubernetesApiServer(Configuration(configured, host, port));

        Assert.Equal(new Uri(expected), address);
    }

    [Theory]
    [InlineData("10.0.0.1", "https")]
    [InlineData("10.0.0.1", "0")]
    [InlineData("10.0.0.1", "65536")]
    [InlineData("user@10.0.0.1", "443")]
    public void RejectsAnInvalidInjectedServiceAddress(string host, string port)
    {
        Assert.Throws<InvalidOperationException>(() =>
            EnvironmentHttpTransport.ResolveKubernetesApiServer(Configuration(null, host, port)));
    }

    private static IConfiguration Configuration(string? configured, string? host, string? port) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kubernetes:ApiServer"] = configured,
            ["KUBERNETES_SERVICE_HOST"] = host,
            ["KUBERNETES_SERVICE_PORT"] = port,
        }).Build();
}
