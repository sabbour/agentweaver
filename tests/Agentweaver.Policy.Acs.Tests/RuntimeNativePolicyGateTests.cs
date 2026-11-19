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
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agentweaver.Policy.Acs.Tests;

[Collection("Runtime Native Policy")]
public sealed class RuntimeNativePolicyGateTests
{
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

    private static Task<string> RequireClearGuardrailsAsync(
        RuntimeNativePolicyGate gate,
        RuntimeNativePolicyPreparation preparation,
        RuntimeRegistration registration,
        RuntimeActionRequest request) =>
        gate.RequireGuardrailsAsync(
            preparation,
            registration,
            request,
            [
                new NativeAcsGuardrailsCheckFacts(
                    "prompt-shields", "1.0.0", 1, "options-v1")
            ],
            [],
            default);

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

    private static NativeAcsArtifact Artifact(string effect) =>
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
            """
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
              ]
            }
            """);

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

    private sealed class FixtureSource(NativeAcsArtifact artifact) : IRuntimeNativePolicySource
    {
        private NativeAcsArtifact _acceptedArtifact = artifact;
        private NativeAcsArtifact _currentArtifact = artifact;
        private long _acceptedRevision = 1;
        private long _currentRevision = 1;
        public bool Unavailable { get; set; }
        public bool Revoked { get; set; }
        public string? ProjectScopeId { get; set; }
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
            return Task.FromResult(
                new RuntimeNativePolicySourceSnapshot(
                    ProjectScopeId is null
                        ? [acceptedRevision]
                        : [acceptedRevision, PlatformRevision(acceptedRevision)],
                    ProjectScopeId is null
                        ? [currentRevision]
                        : [currentRevision, PlatformRevision(currentRevision)]));

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
