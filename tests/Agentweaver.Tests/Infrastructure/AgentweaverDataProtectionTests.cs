using Agentweaver.AspNetCore.DataProtection;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agentweaver.Tests.Infrastructure;

public sealed class AgentweaverDataProtectionTests
{
    [Fact]
    public void DefaultConfiguration_UsesStableApplicationName()
    {
        using var provider = BuildProvider();

        provider.GetRequiredService<IOptions<DataProtectionOptions>>()
            .Value.ApplicationDiscriminator.Should().Be(
                AgentweaverDataProtection.StableApplicationName);
    }

    [Fact]
    public void PersistedKeyRing_CanUnprotectAcrossApplicationInstances()
    {
        var keysDirectory = CreateTempDirectory();
        try
        {
            using var first = BuildProvider(keysDirectory);
            var protector = first.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("agentweaver-tests");
            var protectedValue = protector.Protect("session-value");

            using var second = BuildProvider(keysDirectory);
            var unprotected = second.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("agentweaver-tests")
                .Unprotect(protectedValue);

            unprotected.Should().Be("session-value");
        }
        finally
        {
            try { Directory.Delete(keysDirectory, recursive: true); }
            catch { }
        }
    }

    private static ServiceProvider BuildProvider(string? keysDirectory = null)
    {
        var values = new Dictionary<string, string?>();
        if (keysDirectory is not null)
            values["DataProtection:KeysDirectory"] = keysDirectory;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentweaverDataProtection(configuration, new TestEnvironment());
        return services.BuildServiceProvider();
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentweaver-dp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
