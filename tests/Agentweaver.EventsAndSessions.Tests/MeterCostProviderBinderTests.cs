using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.EventsAndSessions.Cost;
using Agentweaver.Providers;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

public sealed class MeterCostProviderBinderTests
{
    [Fact]
    public void DispatchesCopilotAndAzureMetersToTheirExactBinders()
    {
        var copilot = new RecordingBinder();
        var azure = new RecordingBinder();
        var binder = new MeterCostProviderBinder(new Dictionary<string, ICostProviderBinder>(StringComparer.Ordinal)
        {
            [CopilotCostProvider.MeterSource] = copilot,
            [AzureCostProvider.MeterSource] = azure
        });

        var copilotBinding = binder.ResolveAndPin(CopilotCostProvider.MeterSource, "run-1");
        var azureBinding = binder.ResolveAndPin(AzureCostProvider.MeterSource, "run-2");

        Assert.True(copilotBinding.IsSuccess);
        Assert.True(azureBinding.IsSuccess);
        Assert.Equal(CopilotCostProvider.MeterSource, copilotBinding.Value!.MeterSource);
        Assert.Equal(AzureCostProvider.MeterSource, azureBinding.Value!.MeterSource);
        Assert.Equal([CopilotCostProvider.MeterSource], copilot.ResolvedMeters);
        Assert.Equal([AzureCostProvider.MeterSource], azure.ResolvedMeters);
    }

    [Fact]
    public void CopilotOnlyAndAzureOnlyConfigurationsDoNotFallBackAcrossMeters()
    {
        var copilot = new RecordingBinder();
        var copilotOnly = new MeterCostProviderBinder(new Dictionary<string, ICostProviderBinder>
        {
            [CopilotCostProvider.MeterSource] = copilot
        });
        var missingAzure = copilotOnly.ResolveAndPin(AzureCostProvider.MeterSource, "run-1");
        Assert.False(missingAzure.IsSuccess);
        Assert.Equal(ProviderErrorCode.ProviderNotFound, missingAzure.Error!.Code);
        Assert.Empty(copilot.ResolvedMeters);

        var azure = new RecordingBinder();
        var azureOnly = new MeterCostProviderBinder(new Dictionary<string, ICostProviderBinder>
        {
            [AzureCostProvider.MeterSource] = azure
        });
        var missingCopilot = azureOnly.ResolveAndPin(CopilotCostProvider.MeterSource, "run-1");
        Assert.False(missingCopilot.IsSuccess);
        Assert.Equal(ProviderErrorCode.ProviderNotFound, missingCopilot.Error!.Code);
        Assert.Empty(azure.ResolvedMeters);
    }

    [Fact]
    public void NeitherConfiguredAndUnknownCaseChangedMetersRemainProviderNotFound()
    {
        var empty = new MeterCostProviderBinder(
            new Dictionary<string, ICostProviderBinder>(StringComparer.Ordinal));
        var absent = empty.ResolveAndPin(AzureCostProvider.MeterSource, "run-1");
        Assert.False(absent.IsSuccess);
        Assert.Equal(ProviderErrorCode.ProviderNotFound, absent.Error!.Code);

        var configured = new MeterCostProviderBinder(
            new Dictionary<string, ICostProviderBinder>(StringComparer.OrdinalIgnoreCase)
            {
                [AzureCostProvider.MeterSource] = new RecordingBinder()
            });
        var wrongCase = configured.ResolveAndPin("BYOK.TOKENS", "run-1");
        Assert.False(wrongCase.IsSuccess);
        Assert.Equal(ProviderErrorCode.ProviderNotFound, wrongCase.Error!.Code);
    }

    [Fact]
    public void VerificationRequiresTheExactPinnedMeterBeforeDispatch()
    {
        var azure = new RecordingBinder();
        var binder = new MeterCostProviderBinder(new Dictionary<string, ICostProviderBinder>
        {
            [AzureCostProvider.MeterSource] = azure
        });
        var pinned = Binding(CopilotCostProvider.MeterSource);

        var result = binder.VerifyPinned(AzureCostProvider.MeterSource, "run-1", pinned);

        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderErrorCode.MeterSourceMismatch, result.Error!.Code);
        Assert.Empty(azure.VerifiedMeters);
    }

    private static CostBinding Binding(string meterSource) => new(
        meterSource,
        "test-cost-provider",
        "1.0.0",
        1,
        "test-options-v1",
        "test-resource",
        1,
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal),
        new CostRateCard(
            "test-card",
            "1",
            meterSource,
            "USD",
            1m,
            ImmutableDictionary<string, decimal>.Empty));

    private sealed class RecordingBinder : ICostProviderBinder
    {
        public List<string> ResolvedMeters { get; } = [];
        public List<string> VerifiedMeters { get; } = [];

        public ProviderResult<CostBinding> ResolveAndPin(string meterSource, string runId)
        {
            ResolvedMeters.Add(meterSource);
            return ProviderResult<CostBinding>.Success(Binding(meterSource));
        }

        public ProviderResult<CostBinding> VerifyPinned(
            string meterSource, string runId, CostBinding pinnedBinding)
        {
            VerifiedMeters.Add(meterSource);
            return ProviderResult<CostBinding>.Success(pinnedBinding);
        }
    }
}
