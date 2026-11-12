using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class RuntimeRegistrationRecoveryTests
{
    [Fact]
    public void RecoveryReplayRestoresOnlyTheExactRegistrationRevocation()
    {
        var binding = new RuntimeBinding(
            "issuer", "actor", "tenant", "project", "run", "session", "agent", "turn",
            1, 1, 1, "context", new string('a', 64), 1, "environment", "placement", 1,
            "profile", new Uri("https://runtime.test/"), new Uri("https://runtime.test/"));
        var active = new RuntimeRegistration(
            Guid.NewGuid(), 1, binding, RuntimeRegistrationState.Active, DateTimeOffset.UtcNow.AddMinutes(5));
        var revoked = active with
        {
            Revision = active.Revision + 1,
            State = RuntimeRegistrationState.Revoked
        };

        Assert.Equal(active, RuntimeRegistrationOwner.RestoreReleasedRegistrationForRecovery(
            active, active.Revision));
        Assert.Equal(active, RuntimeRegistrationOwner.RestoreReleasedRegistrationForRecovery(
            revoked, active.Revision));
        Assert.Equal("runtime_registration_stale", Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeRegistrationOwner.RestoreReleasedRegistrationForRecovery(
                revoked, revoked.Revision)).Code);
    }
}
