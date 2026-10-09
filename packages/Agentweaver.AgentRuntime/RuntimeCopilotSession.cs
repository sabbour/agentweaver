using System.Threading.Channels;
using System.Runtime.CompilerServices;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using GitHub.Copilot;

namespace Agentweaver.AgentRuntime;

public sealed class RuntimeCopilotSession : IAsyncDisposable
{
    private readonly CopilotClient _client;
    private readonly CopilotSession _session;
    private readonly Channel<RuntimeCopilotUsageItem> _usage;
    private readonly RuntimeCopilotTurnObserver _turns;
    private readonly RuntimeNativeSessionFiles _nativeFiles;
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeSpan _abortDrainTimeout;
    private int _unusable;
    private int _disposed;
    private readonly object _disposeLock = new();
    private Task? _disposeTask;

    internal RuntimeCopilotSession(
        CopilotClient client,
        CopilotSession session,
        SdkSessionFacts facts,
        Channel<RuntimeCopilotUsageItem> usage,
        RuntimeCopilotTurnObserver turns,
        RuntimeNativeSessionFiles nativeFiles,
        RuntimeSessionRecoveryMode recoveryMode,
        string? recoveryReason,
        TimeSpan abortDrainTimeout)
    {
        _client = client;
        _session = session;
        Facts = facts;
        _usage = usage;
        _turns = turns;
        _nativeFiles = nativeFiles;
        RecoveryMode = recoveryMode;
        RecoveryReason = recoveryReason;
        _abortDrainTimeout = abortDrainTimeout;
    }

    public SdkSessionFacts Facts { get; }
    public Task UsageCompletion => _usage.Reader.Completion;
    public RuntimeSessionRecoveryMode RecoveryMode { get; }
    public string? RecoveryReason { get; }

    internal async Task<byte[]> CaptureNativeCacheAsync(CancellationToken cancellationToken)
    {
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireUsable();
            cancellationToken.ThrowIfCancellationRequested();
            return _nativeFiles.Capture();
        }
        finally
        {
            _turnGate.Release();
        }
    }

    internal static bool HasUnsupportedPromptControls(string prompt) =>
        prompt.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t'));

    internal async Task<string> SendTurnAsync(string prompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prompt) ||
            prompt.Length > AddressedMessageValidation.MaximumTextLength ||
            HasUnsupportedPromptControls(prompt))
            throw new ArgumentException("A bounded text prompt is required.", nameof(prompt));

        RequireUsable();
        using var active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await _turnGate.WaitAsync(active.Token).ConfigureAwait(false);
        try
        {
            RequireUsable();
            var completion = _turns.Begin(active.Token);
            try
            {
                await _session.SendAsync(new MessageOptions { Prompt = prompt }, active.Token)
                    .ConfigureAwait(false);
                return await completion.WaitAsync(active.Token).ConfigureAwait(false);
            }
            catch (Exception turnFailure) when (turnFailure is not OutOfMemoryException)
            {
                if (!_turns.IsIdle)
                {
                    using var drain = new CancellationTokenSource(_abortDrainTimeout);
                    try
                    {
#pragma warning disable GHCP001 // Native abort acknowledgment is pinned to SDK 1.0.18.
                        var result = await _session.Rpc.AbortAsync(AbortReason.UserInitiated, drain.Token)
                            .ConfigureAwait(false);
#pragma warning restore GHCP001
                        if (!result.Success)
                            throw new RuntimeAuthorizationException("runtime_native_abort_failed");
                        await _turns.WaitForIdleAsync(drain.Token).ConfigureAwait(false);
                    }
                    catch (Exception cleanupFailure) when (cleanupFailure is not OutOfMemoryException)
                    {
                        Volatile.Write(ref _unusable, 1);
                        throw new AggregateException("runtime_native_turn_indeterminate", turnFailure, cleanupFailure);
                    }
                }
                throw;
            }
            finally
            {
                _turns.End();
            }
        }
        finally
        {
            _turnGate.Release();
        }
    }

    public async IAsyncEnumerable<SdkUsageObservation> ReadUsageAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _usage.Reader.ReadAllAsync(cancellationToken))
        {
            if (item.Observation is { } usage)
                yield return Extract(usage);
            else
                item.Barrier!.TrySetResult();
        }
    }

    internal async Task FlushUsageAsync(CancellationToken cancellationToken)
    {
        RequireUsable();
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _usage.Writer.WriteAsync(new(null, barrier), cancellationToken).ConfigureAwait(false);
        await barrier.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private SdkUsageObservation Extract(AssistantUsageEvent usage)
    {
        try
        {
            if (_session.SessionId != Facts.SdkSessionId ||
                usage.Data.Model != Facts.ModelId ||
                usage.AgentId is not null || usage.Data.Initiator is not null ||
                Facts.SourceMode == "byok" && usage.Data.CopilotUsage?.TotalNanoAiu is not null ||
                usage.Id == Guid.Empty)
                throw new RuntimeAuthorizationException("runtime_sdk_usage_binding_invalid");
            return new SdkUsageObservation(
                SdkUsageIdentity.Create(Facts.RuntimeInstanceId, Facts.SdkSessionId, usage.Id.ToString("D")),
                usage.Id.ToString("D"),
                Facts.SdkSessionId,
                usage.Timestamp,
                Facts.ModelId,
                Integral(usage.Data.InputTokens),
                Integral(usage.Data.OutputTokens),
                Integral(usage.Data.CacheReadTokens),
                Integral(usage.Data.CacheWriteTokens),
                Integral(usage.Data.ReasoningTokens),
                NullableDecimal(usage.Data.CopilotUsage?.TotalNanoAiu),
                Milliseconds(usage.Data.Duration));
        }
        catch (Exception exception) when (exception is RuntimeAuthorizationException or OverflowException)
        {
            _usage.Writer.TryComplete(exception);
            throw;
        }
    }

    private static long? Integral(double? value)
    {
        if (value is null)
            return null;
        if (!double.IsFinite(value.Value) || value.Value < 0 || Math.Truncate(value.Value) != value ||
            value.Value >= 9223372036854775808d)
            throw new RuntimeAuthorizationException("runtime_sdk_measurement_invalid");
        return checked((long)value.Value);
    }

    internal static decimal? NullableDecimal(double? value)
    {
        if (value is null)
            return null;
        if (!double.IsFinite(value.Value) || value.Value < 0)
            throw new RuntimeAuthorizationException("runtime_sdk_measurement_invalid");
        return checked((decimal)value.Value);
    }

    private static decimal? Milliseconds(TimeSpan? value)
    {
        if (value is null)
            return null;
        if (value.Value < TimeSpan.Zero)
            throw new RuntimeAuthorizationException("runtime_sdk_measurement_invalid");
        return (decimal)value.Value.Ticks / TimeSpan.TicksPerMillisecond;
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref _disposed, 1);
        await _stop.CancelAsync().ConfigureAwait(false);
        await _turnGate.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                await _session.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await _client.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _usage.Writer.TryComplete();
            _turnGate.Release();
            _stop.Dispose();
        }
    }

    private void RequireUsable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _unusable) != 0)
            throw new RuntimeAuthorizationException("runtime_native_turn_indeterminate");
    }
}

