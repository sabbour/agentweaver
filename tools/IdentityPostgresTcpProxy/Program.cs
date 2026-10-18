using System.Net;
using System.Net.Sockets;

// Opens a narrow TCP relay for the approved PostgreSQL host or an exact approved Key Vault endpoint.
const int postgresPort = 5432;
const string defaultVaultHost = "agentweaver-kv.vault.azure.net";
const string fallbackVaultHost = "agentweaver-kv-v1.vault.azure.net";
var keyVault = args.Length == 2 && args[0] == "--key-vault" &&
    (args[1] == defaultVaultHost || args[1] == fallbackVaultHost);
if (!keyVault && (args.Length != 1 ||
    Uri.CheckHostName(args[0]) != UriHostNameType.Dns ||
    !args[0].EndsWith(".postgres.database.azure.com", StringComparison.OrdinalIgnoreCase)))
{
    Console.Error.WriteLine("A PostgreSQL Flexible Server FQDN or the exact approved --key-vault target is required.");
    return 2;
}

var targetHost = args.Length == 2 ? args[1] : args[0];
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

// Relays bytes in both directions and stops when either side closes; it does not inspect protocol payloads.
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
