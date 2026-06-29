using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Auth;

/// <summary>
/// When a user signs in, patches the <c>agentweaver-user-tokens</c> SecretProviderClass
/// to add their KV token secret (<c>ghtok-user--{base32(userId)}</c>) to the objects list
/// and the corresponding key in secretObjects. The CSI driver then begins syncing that
/// user's token to the K8s Secret and rotating it every 2 minutes.
/// </summary>
public sealed class AgentHostUserTokenSyncService
{
    // SecretProviderClass CRD coordinates (secrets-store CSI driver).
    private const string SpcGroup = "secrets-store.csi.x-k8s.io";
    private const string SpcVersion = "v1";
    private const string SpcPlural = "secretproviderclasses";
    private const string SpcName = "agentweaver-user-tokens";

    private readonly IKubernetes? _k8s;
    private readonly ILogger<AgentHostUserTokenSyncService> _logger;
    private readonly string _namespace;

    public AgentHostUserTokenSyncService(
        IKubernetes? k8s,
        ILogger<AgentHostUserTokenSyncService> logger,
        string? @namespace = null)
    {
        _k8s = k8s;
        _logger = logger;
        _namespace = string.IsNullOrWhiteSpace(@namespace) ? "agentweaver" : @namespace!;
    }

    /// <summary>
    /// Ensures the signed-in user's Key Vault token secret is listed in the
    /// <c>agentweaver-user-tokens</c> SecretProviderClass so the CSI driver begins mounting and
    /// rotating it. Best-effort: any failure is logged and swallowed — sign-in must never fail
    /// because the SPC could not be patched.
    /// </summary>
    /// <param name="login">The GitHub login (used only for logging context).</param>
    /// <param name="userId">The user id; the KV/K8s key names are derived from this.</param>
    public async Task EnsureUserTokenInSpcAsync(string login, string userId, CancellationToken ct = default)
    {
        if (_k8s is null)
        {
            _logger.LogWarning(
                "AgentHostUserTokenSyncService: no Kubernetes client available (not in-cluster); " +
                "skipping SPC sync for user {Login}.", login);
            return;
        }

        if (string.IsNullOrWhiteSpace(userId))
            return;

        // KV secret name — must match KeyVaultSecretStore.SanitizeKey("user:{userId}").
        var kvSecretName = "ghtok-user--" + Base32Lower(Encoding.UTF8.GetBytes(userId));

        // K8s Secret key (the mounted filename) — must match SharedTokenStorePaths.SanitizeKey for
        // the "user:{userId}" scope: letters/digits/'-'/'_' kept, everything else -> '_'.
        var userJsonKey = SanitizeScopeKey("user:" + userId) + ".json";

        try
        {
            var raw = await _k8s.CustomObjects
                .GetNamespacedCustomObjectAsync(SpcGroup, SpcVersion, _namespace, SpcPlural, SpcName, cancellationToken: ct)
                .ConfigureAwait(false);

            var json = JsonSerializer.Serialize(raw);
            var root = JsonNode.Parse(json);
            if (root is null)
            {
                _logger.LogWarning(
                    "AgentHostUserTokenSyncService: SPC {Spc} returned no parseable body; skipping sync for user {Login}.",
                    SpcName, login);
                return;
            }

            var objectsYaml = root["spec"]?["parameters"]?["objects"]?.GetValue<string>() ?? string.Empty;

            // Already present — nothing to do (idempotent; avoids redundant API writes on every sign-in).
            if (objectsYaml.Contains($"objectName: {kvSecretName}", StringComparison.Ordinal))
            {
                _logger.LogDebug(
                    "AgentHostUserTokenSyncService: user {Login} ({KvSecret}) already present in SPC {Spc}; no patch.",
                    login, kvSecretName, SpcName);
                return;
            }

            // Append a canonical objects entry (literal block scalar) preserving the existing list.
            var trimmed = objectsYaml.TrimEnd('\n');
            var newEntry = $"  - |\n    objectName: {kvSecretName}\n    objectType: secret";
            var newObjectsYaml = string.IsNullOrEmpty(trimmed)
                ? $"array:\n{newEntry}\n"
                : trimmed + "\n" + newEntry + "\n";

            // Clone the current secretObjects array and append this user's data mapping. A JSON merge
            // patch replaces arrays wholesale, so the patch must carry the FULL updated array.
            var secretObjects = root["spec"]?["secretObjects"]?.AsArray();
            JsonNode secretObjectsForPatch;
            if (secretObjects is { Count: > 0 })
            {
                var clone = JsonNode.Parse(secretObjects.ToJsonString())!.AsArray();
                var data = clone[0]?["data"]?.AsArray();
                if (data is null)
                {
                    data = new JsonArray();
                    clone[0]!["data"] = data;
                }
                data.Add(new JsonObject
                {
                    ["objectName"] = kvSecretName,
                    ["key"] = userJsonKey,
                });
                secretObjectsForPatch = clone;
            }
            else
            {
                // No secretObjects yet — create a minimal one targeting the shared K8s Secret.
                secretObjectsForPatch = new JsonArray
                {
                    new JsonObject
                    {
                        ["secretName"] = SpcName,
                        ["type"] = "Opaque",
                        ["data"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["objectName"] = kvSecretName,
                                ["key"] = userJsonKey,
                            },
                        },
                    },
                };
            }

            var patch = new JsonObject
            {
                ["spec"] = new JsonObject
                {
                    ["parameters"] = new JsonObject { ["objects"] = newObjectsYaml },
                    ["secretObjects"] = secretObjectsForPatch,
                },
            };

            var v1Patch = new V1Patch(patch.ToJsonString(), V1Patch.PatchType.MergePatch);
            await _k8s.CustomObjects
                .PatchNamespacedCustomObjectAsync(v1Patch, SpcGroup, SpcVersion, _namespace, SpcPlural, SpcName, cancellationToken: ct)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "AgentHostUserTokenSyncService: added user {Login} ({KvSecret} -> {Key}) to SPC {Spc}.",
                login, kvSecretName, userJsonKey, SpcName);
        }
        catch (Exception ex)
        {
            // Best-effort: a sync failure must NOT fail sign-in.
            _logger.LogWarning(ex,
                "AgentHostUserTokenSyncService: failed to sync user {Login} ({KvSecret}) into SPC {Spc} (best-effort).",
                login, kvSecretName, SpcName);
        }
    }

    // Sanitizes a token scope key to a filename exactly as FileSystemGitHubTokenStore /
    // SharedTokenStorePaths.SanitizeKey does (letters/digits/'-'/'_' kept, everything else -> '_').
    private static string SanitizeScopeKey(string key) =>
        string.Concat(key.Select(c =>
            char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));

    // Base32 (RFC 4648) lower-case alphabet, no padding — mirrors KeyVaultSecretStore.Base32Lower.
    private static readonly char[] Base32Alphabet = "abcdefghijklmnopqrstuvwxyz234567".ToCharArray();

    private static string Base32Lower(byte[] data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bitsLeft = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                sb.Append(Base32Alphabet[(buffer >> bitsLeft) & 0x1F]);
            }
        }
        if (bitsLeft > 0)
            sb.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        return sb.ToString();
    }
}