internal sealed record RuntimeCopilotUsageItem(AssistantUsageEvent? Observation, TaskCompletionSource? Barrier);

internal sealed class RuntimeCopilotTurnObserver
{
    private readonly object _gate = new();
    private TaskCompletionSource<string>? _completion;
    private StringBuilder? _content;
    private CancellationToken _cancellationToken;
    private TaskCompletionSource? _idle;

    public Task<string> Begin(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_completion is not null)
                throw new InvalidOperationException("A Copilot SDK turn is already active.");
            _content = new StringBuilder();
            _cancellationToken = cancellationToken;
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _completion.Task;
        }
    }

    public bool IsIdle
    {
        get
        {
            lock (_gate)
                return _idle?.Task.IsCompletedSuccessfully == true;
        }
    }

    public Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
            return (_idle?.Task ?? throw new InvalidOperationException("No native turn is active."))
                .WaitAsync(cancellationToken);
    }

    public CancellationToken RequireActiveToken()
    {
        lock (_gate)
        {
            if (_completion is not { Task.IsCompleted: false })
                throw new RuntimeAuthorizationException("runtime_native_turn_unavailable");
            _cancellationToken.ThrowIfCancellationRequested();
            return _cancellationToken;
        }
    }

    public void Observe(SessionEvent sessionEvent)
    {
        lock (_gate)
        {
            if (_completion is not { } completion || _content is null)
                return;

            if (sessionEvent is SessionIdleEvent)
            {
                _idle!.TrySetResult();
                completion.TrySetResult(_content.ToString());
            }
            else if (sessionEvent is AssistantMessageEvent assistant &&
                !completion.Task.IsCompleted && !_cancellationToken.IsCancellationRequested)
            {
                var content = assistant.Data.Content;
                if (_content.Length + content.Length > AddressedMessageValidation.MaximumTextLength)
                {
                    completion.TrySetException(new InvalidOperationException("runtime_sdk_turn_output_too_large"));
                    return;
                }
                _content.Append(content);
            }
            else if (sessionEvent is SessionErrorEvent)
            {
                completion.TrySetException(new InvalidOperationException("runtime_sdk_turn_failed"));
            }
        }
    }

    public void End()
    {
        lock (_gate)
        {
            _completion = null;
            _content = null;
            _cancellationToken = default;
            _idle = null;
        }
    }
}
