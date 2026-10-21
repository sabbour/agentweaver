using System.Collections.Frozen;
using System.Security.Cryptography;

namespace Agentweaver.Projects.Config.AgentSuite;

/// <summary>
/// The trusted AgentSuite JSON Schema bundle copied byte-for-byte from the pinned upstream module.
/// Schemas are embedded resources and are never fetched remotely; every file is pinned by SHA-256.
/// </summary>
internal static class AgentSuiteSchemaBundle
{
    public const string Commit = "a09950a75359cdc7de4d3361694582613f24c732";
    public const string SpecVersion = "1.0.0-draft";
    private const string ResourcePrefix = "AgentSuite.Schemas.";

    public static readonly FrozenDictionary<string, string> PinnedDigests = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["administrative-policy-v2.schema.json"] = "sha256:e2ad4b9b5db673373f7bf4451a425c691705ec0a4a3df07262e3de7e94f999be",
        ["agent.schema.json"] = "sha256:224409759c4aa52d54c5d81103db7fc36e30d03c2714117c74b997269232b6a9",
        ["build-profile.schema.json"] = "sha256:d156e8955a8fa84d60f787cd95f113b0d643871e540d60af30267324f4aa63b1",
        ["composition.schema.json"] = "sha256:f35cdeb76079d3db2ed6ed4b2286bc415ec35002a65013f41244a28e78c7e422",
        ["eval-case.schema.json"] = "sha256:acf6b756e87348d5212317f8d04e61efa183c65c31a15ef54b20904d37479faf",
        ["eval-evidence.schema.json"] = "sha256:7c35edef384659e46f13590448002407a02b74cc59bae7717af3e375cc160a9a",
        ["eval-index.schema.json"] = "sha256:57a18c030b4fdbf5d53b5e1be79a746bb8b2123fd610e897446199183598a6a7",
        ["execution.schema.json"] = "sha256:3460904678b8c5608e79537c176181787e48dc291055fb5f7d7df24e559c3952",
        ["inference.schema.json"] = "sha256:07e09fada65295c9d345266e553d9ac4c9c30e33ef045d206fe3b1d33ef8b306",
        ["policy-v2.schema.json"] = "sha256:600e7b2411ae0b1e20bb616bc1f22fe87a968928a0f2b1c94ae5ac92225fb620",
        ["runtime-profile.schema.json"] = "sha256:d7d18b769b6016603459f98179a51ed044048fa7e0776fcda4fcd9c92f105c0f",
        ["suite.schema.json"] = "sha256:902e9e335c8b7ad317fa02953a718d5a03a79f52240ba7cd11466b387fcbdbfd",
        ["tool-provider-composition.schema.json"] = "sha256:597387e53a27f27e1f27d04989487142361242349f674330f68c831f7dc511c0",
        ["tool-provider.schema.json"] = "sha256:0935f9285be64236af6156d11b78b818a697c0a8bea8eeea58199e8aeef3ecb8",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly Lazy<bool> Verified = new(() => { Verify(); return true; }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Verifies the bundle once per process; later calls are free.</summary>
    public static void EnsureVerified() => _ = Verified.Value;

    /// <summary>Throws when an embedded schema is missing, extra, or differs from its pinned digest.</summary>
    public static void Verify()
    {
        var assembly = typeof(AgentSuiteSchemaBundle).Assembly;
        var embedded = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Select(name => name[ResourcePrefix.Length..])
            .ToHashSet(StringComparer.Ordinal);
        foreach (var extra in embedded.Where(name => !PinnedDigests.ContainsKey(name)))
            throw new InvalidOperationException($"AgentSuite schema bundle contains unpinned file '{extra}'.");
        foreach (var (name, _) in PinnedDigests)
        {
            if (!embedded.Contains(name))
                throw new InvalidOperationException($"AgentSuite schema bundle is missing '{name}'.");
            if (!MatchesPin(name, Read(name)))
                throw new InvalidOperationException($"AgentSuite schema '{name}' does not match its pinned digest.");
        }
    }

    /// <summary>Returns true when the bytes match the pinned digest for the named schema.</summary>
    public static bool MatchesPin(string name, ReadOnlySpan<byte> data) =>
        PinnedDigests.TryGetValue(name, out var pinned) &&
        string.Equals(pinned, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(data)), StringComparison.Ordinal);

    /// <summary>Returns the exact embedded bytes of one pinned schema.</summary>
    public static byte[] Read(string name)
    {
        if (!PinnedDigests.ContainsKey(name))
            throw new ArgumentException($"Unknown AgentSuite schema '{name}'.", nameof(name));
        using var stream = typeof(AgentSuiteSchemaBundle).Assembly.GetManifestResourceStream(ResourcePrefix + name)
            ?? throw new InvalidOperationException($"AgentSuite schema bundle is missing '{name}'.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
