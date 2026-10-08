using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using GitHub.Copilot;

namespace Agentweaver.AgentHost;

public sealed record NativeRuntimeManifest(string SdkVersion, string RuntimeVersion, string ArchiveSha512)
{
    public static StdioRuntimeConnection Connection(string executable, string privateStateDirectory)
    {
        var connection = RuntimeConnection.ForStdio(executable);
        connection.Args = ["--no-auto-update"];
        connection.Environment = new Dictionary<string, string>
        {
            ["PATH"] = "/usr/local/bin:/usr/bin:/bin",
            ["HOME"] = privateStateDirectory,
            ["LANG"] = "C.UTF-8",
            ["TMPDIR"] = "/tmp",
            ["COPILOT_CLI_DIST_DIR"] = Path.GetDirectoryName(executable)
                ?? throw new ArgumentException("The image-owned runtime requires an absolute executable path.", nameof(executable))
        };
        return connection;
    }

    public static (string ExecutablePath, string RuntimeVersion) ReadAndVerify(string directory)
    {
        var path = Path.Combine(directory, "runtime-manifest.json");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
        };
        var manifest = JsonSerializer.Deserialize<NativeRuntimeManifest>(File.ReadAllBytes(path), options)
            ?? throw new InvalidOperationException("The image-owned native runtime manifest is missing.");
        var sdkVersion = typeof(CopilotClient).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0];
        if (manifest.SdkVersion != "1.0.11" || manifest.SdkVersion != sdkVersion ||
            manifest.RuntimeVersion != "1.0.79" ||
            manifest.ArchiveSha512 is not { Length: 128 } || manifest.ArchiveSha512.Any(character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidOperationException("The native runtime manifest does not match the pinned SDK.");
        var executable = Path.Combine(directory, "native", "copilot");
        var checksum = Path.Combine(directory, "native", "copilot.sha256");
        if (!File.Exists(executable) || !File.Exists(checksum))
            throw new InvalidOperationException("The verified native runtime is not part of this image.");
        foreach (var relative in new[] { "index.js", "app.js", "prebuilds/linux-x64/runtime.node" })
        {
            var file = new FileInfo(Path.Combine(directory, "native", relative));
            if (!file.Exists || file.Length == 0)
                throw new InvalidOperationException("The pinned native distribution is incomplete.");
        }
        using var content = File.OpenRead(executable);
        if (File.ReadAllText(checksum).Trim() != Convert.ToHexStringLower(SHA256.HashData(content)))
            throw new InvalidOperationException("The image-owned native runtime checksum does not match.");
        return (executable, manifest.RuntimeVersion);
    }
}
