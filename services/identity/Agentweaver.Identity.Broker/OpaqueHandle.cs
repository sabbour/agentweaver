using System.Security.Cryptography;
using System.Text;

namespace Agentweaver.Identity.Broker;

/// <summary>
/// Generates unguessable opaque handles and hashes them for storage. The raw handle is
/// returned to the caller exactly once and never persisted; only its hash is stored, so a
/// compromised database row cannot be replayed as a live handle.
/// </summary>
public static class OpaqueHandle
{
    public static string NewHandle() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string handle) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(handle)));
}
