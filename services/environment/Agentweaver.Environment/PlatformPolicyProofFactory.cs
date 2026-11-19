using Npgsql;

namespace Agentweaver.Environment;

public sealed class PlatformPolicyProofFactory : IDisposable
{
    private readonly HttpClient reader;
    private readonly HttpClient writer;
    private readonly KubernetesCiliumPolicyResourceStore resourceStore;

    private PlatformPolicyProofFactory(
        Uri origin,
        byte[] ca,
        string readerTokenFile,
        string writerTokenFile,
        NpgsqlDataSource dataSource,
        CiliumEgressProviderOptions providerOptions,
        CiliumPlatformEgressOptions options,
        TimeProvider timeProvider)
    {
        reader = new HttpClient(KubernetesServiceAccountHandler.Create(readerTokenFile, ca))
        {
            BaseAddress = origin,
        };
        HttpClient? createdWriter = null;
        try
        {
            createdWriter = new HttpClient(KubernetesServiceAccountHandler.Create(writerTokenFile, ca))
            {
                BaseAddress = origin,
            };
            writer = createdWriter;
            resourceStore = new KubernetesCiliumPolicyResourceStore(writer);
            var reservations = new PlatformEgressApplyReservationStore(dataSource, timeProvider);
            Coordinator = new PlatformPolicyProofCoordinator(
                new KubernetesPlatformPolicyInventoryReader(reader),
                new CiliumPlatformEgressProvider(resourceStore, providerOptions, options, timeProvider, reservations),
                new PlatformPolicyProofStore(dataSource, timeProvider),
                timeProvider);
        }
        catch
        {
            createdWriter?.Dispose();
            reader.Dispose();
            throw;
        }
    }

    public PlatformPolicyProofCoordinator Coordinator { get; }

    public static PlatformPolicyProofFactory Create(
        string apiAddress,
        byte[] clusterCaCertificate,
        string readerTokenFile,
        string writerTokenFile,
        NpgsqlDataSource dataSource,
        CiliumEgressProviderOptions providerOptions,
        CiliumPlatformEgressOptions options,
        TimeProvider timeProvider)
    {
        var origin = PlatformPolicyProofCoordinator.RequireProofOrigin(
            Uri.TryCreate(apiAddress, UriKind.Absolute, out var address) ? address : null);
        ArgumentNullException.ThrowIfNull(clusterCaCertificate);
        try
        {
            if (string.Equals(
                Path.GetFullPath(readerTokenFile), Path.GetFullPath(writerTokenFile),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new PlatformPolicyProofException(
                    "policy_fence_unavailable", "The policy reader and writer token files must be separate.");
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new PlatformPolicyProofException(
                "policy_fence_unavailable", "The policy reader and writer token file paths are invalid.");
        }
        return new PlatformPolicyProofFactory(
            origin, clusterCaCertificate.ToArray(), readerTokenFile, writerTokenFile,
            dataSource, providerOptions, options, timeProvider);
    }

    internal Task<CiliumNetworkPolicyDocument?> ReadWriterAsync(
        string @namespace, string name, CancellationToken cancellationToken) =>
        resourceStore.GetAsync(@namespace, name, cancellationToken);

    public void Dispose()
    {
        reader.Dispose();
        writer.Dispose();
    }
}
