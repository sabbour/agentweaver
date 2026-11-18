using System.Net;
using System.Net.Security;
using System.Security.Claims;
using System.Security.Authentication;
using System.Text;
using System.Text.Encodings.Web;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class ModelProviderCredentialPublicationEndpointTests
{
    [Fact]
    public async Task PrivateEndpointRejectsOversizedAndDuplicateBodiesWithoutEchoingThem()
    {
        using var host = await StartHostAsync();
        using var client = host.GetTestClient();

        using var oversized = new HttpRequestMessage(HttpMethod.Post, ModelProviderCredentialPublicationEndpoints.Route)
        {
            Content = new StringContent(
                new string('x', ModelProviderCredentialPublicationEndpoints.MaximumRequestBodyBytes + 1),
                Encoding.UTF8,
                "application/json"),
        };
        using (var response = await client.SendAsync(oversized))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.DoesNotContain(new string('x', 64), await response.Content.ReadAsStringAsync());
        }

        var duplicateBody =
            $$"""{"operationId":"{{OperationId:D}}","expectedProviderRevision":7,"scope":"project","projectId":"project-1","providerId":"provider-1","credentialVersion":"0123456789abcdef0123456789abcdef","credentialVersion":"fedcba9876543210fedcba9876543210"}""";
        using var duplicate = new HttpRequestMessage(HttpMethod.Post, ModelProviderCredentialPublicationEndpoints.Route)
        {
            Content = new StringContent(duplicateBody, Encoding.UTF8, "application/json"),
        };
        using var duplicateResponse = await client.SendAsync(duplicate);
        Assert.Equal(HttpStatusCode.BadRequest, duplicateResponse.StatusCode);
        Assert.Equal("no-store", duplicateResponse.Headers.CacheControl?.ToString());
        Assert.DoesNotContain("0123456789abcdef", await duplicateResponse.Content.ReadAsStringAsync());

        var callerPricing =
            $$$"""{"operationId":"{{{OperationId:D}}}","expectedProviderRevision":7,"scope":"project","projectId":"project-1","providerId":"provider-1","credentialVersion":"0123456789abcdef0123456789abcdef","rateCardId":"caller-card","rateCardVersion":"caller-version","rateCardPrices":{"gpt-4.1|input":0},"rateCardHash":"caller-hash","provenance":{"source":"manual"}}""";
        using var callerPricingRequest = new HttpRequestMessage(
            HttpMethod.Post, ModelProviderCredentialPublicationEndpoints.Route)
        {
            Content = new StringContent(callerPricing, Encoding.UTF8, "application/json"),
        };
        using var callerPricingResponse = await client.SendAsync(callerPricingRequest);
        Assert.Equal(HttpStatusCode.BadRequest, callerPricingResponse.StatusCode);
        Assert.Equal("no-store", callerPricingResponse.Headers.CacheControl?.ToString());
        Assert.DoesNotContain("caller-card", await callerPricingResponse.Content.ReadAsStringAsync());

        var chunked = new HttpRequestMessage(HttpMethod.Post, ModelProviderCredentialPublicationEndpoints.Route)
        {
            Content = new UnknownLengthContent(Encoding.UTF8.GetBytes(
                new string('x', ModelProviderCredentialPublicationEndpoints.MaximumRequestBodyBytes * 2))),
        };
        chunked.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var chunkedResponse = await client.SendAsync(chunked);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunkedResponse.StatusCode);
        Assert.Equal("no-store", chunkedResponse.Headers.CacheControl?.ToString());
    }

    private static async Task<IHost> StartHostAsync() =>
        await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthentication(PublicationTestAuthenticationHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, PublicationTestAuthenticationHandler>(
                            PublicationTestAuthenticationHandler.SchemeName, _ => { });
                    services.AddAuthorization(options =>
                        options.AddPolicy(
                            ProjectAuthorizationOwner.BrokerCredentialPublicationAuthorizationPolicy,
                            policy => policy
                                .AddAuthenticationSchemes(PublicationTestAuthenticationHandler.SchemeName)
                                .RequireAuthenticatedUser()));
                    services.AddSingleton(new ModelProviderDraftService(null!, TimeProvider.System));
                });
                web.Configure(app =>
                {
                    app.Use(async (context, next) =>
                    {
                        context.Request.Scheme = "https";
                        context.Features.Set<ITlsHandshakeFeature>(new TestTlsHandshakeFeature());
                        await next(context);
                    });
                    app.UseRouting();
                    app.UseModelProviderCredentialPublicationNoStore();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                        endpoints.MapModelProviderCredentialPublicationEndpoints(enabled: true));
                });
            })
            .StartAsync();

    private static readonly Guid OperationId = Guid.Parse("ad451e41-9b48-412f-99dc-ad6a6cd4080c");

    private sealed class PublicationTestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        internal const string SchemeName = "PublicationTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sub", "identity-broker"),
                new Claim("purpose", "credential-publication"),
                new Claim("actor_id", "actor-1"),
                new Claim("operation_id", OperationId.ToString("D")),
                new Claim("expected_provider_revision", "7"),
                new Claim("scope", "project"),
                new Claim("project_id", "project-1"),
                new Claim("provider_id", "provider-1"),
                new Claim("credential_version", "0123456789abcdef0123456789abcdef"),
                new Claim("credential_slot_id", "0ef1ea5e-d94b-41d0-9380-900b1c532bf2"),
                new Claim("credential_secret_id", "credential-secret"),
            ], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private sealed class TestTlsHandshakeFeature : ITlsHandshakeFeature
    {
        public SslProtocols Protocol => SslProtocols.Tls13;
        public string HostName => "localhost";
#pragma warning disable SYSLIB0058
        public CipherAlgorithmType CipherAlgorithm => default;
        public int CipherStrength => 0;
        public HashAlgorithmType HashAlgorithm => default;
        public int HashStrength => 0;
        public ExchangeAlgorithmType KeyExchangeAlgorithm => default;
#pragma warning restore SYSLIB0058
        public int KeyExchangeStrength => 0;
        public TlsCipherSuite? NegotiatedCipherSuite => null;
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken) =>
            stream.WriteAsync(bytes, cancellationToken).AsTask();
    }
}
