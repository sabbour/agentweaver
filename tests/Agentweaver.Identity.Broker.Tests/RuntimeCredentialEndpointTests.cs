using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

// This isolates the owner boundary; the complete current-Core/native-SDK proof is separate.
[Collection("IdentityBrokerPostgres")]
public sealed class RuntimeCredentialEndpointTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task BrokerIssuedBearerAndIndependentNonceAreRequiredAcrossTheRuntimeHttpLifecycle()
    {
        await using var idp = await FakeIdentityProvider.StartAsync();
        var owner = new EndpointStorageOwner();
        var delivery = new EndpointStorageDelivery();
        await using var factory = new IdentityBrokerWebApplicationFactory(
            await postgres.CreateMigratedDatabaseAsync(), idp,
            configure: settings =>
            {
                settings["IdentityBroker__RuntimeBootstrap__OrchestratorOwnerAddress"] = "https://orchestrator.test/";
                settings["IdentityBroker__RuntimeBootstrap__EnvironmentOwnerAddress"] = "https://environment.test/";
                settings["IdentityBroker__RuntimeBootstrap__BootstrapLifetime"] = "00:01:00";
                settings["IdentityBroker__RuntimeBootstrap__SourceLifetime"] = "00:02:00";
            },
            configureServices: services =>
            {
                services.RemoveAll<IRuntimeRegistrationOwner>();
                services.RemoveAll<IRuntimeBootstrapDelivery>();
                services.AddSingleton<IRuntimeRegistrationOwner>(owner);
                services.AddSingleton<IRuntimeBootstrapDelivery>(delivery);
            });
        using var broker = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://broker.test/")
        });
        var request = new RuntimeBootstrapRequest(
            owner.Registration.RuntimeInstanceId, Guid.NewGuid(), new string('a', 64));
        using (var unauthenticated = await broker.PostAsJsonAsync("/internal/runtime/bootstrap/request", request))
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(0, owner.Reads);
        using (var scope = factory.Services.CreateScope())
        {
            var user = await scope.ServiceProvider.GetRequiredService<BrokerUserProvisioner>().ProvisionAsync(
                FakeIdentityProvider.Authority, idp.Subject, "Runtime endpoint test", null, default);
            owner.Registration = owner.Registration with
            {
                Binding = owner.Registration.Binding with { ActorId = user.Id.ToString("D") }
            };
            await scope.ServiceProvider.GetRequiredService<IdentityGrantAuthority>().ReplaceAsync(
                new SecretRedemptionGrant("runtime-profile-binding", user.Id.ToString("D"), "project", "run",
                    "configure", new SecretRef("profile-reference", "v1"), GrantState.Active,
                    DateTimeOffset.UtcNow.AddMinutes(5)), 0, "runtime-profile-binding");
        }
        using var external = new HttpClient(idp.Server.CreateHandler())
        {
            BaseAddress = new Uri(FakeIdentityProvider.Authority)
        };
        var (verifier, challenge) = Pkce.Create();
        var code = await BrokerFlowDriver.AuthorizeWithConsentAsync(
            broker, external, IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri, "openid api.read", challenge,
            projectId: "project", runId: "run");
        var tokenResult = await BrokerFlowDriver.ExchangeCodeForTokensAsync(
            broker, IdentityBrokerWebApplicationFactory.TestClientId,
            IdentityBrokerWebApplicationFactory.TestClientRedirectUri, code, verifier);
        var token = tokenResult.GetProperty("access_token").GetString()!;
        var tokenExpiry = new JwtSecurityTokenHandler().ReadJwtToken(token).ValidTo;
        var actor = new RuntimeActorAuthorization(new SecretCredential(token, tokenExpiry), "tenant");
        delivery.VerifyPending = async (registration, currentActor, operation, grant, hash, nonce) =>
        {
            var pending = new RuntimePendingBootstrapHttpClient(broker, broker.BaseAddress!, currentActor);
            var proof = new RuntimeCredentialProof(grant, registration.RuntimeInstanceId, 1,
                RuntimeCredentialPurpose.Configure, registration.Binding.ConfigureEndpoint, hash, nonce);
            var receipt = await pending.VerifyPendingBootstrapDeliveryAsync(proof, operation, default);
            Assert.Equal(RuntimeCredentialState.Active, receipt.State);
            Assert.Equal(registration.Revision, receipt.RegistrationRevision);
        };
        using var bootstrapRequest = new HttpRequestMessage(HttpMethod.Post, "/internal/runtime/bootstrap/request")
        {
            Content = JsonContent.Create(request)
        };
        bootstrapRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        bootstrapRequest.Headers.Add("X-Agentweaver-Tenant", "tenant");
        using var bootstrapResponse = await broker.SendAsync(bootstrapRequest);
        Assert.True(bootstrapResponse.StatusCode == HttpStatusCode.OK,
            $"Bootstrap returned HTTP {(int)bootstrapResponse.StatusCode}: " +
            (bootstrapResponse.IsSuccessStatusCode ? "unexpected success" :
                await bootstrapResponse.Content.ReadAsStringAsync()));
        Assert.True(bootstrapResponse.Headers.CacheControl?.NoStore);
        var delivered = await bootstrapResponse.Content.ReadFromJsonAsync<RuntimeBootstrapDeliveryReceipt>();
        Assert.NotNull(delivered);
        Assert.NotNull(delivery.Credential);
        var bootstrap = new RuntimeCredentialProof(delivered.GrantId, delivered.RuntimeInstanceId, 1,
            RuntimeCredentialPurpose.Configure, owner.Registration.Binding.ConfigureEndpoint,
            delivered.ConfigurationHash, delivery.Credential);
        var runtime = new RuntimeBrokerCredentialClient(
            broker, broker.BaseAddress!, IdentityBrokerWebApplicationFactory.Issuer, actor, TimeProvider.System);
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => runtime.VerifySourceAsync(bootstrap, default));
        var wrongNonce = new RuntimeCredentialProof(bootstrap.GrantId, bootstrap.RuntimeInstanceId, 1,
            bootstrap.Purpose, bootstrap.Audience, bootstrap.ConfigurationHash,
            new SecretCredential(new string('f', 64), bootstrap.Credential.ExpiresAt));
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            runtime.ConsumeBootstrapAsync(wrongNonce, Guid.NewGuid(), default));
        var consumed = await runtime.ConsumeBootstrapAsync(bootstrap, Guid.NewGuid(), default);
        var exchange = await runtime.ExchangeBootstrapAsync(
            new RuntimeCredentialProof(bootstrap.GrantId, bootstrap.RuntimeInstanceId, consumed.Revision,
                bootstrap.Purpose, bootstrap.Audience, bootstrap.ConfigurationHash, bootstrap.Credential),
            Guid.NewGuid(), default);
        Assert.NotNull(exchange.Credential);
        var source = new RuntimeCredentialProof(
            exchange.Receipt.GrantId, exchange.Receipt.RuntimeInstanceId, exchange.Receipt.Revision,
            RuntimeCredentialPurpose.Observe, exchange.Receipt.Audience,
            exchange.Receipt.ConfigurationHash, exchange.Credential);
        Assert.Equal(exchange.Receipt, await runtime.VerifySourceAsync(source, default));
        var rotated = await runtime.RotateSourceAsync(source, Guid.NewGuid(), default);
        Assert.NotNull(rotated.Credential);
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => runtime.VerifySourceAsync(source, default));
        var current = new RuntimeCredentialProof(
            rotated.Receipt.GrantId, rotated.Receipt.RuntimeInstanceId, rotated.Receipt.Revision,
            RuntimeCredentialPurpose.Observe, rotated.Receipt.Audience,
            rotated.Receipt.ConfigurationHash, rotated.Credential);
        var ownerReads = owner.Reads;
        using (var expiredInput = new HttpRequestMessage(HttpMethod.Post, "/internal/runtime/source/verify")
        {
            Content = JsonContent.Create(new RuntimeCredentialHttpRequest(
                current.GrantId, current.RuntimeInstanceId, current.Revision, current.Purpose,
                current.Audience, current.ConfigurationHash, current.Credential.GetValue(),
                DateTimeOffset.UtcNow.AddSeconds(-1), Guid.Empty))
        })
        {
            expiredInput.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            expiredInput.Headers.Add("X-Agentweaver-Tenant", "tenant");
            using var rejected = await broker.SendAsync(expiredInput);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Equal(ownerReads, owner.Reads);
        }
        Assert.Equal(RuntimeCredentialState.Revoked,
            (await runtime.RevokeAsync(current, Guid.NewGuid(), default)).State);
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => runtime.VerifySourceAsync(current, default));
        using (var cookieOnly = await broker.PostAsJsonAsync("/internal/runtime/bootstrap/request", request))
            Assert.Equal(HttpStatusCode.Unauthorized, cookieOnly.StatusCode);
        foreach (var secret in new[]
        {
            token, bootstrap.Credential.GetValue(), exchange.Credential.GetValue(), rotated.Credential.GetValue()
        })
            Assert.DoesNotContain(secret, string.Join('\n', factory.LogMessages));
    }

    private sealed class EndpointStorageOwner : IRuntimeRegistrationOwner
    {
        public int Reads { get; private set; }
        public RuntimeRegistration Registration { get; set; } = new(
            Guid.NewGuid(), 1,
            new RuntimeBinding(
                "https://broker.test/", Guid.NewGuid().ToString("D"), "tenant", "project", "run", "session",
                "agent", "turn", 1, 1, 1, "context:1", new string('a', 64), 1, "environment", "placement", 1,
                "profile", new Uri("https://runtime.test/configure"),
                new Uri("https://orchestrator.test/internal/runtime/observations"))
            {
                EnvironmentCurrentFencingGeneration = 4,
                EnvironmentProviderFencingGeneration = 7
            },
            RuntimeRegistrationState.Active, DateTimeOffset.UtcNow.AddMinutes(5));

        public Task<RuntimeRegistration> ReadCurrentAsync(
            Guid runtimeInstanceId, RuntimeActorAuthorization actor, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = new JwtSecurityTokenHandler().ReadJwtToken(actor.Bearer.GetValue());
            if (runtimeInstanceId != Registration.RuntimeInstanceId ||
                token.Subject != Registration.Binding.ActorId || actor.TenantSelector != Registration.Binding.TenantId)
                throw new RuntimeAuthorizationException("runtime_owner_denied");
            Reads++;
            return Task.FromResult(Registration);
        }
    }

    private sealed class EndpointStorageDelivery : IRuntimeBootstrapDelivery
    {
        public SecretCredential? Credential { get; private set; }
        public Func<RuntimeRegistration, RuntimeActorAuthorization, Guid, Guid, string, SecretCredential, Task>?
            VerifyPending { get; set; }

        public async Task<RuntimeBootstrapDeliveryReceipt> DeliverAsync(
            RuntimeRegistration registration, RuntimeActorAuthorization actor, Guid operationId,
            Guid grantId, string configurationHash, SecretCredential credential, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Credential = new SecretCredential(credential.GetValue(), credential.ExpiresAt);
            if (VerifyPending is not null)
                await VerifyPending(registration, actor, operationId, grantId, configurationHash, credential);
            return new RuntimeBootstrapDeliveryReceipt(
                operationId, grantId, registration.RuntimeInstanceId, registration.Revision,
                registration.Binding.PlacementUid, registration.Binding.PlacementGeneration,
                registration.Binding.ExecutionFence, configurationHash, DateTimeOffset.UtcNow)
            {
                EnvironmentCurrentFencingGeneration = registration.Binding.EnvironmentCurrentFencingGeneration,
                EnvironmentProviderFencingGeneration = registration.Binding.EnvironmentProviderFencingGeneration
            };
        }
    }
}
