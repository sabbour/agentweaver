using System.Threading.Channels;
using System.Runtime.CompilerServices;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using GitHub.Copilot;

namespace Agentweaver.AgentRuntime;

public sealed class RuntimeCopilotSession : IAsyncDisposable
{
    private readonly CopilotClient _client;
    private readonly CopilotSession _session;
    private readonly Channel<AssistantUsageEvent> _usage;

    internal RuntimeCopilotSession(
        CopilotClient client, CopilotSession session, SdkSessionFacts facts, Channel<AssistantUsageEvent> usage)
    {
        _client = client;
        _session = session;
        Facts = facts;
        _usage = usage;
    }

    public SdkSessionFacts Facts { get; }
    public Task UsageCompletion => _usage.Reader.Completion;
    public async IAsyncEnumerable<SdkUsageObservation> ReadUsageAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var usage in _usage.Reader.ReadAllAsync(cancellationToken))
            yield return Extract(usage);
    }

    private SdkUsageObservation Extract(AssistantUsageEvent usage)
    {
        try
        {
            if (_session.SessionId != Facts.SdkSessionId ||
                usage.Data.Model != Facts.ModelId ||
                usage.AgentId is not null || usage.Data.Initiator is not null ||
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

    public async ValueTask DisposeAsync()
    {
        _usage.Writer.TryComplete();
        await _session.DisposeAsync();
        await _client.DisposeAsync();
    }
}
