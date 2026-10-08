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
    [InlineData("checksum")]
    [InlineData("missing")]
    [InlineData("index")]
    [InlineData("app")]
    [InlineData("addon")]
    public void ImageOwnedManifestRequiresPinnedSdkCompatibleRuntimeAndVerifiedExecutable(string change)
    {
        var directory = Path.Combine(Path.GetTempPath(), "agenthost-manifest-" + Guid.NewGuid().ToString("N"));
        var native = Directory.CreateDirectory(Path.Combine(directory, "native")).FullName;
        try
        {
            var content = "controlled native executable bytes"u8.ToArray();
            File.WriteAllBytes(Path.Combine(native, "copilot"), content);
            File.WriteAllBytes(Path.Combine(native, "index.js"), content);
            File.WriteAllBytes(Path.Combine(native, "app.js"), content);
            var prebuilds = Directory.CreateDirectory(Path.Combine(native, "prebuilds", "linux-x64")).FullName;
            File.WriteAllBytes(Path.Combine(prebuilds, "runtime.node"), content);
            File.WriteAllText(Path.Combine(native, "copilot.sha256"),
                change == "checksum" ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(content)));
            var manifest = new NativeRuntimeManifest(
                change == "sdk" ? "1.0.10" : "1.0.11",
                change == "runtime" ? "1.0.92" : "1.0.79",
                change == "archive" ? null! : new string('a', 128));
            File.WriteAllText(Path.Combine(directory, "runtime-manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            if (change == "missing")
                File.Delete(Path.Combine(native, "copilot"));
            if (change == "index")
                File.Delete(Path.Combine(native, "index.js"));
            if (change == "app")
                File.Delete(Path.Combine(native, "app.js"));
            if (change == "addon")
                File.WriteAllBytes(Path.Combine(prebuilds, "runtime.node"), []);
            if (change == "valid")
            {
                var verified = NativeRuntimeManifest.ReadAndVerify(directory);
                Assert.Equal(Path.Combine(native, "copilot"), verified.ExecutablePath);
                Assert.Equal("1.0.79", verified.RuntimeVersion);
            }
            else
                Assert.Throws<InvalidOperationException>(() => NativeRuntimeManifest.ReadAndVerify(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
