using System.Net;
using System.Net.Sockets;

// Opens a narrow TCP relay for the approved PostgreSQL host or an exact approved Key Vault endpoint.
const int postgresPort = 5432;
const string vaultUriConfigurationKey = "IdentityBroker__SecretRedemption__VaultUri";
var configuredVaultHost = GetConfiguredVaultHost(Environment.GetEnvironmentVariable(vaultUriConfigurationKey));
var keyVault = args.Length == 2 && args[0] == "--key-vault" &&
    configuredVaultHost is not null &&
    string.Equals(args[1], configuredVaultHost, StringComparison.OrdinalIgnoreCase);
if (!keyVault && (args.Length != 1 ||
    Uri.CheckHostName(args[0]) != UriHostNameType.Dns ||
    !args[0].EndsWith(".postgres.database.azure.com", StringComparison.OrdinalIgnoreCase)))
{
    Console.Error.WriteLine("A PostgreSQL Flexible Server FQDN or the exact deployment-configured --key-vault target is required.");
    return 2;
}

var targetHost = args.Length == 2 ? args[1] : args[0];
var targetPort = keyVault ? 443 : postgresPort;
var listenPort = keyVault ? 8443 : postgresPort;
using var listener = new TcpListener(IPAddress.Any, listenPort);
listener.Start();
Console.WriteLine(keyVault ? "IDENTITY_KV_PROXY_READY" : "IDENTITY_PG_PROXY_READY");

// Matches the exact public Key Vault host in the Identity Broker runtime URI.
static string? GetConfiguredVaultHost(string? configuredUri)
{
    if (!Uri.TryCreate(configuredUri, UriKind.Absolute, out var uri) ||
        uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
        uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
        uri.HostNameType != UriHostNameType.Dns ||
        !uri.Host.EndsWith(".vault.azure.net", StringComparison.OrdinalIgnoreCase))
        return null;

    var name = uri.Host[..^".vault.azure.net".Length];
    return System.Text.RegularExpressions.Regex.IsMatch(name,
        "^[a-z](?:[a-z0-9]|-(?=[a-z0-9])){1,22}[a-z0-9]$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)
        ? uri.Host
        : null;
}

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
