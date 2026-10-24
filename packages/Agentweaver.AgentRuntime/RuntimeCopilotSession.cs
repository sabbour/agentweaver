using System.Threading.Channels;
using System.Runtime.CompilerServices;
using System.Text;
using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using GitHub.Copilot;

namespace Agentweaver.AgentRuntime;

/// <summary>Token and model details captured when native prompt compaction begins.</summary>
internal sealed record RuntimeNativePromptCompactionStart(
    long? CurrentTokens,
    long? ConversationTokens,
    long? SystemTokens,
    long? ToolDefinitionsTokens,
    long? TokenLimit,
    string? Model,
    string? Trigger);

/// <summary>Result and token counts reported when native prompt compaction ends.</summary>
internal sealed record RuntimeNativePromptCompactionComplete(
    bool Success,
    string? Error,
    long? PreCompactionTokens,
    long? PostCompactionTokens,
    long? SystemTokens,
    long? ToolDefinitionsTokens,
    long? TokenLimit,
    string? Trigger);

/// <summary>A paired compaction observation with a monotonically increasing generation.</summary>
internal sealed record RuntimeNativePromptCompactionObservation(
    long Generation,
    RuntimeNativePromptCompactionStart? Start,
    RuntimeNativePromptCompactionComplete Complete);

/// <summary>A sanitized native session error classified for prompt-capacity diagnostics.</summary>
internal sealed record RuntimeNativeSessionErrorObservation(
    long Generation, string? ErrorType, string? ErrorCode, string Cause);

/// <summary>Pairs native compaction events and retains the latest safe error classification.</summary>
internal sealed class RuntimeNativePromptCapacityObserver
{
    private readonly object _gate = new();
    private RuntimeNativePromptCompactionStart? _pendingStart;
    private RuntimeNativePromptCompactionObservation? _latest;
    private RuntimeNativeSessionErrorObservation? _latestError;
    private TaskCompletionSource _changed = CreateSignal();
    private long _generation;
    private long _errorGeneration;
    private bool _unpairedStart;

    public RuntimeNativePromptCompactionObservation? Latest
    {
        get
        {
            lock (_gate)
                return _latest;
        }
    }

    public RuntimeNativeSessionErrorObservation? LatestError
    {
        get
        {
            lock (_gate)
                return _latestError;
        }
    }

    /// <summary>Updates compaction or error evidence without retaining raw error messages.</summary>
    public void Observe(SessionEvent sessionEvent)
    {
        TaskCompletionSource? changed = null;
        lock (_gate)
        {
            if (sessionEvent is SessionCompactionStartEvent start)
            {
                _unpairedStart |= _pendingStart is not null;
                _pendingStart = new(
                    start.Data.CurrentTokens,
                    start.Data.ConversationTokens,
                    start.Data.SystemTokens,
                    start.Data.ToolDefinitionsTokens,
                    start.Data.TokenLimit,
                    start.Data.Model,
                    start.Data.Trigger?.ToString());
            }
            else if (sessionEvent is SessionCompactionCompleteEvent complete)
            {
                _latest = new(++_generation,
                    _unpairedStart ? null : _pendingStart,
                    new(
                        complete.Data.Success,
                        complete.Data.Error,
                        complete.Data.PreCompactionTokens,
                        complete.Data.PostCompactionTokens,
                        complete.Data.SystemTokens,
                        complete.Data.ToolDefinitionsTokens,
                        complete.Data.TokenLimit,
                        complete.Data.Trigger?.ToString()));
                _pendingStart = null;
                _unpairedStart = false;
                changed = _changed;
                _changed = CreateSignal();
            }
            else if (sessionEvent is SessionErrorEvent error)
            {
                _latestError = new(++_errorGeneration,
                    SafeDiagnosticIdentifier(error.Data.ErrorType),
                    SafeDiagnosticIdentifier(error.Data.ErrorCode),
                    ClassifyErrorCause(error.Data.ErrorType, error.Data.ErrorCode, error.Data.Message));
            }
        }

        changed?.TrySetResult();
    }

