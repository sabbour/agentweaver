using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using GitHub.Copilot;

namespace Agentweaver.AgentHost;

public sealed record NativeRuntimeManifest(string SdkVersion, string RuntimeVersion, string ArchiveSha512)
{
    private const string ExpectedArchiveSha512 =
        "262068b5c39fd83706bec46a11346ed28a33a99f4ceab1fc9e30a416de30ee4059beb1c6f8c1e1228d666c6289b28d86663067655b6f258b0a5cc655dcdc7f8a";

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
        if (manifest.SdkVersion != "1.0.18" || manifest.SdkVersion != sdkVersion ||
            manifest.RuntimeVersion != "1.0.97" ||
            manifest.ArchiveSha512 != ExpectedArchiveSha512)
            throw new InvalidOperationException("The native runtime manifest does not match the pinned SDK and runtime archive.");
        var executable = Path.Combine(directory, "native", "copilot");
        var checksum = Path.Combine(directory, "native", "copilot.sha256");
        if (!File.Exists(executable) || !File.Exists(checksum))
            throw new InvalidOperationException("The verified native runtime is not part of this image.");

        foreach (var relative in new[] { "index.js", "app.js", "prebuilds/linux-x64/cli-native.node" })
        {
            var file = new FileInfo(Path.Combine(directory, "native", relative));
            if (!file.Exists || file.Length == 0)
                throw new InvalidOperationException("The pinned native distribution is incomplete.");
        }

        using var content = File.OpenRead(executable);
        Span<byte> header = stackalloc byte[20];
        if (content.Length < header.Length)
            throw new InvalidOperationException("The image-owned native runtime is not a Linux x86-64 ELF executable.");
        content.ReadExactly(header);
        if (header[0] != 0x7f || header[1] != 'E' || header[2] != 'L' || header[3] != 'F' ||
            header[4] != 2 || header[5] != 1 || header[18] != 0x3e || header[19] != 0)
            throw new InvalidOperationException("The image-owned native runtime is not a Linux x86-64 ELF executable.");
        content.Position = 0;
        if (File.ReadAllText(checksum).Trim() != Convert.ToHexStringLower(SHA256.HashData(content)))
            throw new InvalidOperationException("The image-owned native runtime checksum does not match.");
        return (executable, manifest.RuntimeVersion);
    }
}
