using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Agentweaver.AgentRuntime;

/// <summary>
/// Connection settings for the operator assistant's MCP client. <see cref="Endpoint"/> is the
/// AgentweaverMCP server's streamable-HTTP endpoint (for example <c>https://host/mcp</c>).
/// </summary>
public sealed record AgentweaverMcpConnectionOptions
{
    /// <summary>The AgentweaverMCP <c>/mcp</c> streamable-HTTP endpoint.</summary>
    public required Uri Endpoint { get; init; }

    /// <summary>Client identity advertised to the server on initialize.</summary>
    public string ClientName { get; init; } = "agentweaver-operator-assistant";

    /// <summary>Transport connect timeout.</summary>
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Adapter that connects the in-API Copilot session to the real AgentweaverMCP server and exposes
/// its tools as Microsoft.Extensions.AI <see cref="AIFunction"/>s so they can be dropped straight
/// into <c>SessionConfig.Tools</c> (which is a list of <see cref="AIFunctionDeclaration"/>).
///
/// This replaces the 15 hand-wrapped read-only tools that used to live in the legacy Console facade agent with
/// the single source of truth (all ~91 MCP tools). The caller's Agentweaver broker token is passed through
/// on every request through the streamable-HTTP transport, so
/// each JSON-RPC <c>tools/call</c> (a distinct HTTP POST in stateless streamable-HTTP mode) carries the
/// caller identity that the MCP server's bearer middleware forwards to the backend API.
/// </summary>
public interface IAgentweaverMcpToolProvider
{
    /// <summary>
    /// Connects to the MCP server as the given caller and enumerates its tools. The returned session
    /// owns the live MCP connection and MUST be disposed when the conversation ends.
    /// </summary>
    Task<AgentweaverMcpToolSession> ConnectAsync(string brokerToken, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class AgentweaverMcpToolProvider : IAgentweaverMcpToolProvider
{
    private readonly AgentweaverMcpConnectionOptions _options;
    private readonly ILoggerFactory? _loggerFactory;

    // Optional factory so tests can supply an HttpClient bound to an in-process host. Production
    // leaves this null and the transport creates its own pooled HttpClient.
    private readonly Func<HttpClient>? _httpClientFactory;
    private readonly bool _ownsHttpClient;

    public AgentweaverMcpToolProvider(
        AgentweaverMcpConnectionOptions options,
        ILoggerFactory? loggerFactory = null,
        Func<HttpClient>? httpClientFactory = null,
        bool ownsHttpClient = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _loggerFactory = loggerFactory;
        _httpClientFactory = httpClientFactory;
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<AgentweaverMcpToolSession> ConnectAsync(string brokerToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(brokerToken))
            throw new ArgumentException(
                "An Agentweaver broker token for the MCP resource is required.",
                nameof(brokerToken));

        var transportOptions = new SseClientTransportOptions
        {
            Endpoint = _options.Endpoint,
            Name = _options.ClientName,
            // Streamable HTTP matches the server (apps/Agentweaver.Mcp: WithHttpTransport stateless).
            TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = _options.ConnectionTimeout,
            // Per-call bearer passthrough: every streamable-HTTP request carries the caller's token.
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + brokerToken,
            },
        };

        var http = _httpClientFactory?.Invoke();
        var transport = http is null
            ? new SseClientTransport(transportOptions, _loggerFactory)
            : new SseClientTransport(transportOptions, http, _loggerFactory, ownsHttpClient: _ownsHttpClient);

        var client = await McpClientFactory
            .CreateAsync(transport, clientOptions: null, loggerFactory: _loggerFactory, cancellationToken: ct)
            .ConfigureAwait(false);

        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
            return new AgentweaverMcpToolSession(client, tools);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// A live MCP session plus its enumerated tools. Each tool is a <see cref="McpClientTool"/>, which
/// derives from <see cref="AIFunction"/>: invoking it issues a <c>tools/call</c> over the same
/// bearer-authenticated transport. Dispose to close the underlying connection.
/// </summary>
public sealed class AgentweaverMcpToolSession : IAsyncDisposable
{
    private readonly IMcpClient _client;

    internal AgentweaverMcpToolSession(IMcpClient client, IList<McpClientTool> tools)
    {
        _client = client;
        Tools = tools.ToList();
    }

    /// <summary>The MCP tools exposed by the server, as AIFunctions ready for a Copilot session.</summary>
    public IReadOnlyList<McpClientTool> Tools { get; }

    /// <summary>Adapts every MCP tool to the <see cref="AIFunctionDeclaration"/> form used by SessionConfig.Tools.</summary>
    public IReadOnlyList<AIFunctionDeclaration> AsToolDeclarations() =>
        Tools.Cast<AIFunctionDeclaration>().ToList();

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
