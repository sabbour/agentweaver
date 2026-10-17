using System.Collections.Immutable;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Providers.Sandbox.AgentSandbox;

/// <summary>
/// Implements the bounded WebSocket channel exchange used to prepare and collect BuildTest workspace data.
/// </summary>
public sealed partial class KubernetesAgentSandboxClient
{
    /// <summary>The fixed container name used for workspace preparation.</summary>
    public const string WorkspacePreparationContainer = SandboxBuildTestLimits.WorkspacePreparationContainerName;
    /// <summary>The fixed container name used for output capture.</summary>
    public const string WorkspaceCaptureContainer = SandboxBuildTestLimits.WorkspaceCaptureContainerName;
    private const string WorkspaceIoProtocol = "v5.channel.k8s.io";
    private const int MaximumWorkspaceIoFrameBytes = 32 * 1024;

    /// <summary>
    /// Exchanges bounded input and output with the exact Kubernetes Pod over the exec or attach subresource.
    /// </summary>
    /// <param name="pod">Pod name and UID pinned by the caller.</param>
    /// <param name="requestFingerprint">Expected 64-character request identity sent before input.</param>
    /// <param name="input">Bytes supplied to the helper.</param>
    /// <param name="preparingWorkspace">Selects the preparation helper and failure-effect semantics.</param>
    /// <param name="maximumOutputBytes">Maximum accepted stdout length.</param>
    /// <param name="cancellationToken">Cancels the Kubernetes exchange.</param>
    /// <param name="executionCommand">Optional command that selects exec instead of attach.</param>
    /// <returns>The bounded stdout bytes returned by the helper.</returns>
    /// <exception cref="ArgumentException">The request exceeds protocol bounds or contains invalid arguments.</exception>
    /// <exception cref="SandboxProviderException">Pod identity, protocol, status, size, or transport validation fails.</exception>
    public async Task<ImmutableArray<byte>> ExchangeBuildTestWorkspaceAsync(
        SandboxBuildTestPodReference pod,
        string requestFingerprint,
        ImmutableArray<byte> input,
        bool preparingWorkspace,
        int maximumOutputBytes,
        CancellationToken cancellationToken,
        ImmutableArray<string>? executionCommand = null)
    {
        ArgumentNullException.ThrowIfNull(pod);
        _ = pod.Validate();
        ValidatePathSegment(pod.KubernetesNamespace, nameof(pod.KubernetesNamespace));
        ValidatePathSegment(pod.Name, nameof(pod.Name));
        if (requestFingerprint is not { Length: 64 } || !requestFingerprint.All(Uri.IsHexDigit) ||
            input.IsDefault || input.Length > ProducedRunCaptureLimits.MaximumPackageBytes ||
            maximumOutputBytes is < 1 or > ProducedRunCaptureLimits.MaximumDocumentBytes ||
            executionCommand is { } command && (command.IsDefaultOrEmpty ||
                command.Length > SandboxBuildTestLimits.MaximumArguments ||
                command.Any(argument => argument is null ||
                    argument.Length > SandboxBuildTestLimits.MaximumCollectorRequestBytes * 2 ||
                    argument.Any(char.IsControl))))
            throw new ArgumentException("The bounded BuildTest workspace exchange is invalid.");

        RequirePodUid(await GetAsync(
            string.Empty, "pods", pod.KubernetesNamespace, pod.Name, cancellationToken).ConfigureAwait(false),
            pod.Uid);
        var container = preparingWorkspace ? WorkspacePreparationContainer : WorkspaceCaptureContainer;
        var path = NamespacedResourcePath(string.Empty, "pods", pod.KubernetesNamespace, pod.Name) +
            (executionCommand is null ? "/attach" : "/exec") +
            "?container=" + container + "&stdin=true&stdout=true&stderr=true&tty=false";
        if (executionCommand is { } arguments)
            path += string.Concat(arguments.Select(argument => "&command=" + Uri.EscapeDataString(argument)));
        var address = new UriBuilder(new Uri(_httpClient.BaseAddress!, path))
        {
            Scheme = "wss"
        }.Uri;
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(WorkspaceIoProtocol);
        socket.Options.HttpVersion = System.Net.HttpVersion.Version11;
        socket.Options.HttpVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionExact;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_httpClient.Timeout);
        try
        {
            // Reuse the HTTP client's service-account handler and trusted cluster CA.
            await socket.ConnectAsync(address, _httpClient, deadline.Token).ConfigureAwait(false);
            if (socket.SubProtocol != WorkspaceIoProtocol)
                throw WorkspaceIoFailure("kubernetes_workspace_protocol_invalid", preparingWorkspace);
            var output = await ExchangeWorkspaceChannelsAsync(
                socket, pod.Uid, requestFingerprint, input, maximumOutputBytes,
                preparingWorkspace, deadline.Token).ConfigureAwait(false);
            RequirePodUid(await GetAsync(
                string.Empty, "pods", pod.KubernetesNamespace, pod.Name, deadline.Token).ConfigureAwait(false),
                pod.Uid);
            return output;
        }
        catch (WebSocketException)
        {
            throw WorkspaceIoFailure("kubernetes_workspace_transport_error", preparingWorkspace);
        }
        catch (SandboxProviderException exception) when (
            preparingWorkspace && !exception.EffectMayHaveApplied)
        {
            throw WorkspaceIoFailure(exception.Code, preparingWorkspace: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw WorkspaceIoFailure("kubernetes_workspace_io_timeout", preparingWorkspace);
        }
    }

