using System.Net;
using System.Net.Sockets;

const int postgresPort = 5432;
if (args.Length != 1 ||
    Uri.CheckHostName(args[0]) != UriHostNameType.Dns ||
    !args[0].EndsWith(".postgres.database.azure.com", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("A PostgreSQL Flexible Server FQDN is required.");
    return 2;
}

var postgresHost = args[0];
using var listener = new TcpListener(IPAddress.Any, postgresPort);
listener.Start();
Console.WriteLine("IDENTITY_PG_PROXY_READY");

while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    _ = RelayAsync(client, postgresHost);
}

static async Task RelayAsync(TcpClient client, string postgresHost)
{
    using (client)
    using (var server = new TcpClient())
    {
        try
        {
            await server.ConnectAsync(postgresHost, 5432);
            var clientToServer = client.GetStream().CopyToAsync(server.GetStream());
            var serverToClient = server.GetStream().CopyToAsync(client.GetStream());
            var completed = await Task.WhenAny(clientToServer, serverToClient);
            await completed;
        }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException)
        {
            Console.Error.WriteLine($"IDENTITY_PG_PROXY_CONNECTION_FAILED type={error.GetType().Name}");
        }
    }
}
