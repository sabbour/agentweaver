using System.Net;
using System.Net.Sockets;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

public static class RuntimeSandboxHttpTransport
{
    internal static readonly HttpRequestOptionsKey<SandboxRuntimeHttpsEndpoint> EndpointOption =
        new("Agentweaver.SandboxRuntimeEndpoint");

    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
            EndPoint destination = context.DnsEndPoint;
            if (context.InitialRequestMessage.Options.TryGetValue(EndpointOption, out var endpoint))
            {
                endpoint.Validate();
                if (context.DnsEndPoint.Host != endpoint.Address.Host ||
                    context.DnsEndPoint.Port != endpoint.Address.Port)
                    throw new RuntimeAuthorizationException("runtime_sandbox_endpoint_invalid");
                destination = new IPEndPoint(IPAddress.Parse(endpoint.PodAddress), endpoint.Address.Port);
            }
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(destination, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };
}
