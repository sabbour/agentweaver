using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentControlSpecification;
using Agentweaver.Abstractions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Agentweaver.Policy.Acs;

public sealed class NativeAcsArtifactValidator
{
    private const int MaximumManifestBytes = 65_536;
    private const int MaximumManifestDepth = 64;
    private const int MaximumFileCount = 64;
    private const int MaximumFileBytes = 262_144;
    private const int MaximumArtifactBytes = 1_048_576;
    private const int MaximumDiagnostics = 32;
    private const int MaximumDiagnosticMessageLength = 2048;
    private const int MaximumOpaAstCharacters = 4_194_304;
    private const int MaximumOpaErrorCharacters = 65_536;
    private const int MaximumOpaParseMilliseconds = 5000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> AllowedInputPaths = new(StringComparer.Ordinal)
    {
        "input.policy_target.value.tenant_id",
        "input.policy_target.value.project_id",
        "input.policy_target.value.run_id",
        "input.policy_target.value.session_id",
        "input.policy_target.value.agent_id",
        "input.policy_target.value.action_id",
        "input.policy_target.value.human_available",
        "input.policy_target.value.human_oid",
        "input.policy_target.value.human_tenant_id"
    };
    private static readonly HashSet<string> AllowedNetworkInputPaths = new(StringComparer.Ordinal)
    {
        "input.policy_target.value.network.check_kind",
        "input.policy_target.value.network.protocol",
        "input.policy_target.value.network.port",
        "input.policy_target.value.network.destination.kind",
        "input.policy_target.value.network.destination.value"
    };
    private static readonly HashSet<string> UnsafeBuiltins = new(StringComparer.Ordinal)
    {
        "http.send",
        "net.lookup_ip_addr",
        "opa.runtime",
        "print",
        "rand.intn",
        "rand.uniform_int",
        "time.now_ns",
        "trace",
        "uuid.rfc4122"
    };

    private readonly string _opaPath;

    public NativeAcsArtifactValidator(string opaPath)
    {
        if (string.IsNullOrWhiteSpace(opaPath) || !Path.IsPathFullyQualified(opaPath))
            throw new ArgumentException("A full path to the pinned OPA executable is required.", nameof(opaPath));

        _opaPath = Path.GetFullPath(opaPath);
    }

    internal string OpaPath => _opaPath;

    public NativeAcsValidationResult Validate(NativeAcsArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var diagnostics = ImmutableArray.CreateBuilder<NativeAcsDiagnostic>();
        if (artifact.ManifestBytes.IsDefaultOrEmpty || artifact.ManifestBytes.Length > MaximumManifestBytes)
            Add(diagnostics, "manifest_size", "The ACS manifest is empty or exceeds its byte limit.");
        if (artifact.Files.IsDefaultOrEmpty)
            Add(diagnostics, "file_count", "The ACS artifact has no files or exceeds its file limit.");
        var fileCount = artifact.Files.IsDefault ? 0 : artifact.Files.Length;
        if (fileCount > MaximumFileCount)
        {
            Add(diagnostics, "file_count", "The ACS artifact has no files or exceeds its file limit.");
            return Result(false, diagnostics);
        }

        var totalBytes = (long)(artifact.ManifestBytes.IsDefault ? 0 : artifact.ManifestBytes.Length);
        var withinByteBounds = totalBytes <= MaximumManifestBytes;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in artifact.Files.IsDefault ? [] : artifact.Files)
        {
            if (file is null)
            {
                Add(diagnostics, "artifact_file", "The ACS artifact contains an empty file entry.");
                continue;
            }

            if (string.IsNullOrEmpty(file.RelativePath) ||
                !IsCanonicalRelativePath(file.RelativePath) ||
                string.Equals(file.RelativePath, "manifest.yaml", StringComparison.Ordinal))
                Add(diagnostics, "file_path", "An ACS artifact file path is not canonical and relative.");
            else if (!paths.Add(file.RelativePath))
                Add(diagnostics, "duplicate_path", "The ACS artifact contains a duplicate file path.", file.RelativePath);

            if (file.Bytes.IsDefaultOrEmpty || file.Bytes.Length > MaximumFileBytes)
            {
                Add(diagnostics, "file_size", "An ACS artifact file is empty or exceeds its byte limit.", file.RelativePath);
                if (!file.Bytes.IsDefault && file.Bytes.Length > MaximumFileBytes)
                    withinByteBounds = false;
            }

            totalBytes += file.Bytes.IsDefault ? 0 : file.Bytes.Length;
        }

