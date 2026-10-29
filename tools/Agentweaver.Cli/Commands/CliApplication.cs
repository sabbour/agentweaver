using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Cli.Api;

namespace Agentweaver.Cli;

public static class CliApplication
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 32,
    };

    public static async Task<int> RunAsync(
        string[] args,
        CliRuntimeOptions? runtime = null,
        CancellationToken ct = default)
    {
        var output = runtime?.Output ?? Console.Out;
        var error = runtime?.Error ?? Console.Error;
        if (args.Length == 0 || args.Any(IsHelp))
        {
            await output.WriteLineAsync(HelpText).ConfigureAwait(false);
            return 0;
        }
        if (args.Length == 1 && args[0] == "--version")
        {
            await output.WriteLineAsync(VersionText()).ConfigureAwait(false);
            return 0;
        }

        try
        {
            if (args[0] == "api")
                return await RunApiAsync(
                    args[1..],
                    runtime ?? new CliRuntimeOptions(),
                    output,
                    error,
                    ct).ConfigureAwait(false);
            if (args[0] == "auth")
                return await RunAuthAsync(args[1..], runtime ?? new CliRuntimeOptions(), output, ct).ConfigureAwait(false);
            if (args[0] == "test")
                return await RunTestAsync(args[1..], runtime ?? new CliRuntimeOptions(), output, ct).ConfigureAwait(false);
            throw new CliUsageException($"Unknown command: {args[0]}");
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Operation cancelled.").ConfigureAwait(false);
            return 130;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            await error.WriteLineAsync(GatewayClient.RedactText(exception.Message)).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<int> RunApiAsync(
        string[] args,
        CliRuntimeOptions runtime,
        TextWriter output,
        TextWriter error,
        CancellationToken ct)
    {
        if (args.Length == 0)
            throw new CliUsageException("Choose `operations`, `describe`, or `invoke`.");

        var command = args[0];
        if (command is not ("operations" or "describe" or "invoke"))
            throw new CliUsageException($"Unknown API command: {command}");
        var operationId = command is "describe" or "invoke"
            ? args.Length > 1 ? args[1] : throw new CliUsageException($"Usage: agentweaver api {command} <operationId>")
            : null;
        var optionStart = operationId is null ? 1 : 2;
        var parsed = CliOptions.Parse(args[optionStart..], new HashSet<string>(StringComparer.Ordinal));
        var scopes = command == "invoke" ? parsed.Many("scope") : [];
        if (command == "invoke")
            parsed.RequireOnly(
                new[] { "path", "query", "header", "body", "output", "scope" }
                    .Concat(CliConfiguration.TargetOptionNames)
                    .ToArray());
        else
            parsed.RequireOnly(CliConfiguration.TargetOptionNames);
        var identity = CliConfiguration.ReadIdentity(
            scopes,
            discoveryOnly: command is "operations" or "describe",
            options: parsed);
        using var ownedClient = runtime.HttpClient is null ? CreateSafeHttpClient() : null;
        var httpClient = runtime.HttpClient ?? ownedClient!;
        var factory = runtime.GatewayClientFactory ?? new GatewayClientFactory(httpClient, runtime.Authentication);
        var client = factory.Create(identity);

        var document = await client.DiscoverAsync(ct).ConfigureAwait(false);
        var operations = GatewayContract.Parse(document);
        if (command == "operations")
        {
            var listing = operations.Values
                .OrderBy(item => item.OperationId, StringComparer.Ordinal)
                .Select(item => new JsonObject
                {
                    ["operationId"] = item.OperationId,
                    ["method"] = item.Method,
                    ["path"] = item.Path,
                    ["summary"] = GatewayContract.String(item.Document, "summary"),
                    ["description"] = GatewayContract.String(item.Document, "description"),
                });
            await WriteJsonAsync(output, new JsonArray(listing.ToArray())).ConfigureAwait(false);
            return 0;
        }

        if (operationId is null || !operations.TryGetValue(operationId, out var operation))
            throw new CliUsageException($"Operation '{operationId}' is not present in the live Gateway contract.");
        if (command == "describe")
        {
            var description = (JsonObject)operation.Document.DeepClone();
            description["method"] = operation.Method;
            description["path"] = operation.Path;
            await WriteJsonAsync(output, description).ConfigureAwait(false);
            return 0;
        }
        if (command != "invoke")
            throw new CliUsageException($"Unknown API command: {command}");

        var path = ParsePairs(parsed.Many("path"), "--path", StringComparer.Ordinal);
        var query = ParsePairs(parsed.Many("query"), "--query", StringComparer.Ordinal);
        var headers = ParsePairs(parsed.Many("header"), "--header", StringComparer.OrdinalIgnoreCase);
        var bodyOption = parsed.Single("body");
        var bodySourcePath = bodyOption is { Length: > 1 } && bodyOption[0] == '@'
            ? Path.GetFullPath(bodyOption[1..])
            : null;
        var body = ReadBody(bodyOption);
        var outputPath = parsed.Single("output");
        var fullOutputPath = outputPath is null ? null : Path.GetFullPath(outputPath);
        if (fullOutputPath is not null &&
            (File.Exists(fullOutputPath) || Directory.Exists(fullOutputPath)))
            throw new IOException("Binary output must use a new file path; existing files are never overwritten.");
        if (fullOutputPath is not null && bodySourcePath is not null &&
            PathsEqual(fullOutputPath, bodySourcePath))
            throw new IOException("Binary output cannot overwrite the request-body input file.");
        if (fullOutputPath is null && operation.ResponseMediaTypes.Contains("application/octet-stream", StringComparer.Ordinal))
            throw new CliUsageException("This operation advertises binary output; specify a new path with --output.");

        var invocation = new GatewayInvocation(path, query, body, headers)
        {
            OnEventChunk = async chunk =>
            {
                await output.WriteAsync(chunk).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
            },
        };
        var response = await client.InvokeAsync(operationId, invocation, ct).ConfigureAwait(false);
        if (response.BinaryBody is { } binary)
        {
            if (fullOutputPath is null)
                throw new InvalidDataException("The Gateway returned binary content without an output path.");
            await WriteNewFileAsync(fullOutputPath, binary, ct).ConfigureAwait(false);
        }
        var result = new JsonObject
        {
            ["status"] = response.Status,
            ["headers"] = new JsonObject(response.Headers.ToDictionary(
                item => item.Key,
                item => (JsonNode?)JsonValue.Create(item.Value),
                StringComparer.OrdinalIgnoreCase)),
            ["evidence"] = response.Evidence,
        };
        if (response.BinaryBody is { } bytes)
        {
            result["output"] = fullOutputPath;
            result["bytes"] = bytes.Length;
        }
        else if (response.Evidence["contentType"]?.GetValue<string>() != "text/event-stream")
        {
            result["body"] = response.Evidence["responseBody"]?.DeepClone();
        }
        var eventStream = response.Evidence["contentType"]?.GetValue<string>() == "text/event-stream";
        await WriteJsonAsync(eventStream ? error : output, result).ConfigureAwait(false);
        return response.Status is >= 200 and < 300 ? 0 : 1;
    }

    private static async Task<int> RunAuthAsync(
        string[] args,
        CliRuntimeOptions runtime,
        TextWriter output,
        CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is not ("login" or "status" or "logout"))
            throw new CliUsageException("Usage: agentweaver auth login|status|logout [--scope <scope>]");
        if (runtime.Authentication is null)
            throw new InvalidOperationException("Authentication is not composed in this build.");

        var parsed = CliOptions.Parse(args[1..], new HashSet<string>(StringComparer.Ordinal));
        parsed.RequireOnly(new[] { "scope" }.Concat(CliConfiguration.TargetOptionNames).ToArray());
        var identity = CliConfiguration.ReadIdentity(parsed.Many("scope"), options: parsed);
        switch (args[0])
        {
            case "login":
                await WriteAuthStatusAsync(output, await runtime.Authentication.LoginAsync(identity, ct).ConfigureAwait(false))
                    .ConfigureAwait(false);
                return 0;
            case "status":
                await WriteAuthStatusAsync(output, await runtime.Authentication.StatusAsync(identity, ct).ConfigureAwait(false))
                    .ConfigureAwait(false);
                return 0;
            default:
                await runtime.Authentication.LogoutAsync(identity, ct).ConfigureAwait(false);
                await output.WriteLineAsync("Signed out of the selected local credential entry.").ConfigureAwait(false);
                return 0;
        }
    }

    private static async Task<int> RunTestAsync(
        string[] args,
        CliRuntimeOptions runtime,
        TextWriter output,
        CancellationToken ct)
    {
        var parsed = CliOptions.Parse(
            args,
            new HashSet<string>(["dry-run", "interactive"], StringComparer.Ordinal));
        parsed.RequireOnly("input", "output", "dry-run", "interactive", "native-probe-envelope", "dispatch-recovery-envelope");
        var input = parsed.Single("input") ?? throw new CliUsageException("`agentweaver test` requires --input <manifest>.");
        var evidence = parsed.Single("output") ?? throw new CliUsageException("`agentweaver test` requires --output <evidence.jsonl>.");
        if (runtime.AcceptanceRunner is null)
            throw new InvalidOperationException("The native API acceptance runner is not composed in this build.");
        var fullInput = Path.GetFullPath(input);
        var fullOutput = Path.GetFullPath(evidence);
        var proofPaths = new[]
        {
            parsed.Single("native-probe-envelope"),
            parsed.Single("dispatch-recovery-envelope"),
        }.OfType<string>().Select(path => Path.GetFullPath(path));
        if (PathsEqual(fullInput, fullOutput) ||
            proofPaths.Any(path => PathsEqual(path, fullOutput)))
        {
            throw new CliUsageException("The evidence output path cannot overwrite the manifest or a supplied proof artifact.");
        }
        var options = new ApiTestOptions(
            input,
            evidence,
            parsed.HasFlag("dry-run"),
            parsed.Single("native-probe-envelope"),
            parsed.Single("dispatch-recovery-envelope"),
            parsed.HasFlag("interactive") ? InteractionPolicy.Allow : InteractionPolicy.Never);
        return await runtime.AcceptanceRunner.RunAsync(options, ct).ConfigureAwait(false);
    }

    private static async Task WriteAuthStatusAsync(TextWriter writer, CliAuthStatus status)
    {
        var identity = status.Identity;
        await WriteJsonAsync(writer, new JsonObject
        {
            ["isSignedIn"] = status.IsSignedIn,
            ["actorId"] = status.ActorId,
            ["issuer"] = identity.Target.Issuer.GetLeftPart(UriPartial.Authority),
            ["clientId"] = identity.Target.ClientId,
            ["audience"] = identity.Target.Audience,
            ["gateway"] = identity.Target.Gateway.GetLeftPart(UriPartial.Authority),
            ["tenantId"] = identity.Target.TenantId,
            ["scopes"] = new JsonArray(identity.Scopes.Select(scope => (JsonNode?)JsonValue.Create(scope)).ToArray()),
            ["projectId"] = identity.ProjectId,
            ["runId"] = identity.RunId,
        }).ConfigureAwait(false);
    }

    private static JsonNode? ReadBody(string? body)
    {
        if (body is null)
            return null;
        byte[] bytes;
        if (body.StartsWith('@'))
        {
            var path = Path.GetFullPath(body[1..]);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 64L * 1024 * 1024)
                throw new InvalidDataException("The JSON request-body input exceeds the maximum supported size.");
            using var buffer = new MemoryStream((int)stream.Length);
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }
        else
        {
            bytes = Encoding.UTF8.GetBytes(body);
            if (bytes.Length > 64 * 1024 * 1024)
                throw new InvalidDataException("The JSON request body exceeds the maximum supported size.");
        }
        try
        {
            return JsonNode.Parse(bytes, documentOptions: new System.Text.Json.JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException exception)
        {
            throw new CliUsageException($"The request body is not valid JSON: {exception.Message}");
        }
    }

    private static Dictionary<string, string> ParsePairs(
        IReadOnlyList<string> values,
        string option,
        StringComparer comparer)
    {
        var result = new Dictionary<string, string>(comparer);
        foreach (var value in values)
        {
            var separator = value.IndexOf('=');
            if (separator <= 0)
                throw new CliUsageException($"{option} values must use name=value syntax.");
            var name = value[..separator].Trim();
            if (name.Length == 0 || !result.TryAdd(name, value[(separator + 1)..]))
                throw new CliUsageException($"{option} contains an empty or duplicate parameter name.");
        }
        return result;
    }

    private static async Task WriteNewFileAsync(string path, byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        try
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            stream.Dispose();
            File.Delete(path);
            throw;
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static async Task WriteJsonAsync(TextWriter writer, JsonNode node)
    {
        await writer.WriteLineAsync(node.ToJsonString(OutputJson)).ConfigureAwait(false);
    }

    private static bool IsHelp(string argument) => argument is "--help" or "-h";

    private static string VersionText()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "1.0.0";
        string Metadata(string key) =>
            assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(item => item.Key == key)?.Value ?? "unbound";
        return $"Agentweaver 1.x CLI {version} (source {Metadata("Agentweaver.SourceSha")}, tree {Metadata("Agentweaver.SourceTree")})";
    }

    private static HttpClient CreateSafeHttpClient() =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

    private static string HelpText =>
        """
        Agentweaver 1.x CLI

        Usage:
          agentweaver [--help] [--version]
          agentweaver auth login|status|logout [target options] [--scope <scope>]
          agentweaver api operations [target options]
          agentweaver api describe <operationId> [target options]
          agentweaver api invoke <operationId> [target options] [--path name=value] [--query name=value]
              [--body <JSON|@file>] [--header name=value] [--output <new-file>]
          target options: --gateway-url <url> --broker-issuer <url> --client-id <id>
              --api-audience <audience> --tenant-id <id> --project-id <id> --run-id <id>
          agentweaver test --input <manifest> --output <evidence.jsonl>
              [--native-probe-envelope <file>] [--dispatch-recovery-envelope <file>]
              [--dry-run] [--interactive]
        """;
}

