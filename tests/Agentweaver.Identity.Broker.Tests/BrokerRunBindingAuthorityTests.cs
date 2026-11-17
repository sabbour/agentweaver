using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Agentweaver.Identity.Broker;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class BrokerRunBindingAuthorityTests
{
    private const string Issuer = "https://broker.test/";
    private const string Audience = "https://projects-private.test/";
    private const string ProjectId = "7fc0a64da91e4f7188c232f7f8dc8531";
    private const string RunId = "run-1";
    private static readonly Guid UserId = Guid.Parse("7fc0a64d-a91e-4f71-88c2-32f7f8dc8531");

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "orchestrator_role_required", "OrchestratorRoleRequired")]
    [InlineData(HttpStatusCode.NotFound, "run_selection_not_found", "RunSelectionNotFound")]
    [InlineData(HttpStatusCode.Forbidden, "run_selection_not_found", null)]
    [InlineData(HttpStatusCode.NotFound, "orchestrator_role_required", null)]
    [InlineData(HttpStatusCode.Forbidden, "forbidden", null)]
    [InlineData(HttpStatusCode.NotFound, "not_found", null)]
    public async Task DeniedReasonUsesOnlyAllowlistedCodesAndMatchingStatuses(
        HttpStatusCode statusCode,
        string code,
        string? expectedReason)
    {
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest(
            "CN=broker.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var body = JsonSerializer.Serialize(new { code, detail = "Projects text must not reach the caller." });
        var handler = new OwnerResponseHandler(body, statusCode, "application/problem+json");
        using var client = new HttpClient(handler);
        var authority = new BrokerRunBindingAuthority(
            CreateOptions(), certificate, client, TimeProvider.System);

        var result = await authority.CheckAsync(UserId, ProjectId, RunId, null, CancellationToken.None);

        Assert.Equal(BrokerRunBindingStatus.Denied, result.Status);
        Assert.Null(result.Receipt);
        Assert.Equal(expectedReason, result.ReasonCode?.ToString());
    }

    [Fact]
    public async Task DenialReasonRequiresNoStoreProblemResponse()
    {
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest(
            "CN=broker.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var handler = new OwnerResponseHandler(
            """{"code":"orchestrator_role_required"}""",
            HttpStatusCode.Forbidden,
            "application/problem+json",
            noStore: false);
        using var client = new HttpClient(handler);
        var authority = new BrokerRunBindingAuthority(
            CreateOptions(), certificate, client, TimeProvider.System);

        var result = await authority.CheckAsync(UserId, ProjectId, RunId, null, CancellationToken.None);

        Assert.Equal(BrokerRunBindingStatus.Denied, result.Status);
        Assert.Null(result.ReasonCode);
    }

    [Fact]
    public async Task MalformedDenialReasonKeepsGenericDenial()
    {
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest(
            "CN=broker.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var handler = new OwnerResponseHandler(
            """{"code":"\uD800"}""",
            HttpStatusCode.Forbidden,
            "application/problem+json");
        using var client = new HttpClient(handler);
        var authority = new BrokerRunBindingAuthority(
            CreateOptions(), certificate, client, TimeProvider.System);

        var result = await authority.CheckAsync(UserId, ProjectId, RunId, null, CancellationToken.None);

        Assert.Equal(BrokerRunBindingStatus.Denied, result.Status);
        Assert.Null(result.ReasonCode);
    }

    [Fact]
    public async Task StalledDenialBodyKeepsGenericDenial()
    {
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest(
            "CN=broker.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var client = new HttpClient(new StalledOwnerResponseHandler());
        var authority = new BrokerRunBindingAuthority(
            CreateOptions(), certificate, client, TimeProvider.System);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var result = await authority.CheckAsync(UserId, ProjectId, RunId, null, cancellation.Token);

        Assert.Equal(BrokerRunBindingStatus.Denied, result.Status);
        Assert.Null(result.ReasonCode);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    public async Task InvalidOwnerReceiptReturnsUnavailable(string selectionHashState)
    {
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest(
            "CN=broker.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var handler = new OwnerResponseHandler(CreateReceiptJson(selectionHashState));
        using var client = new HttpClient(handler);
        var authority = new BrokerRunBindingAuthority(
            CreateOptions(), certificate, client, TimeProvider.System);

        var result = await authority.CheckAsync(UserId, ProjectId, RunId, null, CancellationToken.None);

        Assert.Equal(BrokerRunBindingStatus.Unavailable, result.Status);
        Assert.Null(result.Receipt);
    }

    [Fact]
    public async Task QueryUsesTypedShortLivedTicketAndAcceptsValidOwnerReceipt()
    {
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest(
            "CN=broker.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var handler = new OwnerResponseHandler(CreateReceiptJson("valid"));
        using var client = new HttpClient(handler);
        var authority = new BrokerRunBindingAuthority(
            CreateOptions(), certificate, client, TimeProvider.System);

        var result = await authority.CheckAsync(UserId, ProjectId, RunId, null, CancellationToken.None);

        Assert.Equal(BrokerRunBindingStatus.Authorized, result.Status);
        Assert.NotNull(result.Receipt);
        Assert.Equal(
            new Uri("https://projects.test/internal/projects/" + ProjectId +
                "/runs/" + RunId + "/broker-run-binding"),
            handler.RequestUri);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.False(handler.HasBody);
        Assert.NotNull(handler.BearerToken);
        var token = new JwtSecurityTokenHandler().ReadJwtToken(handler.BearerToken);
        Assert.Equal(BrokerRunBindingAuthority.JwtType, token.Header.Typ);
        Assert.Equal(Audience, Assert.Single(token.Audiences));
        Assert.True(token.Payload.TryGetValue("iat", out var issuedAt));
        Assert.True(token.Payload.TryGetValue("nbf", out _));
        Assert.True(token.Payload.TryGetValue("exp", out _));
        Assert.IsType<long>(issuedAt);
    }

    [Fact]
    public async Task ChangedOwnerReceiptDeniesPreviouslyAuthorizedBinding()
    {
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest(
            "CN=broker.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var handler = new OwnerResponseHandler(CreateReceiptJson("valid"));
        using var client = new HttpClient(handler);
        var authority = new BrokerRunBindingAuthority(
            CreateOptions(), certificate, client, TimeProvider.System);
        var expectedReceipt = new BrokerRunBindingReceipt(
            Issuer,
            UserId.ToString("D"),
            "tenant-1",
            Guid.Parse("3e152378-43a3-4ac4-8db9-d2ce1361800b"),
            1,
            ProjectId,
            RunId,
            Guid.Parse("b360b9ed-27b4-4f2a-bc0b-1ab842cbcf4a"),
            1,
            1,
            1,
            1,
            "context-v1",
            new string('B', 64));

        var result = await authority.CheckAsync(
            UserId, ProjectId, RunId, expectedReceipt, CancellationToken.None);

        Assert.Equal(BrokerRunBindingStatus.Denied, result.Status);
        Assert.Null(result.Receipt);
    }

    [Theory]
    [InlineData("projects.orchestrator", "projects-owner", true)]
    [InlineData("projects.orchestrator", "secret-grant", false)]
    [InlineData("projects.orchestrator", null, false)]
    [InlineData("api.read", "secret-grant", true)]
    [InlineData("api.read", "projects-owner", false)]
    public void AuthorityKindMustMatchRunScopes(
        string scope,
        string? authorityKind,
        bool expected)
    {
        Assert.Equal(
            expected,
            BrokerRunBindingAuthority.IsAuthorityKindConsistentWithScopes(authorityKind, [scope]));
    }

    [Fact]
    public void MissingRunBindingKeepsTheOptionalCompositionDisabled()
    {
        var options = CreateOptions();
        options.RunBinding = null;

        Assert.Null(BrokerRunBindingAuthority.LoadTlsConfiguration(options));
    }

    [Fact]
    public void PartialRunBindingSettingsFailStartupValidation()
    {
        var options = CreateOptions();
        options.RunBinding!.ProjectsServerCertificateSha256 = null;

        Assert.Throws<InvalidOperationException>(
            () => BrokerRunBindingAuthority.ValidateTlsConfiguration(options));
    }

    [Fact]
    public void CompleteRunBindingSettingsLoadThePinnedCaBundle()
    {
        using var testAuthority = BrokerRunBindingTlsIdentityTests.TestCertificateAuthority.Create();
        var runBinding = testAuthority.CreateOptions();
        runBinding.ProjectsOwnerAddress = "https://projects.test/";
        runBinding.ProjectsAudience = Audience;
        var options = CreateOptions();
        options.RunBinding = runBinding;

        using var tlsIdentity = BrokerRunBindingTlsIdentity.Load(
            runBinding, testAuthority.TrustBundleBytes);

        Assert.NotNull(tlsIdentity);
    }

    internal static IdentityBrokerOptions CreateOptions() => new()
    {
        Issuer = Issuer,
        Signing = new SigningCredentialOptions { PfxPath = "unused", PfxPassword = "unused" },
        ExternalProvider = new ExternalProviderOptions
        {
            Authority = "https://idp.test/",
            ClientId = "idp-client",
        },
        SecretRedemption = new SecretRedemptionOptions
        {
            Audience = "https://secrets.test/",
            VaultUri = "https://vault.test/",
            WorkloadIdentityTenantId = "tenant",
            WorkloadIdentityClientId = "client",
            WorkloadIdentityTokenFilePath = "token",
        },
        RunBinding = new BrokerRunBindingOptions
        {
            ProjectsOwnerAddress = "https://projects.test/",
            ProjectsAudience = Audience,
            ProjectsServerCertificateSha256 = new string('B', 64),
            ProjectsTrustBundleSha256 = new string('A', 64),
        },
        DataProtectionKeyPath = "unused",
        Clients =
        [
            new BrokerClientOptions
            {
                ClientId = "client",
                DisplayName = "Test client",
                Type = BrokerClientType.Public,
                RedirectUris = ["https://client.test/callback"],
                Scopes = ["api.read", "projects.orchestrator"],
                Resources = ["https://api.test/"],
            },
        ],
    };

    private static string CreateReceiptJson(string selectionHashState)
    {
        var receipt = new Dictionary<string, object?>
        {
            ["actorIssuer"] = Issuer,
            ["actorSubject"] = UserId.ToString("D"),
            ["tenantId"] = "tenant-1",
            ["membershipId"] = Guid.Parse("3e152378-43a3-4ac4-8db9-d2ce1361800b"),
            ["membershipRevision"] = 1,
            ["projectId"] = ProjectId,
            ["runId"] = RunId,
            ["assignmentId"] = Guid.Parse("b360b9ed-27b4-4f2a-bc0b-1ab842cbcf4a"),
            ["assignmentRevision"] = 1,
            ["projectRevision"] = 1,
            ["projectConfigurationRevision"] = 1,
            ["platformRuntimeRevision"] = 1,
            ["contextRevision"] = "context-v1",
        };
        if (selectionHashState != "missing")
            receipt["selectionHash"] = selectionHashState == "null" ? null : new string('A', 64);
        return JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private sealed class OwnerResponseHandler(
        string body,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string mediaType = "application/json",
        bool noStore = true) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? BearerToken { get; private set; }
        public HttpMethod? Method { get; private set; }
        public bool HasBody { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            BearerToken = request.Headers.Authorization?.Parameter;
            Method = request.Method;
            HasBody = request.Content is not null;
            var response = new HttpResponseMessage(statusCode)
            {
                RequestMessage = request,
                Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType),
            };
            if (noStore)
                response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                {
                    NoStore = true,
                };
            return Task.FromResult(response);
        }
    }

    private sealed class StalledOwnerResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                RequestMessage = request,
                Content = new StreamContent(new StalledReadStream()),
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/problem+json");
            response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoStore = true,
            };
            return Task.FromResult(response);
        }
    }

    private sealed class StalledReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
