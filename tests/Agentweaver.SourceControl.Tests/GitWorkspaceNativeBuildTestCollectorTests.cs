using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Xunit;
using WorkspaceRequest = Agentweaver.Abstractions.SandboxBuildTestBindingPreparation.SandboxBuildTestWorkspaceRequest;
using PreparationReceipt = Agentweaver.Abstractions.SandboxBuildTestBindingPreparation.SandboxBuildTestWorkspacePreparationReceipt;

namespace Agentweaver.SourceControl.Tests;

public sealed partial class GitWorkspaceManagerTests
{
    [NativeHostImageFact]
    public async Task ActualHostImageRetainsCompletePreparedRepositoryBytesAcrossCollectorProcesses()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("AGENTWEAVER_BUILDTEST_REPOSITORY_ROOT");
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredRoot);
        var root = Path.GetFullPath(configuredRoot);
        Assert.True(File.Exists(Path.Combine(root, "services", "agenthost", "Agentweaver.AgentHost", "Dockerfile")));
        var engine = Environment.GetEnvironmentVariable("AGENTWEAVER_CONTAINER_ENGINE") ?? "docker";
        Assert.Contains(engine, new[] { "docker", "podman" });
        var suffix = "aw-buildtest-native-" + Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(root, "Temp", suffix);
        var artifact = Path.Combine(root, "artifacts", "buildtest-native", suffix);
        Directory.CreateDirectory(temporary);
        Directory.CreateDirectory(artifact);
        var image = suffix + ":source-check";
        var workspaceVolume = suffix + "-workspace";
        var preparationVolume = suffix + "-preparation";
        var captureVolume = suffix + "-capture";
        var volumes = new List<string>();
        var containers = new List<string>();
        var imageBuilt = false;
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        try
        {
            var sourceSha = (await RunFixtureGitAsync(root, ["rev-parse", "HEAD"])).Trim();
            var sourceTree = (await RunFixtureGitAsync(root, ["rev-parse", "HEAD^{tree}"])).Trim();
            Assert.Equal(Environment.GetEnvironmentVariable("AGENTWEAVER_BUILDTEST_SOURCE_SHA"), sourceSha);
            Assert.Empty((await RunFixtureGitAsync(root, ["status", "--porcelain", "--untracked-files=normal"])).Trim());
            var fixture = await CreateBuildTestSeedFixtureAsync(temporary);
            var source = fixture.Source;
            var operation = Guid.NewGuid();
            var commandUid = suffix + "-command-uid";
            var collectorUid = suffix + "-collector-uid";
            var checkpoint = new SandboxBuildTestCheckpointReference(
                source.Identity.ProjectId, source.Identity.RunId, source.Identity.SessionId,
                "checkpoint", "plan", "step", 1, 1, 1, source.AcceptedSelectionHash);
            var outputs = ImmutableArray.Create(
                new SandboxBuildTestOutputObligation("result", "out/result.bin", true, 128),
                new SandboxBuildTestOutputObligation("count", "out/count.txt", true, 128));
            var preparation = new WorkspaceRequest(operation, "sha256:" + new string('a', 64),
                new string('b', 64), checkpoint, source, "/workspace", true, outputs).Validate();
            var capture = preparation with { PreparingWorkspace = false };
            var preparedPath = "/workspace/" + source.PreparedRepositoryRelativePath;

            await EngineAsync(["build", "--platform", "linux/amd64", "--file",
                "services/agenthost/Agentweaver.AgentHost/Dockerfile", "--tag", image, "."],
                timeout: TimeSpan.FromMinutes(15));
            imageBuilt = true;
            var inspection = await EngineAsync(["image", "inspect", image]);
            using var inspected = JsonDocument.Parse(inspection.Stdout);
            var imageInfo = inspected.RootElement[0];
            Assert.Equal("linux", imageInfo.GetProperty("Os").GetString());
            Assert.Equal("amd64", imageInfo.GetProperty("Architecture").GetString());
            Assert.Equal("1654:1654", imageInfo.GetProperty("Config").GetProperty("User").GetString());
            foreach (var volume in new[] { workspaceVolume, preparationVolume, captureVolume })
            {
                await EngineAsync(["volume", "create", volume]);
                volumes.Add(volume);
            }
            await ShellAsync("setup", "chown 1000:1000 /workspace /state",
                workspaceVolume, preparationVolume, writableWorkspace: true, rootUser: true);
            await ShellAsync("setup-capture", "chown 1000:1000 /state",
                workspaceVolume, captureVolume, writableWorkspace: false, rootUser: true);

            var prepared = await HelperAsync("prepare", preparation, commandUid, preparationVolume,
                fixture.Seed.Bytes, writableWorkspace: true);
            var receipt = JsonSerializer.Deserialize<PreparationReceipt>(
                RemoveReady(prepared.Stdout, preparation, commandUid), json);
            Assert.NotNull(receipt);
            Assert.Equal(operation, receipt.OperationId);
            Assert.Equal(commandUid, receipt.PodUid);
            Assert.Equal(source.PreparedRepositoryRelativePath, receipt.RepositoryRelativePath);
            Assert.Equal(source.SeedSha256, receipt.SeedSha256);
            Assert.Equal(source.BaselineTreeSha, receipt.BaselineTreeSha);
            var nativeCommand = await ShellAsync("command",
                $"set -eu; cd '{preparedPath}'; test \"$(git rev-parse HEAD)\" = '{source.BaseSha}'; " +
                $"test \"$(git write-tree)\" = '{source.BaselineTreeSha}'; " +
                "test -z \"$(git remote)\"; mkdir out; printf '\\000\\377\\200\\012' > out/result.bin; " +
                "printf '1\\n' > out/count.txt; pwd",
                workspaceVolume, preparationVolume, writableWorkspace: true, mountState: false);
            Assert.Equal(preparedPath, Encoding.UTF8.GetString(nativeCommand.Stdout).Trim());

            var first = await HelperAsync("capture", capture, collectorUid, captureVolume, fixture.Seed.Bytes);
            var referenceBytes = RemoveReady(first.Stdout, capture, collectorUid);
            var reference = JsonSerializer.Deserialize<SandboxBuildTestWorkspaceCaptureReference>(referenceBytes, json);
            Assert.NotNull(reference);
            Assert.Equal(operation, reference.OperationId);
            Assert.Equal(checkpoint, reference.Checkpoint);
            Assert.Equal(capture.ImmutableHash, reference.ImmutableHash);
            Assert.Equal(capture.RequestFingerprint, reference.RequestFingerprint);
            Assert.Equal(collectorUid, reference.PodUid);
            Assert.Equal(source.PreparedRepositoryRelativePath, reference.RepositoryRelativePath);
            Assert.Equal(source.SeedSha256, reference.SeedSha256);
            var exported = await HelperAsync("export", capture, collectorUid, captureVolume, []);
            var bytes = RemoveReady(exported.Stdout, capture, collectorUid);
            var document = GitWorkspaceBuildTestOutput.Parse(bytes, reference, source, fixture.Seed.Bytes, outputs);
            Assert.Equal(fixture.Seed.Baseline.Manifest.Files.Length + outputs.Length, document.Manifest.Files.Length);
            foreach (var baseline in fixture.Seed.Baseline.Manifest.Files)
                Assert.Equal(
                    GitWorkspaceCapturePackage.ExtractFile(fixture.Seed.Baseline.PackageBytes,
                        fixture.Seed.Baseline.Manifest, fixture.Seed.Baseline.PackageSha256, baseline.Path),
                    GitWorkspaceCapturePackage.ExtractFile(document.PackageBytes, document.Manifest,
                        document.PackageSha256, baseline.Path));
            Assert.Equal(new byte[] { 0, 255, 128, 10 },
                GitWorkspaceCapturePackage.ExtractFile(document.PackageBytes, document.Manifest,
                    document.PackageSha256, "out/result.bin"));
            Assert.Equal("1\n", Encoding.UTF8.GetString(
                GitWorkspaceCapturePackage.ExtractFile(document.PackageBytes, document.Manifest,
                    document.PackageSha256, "out/count.txt")));

            await ShellAsync("change-after-seal",
                $"printf changed > '{preparedPath}/out/result.bin'",
                workspaceVolume, preparationVolume, writableWorkspace: true, mountState: false);
            var prepareReplay = await HelperAsync("prepare", preparation, commandUid, preparationVolume,
                fixture.Seed.Bytes, writableWorkspace: true);
            Assert.Equal(RemoveReady(prepared.Stdout, preparation, commandUid),
                RemoveReady(prepareReplay.Stdout, preparation, commandUid));
            var stillChanged = await ShellAsync("check-not-reseeded", $"cat '{preparedPath}/out/result.bin'",
                workspaceVolume, preparationVolume, writableWorkspace: true, mountState: false);
            Assert.Equal("changed", Encoding.UTF8.GetString(stillChanged.Stdout));
            var repeated = await HelperAsync("capture", capture, collectorUid, captureVolume, fixture.Seed.Bytes);
            Assert.Equal(referenceBytes, RemoveReady(repeated.Stdout, capture, collectorUid));
            var replay = await HelperAsync("export", capture, collectorUid, captureVolume, []);
            Assert.Equal(bytes, RemoveReady(replay.Stdout, capture, collectorUid));
            await ShellAsync("reference-write-interrupted",
                $"mv /state/buildtest/{operation:N}/reference.json /state/buildtest/{operation:N}/reference.json.pending",
                workspaceVolume, captureVolume, writableWorkspace: false);
            var recovered = await HelperAsync("capture", capture, collectorUid, captureVolume, fixture.Seed.Bytes);
            Assert.Equal(referenceBytes, RemoveReady(recovered.Stdout, capture, collectorUid));

            foreach (var stale in new[] { "pod", "operation" })
            {
                var request = stale == "operation" ? capture with { OperationId = Guid.NewGuid() } : capture;
                var uid = stale == "pod" ? "foreign-collector-uid" : collectorUid;
                var denied = await HelperAsync("export", request, uid, captureVolume, [], allowFailure: true);
                Assert.Equal(4, denied.ExitCode);
                Assert.Equal("collector_read_failed", denied.Stderr.Trim());
                Assert.Empty(RemoveReady(denied.Stdout, request, uid));
            }
            await ShellAsync("unsafe-native-tree", $"ln -s out/result.bin '{preparedPath}/bad-link'",
                workspaceVolume, preparationVolume, writableWorkspace: true, mountState: false);
            var unsafeRequest = capture with { OperationId = Guid.NewGuid() };
            var unsafeTree = await HelperAsync("capture", unsafeRequest, collectorUid, captureVolume,
                fixture.Seed.Bytes, allowFailure: true);
            Assert.Equal(4, unsafeTree.ExitCode);
            Assert.Equal("collector_read_failed", unsafeTree.Stderr.Trim());
            Assert.Empty(RemoveReady(unsafeTree.Stdout, unsafeRequest, collectorUid));
            await File.WriteAllBytesAsync(Path.Combine(artifact, "output.capture"), bytes);
            await File.WriteAllBytesAsync(Path.Combine(artifact, "reference.json"), referenceBytes);
            await File.WriteAllTextAsync(Path.Combine(artifact, "receipt.json"), JsonSerializer.Serialize(new
            {
                sourceSha, sourceTree, imageId = imageInfo.GetProperty("Id").GetString(),
                platform = "linux/amd64", engine, operation, commandUid, collectorUid, preparedPath,
                image, volumes, containers,
                seedSha256 = source.SeedSha256, reference.DocumentSha256, reference.DocumentByteLength,
                processRestartReplay = true, pendingReferenceRecovery = true,
                nativeBaselineVerified = true, preparationDoesNotReseed = true,
                stalePodAndOperationDenied = true, unsafeNativeTreeDenied = true,
                boundary = "Actual Host collector and retained bytes only; no Environment/Core/Events admission, Kubernetes or deployment."
            }, json));

            async Task<NativeProcessResult> HelperAsync(
                string action, WorkspaceRequest request, string uid, string stateVolume, byte[] input,
                bool writableWorkspace = false, bool allowFailure = false)
            {
                var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(request, json))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                var args = RunArguments(action, stateVolume, writableWorkspace, rootUser: false, mountState: true);
                args.AddRange(["--env", $"AGENTWEAVER_COLLECTOR_POD_UID={uid}", image,
                    SandboxBuildTestLimits.WorkspaceCollectorMode, encoded, action]);
                var result = await EngineAsync(args, input, allowFailure: allowFailure);
                if (!allowFailure)
                    Assert.Empty(result.Stderr);
                return result;
            }

            Task<NativeProcessResult> ShellAsync(
                string name, string script, string workspace, string stateVolume, bool writableWorkspace,
                bool rootUser = false, bool mountState = true)
            {
                Assert.Equal(workspaceVolume, workspace);
                var args = RunArguments(name, stateVolume, writableWorkspace, rootUser, mountState);
                args.AddRange(["--entrypoint", "/bin/sh", image, "-c", script]);
                return EngineAsync(args);
            }

            List<string> RunArguments(
                string name, string stateVolume, bool writableWorkspace, bool rootUser, bool mountState)
            {
                var container = suffix + "-" + name + "-" + containers.Count;
                containers.Add(container);
                var args = new List<string>
                {
                    "run", "--rm", "--name", container, "--pull=never", "--platform", "linux/amd64",
                    "--network=none", "--read-only", "--user", rootUser ? "0:0" : "1000:1000",
                    "--mount", $"type=volume,source={workspaceVolume},destination=/workspace" +
                        (writableWorkspace ? "" : ",readonly")
                };
                if (!rootUser)
                    args.AddRange(["--cap-drop=ALL", "--security-opt=no-new-privileges"]);
                if (mountState)
                    args.AddRange(["--mount", $"type=volume,source={stateVolume},destination=/state"]);
                args.Add("-i");
                return args;
            }
        }
        finally
        {
            foreach (var container in containers)
            {
                var exists = await EngineAsync(["container", "inspect", container], allowFailure: true);
                if (exists.ExitCode == 0)
                    await EngineAsync(["container", "rm", "--force", container]);
                else
                    Assert.Equal(1, exists.ExitCode);
            }
            foreach (var volume in volumes)
                await EngineAsync(["volume", "rm", volume]);
            if (imageBuilt)
                await EngineAsync(["image", "rm", image]);
            DeleteTemporaryDirectory(temporary);
            await File.WriteAllTextAsync(Path.Combine(artifact, "cleanup.json"), JsonSerializer.Serialize(new
            {
                containers, volumes, image, imageBuilt, temporary,
                namedContainersRemoved = true, createdVolumesRemoved = true,
                builtImageRemoved = imageBuilt, fixtureDirectoryRemoved = true
            }, json));
        }

        async Task<NativeProcessResult> EngineAsync(
            IEnumerable<string> arguments, byte[]? input = null, bool allowFailure = false, TimeSpan? timeout = null)
        {
            var start = new ProcessStartInfo(engine)
            {
                WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Container engine did not start.");
            using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(2));
            using var stdout = new MemoryStream();
            var readOutput = process.StandardOutput.BaseStream.CopyToAsync(stdout, deadline.Token);
            var readError = process.StandardError.ReadToEndAsync(deadline.Token);
            try
            {
                if (input is not null)
                    await process.StandardInput.BaseStream.WriteAsync(input, deadline.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(deadline.Token);
                await readOutput;
                var result = new NativeProcessResult(process.ExitCode, stdout.ToArray(), await readError);
                if (!allowFailure)
                {
                    Assert.True(result.ExitCode == 0, result.Stderr);
                }
                return result;
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
        }
    }

    private static byte[] RemoveReady(byte[] output, WorkspaceRequest request, string podUid)
    {
        var ready = Encoding.UTF8.GetBytes($"AWPODIO2 {podUid} {request.RequestFingerprint}\n");
        Assert.True(output.AsSpan().StartsWith(ready));
        return output[ready.Length..];
    }

    private sealed record NativeProcessResult(int ExitCode, byte[] Stdout, string Stderr);

    private sealed class NativeHostImageFactAttribute : FactAttribute
    {
        public NativeHostImageFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("AGENTWEAVER_BUILDTEST_NATIVE_IMAGE_CHECK") != "1")
                Skip = "Requires the separately approved actual Host image check.";
        }
    }
}
