using System.Security.Claims;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Tests.Auth;

public sealed class CallerContextClaimsAdapterTests
{
    [Fact]
    public void PrincipalRoundTrip_PreservesCallerContractAndOwnershipAliases()
    {
        var caller = new CallerContext
        {
            User = "entra-object-id",
            EntraObjectId = "entra-object-id",
            EntraTenantId = "tenant-id",
            PlatformRoles = [PlatformRoles.Contributor, PlatformRoles.Viewer],
            RawPlatformRoles = ["Contributor", "Unknown"],
            PrimaryPlatformRole = PlatformRoles.Contributor,
            GitHubLogin = "octocat",
            DisplayName = "Octo Cat",
            Email = "octocat@example.test",
            AuthenticationScheme = AgentweaverAuthenticationSchemes.BrokerBearer,
            Org = "example",
        };

        var principal = CallerContextClaimsAdapter.ToPrincipal(
            caller,
            AgentweaverAuthenticationSchemes.BrokerBearer);
        var projected = CallerContextClaimsAdapter.FromPrincipal(principal);

        projected.Should().BeEquivalentTo(caller);
        projected.AuthenticationScheme.Should().Be(AgentweaverAuthenticationSchemes.BrokerBearer);
        projected.IsOAuthJwt.Should().BeTrue();
        projected.Owns("entra-object-id").Should().BeTrue();
        projected.Owns("octocat").Should().BeTrue();
    }

    [Fact]
    public void GetCaller_ProjectsFromPrincipal_NotLegacyItems()
    {
        var context = new DefaultHttpContext
        {
            User = CallerContextClaimsAdapter.ToPrincipal(
                new CallerContext { User = "claims-user" },
                AgentweaverAuthenticationSchemes.Entra),
        };
        context.Items[string.Concat("agentweaver.", "caller")] =
            new CallerContext { User = "legacy-items-user" };

        CallerContextClaimsAdapter.FromPrincipal(context.User).User.Should().Be("claims-user");
    }

    [Fact]
    public void PrivateInboundClaims_AreRemovedBeforeSchemeStamping()
    {
        Claim[] inbound =
        [
            new("oid", "object-id"),
            new(AgentweaverClaimTypes.AuthenticationScheme, AgentweaverAuthenticationSchemes.InternalServiceKey),
            new(AgentweaverClaimTypes.Organization, "forged-org"),
        ];

        var sanitized = CallerContextClaimsAdapter.RemovePrivateInboundClaims(inbound);

        sanitized.Should().ContainSingle(claim => claim.Type == "oid");
        sanitized.Should().NotContain(claim =>
            claim.Type.StartsWith(AgentweaverClaimTypes.PrivatePrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void PrincipalClaims_NeverContainPresentedCredential()
    {
        const string presentedCredential = "secret-presented-bearer";
        var principal = CallerContextClaimsAdapter.ToPrincipal(
            new CallerContext { User = "caller" },
            AgentweaverAuthenticationSchemes.Entra);

        principal.Claims.Should().NotContain(claim => claim.Value == presentedCredential);
    }
}
