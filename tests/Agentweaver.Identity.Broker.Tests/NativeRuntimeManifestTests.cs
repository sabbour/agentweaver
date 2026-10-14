extern alias AgentHostService;

using System.Security.Cryptography;
using System.Text.Json;
using Agentweaver.AgentRuntime;
using AgentHostService::Agentweaver.AgentHost;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class NativeRuntimeManifestTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("sdk")]
    [InlineData("runtime")]
    [InlineData("archive")]
    [InlineData("archive-format")]
    [InlineData("checksum")]
    [InlineData("missing")]
    [InlineData("missing-checksum")]
    [InlineData("empty")]
    [InlineData("not-elf")]
    [InlineData("wrong-machine")]
    public void ImageOwnedManifestRequiresPinnedSdkCompatibleRuntimeAndVerifiedExecutable(string change)
    {
        var directory = Path.Combine(Path.GetTempPath(), "agenthost-manifest-" + Guid.NewGuid().ToString("N"));
        var native = Directory.CreateDirectory(Path.Combine(directory, "native")).FullName;
        try
        {
            var content = CreateAmd64ElfFixture();
            if (change == "empty")
                content = [];
            if (change == "not-elf")
                content = "not an ELF executable"u8.ToArray();
            if (change == "wrong-machine")
                content[18] = 0xb7;
            File.WriteAllBytes(Path.Combine(native, "copilot"), content);
            File.WriteAllText(Path.Combine(native, "copilot.sha256"),
                change == "checksum" ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(content)));
            var manifest = new NativeRuntimeManifest(
                change == "sdk" ? "1.0.17" : "1.0.18",
                change == "runtime" ? "1.0.79" : "1.0.97",
                change == "archive" ? new string('a', 128) :
                    change == "archive-format" ? null! :
                    "262068b5c39fd83706bec46a11346ed28a33a99f4ceab1fc9e30a416de30ee4059beb1c6f8c1e1228d666c6289b28d86663067655b6f258b0a5cc655dcdc7f8a");
            File.WriteAllText(Path.Combine(directory, "runtime-manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            if (change == "missing")
                File.Delete(Path.Combine(native, "copilot"));
            if (change == "missing-checksum")
                File.Delete(Path.Combine(native, "copilot.sha256"));
            if (change == "valid")
            {
                var verified = NativeRuntimeManifest.ReadAndVerify(directory);
                Assert.Equal(Path.Combine(native, "copilot"), verified.ExecutablePath);
                Assert.Equal("1.0.97", verified.RuntimeVersion);
            }
            else
                Assert.Throws<InvalidOperationException>(() => NativeRuntimeManifest.ReadAndVerify(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] CreateAmd64ElfFixture()
    {
        var header = new byte[64];
        header[0] = 0x7f;
        header[1] = (byte)'E';
        header[2] = (byte)'L';
        header[3] = (byte)'F';
        header[4] = 2;
        header[5] = 1;
        header[6] = 1;
        header[16] = 2;
        header[18] = 0x3e;
        return header;
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("private-state")]
    [InlineData("ambient-token")]
    public void PrivateStdioRuntimeRequiresOnlyItsImageOwnedDistribution(string change)
    {
        var imageDirectory = Path.Combine(Path.GetTempPath(), "agenthost-image");
        var privateState = Path.Combine(Path.GetTempPath(), "agenthost-private-state");
        var executable = Path.Combine(imageDirectory, "native", "copilot");
        var connection = NativeRuntimeManifest.Connection(executable, privateState);
        Assert.Equal(Path.Combine(imageDirectory, "native"), connection.Environment!["COPILOT_CLI_DIST_DIR"]);
        Assert.Equal(privateState, connection.Environment["HOME"]);
        Assert.Equal("/tmp", connection.Environment["TMPDIR"]);
        Assert.Equal(["--no-auto-update"], connection.Args);
        var environment = new Dictionary<string, string>(connection.Environment);
        connection.Environment = environment;
        if (change == "missing")
            environment.Remove("COPILOT_CLI_DIST_DIR");
        if (change == "private-state")
            environment["COPILOT_CLI_DIST_DIR"] = privateState;
        if (change == "ambient-token")
            environment["GH_TOKEN"] = "controlled-ambient-token";
        if (change == "valid")
            _ = new RuntimeCopilotSessionFactory(connection, privateState, new Dictionary<string, RuntimeModelBinding>());
        else
            Assert.Throws<ArgumentException>(() =>
                new RuntimeCopilotSessionFactory(connection, privateState, new Dictionary<string, RuntimeModelBinding>()));
    }
}