    /// <summary>
    /// Enforces the channel order, request identity, output limit, successful helper status, and clean close.
    /// </summary>
    private static async Task<ImmutableArray<byte>> ExchangeWorkspaceChannelsAsync(
        WebSocket socket,
        string podUid,
        string requestFingerprint,
        ImmutableArray<byte> input,
        int maximumOutputBytes,
        bool preparingWorkspace,
        CancellationToken cancellationToken)
    {
        var ready = Encoding.UTF8.GetBytes($"AWPODIO2 {podUid} {requestFingerprint}\n");
        var readyOffset = 0;
        using var output = new MemoryStream();
        using var status = new MemoryStream();
        var buffer = new byte[MaximumWorkspaceIoFrameBytes];
        byte? channel = null;
        var statusSeen = false;
        var inputSent = false;
        var stdoutClosed = false;

        while (true)
        {
            var received = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                if (socket.CloseStatus != WebSocketCloseStatus.NormalClosure ||
                    channel is not null || !inputSent || readyOffset != ready.Length || !statusSeen)
                    throw WorkspaceIoFailure("kubernetes_workspace_io_incomplete", preparingWorkspace);
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure, string.Empty, cancellationToken).ConfigureAwait(false);
                return System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(output.ToArray());
            }
            if (received.MessageType != WebSocketMessageType.Binary || received.Count == 0)
                throw WorkspaceIoFailure("kubernetes_workspace_channel_invalid", preparingWorkspace);
            var offset = 0;
            if (channel is null)
            {
                channel = buffer[0];
                offset = 1;
                if (channel is not (1 or 2 or 3 or 255) ||
                    channel == 1 && stdoutClosed || channel == 3 && statusSeen)
                    throw WorkspaceIoFailure("kubernetes_workspace_channel_invalid", preparingWorkspace);
            }
            var count = received.Count - offset;
            switch (channel)
            {
                case 1:
                    if (!inputSent)
                    {
                        var needed = ready.Length - readyOffset;
                        if (count > needed ||
                            !buffer.AsSpan(offset, count).SequenceEqual(ready.AsSpan(readyOffset, count)))
                            throw WorkspaceIoFailure("kubernetes_workspace_identity_invalid", preparingWorkspace);
                        readyOffset += count;
                        if (readyOffset == ready.Length)
                        {
                            if (!received.EndOfMessage)
                                throw WorkspaceIoFailure("kubernetes_workspace_identity_invalid", preparingWorkspace);
                            await SendWorkspaceInputAsync(socket, input, cancellationToken).ConfigureAwait(false);
                            inputSent = true;
                        }
                    }
                    else
                    {
                        if (output.Length + count > maximumOutputBytes)
                            throw WorkspaceIoFailure("kubernetes_workspace_output_too_large", preparingWorkspace);
                        output.Write(buffer, offset, count);
                    }
                    break;
                case 2:
                    if (count != 0)
                        throw WorkspaceIoFailure("kubernetes_workspace_helper_failed", preparingWorkspace);
                    break;
                case 3:
                    if (status.Length + count > 4096)
                        throw WorkspaceIoFailure("kubernetes_workspace_status_invalid", preparingWorkspace);
                    status.Write(buffer, offset, count);
                    if (received.EndOfMessage)
                    {
                        RequireSuccessfulWorkspaceStatus(status.ToArray(), preparingWorkspace);
                        statusSeen = true;
                    }
                    break;
                case 255:
                    if (!received.EndOfMessage || count != 1 || buffer[offset] is not (1 or 2))
                        throw WorkspaceIoFailure("kubernetes_workspace_channel_invalid", preparingWorkspace);
                    stdoutClosed |= buffer[offset] == 1;
                    break;
            }
            if (received.EndOfMessage)
                channel = null;
        }
    }

    /// <summary>Sends the bounded input on stdin and then signals end-of-input on the protocol channel.</summary>
    private static async Task SendWorkspaceInputAsync(
        WebSocket socket,
        ImmutableArray<byte> input,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[MaximumWorkspaceIoFrameBytes];
        buffer[0] = 0;
        for (var offset = 0; offset < input.Length;)
        {
            var count = Math.Min(buffer.Length - 1, input.Length - offset);
            input.AsSpan(offset, count).CopyTo(buffer.AsSpan(1));
            await socket.SendAsync(
                buffer.AsMemory(0, count + 1), WebSocketMessageType.Binary,
                endOfMessage: true, cancellationToken).ConfigureAwait(false);
            offset += count;
        }
        await socket.SendAsync(
            new byte[] { 255, 0 }.AsMemory(), WebSocketMessageType.Binary,
            endOfMessage: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Parses the bounded status channel and rejects a helper result other than Success.</summary>
    private static void RequireSuccessfulWorkspaceStatus(byte[] bytes, bool preparingWorkspace)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("status", out var status) ||
                status.ValueKind != JsonValueKind.String || status.GetString() != "Success")
                throw WorkspaceIoFailure("kubernetes_workspace_helper_failed", preparingWorkspace);
        }
        catch (JsonException)
        {
            throw WorkspaceIoFailure("kubernetes_workspace_status_invalid", preparingWorkspace);
        }
    }

    /// <summary>Builds the protocol failure while preserving whether workspace preparation may have changed state.</summary>
    private static SandboxProviderException WorkspaceIoFailure(string code, bool preparingWorkspace) =>
        new(code, "The bounded native BuildTest workspace exchange did not complete.", preparingWorkspace);
}
