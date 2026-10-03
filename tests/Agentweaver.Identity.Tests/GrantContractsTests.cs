using System.Text.Json;
using Agentweaver.Abstractions;
using Xunit;

namespace Agentweaver.Identity.Tests;

public sealed class GrantContractsTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly TestClock _clock = new(Now);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("admin/*")]
    [InlineData("has space")]
    public void ActorAndGrantRejectNonOpaqueIdentifiers(string? invalid)
    {
        Assert.Throws<ArgumentException>(() => new TrustedActorContext(invalid!, "project", "run"));
        Assert.Throws<ArgumentException>(() => new TrustedActorContext("actor", invalid!, "run"));
        Assert.Throws<ArgumentException>(() => new TrustedActorContext("actor", "project", invalid!));
        Assert.Throws<ArgumentException>(() => Grant(id: invalid!));
        Assert.Throws<ArgumentException>(() => Grant(revision: invalid!));
        Assert.Throws<ArgumentException>(() => new SecretRedemptionGrant(
            "grant", invalid!, "project", "run", "purpose", new SecretRef("secret", "v1"),
            GrantState.Active, Now.AddMinutes(1), _clock));
        Assert.Throws<ArgumentException>(() => new SecretRedemptionGrant(
            "grant", "actor", invalid!, "run", "purpose", new SecretRef("secret", "v1"),
            GrantState.Active, Now.AddMinutes(1), _clock));
        Assert.Throws<ArgumentException>(() => new SecretRedemptionGrant(
            "grant", "actor", "project", invalid!, "purpose", new SecretRef("secret", "v1"),
            GrantState.Active, Now.AddMinutes(1), _clock));
        Assert.Throws<ArgumentException>(() => new SecretRedemptionGrant(
            "grant", "actor", "project", "run", invalid!, new SecretRef("secret", "v1"),
            GrantState.Active, Now.AddMinutes(1), _clock));
    }

    [Fact]
    public void GrantRequiresReferenceKnownStateAndFutureExpiry()
    {
        Assert.Throws<ArgumentNullException>(() => new SecretRedemptionGrant(
            "grant", "actor", "project", "run", "purpose", null!,
            GrantState.Active, Now.AddMinutes(1), _clock));
        Assert.Throws<ArgumentOutOfRangeException>(() => Grant(state: (GrantState)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Grant(expiry: Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => Grant(expiry: Now.AddSeconds(-1)));
    }

    [Fact]
    public void GrantActorAndReferenceAreImmutableAndContainOnlyMetadata()
    {
        foreach (var type in new[] { typeof(TrustedActorContext), typeof(SecretRedemptionGrant), typeof(SecretRef) })
        {
            Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod));
            Assert.Empty(type.GetFields());
        }
        var grant = Grant(revision: "r2");
        Assert.Equal("r2", grant.Revision);
        Assert.Contains("\"Revision\":\"r2\"", JsonSerializer.Serialize(grant), StringComparison.Ordinal);
        Assert.DoesNotContain("GetValue", JsonSerializer.Serialize(grant), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("project")]
    [InlineData("run")]
    [InlineData("purpose")]
    [InlineData("secret")]
    [InlineData("version")]
    [InlineData("null-list")]
    [InlineData("null-grant")]
    public async Task ExactCaseAndNullBindingsDenyBeforeBackendAccess(string field)
    {
        var grant = new SecretRedemptionGrant(
            "grant", field == "actor" ? "Actor" : "actor", field == "project" ? "Project" : "project",
            field == "run" ? "Run" : "run", field == "purpose" ? "Purpose" : "purpose",
            new SecretRef(field == "secret" ? "Secret" : "secret", field == "version" ? "V1" : "v1"),
            GrantState.Active, Now.AddMinutes(1), _clock);
        var authority = new FakeGrantAuthority(() => field switch
        {
            "null-list" => null!,
            "null-grant" => [null!],
            _ => [grant],
        });
        var backend = new FakeSecretRedemption((_, _) =>
            throw new InvalidOperationException("Backend must not be called."));
        var redemption = new AuthorizedSecretRedemption(
            new TrustedActorContext("actor", "project", "run"), authority, backend, _clock);
        await Assert.ThrowsAsync<SecretAuthorizationDeniedException>(() => redemption.RedeemAsync(
            new SecretRedemptionRequest(new SecretRef("secret", "v1"), "purpose", "run"),
            CancellationToken.None));
        Assert.Equal(0, backend.Calls);
    }

    private SecretRedemptionGrant Grant(
        string id = "grant", string revision = "1", GrantState state = GrantState.Active,
        DateTimeOffset? expiry = null) =>
        new(id, "actor", "project", "run", "purpose", new SecretRef("secret", "v1"),
            state, expiry ?? Now.AddMinutes(1), _clock, revision);
}