    private static string? SafeDiagnosticIdentifier(string? value) =>
        string.IsNullOrEmpty(value) ? null :
        value.Length <= 64 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
                ? value
                : "redacted";

    private static string ClassifyErrorCause(string? errorType, string? errorCode, string? message)
    {
        if (string.Equals(errorType, "context_limit", StringComparison.OrdinalIgnoreCase))
            return "context-limit";
        if (HasPromptCapacitySignal(errorCode))
            return "provider-code-prompt-capacity";
        if (HasPromptCapacitySignal(message))
            return "error-message-prompt-capacity";
        return "unclassified";
    }

    private static bool HasPromptCapacitySignal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var sample = value.Length > 1024 ? value[..1024] : value;
        var normalized = sample.Replace('_', ' ').Replace('-', ' ');
        var hasLimit = normalized.Contains("limit", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("exceed", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("maximum", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("too large", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("overflow", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("length", StringComparison.OrdinalIgnoreCase);
        var hasCapacity = normalized.Contains("context", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("window", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("prompt", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("token", StringComparison.OrdinalIgnoreCase);
        return hasLimit && hasCapacity;
    }

    /// <summary>Waits until an observation newer than the supplied generation is available.</summary>
    public async Task<RuntimeNativePromptCompactionObservation> WaitForNextAsync(
        long afterGeneration, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_latest is { } latest && latest.Generation > afterGeneration)
                    return latest;
                wait = _changed.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Wraps one native Copilot SDK session, serializes model turns, observes usage and compaction,
/// and captures or restores the SDK-owned virtual filesystem.
/// </summary>
public sealed class RuntimeCopilotSession : IAsyncDisposable
{
    private readonly CopilotClient _client;
    private readonly CopilotSession _session;
    private readonly Channel<RuntimeCopilotUsageItem> _usage;
    private readonly RuntimeNativePromptCapacityObserver _promptCapacityObserver;
    private readonly RuntimeCopilotTurnObserver _turns;
    private readonly RuntimeNativeSessionFiles _nativeFiles;
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeSpan _abortDrainTimeout;
    private int _unusable;
    private int _disposed;
    private readonly object _disposeLock = new();
    private Task? _disposeTask;

    /// <summary>Creates a wrapper around a configured SDK session and its bounded event streams.</summary>
    internal RuntimeCopilotSession(
        CopilotClient client,
        CopilotSession session,
        SdkSessionFacts facts,
        Channel<RuntimeCopilotUsageItem> usage,
        RuntimeNativePromptCapacityObserver promptCapacityObserver,
        RuntimeCopilotTurnObserver turns,
        RuntimeNativeSessionFiles nativeFiles,
        RuntimeSessionRecoveryMode recoveryMode,
        string? recoveryReason,
        RuntimeResumeJournalRebuildReceipt? journalRebuildReceipt,
        SessionMaterialAcknowledgment? consumedNativeCacheAcknowledgment,
        RuntimeSessionJournalEvidence? journalEvidence,
        TimeSpan abortDrainTimeout,
        ImmutableArray<string>? verifiedHostedReasoningEfforts = null,
        DateTimeOffset? verifiedHostedReasoningCatalogAt = null)
    {
        _client = client;
        _session = session;
        Facts = facts;
        _usage = usage;
        _promptCapacityObserver = promptCapacityObserver;
        _turns = turns;
        _nativeFiles = nativeFiles;
        RecoveryMode = recoveryMode;
        RecoveryReason = recoveryReason;
        JournalRebuildReceipt = journalRebuildReceipt;
        ConsumedNativeCacheAcknowledgment = consumedNativeCacheAcknowledgment;
        JournalEvidence = journalEvidence;
        VerifiedHostedReasoningEfforts = verifiedHostedReasoningEfforts;
        VerifiedHostedReasoningCatalogAt = verifiedHostedReasoningCatalogAt;
        _abortDrainTimeout = abortDrainTimeout;
    }

    /// <summary>Immutable SDK, model, and source facts validated during session creation.</summary>
    public SdkSessionFacts Facts { get; }

    /// <summary>Reasoning levels read from the live SDK catalog for this hosted session, or null for BYOK.</summary>
    public ImmutableArray<string>? VerifiedHostedReasoningEfforts { get; }

    /// <summary>Time the live hosted SDK model catalog was read for these reasoning levels.</summary>
    public DateTimeOffset? VerifiedHostedReasoningCatalogAt { get; }

    /// <summary>Completes when the usage event channel is closed or faulted.</summary>
    public Task UsageCompletion => _usage.Reader.Completion;

    /// <summary>Indicates whether the session was created fresh, from native cache, or by journal rebuild.</summary>
    public RuntimeSessionRecoveryMode RecoveryMode { get; }

    /// <summary>Safe reason explaining why a native cache was not reused, when applicable.</summary>
    public string? RecoveryReason { get; }

    /// <summary>Receipt for a resume that rebuilt the session from its recorded event journal.</summary>
    public RuntimeResumeJournalRebuildReceipt? JournalRebuildReceipt { get; }

    /// <summary>The acknowledgment for a native cache consumed during resume.</summary>
    internal SessionMaterialAcknowledgment? ConsumedNativeCacheAcknowledgment { get; }

    /// <summary>Evidence describing the journal snapshot used to recover this session.</summary>
    internal RuntimeSessionJournalEvidence? JournalEvidence { get; }

    /// <summary>Reads current prompt context after checking the effective model and provider identity.</summary>
    internal async Task<GitHub.Copilot.Rpc.MetadataContextInfoResultContextInfo?> ReadPromptContextInfoAsync(
        CancellationToken cancellationToken)
    {
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireUsable();
#pragma warning disable GHCP001 // Context evidence is pinned to SDK 1.0.18.
            var model = await _session.Rpc.Model.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var expectedProviderId = Facts.ByokProvider?.Type;
            if (model.ModelId != Facts.ModelId ||
                HasProviderIdentityMismatch(expectedProviderId, model.ProviderId))
                throw CreateEffectiveModelMismatch(model.ModelId, model.ProviderId);

            string? providerId = expectedProviderId is not null && string.IsNullOrEmpty(model.ProviderId)
                ? null
                : model.ProviderId;
            var result = await _session.Rpc.Metadata.ContextInfoAsync(
                0, 0, model.ModelId, providerId, cancellationToken).ConfigureAwait(false);
            var context = result?.ContextInfo;
            var contextProviderId = context?.Provider?.Id;
            if (HasProviderIdentityMismatch(expectedProviderId, contextProviderId))
                throw CreateEffectiveModelMismatch(
                    model.ModelId, model.ProviderId, contextProviderId);

#pragma warning restore GHCP001
            return context;
        }
        finally
        {
            _turnGate.Release();
        }
    }

#pragma warning disable GHCP001 // Context evidence is pinned to SDK 1.0.18.
    /// <summary>Returns the SDK's heaviest context messages for the current validated model.</summary>
    internal async Task<IReadOnlyList<GitHub.Copilot.Rpc.ContextHeaviestMessage>>
        ReadContextHeaviestMessagesAsync(CancellationToken cancellationToken)
    {
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireUsable();
            var model = await _session.Rpc.Model.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var expectedProviderId = Facts.ByokProvider?.Type;
            if (model.ModelId != Facts.ModelId ||
                HasProviderIdentityMismatch(expectedProviderId, model.ProviderId))
                throw CreateEffectiveModelMismatch(model.ModelId, model.ProviderId);

            var result = await _session.Rpc.Metadata.GetContextHeaviestMessagesAsync(
                100, cancellationToken).ConfigureAwait(false);
            return result.Messages?.ToArray()
                ?? throw new RuntimeAuthorizationException("runtime_context_messages_unavailable");
        }
        finally
        {
            _turnGate.Release();
        }
    }
#pragma warning restore GHCP001

    /// <summary>Reports a mismatch only when both the pinned and effective provider IDs are present.</summary>
    internal static bool HasProviderIdentityMismatch(string? expectedProviderId, string? actualProviderId) =>
        expectedProviderId is not null &&
        !string.IsNullOrEmpty(actualProviderId) &&
        actualProviderId != expectedProviderId;

    /// <summary>The latest completed native compaction observation, if any.</summary>
    internal RuntimeNativePromptCompactionObservation? LatestPromptCapacityCompaction =>
        _promptCapacityObserver.Latest;

    /// <summary>The latest sanitized native session error, if any.</summary>
    internal RuntimeNativeSessionErrorObservation? LatestNativeSessionError =>
        _promptCapacityObserver.LatestError;

    /// <summary>Waits for compaction evidence newer than the given generation.</summary>
    internal Task<RuntimeNativePromptCompactionObservation> WaitForPromptCapacityCompactionAsync(
        long afterGeneration, CancellationToken cancellationToken) =>
        _promptCapacityObserver.WaitForNextAsync(afterGeneration, cancellationToken);

    /// <summary>Captures a bounded snapshot of the SDK virtual filesystem while no turn is active.</summary>
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

    /// <summary>Captures the event-log cursor used to delimit the next native turn receipt.</summary>
    internal async Task<string> CaptureNativeEventCursorAsync(CancellationToken cancellationToken)
    {
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireUsable();
            return await RuntimeCopilotNativeTurnJournal.CaptureCursorAsync(_session, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _turnGate.Release();
        }
    }

    private RuntimeAuthorizationException CreateEffectiveModelMismatch(
        string? actualModelId, string? actualProviderId, string? contextProviderId = null)
    {
        var expectedProvider = Facts.ByokProvider is { } provider
            ? SafeSdkIdentity(provider.Type)
            : "<not-applicable>";
        var contextProvider = contextProviderId is null
            ? "<not-checked>"
            : SafeSdkIdentity(contextProviderId);
        return new RuntimeAuthorizationException(
            "runtime_sdk_effective_model_mismatch",
            $"expected_model={SafeSdkIdentity(Facts.ModelId)}; " +
            $"actual_model={SafeSdkIdentity(actualModelId)}; " +
            $"expected_provider={expectedProvider}; " +
            $"actual_provider={SafeSdkIdentity(actualProviderId)}; " +
            $"context_provider={contextProvider}");
    }

    private static string SafeSdkIdentity(string? identity)
    {
        if (string.IsNullOrEmpty(identity))
            return "<empty>";

        const int maximumLength = 64;
        var length = Math.Min(identity.Length, maximumLength);
        var characters = new char[length];
        for (var index = 0; index < length; index++)
        {
            var character = identity[index];
            characters[index] = char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'
                ? character
                : '_';
        }

        return new string(characters) + (identity.Length > length ? "..." : string.Empty);
    }

    /// <summary>Proves that a failed or denied attempt emitted no native events after the captured cursor.</summary>
    internal async Task<RuntimeNativeEmptyRangeEvidence> RequireNoNativeEventsSinceAsync(
        string startCursor, CancellationToken cancellationToken)
    {
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireUsable();
            return await RuntimeCopilotNativeTurnJournal.RequireEmptyRangeAsync(
                _session, startCursor, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _turnGate.Release();
        }
    }

    /// <summary>Rejects control characters other than tab and line breaks in user prompts.</summary>
    internal static bool HasUnsupportedPromptControls(string prompt) =>
        prompt.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t'));

    /// <summary>Sends a bounded text prompt and returns the assistant response.</summary>
    internal async Task<string> SendTurnAsync(
        string prompt,
        CancellationToken cancellationToken,
        Guid? a2aMessageId = null,
        bool readOnlyToolsOnly = false) =>
        (await SendTurnCoreAsync(
            prompt, captureNativeReceipt: false, cancellationToken, a2aMessageId, readOnlyToolsOnly)
            .ConfigureAwait(false)).Response;

    /// <summary>Sends a turn and returns the SDK event evidence tied to its native message ID.</summary>
    internal async Task<RuntimeCopilotNativeTurnResult> SendTurnWithNativeReceiptAsync(
        string prompt, CancellationToken cancellationToken, Guid? a2aMessageId = null)
    {
        var result = await SendTurnCoreAsync(
            prompt, captureNativeReceipt: true, cancellationToken, a2aMessageId, readOnlyToolsOnly: false)
            .ConfigureAwait(false);
        return new(result.Response, result.Receipt ??
            throw new RuntimeAuthorizationException("runtime_native_turn_receipt_unavailable"));
    }

    /// <summary>
    /// Serializes the turn, optionally captures native events, and aborts/drains a failed call;
    /// an unconfirmed drain marks the session unusable.
    /// </summary>
    private async Task<(string Response, RuntimeNativeTurnObservation? Receipt)> SendTurnCoreAsync(
        string prompt,
        bool captureNativeReceipt,
        CancellationToken cancellationToken,
        Guid? a2aMessageId,
        bool readOnlyToolsOnly)
    {
        if (string.IsNullOrWhiteSpace(prompt) ||
            prompt.Length > AddressedMessageValidation.MaximumTextLength ||
            HasUnsupportedPromptControls(prompt) || a2aMessageId == Guid.Empty)
            throw new ArgumentException("A bounded text prompt is required.", nameof(prompt));

        RequireUsable();
        using var active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await _turnGate.WaitAsync(active.Token).ConfigureAwait(false);
        try
        {
            RequireUsable();
            string? startCursor = null;
            if (captureNativeReceipt)
                startCursor = await RuntimeCopilotNativeTurnJournal.CaptureCursorAsync(_session, active.Token)
                    .ConfigureAwait(false);
            // Bind the requested tool restriction to this exact active native turn.
            var completion = _turns.Begin(active.Token, a2aMessageId, readOnlyToolsOnly);
            try
            {
                var nativeMessageId = await _session.SendAsync(new MessageOptions { Prompt = prompt }, active.Token)
                    .ConfigureAwait(false);
                var response = await completion.WaitAsync(active.Token).ConfigureAwait(false);
                var receipt = captureNativeReceipt
                    ? await RuntimeCopilotNativeTurnJournal.ReadAsync(
                        _session, Facts, startCursor!, nativeMessageId, response, active.Token)
                        .ConfigureAwait(false)
                    : null;
                return (response, receipt);
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

    /// <summary>Reads SDK usage events and releases queued flush barriers in event order.</summary>
    public async IAsyncEnumerable<SdkUsageObservation> ReadUsageAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _usage.Reader.ReadAllAsync(cancellationToken))
        {
            if (item.Observation is { } usage)
                yield return Extract(usage, item.A2AMessageId);
            else
                item.Barrier!.TrySetResult();
        }
    }

    /// <summary>Waits until all usage events queued before the barrier have been consumed.</summary>
    internal async Task FlushUsageAsync(CancellationToken cancellationToken)
    {
        RequireUsable();
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _usage.Writer.WriteAsync(new(null, barrier), cancellationToken).ConfigureAwait(false);
        await barrier.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private SdkUsageObservation Extract(AssistantUsageEvent usage, Guid? a2aMessageId)
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
                Milliseconds(usage.Data.Duration))
            {
                A2AMessageId = a2aMessageId,
                Accounting = ExtractAccounting(Facts, usage)
            };
        }
        catch (Exception exception) when (exception is RuntimeAuthorizationException or OverflowException)
        {
            _usage.Writer.TryComplete(exception);
            throw;
        }
    }

    /// <summary>Maps SDK accounting status and identity into the validated usage contract.</summary>
    internal static SdkUsageAccountingObservation ExtractAccounting(
        SdkSessionFacts source, AssistantUsageEvent usage)
    {
        var status = usage.Data.AiCreditsStatus;
        var accounting = new SdkUsageAccountingObservation(
            usage.Data.Accounting is { } identity
                ? new(identity.SourceSessionId, identity.Sequence, identity.UsageId) : null,
            status switch
            {
                null => SdkAiCreditsStatus.Unavailable,
                { } value when value == AiCreditsStatus.Complete => SdkAiCreditsStatus.Complete,
                { } value when value == AiCreditsStatus.Partial => SdkAiCreditsStatus.Partial,
                { } value when value == AiCreditsStatus.Unavailable => SdkAiCreditsStatus.Unavailable,
                _ => throw new RuntimeAuthorizationException("runtime_usage_accounting_invalid")
            },
            status is not null);
        RuntimeUsageSourceReceiptContract.ValidateAccounting(
            source, accounting, NullableDecimal(usage.Data.CopilotUsage?.TotalNanoAiu));
        return accounting;
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

    /// <summary>Converts a finite, non-negative SDK measurement without silently accepting invalid values.</summary>
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

    /// <summary>Stops active work and disposes the native SDK session and client once.</summary>
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

/// <summary>One SDK usage observation or an ordered barrier in the session's single-reader channel.</summary>
internal sealed record RuntimeCopilotUsageItem(
    AssistantUsageEvent? Observation, TaskCompletionSource? Barrier, Guid? A2AMessageId = null);

/// <summary>Collects assistant text and idle/error signals for one active native SDK turn.</summary>
internal sealed class RuntimeCopilotTurnObserver
{
    private readonly object _gate = new();
    private TaskCompletionSource<string>? _completion;
    private StringBuilder? _content;
    private CancellationToken _cancellationToken;
    private TaskCompletionSource? _idle;
    private Guid? _a2aMessageId;
    private bool _readOnlyToolsOnly;

    /// <summary>Starts collecting one turn and returns the task completed by idle or error events.</summary>
    public Task<string> Begin(
        CancellationToken cancellationToken,
        Guid? a2aMessageId = null,
        bool readOnlyToolsOnly = false)
    {
        lock (_gate)
        {
            if (_completion is not null)
                throw new InvalidOperationException("A Copilot SDK turn is already active.");
            _content = new StringBuilder();
            _cancellationToken = cancellationToken;
            _a2aMessageId = a2aMessageId;
            _readOnlyToolsOnly = readOnlyToolsOnly;
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _completion.Task;
        }
    }

    /// <summary>Gets the message ID only while its native turn remains active.</summary>
    public Guid? ActiveA2AMessageId
    {
        get
        {
            lock (_gate)
                return _completion is { Task.IsCompleted: false } ? _a2aMessageId : null;
        }
    }

    /// <summary>Indicates whether the active message is limited to read-only tools.</summary>
    public bool ReadOnlyToolsOnly
    {
        get
        {
            lock (_gate)
                return _completion is { Task.IsCompleted: false } && _readOnlyToolsOnly;
        }
    }

    /// <summary>Indicates that the active native turn emitted its idle event.</summary>
    public bool IsIdle
    {
        get
        {
            lock (_gate)
                return _idle?.Task.IsCompletedSuccessfully == true;
        }
    }

    /// <summary>Waits for the current native turn to become idle.</summary>
    public Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
            return (_idle?.Task ?? throw new InvalidOperationException("No native turn is active."))
                .WaitAsync(cancellationToken);
    }

    /// <summary>Returns the active turn token or rejects access when no turn is running.</summary>
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

    /// <summary>Appends assistant text up to the response limit and completes on idle or SDK error.</summary>
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

    /// <summary>Clears the active turn state after completion or failure.</summary>
    public void End()
    {
        lock (_gate)
        {
            _completion = null;
            _content = null;
            _cancellationToken = default;
            _idle = null;
            _a2aMessageId = null;
            _readOnlyToolsOnly = false;
        }
    }
}
