using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.EventsAndSessions;
using Agentweaver.Identity;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

public sealed class SessionMaterialReadAuthorityTests
{
    [Fact]
    public void HistoricalTurnContentUsesCurrentSignedRunReadEntitlementWithoutRuntimeAuthority()
    {
        SessionMaterialApplicationService.RequireMaterialReadAuthority(Authority(), SessionMaterialKind.TurnContent);
        SessionMaterialApplicationService.RequireMaterialReadAuthority(
            Authority("readRunSelection"), SessionMaterialKind.TurnContent);
        Assert.Throws<RuntimeAuthorizationException>(() =>
            SessionMaterialApplicationService.RequireMaterialReadAuthority(Authority(), SessionMaterialKind.SdkCache));
        SessionMaterialApplicationService.RequireMaterialReadAuthority(
            Authority("readRunSelection"), SessionMaterialKind.SdkCache);
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("project")]
    [InlineData("run")]
    [InlineData("membership")]
    [InlineData("revision")]
    [InlineData("resource")]
    [InlineData("missing")]
    public void HistoricalMaterialDeniesMissingOrForeignCurrentAuthority(string changed)
    {
        var current = Authority();
        current = changed switch
        {
            "permission" => Authority("readRuntimeContext"),
            "project" => current with { BoundProjectId = null },
            "run" => current with { BoundRunId = null },
            "membership" => current with { MembershipRevision = 0 },
            "revision" => Authority(revision: 0),
            "resource" => Authority(resourceId: "other-project"),
            "missing" => current with { EffectiveAuthority = default },
            _ => throw new ArgumentException(nameof(changed))
        };
        var failure = Assert.Throws<RuntimeAuthorizationException>(() =>
            SessionMaterialApplicationService.RequireMaterialReadAuthority(current, SessionMaterialKind.TurnContent));
        Assert.Equal("runtime_material_read_authority_denied", failure.Code);
    }

    private static ProjectsAuthorizationContextResponse Authority(
        string permission = "readProjects", long revision = 1, string resourceId = "project") =>
        new(1, "https://broker.test/", "actor", "tenant", 1, "project", "run",
            [new("project", resourceId, ImmutableArray.Create(new ProjectsAuthorizationPermissionGrant(permission, revision)))]);
}
