using System.Net;
using System.Net.Sockets;

const int postgresPort = 5432;
const string vaultHost = "aw-v1-p0-kv.vault.azure.net";
var keyVault = args.Length == 2 && args[0] == "--key-vault" && args[1] == vaultHost;
if (!keyVault && (args.Length != 1 ||
    Uri.CheckHostName(args[0]) != UriHostNameType.Dns ||
    !args[0].EndsWith(".postgres.database.azure.com", StringComparison.OrdinalIgnoreCase)))
{
    Console.Error.WriteLine("A PostgreSQL Flexible Server FQDN or the exact approved --key-vault target is required.");
    return 2;
}

var targetHost = keyVault ? vaultHost : args[0];
var targetPort = keyVault ? 443 : postgresPort;
var listenPort = keyVault ? 8443 : postgresPort;
using var listener = new TcpListener(IPAddress.Any, listenPort);
listener.Start();
Console.WriteLine(keyVault ? "IDENTITY_KV_PROXY_READY" : "IDENTITY_PG_PROXY_READY");

while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    _ = RelayAsync(client, targetHost, targetPort);
}

static async Task RelayAsync(TcpClient client, string targetHost, int targetPort)
{
    using (client)
    using (var server = new TcpClient())
    {
        try
        {
            await server.ConnectAsync(targetHost, targetPort);
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
