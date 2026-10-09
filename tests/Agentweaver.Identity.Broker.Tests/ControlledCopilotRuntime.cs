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
    private readonly CancellationTokenSource _stop;
    private readonly Task _server;
    private readonly string _connectionToken = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim _writes = new(1, 1);
    private TcpClient? _socket;
    private NetworkStream? _stream;
    private string? _sessionId;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _nativeFileRequests = [];
    private Task? _turnOutput;
    private Task? _abortOutput;
    private CancellationTokenSource? _turnStop;

    public ControlledCopilotRuntime(TimeSpan? lifetime = null)
    {
        _stop = new CancellationTokenSource(lifetime ?? TimeSpan.FromMinutes(2));
        _listener.Start();
        _server = ServeAsync();
    }

    public ConcurrentQueue<(string Method, JsonElement Parameters)> Requests { get; } = [];
    public string ModelId { get; set; } = "controlled-model";
    public string SdkCredential { get; set; } = "ghu_external-sdk-credential";
    public string AssistantResponse { get; set; } = "controlled response";
    public string ExpectedPrompt { get; set; } = "A bounded user request.";
    public string? EffectiveModelId { get; set; }
    public Action? BeforeEffectiveModelResponse { get; set; }
    public Func<CancellationToken, Task>? BeforeStatusResponse { get; set; }
    public bool EmitUsageAfterCreate { get; set; } = true;
    public bool Byok { get; set; }
    public int ExpectedAvailableToolsCount { get; set; }
    public Func<CancellationToken, Task>? BeforeTurnResponse { get; set; }
    public Func<CancellationToken, Task>? BeforeAbortIdle { get; set; }
    public bool AbortSucceeds { get; set; } = true;
    public string? LateAbortAssistantContent { get; set; }
    public bool PersistNativeSessionState { get; set; }
    public TaskCompletionSource TurnReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource AbortAcknowledged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Guid UsageEventId { get; } = Guid.NewGuid();
    public DateTimeOffset UsageTimestamp { get; } = DateTimeOffset.UtcNow;
    public RuntimeConnection Connection => RuntimeConnection.ForUri(
        $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}", _connectionToken);

    public async Task<JsonElement> InvokeNativeFilesAsync(
        string method, string path, string? content, CancellationToken cancellationToken)
        => await InvokeSdkCallbackAsync(method, new { sessionId = _sessionId, path, content }, cancellationToken);

    public async Task<JsonElement> InvokeSdkCallbackAsync(
        string method, object parameters, CancellationToken cancellationToken)
    {
        var id = "native-files-" + Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(_nativeFileRequests.TryAdd(id, completion));
        try
        {
            await WriteAsync(new
            {
                jsonrpc = "2.0", id, method,
                @params = parameters
            });
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _nativeFileRequests.TryRemove(id, out _);
        }
    }

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

    private async Task EmitAssistantAsync(string content)
    {
        var sessionId = _sessionId ?? throw new InvalidOperationException("No native session exists.");
        await WriteAsync(new
        {
            jsonrpc = "2.0",
            method = "session.event",
            @params = new
            {
                sessionId,
                @event = new
                {
                    type = "assistant.message",
                    id = Guid.NewGuid(),
                    timestamp = DateTimeOffset.UtcNow,
                    parentId = (Guid?)null,
                    agentId = (string?)null,
                    data = new { messageId = Guid.NewGuid(), content }
                }
            }
        });
    }

    private async Task EmitIdleAsync()
    {
        await WriteAsync(new
        {
            jsonrpc = "2.0",
            method = "session.event",
            @params = new
            {
                sessionId = _sessionId,
                @event = new
                {
                    type = "session.idle",
                    id = Guid.NewGuid(),
                    timestamp = DateTimeOffset.UtcNow,
                    parentId = (Guid?)null,
                    agentId = (string?)null,
                    data = new { }
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
        copilotUsage = Byok ? null : new { totalNanoAiu = 1234567.25 }
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
                    if (!root.TryGetProperty("method", out var methodName))
                    {
                        Assert.True(_nativeFileRequests.TryGetValue(id.GetString()!, out var completion));
                        if (root.TryGetProperty("error", out var failure))
                            completion.TrySetException(new InvalidOperationException(failure.ToString()));
                        else
                            completion.TrySetResult(root.GetProperty("result").Clone());
                        continue;
                    }
                    var method = methodName.GetString()!;
                    var parameters = root.TryGetProperty("params", out var supplied)
                        ? supplied : JsonSerializer.SerializeToElement(new { });
                    if (parameters.ValueKind == JsonValueKind.Array && parameters.GetArrayLength() > 0)
                        parameters = parameters[0];
                    Requests.Enqueue((method, parameters.Clone()));
                    Console.WriteLine($"Controlled native SDK {DateTimeOffset.UtcNow:O}: {method} received.");
                    object result;
                    switch (method)
                    {
                        case "connect":
                            Assert.Equal(_connectionToken, parameters.GetProperty("token").GetString());
                            result = new { ok = true, protocolVersion = 3, version = "controlled-runtime-v1" };
                            break;
                        case "sessionFs.setProvider":
                            Assert.Equal("state", parameters.GetProperty("sessionStatePath").GetString());
                            Assert.Equal("posix", parameters.GetProperty("conventions").GetString());
                            Assert.False(parameters.GetProperty("capabilities").GetProperty("sqlite").GetBoolean());
                            result = new { success = true };
                            break;
                        case "models.list":
                            Assert.False(Byok);
                            Assert.Equal(SdkCredential, parameters.GetProperty("gitHubToken").GetString());
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
                            if (BeforeStatusResponse is not null)
                                await BeforeStatusResponse(_stop.Token);
                            result = new { version = "controlled-runtime-v1", protocolVersion = 3 };
                            break;
                        case "session.create":
                        case "session.resume":
                            _sessionId = parameters.GetProperty("sessionId").GetString();
                            Assert.Equal(ModelId, parameters.GetProperty("model").GetString());
                            if (Byok)
                            {
                                Assert.False(parameters.TryGetProperty("gitHubToken", out var githubToken) &&
                                    githubToken.ValueKind != JsonValueKind.Null);
                                Assert.Equal(SdkCredential,
                                    parameters.GetProperty("provider").GetProperty("apiKey").GetString());
                            }
                            else
                            {
                                Assert.Equal(SdkCredential, parameters.GetProperty("gitHubToken").GetString());
                                Assert.False(parameters.TryGetProperty("provider", out var provider) &&
                                    provider.ValueKind != JsonValueKind.Null);
                            }
                            Assert.False(parameters.GetProperty("enableConfigDiscovery").GetBoolean());
                            Assert.False(parameters.GetProperty("enableSessionStore").GetBoolean());
                            Assert.Equal("off", parameters.GetProperty("remoteSession").GetString());
                            Assert.Equal(ExpectedAvailableToolsCount, parameters.GetProperty("availableTools").GetArrayLength());
                            if (method == "session.resume")
                                Assert.False(parameters.GetProperty("continuePendingWork").GetBoolean());
                            result = new { sessionId = _sessionId };
                            break;
                        case "session.model.getCurrent":
                            BeforeEffectiveModelResponse?.Invoke();
                            result = new { modelId = EffectiveModelId ?? ModelId };
                            break;
                        case "session.send":
                            Assert.Equal(ExpectedPrompt, parameters.GetProperty("prompt").GetString());
                            _turnStop?.Dispose();
                            _turnStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                            TurnReceived.TrySetResult();
                            result = new { };
                            break;
                        case "session.abort":
                            if (AbortSucceeds)
                            {
                                await _turnStop!.CancelAsync();
                                if (_turnOutput is not null)
                                    await _turnOutput;
                            }
                            result = new { success = AbortSucceeds };
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
                            if (_turnStop is not null)
                                await _turnStop.CancelAsync();
                            result = new { success = true };
                            break;
                        case "session.detach":
                            Assert.Equal(_sessionId, parameters.GetProperty("sessionId").GetString());
                            result = new { success = true };
                            break;
                        default:
                            throw new InvalidOperationException($"Unexpected actual SDK method: {method}");
                    }
                    await WriteAsync(new { jsonrpc = "2.0", id = id.Clone(), result });
                    Console.WriteLine($"Controlled native SDK {DateTimeOffset.UtcNow:O}: {method} response completed.");
                    if (method == "session.create" && EmitUsageAfterCreate)
                        await EmitUsageAsync(UsageData());
                    if (method == "session.send")
                        _turnOutput = CompleteTurnAsync(AssistantResponse, _turnStop!.Token);
                    if (method == "session.abort" && AbortSucceeds)
                    {
                        AbortAcknowledged.TrySetResult();
                        _abortOutput = CompleteAbortAsync();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        finally
        {
            _socket?.Dispose();
            Console.WriteLine($"Controlled native SDK {DateTimeOffset.UtcNow:O}: transport server stopped.");
        }
    }

    private async Task CompleteTurnAsync(string answer, CancellationToken cancellationToken)
    {
        try
        {
            if (BeforeTurnResponse is not null)
                await BeforeTurnResponse(cancellationToken);
            if (PersistNativeSessionState)
            {
                var result = await InvokeNativeFilesAsync("sessionFs.writeFile", "state/events.jsonl",
                    JsonSerializer.Serialize(new { sessionId = _sessionId, nativeAssistantContent = answer }),
                    cancellationToken);
                Assert.Equal(JsonValueKind.Null, result.ValueKind);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await EmitAssistantAsync(answer);
            await EmitIdleAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task CompleteAbortAsync()
    {
        if (BeforeAbortIdle is not null)
            await BeforeAbortIdle(_stop.Token);
        if (LateAbortAssistantContent is not null)
            await EmitAssistantAsync(LateAbortAssistantContent);
        await EmitIdleAsync();
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
        Console.WriteLine($"Controlled native SDK {DateTimeOffset.UtcNow:O}: transport disposal begin.");
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _server;
            if (_turnOutput is not null)
            {
                try
                {
                    await _turnOutput;
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                }
            }
            if (_abortOutput is not null)
            {
                try
                {
                    await _abortOutput;
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                }
            }
        }
        finally
        {
            _socket?.Dispose();
            _turnStop?.Dispose();
            _writes.Dispose();
            _stop.Dispose();
            Console.WriteLine($"Controlled native SDK {DateTimeOffset.UtcNow:O}: transport disposal completed.");
        }
    }
}
