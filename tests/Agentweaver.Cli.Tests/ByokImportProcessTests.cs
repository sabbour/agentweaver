using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Agentweaver.Cli.Tests;

public sealed class ByokImportProcessTests
{
    private const string ModeVariable = "AGENTWEAVER_IMPORT_PROCESS_PROBE";
    private const string DirectoryVariable = "AGENTWEAVER_IMPORT_PROCESS_DIRECTORY";

    [Fact]
    public async Task ImportRecoversAfterProcessRestartWithoutHiddenState()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "import-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "overlay.json"), """
            {"kind":"agentweaver.model-bindings-overlay","schemaVersion":1,"baseRevision":"base",
             "baseModelBindings":{},"revision":"next","modelBindings":{
             "Alpha":{"ModelId":"model","SourceMode":"Byok","Provider":{"Type":"openai","BaseUrl":"https://model.example/v1","WireApi":"responses"},"SupportedReasoningEfforts":["low"]},
             "Zulu":{"ModelId":"model","SourceMode":"Byok","Provider":{"Type":"openai","BaseUrl":"https://model.example/v1","WireApi":"responses"},"SupportedReasoningEfforts":["low"]}},
             "rateCard":{"id":"price","version":"2026-01-01"},"modelRates":{"model":{"input":"1","output":"2"}}}
            """);
        await File.WriteAllTextAsync(Path.Combine(directory, "context.json"), """
            {"issuer":"https://issuer.example","tenantId":"tenant-1","actorId":"actor-1","installationId":"00112233-4455-4677-8899-aabbccddeeff"}
            """);
        using var crash = StartProbe(directory, "crash");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var marker = Path.Combine(directory, "committed");
            while (!File.Exists(marker))
            {
                if (crash.HasExited)
                    throw new InvalidOperationException(await crash.StandardError.ReadToEndAsync());
                await Task.Delay(50, timeout.Token);
            }
            crash.Kill(entireProcessTree: true);
            await crash.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotEqual(0, crash.ExitCode);

            await RunProbe(directory, "wrong-target");
            var wrong = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "wrong-target.json")))!;
            Assert.Empty(wrong["posts"]!.AsArray());
            Assert.Equal("expected_context_mismatch", wrong["summary"]!["stop"]!["code"]!.GetValue<string>());

            await RunProbe(directory, "recover");
            var recovered = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "recover.json")))!;
            Assert.Equal(0, recovered["exit"]!.GetValue<int>());
            Assert.Equal(2, recovered["summary"]!["completed"]!.AsArray().Count);
            var saved = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "owner-state.json")))!.AsObject();
            var firstBody = saved.First().Value!["body"]!.GetValue<string>();
            Assert.Equal(firstBody, recovered["posts"]![0]!.ToJsonString());
        }
        finally
        {
            if (!crash.HasExited)
            {
                crash.Kill(entireProcessTree: true);
                await crash.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ImportProcessProbe()
    {
        var mode = Environment.GetEnvironmentVariable(ModeVariable);
        if (mode is null) return;
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable)!;
        using var gateway = new ImportGatewayFixture();
        var statePath = Path.Combine(directory, "owner-state.json");
        if (File.Exists(statePath))
        {
            var saved = JsonNode.Parse(await File.ReadAllTextAsync(statePath))!.AsObject();
            foreach (var (operation, entry) in saved)
                gateway.Receipts.Add(operation, (entry!["body"]!.GetValue<string>(),
                    entry["actor"]!.GetValue<string>(), entry["receipt"]!.DeepClone().AsObject()));
        }
        if (mode == "wrong-target")
            gateway.Target["installationId"] = "00112233-4455-4677-8899-aabbccddee00";
        if (mode == "crash")
        {
            gateway.AfterCreate = (_, _, response) =>
            {
                var saved = new JsonObject();
                foreach (var (operation, entry) in gateway.Receipts)
                    saved[operation] = new JsonObject
                    {
                        ["body"] = entry.Body, ["actor"] = entry.Actor, ["receipt"] = entry.Receipt.DeepClone(),
                    };
                File.WriteAllText(statePath, saved.ToJsonString());
                File.WriteAllText(Path.Combine(directory, "committed"), "committed");
                Thread.Sleep(Timeout.Infinite);
                return response;
            };
        }
        var overlay = Path.Combine(directory, "overlay.json");
        var digest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(overlay)));
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await CliApplication.RunAsync(
            ["models", "byok", "import", "--model-bindings", overlay, "--file-sha256", digest,
                "--expected-context", Path.Combine(directory, "context.json"),
                "--gateway-url", "https://gateway.example/", "--broker-issuer", "https://issuer.example/",
                "--api-audience", "https://gateway.example/", "--tenant-id", "tenant-1"],
            new CliRuntimeOptions(HttpClient: gateway.Client, Authentication: gateway, Output: output, Error: error));
        var result = new JsonObject
        {
            ["exit"] = exit,
            ["summary"] = JsonNode.Parse(output.ToString()),
            ["posts"] = new JsonArray(gateway.PostBodies.Select(body => (JsonNode?)body.DeepClone()).ToArray()),
        };
        await File.WriteAllTextAsync(Path.Combine(directory, mode + ".json"), result.ToJsonString());
    }

    private static async Task RunProbe(string directory, string mode)
    {
        using var process = StartProbe(directory, mode);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        Assert.True(process.ExitCode == 0, output + error);
    }

    private static Process StartProbe(string directory, string mode)
    {
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Agentweaver.Cli.Tests.csproj"));
        var configuration = new DirectoryInfo(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!).Name;
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        foreach (var item in new[] { "test", project, "--no-build", "--no-restore", "--configuration", configuration,
                     "--filter", "FullyQualifiedName~ByokImportProcessTests.ImportProcessProbe" })
            start.ArgumentList.Add(item);
        start.Environment[ModeVariable] = mode;
        start.Environment[DirectoryVariable] = directory;
        return Process.Start(start)!;
    }
}
