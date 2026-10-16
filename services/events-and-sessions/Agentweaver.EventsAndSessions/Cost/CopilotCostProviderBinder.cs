using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Agentweaver.Telemetry;
using Microsoft.Extensions.Logging;

namespace Agentweaver.EventsAndSessions.Cost;

public interface ICostProviderBinder
{
    ProviderResult<CostBinding> ResolveAndPin(string meterSource, string runId);

    ProviderResult<CostBinding> VerifyPinned(
        string meterSource, string runId, CostBinding pinnedBinding);
}

public sealed class CopilotCostProviderBinder(
    CopilotCostProvider provider,
    ProviderResolver resolver,
    ILogger<CopilotCostProviderBinder> logger) : ICostProviderBinder
{
    public ProviderResult<CostBinding> ResolveAndPin(string meterSource, string runId)
    {
        if (!string.Equals(meterSource, CopilotCostProvider.MeterSource, StringComparison.Ordinal))
            return Fail(ProviderErrorCode.MeterSourceMismatch,
                "The Copilot Cost adapter does not serve the requested meter source.");
        var request = NewRequest(
            meterSource,
            CopilotCostProvider.AdapterVersion,
            CopilotCostProvider.OptionsSchemaVersion,
            CopilotCostCapabilities.All);
        var resolution = resolver.ResolveCost(request);
        if (!resolution.IsSuccess || resolution.Value is null)
            return TraceFailure(resolution.Error);

        var negotiated = provider.Negotiate(resolution.Value.Candidate);
        if (!negotiated.IsSuccess || negotiated.Value is null)
            return TraceFailure(negotiated.Error);

        var pinned = resolver.PinCost(
            runId, resolution.Value, provider.Options.ResourceId, negotiated.Value);
        if (!pinned.IsSuccess || pinned.Value is null)
            return TraceFailure(pinned.Error);

        var binding = provider.CreateBinding(pinned.Value);
        RecordBinding(binding, "cost.provider.binding.pinned");
        return ProviderResult<CostBinding>.Success(binding);
    }

    public ProviderResult<CostBinding> VerifyPinned(
        string meterSource, string runId, CostBinding pinnedBinding)
    {
        if (pinnedBinding is null)
            return Fail(ProviderErrorCode.InvalidConfiguration, "A pinned Cost binding is required.");
        if (!string.Equals(meterSource, CopilotCostProvider.MeterSource, StringComparison.Ordinal) ||
            !string.Equals(pinnedBinding.MeterSource, meterSource, StringComparison.Ordinal))
            return TraceFailure(new ProviderError(
                ProviderErrorCode.MeterSourceMismatch,
                "The pinned Cost binding belongs to another meter source."));
        if (!Version.TryParse(pinnedBinding.AdapterVersion, out var pinnedVersion) ||
            pinnedVersion is null ||
            !string.Equals(pinnedVersion.ToString(), pinnedBinding.AdapterVersion, StringComparison.Ordinal) ||
            pinnedBinding.NegotiatedCapabilities is null)
            return TraceFailure(new ProviderError(
                ProviderErrorCode.PinnedBindingMismatch,
                "The pinned Cost binding identity is invalid."));

        var request = NewRequest(
            meterSource,
            pinnedVersion,
            pinnedBinding.OptionsSchemaVersion,
            pinnedBinding.NegotiatedCapabilities);
        var resolution = resolver.VerifyCost(request, pinnedBinding);
        if (!resolution.IsSuccess || resolution.Value is null)
            return TraceFailure(resolution.Error);

        var negotiated = provider.Negotiate(resolution.Value.Candidate);
        if (!negotiated.IsSuccess || negotiated.Value is null)
            return TraceFailure(negotiated.Error);
        var currentPin = resolver.PinCost(
            runId, resolution.Value, pinnedBinding.ResourceId, negotiated.Value);
        if (!currentPin.IsSuccess || currentPin.Value is null)
            return TraceFailure(currentPin.Error);

        CostBinding current;
        try
        {
            current = provider.CreateBinding(currentPin.Value);
        }
        catch (ArgumentException)
        {
            return TraceFailure(new ProviderError(
                ProviderErrorCode.PinnedBindingMismatch,
                "The exact pinned Cost options or resource are no longer available."));
        }

        if (!BindingsEqual(pinnedBinding, current))
            return TraceFailure(new ProviderError(
                ProviderErrorCode.PinnedBindingMismatch,
                "The exact pinned Cost provider, options, resource, or rate card has changed."));

        RecordBinding(current, "cost.provider.binding.verified");
        return ProviderResult<CostBinding>.Success(current);
    }

    private static CostProviderResolutionRequest NewRequest(
        string meterSource, Version version, int schema, ImmutableHashSet<string> capabilities) =>
        new(meterSource, version, schema, capabilities);

    private static bool BindingsEqual(CostBinding left, CostBinding right) =>
        left.MeterSource == right.MeterSource &&
        left.ProviderId == right.ProviderId &&
        left.AdapterVersion == right.AdapterVersion &&
        left.OptionsSchemaVersion == right.OptionsSchemaVersion &&
        left.OptionsRevision == right.OptionsRevision &&
        left.ResourceId == right.ResourceId &&
        left.ResourceGeneration == right.ResourceGeneration &&
        left.NegotiatedCapabilities is not null &&
        left.NegotiatedCapabilities.SetEquals(right.NegotiatedCapabilities) &&
        CopilotCostProvider.RateCardsEqual(left.RateCard, right.RateCard);

    private ProviderResult<CostBinding> TraceFailure(ProviderError? error)
    {
        var result = error ?? new ProviderError(
            ProviderErrorCode.ProviderNotFound, "The exact Cost provider is unavailable.");
        RecordFailure(result.Code);
        return ProviderResult<CostBinding>.Failure(result.Code, result.Message);
    }

    private ProviderResult<CostBinding> Fail(ProviderErrorCode code, string message)
    {
        RecordFailure(code);
        return ProviderResult<CostBinding>.Failure(code, message);
    }

    private void RecordBinding(CostBinding binding, string activityName)
    {
        try
        {
            using var activity = TelemetrySignals.Activities.StartActivity(activityName, ActivityKind.Internal);
            if (activity is null)
                return;
            activity.SetTag("cost.provider.id", binding.ProviderId);
            activity.SetTag("cost.provider.adapter_version", binding.AdapterVersion);
            activity.SetTag("cost.provider.options_schema_version", binding.OptionsSchemaVersion);
            activity.SetTag("cost.provider.options_revision", binding.OptionsRevision);
            activity.SetTag("cost.provider.meter_source", binding.MeterSource);
            activity.SetTag("cost.provider.result_code", "ok");
            activity.SetTag("cost.provider.negotiated_capabilities",
                binding.NegotiatedCapabilities.OrderBy(value => value, StringComparer.Ordinal).ToArray());
            activity.SetTag("cost.provider.resource.id_hash",
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(binding.ResourceId)))[..24]);
            activity.SetTag("cost.provider.resource.generation", binding.ResourceGeneration);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogObserverFailure("ok", exception);
        }
    }

    private void RecordFailure(ProviderErrorCode code)
    {
        try
        {
            using var activity = TelemetrySignals.Activities.StartActivity(
                "cost.provider.binding.failed", ActivityKind.Internal);
            if (activity is null)
                return;
            activity.SetTag("cost.provider.id", CopilotCostProvider.ProviderId);
            activity.SetTag("cost.provider.adapter_version", CopilotCostProvider.AdapterVersion.ToString());
            activity.SetTag("cost.provider.options_schema_version", provider.Options.OptionsSchemaVersion);
            activity.SetTag("cost.provider.options_revision", provider.Options.OptionsRevision);
            activity.SetTag("cost.provider.meter_source", CopilotCostProvider.MeterSource);
            activity.SetTag("cost.provider.result_code", code.ToString());
            activity.SetTag("cost.provider.resource.id_hash",
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(provider.Options.ResourceId)))[..24]);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogObserverFailure(code.ToString(), exception);
        }
    }

    private void LogObserverFailure(string resultCode, Exception exception)
    {
        var exceptionType = exception.GetType().Name;
        if (exceptionType.Length > 64)
            exceptionType = exceptionType[..64];
        if (resultCode.Length > 48)
            resultCode = resultCode[..48];
        logger.LogWarning(
            "Cost provider diagnostic observer failed: {ResultCode}; {FailureKind}",
            resultCode,
            exceptionType);
    }
}
