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
        var snapshot = CostSnapshot(request, 5, 1) with
        {
            DispatchId = request.DispatchId, RepresentedReceipts = [reference]
        };
        if (unpriced)
            snapshot = snapshot with
            {
                Binding = null,
                Quote = new(null, null, CostDisposition.Unpriced, null, "cost-provider-unavailable"),
                CopilotTotals = snapshot.CopilotTotals with { IsFullyPriced = false }
            };
        RuntimeUsageCostSnapshotContract.ValidateObservedReceipt(snapshot, request);
        var roundTrip = JsonSerializer.Deserialize<RuntimeUsageCostSnapshotReceipt>(JsonSerializer.Serialize(snapshot))!;
        RuntimeUsageCostSnapshotContract.ValidateObservedReceipt(roundTrip, request);
        if (unpriced)
            Assert.Throws<RuntimeAuthorizationException>(() =>
                RuntimeUsageCostSnapshotContract.ValidateReceipt(snapshot, request));
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
        var snapshot = CostSnapshot(request, 0, 0) with
        {
            DispatchId = admission.MessageId, RepresentedReceipts = references
        };
        var completed = new RuntimeNativeTurnAccountingReceipt(recorded, snapshot, 4);
        RuntimeNativeTurnContract.ValidateAccounted(completed, recorded, references);
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeNativeTurnContract.ValidateAccounted(completed with { OwnerRevision = 3 }, recorded, references));
        ImmutableArray<RuntimeUsageCostReceiptReference> wrong = observesUsage ? [] : [reference];
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeNativeTurnContract.AccountingSnapshotRequest(recorded, wrong));
    }

    private static RuntimeUsageCostReceiptReference AccountingReference(RuntimeUsageSourceReceipt native, bool unpriced) =>
        new(native.ReceiptId, new(native.Usage.EventId, native.Usage.Attribution, new string('f', 64),
            unpriced ? CostDisposition.Unpriced : CostDisposition.Estimate,
            unpriced ? null : 5, unpriced ? null : "AIC", unpriced ? "unknown-cost" : null,
            unpriced ? null : "card", unpriced ? null : "1", DateTimeOffset.UtcNow));

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
        var json = JsonSerializer.Serialize(receipt);
        Assert.Equal(amount, RuntimeUsageCostSnapshotContract.ValidateReceipt(
            JsonSerializer.Deserialize<RuntimeUsageCostSnapshotReceipt>(json)!, request));
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
    public void MissingOrUnpricedCostSnapshotCannotBecomeFreeAdmission(string fault)
    {
        var native = Receipt();
        var request = new RuntimeUsageCostSnapshotRequest(1, native.Registration, native.Usage.SdkSource!);
        var receipt = CostSnapshot(request, 5, 2);
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
                    RateCard = receipt.Binding!.RateCard with { ModelMultipliers = ImmutableDictionary<string, decimal>.Empty }
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
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeUsageCostSnapshotContract.ValidateReceipt(receipt, request));
    }

    private static RuntimeUsageCostSnapshotReceipt CostSnapshot(
        RuntimeUsageCostSnapshotRequest request, decimal amount, long events)
    {
        var card = new CostRateCard("card", "1", SdkMeterSources.CopilotNanoAiu, "AIC", 1_000_000_000m,
            ImmutableDictionary<string, decimal>.Empty.Add(request.Source.ModelId, 2.5m));
        var binding = new CostBinding(SdkMeterSources.CopilotNanoAiu, "copilot.usage-cost", "1.0.0", 1,
            "cost-options", "cost", 1, ImmutableHashSet<string>.Empty, card);
        var scope = request.Registration.Binding;
        ImmutableArray<UsageAmountTotal> amounts =
            events == 0 ? [] : [new(SdkMeterSources.CopilotNanoAiu, "AIC", amount, events, 0)];
        return new(1, RuntimeUsageSourceReceiptContract.HashSource(request.Registration, request.Source),
            binding, new(0, "AIC", CostDisposition.Estimate, card, null),
            new(scope.TenantId, scope.ProjectId, scope.RunId, events, true,
                events == 0 ? [] : [new(scope.AgentId, events, null, null, null, null, null, null, true, amounts)],
                amounts));
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
        foreach (var usage in new[]
        {
            receipt.Usage with { Attribution = receipt.Usage.Attribution with { TurnId = "foreign-turn" } },
            receipt.Usage with { EventId = Guid.NewGuid() },
            receipt.Usage with { SdkSource = receipt.Usage.SdkSource! with { SourceMode = "byok" } },
            receipt.Usage with { SdkSource = receipt.Usage.SdkSource! with { AcceptedSelectionHash = new string('b', 64) } },
            receipt.Usage with { SdkSource = receipt.Usage.SdkSource! with { RegistrationRevision = 2 } },
            receipt.Usage with { SdkSource = receipt.Usage.SdkSource! with { SdkSessionId = "foreign-session" } },
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
                PlacementProviderId = "sandbox-platform",
                EnvironmentLifecycleGeneration = 1,
                EnvironmentLeaseRevision = 2,
                EnvironmentCurrentFencingGeneration = 3,
                EnvironmentProviderFencingGeneration = 3
            },
            RuntimeRegistrationState.Active, DateTimeOffset.UtcNow.AddMinutes(1));
        var source = new SdkSessionFacts(
            registration.RuntimeInstanceId, RuntimeContractValidation.NativeSessionId(registration.Binding),
            "1.0.11+source", "runtime-v1", "accepted-model", "native-model", new string('b', 64),
            2.5m, "hosted-copilot", SdkMeterSources.CopilotNanoAiu, registration.Binding.AcceptedSelectionHash, 1);
        var eventId = Guid.NewGuid().ToString("D");
        var observation = new SdkUsageObservation(
            SdkUsageIdentity.Create(source.RuntimeInstanceId, source.SdkSessionId, eventId),
            eventId, source.SdkSessionId, DateTimeOffset.UtcNow, source.ModelId,
            17, 11, 7, 5, 3, 1234567.25m, 12.5m);
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
            Binding = hosted.Registration.Binding with { ModelSourceMode = ModelSourceMode.Byok }
        };
        var source = hosted.Usage.SdkSource! with
        {
            SourceMode = "byok", MeterSource = SdkMeterSources.ByokTokens, ModelMultiplier = null
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
            usage with { SdkSource = source with { ModelMultiplier = 0 } }
        })
            Assert.Throws<RuntimeAuthorizationException>(() =>
                RuntimeUsageSourceReceiptContract.ValidateUsage(registration, invalid));
    }
}
