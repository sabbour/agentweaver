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
            registration with { Binding = registration.Binding with { AcceptedSelectionHash = new string('b', 64) } }
        })
            Assert.NotEqual(original, RuntimeContractValidation.RegistrationHash(changed));
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
