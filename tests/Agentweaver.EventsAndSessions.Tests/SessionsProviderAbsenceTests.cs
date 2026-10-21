using System.Collections.Immutable;
using System.Reflection;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

/// <summary>
/// Proves that the Events &amp; Sessions host builds, resolves, and runs its Sessions journal with the
/// optional AgentSessions provider absent. The native PostgreSQL provider is the only default.
/// </summary>
public sealed class SessionsProviderAbsenceTests
{
    private const string OptionalProviderAssembly = "Agentweaver.Providers.Sessions.AgentSessions";

    public static TheoryData<string> PlatformAssemblies => new()
    {
        typeof(NativePostgresSessionsProvider).Assembly.GetName().Name!,
        typeof(ISessionsJournal).Assembly.GetName().Name!,
        typeof(ProviderCatalog).Assembly.GetName().Name!,
    };

    [Theory]
    [MemberData(nameof(PlatformAssemblies))]
    public void PlatformAssembliesDoNotReferenceTheAgentSessionsProvider(string assemblyName)
    {
        var assembly = Assembly.Load(assemblyName);

        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            reference.Name is OptionalProviderAssembly or "Google.Protobuf");
        Assert.DoesNotContain(assembly.GetTypes(), type =>
            type.Namespace is { } name &&
            (name.StartsWith("Agentsessions", StringComparison.Ordinal) ||
             name.StartsWith(OptionalProviderAssembly, StringComparison.Ordinal)));
    }

    [Fact]
    public void TestHostDoesNotDeployTheAgentSessionsProvider()
    {
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, OptionalProviderAssembly + ".dll")));
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly =>
            assembly.GetName().Name == OptionalProviderAssembly);
    }

    [Fact]
    public void NativeProviderIsTheDefaultWhenOnlyTheNativeProviderIsRegistered()
    {
        var provider = new NativePostgresSessionsProvider();
        var options = new PostgresSessionsProviderOptions(
            "sessions-primary", "agentweaver", 1, "sessions", "rev-1");
        var catalog = ProviderCatalog.Create(
            [provider.CreateRegistration(options)],
            [new ProviderSelection(ProviderSeam.Sessions, NativePostgresSessionsProvider.ProviderId)],
            []);
        Assert.True(catalog.IsSuccess);

        var resolution = new ProviderResolver(catalog.Value!).Resolve(new ProviderResolutionRequest(
            ProviderSeam.Sessions,
            ProjectOverrideId: null,
            NativePostgresSessionsProvider.AdapterVersion,
            NativePostgresSessionsProvider.OptionsSchemaVersion,
            SessionsCapabilities.All));

        Assert.True(resolution.IsSuccess);
        Assert.Equal(NativePostgresSessionsProvider.ProviderId, resolution.Value!.Candidate!.ProviderId);
        Assert.Equal(SessionsCapabilities.All, provider.Descriptor.AdvertisedCapabilities);
    }
}
