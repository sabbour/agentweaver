using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GitHub.Copilot;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

// Controls only the external SDK transport, catalog and native events.
internal sealed class ControlledCopilotRuntime : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
    private readonly Task _server;
    private readonly string _connectionToken = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim _writes = new(1, 1);
    private TcpClient? _socket;
    private NetworkStream? _stream;
    private string? _sessionId;

    public ControlledCopilotRuntime()
    {
        _listener.Start();
        _server = ServeAsync();
    }

    public ConcurrentQueue<(string Method, JsonElement Parameters)> Requests { get; } = [];
    public string ModelId { get; set; } = "controlled-model";
    public string? EffectiveModelId { get; set; }
    public Action? BeforeEffectiveModelResponse { get; set; }
    public bool EmitUsageAfterCreate { get; set; } = true;
    public Guid UsageEventId { get; } = Guid.NewGuid();
    public DateTimeOffset UsageTimestamp { get; } = DateTimeOffset.UtcNow;
    public RuntimeConnection Connection => RuntimeConnection.ForUri(
        $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}", _connectionToken);

    public async Task EmitUsageAsync(object data, string? agentId = null)
    {
        await WriteAsync(new
        {
            jsonrpc = "2.0",
            method = "session.event",
            @params = new
            {
                sessionId = _sessionId ?? throw new InvalidOperationException("No native session exists."),
                @event = new
                {
                    type = "assistant.usage",
                    id = UsageEventId,
                    timestamp = UsageTimestamp,
                    parentId = (Guid?)null,
                    agentId,
                    data
                }
            }
        });
    }

    public object UsageData(string? model = null, string? initiator = null) => new
    {
        model = model ?? ModelId,
        initiator,
        inputTokens = 17,
        outputTokens = 11,
        cacheReadTokens = 7,
        cacheWriteTokens = 5,
        reasoningTokens = 3,
        duration = 12.5,
        copilotUsage = new { totalNanoAiu = 1234567.25 }
    };

    private async Task ServeAsync()
    {
        try
        {
            _socket = await _listener.AcceptTcpClientAsync(_stop.Token);
            _stream = _socket.GetStream();
            while (await ReadAsync(_stream, _stop.Token) is { } message)
            {
                using (message)
                {
                    var root = message.RootElement;
                    if (!root.TryGetProperty("id", out var id))
                        continue;
                    var method = root.GetProperty("method").GetString()!;
                    var parameters = root.TryGetProperty("params", out var supplied)
                        ? supplied : JsonSerializer.SerializeToElement(new { });
                    if (parameters.ValueKind == JsonValueKind.Array && parameters.GetArrayLength() > 0)
                        parameters = parameters[0];
                    Requests.Enqueue((method, parameters.Clone()));
                    object result;
                    switch (method)
                    {
                        case "connect":
                            Assert.Equal(_connectionToken, parameters.GetProperty("token").GetString());
                            result = new { ok = true, protocolVersion = 3, version = "controlled-runtime-v1" };
                            break;
                        case "models.list":
                            Assert.Equal("external-sdk-credential", parameters.GetProperty("gitHubToken").GetString());
                            result = new
                            {
                                models = new[]
                                {
                                    new
                                    {
                                        id = ModelId, name = "Controlled catalog model",
                                        policy = new { state = "enabled" },
                                        billing = new { multiplier = 2.5 }
                                    }
                                }
                            };
                            break;
                        case "status.get":
                            result = new { version = "controlled-runtime-v1", protocolVersion = 3 };
                            break;
                        case "session.create":
                            _sessionId = parameters.GetProperty("sessionId").GetString();
                            Assert.Equal(ModelId, parameters.GetProperty("model").GetString());
                            Assert.Equal("external-sdk-credential", parameters.GetProperty("gitHubToken").GetString());
                            Assert.False(parameters.GetProperty("enableConfigDiscovery").GetBoolean());
                            Assert.False(parameters.GetProperty("enableSessionStore").GetBoolean());
                            Assert.Equal("off", parameters.GetProperty("remoteSession").GetString());
                            Assert.Equal(0, parameters.GetProperty("availableTools").GetArrayLength());
                            result = new { sessionId = _sessionId };
                            break;
                        case "session.model.getCurrent":
                            BeforeEffectiveModelResponse?.Invoke();
                            result = new { modelId = EffectiveModelId ?? ModelId };
                            break;
                        case "session.options.update":
                            Assert.True(parameters.GetProperty("skipCustomInstructions").GetBoolean());
                            Assert.True(parameters.GetProperty("customAgentsLocalOnly").GetBoolean());
                            Assert.False(parameters.GetProperty("coauthorEnabled").GetBoolean());
                            Assert.False(parameters.GetProperty("manageScheduleEnabled").GetBoolean());
                            Assert.Equal(0, parameters.GetProperty("installedPlugins").GetArrayLength());
                            result = new { };
                            break;
                        case "session.destroy":
                            result = new { success = true };
                            break;
                        default:
                            throw new InvalidOperationException($"Unexpected actual SDK method: {method}");
                    }
                    await WriteAsync(new { jsonrpc = "2.0", id = id.Clone(), result });
                    if (method == "session.create" && EmitUsageAfterCreate)
                        await EmitUsageAsync(UsageData());
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        finally
        {
            _socket?.Dispose();
        }
    }

    private async Task WriteAsync(object message)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        await _writes.WaitAsync(_stop.Token);
        try
        {
            var stream = _stream ?? throw new InvalidOperationException("No native connection exists.");
            await stream.WriteAsync(header, _stop.Token);
            await stream.WriteAsync(payload, _stop.Token);
            await stream.FlushAsync(_stop.Token);
        }
        finally
        {
            _writes.Release();
        }
    }

    private static async Task<JsonDocument?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new StringBuilder();
        var single = new byte[1];
        while (header.Length < 128 && !header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(single, cancellationToken) == 0)
                return null;
            header.Append((char)single[0]);
        }
        var text = header.ToString();
        if (!text.StartsWith("Content-Length: ", StringComparison.Ordinal) ||
            !text.EndsWith("\r\n\r\n", StringComparison.Ordinal))
            throw new InvalidOperationException("Malformed actual SDK frame.");
        var length = int.Parse(text[16..^4], CultureInfo.InvariantCulture);
        if (length <= 0 || length > 65536)
            throw new InvalidOperationException("Unbounded actual SDK frame.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonDocument.Parse(payload);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _server;
        }
        finally
        {
            _socket?.Dispose();
            _writes.Dispose();
            _stop.Dispose();
        }
    }
}
