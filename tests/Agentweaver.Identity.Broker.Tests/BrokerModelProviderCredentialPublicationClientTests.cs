using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity.Broker;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class BrokerModelProviderCredentialPublicationClientTests
{
    [Fact]
    public async Task PublicationUsesPrivateTypedTokenAndSendsOnlyTheVersionReference()
    {
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest(
            "CN=broker.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var publication = Request();
        var receipt = new ModelProviderCredentialPublicationReceipt(
            publication.OperationId,
            publication.Scope,
            publication.ProjectId,
            publication.ProviderId,
            publication.ExpectedProviderRevision + 1,
            publication.CredentialVersion,
            new string('a', 64),
            publication.CredentialIdentity,
            publication.CredentialReference);
        var handler = new PublicationResponseHandler(JsonSerializer.Serialize(receipt));
        using var client = new HttpClient(handler);
        var publisher = new BrokerModelProviderCredentialPublicationClient(
            BrokerRunBindingAuthorityTests.CreateOptions(),
            certificate,
            client,
            TimeProvider.System);

        var result = await publisher.PublishAsync(publication, "actor-1", CancellationToken.None);

        Assert.Equal(receipt, result);
        Assert.Equal(
            new Uri("https://projects.test/internal/byok/credential-publications"),
            handler.RequestUri);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.True(handler.NoStore);
        Assert.Contains(publication.CredentialVersion, handler.Body);
        Assert.DoesNotContain("api_key", handler.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vault", handler.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secretName", handler.Body, StringComparison.OrdinalIgnoreCase);

        var token = new JwtSecurityTokenHandler().ReadJwtToken(handler.BearerToken);
        Assert.Equal(BrokerModelProviderCredentialPublicationClient.JwtType, token.Header.Typ);
        Assert.Equal("https://projects-private.test/", Assert.Single(token.Audiences));
        Assert.Equal(BrokerModelProviderCredentialPublicationClient.Purpose, token.Payload["purpose"]);
        Assert.Equal("identity-broker", token.Payload["sub"]);
        Assert.Equal("actor-1", token.Payload["actor_id"]);
        Assert.Equal(publication.OperationId.ToString("D"), token.Payload["operation_id"]);
        Assert.Equal(publication.CredentialVersion, token.Payload["credential_version"]);
        Assert.Equal(publication.CredentialIdentity.CredentialSlotId.ToString("D"),
            token.Payload["credential_slot_id"]);
        Assert.Equal(publication.CredentialReference.Id, token.Payload["credential_secret_id"]);
    }

    [Fact]
    public async Task PublicationRejectsOversizedReceiptWithoutReturningItsBody()
    {
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest(
            "CN=broker.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var handler = new PublicationResponseHandler(new string('x', 16 * 1024 + 1));
        using var client = new HttpClient(handler);
        var publisher = new BrokerModelProviderCredentialPublicationClient(
            BrokerRunBindingAuthorityTests.CreateOptions(),
            certificate,
            client,
            TimeProvider.System);

        var exception = await Assert.ThrowsAsync<BrokerCredentialPublicationException>(() =>
            publisher.PublishAsync(Request(), "actor-1", CancellationToken.None));

        Assert.Null(exception.StatusCode);
        Assert.DoesNotContain(new string('x', 64), exception.Message);
    }

    [Fact]
    public async Task PublicationTimesOutWhenReceiptBodyStallsAfterHeaders()
    {
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest(
            "CN=broker.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var stream = new StalledReadStream();
        using var client = new HttpClient(new StalledPublicationResponseHandler(stream))
        {
            Timeout = TimeSpan.FromMilliseconds(100),
        };
        var publisher = new BrokerModelProviderCredentialPublicationClient(
            BrokerRunBindingAuthorityTests.CreateOptions(),
            certificate,
            client,
            TimeProvider.System);

        var publication = publisher.PublishAsync(Request(), "actor-1", CancellationToken.None);
        var completed = await Task.WhenAny(publication, Task.Delay(TimeSpan.FromSeconds(2)));
        stream.Release();

        var exception = await Assert.ThrowsAsync<BrokerCredentialPublicationException>(() => publication);

        Assert.Same(publication, completed);
        Assert.Null(exception.StatusCode);
    }

    private static ModelProviderCredentialPublicationRequest Request() =>
        new(
            Guid.Parse("ad451e41-9b48-412f-99dc-ad6a6cd4080c"),
            7,
            ModelProviderBindingScope.Project,
            "project-1",
            "provider-1",
            "0123456789abcdef0123456789abcdef",
            new(Guid.Parse("0ef1ea5e-d94b-41d0-9380-900b1c532bf2"),
                ModelProviderBindingScope.Project, "project-1", "provider-1"),
            new("credential-secret", "0123456789abcdef0123456789abcdef"));

    private sealed class PublicationResponseHandler(string body) : HttpMessageHandler
    {
        internal Uri? RequestUri { get; private set; }
        internal string? BearerToken { get; private set; }
        internal HttpMethod? Method { get; private set; }
        internal string Body { get; private set; } = string.Empty;
        internal bool NoStore { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            BearerToken = request.Headers.Authorization?.Parameter;
            Method = request.Method;
            NoStore = request.Headers.CacheControl?.NoStore == true;
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoStore = true,
            };
            return response;
        }
    }

    private sealed class StalledPublicationResponseHandler(Stream stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StreamContent(stream),
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoStore = true,
            };
            return Task.FromResult(response);
        }
    }

    private sealed class StalledReadStream : Stream
    {
        private readonly TaskCompletionSource<bool> released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal void Release() => released.TrySetResult(true);

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var cancelled = Task.Delay(Timeout.Infinite, cancellationToken);
            var read = await Task.WhenAny(released.Task, cancelled);
            await read;
            return 0;
        }
    }
}
