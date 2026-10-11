using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Agentweaver.Environment.Tests;

public sealed class KubernetesBuildTestWorkspaceIoTests
{
    private const string PodUid = "workspace-pod-uid";
    private static readonly string Fingerprint = new('a', 64);
    private static readonly SandboxBuildTestPodReference Pod =
        new("agentweaver", "workspace-pod", PodUid, "workspace-resource-version");

    [Fact]
    public async Task NativeTlsUsesThePinnedCertificateAndBearer()
    {
        await using var server = await NativeServer.StartAsync("ok");
        using var response = await server.Client.GetAsync("api/v1/namespaces/agentweaver/pods/workspace-pod");
        response.EnsureSuccessStatusCode();
        Assert.Equal(1, server.AuthorizedRequests);
    }

    [Fact]
    public async Task NativeExchangeTransfersTheExactMaximumSeedAndCompleteBinaryResult()
    {
        await using var server = await NativeServer.StartAsync("max-exchange");
        var bytes = new byte[ProducedRunCaptureLimits.MaximumPackageBytes];
        bytes[0] = 255;
        bytes[^1] = 128;
        var input = System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(bytes);
        var client = new KubernetesAgentSandboxClient(server.Client);

        var output = await client.ExchangeBuildTestWorkspaceAsync(
            Pod, Fingerprint, input, true, ProducedRunCaptureLimits.MaximumPackageBytes, CancellationToken.None);

        Assert.Equal(ProducedRunCaptureLimits.MaximumPackageBytes, output.Length);
        Assert.Equal(input.Length, server.Input.Length);
        Assert.Equal(SHA256.HashData(input.AsSpan()), SHA256.HashData(output.AsSpan()));
        Assert.True(server.InputClosed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeExecKeepsTheExactHelperCommandAndBinaryInput(bool preparing)
    {
        await using var server = await NativeServer.StartAsync("exec");
        var input = preparing ? ImmutableArray.Create<byte>(0, 255, 128) : ImmutableArray<byte>.Empty;
        var command = ImmutableArray.Create(
            "dotnet", "/app/Agentweaver.AgentHost.dll", SandboxBuildTestLimits.WorkspaceCollectorMode,
            "encoded-request", preparing ? "prepare" : "export");
        var client = new KubernetesAgentSandboxClient(server.Client);

        var output = await client.ExchangeBuildTestWorkspaceAsync(
            Pod, Fingerprint, input, preparing, 100, CancellationToken.None, command);

        Assert.Equal(command.ToArray(), server.Command);
        Assert.Equal(input.ToArray(), server.Input.ToArray());
        Assert.Equal(new byte[] { 0, 255, 128, 10 }, output.ToArray());
        Assert.True(server.InputClosed);
        Assert.Equal(2, server.PodReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeWebSocketUsesPinnedHttpHandlerAndTransfersBinarySeedOnlyAfterBoundReady(bool preparing)
    {
        await using var server = await NativeServer.StartAsync("ok");
        var input = Enumerable.Range(0, 100_000).Select(index => (byte)index).ToImmutableArray();
        var client = new KubernetesAgentSandboxClient(server.Client);

        var output = await client.ExchangeBuildTestWorkspaceAsync(
            Pod, Fingerprint, input, preparing, 100, CancellationToken.None);

        Assert.Equal(new byte[] { 0, 255, 128, 10 }, output.ToArray());
        Assert.Equal(input.ToArray(), server.Input.ToArray());
        Assert.Equal(2, server.PodReads);
        Assert.Equal(
            preparing ? KubernetesAgentSandboxClient.WorkspacePreparationContainer :
                KubernetesAgentSandboxClient.WorkspaceCaptureContainer,
            server.Container);
        Assert.Equal(3, server.AuthorizedRequests);
        Assert.True(server.InputClosed);
    }

    [Theory]
    [InlineData("uid", "kubernetes_pod_uid_mismatch")]
    [InlineData("ready", "kubernetes_workspace_identity_invalid")]
    [InlineData("fingerprint", "kubernetes_workspace_identity_invalid")]
    [InlineData("protocol", "kubernetes_workspace_protocol_invalid")]
    [InlineData("overflow", "kubernetes_workspace_output_too_large")]
    [InlineData("stderr", "kubernetes_workspace_helper_failed")]
    [InlineData("status", "kubernetes_workspace_helper_failed")]
    [InlineData("invalid-status", "kubernetes_workspace_status_invalid")]
    [InlineData("incomplete", "kubernetes_workspace_io_incomplete")]
    [InlineData("changed-uid", "kubernetes_pod_uid_mismatch")]
    public async Task NativeExchangeRejectsIdentityProtocolAndIncompleteEvidence(string fault, string code)
    {
        await using var server = await NativeServer.StartAsync(fault);
        var client = new KubernetesAgentSandboxClient(server.Client);

        var exception = await Assert.ThrowsAsync<SandboxProviderException>(() =>
            client.ExchangeBuildTestWorkspaceAsync(
                Pod, Fingerprint, ImmutableArray.Create<byte>(0, 255), true, 4, CancellationToken.None));

        Assert.Equal(code, exception.Code);
        if (fault is "uid" or "ready" or "fingerprint" or "protocol" or "incomplete")
            Assert.Empty(server.Input.ToArray());
    }

    private sealed class NativeServer(
        WebApplication app,
        X509Certificate2 certificate,
        HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public MemoryStream Input { get; private set; } = new();
        public int PodReads { get; private set; }
        public int AuthorizedRequests { get; private set; }
        public string? Container { get; private set; }
        public string[]? Command { get; private set; }
        public bool InputClosed { get; private set; }

        public static async Task<NativeServer> StartAsync(string fault)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var signed = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var certificate = X509CertificateLoader.LoadPkcs12(
                signed.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
                options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
            var app = builder.Build();
            app.UseWebSockets();
            NativeServer? server = null;
            app.Run(async context =>
            {
                Assert.Equal("Bearer native-workspace-test", context.Request.Headers.Authorization.ToString());
                server!.AuthorizedRequests++;
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    server.PodReads++;
                    var uid = fault == "uid" || fault == "changed-uid" && server.PodReads > 1
                        ? "replaced-pod" : PodUid;
                    await context.Response.WriteAsJsonAsync(new { metadata = new { uid } });
                    return;
                }
                Assert.EndsWith(fault == "exec" ? "/exec" : "/attach", context.Request.Path.Value);
                if (fault == "exec")
                    server.Command = context.Request.Query["command"].Select(value => value!).ToArray();
                Assert.Equal("true", context.Request.Query["stdin"].ToString());
                Assert.Equal("true", context.Request.Query["stdout"].ToString());
                Assert.Equal("true", context.Request.Query["stderr"].ToString());
                Assert.Equal("false", context.Request.Query["tty"].ToString());
                server.Container = context.Request.Query["container"].ToString();
                using var socket = await context.WebSockets.AcceptWebSocketAsync(
                    fault == "protocol" ? null : "v5.channel.k8s.io");
                if (fault == "protocol")
                    return;
                if (fault == "incomplete")
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", context.RequestAborted);
                    return;
                }

                var uidAtReady = fault == "ready" ? "replaced-pod" : PodUid;
                var hashAtReady = fault == "fingerprint" ? new string('b', 64) : Fingerprint;
                var ready = Encoding.UTF8.GetBytes($"AWPODIO2 {uidAtReady} {hashAtReady}\n");
                // Fragment the channel header and readiness bytes independently.
                await socket.SendAsync(
                    new byte[] { 1 }.AsMemory(), WebSocketMessageType.Binary, false, context.RequestAborted);
                await socket.SendAsync(ready.AsMemory(), WebSocketMessageType.Binary, true, context.RequestAborted);
                if (fault is "ready" or "fingerprint")
                    return;

                var buffer = new byte[32 * 1024];
                while (true)
                {
                    var received = await socket.ReceiveAsync(buffer.AsMemory(), context.RequestAborted);
                    Assert.Equal(WebSocketMessageType.Binary, received.MessageType);
                    Assert.True(received.EndOfMessage);
                    if (buffer[0] == 255)
                    {
                        Assert.Equal(2, received.Count);
                        Assert.Equal(0, buffer[1]);
                        server.InputClosed = true;
                        break;
                    }
                    Assert.Equal(0, buffer[0]);
                    server.Input.Write(buffer, 1, received.Count - 1);
                }
                if (fault == "stderr")
                    await SendAsync(socket, 2, "private helper details"u8.ToArray(), context.RequestAborted);
                else if (fault is "status" or "invalid-status")
                    await SendAsync(socket, 3,
                        Encoding.UTF8.GetBytes(fault == "status" ? """{"status":"Failure"}""" : "{"),
                        context.RequestAborted);
                else
                {
                    if (fault == "max-exchange")
                    {
                        var bytes = server.Input.GetBuffer();
                        for (var offset = 0; offset < server.Input.Length;)
                        {
                            var count = (int)Math.Min(8192, server.Input.Length - offset);
                            await SendAsync(socket, 1, bytes.AsSpan(offset, count).ToArray(), context.RequestAborted);
                            offset += count;
                        }
                    }
                    else
                        await SendAsync(socket, 1,
                            fault == "overflow" ? new byte[5] : [0, 255, 128, 10], context.RequestAborted);
                    await SendAsync(socket, 3, """{"status":"Success"}"""u8.ToArray(), context.RequestAborted);
                }
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", context.RequestAborted);
            });
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, presented, _, _) =>
                    presented is not null && presented.RawData.AsSpan().SequenceEqual(certificate.RawData)
            };
            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(address + "/"),
                Timeout = TimeSpan.FromSeconds(10)
            };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", "native-workspace-test");
            server = new(app, certificate, client);
            if (fault == "max-exchange")
            {
                server.Input.Dispose();
                server.Input = new MemoryStream(ProducedRunCaptureLimits.MaximumPackageBytes);
            }
            return server;
        }

        private static async Task SendAsync(
            WebSocket socket, byte channel, byte[] bytes, CancellationToken cancellationToken)
        {
            var message = new byte[bytes.Length + 1];
            message[0] = channel;
            bytes.CopyTo(message, 1);
            await socket.SendAsync(
                message.AsMemory(), WebSocketMessageType.Binary, true, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            certificate.Dispose();
            Input.Dispose();
        }
    }
}
