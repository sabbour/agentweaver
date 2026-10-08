extern alias AgentHostService;

using System.Security.Cryptography;
using System.Text.Json;
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
    public void ImageOwnedManifestRequiresPinnedSdkCompatibleRuntimeAndVerifiedExecutable(string change)
    {
        var directory = Path.Combine(Path.GetTempPath(), "agenthost-manifest-" + Guid.NewGuid().ToString("N"));
        var native = Directory.CreateDirectory(Path.Combine(directory, "native")).FullName;
        try
        {
            var content = "controlled native executable bytes"u8.ToArray();
            File.WriteAllBytes(Path.Combine(native, "copilot"), content);
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
}
