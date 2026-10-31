using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Agentweaver.Cli;

public sealed class ApiAcceptanceRunner : IApiAcceptanceRunner
{
    private readonly IGatewayClientFactory _clients;
    private readonly ICliAuthentication _authentication;
    private readonly IAcceptanceDeploymentEvidenceProvider _deploymentEvidence;
    private readonly TextWriter _output;

    public ApiAcceptanceRunner(
        HttpClient httpClient,
        IGatewayClientFactory gatewayClients,
        ICliAuthentication authentication)
        : this(gatewayClients, authentication, new AcceptanceDeploymentEvidenceProvider(httpClient), Console.Out)
    {
    }

    internal ApiAcceptanceRunner(
        IGatewayClientFactory gatewayClients,
        ICliAuthentication authentication,
        IAcceptanceDeploymentEvidenceProvider deploymentEvidence,
        TextWriter? output = null)
    {
        _clients = gatewayClients ?? throw new ArgumentNullException(nameof(gatewayClients));
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _deploymentEvidence = deploymentEvidence ?? throw new ArgumentNullException(nameof(deploymentEvidence));
        _output = output ?? Console.Out;
    }

    public async Task<int> RunAsync(ApiTestOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        string? outputPath = null;
        AcceptanceManifest? manifest = null;
        JsonObject? sourceEvidence = null;
        List<JsonObject> steps = [];
        List<JsonObject> nativeProbes = [];
        JsonObject observations = NewInitialObservations();
        string surfaceStatus = "blocked";
        string? failure = null;
        AcceptanceFixtureState? fixtureState = null;
        AcceptanceFixture? fixture = null;
        string? invocationId = null;
        string? actorId = null;
        bool invocationReserved = false;

        try
        {
            if (string.IsNullOrWhiteSpace(options.InputPath) || string.IsNullOrWhiteSpace(options.OutputPath))
            {
                throw new InvalidDataException("A P1 input manifest and evidence output path are required.");
            }

            string inputPath = Path.GetFullPath(options.InputPath);
            outputPath = Path.GetFullPath(options.OutputPath);
            if (ApiAcceptanceSupport.PathIdentity(inputPath) == ApiAcceptanceSupport.PathIdentity(outputPath))
            {
                outputPath = null;
                throw new InvalidDataException("The evidence output path must not replace the input manifest.");
            }

            manifest = await ApiAcceptanceSupport.ReadManifestAsync(inputPath, ct).ConfigureAwait(false);
            if (OutputOverwritesInput(options, manifest, inputPath, outputPath))
            {
                outputPath = null;
                throw new InvalidDataException("The evidence output path must not replace a P1 input or deployment artifact.");
            }
            nativeProbes = await ReadNativeProbesAsync(options, inputPath, outputPath, ct).ConfigureAwait(false);
            IReadOnlyDictionary<string, string> proofHashes = await ReadProofHashesAsync(manifest, outputPath, ct).ConfigureAwait(false);
            string planHash = ApiAcceptanceSupport.ExecutionPlanHash(manifest.Root, nativeProbes, proofHashes);

            if (options.DryRun)
            {
                await _output.WriteLineAsync("Dry run only: P1 inputs and proof files are valid. No network request, sign-in, evidence file, project, or assignment was created.")
                    .ConfigureAwait(false);
                return 0;
            }

            sourceEvidence = await _deploymentEvidence.PrepareAsync(manifest, ct).ConfigureAwait(false);
            ApiAcceptanceSupport.AddStep(steps, "source-deployment-binding", "passed",
                ("sourceSha", JsonValue.Create(ApiAcceptanceSupport.GetString(sourceEvidence["source"] as JsonObject ?? [], "sourceSha"))),
                ("sourceTree", JsonValue.Create(ApiAcceptanceSupport.GetString(sourceEvidence["source"] as JsonObject ?? [], "sourceTree"))));

            invocationId = await ReserveInvocationAsync(outputPath, planHash, ct).ConfigureAwait(false);
            invocationReserved = true;
            actorId = ApiAcceptanceSupport.GetString(manifest.Environment, "actorId");
            CliTarget target = new(
                new Uri(ApiAcceptanceSupport.GetString(manifest.Environment, "baseUrl")!, UriKind.Absolute),
                new Uri(ApiAcceptanceSupport.GetString(manifest.Environment, "issuer")!, UriKind.Absolute),
                "agentweaver-cli",
                ApiAcceptanceSupport.GetString(manifest.Environment, "audience")!,
                ApiAcceptanceSupport.GetString(manifest.Environment, "tenantId")!);
            CliIdentity setupIdentity = new(target, ["openid", "api.read", "offline_access", "projects.admin"]);
            IGatewayClient setupApi = _clients.Create(setupIdentity);
            try
            {
                _ = await _authentication.GetBearerAsync(setupIdentity, options.Interaction, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                throw new AcceptanceBlockedException("Unbound Broker authorization for fixture setup and cleanup is unavailable.");
            }

            // Fixture setup starts tenant-scoped; it obtains run-bound authorization only after selection readback.
            (fixture, IGatewayClient runApi, IGatewayClient? deniedApi, fixtureState) =
                await ApiAcceptanceFixtureSetup.CreateAsync(
                    manifest, invocationId, setupApi, _clients, _authentication, options.Interaction, ct)
                    .ConfigureAwait(false);

            try
            {
                (surfaceStatus, failure) = await ApiAcceptanceJourney.RunAsync(
                    manifest,
                    fixture,
                    runApi,
                    deniedApi,
                    sourceEvidence,
                    steps,
                    observations,
                    _ => ApiAcceptanceSupport.StableUuid(invocationId, "surface-idempotency"),
                    ct).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                failure = AppendFailure(failure, error);
                surfaceStatus = "failed";
            }
            finally
            {
                try
                {
                    await ApiAcceptanceFixtureSetup.CleanupAsync(setupApi, fixtureState, actorId!, CancellationToken.None)
                        .ConfigureAwait(false);
                    observations["cleanup"] = fixtureState.Cleanup?.DeepClone();
                }
                catch (Exception error)
                {
                    fixtureState.Cleanup ??= new JsonObject
                    {
                        ["invocationId"] = invocationId,
                        ["state"] = "indeterminate",
                        ["reason"] = "Cleanup did not return every expected receipt; reconcile this invocation before taking further action.",
                    };
                    observations["cleanup"] = fixtureState.Cleanup.DeepClone();
                    failure = AppendFailure(failure, error);
                    surfaceStatus = "failed";
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            fixtureState ??= null;
            failure = "The P1 acceptance run was cancelled.";
            surfaceStatus = "failed";
        }
        catch (AcceptanceFixtureSetupException error)
        {
            fixtureState = error.State;
            failure = ApiAcceptanceSupport.RedactText(error.Message);
            surfaceStatus = "blocked";
            if (manifest is not null && outputPath is not null && actorId is not null)
            {
                try
                {
                    CliTarget target = new(
                        new Uri(ApiAcceptanceSupport.GetString(manifest.Environment, "baseUrl")!, UriKind.Absolute),
                        new Uri(ApiAcceptanceSupport.GetString(manifest.Environment, "issuer")!, UriKind.Absolute),
                        "agentweaver-cli",
                        ApiAcceptanceSupport.GetString(manifest.Environment, "audience")!,
                        ApiAcceptanceSupport.GetString(manifest.Environment, "tenantId")!);
                    CliIdentity cleanupIdentity = new(target, ["openid", "api.read", "offline_access", "projects.admin"]);
                    IGatewayClient cleanupApi = _clients.Create(cleanupIdentity);
                    _ = await _authentication.GetBearerAsync(cleanupIdentity, options.Interaction, CancellationToken.None)
                        .ConfigureAwait(false);
                    await ApiAcceptanceFixtureSetup.CleanupAsync(cleanupApi, fixtureState, actorId, CancellationToken.None).ConfigureAwait(false);
                    observations["cleanup"] = fixtureState.Cleanup?.DeepClone();
                }
                catch
                {
                    fixtureState.Cleanup ??= new JsonObject
                    {
                        ["invocationId"] = fixtureState.InvocationId,
                        ["state"] = "indeterminate",
                        ["reason"] = "Setup failed after fixture writes; reconcile the invocation before taking further action.",
                    };
                    observations["cleanup"] = fixtureState.Cleanup.DeepClone();
                }
            }
        }
        catch (Exception error)
        {
            failure = ApiAcceptanceSupport.RedactText(error.Message);
            surfaceStatus = "blocked";
        }

        if (fixtureState is not null)
        {
            observations["fixtureSetup"] = ApiAcceptanceSupport.SummarizeFixture(
                fixtureState, fixture?.RootSessionId, fixture?.SessionId, fixture?.ExecutionFence);
        }

        JsonObject result = ApiAcceptanceSupport.Result(
            surfaceStatus,
            sourceEvidence,
            steps,
            observations,
            failure,
            nativeProbes,
            manifest);
        // An uncertain setup or cleanup receipt takes precedence over a passed or merely blocked journey result.
        if (fixtureState is not null && ApiAcceptanceSupport.GetString(fixtureState.Cleanup ?? [], "state") == "indeterminate")
        {
            result["surfaceStatus"] = "failed";
            result["fullP1Status"] = "failed";
            result["exitCode"] = 1;
            result["failure"] = new JsonObject
            {
                ["message"] = AppendFailure(
                    ApiAcceptanceSupport.GetString(result["failure"] as JsonObject ?? [], "message"),
                    new InvalidOperationException("A fixture setup or cleanup request may have completed without a verified receipt; reconcile before taking further action.")),
            };
        }

        int exitCode = TryGetExitCode(result);
        if (outputPath is not null && (invocationReserved || !File.Exists(outputPath)))
        {
            try
            {
                using CancellationTokenSource persistenceTimeout = new(TimeSpan.FromSeconds(10));
                await AppendResultAsync(outputPath, result, persistenceTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                await _output.WriteLineAsync($"Could not persist P1 evidence: {ApiAcceptanceSupport.RedactText(error.Message)}").ConfigureAwait(false);
                await _output.WriteLineAsync(ApiAcceptanceSupport.EvidenceJson(ApiAcceptanceSupport.Redact(result))).ConfigureAwait(false);
                return 1;
            }
        }

        await _output.WriteLineAsync(ApiAcceptanceSupport.EvidenceJson(ApiAcceptanceSupport.Redact(result))).ConfigureAwait(false);
        return exitCode;
    }

    private static JsonObject NewInitialObservations() => new()
    {
        ["cleanup"] = new JsonObject { ["intent"] = "none", ["status"] = "not-started" },
        ["adapterCleanup"] = new JsonObject { ["mcp"] = "not-started", ["ui"] = "not-required" },
    };

    private static async Task<List<JsonObject>> ReadNativeProbesAsync(
        ApiTestOptions options,
        string inputPath,
        string outputPath,
        CancellationToken ct)
    {
        List<(string Path, string Kind)> paths = [];
        if (options.NativeProbeEnvelopePath is not null)
            paths.Add((Path.GetFullPath(options.NativeProbeEnvelopePath), "p1-native-acceptance"));
        if (options.DispatchRecoveryEnvelopePath is not null)
            paths.Add((Path.GetFullPath(options.DispatchRecoveryEnvelopePath), "p1-dispatch-recovery-acceptance"));

        HashSet<string> protectedPaths = new(StringComparer.Ordinal)
        {
            ApiAcceptanceSupport.PathIdentity(inputPath),
            ApiAcceptanceSupport.PathIdentity(outputPath),
        };
        foreach ((string path, _) in paths)
        {
            if (!protectedPaths.Add(ApiAcceptanceSupport.PathIdentity(path)))
                throw new InvalidDataException("Probe envelope paths must not replace or duplicate another P1 input or the evidence output.");
        }

        List<JsonObject> rows = [];
        JsonObject? native = null;
        JsonObject? recovery = null;
        foreach ((string path, string kind) in paths)
        {
            JsonObject envelope;
            try
            {
                envelope = JsonNode.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)) as JsonObject
                    ?? throw new InvalidDataException();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                throw new InvalidDataException($"The {kind} output envelope is missing or malformed JSON.");
            }
            if (ApiAcceptanceSupport.GetString(envelope, "kind") != kind)
                throw new InvalidDataException($"The supplied probe output must be an exact {kind} envelope.");
            if (kind == "p1-native-acceptance") native = envelope;
            else recovery = envelope;
        }

        rows.AddRange(ApiAcceptanceSupport.ValidateNativeEnvelopes(native, recovery));
        return rows;
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadProofHashesAsync(
        AcceptanceManifest manifest,
        string outputPath,
        CancellationToken ct)
    {
        JsonObject evidence = ApiAcceptanceSupport.RequireObject(
            manifest.Root["deploymentEvidence"], "Exact source deployment evidence paths are required.");
        List<string> names = ["manifestPath", "imageManifestPath"];
        string outputIdentity = ApiAcceptanceSupport.PathIdentity(outputPath);
        Dictionary<string, string> hashes = new(StringComparer.Ordinal);
        foreach (string name in names)
        {
            string rawPath = ApiAcceptanceSupport.RequireString(evidence, name, $"deployment evidence {name}");
            string fullPath = Path.GetFullPath(Path.IsPathRooted(rawPath)
                ? rawPath
                : Path.Combine(manifest.ManifestDirectory, rawPath));
            if (ApiAcceptanceSupport.PathIdentity(fullPath) == outputIdentity)
                throw new InvalidDataException("The evidence output path must not replace a P1 deployment artifact.");
            byte[] content;
            try
            {
                content = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                throw new InvalidDataException("The exact source-bound publication, required deployment receipt, or manifest is missing or malformed.");
            }
            hashes[name] = ApiAcceptanceSupport.Sha256Hex(content);
        }
        return hashes;
    }

    private static bool OutputOverwritesInput(
        ApiTestOptions options,
        AcceptanceManifest manifest,
        string inputPath,
        string outputPath)
    {
        string outputIdentity = ApiAcceptanceSupport.PathIdentity(outputPath);
        HashSet<string> inputs = [ApiAcceptanceSupport.PathIdentity(inputPath)];
        foreach (string name in new[] { "manifestPath", "imageManifestPath" })
        {
            if (manifest.Root["deploymentEvidence"]?[name] is not JsonValue value
                || !value.TryGetValue<string>(out string? proofPath))
                continue;
            inputs.Add(ApiAcceptanceSupport.PathIdentity(Path.GetFullPath(Path.IsPathRooted(proofPath)
                ? proofPath
                : Path.Combine(manifest.ManifestDirectory, proofPath))));
        }
        foreach (string? probePath in new[] { options.NativeProbeEnvelopePath, options.DispatchRecoveryEnvelopePath })
        {
            if (!string.IsNullOrWhiteSpace(probePath))
                inputs.Add(ApiAcceptanceSupport.PathIdentity(Path.GetFullPath(probePath)));
        }
        return inputs.Contains(outputIdentity);
    }

    private static async Task<string> ReserveInvocationAsync(string outputPath, string planHash, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        string invocationId = Guid.NewGuid().ToString("D");
        JsonObject record = ApiAcceptanceSupport.SetupInvocation(invocationId, planHash);
        try
        {
            await using FileStream stream = new(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            SetPrivatePermissions(outputPath);
            byte[] bytes = Encoding.UTF8.GetBytes(ApiAcceptanceSupport.EvidenceJson(ApiAcceptanceSupport.Redact(record)) + "\n");
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            return invocationId;
        }
        catch (IOException) when (File.Exists(outputPath))
        {
            string[] lines;
            try
            {
                lines = (await File.ReadAllTextAsync(outputPath, ct).ConfigureAwait(false))
                    .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
            }
            catch
            {
                throw new InvalidDataException("The existing P1 evidence output cannot be resumed safely.");
            }

            JsonObject[] records;
            try
            {
                records = lines.Select(line => JsonNode.Parse(line) as JsonObject
                    ?? throw new InvalidDataException()).ToArray();
            }
            catch
            {
                throw new InvalidDataException("The existing P1 evidence output is not valid JSON Lines.");
            }
            JsonObject[] intents = records.Where(item => ApiAcceptanceSupport.GetString(item, "kind") == "p1-setup-invocation").ToArray();
            string? existingId = intents.Length == 1 ? ApiAcceptanceSupport.GetString(intents[0], "invocationId") : null;
            // Completed output is terminal even after cleanup failure; a new invocation would abandon reconciliation.
            if (existingId is null || !ApiAcceptanceSupport.IsUuid(existingId)
                || records.Any(item => ApiAcceptanceSupport.GetString(item, "kind") == "p1-acceptance-result"))
                throw new InvalidDataException("The existing P1 evidence output is not an unfinished invocation that can be resumed.");
            if (records.Any(item => ApiAcceptanceSupport.GetString(item, "kind") == "p1-ui-action-started"))
                throw new InvalidDataException("The existing P1 evidence output may contain an attempted UI write; reconcile its owner state before taking further action.");
            if (ApiAcceptanceSupport.TryLong(intents[0]["schemaVersion"], out long version) && version != 2
                || ApiAcceptanceSupport.GetString(intents[0], "surface") != "api"
                || ApiAcceptanceSupport.GetString(intents[0], "executionPlanSha256") != planHash)
                throw new InvalidDataException("The existing P1 invocation is bound to a different validated execution plan or surface.");
            return existingId;
        }
    }

    private static async Task AppendResultAsync(string path, JsonObject result, CancellationToken ct)
    {
        JsonArray probes = result["probes"] as JsonArray ?? [];
        string fullStatus = ApiAcceptanceSupport.GetString(result, "fullP1Status") ?? "failed";
        foreach (JsonObject probe in probes.OfType<JsonObject>())
        {
            JsonObject disposition = ApiAcceptanceSupport.CloneObject(probe);
            disposition["kind"] = "p1-probe-disposition";
            disposition["surface"] = "api";
            disposition["fullP1Status"] = fullStatus;
            await ApiAcceptanceSupport.AppendEvidenceAsync(path, disposition, ct).ConfigureAwait(false);
        }
        await ApiAcceptanceSupport.AppendEvidenceAsync(path, result, ct).ConfigureAwait(false);
    }

    private static void SetPrivatePermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static int TryGetExitCode(JsonObject result)
    {
        if (result["exitCode"] is JsonValue value &&
            value.TryGetValue<int>(out int code) && code is >= 0 and <= 2)
            return code;
        return ApiAcceptanceSupport.TryLong(result["exitCode"], out long longCode) && longCode is >= 0 and <= 2
            ? (int)longCode
            : 1;
    }

    private static string AppendFailure(string? failure, Exception error)
    {
        string safe = ApiAcceptanceSupport.RedactText(error.Message);
        return string.IsNullOrWhiteSpace(failure) ? safe : $"{failure}; {safe}";
    }
}

internal interface IAcceptanceDeploymentEvidenceProvider
{
    Task<JsonObject> PrepareAsync(AcceptanceManifest manifest, CancellationToken ct);
}
