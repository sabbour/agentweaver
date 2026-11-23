using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentControlSpecification;
using Agentweaver.AgentRuntime;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Policy.Acs;
using Agentweaver.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agentweaver.Policy.Acs.Tests;

[Collection("Runtime Native Policy")]
public sealed class RuntimeNativePolicyGateTests
{
    [RuntimeNativePolicyFact]
    public async Task ProductionRegistrationCompositionAllowsCurrentNativeToolCallback()
    {
        var evaluator = new NativeAcsPolicyEvaluator(
            new NativeAcsArtifactValidator(RequirePinnedOpa()), WorkRoot);
        var source = new FixtureSource(ToolArtifact(allowEdit: true));
        var store = new FixtureAdmissionStore();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Orchestrator:RuntimeRegistration:EnvironmentOwnerAddress"] =
                    "https://environment.test/",
                ["ACS_OPA_PATH"] = RequirePinnedOpa()
            }).Build();
        var services = new ServiceCollection()
            .AddSingleton(evaluator)
            .AddSingleton<IRuntimeNativePolicySource>(source)
            .AddSingleton<IRuntimeNativePolicyAdmissionStore>(store);
        Assert.True(services.AddRuntimeRegistrationOwner(
            configuration,
            new OrchestratorOptions(
                "https://issuer.test/", "orchestrator",
                "https://projects.test/", "projects",
                "https://events.test/", "events")));
        services.AddSingleton<IRuntimeNativePolicySource>(source);
        services.AddSingleton<IRuntimeNativePolicyAdmissionStore>(store);
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var gate = scope.ServiceProvider.GetRequiredService<RuntimeNativePolicyGate>();
        var (registration, accepted) = RunBinding("byok-model");
        var request = ToolRequest(registration, "invocation");

        var preparation = await gate.PrepareAuthorizeAsync(
            registration, accepted, request, default);
        await gate.CommitAsync(preparation, null, default);

        Assert.NotNull(await gate.RequireVerifyAsync(
            registration, accepted, request, default));
    }

    [RuntimeNativePolicyFact]
    public async Task NativeToolPolicyBindsSecondaryPermissionAndRejectsLateTightening()
    {
        var evaluator = new NativeAcsPolicyEvaluator(
            new NativeAcsArtifactValidator(RequirePinnedOpa()), WorkRoot);
        var source = new FixtureSource(ToolArtifact(allowEdit: true));
        var store = new FixtureAdmissionStore();
        var gate = new RuntimeNativePolicyGate(source, store, evaluator);
        var (registration, accepted) = RunBinding("byok-model");
        var invocation = ToolRequest(registration, "invocation");

        var preparation = await gate.PrepareAuthorizeAsync(
            registration, accepted, invocation, default);
        await gate.CommitAsync(preparation, null, default);
        Assert.NotNull(await gate.RequireVerifyAsync(
            registration, accepted, invocation, default));

        var secondary = ToolRequest(registration, "permission") with
        {
            ToolOwner = invocation.ToolOwner! with
            {
                Phase = "permission",
                ArgumentDigest = new string('c', 64),
                PermissionKind = "write",
                InvocationArgumentDigest = invocation.ToolOwner.ArgumentDigest,
                InvocationEventId = invocation.EventId,
                InvocationRequestHash = RuntimeActionContract.Hash(invocation)
            }
        };
        var secondaryPreparation = await gate.PrepareAuthorizeAsync(
            registration, accepted, secondary, default);
        Assert.NotNull(secondaryPreparation);

        source.SetArtifact(ToolArtifact(allowEdit: false));
        var effects = 0;
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(async () =>
        {
            _ = await gate.RequireVerifyAsync(
                registration, accepted, invocation, default);
            effects++;
        });
        Assert.Equal(0, effects);
    }

    [RuntimeNativePolicyFact]
    public async Task SecondaryPermissionRejectsDifferentInvocationTool()
    {
        var evaluator = new NativeAcsPolicyEvaluator(
            new NativeAcsArtifactValidator(RequirePinnedOpa()), WorkRoot);
        var source = new FixtureSource(ToolArtifact(allowEdit: true));
        var store = new FixtureAdmissionStore();
        var gate = new RuntimeNativePolicyGate(source, store, evaluator);
        var (registration, accepted) = RunBinding("byok-model");
        var invocation = ToolRequest(registration, "invocation");
        var preparation = await gate.PrepareAuthorizeAsync(
            registration, accepted, invocation, default);
        await gate.CommitAsync(preparation, null, default);
        var secondary = ToolRequest(registration, "permission") with
        {
            ToolOwner = invocation.ToolOwner! with
            {
                ToolName = "create",
                Phase = "permission",
                PermissionKind = "write",
                ArgumentDigest = new string('c', 64),
                InvocationArgumentDigest = invocation.ToolOwner.ArgumentDigest,
                InvocationEventId = invocation.EventId,
                InvocationRequestHash = RuntimeActionContract.Hash(invocation)
            }
        };

        var error = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            gate.PrepareAuthorizeAsync(registration, accepted, secondary, default));

        Assert.Equal(RuntimeNativePolicyGate.StaleCode, error.Code);
    }

    [RuntimeNativePolicyFact]
    public async Task SecondaryPermissionRejectsPolicyChangedAfterInvocation()
    {
        var evaluator = new NativeAcsPolicyEvaluator(
            new NativeAcsArtifactValidator(RequirePinnedOpa()), WorkRoot);
        var source = new FixtureSource(ToolArtifact(allowEdit: true));
        var store = new FixtureAdmissionStore();
        var gate = new RuntimeNativePolicyGate(source, store, evaluator);
        var (registration, accepted) = RunBinding("byok-model");
        var invocation = ToolRequest(registration, "invocation");
        var preparation = await gate.PrepareAuthorizeAsync(
            registration, accepted, invocation, default);
        await gate.CommitAsync(preparation, null, default);
        source.SetArtifact(ToolArtifact(allowEdit: false));
        var secondary = ToolRequest(registration, "permission") with
        {
            ToolOwner = invocation.ToolOwner! with
            {
                Phase = "permission",
                PermissionKind = "write",
                ArgumentDigest = new string('c', 64),
                InvocationArgumentDigest = invocation.ToolOwner.ArgumentDigest,
                InvocationEventId = invocation.EventId,
                InvocationRequestHash = RuntimeActionContract.Hash(invocation)
            }
        };

        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            gate.PrepareAuthorizeAsync(registration, accepted, secondary, default));
    }

    [Fact]
    public async Task RemoteSnapshotGateUsesScanEvidenceContentEquality()
    {
        var source = new FixtureSource(ToolArtifact(allowEdit: true));
        var (baseRegistration, baseAccepted) = RunBinding("byok-model");
        var snapshot = RemoteSnapshot(baseRegistration);
        var selection = JsonSerializer.SerializeToElement(new
        {
            modelSelection = baseAccepted.Selection.Snapshot.GetProperty("modelSelection"),
            projectConfiguration = new
            {
                reviewedRemoteToolSnapshots = new[] { snapshot.Reference }
            }
        });
        var accepted = baseAccepted with
        {
            Selection = baseAccepted.Selection with { Snapshot = selection }
        };
        var registration = baseRegistration with
        {
            Binding = baseRegistration.Binding with
            {
                AcceptedSelectionHash = RuntimeContractValidation.Hash(
                    Encoding.UTF8.GetBytes(selection.GetRawText()))
            }
        };
        var roundTripped = JsonSerializer.Deserialize<RemoteMcpCatalogScanEvidence>(
            JsonSerializer.Serialize(snapshot.ScanEvidence))!;
        var request = RemoteRequest(registration, snapshot, roundTripped);
        var resolver = new FixtureSnapshotResolver(snapshot);
        var withResolver = new RuntimeNativePolicyGate(
            source,
            new FixtureAdmissionStore(),
            new NativeAcsPolicyEvaluator(
                new NativeAcsArtifactValidator(
                    Path.Combine(AppContext.BaseDirectory, "missing-opa")),
                WorkRoot),
            resolver);

        var reachedEvaluation = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            withResolver.PrepareAuthorizeAsync(
                registration, accepted, request, default));
        Assert.Equal(RuntimeNativePolicyGate.UnavailableCode, reachedEvaluation.Code);

        var changed = new RemoteMcpCatalogScanEvidence(
            roundTripped.ContractVersion,
            roundTripped.ScannerId,
            roundTripped.ScannerVersion,
            roundTripped.ConfigurationRevision,
            roundTripped.DiscoveryRevision,
            roundTripped.CatalogSha256,
            roundTripped.BaselineDiscoveryRevision,
            roundTripped.BaselineCatalogSha256,
            roundTripped.EvaluatedAt,
            roundTripped.State,
            roundTripped.Checks.SetItem(
                0, roundTripped.Checks[0] with { ReasonCode = "changed" }),
            roundTripped.ErrorCode);
        var mismatch = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            withResolver.PrepareAuthorizeAsync(
                registration, accepted, RemoteRequest(registration, snapshot, changed), default));

        Assert.Equal("runtime_remote_tool_snapshot_binding_invalid", mismatch.Code);
    }

    [RuntimeNativePolicyFact]
    public async Task FinalVerificationRejectsPolicyUpdatedWhileVerificationIsInFlight()
    {
        var evaluator = new NativeAcsPolicyEvaluator(
            new NativeAcsArtifactValidator(RequirePinnedOpa()), WorkRoot);
        var source = new FixtureSource(ToolArtifact(allowEdit: true));
        var store = new FixtureAdmissionStore();
        var gate = new RuntimeNativePolicyGate(source, store, evaluator);
        var owner = new RuntimeActionOwner(
            null!, null!, null!, null!, null!, null!, null!, null!,
            new ServiceCollection().AddSingleton(gate).BuildServiceProvider(),
            new OrchestratorOptions(
                "https://issuer.test/", "orchestrator",
                "https://projects.test/", "projects",
                "https://events.test/", "events"));
        var (registration, accepted) = RunBinding("byok-model");
        var request = ToolRequest(registration, "invocation");
        var preparation = await gate.PrepareAuthorizeAsync(
            registration, accepted, request, default);
        await gate.CommitAsync(preparation, null, default);
        var changed = false;
        source.AfterRead = () =>
        {
            if (changed)
                return;
            changed = true;
            source.SetArtifact(ToolArtifact(allowEdit: false));
        };

        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            owner.RequireNativePolicyReleaseAsync(
                registration, accepted, request, default));
    }

    [RuntimeNativePolicyFact]
    public async Task NativePolicyBlocksEveryWithheldModelSendAndBindsRevision()
    {
        var evaluator = new NativeAcsPolicyEvaluator(
            new NativeAcsArtifactValidator(RequirePinnedOpa()),
            WorkRoot);
        var source = new FixtureSource(Artifact("allow"));
        var store = new FixtureAdmissionStore();
        var gate = new RuntimeNativePolicyGate(source, store, evaluator);
        var services = new ServiceCollection()
            .AddSingleton(gate)
            .BuildServiceProvider();
        var owner = new RuntimeActionOwner(
            null!, null!, null!, null!, null!, null!, null!, null!, services,
            new OrchestratorOptions(
                "https://issuer.test/",
                "orchestrator",
                "https://projects.test/",
                "projects",
                "https://events.test/",
                "events"));
        var (registration, accepted) = RunBinding("byok-model");
        var request = ModelTurnRequest(registration);
        var sends = 0;
        var fallbackSends = 0;

        async Task SendWhenVerifiedAsync()
        {
            var preparation = Assert.IsType<RuntimeNativePolicyPreparation>(
                await owner.PrepareNativePolicyAsync(
                    registration, accepted, request, default));
            Assert.NotNull(await RequireClearGuardrailsAsync(
                gate, preparation, registration, request));
            await RequireModelSendAsync(owner, gate, registration, accepted, request);
            sends++;
        }

        await SendWhenVerifiedAsync();
        Assert.Equal(1, sends);
        Assert.NotNull(store.ReadLast());
        Assert.All(
            store.ReadLast()!.AcceptedSources,
            item =>
            {
                Assert.Equal(1, item.Revision);
                Assert.Equal(source.ContentSha256, item.ContentSha256);
            });
        Assert.All(
            store.ReadLast()!.CurrentSources,
            item =>
            {
                Assert.Equal(1, item.Revision);
                Assert.Equal(source.ContentSha256, item.ContentSha256);
            });
        Assert.Equal(
            store.ReadLast()!.BindingHash,
            Assert.IsType<RuntimeNativePolicyPreparation>(
                await owner.PrepareNativePolicyAsync(
                    registration, accepted, request, default)).Binding.BindingHash);

        source.SetArtifact(Artifact("deny"));
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            RequireModelSendAsync(
                owner, gate, registration, accepted,
                request with { EventId = Guid.NewGuid() }));
        Assert.Equal(1, sends);
        Assert.Equal(0, fallbackSends);

        source.Unavailable = true;
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            RequireModelSendAsync(
                owner, gate, registration, accepted,
                request with { EventId = Guid.NewGuid() }));
        source.Unavailable = false;
        Assert.Equal(1, sends);

        source.SetArtifact(Artifact("allow"));
        var mismatched = registration with
        {
            Binding = registration.Binding with
            {
                ModelBindingPin = registration.Binding.ModelBindingPin! with
                {
                    ModelId = "different-deployment"
                }
            }
        };
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            RequireModelSendAsync(
                owner, gate, mismatched, accepted,
                request with { EventId = Guid.NewGuid() }));
        Assert.Equal(1, sends);

        var staleRequest = request with { EventId = Guid.NewGuid() };
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            RequireModelSendAsync(
                owner, gate, registration, accepted, staleRequest,
                beforeVerify: source.AdvanceRevision));
        Assert.Equal(1, sends);

        var revokedRequest = request with { EventId = Guid.NewGuid() };
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            RequireModelSendAsync(
                owner, gate, registration, accepted, revokedRequest,
                beforeVerify: () => source.Revoked = true));
        Assert.Equal(1, sends);

        source.Revoked = false;
        source.AdvanceRevision();
        var swappedRequest = request with { EventId = Guid.NewGuid() };
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            RequireModelSendAsync(
                owner, gate, registration, accepted, swappedRequest,
                beforeVerify: source.SwapAcceptedAndCurrent));
        Assert.Equal(1, sends);

        source.ResetBindings();
        source.ProjectScopeId = "another-project";
        var foreignRequest = request with { EventId = Guid.NewGuid() };
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            RequireModelSendAsync(
                owner, gate, registration, accepted, foreignRequest));
        source.ProjectScopeId = null;
        Assert.Equal(1, sends);

        var blockedRequest = request with { EventId = Guid.NewGuid() };
        var blockedPreparation = Assert.IsType<RuntimeNativePolicyPreparation>(
            await owner.PrepareNativePolicyAsync(
                registration, accepted, blockedRequest, default));
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            gate.RequireGuardrailsAsync(
                blockedPreparation,
                registration,
                blockedRequest,
                [
                    new NativeAcsGuardrailsCheckFacts(
                        "prompt-shields", "1.0.0", 1, "options-v1")
                ],
                [
                    new NativeAcsGuardrailsFindingFacts(
                        "prompt-shields", "prompt-injection", "high")
                ],
                default));
        Assert.Equal(1, sends);
    }

    private static async Task<string> RequireClearGuardrailsAsync(
        RuntimeNativePolicyGate gate,
        RuntimeNativePolicyPreparation preparation,
        RuntimeRegistration registration,
        RuntimeActionRequest request) =>
        (await gate.RequireGuardrailsAsync(
            preparation,
            registration,
            request,
            [
                new NativeAcsGuardrailsCheckFacts(
                    "prompt-shields", "1.0.0", 1, "options-v1")
            ],
            [],
            default).ConfigureAwait(false)).BindingHash;

    private static async Task RequireModelSendAsync(
        RuntimeActionOwner owner,
        RuntimeNativePolicyGate gate,
        RuntimeRegistration registration,
        AuthorizedRunSelection accepted,
        RuntimeActionRequest request,
        Action? beforeVerify = null)
    {
        using var http = new HttpClient(new PolicyOwnerHandler(async message =>
        {
            try
            {
                var path = message.RequestUri!.AbsolutePath;
                if (path.EndsWith("/authorize", StringComparison.Ordinal))
                {
                    var body = await message.Content!
                        .ReadFromJsonAsync<RuntimeActionRequest>();
                    var preparation = await owner.PrepareNativePolicyAsync(
                        registration, accepted, body!, default);
                    if (preparation is not null && !preparation.RequiredChecks.IsDefaultOrEmpty)
                        return Failure(message, HttpStatusCode.PreconditionRequired);
                    throw new InvalidOperationException("Expected native Guardrails requirements.");
                }
                if (path.EndsWith("/authorize-content", StringComparison.Ordinal))
                {
                    var body = await message.Content!
                        .ReadFromJsonAsync<RuntimeActionContentRequest>();
                    var preparation = Assert.IsType<RuntimeNativePolicyPreparation>(
                        await owner.PrepareNativePolicyAsync(
                            registration, accepted, body!.Request, default));
                    var guardrailsHash = await RequireClearGuardrailsAsync(
                        gate, preparation, registration, body.Request);
                    await owner.CommitNativePolicyAsync(
                        preparation, guardrailsHash, default);
                    return Json(message, new RuntimeActionAdmission(
                        1,
                        body.Request,
                        body.Request.EventId.ToString("N"),
                        "1",
                        body.Request.EventId,
                        PolicyEvaluationOutcome.Allow,
                        PolicyEvaluationReasonCode.Allowed));
                }
                if (path.EndsWith("/verify", StringComparison.Ordinal))
                {
                    beforeVerify?.Invoke();
                    var body = await message.Content!
                        .ReadFromJsonAsync<RuntimeActionAdmission>(JsonOptions);
                    await owner.RequireNativePolicyVerifyAsync(
                        registration, accepted, body!.Request, default);
                    return Json(message, body);
                }
                return Failure(message, HttpStatusCode.NotFound);
            }
            catch (RuntimeAuthorizationException exception)
            {
                return Failure(
                    message,
                    exception.Code == RuntimeNativePolicyGate.UnavailableCode
                        ? HttpStatusCode.ServiceUnavailable
                        : HttpStatusCode.Forbidden);
            }
        }));
        var actor = new RuntimeActorAuthorization(
            new SecretCredential("protected-actor", DateTimeOffset.UtcNow.AddMinutes(1)),
            "tenant-1");
        var actions = new RuntimeActionHttpClient(
            http, new Uri("https://orchestrator.test/"), actor);
        await actions.RequireAsync(
            request, _ => Task.CompletedTask, default, "prompt");
    }

    private static HttpResponseMessage Json<T>(
        HttpRequestMessage request, T value) =>
        new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = JsonContent.Create(value)
        };

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

    private static HttpResponseMessage Failure(
        HttpRequestMessage request, HttpStatusCode status) =>
        new(status)
        {
            RequestMessage = request,
            Content = JsonContent.Create(new { error = "denied" })
        };

    [Fact]
    public void NativeGuardrailsRequirementsMakeContentThePrecondition()
    {
        var resolver = new GuardrailsStagePlanResolver(
            Assert.IsType<ProviderCatalog>(ProviderCatalog.Create([], [], []).Value), []);
        var services = new ServiceCollection()
            .AddSingleton(resolver)
            .BuildServiceProvider();
        var native = new RuntimeNativePolicyPreparation(
            new RuntimeNativePolicyAdmissionBinding(
                Guid.NewGuid(), new string('a', 64), new string('b', 64), new string('c', 64), [], []),
            [],
            [
                new NativeAcsGuardrailsRequiredCheck(
                    "prompt-shields", "1.0.0", 1, "options-v1")
            ]);
        var accepted = Accepted("{}");

        var failure = Assert.Throws<RuntimeAuthorizationException>(
            () => RuntimeGuardrailsGate.Prepare(
                services,
                ModelTurnRequest(Registration("byok-model")),
                accepted,
                null,
                native));
        Assert.Equal(RuntimeGuardrailsGate.ContentRequiredCode, failure.Code);
    }

    [RuntimeNativePolicyFact]
    public async Task NativeApprovalRejectsChangedOwnerTurnAndArgumentDigest()
    {
        var evaluator = new NativeAcsPolicyEvaluator(
            new NativeAcsArtifactValidator(RequirePinnedOpa()), WorkRoot);
        var source = new FixtureSource(Artifact(
            "allow",
            escalates: true));
        var gate = new RuntimeNativePolicyGate(source, new FixtureAdmissionStore(), evaluator);
        var (registration, accepted) = RunBinding("byok-model");
        var request = ModelTurnRequest(registration);
        var preparation = await gate.PrepareAuthorizeAsync(
            registration, accepted, request, default);

        var retained = await gate.RequireGuardrailsAsync(
            preparation,
            registration,
            request,
            [new NativeAcsGuardrailsCheckFacts(
                "prompt-shields", "1.0.0", 1, "options-v1")],
            [new NativeAcsGuardrailsFindingFacts(
                "prompt-shields", "sensitive-request", "medium")],
            default);

        var escalation = Assert.Single(retained.Escalations);
        Assert.Equal(registration.Binding.TurnId,
            escalation.PolicyInput.GetProperty("policy_target").GetProperty("value")
                .GetProperty("approval").GetProperty("turn_id").GetString());
        Assert.Equal(request.InputHash,
            escalation.PolicyInput.GetProperty("policy_target").GetProperty("value")
                .GetProperty("approval").GetProperty("arguments_digest").GetString());
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            gate.ResolveApprovalAsync(
                registration with
                {
                    Binding = registration.Binding with { TurnId = "turn-sibling" }
                },
                request,
                retained,
                approve: true,
                default));
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            gate.ResolveApprovalAsync(
                registration,
                request with { InputHash = new string('c', 64) },
                retained,
                approve: true,
                default));

        await gate.ResolveApprovalAsync(
            registration, request, retained, approve: true, default);
    }

    private static NativeAcsArtifact Artifact(string effect, bool escalates = false) =>
        NativeAcsUnifiedPolicyProfile.CreateArtifact(
            """{"allowed_action_ids":["model.turn"]}""",
            """
            {
              "agentweaver": {
                "platform_egress": {
                  "profile": "finite-network-v1",
                  "mode": "Allowlist",
                  "approved": [],
                  "system": {
                    "catalog": "hosted-copilot-v1",
                    "destinations": [
                      { "host": "api.business.githubcopilot.com", "port": 443 },
                      { "host": "api.enterprise.githubcopilot.com", "port": 443 },
                      { "host": "api.github.com", "port": 443 },
                      { "host": "api.githubcopilot.com", "port": 443 },
                      { "host": "api.individual.githubcopilot.com", "port": 443 }
                    ]
                  }
                }
              }
            }
            """,
            $$"""
            {
              "rules": [
                {
                  "effect": "{{effect}}",
                  "model_selection_reference": "byok-model",
                  "model_id": "deployment-1",
                  "source_mode": "byok",
                  "provider_type": "azure",
                  "provider_id": null,
                  "configuration_revision": "7",
                  "configuration_hash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                }
              ]
            }
            """,
            $$"""
            {
              "required_checks": [
                {
                  "provider_id": "prompt-shields",
                  "adapter_version": "1.0.0",
                  "options_schema_version": 1,
                  "options_revision": "options-v1"
                }
              ],
              "blocked_findings": [
                {
                  "check_id": "prompt-shields",
                  "code": "prompt-injection",
                  "severity": "high"
                }
              ],
              {{(escalates
                  ? """
                    "escalation": {
                      "version": "guardrails-escalation-v1",
                      "rules": [
                        {
                          "check_id": "prompt-shields",
                          "code": "sensitive-request",
                          "severity": "medium"
                        }
                      ]
                    }
                    """
                  : """
                    "escalation": {
                      "version": "guardrails-escalation-v1",
                      "rules": []
                    }
                    """)}}
            }
            """);

    private static NativeAcsArtifact ToolArtifact(bool allowEdit) =>
        NativeAcsUnifiedPolicyProfile.CreateArtifact(
            """{"allowed_action_ids":["tool.write"]}""",
            """
            {
              "agentweaver": {
                "platform_egress": {
                  "profile": "finite-network-v1",
                  "mode": "Allowlist",
                  "approved": [],
                  "system": {
                    "catalog": "hosted-copilot-v1",
                    "destinations": [
                      { "host": "api.business.githubcopilot.com", "port": 443 },
                      { "host": "api.enterprise.githubcopilot.com", "port": 443 },
                      { "host": "api.github.com", "port": 443 },
                      { "host": "api.githubcopilot.com", "port": 443 },
                      { "host": "api.individual.githubcopilot.com", "port": 443 }
                    ]
                  }
                }
              }
            }
            """,
            """{"rules":[]}""",
            """{"required_checks":[],"blocked_findings":[]}""",
            allowEdit
                ? """
                  {"remote_mcp_scan_requirements":[{"check_id":"mcp.accepted-baseline-drift","required":false,"maximum_risk_score":100}],"rules":[{"tool_name":"edit","action_id":"tool.write","phase":"invocation","permission_kind":null,"read_access":false,"write_access":true,"shell_access":false},{"tool_name":"edit","action_id":"tool.write","phase":"permission","permission_kind":"write","read_access":false,"write_access":true,"shell_access":false}]}
                  """
                : """{"remote_mcp_scan_requirements":[{"check_id":"mcp.accepted-baseline-drift","required":false,"maximum_risk_score":100}],"rules":[]}""");

    private static ReviewedRemoteToolSnapshot RemoteSnapshot(RuntimeRegistration registration)
    {
        var evidence = new RemoteMcpCatalogScanEvidence(
            1,
            RemoteMcpCatalogScanEvidence.CurrentScannerId,
            RemoteMcpCatalogScanEvidence.CurrentScannerVersion,
            4,
            8,
            new string('c', 64),
            null,
            null,
            DateTimeOffset.Parse("2026-11-23T00:00:00Z"),
            RemoteMcpScanState.Completed,
            RemoteMcpCatalogScanEvidence.CheckIds.Select(checkId =>
                new RemoteMcpScanCheckEvidence(
                    checkId,
                    checkId == RemoteMcpCatalogScanEvidence.BaselineDriftCheck
                        ? RemoteMcpScanOutcome.Unknown
                        : RemoteMcpScanOutcome.Pass,
                    null,
                    checkId == RemoteMcpCatalogScanEvidence.BaselineDriftCheck
                        ? "remote_mcp_accepted_baseline_absent"
                        : null)).ToImmutableArray());
        return new(
            Guid.NewGuid(),
            registration.Binding.ProjectId,
            registration.Binding.AgentId,
            "node-1",
            "connection-1",
            5,
            4,
            8,
            "Enabled",
            new string('b', 64),
            "https://mcp.example.com/mcp",
            null,
            "None",
            null,
            "StreamableHttp20250618",
            null,
            "catalog-8",
            new string('c', 64),
            "remote.lookup",
            "tool-r1",
            new string('d', 64),
            "schema-r1",
            new string('e', 64),
            """{"name":"remote.lookup"}""",
            """{"type":"object"}""",
            new(
                "tool.read",
                RuntimeActionContract.Purpose,
                """{"source":"review"}"""),
            evidence);
    }

    private static RuntimeActionRequest RemoteRequest(
        RuntimeRegistration registration,
        ReviewedRemoteToolSnapshot snapshot,
        RemoteMcpCatalogScanEvidence evidence) =>
        new(
            1,
            registration.RuntimeInstanceId,
            registration.Revision,
            registration.Binding.ExecutionFence,
            Guid.NewGuid(),
            "tool.read",
            new string('f', 64))
        {
            IsToolInvocation = true,
            RemoteToolOwner = new(
                1,
                snapshot.AgentId,
                snapshot.NodeId,
                "call-1",
                new string('1', 64),
                registration.Binding.TurnId,
                snapshot.SnapshotId.ToString("D"),
                snapshot.SnapshotDigest,
                snapshot.ConnectionId,
                snapshot.ConnectionConfigurationRevision,
                snapshot.ConnectionDiscoveryRevision!.Value,
                snapshot.ConfigurationSha256,
                snapshot.CatalogDigest,
                snapshot.ToolId,
                snapshot.ToolRevision,
                snapshot.ToolDigest,
                snapshot.TransportProfile,
                snapshot.TransportOptionsSha256,
                new string('2', 64),
                1,
                evidence)
        };

    private static RuntimeActionRequest ToolRequest(
        RuntimeRegistration registration, string phase)
    {
        var owner = new RuntimeActionToolOwnerProjection(
            1,
            RuntimeContractValidation.NativeSessionId(registration.Binding),
            "edit",
            "call-edit-1",
            phase,
            new string('b', 64),
            Guid.NewGuid(),
            registration.Binding.TurnId,
            "tool.write",
            false,
            true,
            false,
            phase == "permission" ? "write" : null,
            phase == "permission" ? new string('a', 64) : null,
            phase == "permission" ? Guid.NewGuid() : null,
            phase == "permission" ? new string('e', 64) : null);
        return new(
            1, registration.RuntimeInstanceId, registration.Revision,
            registration.Binding.ExecutionFence, Guid.NewGuid(), "tool.write",
            new string('d', 64))
        {
            IsToolInvocation = phase == "invocation",
            IsNativeToolCallback = true,
            ToolOwner = owner
        };
    }

    private static (RuntimeRegistration Registration, AuthorizedRunSelection Accepted) RunBinding(
        string reference)
    {
        var registration = Registration(reference);
        var pin = JsonSerializer.SerializeToElement(
            registration.Binding.ModelBindingPin,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var selection = JsonDocument.Parse($$"""
            {
              "modelSelection": {
                "reference": "{{reference}}",
                "sourceMode": "byok",
                "reasoningEffort": "high",
                "modelBindingPin": {{pin.GetRawText()}}
              }
            }
            """).RootElement.Clone();
        var accepted = new AuthorizedRunSelection(
            new EffectiveRunSelection(
                registration.Binding.ProjectId,
                registration.Binding.RunId,
                registration.Binding.ProjectRevision,
                registration.Binding.ProjectConfigurationRevision,
                registration.Binding.PlatformRuntimeRevision,
                registration.Binding.ContextRevision,
                selection),
            new ProjectsAuthorizationContext(
                1,
                registration.Binding.ActorIssuer,
                registration.Binding.ActorId,
                registration.Binding.TenantId,
                1,
                registration.Binding.ProjectId,
                registration.Binding.RunId,
                []));
        registration = registration with
        {
            Binding = registration.Binding with
            {
                AcceptedSelectionHash = RuntimeContractValidation.Hash(
                    Encoding.UTF8.GetBytes(selection.GetRawText()))
            }
        };
        return (registration, accepted);
    }

    private static RuntimeRegistration Registration(string reference)
    {
        var pin = new RuntimeModelBindingPin(
            1, reference, "deployment-1", ModelSourceMode.Byok, "7", new string('a', 64))
        {
            ProviderType = "azure"
        };
        var binding = new RuntimeBinding(
            "https://issuer.test/",
            Guid.NewGuid().ToString("D"),
            "tenant-1",
            "project-1",
            "run-1",
            "session-1",
            "agent-1",
            "turn-1",
            1,
            1,
            1,
            "context-1",
            new string('b', 64),
            1,
            "environment-1",
            "placement-1",
            1,
            "profile-1",
            new Uri("https://runtime.test/configure"),
            new Uri("https://runtime.test/observe"))
        {
            EnvironmentCurrentFencingGeneration = 1,
            EnvironmentProviderFencingGeneration = 1,
            ModelSelectionReference = reference,
            ModelSourceMode = ModelSourceMode.Byok,
            ModelBindingPin = pin
        };
        return new(
            Guid.NewGuid(), 1, binding, RuntimeRegistrationState.Active,
            DateTimeOffset.UtcNow.AddMinutes(5));
    }

    private static RuntimeActionRequest ModelTurnRequest(RuntimeRegistration registration) =>
        new(
            1,
            registration.RuntimeInstanceId,
            registration.Revision,
            registration.Binding.ExecutionFence,
            Guid.NewGuid(),
            "model.turn",
            RuntimeContractValidation.Hash("prompt"u8));

    private static AuthorizedRunSelection Accepted(string snapshotJson)
    {
        using var document = JsonDocument.Parse(snapshotJson);
        return Accepted(document.RootElement.Clone());
    }

    private static AuthorizedRunSelection Accepted(JsonElement snapshot) =>
        new(
            new EffectiveRunSelection(
                "project-1", "run-1", 1, 1, 1, "context-1", snapshot),
            new ProjectsAuthorizationContext(
                1,
                "https://issuer.test/",
                Guid.Empty.ToString("D"),
                "tenant-1",
                1,
                "project-1",
                "run-1",
                []));

    private static string RequirePinnedOpa()
    {
        var path = Environment.GetEnvironmentVariable("ACS_OPA_PATH");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException("Set ACS_OPA_PATH to the pinned OPA 0.70.0 executable.");
        return Path.GetFullPath(path);
    }

    private static string WorkRoot => Path.Combine(AppContext.BaseDirectory, "runtime-native-policy-work");

    private sealed class FixtureSnapshotResolver(
        ReviewedRemoteToolSnapshot snapshot) : IReviewedRemoteToolSnapshotResolver
    {
        public Task<ReviewedRemoteToolSnapshot?> ResolveAsync(
            ReviewedRemoteToolSnapshotReference reference,
            CancellationToken cancellationToken) =>
            Task.FromResult(reference.SnapshotDigest == snapshot.SnapshotDigest
                ? snapshot
                : null);
    }

    private sealed class FixtureSource(NativeAcsArtifact artifact) : IRuntimeNativePolicySource
    {
        private NativeAcsArtifact _acceptedArtifact = artifact;
        private NativeAcsArtifact _currentArtifact = artifact;
        private long _acceptedRevision = 1;
        private long _currentRevision = 1;
        public bool Unavailable { get; set; }
        public bool Revoked { get; set; }
        public string? ProjectScopeId { get; set; }
        public Action? AfterRead { get; set; }
        public string ContentSha256 => _currentArtifact.ContentSha256;

        public void SetArtifact(NativeAcsArtifact artifact)
        {
            _acceptedArtifact = artifact;
            _currentArtifact = artifact;
            _acceptedRevision++;
            _currentRevision++;
        }

        public void AdvanceRevision() => _currentRevision++;

        public void SwapAcceptedAndCurrent()
        {
            (_acceptedArtifact, _currentArtifact) = (_currentArtifact, _acceptedArtifact);
            (_acceptedRevision, _currentRevision) = (_currentRevision, _acceptedRevision);
        }

        public void ResetBindings()
        {
            _acceptedArtifact = _currentArtifact;
            _acceptedRevision = _currentRevision;
        }

        public Task<RuntimeNativePolicySourceSnapshot> ReadAsync(
            RuntimeRegistration registration,
            AuthorizedRunSelection accepted,
            CancellationToken cancellationToken)
        {
            if (Revoked)
                throw new RuntimeAuthorizationException("runtime_native_policy_revoked");
            if (Unavailable)
                throw new IOException("Source is unavailable.");
            var acceptedRevision = new RuntimeNativePolicyRevision(
                ProjectScopeId is null ? "Platform" : "Project",
                ProjectScopeId ?? "default",
                "runtime-policy",
                _acceptedRevision,
                _acceptedArtifact.ContentSha256,
                _acceptedArtifact);
            var currentRevision = new RuntimeNativePolicyRevision(
                ProjectScopeId is null ? "Platform" : "Project",
                ProjectScopeId ?? "default",
                "runtime-policy",
                _currentRevision,
                _currentArtifact.ContentSha256,
                _currentArtifact);
            var snapshot =
                new RuntimeNativePolicySourceSnapshot(
                    ProjectScopeId is null
                        ? [acceptedRevision]
                        : [acceptedRevision, PlatformRevision(acceptedRevision)],
                    ProjectScopeId is null
                        ? [currentRevision]
                        : [currentRevision, PlatformRevision(currentRevision)]);
            AfterRead?.Invoke();
            return Task.FromResult(snapshot);

            static RuntimeNativePolicyRevision PlatformRevision(
                RuntimeNativePolicyRevision revision) =>
                revision with
                {
                    ScopeType = "Platform",
                    ScopeId = "default"
                };
        }
    }

    private sealed class PolicyOwnerHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await handler(request);
            response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoStore = true
            };
            return response;
        }
    }

    private sealed class FixtureAdmissionStore : IRuntimeNativePolicyAdmissionStore
    {
        private readonly Dictionary<Guid, RuntimeNativePolicyAdmissionBinding> _bindings = [];

        public Task<RuntimeNativePolicyAdmissionBinding?> ReadAsync(
            Guid eventId, CancellationToken cancellationToken) =>
            Task.FromResult(_bindings.GetValueOrDefault(eventId));

        public Task StoreAsync(
            RuntimeNativePolicyAdmissionBinding binding, CancellationToken cancellationToken)
        {
            if (_bindings.TryGetValue(binding.EventId, out var existing) && existing != binding)
                throw new InvalidOperationException("Changed binding for one event.");
            _bindings[binding.EventId] = binding;
            return Task.CompletedTask;
        }

        public RuntimeNativePolicyAdmissionBinding? ReadLast() => _bindings.Values.LastOrDefault();
    }
}

[CollectionDefinition("Runtime Native Policy", DisableParallelization = true)]
public sealed class RuntimeNativePolicyTestCollection
{
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class RuntimeNativePolicyFactAttribute : FactAttribute
{
    public RuntimeNativePolicyFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "The pinned ACS package has a Linux-x64 native library.";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ACS_OPA_PATH")) ||
                 !File.Exists(Environment.GetEnvironmentVariable("ACS_OPA_PATH")))
            Skip = "Set ACS_OPA_PATH to the pinned OPA 0.70.0 executable.";
    }
}
