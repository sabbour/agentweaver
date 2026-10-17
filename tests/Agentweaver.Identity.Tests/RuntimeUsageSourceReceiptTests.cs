using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Xunit;

namespace Agentweaver.Identity.Tests;

public sealed class RuntimeUsageSourceReceiptTests
{
    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, 1024, false)]
    [InlineData(1024, null, false)]
    [InlineData(1024, -1, false)]
    [InlineData(1024, 0, false)]
    [InlineData(1024, 1, true)]
    [InlineData(1024, 1024, true)]
    [InlineData(1024, 1025, false)]
    [InlineData(200000, 200000, true)]
    public void NativePromptCapacityRequiresTheAcceptedBoundAndAnExactPositiveSourcePin(
        int? accepted, int? observed, bool valid)
    {
        var native = Receipt();
        var registration = native.Registration with
        {
            Binding = native.Registration.Binding with { MaxPromptTokens = accepted }
        };
        var source = native.Usage.SdkSource! with { MaxPromptTokens = observed };
        if (valid)
            RuntimeUsageSourceReceiptContract.ValidateSource(registration, source);
        else
            Assert.Equal("runtime_usage_binding_invalid",
                Assert.Throws<RuntimeAuthorizationException>(() =>
                    RuntimeUsageSourceReceiptContract.ValidateSource(registration, source)).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeSourceMustMatchTheAcceptedConcreteModelPin(bool changedModel)
    {
        var native = Receipt();
        var source = native.Usage.SdkSource!;
        var registration = native.Registration with
        {
            Binding = native.Registration.Binding with
            {
                ModelBindingPin = new(1, source.ModelSelectionReference,
                    changedModel ? "foreign-model" : source.ModelId, ModelSourceMode.HostedCopilot,
                    "model-bindings-v1", new string('a', 64))
            }
        };
        source = source with
        {
            ModelBindingPin = registration.Binding.ModelBindingPin,
            ResolvedModelBindingPin = registration.Binding.ModelBindingPin
        };
        if (changedModel)
            Assert.Equal("runtime_usage_binding_invalid",
                Assert.Throws<RuntimeAuthorizationException>(() =>
                    RuntimeUsageSourceReceiptContract.ValidateSource(registration, source)).Code);
        else
            RuntimeUsageSourceReceiptContract.ValidateSource(registration, source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObservedSnapshotRepresentsExactReceiptsWithoutMakingUnpricedUsageFree(bool unpriced)
    {
        var native = Receipt();
        var reference = AccountingReference(native, unpriced);
        var request = new RuntimeUsageCostSnapshotRequest(1, native.Registration, native.Usage.SdkSource!)
        {
            DispatchId = Guid.NewGuid(), RequiredReceipts = [reference]
        };
        var snapshot = CostSnapshot(request, 5, 1, modelRateAvailable: !unpriced) with
        {
            DispatchId = request.DispatchId, RepresentedReceipts = [reference]
        };
        RuntimeUsageCostSnapshotContract.ValidateObservedReceipt(snapshot, request);
        var roundTrip = JsonSerializer.Deserialize<RuntimeUsageCostSnapshotReceipt>(JsonSerializer.Serialize(snapshot))!;
        RuntimeUsageCostSnapshotContract.ValidateObservedReceipt(roundTrip, request);
        if (unpriced)
            Assert.Equal(0, RuntimeUsageCostSnapshotContract.ValidateReceipt(snapshot, request));
        else
            Assert.Equal(5, RuntimeUsageCostSnapshotContract.ValidateReceipt(snapshot, request));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("changed")]
    [InlineData("dispatch")]
    [InlineData("source")]
    public void ObservedSnapshotRejectsMissingForeignOrChangedAcknowledgments(string fault)
    {
        var native = Receipt();
        var reference = AccountingReference(native, false);
        var request = new RuntimeUsageCostSnapshotRequest(1, native.Registration, native.Usage.SdkSource!)
        {
            DispatchId = Guid.NewGuid(), RequiredReceipts = [reference]
        };
        var snapshot = CostSnapshot(request, 5, 1) with
        {
            DispatchId = request.DispatchId, RepresentedReceipts = [reference]
        };
        snapshot = fault switch
        {
            "missing" => snapshot with { RepresentedReceipts = [] },
            "extra" => snapshot with { RepresentedReceipts = [reference, reference] },
            "changed" => snapshot with
            {
                RepresentedReceipts = [reference with { Accounting = reference.Accounting with { Amount = 6 } }]
            },
            "dispatch" => snapshot with { DispatchId = Guid.NewGuid() },
            "source" => snapshot with { SourceHash = new string('e', 64) },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeUsageCostSnapshotContract.ValidateObservedReceipt(snapshot, request));
    }

    [Theory]
    [InlineData("default")]
    [InlineData("unscoped")]
    [InlineData("empty-dispatch")]
    [InlineData("duplicate-source")]
    [InlineData("duplicate-event")]
    [InlineData("foreign-attribution")]
    [InlineData("unpriced-amount")]
    [InlineData("unpriced-reason")]
    [InlineData("negative")]
    [InlineData("oversized")]
    public void ObservedSnapshotRequestRequiresBoundedUniqueScopedAccounting(string fault)
    {
        var native = Receipt();
        var reference = AccountingReference(native, false);
        var request = new RuntimeUsageCostSnapshotRequest(1, native.Registration, native.Usage.SdkSource!)
        {
            DispatchId = Guid.NewGuid(), RequiredReceipts = [reference]
        };
        request = fault switch
        {
            "default" => request with { RequiredReceipts = default },
            "unscoped" => request with { DispatchId = null },
            "empty-dispatch" => request with { DispatchId = Guid.Empty },
            "duplicate-source" => request with { RequiredReceipts = [reference, reference] },
            "duplicate-event" => request with
            {
                RequiredReceipts = [reference, reference with { SourceReceiptId = Guid.NewGuid() }]
            },
            "foreign-attribution" => request with
            {
                RequiredReceipts = [reference with
                {
                    Accounting = reference.Accounting with
                    {
                        Attribution = reference.Accounting.Attribution with { SessionId = "foreign" }
                    }
                }]
            },
            "unpriced-amount" => request with
            {
                RequiredReceipts = [reference with
                {
                    Accounting = reference.Accounting with
                    {
                        Disposition = CostDisposition.Unpriced, UnpricedReason = "unknown"
                    }
                }]
            },
            "unpriced-reason" => request with
            {
                RequiredReceipts = [reference with
                {
                    Accounting = reference.Accounting with { Disposition = CostDisposition.Unpriced, Amount = null }
                }]
            },
            "negative" => request with
            {
                RequiredReceipts = [reference with { Accounting = reference.Accounting with { Amount = -1 } }]
            },
            "oversized" => request with
            {
                RequiredReceipts = Enumerable.Repeat(reference,
                    RuntimeUsageCostSnapshotContract.MaximumReceiptReferences + 1).ToImmutableArray()
            },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        Assert.Throws<RuntimeAuthorizationException>(() => RuntimeUsageCostSnapshotContract.ValidateRequest(request));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeAccountedCompletionRequiresExactlyTheObservedEventSet(bool observesUsage)
    {
        var (admission, _, observation) = NativeTurn();
        var native = Receipt();
        var eventId = Guid.ParseExact(native.Usage.SdkEventId!, "D");
        observation = observation with { UsageEventIds = observesUsage ? [eventId] : [] };
        var recorded = new RuntimeNativeTurnRecordedReceipt(
            admission, observation, RuntimeNativeTurnContract.ObservationHash(observation), 3);
        var reference = AccountingReference(native, true) with
        {
            Accounting = AccountingReference(native, true).Accounting with
            {
                EventId = SdkUsageIdentity.Create(admission.Source.RuntimeInstanceId,
                    admission.Source.SdkSessionId, eventId.ToString("D")),
                Attribution = native.Usage.Attribution with
                {
                    TenantId = admission.Registration.Binding.TenantId,
                    ProjectId = admission.Registration.Binding.ProjectId,
                    RunId = admission.Registration.Binding.RunId,
                    SessionId = admission.Registration.Binding.SessionId,
                    AgentId = admission.Registration.Binding.AgentId,
                    TurnId = admission.Registration.Binding.TurnId
                }
            }
        };
        ImmutableArray<RuntimeUsageCostReceiptReference> references = observesUsage ? [reference] : [];
        var request = RuntimeNativeTurnContract.AccountingSnapshotRequest(recorded, references);
        Assert.Equal(recorded, request.NativeTurn);
        RuntimeUsageCostSnapshotContract.ValidateRequest(
            JsonSerializer.Deserialize<RuntimeUsageCostSnapshotRequest>(JsonSerializer.Serialize(request))!);
        foreach (var invalid in new[]
        {
            request with { DispatchId = Guid.NewGuid() },
            request with { Source = request.Source with { SdkVersion = "other-sdk" } },
            request with { Registration = request.Registration with { Revision = 2 } },
            request with { NativeTurn = recorded with { CanonicalPayloadHash = new string('c', 64) } },
            request with { NativeTurn = recorded with { OwnerRevision = 2 } },
            request with { RequiredReceipts = observesUsage ? [] : [reference] }
        })
            Assert.Throws<RuntimeAuthorizationException>(() => RuntimeUsageCostSnapshotContract.ValidateRequest(invalid));
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeNativeTurnContract.ValidateAccountingSnapshot(
                request, recorded with { OwnerRevision = recorded.OwnerRevision + 1 }));
        var snapshot = (observesUsage
            ? CostSnapshot(request, 0, 1, modelRateAvailable: false)
            : CostSnapshot(request, 0, 0)) with
        {
            DispatchId = admission.MessageId, RepresentedReceipts = references
        };
        var completed = new RuntimeNativeTurnAccountingReceipt(recorded, snapshot, 4);
        RuntimeNativeTurnContract.ValidateAccounted(completed, recorded, references);
        if (!observesUsage)
            Assert.Throws<RuntimeAuthorizationException>(() =>
                RuntimeNativeTurnContract.ValidateAccounted(
                    completed with
                    {
                        Snapshot = snapshot with
                        {
                            CopilotTotals = snapshot.CopilotTotals with { Events = 1 }
                        }
                    },
                    recorded, references));
        if (observesUsage)
        {
            var understatedAmount = snapshot.CopilotTotals.Amounts[0] with
            {
                Amount = 0,
                PricedEvents = 0,
                UnpricedEvents = 0
            };
            var originalAgent = snapshot.CopilotTotals.Agents[0];
            var understatedTotals = snapshot.CopilotTotals with
            {
                IsFullyPriced = false,
                Amounts = [understatedAmount],
                Agents = [originalAgent with
                {
                    IsFullyPriced = false,
                    Amounts = [understatedAmount]
                }]
            };
            Assert.Throws<RuntimeAuthorizationException>(() =>
                RuntimeNativeTurnContract.ValidateAccounted(
                    completed with { Snapshot = snapshot with { CopilotTotals = understatedTotals } },
                    recorded, references));
        }
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeNativeTurnContract.ValidateAccounted(completed with { OwnerRevision = 3 }, recorded, references));
        ImmutableArray<RuntimeUsageCostReceiptReference> wrong = observesUsage ? [] : [reference];
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeNativeTurnContract.AccountingSnapshotRequest(recorded, wrong));
    }

    // Pinned-card identity stays auditable on both priced and Unpriced accounting receipts.
    private static RuntimeUsageCostReceiptReference AccountingReference(RuntimeUsageSourceReceipt native, bool unpriced) =>
        new(native.ReceiptId, new(native.Usage.EventId, native.Usage.Attribution, new string('f', 64),
            unpriced ? CostDisposition.Unpriced : CostDisposition.Estimate,
            unpriced ? null : 5, "AIC", unpriced ? "unknown-cost" : null,
            "card", "2026-10-17", DateTimeOffset.UtcNow)
        {
            RateCardContentHash = new string('a', 64)
        });

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 2)]
    public void CostSnapshotUsesVerifiedZeroWorkQuoteAndObservedPricedTotals(decimal amount, long events)
    {
        var native = Receipt();
        var request = new RuntimeUsageCostSnapshotRequest(1, native.Registration, native.Usage.SdkSource!);
        var receipt = CostSnapshot(request, amount, events);
        Assert.Equal(amount, RuntimeUsageCostSnapshotContract.ValidateReceipt(receipt, request));
        Assert.DoesNotContain("SourceComplete", JsonSerializer.Serialize(receipt));
        Assert.DoesNotContain("UsageSubmission", JsonSerializer.Serialize(request));
        Assert.DoesNotContain("NativeTurn", JsonSerializer.Serialize(request));
        var json = JsonSerializer.Serialize(receipt);
        Assert.Equal(amount, RuntimeUsageCostSnapshotContract.ValidateReceipt(
            JsonSerializer.Deserialize<RuntimeUsageCostSnapshotReceipt>(json)!, request));
    }

    [Fact]
    public void CostSnapshotAcceptsAWellFormedUnpricedQuoteForThePinnedCard()
    {
        var native = Receipt();
        var request = new RuntimeUsageCostSnapshotRequest(1, native.Registration, native.Usage.SdkSource!);
        var receipt = CostSnapshot(request, 0, 0);
        receipt = receipt with
        {
            Quote = new(null, "AIC", CostDisposition.Unpriced, receipt.Binding!.RateCard,
                "pinned-cost-binding-unavailable")
        };

        Assert.Equal(0, RuntimeUsageCostSnapshotContract.ValidateReceipt(receipt, request));
    }

    [Fact]
    public void LegacyCostSnapshotWithoutPublishedRateFieldsStillReplays()
    {
        var native = Receipt();
        var request = new RuntimeUsageCostSnapshotRequest(1, native.Registration, native.Usage.SdkSource!);
        var scope = request.Registration.Binding;
        // Historic snapshots have no token-rate provenance or content hash.
        var card = new CostRateCard(
            "legacy-copilot-card", "v1", SdkMeterSources.CopilotNanoAiu, "AIC",
            1_000_000_000m,
            ImmutableDictionary<string, decimal>.Empty.Add(request.Source.ModelId, 1m));
        var binding = new CostBinding(
            SdkMeterSources.CopilotNanoAiu, "copilot.usage-cost", "1.0.0", 1,
            "cost-options", "cost", 1, ImmutableHashSet<string>.Empty, card);
        var amount = new UsageAmountTotal(SdkMeterSources.CopilotNanoAiu, "AIC", 0.25m, 1, 0);
        var totals = new UsageRunTotals(scope.TenantId, scope.ProjectId, scope.RunId, 1, true,
            [new(scope.AgentId, 1, null, null, null, null, null, null, true, [amount])], [amount]);
        var receipt = new RuntimeUsageCostSnapshotReceipt(
            1, RuntimeUsageSourceReceiptContract.HashSource(request.Registration, request.Source),
            binding, new(0, "AIC", CostDisposition.Estimate, card, null), totals);

        var roundTrip = JsonSerializer.Deserialize<RuntimeUsageCostSnapshotReceipt>(
            JsonSerializer.Serialize(receipt))!;

        Assert.Null(roundTrip.RateCardContentHash);
        Assert.Equal(0.25m, RuntimeUsageCostSnapshotContract.ValidateReceipt(roundTrip, request));
    }

    [Fact]
    public void LegacyUnpricedSnapshotWithOmittedAmountGroupsStillReplays()
    {
        var native = Receipt();
        var request = new RuntimeUsageCostSnapshotRequest(1, native.Registration, native.Usage.SdkSource!);
        var scope = request.Registration.Binding;
        var card = new CostRateCard(
            "legacy-copilot-card", "v1", SdkMeterSources.CopilotNanoAiu, "AIC",
            1_000_000_000m,
            ImmutableDictionary<string, decimal>.Empty.Add("gpt-6.1-sol", 1m));
        var binding = new CostBinding(
            SdkMeterSources.CopilotNanoAiu, "copilot.usage-cost", "1.0.0", 1,
            "cost-options", "cost", 1, ImmutableHashSet<string>.Empty, card);
        var totals = new UsageRunTotals(scope.TenantId, scope.ProjectId, scope.RunId, 1, false,
            [new(scope.AgentId, 1, null, null, null, null, null, null, false, [])], []);
        var legacyCardJson = new
        {
            card.Id,
            card.Version,
            card.MeterSource,
            card.Unit,
            card.NanoUnitsPerUnit,
            card.ModelMultipliers
        };
        // This fixture preserves the pre-token-rate JSON shape, including its omitted Unpriced amount groups.
        var legacyJson = JsonSerializer.Serialize(new
        {
            ContractVersion = 1,
            SourceHash = RuntimeUsageSourceReceiptContract.HashSource(request.Registration, request.Source),
            Binding = new
            {
                binding.MeterSource,
                binding.ProviderId,
                binding.AdapterVersion,
                binding.OptionsSchemaVersion,
                binding.OptionsRevision,
                binding.ResourceId,
                binding.ResourceGeneration,
                binding.NegotiatedCapabilities,
                RateCard = legacyCardJson
            },
            Quote = new
            {
                Amount = (decimal?)null,
                Unit = "AIC",
                Disposition = CostDisposition.Unpriced,
                RateCard = legacyCardJson,
                UnpricedReason = "model-rate-unavailable"
            },
            CopilotTotals = totals
        });
        var snapshot = JsonSerializer.Deserialize<RuntimeUsageCostSnapshotReceipt>(legacyJson)!;

        Assert.DoesNotContain("ModelTokenRates", legacyJson);
        Assert.DoesNotContain("RateCardContentHash", legacyJson);
        Assert.Equal(0, RuntimeUsageCostSnapshotContract.ValidateReceipt(snapshot, request));
    }

    [Fact]
    public void CostSnapshotCarriesAndValidatesTheExactRateCardContentHash()
    {
        var native = Receipt();
        var request = new RuntimeUsageCostSnapshotRequest(1, native.Registration, native.Usage.SdkSource!);
        var receipt = CostSnapshot(request, 0, 0, includeContentHash: true);
        var roundTrip = JsonSerializer.Deserialize<RuntimeUsageCostSnapshotReceipt>(
            JsonSerializer.Serialize(receipt))!;

        Assert.Equal(new string('a', 64), roundTrip.RateCardContentHash);
        Assert.Equal(0, RuntimeUsageCostSnapshotContract.ValidateReceipt(roundTrip, request));
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeUsageCostSnapshotContract.ValidateReceipt(
                roundTrip with { RateCardContentHash = new string('b', 64) }, request));
    }

    [Fact]
    public void PublishedRateCardSnapshotRejectsRemovedCardAndReceiptHashes()
    {
        var native = Receipt();
        var reference = AccountingReference(native, unpriced: false);
        var request = new RuntimeUsageCostSnapshotRequest(1, native.Registration, native.Usage.SdkSource!)
        {
            DispatchId = Guid.NewGuid(),
            RequiredReceipts = [reference]
        };
        var snapshot = CostSnapshot(request, 5, 1) with
        {
            DispatchId = request.DispatchId,
            RepresentedReceipts = [reference]
        };
        var tampered = snapshot with
        {
            RateCardContentHash = null,
            Binding = snapshot.Binding! with
            {
                RateCard = snapshot.Binding!.RateCard with { ContentHash = null }
            },
            Quote = snapshot.Quote with
            {
                RateCard = snapshot.Quote.RateCard! with { ContentHash = null }
            },
            RepresentedReceipts =
            [
                reference with
                {
                    Accounting = reference.Accounting with { RateCardContentHash = null }
                }
            ]
        };

        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeUsageCostSnapshotContract.ValidateReceipt(tampered, request));
    }

    [Fact]
    public void CostReconciliationBindsTheExactRateCardContentHash()
    {
        var native = Receipt();
        var scope = native.Registration.Binding;
        var hash = new string('a', 64);
        var reference = AccountingReference(native, unpriced: false) with
        {
            Accounting = AccountingReference(native, unpriced: false).Accounting with
            {
                RateCardContentHash = hash
            }
        };
        var request = new RuntimeUsageCostReconciliationRequest(
            1, native.Usage.A2AMessageId!.Value, native.Registration.RuntimeInstanceId,
            native.Registration.Revision, scope.ExecutionFence, scope.SessionId, scope.TurnId!,
            scope.AcceptedSelectionHash, scope.ModelSelectionReference!, native.Usage.ModelBinding.ModelId,
            native.Usage.ModelBinding.MeterSource, reference.Accounting.RateCardId!,
            reference.Accounting.RateCardVersion!, reference.Accounting.Unit!, 0, [reference])
        {
            RateCardContentHash = hash
        };
        var amount = new UsageAmountTotal(SdkMeterSources.CopilotNanoAiu, "AIC", 5, 1, 0);
        var totals = new UsageRunTotals(scope.TenantId, scope.ProjectId, scope.RunId, 1, true,
            [new(scope.AgentId, 1, null, null, null, null, null, null, true, [amount])], [amount]);
        var receipt = new RuntimeUsageCostReconciliationReceipt(
            1, request.CostTurnId, request.RuntimeInstanceId, request.RegistrationRevision,
            request.ExecutionFence, scope.TenantId, scope.ProjectId, scope.RunId, request.SessionId,
            request.RuntimeTurnId, request.AcceptedSelectionHash, request.ModelSelectionReference,
            request.ModelId, request.MeterSource, request.RateCardId, request.RateCardVersion,
            request.RateCardUnit, 1, [reference], totals)
        {
            RateCardContentHash = hash
        };

        RuntimeUsageCostReconciliationContract.ValidateRequest(request);
        RuntimeUsageCostReconciliationContract.ValidateReceipt(receipt, request,
            scope.TenantId, scope.ProjectId, scope.RunId);
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeUsageCostReconciliationContract.ValidateReceipt(
                receipt with { RateCardContentHash = new string('b', 64) }, request,
                scope.TenantId, scope.ProjectId, scope.RunId));
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeUsageCostReconciliationContract.ValidateRequest(
                request with { RateCardContentHash = new string('b', 64) }));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("binding")]
    [InlineData("quote")]
    [InlineData("quote-unpriced")]
    [InlineData("divisor")]
    [InlineData("model")]
    [InlineData("scope")]
    [InlineData("unpriced")]
    [InlineData("meter")]
    [InlineData("unit")]
    [InlineData("negative")]
    [InlineData("missing-amount")]
    [InlineData("missing-agent")]
    [InlineData("tampered-total")]
    [InlineData("unpriced-total-amount")]
    [InlineData("unpriced-omitted-amounts")]
    public void MalformedOrTamperedCostSnapshotFailsClosed(string fault)
    {
        var native = Receipt();
        var request = new RuntimeUsageCostSnapshotRequest(1, native.Registration, native.Usage.SdkSource!);
        var receipt = CostSnapshot(request, 5, 2, modelRateAvailable: fault != "unpriced-total-amount");
        receipt = fault switch
        {
            "source" => receipt with { SourceHash = new string('c', 64) },
            "binding" => receipt with { Binding = null },
            "quote" => receipt with { Quote = receipt.Quote with { Amount = null } },
            "quote-unpriced" => receipt with { Quote = receipt.Quote with { Disposition = CostDisposition.Unpriced } },
            "divisor" => receipt with
            {
                Binding = receipt.Binding! with { RateCard = receipt.Binding!.RateCard with { NanoUnitsPerUnit = 0 } }
            },
            "model" => receipt with
            {
                Binding = receipt.Binding! with
                {
                    RateCard = receipt.Binding!.RateCard with
                    {
                        ModelTokenRates = ImmutableDictionary<string, CostModelTokenRates>.Empty
                    }
                }
            },
            "scope" => receipt with { CopilotTotals = receipt.CopilotTotals with { RunId = "foreign-run" } },
            "unpriced" => receipt with { CopilotTotals = receipt.CopilotTotals with { IsFullyPriced = false } },
            "meter" => receipt with
            {
                CopilotTotals = receipt.CopilotTotals with { Amounts = [new("azure", "AIC", 5, 2, 0)] }
            },
            "unit" => receipt with
            {
                CopilotTotals = receipt.CopilotTotals with { Amounts = [new(SdkMeterSources.CopilotNanoAiu, "USD", 5, 2, 0)] }
            },
            "negative" => receipt with
            {
                CopilotTotals = receipt.CopilotTotals with { Amounts = [new(SdkMeterSources.CopilotNanoAiu, "AIC", -1, 2, 0)] }
            },
            "missing-amount" => receipt with { CopilotTotals = receipt.CopilotTotals with { Amounts = [] } },
            "missing-agent" => receipt with { CopilotTotals = receipt.CopilotTotals with { Agents = [] } },
            "tampered-total" => receipt with
            {
                CopilotTotals = receipt.CopilotTotals with
                {
                    Amounts = receipt.CopilotTotals.Amounts.SetItem(
                        0, receipt.CopilotTotals.Amounts[0] with { Amount = 0 })
                }
            },
            "unpriced-total-amount" => receipt with
            {
                CopilotTotals = receipt.CopilotTotals with
                {
                    Amounts = [receipt.CopilotTotals.Amounts[0] with { Amount = 1 }],
                    Agents = [receipt.CopilotTotals.Agents[0] with
                    {
                        Amounts = [receipt.CopilotTotals.Agents[0].Amounts[0] with
                        {
                            Amount = 1
                        }]
                    }]
                }
            },
            "unpriced-omitted-amounts" => receipt with
            {
                Quote = new(null, "AIC", CostDisposition.Unpriced, receipt.Binding!.RateCard,
                    "model-rate-unavailable"),
                CopilotTotals = receipt.CopilotTotals with
                {
                    IsFullyPriced = false,
                    Amounts = [],
                    Agents = [receipt.CopilotTotals.Agents[0] with
                    {
                        IsFullyPriced = false,
                        Amounts = []
                    }]
                }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeUsageCostSnapshotContract.ValidateReceipt(receipt, request));
    }

    [Fact]
    public void CostSnapshotRejectsTotalsThatUnderstateARepresentedPricedReceipt()
    {
        var native = Receipt();
        var reference = AccountingReference(native, unpriced: false);
        var request = new RuntimeUsageCostSnapshotRequest(1, native.Registration, native.Usage.SdkSource!)
        {
            DispatchId = Guid.NewGuid(), RequiredReceipts = [reference]
        };
        var receipt = CostSnapshot(request, 5, 1) with
        {
            DispatchId = request.DispatchId,
            RepresentedReceipts = [reference]
        };
        var originalAmount = receipt.CopilotTotals.Amounts[0];
        var understatedAmount = originalAmount with
        {
            Amount = 0,
            PricedEvents = 0,
            UnpricedEvents = 1
        };
        var originalAgent = receipt.CopilotTotals.Agents[0];
        var totals = receipt.CopilotTotals with
        {
            IsFullyPriced = false,
            Amounts = [understatedAmount],
            Agents = [originalAgent with
            {
                IsFullyPriced = false,
                Amounts = [understatedAmount]
            }]
        };

        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeUsageCostSnapshotContract.ValidateReceipt(
                receipt with { CopilotTotals = totals }, request));
    }

    [Fact]
    public void CostSnapshotRejectsPricedReceiptWithoutAmount()
    {
        var native = Receipt();
        var reference = AccountingReference(native, unpriced: false);
        reference = reference with
        {
            Accounting = reference.Accounting with { Amount = null }
        };
        var request = new RuntimeUsageCostSnapshotRequest(1, native.Registration, native.Usage.SdkSource!)
        {
            DispatchId = Guid.NewGuid(), RequiredReceipts = [reference]
        };
        var receipt = CostSnapshot(request, 5, 1) with
        {
            DispatchId = request.DispatchId, RepresentedReceipts = [reference]
        };

        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeUsageCostSnapshotContract.ValidateReceipt(receipt, request));
    }

    private static RuntimeUsageCostSnapshotReceipt CostSnapshot(
        RuntimeUsageCostSnapshotRequest request, decimal amount, long events, bool modelRateAvailable = true,
        bool includeContentHash = true)
    {
        var card = PublishedCard(
            modelRateAvailable ? request.Source.ModelId : "gpt-6.1-sol", includeContentHash);
        var binding = new CostBinding(SdkMeterSources.CopilotNanoAiu, "copilot.usage-cost", "1.0.0", 1,
            "cost-options", "cost", 1, ImmutableHashSet<string>.Empty, card);
        var scope = request.Registration.Binding;
        var quoted = modelRateAvailable
            ? new CostPrice(0, "AIC", CostDisposition.Estimate, card, null)
            : new CostPrice(null, "AIC", CostDisposition.Unpriced, card, "model-rate-unavailable");
        ImmutableArray<UsageAmountTotal> amounts = events == 0
            ? ImmutableArray<UsageAmountTotal>.Empty
            : [new(SdkMeterSources.CopilotNanoAiu, "AIC",
                modelRateAvailable ? amount : 0, modelRateAvailable ? events : 0, modelRateAvailable ? 0 : events)];
        return new(1, RuntimeUsageSourceReceiptContract.HashSource(request.Registration, request.Source),
            binding, quoted,
            new(scope.TenantId, scope.ProjectId, scope.RunId, events, modelRateAvailable,
                events == 0 ? [] :
                    [new(scope.AgentId, events, null, null, null, null, null, null, modelRateAvailable, amounts)],
                amounts))
        {
            RateCardContentHash = card.ContentHash
        };
    }

    /// <summary>Builds a test card from GitHub-published rates for the two configured Copilot models.</summary>
    private static CostRateCard PublishedCard(string modelId, bool includeContentHash = true)
    {
        var tokenRates = modelId switch
        {
            "gpt-6.1-sol" => new CostModelTokenRates(
                new CostTokenRates(2m, 0.1m, 2.5m, 10m),
                new CostTokenRates(4m, 0.2m, 5m, 15m)),
            "gpt-6-luna" => new CostModelTokenRates(
                new CostTokenRates(0.1m, 0.01m, 0.125m, 0.5m),
                new CostTokenRates(0.2m, 0.02m, 0.25m, 0.75m)),
            _ => throw new ArgumentOutOfRangeException(nameof(modelId), modelId,
                "No GitHub-published test rate is defined for this model.")
        };
        return new("card", "2026-10-17", SdkMeterSources.CopilotNanoAiu, "AIC", 1_000_000_000m,
            ImmutableDictionary<string, decimal>.Empty)
        {
            ProviderSourceUrl = "https://docs.github.com/en/copilot/reference/copilot-billing/models-and-pricing",
            SnapshotVersion = "2026-10-17",
            RetrievedOn = new DateOnly(2026, 10, 17),
            TokenRateCurrency = "USD",
            TokensPerRateUnit = 1_000_000m,
            CreditsPerCurrencyUnit = 100m,
            LongContextThresholdTokens = 272_000,
            ContentHash = includeContentHash ? new string('a', 64) : null,
            ModelTokenRates = ImmutableDictionary<string, CostModelTokenRates>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(modelId, tokenRates)
        };
    }

    [Fact]
    public void NativeAdmissionBindsPlatformIntentBeforeAnyNativeIdsExist()
    {
        var (admission, message, observation) = NativeTurn();
        RuntimeNativeTurnContract.ValidateAdmission(
            admission, admission.Registration, admission.Source, message);
        var json = JsonSerializer.Serialize(admission);
        Assert.DoesNotContain("NativeMessageId", json);
        Assert.DoesNotContain("NativeTurnId", json);
        Assert.NotEqual(admission.MessageId.ToString("D"), observation.NativeMessageId);
        foreach (var altered in new[]
        {
            admission with { MessageId = Guid.NewGuid() },
            admission with { PromptHash = new string('c', 64) },
            admission with { RequestHash = new string('d', 64) },
            admission with { Source = admission.Source with { ModelId = "another-model" } },
            admission with { Registration = admission.Registration with { Revision = 2 } },
            admission with { OwnerRevision = 1 }
        })
            Assert.Throws<RuntimeAuthorizationException>(() => RuntimeNativeTurnContract.ValidateAdmission(
                altered, admission.Registration, admission.Source, message));
        var wrongPurpose = message with
        {
            Message = message.Message with
            {
                Metadata = message.Message.Metadata with
                {
                    Runtime = message.Message.Metadata.Runtime with { Purpose = RuntimeCredentialPurpose.Configure }
                }
            }
        };
        Assert.Throws<RuntimeAuthorizationException>(() => RuntimeNativeTurnContract.ValidateAdmission(
            admission with { RequestHash = RuntimeNativeTurnContract.RequestHash(wrongPurpose) },
            admission.Registration, admission.Source, wrongPurpose));
        Assert.Throws<RuntimeAuthorizationException>(() => RuntimeNativeTurnContract.ValidateAdmission(
            admission, admission.Registration, admission.Source,
            message with { Message = message.Message with { Parts = default } }));
    }

    [Fact]
    public void NativeObservationCarriesActualRangeAndOnlyAnObservedAccountingFloor()
    {
        var (admission, _, observation) = NativeTurn();
        var checkpoint = new RuntimeNativeAccountingCheckpoint(Guid.NewGuid(), 5m,
            ImmutableDictionary<string, long>.Empty.Add(admission.Source.SdkSessionId, 7));
        observation = observation with { AccountingCheckpoint = checkpoint };
        RuntimeNativeTurnContract.ValidateObservation(admission, observation);
        var receipt = new RuntimeNativeTurnRecordedReceipt(
            admission, observation, RuntimeNativeTurnContract.ObservationHash(observation), 3);
        RuntimeNativeTurnContract.ValidateRecorded(receipt);
        Assert.DoesNotContain("SourceComplete", JsonSerializer.Serialize(receipt));
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeNativeTurnContract.ValidateRecorded(receipt with { OwnerRevision = 2 }));
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeNativeTurnContract.ValidateRecorded(receipt with
            {
                Observation = observation with { OutputHash = new string('d', 64) }
            }));
        Assert.NotEqual(receipt.CanonicalPayloadHash, RuntimeNativeTurnContract.ObservationHash(
            observation with { UsageEventIds = [Guid.NewGuid()] }));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RuntimeNativeTurnObservation>(
            JsonSerializer.Serialize(observation)[..^1] + ",\"SourceComplete\":true}"));
    }

    [Fact]
    public void NativeObservationHashSurvivesWatermarkOrderAndSerializerRoundTrips()
    {
        var (admission, _, observation) = NativeTurn();
        observation = observation with
        {
            AccountingCheckpoint = new(Guid.NewGuid(), 5m,
                ImmutableDictionary<string, long>.Empty.Add("source-z", 7).Add("source-a", 9))
        };
        var first = RuntimeNativeTurnContract.ObservationHash(observation);
        var reordered = observation with
        {
            AccountingCheckpoint = observation.AccountingCheckpoint with
            {
                SourceWatermarks = ImmutableDictionary<string, long>.Empty.Add("source-a", 9).Add("source-z", 7)
            }
        };
        RuntimeNativeTurnContract.ValidateObservation(admission, reordered);
        Assert.Equal(first, RuntimeNativeTurnContract.ObservationHash(reordered));
        var roundTrip = JsonSerializer.Deserialize<RuntimeNativeTurnObservation>(
            JsonSerializer.Serialize(reordered, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(first, RuntimeNativeTurnContract.ObservationHash(roundTrip));
        Assert.Equal(first, RuntimeNativeTurnContract.ObservationHash(
            observation with { CompletedAt = observation.CompletedAt.ToOffset(TimeSpan.FromHours(3)) }));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("message")]
    [InlineData("turn")]
    [InlineData("cursor")]
    [InlineData("overlap")]
    [InlineData("usage-duplicate")]
    [InlineData("usage-boundary")]
    [InlineData("usage-limit")]
    [InlineData("floor")]
    public void InvalidNativeObservationCannotBecomeADurableOwnerReceipt(string fault)
    {
        var (admission, _, observation) = NativeTurn();
        observation = fault switch
        {
            "source" => observation with { Source = observation.Source with { RuntimeInstanceId = Guid.NewGuid() } },
            "message" => observation with { NativeMessageId = Guid.Empty.ToString("D") },
            "turn" => observation with { NativeTurnId = "\0" },
            "cursor" => observation with { DurableCursor = "native\0cursor" },
            "overlap" => observation with { NativeTurnEndEventId = observation.NativeTurnStartEventId },
            "usage-duplicate" => observation with { UsageEventIds = [observation.UsageEventIds[0], observation.UsageEventIds[0]] },
            "usage-boundary" => observation with { UsageEventIds = [observation.NativeTurnEndEventId] },
            "usage-limit" => observation with { UsageEventIds = Enumerable.Range(0, 513).Select(_ => Guid.NewGuid()).ToImmutableArray() },
            "floor" => observation with
            {
                AccountingCheckpoint = new(Guid.NewGuid(), -1m,
                    ImmutableDictionary<string, long>.Empty.Add(admission.Source.SdkSessionId, 7))
            },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeNativeTurnContract.ValidateObservation(admission, observation));
    }

    [Fact]
    public void HardLimitDenialAndNoSendReceiptsBindActualSourceAndAnEmptyCompleteRange()
    {
        var usageReceipt = Receipt();
        var registration = usageReceipt.Registration with
        {
            Binding = usageReceipt.Registration.Binding with { CopilotHardCreditLimit = 5m }
        };
        var source = usageReceipt.Usage.SdkSource!;
        var sourceReceipt = new RuntimeSdkSourceReceipt(
            registration.RuntimeInstanceId, registration.Revision, Guid.NewGuid(), source,
            RuntimeUsageSourceReceiptContract.HashSource(registration, source), DateTimeOffset.UtcNow);
        const string checkpointHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var dispatchId = Guid.NewGuid();
        var registrationHash = RuntimeContractValidation.RegistrationHash(registration);
        var sourceHash = RuntimeUsageSourceReceiptContract.HashSource(registration, source);
        var denial = RuntimeNativeDenialContract.SealDenial(new(
            1, registration.RuntimeInstanceId, registration.Revision, registration.Binding.ExecutionFence,
            sourceReceipt.SourceGrantId,
            dispatchId, RuntimeCreditLimitDenialStage.NativeTurnBegin,
            RuntimeCreditLimitDenialCode.HardLimitReached,
            registrationHash, sourceHash,
            RuntimeNativeDenialContract.DispatchHash(
                dispatchId, registrationHash, sourceHash, checkpointHash, "prepared", 1),
            checkpointHash, 5m, 5m, 3, new string('b', 64), new string('c', 64),
            2, 3, "prepared", 1, "prepared", 1, DateTimeOffset.UtcNow, string.Empty));
        RuntimeNativeDenialContract.ValidateDenial(denial, registration, sourceReceipt);

        var cursorHash = new string('d', 64);
        var evidence = RuntimeNativeDenialContract.SealNoSend(new(
            1, registration.RuntimeInstanceId, registration.Revision, dispatchId,
            denial.CanonicalPayloadHash, denial.RegistrationHash, denial.SourceHash,
            RuntimeContractValidation.Hash(System.Text.Encoding.UTF8.GetBytes(source.SdkSessionId)),
            cursorHash, cursorHash, 0, true, DateTimeOffset.UtcNow, string.Empty));
        RuntimeNativeDenialContract.ValidateNoSend(evidence, denial, registration, sourceReceipt);

        var roundTrip = JsonSerializer.Deserialize<RuntimeNativeTurnNoSendReceipt>(
            JsonSerializer.Serialize(evidence))!;
        RuntimeNativeDenialContract.ValidateNoSend(roundTrip, denial, registration, sourceReceipt);
        Assert.DoesNotContain("credential", JsonSerializer.Serialize(evidence), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", JsonSerializer.Serialize(denial), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DenialReceiptRejectsBelowLimitChangedFenceOrPossibleDispatch()
    {
        var usageReceipt = Receipt();
        var registration = usageReceipt.Registration with
        {
            Binding = usageReceipt.Registration.Binding with { CopilotHardCreditLimit = 5m }
        };
        var sourceReceipt = new RuntimeSdkSourceReceipt(
            registration.RuntimeInstanceId, registration.Revision, Guid.NewGuid(),
            usageReceipt.Usage.SdkSource!,
            RuntimeUsageSourceReceiptContract.HashSource(registration, usageReceipt.Usage.SdkSource!),
            DateTimeOffset.UtcNow);
        var denial = NativeCreditDenial(registration, sourceReceipt);

        foreach (var invalid in new[]
        {
            RuntimeNativeDenialContract.SealDenial(denial with { ObservedCredits = 4m }),
            RuntimeNativeDenialContract.SealDenial(denial with { GuardRevisionAfter = 4 }),
            RuntimeNativeDenialContract.SealDenial(denial with { DispatchStateAfter = "sending" }),
            RuntimeNativeDenialContract.SealDenial(denial with { DispatchRevisionAfter = 2 }),
            RuntimeNativeDenialContract.SealDenial(denial with { RegistrationHash = new string('e', 64) })
        })
            Assert.Throws<RuntimeAuthorizationException>(() =>
                RuntimeNativeDenialContract.ValidateDenial(invalid, registration, sourceReceipt));
    }

    [Fact]
    public void NoSendReceiptRejectsIncompleteOrNonemptyNativeJournalRange()
    {
        var usageReceipt = Receipt();
        var registration = usageReceipt.Registration with
        {
            Binding = usageReceipt.Registration.Binding with { CopilotHardCreditLimit = 5m }
        };
        var source = usageReceipt.Usage.SdkSource!;
        var sourceReceipt = new RuntimeSdkSourceReceipt(
            registration.RuntimeInstanceId, registration.Revision, Guid.NewGuid(), source,
            RuntimeUsageSourceReceiptContract.HashSource(registration, source), DateTimeOffset.UtcNow);
        var denial = NativeCreditDenial(
            registration, sourceReceipt, RuntimeCreditLimitDenialStage.NativeTurnBegin);
        var valid = RuntimeNativeDenialContract.SealNoSend(new(
            1, registration.RuntimeInstanceId, registration.Revision, denial.DispatchId,
            denial.CanonicalPayloadHash, denial.RegistrationHash, denial.SourceHash,
            RuntimeContractValidation.Hash(System.Text.Encoding.UTF8.GetBytes(source.SdkSessionId)),
            new string('d', 64), new string('d', 64), 0, true,
            DateTimeOffset.UtcNow, string.Empty));
        RuntimeNativeDenialContract.ValidateNoSend(valid, denial, registration, sourceReceipt);

        foreach (var invalid in new[]
        {
            RuntimeNativeDenialContract.SealNoSend(valid with { ObservedEventCount = 1 }),
            RuntimeNativeDenialContract.SealNoSend(valid with { RangeComplete = false }),
            RuntimeNativeDenialContract.SealNoSend(valid with { AfterCursorHash = new string('e', 64) }),
            RuntimeNativeDenialContract.SealNoSend(valid with { DenialReceiptHash = new string('f', 64) })
        })
            Assert.Throws<RuntimeAuthorizationException>(() =>
            {
                RuntimeNativeDenialContract.ValidateNoSend(invalid, denial, registration, sourceReceipt);
            });
    }

    [Fact]
    public void FixedCreditGuardReceiptRetainsRegistrationAndBindsTheExactActionRequest()
    {
        var native = Receipt();
        var registration = native.Registration with
        {
            Binding = native.Registration.Binding with { CopilotHardCreditLimit = 0m }
        };
        var action = new RuntimeActionRequest(
            1, registration.RuntimeInstanceId, registration.Revision,
            registration.Binding.ExecutionFence, Guid.NewGuid(), "model.turn",
            RuntimeContractValidation.Hash("bounded input"u8));
        var receipt = RuntimeFixedCreditGuardContract.Seal(new RuntimeFixedCreditGuardReceipt(
            1, RuntimeFixedCreditGuardStage.ActionAuthorize, RuntimeFixedCreditGuardCode.HardLimitZero,
            registration, RuntimeContractValidation.RegistrationHash(registration), action.EventId,
            RuntimeActionContract.Hash(action), null, null, null, DateTimeOffset.UtcNow, string.Empty));

        RuntimeFixedCreditGuardContract.ValidateReceipt(receipt);
        Assert.Equal(registration, receipt.Registration);
        Assert.Equal(action.EventId, receipt.RequestId);
        Assert.Equal(RuntimeActionContract.Hash(action), receipt.RequestHash);
        Assert.DoesNotContain("bounded input", JsonSerializer.Serialize(receipt), StringComparison.Ordinal);
        Assert.DoesNotContain("credential", JsonSerializer.Serialize(receipt), StringComparison.OrdinalIgnoreCase);

        var hostReceiptWithoutSource = RuntimeFixedCreditGuardContract.Seal(receipt with
        {
            Stage = RuntimeFixedCreditGuardStage.HostPreQueue
        });
        Assert.Throws<RuntimeAuthorizationException>(() =>
        {
            RuntimeFixedCreditGuardContract.ValidateReceipt(hostReceiptWithoutSource);
        });
        Assert.Throws<RuntimeAuthorizationException>(() =>
        {
            RuntimeFixedCreditGuardContract.ValidateReceipt(
                RuntimeFixedCreditGuardContract.Seal(receipt with { SourceHash = new string('a', 64) }));
        });
        Assert.Throws<RuntimeAuthorizationException>(() =>
        {
            RuntimeFixedCreditGuardContract.ValidateReceipt(RuntimeFixedCreditGuardContract.Seal(receipt with
            {
                Registration = registration with
                {
                    Binding = registration.Binding with { CopilotHardCreditLimit = 1m }
                }
            }));
        });
    }

    [Fact]
    public void FixedCreditNoSendEvidenceRequiresCurrentGrantAndCompleteEmptySdkRange()
    {
        var native = Receipt();
        var registration = native.Registration with
        {
            Binding = native.Registration.Binding with { CopilotHardCreditLimit = 0m }
        };
        var source = native.Usage.SdkSource!;
        var grant = new RuntimeGrantReceipt(
            Guid.NewGuid(), registration.RuntimeInstanceId, registration.Revision, 7,
            registration.Binding.ActorIssuer, RuntimeCredentialPurpose.Observe,
            registration.Binding.ObservationEndpoint, RuntimeCredentialState.Active,
            new string('a', 64), registration.ExpiresAt, DateTimeOffset.UtcNow);
        var sourceHash = RuntimeUsageSourceReceiptContract.HashSource(registration, source);
        var sourceReceipt = new RuntimeSdkSourceReceipt(
            registration.RuntimeInstanceId, registration.Revision, grant.GrantId,
            source, sourceHash, DateTimeOffset.UtcNow);
        var messageId = Guid.NewGuid();
        var request = new RuntimeA2ASendRequest(new(
            "message", messageId, source.SdkSessionId, "user",
            [new("text", "A protected prompt.")],
            new(new(1, registration, grant.GrantId, grant.Revision, RuntimeCredentialPurpose.Observe),
                AddressedMessageDeliveryMode.Immediate)));
        var requestHash = RuntimeFixedCreditGuardContract.HashHostRequest(request);
        var receipt = RuntimeFixedCreditGuardContract.Seal(new RuntimeFixedCreditGuardReceipt(
            1, RuntimeFixedCreditGuardStage.HostPreQueue, RuntimeFixedCreditGuardCode.HardLimitZero,
            registration, RuntimeContractValidation.RegistrationHash(registration), messageId,
            requestHash, sourceHash, grant.GrantId, grant.Revision, DateTimeOffset.UtcNow, string.Empty));
        RuntimeFixedCreditGuardContract.ValidateSourceTuple(receipt, registration, grant, sourceReceipt);
        var cursorHash = new string('d', 64);
        var evidence = RuntimeFixedCreditGuardContract.Seal(new RuntimeFixedCreditNoSendReceipt(
            1, registration.RuntimeInstanceId, registration.Revision, registration.Binding.ExecutionFence,
            messageId, RuntimeFixedCreditGuardStage.HostPreQueue, receipt.CanonicalPayloadHash,
            receipt.RegistrationHash, sourceHash, grant.GrantId, grant.Revision,
            RuntimeContractValidation.Hash(System.Text.Encoding.UTF8.GetBytes(source.SdkSessionId)),
            cursorHash, cursorHash, 0, true, DateTimeOffset.UtcNow, string.Empty));

        RuntimeFixedCreditGuardContract.ValidateEvidence(
            evidence, receipt, registration, grant, sourceReceipt);
        Assert.NotEqual(registration.Revision, evidence.SourceGrantRevision);
        Assert.DoesNotContain("A protected prompt", JsonSerializer.Serialize(receipt), StringComparison.Ordinal);

        var read = new RuntimeFixedCreditGuardReadRequest(
            null, registration.RuntimeInstanceId, registration.Revision,
            registration.Binding.ExecutionFence, receipt.Stage, receipt.RequestId, receipt.RequestHash);
        RuntimeFixedCreditGuardContract.ValidateReadResult(
            new(receipt, null), registration, read);
        RuntimeFixedCreditGuardContract.ValidateReadResult(
            new(receipt, evidence), registration, read, grant, sourceReceipt);
        Assert.Throws<RuntimeAuthorizationException>(() =>
        {
            RuntimeFixedCreditGuardContract.ValidateReadResult(
                new(null, evidence), registration, read, grant, sourceReceipt);
        });
        Assert.Throws<RuntimeAuthorizationException>(() =>
        {
            RuntimeFixedCreditGuardContract.ValidateReadResult(
                new(receipt, evidence), registration, read with { RequestHash = new string('e', 64) },
                grant, sourceReceipt);
        });

        foreach (var invalid in new[]
        {
            RuntimeFixedCreditGuardContract.Seal(evidence with { ObservedEventCount = 1 }),
            RuntimeFixedCreditGuardContract.Seal(evidence with { RangeComplete = false }),
            RuntimeFixedCreditGuardContract.Seal(evidence with { AfterCursorHash = new string('e', 64) }),
            RuntimeFixedCreditGuardContract.Seal(evidence with { SourceGrantRevision = registration.Revision }),
            RuntimeFixedCreditGuardContract.Seal(evidence with { GuardReceiptHash = new string('f', 64) })
        })
            Assert.Throws<RuntimeAuthorizationException>(() =>
            {
                RuntimeFixedCreditGuardContract.ValidateEvidence(
                    invalid, receipt, registration, grant, sourceReceipt);
            });
    }

    [Fact]
    public void NativeDenialReadRejectsUnpairedOrWrongRequestNoSendEvidence()
    {
        var native = Receipt();
        var registration = native.Registration with
        {
            Binding = native.Registration.Binding with { CopilotHardCreditLimit = 5m }
        };
        var source = native.Usage.SdkSource!;
        var sourceReceipt = new RuntimeSdkSourceReceipt(
            registration.RuntimeInstanceId, registration.Revision, Guid.NewGuid(), source,
            RuntimeUsageSourceReceiptContract.HashSource(registration, source), DateTimeOffset.UtcNow);
        var denial = NativeCreditDenial(
            registration, sourceReceipt, RuntimeCreditLimitDenialStage.NativeTurnBegin);
        var cursorHash = new string('d', 64);
        var evidence = RuntimeNativeDenialContract.SealNoSend(new(
            1, registration.RuntimeInstanceId, registration.Revision, denial.DispatchId,
            denial.CanonicalPayloadHash, denial.RegistrationHash, denial.SourceHash,
            RuntimeContractValidation.Hash(System.Text.Encoding.UTF8.GetBytes(source.SdkSessionId)),
            cursorHash, cursorHash, 0, true, DateTimeOffset.UtcNow, string.Empty));
        var request = new RuntimeNativeCreditDenialReadRequest(
            new(Guid.NewGuid(), registration.RuntimeInstanceId, 1, RuntimeCredentialPurpose.Observe,
                registration.Binding.ObservationEndpoint, new string('a', 64),
                new string('a', 64), DateTimeOffset.UtcNow.AddMinutes(1), Guid.Empty),
            denial.DispatchId, RuntimeCreditLimitDenialStage.NativeTurnBegin);

        RuntimeNativeDenialContract.ValidateReadResult(
            new(denial), registration, sourceReceipt, denial.DispatchId, request.Stage);
        RuntimeNativeDenialContract.ValidateReadResult(
            new(denial, evidence), registration, sourceReceipt, denial.DispatchId, request.Stage);
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeNativeDenialContract.ValidateReadResult(
                new(null, evidence), registration, sourceReceipt, denial.DispatchId, request.Stage));
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeNativeDenialContract.ValidateReadResult(
                new(denial, evidence), registration, sourceReceipt, Guid.NewGuid(), request.Stage));
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeNativeDenialContract.ValidateReadResult(
                new(denial, evidence), registration, sourceReceipt, denial.DispatchId,
                RuntimeCreditLimitDenialStage.Preparation));
    }

    private static RuntimeNativeCreditDenialReceipt NativeCreditDenial(
        RuntimeRegistration registration, RuntimeSdkSourceReceipt sourceReceipt,
        RuntimeCreditLimitDenialStage stage = RuntimeCreditLimitDenialStage.Preparation)
    {
        const string checkpointHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var dispatchId = Guid.NewGuid();
        var registrationHash = RuntimeContractValidation.RegistrationHash(registration);
        var sourceHash = RuntimeUsageSourceReceiptContract.HashSource(registration, sourceReceipt.Source);
        return RuntimeNativeDenialContract.SealDenial(new(
            1, registration.RuntimeInstanceId, registration.Revision, registration.Binding.ExecutionFence,
            sourceReceipt.SourceGrantId,
            dispatchId, stage,
            RuntimeCreditLimitDenialCode.HardLimitReached,
            registrationHash, sourceHash,
            RuntimeNativeDenialContract.DispatchHash(
                dispatchId, registrationHash, sourceHash, checkpointHash, "prepared", 1),
            checkpointHash, 5m, 6m, 3, new string('b', 64), new string('c', 64),
            2, 3, "prepared", 1, "prepared", 1, DateTimeOffset.UtcNow, string.Empty));
    }

    private static (RuntimeNativeTurnAdmissionReceipt, RuntimeA2ASendRequest, RuntimeNativeTurnObservation) NativeTurn()
    {
        var receipt = Receipt();
        var registration = receipt.Registration;
        var source = receipt.Usage.SdkSource!;
        var message = new RuntimeA2ASendRequest(new("message", Guid.NewGuid(), source.SdkSessionId, "user",
            [new("text", "Actual request.")],
            new(new(1, registration, Guid.NewGuid(), 1, RuntimeCredentialPurpose.Observe),
                AddressedMessageDeliveryMode.Immediate)));
        var admission = new RuntimeNativeTurnAdmissionReceipt(registration, source, message.Message.MessageId,
            RuntimeContractValidation.Hash(System.Text.Encoding.UTF8.GetBytes("Actual request.")),
            RuntimeNativeTurnContract.RequestHash(message), 2);
        var observation = new RuntimeNativeTurnObservation(source, Guid.NewGuid().ToString("D"),
            "actual-native-turn", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            "actual-native-cursor", new string('c', 64), null) { UsageEventIds = [Guid.NewGuid()] };
        return (admission, message, observation);
    }

    [Fact]
    public void NativeReceiptBindsAllCurrentOwnerAndSdkFieldsWithoutInventingMeasurements()
    {
        var receipt = Receipt();
        RuntimeUsageSourceReceiptContract.Validate(receipt);
        Assert.Null(receipt.Usage.Measurement.RequestCount);
        Assert.Equal(5, receipt.Usage.Measurement.CacheWriteTokens);
        Assert.Equal(1234567.25m, receipt.Usage.Measurement.ProviderUnits);
        Assert.Equal(2.5m, receipt.Usage.SdkSource!.ModelMultiplier);
        Assert.NotEqual(Guid.Empty, receipt.Usage.A2AMessageId);
        foreach (var usage in new[]
        {
            receipt.Usage with { Attribution = receipt.Usage.Attribution with { TurnId = "foreign-turn" } },
            receipt.Usage with { EventId = Guid.NewGuid() },
            receipt.Usage with { SdkSource = receipt.Usage.SdkSource! with { SourceMode = "byok" } },
            receipt.Usage with { SdkSource = receipt.Usage.SdkSource! with { AcceptedSelectionHash = new string('b', 64) } },
            receipt.Usage with { SdkSource = receipt.Usage.SdkSource! with { RegistrationRevision = 2 } },
            receipt.Usage with { SdkSource = receipt.Usage.SdkSource! with { SdkSessionId = "foreign-session" } },
            receipt.Usage with { A2AMessageId = Guid.Empty },
            receipt.Usage with { Measurement = receipt.Usage.Measurement with { RequestCount = 1 } }
        })
            Assert.Throws<RuntimeAuthorizationException>(() => RuntimeUsageSourceReceiptContract.ValidateUsage(
                receipt.Registration, usage));
        var changed = receipt with
        {
            Usage = receipt.Usage with
            {
                Measurement = receipt.Usage.Measurement with { CacheWriteTokens = 6 }
            }
        };
        Assert.Throws<RuntimeAuthorizationException>(() => RuntimeUsageSourceReceiptContract.Validate(changed));
        Assert.NotEqual(receipt.CanonicalPayloadHash,
            RuntimeUsageSourceReceiptContract.Hash(changed.Registration, changed.Usage));
        Assert.NotEqual(receipt.CanonicalPayloadHash,
            RuntimeUsageSourceReceiptContract.Hash(receipt.Registration,
                receipt.Usage with { A2AMessageId = Guid.NewGuid() }));

        var legacyUsage = receipt.Usage with { A2AMessageId = null };
        var legacyJson = JsonSerializer.SerializeToElement(
            legacyUsage, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.False(legacyJson.TryGetProperty("a2aMessageId", out _));
        Assert.Equal(LegacyHash(receipt.Registration, legacyUsage),
            RuntimeUsageSourceReceiptContract.Hash(receipt.Registration, legacyUsage));
    }

    [Fact]
    public void MissingNativeMeasurementsRemainNullAndHistoricalReceiptDoesNotRequireALiveLease()
    {
        var receipt = Receipt();
        var missing = receipt.Usage with
        {
            Measurement = new(null, null, null, null, null, null, "nano_aiu", null)
        };
        var expired = receipt.Registration with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var history = new RuntimeUsageSourceReceipt(
            1, receipt.ReceiptId, expired, missing,
            RuntimeUsageSourceReceiptContract.Hash(expired, missing), receipt.RecordedAt);
        RuntimeUsageSourceReceiptContract.Validate(history);
        Assert.Null(history.Usage.Measurement.InputTokens);
        Assert.Null(history.Usage.Measurement.CacheWriteTokens);
        Assert.Null(history.Usage.Measurement.ProviderUnits);
        Assert.Null(history.Usage.Measurement.DurationMilliseconds);
        Assert.Null(history.Usage.Measurement.RequestCount);
    }

    [Fact]
    public void NativeAccountingIdentityAndAvailabilityAreImmutablePartsOfTheOwnerReceipt()
    {
        var receipt = Receipt();
        var source = receipt.Usage.SdkSource!;
        var accounting = new SdkUsageAccountingObservation(
            new(source.SdkSessionId, 17, "native-call-42"), SdkAiCreditsStatus.Complete, true);
        var usage = receipt.Usage with { SdkAccounting = accounting };
        var hash = RuntimeUsageSourceReceiptContract.Hash(receipt.Registration, usage);
        RuntimeUsageSourceReceiptContract.Validate(receipt with
        {
            Usage = usage,
            CanonicalPayloadHash = hash
        });
        Assert.NotEqual(receipt.CanonicalPayloadHash, hash);
        foreach (var changed in new[]
        {
            accounting with { Identity = accounting.Identity! with { Sequence = 18 } },
            accounting with { Identity = accounting.Identity! with { UsageId = "other-call" } },
            accounting with { AiCreditsStatus = SdkAiCreditsStatus.Partial }
        })
            Assert.Throws<RuntimeAuthorizationException>(() =>
                RuntimeUsageSourceReceiptContract.Validate(receipt with
                {
                    Usage = usage with { SdkAccounting = changed },
                    CanonicalPayloadHash = hash
                }));
    }

    [Theory]
    [InlineData("foreign-session", 17)]
    [InlineData(null, 0)]
    public void NativeAccountingCannotMoveToAnotherSourceOrAcquireAnInventedSequence(
        string? sourceSession, long sequence)
    {
        var receipt = Receipt();
        var accounting = new SdkUsageAccountingObservation(
            new(sourceSession ?? receipt.Usage.SdkSource!.SdkSessionId, sequence, "native-call-42"),
            SdkAiCreditsStatus.Complete, true);
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeUsageSourceReceiptContract.ValidateUsage(
                receipt.Registration, receipt.Usage with { SdkAccounting = accounting }));
    }

    private static RuntimeUsageSourceReceipt Receipt()
    {
        var registration = new RuntimeRegistration(
            Guid.NewGuid(), 1,
            new("https://broker.test/", Guid.NewGuid().ToString("D"), "tenant", "project", "run",
                "session", "agent", "turn", 1, 1, 1, "context:1", new string('a', 64), 1,
                "environment", "placement", 1, "profile", new("https://runtime.test/configure"),
                new("https://orchestrator.test/internal/runtime/observations"))
            {
                ModelSelectionReference = "accepted-model",
                ModelSourceMode = ModelSourceMode.HostedCopilot,
                ModelBindingPin = new(1, "accepted-model", "gpt-6-luna", ModelSourceMode.HostedCopilot,
                    "model-bindings-v1", new string('c', 64)),
                PlacementProviderId = "sandbox-platform",
                EnvironmentLifecycleGeneration = 1,
                EnvironmentLeaseRevision = 2,
                EnvironmentCurrentFencingGeneration = 3,
                EnvironmentProviderFencingGeneration = 3
            },
            RuntimeRegistrationState.Active, DateTimeOffset.UtcNow.AddMinutes(1));
        var source = new SdkSessionFacts(
            registration.RuntimeInstanceId, RuntimeContractValidation.NativeSessionId(registration.Binding),
            "1.0.11+source", "runtime-v1", "accepted-model", "gpt-6-luna", new string('b', 64),
            2.5m, "hosted-copilot", SdkMeterSources.CopilotNanoAiu, registration.Binding.AcceptedSelectionHash, 1)
        {
            ModelBindingPin = registration.Binding.ModelBindingPin,
            ResolvedModelBindingPin = registration.Binding.ModelBindingPin
        };
        var eventId = Guid.NewGuid().ToString("D");
        var observation = new SdkUsageObservation(
            SdkUsageIdentity.Create(source.RuntimeInstanceId, source.SdkSessionId, eventId),
            eventId, source.SdkSessionId, DateTimeOffset.UtcNow, source.ModelId,
            17, 11, 7, 5, 3, 1234567.25m, 12.5m)
        {
            A2AMessageId = Guid.NewGuid()
        };
        var usage = RuntimeUsageSourceReceiptContract.CreateUsage(registration, source, observation);
        return new(1, Guid.NewGuid(), registration, usage,
            RuntimeUsageSourceReceiptContract.Hash(registration, usage), DateTimeOffset.UtcNow);
    }

    [Fact]
    public void ByokTokenObservationsCannotAcquireCopilotUnitsOrHostedSourceFacts()
    {
        var hosted = Receipt();
        var registration = hosted.Registration with
        {
            Binding = hosted.Registration.Binding with
            {
                ModelSourceMode = ModelSourceMode.Byok,
                ModelBindingPin = new(1, hosted.Usage.SdkSource!.ModelSelectionReference,
                    hosted.Usage.SdkSource.ModelId, ModelSourceMode.Byok, "byok-bindings-v1", new string('a', 64))
                {
                    ProviderType = "azure"
                }
            }
        };
        var source = hosted.Usage.SdkSource! with
        {
            SourceMode = "byok", MeterSource = SdkMeterSources.ByokTokens, ModelMultiplier = null,
            ByokProvider = new("azure", hosted.Usage.SdkSource.ModelId, new string('a', 64)),
            ModelBindingPin = registration.Binding.ModelBindingPin,
            ResolvedModelBindingPin = registration.Binding.ModelBindingPin
        };
        var eventId = Guid.NewGuid().ToString("D");
        var observation = new SdkUsageObservation(
            SdkUsageIdentity.Create(source.RuntimeInstanceId, source.SdkSessionId, eventId),
            eventId, source.SdkSessionId, DateTimeOffset.UtcNow, source.ModelId,
            17, 11, 7, 5, 3, null, 12.5m);
        var usage = RuntimeUsageSourceReceiptContract.CreateUsage(registration, source, observation);
        Assert.Equal("tokens", usage.Measurement.ProviderUnit);
        Assert.Null(usage.Measurement.ProviderUnits);
        Assert.Equal(17, usage.Measurement.InputTokens);
        foreach (var invalid in new[]
        {
            usage with { Measurement = usage.Measurement with { ProviderUnits = 0 } },
            usage with { Measurement = usage.Measurement with { ProviderUnit = "nano_aiu" } },
            usage with { SdkSource = source with { SourceMode = "hosted-copilot" } },
            usage with { SdkSource = source with { ModelMultiplier = 0 } },
            usage with { SdkSource = source with { ByokProvider = null } },
            usage with { SdkSource = source with { ByokProvider = source.ByokProvider! with { Type = "openai" } } },
            usage with { SdkSource = source with { ByokProvider = source.ByokProvider! with { DeploymentId = "foreign" } } },
            usage with { SdkSource = source with { ByokProvider = source.ByokProvider! with { ConfigurationHash = new string('b', 64) } } },
            usage with { Measurement = usage.Measurement with { InputTokens = null } },
            usage with { Measurement = usage.Measurement with { OutputTokens = null } }
        })
            Assert.Throws<RuntimeAuthorizationException>(() =>
                RuntimeUsageSourceReceiptContract.ValidateUsage(registration, invalid));
    }

    private static string LegacyHash(RuntimeRegistration registration, UsageSubmission usage)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        var legacyUsage = new
        {
            usage.EventId,
            usage.OccurredAt,
            usage.Attribution,
            usage.ModelBinding,
            usage.Measurement,
            usage.SdkSource,
            usage.SdkEventId
        };
        return RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(
            new { Registration = registration, Usage = legacyUsage }, options));
    }
}
