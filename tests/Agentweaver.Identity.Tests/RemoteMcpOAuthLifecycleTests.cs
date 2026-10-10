using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Xunit;

namespace Agentweaver.Identity.Tests;

public sealed class RemoteMcpOAuthLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BindingCanonicalizesScopesAndPinsEveryOwnerAndResourceField()
    {
        var binding = Binding(scopes: ["tools.read", "tools.list"]);
        var reordered = Binding(scopes: ["tools.list", "tools.read"]);

        Assert.Equal(binding.BindingHash, reordered.BindingHash);
        Assert.Equal(["tools.list", "tools.read"], binding.Scopes);
        Assert.All(
            new[]
            {
                Binding(humanId: "other-human"),
                Binding(tenantId: "other-tenant"),
                Binding(projectId: "other-project"),
                Binding(connectionId: Guid.NewGuid()),
                Binding(configurationRevision: 2),
                Binding(environmentConfigurationHash: new string('b', 64)),
                Binding(identityBindingReference: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
                Binding(endpoint: new Uri("https://mcp.example.test/other-endpoint")),
                Binding(resource: new Uri("https://mcp.example.test/other")),
                Binding(issuer: new Uri("https://issuer.example.test/other")),
                Binding(redirectUri: new Uri("https://identity.example.test/other")),
                Binding(scopes: ["tools.read"])
            },
            changed => Assert.False(binding.Matches(changed)));

        var scopes = Assert.IsAssignableFrom<IList<string>>(binding.Scopes);
        Assert.Throws<NotSupportedException>(() => scopes[0] = "tools.write");
    }

    [Theory]
    [InlineData("http://mcp.example.test/resource")]
    [InlineData("https://user@mcp.example.test/resource")]
    [InlineData("https://mcp.example.test/resource#fragment")]
    [InlineData("https://MCP.example.test/resource")]
    public void BindingRejectsNoncanonicalOrUnsafeHttpsUris(string uri)
    {
        Assert.Throws<ArgumentException>(() => Binding(resource: new Uri(uri)));
    }

    [Fact]
    public void BindingRejectsAnEmptyScopeSet()
    {
        Assert.Throws<ArgumentException>(() => Binding(scopes: []));
    }

    [Fact]
    public void BindingRejectsUnsupportedTransportProfiles()
    {
        Assert.Throws<ArgumentException>(() => Binding(transportProfile: "mcp-other-profile"));
    }

    [Theory]
    [InlineData("tools.read,tools.read")]
    [InlineData("tools read")]
    [InlineData("tools\\read")]
    public void BindingRejectsInvalidScopeSets(string scopes)
    {
        Assert.Throws<ArgumentException>(() => Binding(scopes: scopes.Split(',', StringSplitOptions.None)));
    }

    [Fact]
    public void BindingReferenceMustBeIdentityIssuedOpaqueUuid()
    {
        Assert.Throws<ArgumentException>(() => Binding(identityBindingReference: "caller-chosen"));
        Assert.Throws<ArgumentException>(() =>
            Binding(identityBindingReference: "00000000000000000000000000000000"));
    }

    [Fact]
    public void ConsentMaterialUsesS256AndStoresOnlyStateHashAndProtectedVerifierReference()
    {
        var binding = Binding();
        var material = Material(binding);
        var state = material.State.GetValue();
        var verifier = material.PkceVerifier.GetValue();
        var verifierReference = new SecretRef("remote-mcp-verifier", "v3");
        var pending = material.CreatePendingConsent(2, verifierReference);

        Assert.Equal(43, state.Length);
        Assert.Equal(43, verifier.Length);
        Assert.Equal(
            Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            material.PkceChallenge);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(state))).ToLowerInvariant(),
            pending.StateHash);
        Assert.Equal(verifierReference, pending.ProtectedVerifierReference);
        foreach (var value in new object[] { material, pending })
        {
            var json = JsonSerializer.Serialize(value);
            Assert.DoesNotContain(state, json, StringComparison.Ordinal);
            Assert.DoesNotContain(verifier, json, StringComparison.Ordinal);
            Assert.DoesNotContain(state, value.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(verifier, value.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ConsentCallbackIsClaimedOnceAndConsumesOnlyAfterCurrentBindingCompletes()
    {
        var binding = Binding();
        var material = Material(binding);
        var start = BeginConsent(binding, material);
        var claimId = Guid.NewGuid();

        var claimed = RemoteMcpOAuthLifecycle.TryClaimCallback(
            start.Consent, start.Connection, binding, material.State.GetValue(),
            claimId, start.Consent.Revision, Now);

        Assert.NotNull(claimed);
        Assert.Equal(RemoteMcpOAuthConsentState.CallbackClaimed, claimed.State);
        Assert.Null(RemoteMcpOAuthLifecycle.TryClaimCallback(
            claimed, start.Connection, binding, material.State.GetValue(),
            Guid.NewGuid(), claimed.Revision, Now));

        var completion = RemoteMcpOAuthLifecycle.TryCompleteCallback(
            claimed, start.Connection, binding, claimId, claimed.Revision,
            new SecretRef("remote-mcp-access", "v1"),
            new SecretRef("remote-mcp-refresh", "v1"),
            Now.AddHours(1), Now);

        Assert.NotNull(completion);
        Assert.Equal(RemoteMcpOAuthConnectionState.Authorized, completion.Connection.State);
        Assert.Equal(RemoteMcpOAuthConsentState.Consumed, completion.Consent.State);
        Assert.True(RemoteMcpOAuthLifecycle.IsCurrentAccessTokenUsable(
            completion.Connection, binding, Now));
    }

    [Fact]
    public void CallbackCannotPublishAfterDisconnectOrConfigurationChange()
    {
        var binding = Binding();
        var material = Material(binding);
        var start = BeginConsent(binding, material);
        var claimId = Guid.NewGuid();
        var claimed = RemoteMcpOAuthLifecycle.TryClaimCallback(
            start.Consent, start.Connection, binding, material.State.GetValue(),
            claimId, start.Consent.Revision, Now)!;
        var disconnected = RemoteMcpOAuthLifecycle.TryDisconnect(
            start.Connection, binding, start.Connection.Revision)!;
        var changedBinding = Binding(configurationRevision: 2);

        Assert.Null(RemoteMcpOAuthLifecycle.TryCompleteCallback(
            claimed, disconnected, binding, claimId, claimed.Revision,
            new SecretRef("remote-mcp-access", "v2"),
            new SecretRef("remote-mcp-refresh", "v2"),
            Now.AddHours(1), Now));
        Assert.Null(RemoteMcpOAuthLifecycle.TryClaimCallback(
            start.Consent, start.Connection, changedBinding, material.State.GetValue(),
            Guid.NewGuid(), start.Consent.Revision, Now));
        Assert.Equal(RemoteMcpOAuthConnectionState.Disconnected, disconnected.State);
        Assert.Null(disconnected.AccessTokenReference);
        Assert.Null(disconnected.RefreshTokenReference);
    }

    [Fact]
    public void CallbackCannotBeClaimedAfterOwnerOrResourceBindingChanges()
    {
        var binding = Binding();
        var material = Material(binding);
        var start = BeginConsent(binding, material);
        var changedBindings = new[]
        {
            Binding(humanId: "other-human"),
            Binding(tenantId: "other-tenant"),
            Binding(projectId: "other-project"),
            Binding(scopes: ["tools.read"]),
            Binding(endpoint: new Uri("https://mcp.example.test/other-endpoint")),
            Binding(resource: new Uri("https://mcp.example.test/other-resource")),
            Binding(issuer: new Uri("https://issuer.example.test/other")),
            Binding(redirectUri: new Uri("https://identity.example.test/other-callback")),
            Binding(configurationRevision: 2),
            Binding(environmentConfigurationHash: new string('b', 64)),
            Binding(identityBindingReference: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
        };

        Assert.All(changedBindings, changedBinding =>
            Assert.Null(RemoteMcpOAuthLifecycle.TryClaimCallback(
                start.Consent, start.Connection, changedBinding, material.State.GetValue(),
                Guid.NewGuid(), start.Consent.Revision, Now)));
    }

    [Fact]
    public void CallbackFailureIsRetryableOnlyWhenTransportProvesDefinitelyNotSent()
    {
        var binding = Binding();
        var material = Material(binding);
        var start = BeginConsent(binding, material);
        var claimId = Guid.NewGuid();
        var claimed = RemoteMcpOAuthLifecycle.TryClaimCallback(
            start.Consent, start.Connection, binding, material.State.GetValue(),
            claimId, start.Consent.Revision, Now)!;

        var retryable = RemoteMcpOAuthLifecycle.TryResolveCallbackFailure(
            claimed, start.Connection, binding, claimId, claimed.Revision,
            RemoteMcpOAuthRequestFailure.DefinitelyNotSent, Now);
        var consumed = RemoteMcpOAuthLifecycle.TryResolveCallbackFailure(
            claimed, start.Connection, binding, claimId, claimed.Revision,
            RemoteMcpOAuthRequestFailure.PossiblySent, Now);
        var denied = RemoteMcpOAuthLifecycle.TryResolveCallbackFailure(
            claimed, start.Connection, binding, claimId, claimed.Revision,
            RemoteMcpOAuthRequestFailure.ProviderRejected, Now);

        Assert.Equal(RemoteMcpOAuthConsentState.Pending, retryable!.State);
        Assert.Equal(RemoteMcpOAuthConsentState.Consumed, consumed!.State);
        Assert.Equal(RemoteMcpOAuthConsentState.Denied, denied!.State);
    }

    [Fact]
    public void RefreshRequiresExactAttemptRevisionAndCredentialRevision()
    {
        var binding = Binding();
        var current = Authorized(binding);
        var claim = RemoteMcpOAuthLifecycle.TryBeginRefresh(
            current, binding, current.Revision, Guid.NewGuid(), Now, out var inProgress);

        Assert.NotNull(claim);
        Assert.NotNull(inProgress);
        Assert.Equal(current.CredentialRevision, claim.CredentialRevision);
        Assert.Null(RemoteMcpOAuthLifecycle.TryBeginRefresh(
            inProgress, binding, current.Revision, Guid.NewGuid(), Now, out _));
        var rotated = RemoteMcpOAuthLifecycle.TryCompleteRefresh(
            inProgress, binding, claim, claim.ConnectionRevision,
            new SecretRef("remote-mcp-access", "v2"),
            new SecretRef("remote-mcp-refresh", "v2"),
            Now.AddHours(2), Now);

        Assert.NotNull(rotated);
        Assert.Equal(current.CredentialRevision + 1, rotated.CredentialRevision);
        Assert.Equal(current.Revision + 2, rotated.Revision);
        Assert.Equal("v2", rotated.RefreshTokenReference!.Version);
    }

    [Fact]
    public void PossibleRefreshSendBecomesIndeterminateAndCannotBeUsedOrRetried()
    {
        var binding = Binding();
        var current = Authorized(binding);
        var claim = RemoteMcpOAuthLifecycle.TryBeginRefresh(
            current, binding, current.Revision, Guid.NewGuid(), Now, out var inProgress)!;
        var uncertain = RemoteMcpOAuthLifecycle.TryResolveRefreshFailure(
            inProgress!, binding, claim, claim.ConnectionRevision,
            RemoteMcpOAuthRequestFailure.PossiblySent)!;

        Assert.Equal(RemoteMcpOAuthConnectionState.RefreshIndeterminate, uncertain.State);
        Assert.False(RemoteMcpOAuthLifecycle.IsCurrentAccessTokenUsable(uncertain, binding, Now));
        Assert.Null(RemoteMcpOAuthLifecycle.TryBeginRefresh(
            uncertain, binding, uncertain.Revision, Guid.NewGuid(), Now, out _));
        Assert.Null(RemoteMcpOAuthLifecycle.TryCompleteRefresh(
            uncertain, binding, claim, uncertain.Revision,
            new SecretRef("remote-mcp-access", "v3"),
            new SecretRef("remote-mcp-refresh", "v3"),
            Now.AddHours(3), Now));
    }

    [Fact]
    public void DisconnectPreventsLateRefreshCompletionAndDefiniteNotSentRestore()
    {
        var binding = Binding();
        var current = Authorized(binding);
        var claim = RemoteMcpOAuthLifecycle.TryBeginRefresh(
            current, binding, current.Revision, Guid.NewGuid(), Now, out var inProgress)!;
        var disconnected = RemoteMcpOAuthLifecycle.TryDisconnect(
            inProgress!, binding, claim.ConnectionRevision)!;

        Assert.Null(RemoteMcpOAuthLifecycle.TryCompleteRefresh(
            disconnected, binding, claim, disconnected.Revision,
            new SecretRef("remote-mcp-access", "v2"),
            new SecretRef("remote-mcp-refresh", "v2"),
            Now.AddHours(2), Now));
        Assert.Null(RemoteMcpOAuthLifecycle.TryResolveRefreshFailure(
            disconnected, binding, claim, disconnected.Revision,
            RemoteMcpOAuthRequestFailure.DefinitelyNotSent));
        Assert.Equal(RemoteMcpOAuthConnectionState.Disconnected, disconnected.State);
        Assert.Null(disconnected.AccessTokenReference);
        Assert.Null(disconnected.RefreshTokenReference);
    }

    [Fact]
    public void DefinitelyNotSentRefreshRestoresOnlyItsCurrentClaim()
    {
        var binding = Binding();
        var current = Authorized(binding);
        var claim = RemoteMcpOAuthLifecycle.TryBeginRefresh(
            current, binding, current.Revision, Guid.NewGuid(), Now, out var inProgress)!;
        var restored = RemoteMcpOAuthLifecycle.TryResolveRefreshFailure(
            inProgress!, binding, claim, claim.ConnectionRevision,
            RemoteMcpOAuthRequestFailure.DefinitelyNotSent);

        Assert.Equal(RemoteMcpOAuthConnectionState.Authorized, restored!.State);
        Assert.Equal(current.CredentialRevision, restored.CredentialRevision);
        Assert.Equal(current.AccessTokenReference, restored.AccessTokenReference);
        Assert.Equal(current.RefreshTokenReference, restored.RefreshTokenReference);
        Assert.False(RemoteMcpOAuthLifecycle.IsCurrentAccessTokenUsable(
            restored, Binding(configurationRevision: 2), Now));
    }

    [Fact]
    public void AccessIsUnavailableWhenExpiredOrConnectionStateIsNotAuthorized()
    {
        var binding = Binding();
        var current = Authorized(binding);
        var expired = new RemoteMcpOAuthConnection(
            binding, current.Revision + 1, current.CredentialRevision,
            RemoteMcpOAuthConnectionState.Authorized, current.AccessTokenReference,
            current.RefreshTokenReference, Now);

        Assert.False(RemoteMcpOAuthLifecycle.IsCurrentAccessTokenUsable(expired, binding, Now));
        Assert.False(RemoteMcpOAuthLifecycle.IsCurrentAccessTokenUsable(
            RemoteMcpOAuthLifecycle.TryDisconnect(current, binding, current.Revision)!, binding, Now));
    }

    [Fact]
    public void ExpiredRefreshReferenceCannotBeClaimedAndProviderRejectionRevokes()
    {
        var binding = Binding();
        var current = new RemoteMcpOAuthConnection(
            binding, 5, 3, RemoteMcpOAuthConnectionState.Authorized,
            new SecretRef("remote-mcp-access", "v1"),
            new SecretRef("remote-mcp-refresh", "v1"),
            Now.AddHours(1),
            refreshTokenExpiresAt: Now);
        Assert.Null(RemoteMcpOAuthLifecycle.TryBeginRefresh(
            current, binding, current.Revision, Guid.NewGuid(), Now, out _));

        current = Authorized(binding);
        var claim = RemoteMcpOAuthLifecycle.TryBeginRefresh(
            current, binding, current.Revision, Guid.NewGuid(), Now, out var inProgress)!;
        var revoked = RemoteMcpOAuthLifecycle.TryResolveRefreshFailure(
            inProgress!, binding, claim, claim.ConnectionRevision,
            RemoteMcpOAuthRequestFailure.ProviderRejected)!;

        Assert.Equal(RemoteMcpOAuthConnectionState.Revoked, revoked.State);
        Assert.Null(revoked.AccessTokenReference);
        Assert.Null(revoked.RefreshTokenReference);
    }

    private static RemoteMcpOAuthConnectionBinding Binding(
        string humanId = "human-1",
        string tenantId = "tenant-1",
        string projectId = "project-1",
        Guid? connectionId = null,
        long configurationRevision = 1,
        string environmentConfigurationHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        string identityBindingReference = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        Uri? endpoint = null,
        Uri? resource = null,
        Uri? issuer = null,
        Uri? redirectUri = null,
        string transportProfile = "mcp-2025-06-18",
        string[]? scopes = null) =>
        new(humanId, tenantId, projectId, connectionId ?? Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            configurationRevision, environmentConfigurationHash, identityBindingReference,
            endpoint ?? new Uri("https://mcp.example.test/"), resource ?? new Uri("https://mcp.example.test/resource"),
            issuer ?? new Uri("https://issuer.example.test/"),
            redirectUri ?? new Uri("https://identity.example.test/oauth/callback"),
            transportProfile,
            scopes ?? ["tools.list", "tools.read"]);

    private static RemoteMcpOAuthConsentMaterial Material(RemoteMcpOAuthConnectionBinding binding) =>
        RemoteMcpOAuthConsentMaterial.Create(
            binding, Guid.NewGuid(), Now.AddMinutes(5), new TestClock(Now));

    private static RemoteMcpOAuthConsentStart BeginConsent(
        RemoteMcpOAuthConnectionBinding binding,
        RemoteMcpOAuthConsentMaterial material)
    {
        var initial = new RemoteMcpOAuthConnection(
            binding, 1, 0, RemoteMcpOAuthConnectionState.NotConnected);
        return RemoteMcpOAuthLifecycle.TryBeginConsent(
            initial, binding, initial.Revision, material,
            new SecretRef("remote-mcp-verifier", "v1"))!;
    }

    private static RemoteMcpOAuthConnection Authorized(RemoteMcpOAuthConnectionBinding binding) =>
        new(binding, 5, 3, RemoteMcpOAuthConnectionState.Authorized,
            new SecretRef("remote-mcp-access", "v1"),
            new SecretRef("remote-mcp-refresh", "v1"),
            Now.AddHours(1));

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
