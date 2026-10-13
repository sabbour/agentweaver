using System.Security.Claims;
using Agentweaver.Projects.Config;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class BrokerRunBindingOwnerTests
{
    private const string Issuer = "https://identity.test/";
    private const string ProjectId = "7fc0a64da91e4f7188c232f7f8dc8531";
    private const string RunId = "run-1";

    [Theory]
    [InlineData("missing-actor-issuer")]
    [InlineData("duplicate-actor-subject")]
    [InlineData("wrong-project")]
    [InlineData("invalid-actor-subject")]
    [InlineData("missing-purpose")]
    [InlineData("duplicate-jti")]
    [InlineData("extra-scope")]
    public async Task RejectsInvalidPrivateQueryClaimsBeforeDatabaseRead(string variation)
    {
        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused")
            .Options;
        await using var db = new ProjectsConfigDbContext(options);
        var owner = new ProjectAuthorizationOwner(db, new ProjectsConfigIdentityOptions(Issuer));
        var principal = CreatePrincipal(variation);

        var exception = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            owner.ResolveBrokerRunBindingAsync(principal, ProjectId, RunId, CancellationToken.None));

        Assert.Equal(StatusCodes.Status403Forbidden, exception.StatusCode);
    }

    private static ClaimsPrincipal CreatePrincipal(string variation)
    {
        var claims = new List<Claim>
        {
            new("sub", ProjectAuthorizationOwner.BrokerRunBindingServiceSubject),
            new("actor_iss", Issuer),
            new("actor_sub", "7fc0a64d-a91e-4f71-88c2-32f7f8dc8531"),
            new("project_id", ProjectId),
            new("run_id", RunId),
            new("purpose", ProjectAuthorizationOwner.BrokerRunBindingPurpose),
            new("jti", "0e16d39101a34613a0032f8621f843e3"),
        };
        switch (variation)
        {
            case "missing-actor-issuer":
                claims.RemoveAt(1);
                break;
            case "duplicate-actor-subject":
                claims.Add(new Claim("actor_sub", "7fc0a64d-a91e-4f71-88c2-32f7f8dc8531"));
                break;
            case "wrong-project":
                claims[3] = new Claim("project_id", "another-project");
                break;
            case "invalid-actor-subject":
                claims[2] = new Claim("actor_sub", "not-a-guid");
                break;
            case "missing-purpose":
                claims.RemoveAt(5);
                break;
            case "duplicate-jti":
                claims.Add(new Claim("jti", "6e16d39101a34613a0032f8621f843e3"));
                break;
            case "extra-scope":
                claims.Add(new Claim("scope", "projects.orchestrator"));
                break;
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "BrokerRunBinding"));
    }
}