internal sealed class CliUsageException(string message) : ArgumentException(message);

internal sealed class CliOptions
{
    private readonly Dictionary<string, List<string>> _values;
    private readonly HashSet<string> _flags;
    private readonly IReadOnlySet<string> _allowedFlags;

    private CliOptions(
        Dictionary<string, List<string>> values,
        HashSet<string> flags,
        IReadOnlySet<string> allowedFlags)
    {
        _values = values;
        _flags = flags;
        _allowedFlags = allowedFlags;
    }

    public static CliOptions Parse(string[] args, IReadOnlySet<string> allowedFlags)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (!option.StartsWith("--", StringComparison.Ordinal) || option.Length == 2)
                throw new CliUsageException($"Unexpected argument: {option}");
            var name = option[2..];
            if (allowedFlags.Contains(name))
            {
                if (!flags.Add(name))
                    throw new CliUsageException($"Option --{name} may be specified only once.");
                continue;
            }
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new CliUsageException($"Option --{name} requires a value.");
            if (!values.TryGetValue(name, out var entries))
                values[name] = entries = [];
            entries.Add(args[++index]);
        }
        return new CliOptions(values, flags, allowedFlags);
    }

    public void RequireOnly(params string[] allowed)
    {
        var set = allowed.ToHashSet(StringComparer.Ordinal);
        var unexpected = _values.Keys.Concat(_flags).FirstOrDefault(key => !set.Contains(key));
        if (unexpected is not null)
            throw new CliUsageException($"Unknown option: --{unexpected}");
    }

    public IReadOnlyList<string> Many(string name) =>
        _values.TryGetValue(name, out var entries) ? entries : [];

    public string? Single(string name)
    {
        if (!_values.TryGetValue(name, out var entries))
            return null;
        if (entries.Count != 1)
            throw new CliUsageException($"Option --{name} may be specified only once.");
        return entries[0];
    }

    public bool HasFlag(string name) => _flags.Contains(name);
}