        foreach (var path in paths)
        {
            var segments = path.Split('/');
            for (var index = 1; index < segments.Length; index++)
            {
                var parent = string.Join('/', segments.Take(index));
                if (paths.Contains(parent))
                    Add(diagnostics, "path_conflict", "An ACS artifact file path conflicts with a directory.", path);
            }
        }

        if (totalBytes > MaximumArtifactBytes)
        {
            Add(diagnostics, "artifact_size", "The ACS artifact exceeds its total byte limit.");
            withinByteBounds = false;
        }
        if (!withinByteBounds)
            return Result(false, diagnostics);

        string? computedHash;
        try
        {
            computedHash = ComputeContentSha256(artifact);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            computedHash = null;
        }

        var hashMatches = false;
        if (artifact.ContentSha256 is { } contentHash &&
            IsSha256(contentHash) &&
            computedHash is { } actualHash)
        {
            hashMatches = CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(contentHash),
                Convert.FromHexString(actualHash));
        }
        if (!hashMatches)
            Add(diagnostics, "artifact_hash", "The ACS artifact hash does not match all supplied bytes.");

        if (diagnostics.Count > 0)
            return Result(false, diagnostics);
        if (!File.Exists(_opaPath))
            return Result(false, diagnostics, NativeAcsValidationDependencyFailure.OpaUnavailable);

        var isNetworkArtifact = NativeAcsFiniteNetworkProfile.IsCandidate(artifact);
        string manifest;
        Dictionary<string, string> regoModules;
        var definedRulePaths = new HashSet<string>(StringComparer.Ordinal);
        var opaParseStopwatch = Stopwatch.StartNew();
        try
        {
            manifest = StrictUtf8.GetString(artifact.ManifestBytes.AsSpan());
            if (ContainsExternalUrl(manifest))
                Add(diagnostics, "external_url", "The ACS manifest cannot reference an external URL.");

            regoModules = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in artifact.Files)
            {
                var text = StrictUtf8.GetString(file.Bytes.AsSpan());
                if (file.RelativePath.EndsWith(".rego", StringComparison.Ordinal))
                {
                    regoModules.Add(file.RelativePath, text);
                    var remainingMilliseconds = MaximumOpaParseMilliseconds -
                        (int)opaParseStopwatch.ElapsedMilliseconds;
                    // A timeout does not prove that the policy is invalid.
                    if (remainingMilliseconds <= 0)
                        return Result(false, diagnostics, NativeAcsValidationDependencyFailure.OpaUnavailable);

                    var dependencyFailure = ValidateInputFacts(
                        file.RelativePath,
                        text,
                        diagnostics,
                        definedRulePaths,
                        remainingMilliseconds,
                        isNetworkArtifact);
                    if (dependencyFailure is not null)
                        return Result(false, diagnostics, dependencyFailure);
                }
                else if (file.RelativePath.EndsWith(".json", StringComparison.Ordinal))
                {
                    using var _ = JsonDocument.Parse(
                        file.Bytes.ToArray(),
                        new JsonDocumentOptions { MaxDepth = 32 });
                }
                else
                {
                    Add(diagnostics, "file_type", "Only Rego modules and JSON data files are supported.", file.RelativePath);
                }
            }
        }
        catch (DecoderFallbackException)
        {
            Add(diagnostics, "file_encoding", "ACS manifests, modules, and data must use valid UTF-8.");
            return Result(false, diagnostics);
        }
        catch (JsonException)
        {
            Add(diagnostics, "data_json", "ACS data files must contain valid JSON.");
            return Result(false, diagnostics);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Add(diagnostics, "artifact_read", "The ACS artifact could not be read.");
            return Result(false, diagnostics);
        }

        if (NativeAcsFiniteNetworkProfile.IsCandidate(artifact))
        {
            var networkProfile = NativeAcsFiniteNetworkProfile.Validate(artifact);
            foreach (var diagnostic in networkProfile.Diagnostics)
                Add(diagnostics, diagnostic.Code, diagnostic.Message, diagnostic.RelativePath);
        }

        ValidateManifest(
            manifest,
            paths,
            definedRulePaths,
            diagnostics);
        if (diagnostics.Count > 0)
            return Result(false, diagnostics);

        try
        {
            var nativeResult = ArtifactValidator.Validate(manifest, regoModules, _opaPath);
            NativeAcsValidationDependencyFailure? nativeDependencyFailure = null;
            foreach (var diagnostic in nativeResult.Diagnostics)
            {
                if (ClassifyNativeDependencyFailure(diagnostic.Code) is { } dependencyFailure)
                {
                    nativeDependencyFailure ??= dependencyFailure;
                    continue;
                }

                Add(
                    diagnostics,
                    diagnostic.Code,
                    diagnostic.Message,
                    diagnostic.Path is { } path && IsCanonicalRelativePath(path) ? path : null,
                    ToInt32(diagnostic.Line),
                    ToInt32(diagnostic.Column));
            }

            // A process, timeout, or staging failure does not prove that the artifact is invalid.
            if (nativeDependencyFailure is { } failure)
                return Result(false, diagnostics, failure);
            if (!nativeResult.Valid && diagnostics.Count == 0)
                Add(diagnostics, "native_validation", "The native ACS validator rejected the artifact.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Result(
                false,
                diagnostics,
                NativeAcsValidationDependencyFailure.NativeValidatorUnavailable);
        }

        return Result(diagnostics.Count == 0, diagnostics);
    }

    public static string ComputeContentSha256(NativeAcsArtifact artifact)
        => NativeAcsArtifactHash.Compute(artifact);

    // Codes from AgentControlSpecification 0.4.0-beta.0 artifact validation that report dependency state.
    internal static NativeAcsValidationDependencyFailure? ClassifyNativeDependencyFailure(string? code) =>
        code switch
        {
            "opa_timeout" or "opa_invalid_executable" or "opa_execution_error" =>
                NativeAcsValidationDependencyFailure.OpaUnavailable,
            "rego_staging_error" or "validation_internal_error" =>
                NativeAcsValidationDependencyFailure.NativeValidatorUnavailable,
            _ => null,
        };

    private NativeAcsValidationDependencyFailure? ValidateInputFacts(
        string relativePath,
        string module,
        ImmutableArray<NativeAcsDiagnostic>.Builder diagnostics,
        HashSet<string> definedRulePaths,
        int timeoutMilliseconds,
        bool allowNetworkFacts)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _opaPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("parse");
        startInfo.ArgumentList.Add("--format=json");
        startInfo.ArgumentList.Add("/dev/stdin");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return NativeAcsValidationDependencyFailure.OpaUnavailable;

            var outputTask = ReadBoundedAsync(process.StandardOutput, MaximumOpaAstCharacters);
            var errorTask = ReadBoundedAsync(process.StandardError, MaximumOpaErrorCharacters);
            process.StandardInput.Write(module);
            process.StandardInput.Close();

            var stopwatch = Stopwatch.StartNew();
            while (!process.HasExited &&
                   !outputTask.IsCompleted &&
                   stopwatch.ElapsedMilliseconds < timeoutMilliseconds)
                Thread.Sleep(10);

            if (outputTask.IsCompleted && outputTask.GetAwaiter().GetResult().Exceeded)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                process.WaitForExit();
                Add(diagnostics, "opa_output_limit", "OPA exceeded the Rego parse output limit.", relativePath);
                return null;
            }

            var remainingMilliseconds = Math.Max(0, timeoutMilliseconds - (int)stopwatch.ElapsedMilliseconds);
            if (!process.HasExited && !process.WaitForExit(remainingMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return NativeAcsValidationDependencyFailure.OpaUnavailable;
            }

            Task.WaitAll(outputTask, errorTask);
            var (output, exceededLimit) = outputTask.Result;
            if (exceededLimit)
            {
                Add(diagnostics, "opa_output_limit", "OPA exceeded the Rego parse output limit.", relativePath);
                return null;
            }

            if (process.ExitCode != 0)
            {
                // Only a structured OPA parse error proves that the module is invalid.
                var (error, errorExceeded) = errorTask.Result;
                if (process.ExitCode != 1 || errorExceeded || !IsRegoParseError(error))
                    return NativeAcsValidationDependencyFailure.OpaUnavailable;
                Add(diagnostics, "opa_parse", "OPA could not parse the Rego module.", relativePath);
                return null;
            }

            using var document = JsonDocument.Parse(output);
            ValidateAstInputFacts(document.RootElement, relativePath, diagnostics, allowNetworkFacts);
            CollectRulePaths(document.RootElement, definedRulePaths);
            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return NativeAcsValidationDependencyFailure.OpaUnavailable;
        }
    }

    private static void CollectRulePaths(JsonElement module, HashSet<string> definedRulePaths)
    {
        if (!module.TryGetProperty("package", out var package) ||
            !package.TryGetProperty("path", out var packagePath) ||
            !TryReadDataPath(packagePath, out var packageName) ||
            !module.TryGetProperty("rules", out var rules) ||
            rules.ValueKind != JsonValueKind.Array)
            return;

        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.TryGetProperty("head", out var head) &&
                head.TryGetProperty("name", out var name) &&
                name.ValueKind == JsonValueKind.String)
                definedRulePaths.Add($"{packageName}.{name.GetString()}");
        }
    }

    private static bool TryReadDataPath(JsonElement path, out string dataPath)
    {
        dataPath = string.Empty;
        if (path.ValueKind != JsonValueKind.Array)
            return false;

        var segments = path.EnumerateArray().ToArray();
        if (segments.Length < 2 ||
            !segments[0].TryGetProperty("type", out var rootType) ||
            rootType.ValueKind != JsonValueKind.String ||
            rootType.GetString() != "var" ||
            !segments[0].TryGetProperty("value", out var rootValue) ||
            rootValue.ValueKind != JsonValueKind.String ||
            rootValue.GetString() != "data")
            return false;

        var names = new List<string>(segments.Length - 1);
        for (var index = 1; index < segments.Length; index++)
        {
            if (!segments[index].TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String ||
                type.GetString() != "string" ||
                !segments[index].TryGetProperty("value", out var value) ||
                value.ValueKind != JsonValueKind.String)
                return false;
            names.Add(value.GetString()!);
        }

        dataPath = $"data.{string.Join('.', names)}";
        return true;
    }

    private static void ValidateManifest(
        string manifest,
        HashSet<string> artifactPaths,
        HashSet<string> definedRulePaths,
        ImmutableArray<NativeAcsDiagnostic>.Builder diagnostics)
    {
        YamlStream yaml;
        try
        {
            yaml = new YamlStream();
            using var reader = new StringReader(manifest);
            yaml.Load(reader);
        }
        catch (YamlException)
        {
            Add(diagnostics, "manifest_yaml", "The ACS manifest is not valid YAML.");
            return;
        }

        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            Add(diagnostics, "manifest_shape", "The ACS manifest must contain one mapping document.");
            return;
        }

        var queryCount = 0;
        var bundlePaths = new HashSet<string>(StringComparer.Ordinal);
        VisitManifestNode(
            root,
            artifactPaths,
            definedRulePaths,
            bundlePaths,
            diagnostics,
            new HashSet<YamlNode>(ReferenceEqualityComparer.Instance),
            0,
            ref queryCount);

        if (queryCount == 0)
            Add(diagnostics, "unknown_rule", "The ACS manifest has no supported Rego rule query.");

        foreach (var bundlePath in bundlePaths)
        {
            if (!artifactPaths.Any(path => path.StartsWith($"{bundlePath}/", StringComparison.Ordinal)))
                Add(diagnostics, "external_path", "The ACS bundle does not contain supplied artifact files.");
        }

        foreach (var artifactPath in artifactPaths)
        {
            if (!bundlePaths.Any(bundlePath =>
                    artifactPath.StartsWith($"{bundlePath}/", StringComparison.Ordinal)))
                Add(diagnostics, "external_path", "An ACS artifact file is outside every declared bundle.", artifactPath);
        }
    }

    private static void VisitManifestNode(
        YamlNode node,
        HashSet<string> artifactPaths,
        HashSet<string> definedRulePaths,
        HashSet<string> bundlePaths,
        ImmutableArray<NativeAcsDiagnostic>.Builder diagnostics,
        HashSet<YamlNode> activeNodes,
        int depth,
        ref int queryCount)
    {
        if (depth > MaximumManifestDepth)
        {
            Add(diagnostics, "manifest_depth", "The ACS manifest exceeds its nesting limit.");
            return;
        }
        if (!activeNodes.Add(node))
        {
            Add(diagnostics, "manifest_cycle", "The ACS manifest contains a cyclic YAML alias.");
            return;
        }

        try
        {
            if (node is YamlMappingNode mapping)
            {
                foreach (var (keyNode, valueNode) in mapping.Children)
                {
                    if (keyNode is not YamlScalarNode { Value: { } key })
                    {
                        Add(diagnostics, "manifest_shape", "The ACS manifest must use scalar keys.");
                        continue;
                    }

                    if (key is "extends" or "extend" or "include")
                    {
                        Add(diagnostics, "external_path", "The ACS manifest cannot extend another artifact.");
                        continue;
                    }
                    if (key is "transform" or "transforms")
                    {
                        Add(diagnostics, "unsupported_transform", "The ACS manifest cannot declare transforms.");
                        continue;
                    }

                    if (key == "bundle")
                        ValidateBundlePath(valueNode, artifactPaths, bundlePaths, diagnostics);
                    else if (key == "data_paths" || key == "data_path")
                        ValidateDataPaths(valueNode, artifactPaths, diagnostics);
                    else if (key == "query")
                    {
                        queryCount++;
                        if (valueNode is not YamlScalarNode { Value: { } query } ||
                            !definedRulePaths.Contains(query))
                            Add(diagnostics, "unknown_rule", "The ACS manifest query does not name a supplied Rego rule.");
                    }

                    VisitManifestNode(
                        valueNode,
                        artifactPaths,
                        definedRulePaths,
                        bundlePaths,
                        diagnostics,
                        activeNodes,
                        depth + 1,
                        ref queryCount);
                }
            }
            else if (node is YamlSequenceNode sequence)
            {
                foreach (var item in sequence.Children)
                    VisitManifestNode(
                        item,
                        artifactPaths,
                        definedRulePaths,
                        bundlePaths,
                        diagnostics,
                        activeNodes,
                        depth + 1,
                        ref queryCount);
            }
            else if (node is YamlScalarNode { Value: { } value } && IsExternalPath(value))
            {
                Add(diagnostics, "external_path", "The ACS manifest cannot reference an absolute or parent path.");
            }
        }
        finally
        {
            activeNodes.Remove(node);
        }
    }

    private static void ValidateBundlePath(
        YamlNode node,
        HashSet<string> artifactPaths,
        HashSet<string> bundlePaths,
        ImmutableArray<NativeAcsDiagnostic>.Builder diagnostics)
    {
        if (node is not YamlScalarNode { Value: { } value } ||
            !TryNormalizeRelativePath(value, out var path) ||
            !artifactPaths.Any(file => file.StartsWith($"{path}/", StringComparison.Ordinal)))
        {
            Add(diagnostics, "external_path", "The ACS manifest must use a relative bundle with supplied files.");
            return;
        }

        bundlePaths.Add(path);
    }

    private static void ValidateDataPaths(
        YamlNode node,
        HashSet<string> artifactPaths,
        ImmutableArray<NativeAcsDiagnostic>.Builder diagnostics)
    {
        if (node is YamlSequenceNode sequence)
        {
            foreach (var item in sequence.Children)
                ValidateDataPath(item, artifactPaths, diagnostics);
            return;
        }

        ValidateDataPath(node, artifactPaths, diagnostics);
    }

    private static void ValidateDataPath(
        YamlNode node,
        HashSet<string> artifactPaths,
        ImmutableArray<NativeAcsDiagnostic>.Builder diagnostics)
    {
        if (node is not YamlScalarNode { Value: { } value } ||
            !TryNormalizeRelativePath(value, out var path) ||
            !artifactPaths.Any(file =>
                string.Equals(file, path, StringComparison.Ordinal) ||
                file.StartsWith($"{path}/", StringComparison.Ordinal)))
            Add(diagnostics, "external_path", "ACS data paths must name supplied relative files or directories.");
    }

    private static bool TryNormalizeRelativePath(string value, out string path)
    {
        path = value.StartsWith("./", StringComparison.Ordinal) ? value[2..] : value;
        return IsCanonicalRelativePath(path);
    }

    private static bool IsExternalPath(string value) =>
        value.StartsWith("/", StringComparison.Ordinal) ||
        value.StartsWith('\\') ||
        value.Contains("://", StringComparison.Ordinal) ||
        (value.Length >= 3 &&
         char.IsAsciiLetter(value[0]) &&
         value[1] == ':' &&
         value[2] is '/' or '\\') ||
        value.Replace('\\', '/').Split('/').Any(segment => segment == "..");

    private static async Task<(string Text, bool Exceeded)> ReadBoundedAsync(
        TextReader reader,
        int maximumCharacters)
    {
        var builder = new StringBuilder(Math.Min(maximumCharacters, 8192));
        var buffer = new char[8192];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (count == 0)
                return (builder.ToString(), false);

            if (builder.Length + count > maximumCharacters)
            {
                var remaining = maximumCharacters - builder.Length;
                if (remaining > 0)
                    builder.Append(buffer, 0, remaining);
                return (builder.ToString(), true);
            }

            builder.Append(buffer, 0, count);
        }
    }

    private static bool IsRegoParseError(string error)
    {
        try
        {
            using var document = JsonDocument.Parse(error);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Array &&
                errors.GetArrayLength() > 0 &&
                errors.EnumerateArray().All(item =>
                    item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("code", out var code) &&
                    code.ValueKind == JsonValueKind.String &&
                    code.GetString() == "rego_parse_error");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ValidateAstInputFacts(
        JsonElement element,
        string relativePath,
        ImmutableArray<NativeAcsDiagnostic>.Builder diagnostics,
        bool allowNetworkFacts)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String &&
                type.GetString() == "ref" &&
                element.TryGetProperty("value", out var reference) &&
                reference.ValueKind == JsonValueKind.Array)
            {
                var segments = reference.EnumerateArray().ToArray();
                if (segments.Length > 0 &&
                    segments[0].TryGetProperty("type", out var rootType) &&
                    rootType.GetString() == "var" &&
                    segments[0].TryGetProperty("value", out var rootValue) &&
                    rootValue.GetString() == "input")
                {
                    var inputPath = TryReadReferencePath(element, out var parsedPath) &&
                        (parsedPath == "input" || parsedPath.StartsWith("input.", StringComparison.Ordinal))
                            ? parsedPath
                            : null;
                    if (inputPath is null ||
                        (!AllowedInputPaths.Contains(inputPath) &&
                         !(allowNetworkFacts && AllowedNetworkInputPaths.Contains(inputPath))))
                        Add(diagnostics, "unknown_fact", "The Rego module reads an unsupported input fact.", relativePath);
                }

                if (TryReadReferencePath(element, out var referencePath) && IsUnsafeBuiltinPath(referencePath))
                    Add(diagnostics, "unsafe_builtin", "The Rego module references an unsupported built-in.", relativePath);

                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("value") && property.Value.ValueKind == JsonValueKind.Array)
                    {
                        var valueSegments = property.Value.EnumerateArray().ToArray();
                        var firstIsRootVariable = valueSegments.Length > 0 &&
                            valueSegments[0].ValueKind == JsonValueKind.Object &&
                            valueSegments[0].TryGetProperty("type", out var firstType) &&
                            firstType.ValueKind == JsonValueKind.String &&
                            firstType.GetString() == "var";
                        foreach (var segment in valueSegments.Skip(firstIsRootVariable ? 1 : 0))
                            ValidateAstInputFacts(segment, relativePath, diagnostics, allowNetworkFacts);
                    }
                    else
                    {
                        ValidateAstInputFacts(property.Value, relativePath, diagnostics, allowNetworkFacts);
                    }
                }

                return;
            }

            if (element.TryGetProperty("type", out type) &&
                type.ValueKind == JsonValueKind.String &&
                type.GetString() == "call" &&
                element.TryGetProperty("value", out var call) &&
                call.ValueKind == JsonValueKind.Array &&
                call.GetArrayLength() > 0)
            {
                var name = TryReadReferencePath(call[0], out var function) ? function : null;
                if (name is not null && UnsafeBuiltins.Contains(name))
                    Add(diagnostics, "unsafe_builtin", "The Rego module calls an unsupported built-in.", relativePath);
            }

            if (element.TryGetProperty("type", out type) &&
                type.ValueKind == JsonValueKind.String &&
                type.GetString() == "var" &&
                element.TryGetProperty("value", out var variable) &&
                variable.ValueKind == JsonValueKind.String &&
                variable.GetString() == "input")
            {
                Add(diagnostics, "dynamic_fact", "The Rego module uses an unbounded input reference.", relativePath);
                return;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("imports") && property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var import in property.Value.EnumerateArray())
                    {
                        if (import.TryGetProperty("path", out var importPath) &&
                            TryReadReferencePath(importPath, out var importedPath))
                        {
                            if (importedPath == "input" ||
                                importedPath.StartsWith("input.", StringComparison.Ordinal))
                                Add(diagnostics, "input_import", "The Rego module cannot import input facts.", relativePath);
                            else if (IsUnsafeBuiltinPath(importedPath))
                                Add(diagnostics, "unsafe_builtin", "The Rego module cannot import an unsupported built-in.", relativePath);
                        }
                    }
                }

                ValidateAstInputFacts(property.Value, relativePath, diagnostics, allowNetworkFacts);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                ValidateAstInputFacts(item, relativePath, diagnostics, allowNetworkFacts);
        }
    }

    private static bool IsUnsafeBuiltinPath(string path) =>
        UnsafeBuiltins.Contains(path) ||
        path == "http" ||
        path.StartsWith("http.", StringComparison.Ordinal) ||
        path == "rand" ||
        path.StartsWith("rand.", StringComparison.Ordinal) ||
        path == "uuid" ||
        path.StartsWith("uuid.", StringComparison.Ordinal) ||
        path == "time.now_ns" ||
        path.StartsWith("time.now_ns.", StringComparison.Ordinal) ||
        path == "opa.runtime" ||
        path.StartsWith("opa.runtime.", StringComparison.Ordinal) ||
        path == "net.lookup_ip_addr";

    private static bool TryReadReferencePath(JsonElement reference, out string path)
    {
        path = string.Empty;
        if (reference.ValueKind != JsonValueKind.Object ||
            !reference.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String ||
            type.GetString() != "ref" ||
            !reference.TryGetProperty("value", out var value) ||
            value.ValueKind != JsonValueKind.Array)
            return false;

        var segments = value.EnumerateArray().ToArray();
        if (segments.Length == 0 ||
            !segments[0].TryGetProperty("type", out var rootType) ||
            rootType.ValueKind != JsonValueKind.String ||
            rootType.GetString() != "var" ||
            !segments[0].TryGetProperty("value", out var rootValue) ||
            rootValue.ValueKind != JsonValueKind.String)
            return false;

        var names = new List<string>(segments.Length) { rootValue.GetString()! };
        for (var index = 1; index < segments.Length; index++)
        {
            if (!segments[index].TryGetProperty("type", out var segmentType) ||
                segmentType.ValueKind != JsonValueKind.String ||
                segmentType.GetString() != "string" ||
                !segments[index].TryGetProperty("value", out var segmentValue) ||
                segmentValue.ValueKind != JsonValueKind.String)
                return false;
            names.Add(segmentValue.GetString()!);
        }

        path = string.Join('.', names);
        return true;
    }

    private static bool IsCanonicalRelativePath(string path)
    {
        if (path.Length > 256 || path.StartsWith('/') || path.Contains('\\') || path.Contains(':') ||
            path.Any(char.IsControl))
            return false;

        return path.Split('/').All(segment =>
            segment.Length > 0 &&
            segment is not ("." or "..") &&
            segment.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'));
    }

    private static bool ContainsExternalUrl(string manifest) =>
        manifest.Contains("://", StringComparison.Ordinal);

    private static bool IsSha256(string value)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            return false;

        return true;
    }

    private static int? ToInt32(ulong? value) =>
        value is { } number && number <= int.MaxValue
            ? checked((int)number)
            : null;

    private static void Add(
        ImmutableArray<NativeAcsDiagnostic>.Builder diagnostics,
        string code,
        string message,
        string? path = null,
        int? line = null,
        int? column = null)
    {
        if (diagnostics.Count >= MaximumDiagnostics)
            return;

        diagnostics.Add(new NativeAcsDiagnostic(
            code.Length <= 128 ? code : code[..128],
            message.Length <= MaximumDiagnosticMessageLength
                ? message
                : message[..MaximumDiagnosticMessageLength],
            path,
            line,
            column));
    }

    private static NativeAcsValidationResult Result(
        bool valid,
        ImmutableArray<NativeAcsDiagnostic>.Builder diagnostics,
        NativeAcsValidationDependencyFailure? dependencyFailure = null) =>
        new(valid && dependencyFailure is null, diagnostics.ToImmutable(), dependencyFailure);
}
