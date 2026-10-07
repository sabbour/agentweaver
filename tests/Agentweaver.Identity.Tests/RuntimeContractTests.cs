using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Xunit;

namespace Agentweaver.Identity.Tests;

public sealed class RuntimeContractTests
{
    [Fact]
    public void RegistrationPinsTheCompleteServerOwnedTuple()
    {
        var registration = CreateRegistration();
        RuntimeContractValidation.Validate(registration);
        var original = RuntimeContractValidation.RegistrationHash(registration);
        foreach (var changed in new[]
        {
            registration with { Revision = 2 },
            registration with { RuntimeInstanceId = Guid.NewGuid() },
            registration with { State = RuntimeRegistrationState.Revoked },
            registration with { Binding = registration.Binding with { AgentId = "other-agent" } },
            registration with { Binding = registration.Binding with { TurnId = "other-turn" } },
            registration with { Binding = registration.Binding with { ExecutionFence = 2 } },
            registration with { Binding = registration.Binding with { EnvironmentCurrentFencingGeneration = 2 } },
            registration with { Binding = registration.Binding with { EnvironmentProviderFencingGeneration = 3 } },
            registration with { Binding = registration.Binding with { PlacementUid = "other-placement" } },
            registration with { Binding = registration.Binding with { ProjectRevision = 2 } },
            registration with { Binding = registration.Binding with { ModelSelectionReference = "accepted-model" } },
            registration with { Binding = registration.Binding with { AcceptedSelectionHash = new string('b', 64) } }
        })
            Assert.NotEqual(original, RuntimeContractValidation.RegistrationHash(changed));
    }

    [Fact]
    public void MissingLegacyModelReferenceDoesNotChangeTheStoredBindingJson()
    {
        var binding = CreateRegistration().Binding;
        var serialized = JsonSerializer.Serialize(binding);
        Assert.DoesNotContain("ModelSelectionReference", serialized);
        Assert.DoesNotContain("PlacementProviderId", serialized);
        Assert.DoesNotContain("EnvironmentLifecycleGeneration", serialized);
        Assert.DoesNotContain("EnvironmentLeaseRevision", serialized);
        var pinned = binding with { ModelSelectionReference = "accepted-model" };
        Assert.Contains("ModelSelectionReference", JsonSerializer.Serialize(pinned));
    }

    [Fact]
    public void CurrentPlacementPinsAreCompleteAndChangeTheBindingHash()
    {
        var registration = CreateRegistration();
        var pinned = registration with
        {
            Binding = registration.Binding with
            {
                PlacementProviderId = "sandbox-platform",
                EnvironmentLifecycleGeneration = 7,
                EnvironmentLeaseRevision = 3
            }
        };
        RuntimeContractValidation.Validate(pinned);
        Assert.NotEqual(RuntimeContractValidation.RegistrationHash(registration),
            RuntimeContractValidation.RegistrationHash(pinned));
        foreach (var changed in new[]
        {
            pinned with { Binding = pinned.Binding with { PlacementProviderId = "foreign-provider" } },
            pinned with { Binding = pinned.Binding with { EnvironmentLifecycleGeneration = 8 } },
            pinned with { Binding = pinned.Binding with { EnvironmentLeaseRevision = 4 } }
        })
            Assert.NotEqual(RuntimeContractValidation.RegistrationHash(pinned),
                RuntimeContractValidation.RegistrationHash(changed));
        Assert.Throws<RuntimeAuthorizationException>(() => RuntimeContractValidation.Validate(
            pinned with { Binding = pinned.Binding with { EnvironmentLeaseRevision = 0 } }));
        Assert.Throws<RuntimeAuthorizationException>(() => RuntimeContractValidation.Validate(
            pinned with { Binding = pinned.Binding with { PlacementProviderId = null } }));
    }

    [Theory]
    [InlineData("http://runtime.test/configure")]
    [InlineData("https://runtime.test/configure?target=foreign")]
    [InlineData("https://runtime.test/configure#fragment")]
    [InlineData("https://user:password@runtime.test/configure")]
    [InlineData("/configure")]
    public void ConfigureTargetMustBeAnExactHttpsEndpoint(string address)
    {
        var registration = CreateRegistration();
        Assert.Throws<RuntimeAuthorizationException>(() => RuntimeContractValidation.Validate(
            registration with
            {
                Binding = registration.Binding with
                {
                    ConfigureEndpoint = new Uri(address, UriKind.RelativeOrAbsolute)
                }
            }));
    }

    [Fact]
    public void CredentialValuesCannotEnterDefaultJsonOrDiagnosticStrings()
    {
        var raw = new string('d', 64);
        var credential = new SecretCredential(raw, DateTimeOffset.UtcNow.AddMinutes(1));
        var receipt = new RuntimeGrantReceipt(
            Guid.NewGuid(), Guid.NewGuid(), 1, 1, "https://broker.test/",
            RuntimeCredentialPurpose.Observe, new Uri("https://orchestrator.test/runtime/observations"),
            RuntimeCredentialState.Active, new string('a', 64),
            credential.ExpiresAt, DateTimeOffset.UtcNow);
        var proof = new RuntimeCredentialProof(
            receipt.GrantId, receipt.RuntimeInstanceId, 1, receipt.Purpose,
            receipt.Audience, receipt.ConfigurationHash, credential);
        var issue = new RuntimeCredentialIssue(receipt, credential);
        var exchange = new RuntimeCredentialExchange(receipt, credential, false);
        var actor = new RuntimeActorAuthorization(credential, "tenant");
        foreach (var value in new object[] { proof, issue, exchange, actor })
        {
            Assert.DoesNotContain(raw, JsonSerializer.Serialize(value));
            Assert.DoesNotContain(raw, value.ToString());
        }
        credential.Invalidate();
        Assert.Throws<InvalidOperationException>(() => credential.GetValue());
    }

    [Fact]
    public void VerifierChecksTheCryptographicSecretRatherThanTheReference()
    {
        var raw = new string('d', 64);
        var verifier = RuntimeContractValidation.Hash(Encoding.ASCII.GetBytes(raw));
        Assert.True(RuntimeContractValidation.VerifierMatches(raw, verifier));
        Assert.False(RuntimeContractValidation.VerifierMatches(new string('e', 64), verifier));
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeContractValidation.VerifierMatches(Guid.NewGuid().ToString(), verifier));
    }

    private static RuntimeRegistration CreateRegistration() => new(
        Guid.NewGuid(), 1,
        new RuntimeBinding(
            "https://broker.test/", Guid.NewGuid().ToString("D"), "tenant", "project", "run",
            "session", "agent", "turn", 1, 1, 1, "context:1", new string('a', 64), 1,
            "environment", "placement-uid", 1, "hosted-profile",
            new Uri("https://runtime.test/configure"),
            new Uri("https://orchestrator.test/runtime/observations"))
        {
            EnvironmentCurrentFencingGeneration = 4,
            EnvironmentProviderFencingGeneration = 7
        },
        RuntimeRegistrationState.Active, DateTimeOffset.UtcNow.AddMinutes(5));
}
