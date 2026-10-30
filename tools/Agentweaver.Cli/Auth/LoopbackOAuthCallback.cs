using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Agentweaver.Cli.Auth;

internal sealed record OAuthCallback(string? Code, string? Error);

internal interface IOAuthCallbackListenerFactory
{
    IOAuthCallbackListener Start();
}

internal interface IOAuthCallbackListener : IAsyncDisposable
{
    Uri RedirectUri { get; }
    Task<OAuthCallback> WaitAsync(string expectedState, string expectedIssuer, CancellationToken ct);
}

internal sealed class OAuthCallbackListenerFactory : IOAuthCallbackListenerFactory
{
    public IOAuthCallbackListener Start() => new LoopbackOAuthCallbackListener();
}

internal sealed class LoopbackOAuthCallbackListener : IOAuthCallbackListener
{
    public const int Port = 49173;
    public const string Host = "127.0.0.1";
    public const string Path = "/oauth/callback";
    private const int MaximumHeaderBytes = 8192;
    private readonly TcpListener _listener;
    private bool _disposed;

    public LoopbackOAuthCallbackListener()
    {
        _listener = new TcpListener(IPAddress.Loopback, Port);
        try
        {
            _listener.Start(1);
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            throw new InvalidOperationException(
                $"OAuth callback port {Port} is already in use. Close the conflicting process and try again.",
                exception);
        }
        catch (SocketException exception)
        {
            throw new InvalidOperationException("Could not start the IPv4 localhost OAuth callback listener.", exception);
        }
    }

    public Uri RedirectUri { get; } = new($"http://{Host}:{Port}{Path}", UriKind.Absolute);

    public async Task<OAuthCallback> WaitAsync(
        string expectedState,
        string expectedIssuer,
        CancellationToken ct)
    {
        using var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
        // A loopback listener is not proof of a local caller; reject every peer except the exact IPv4 loopback address.
        if (client.Client.RemoteEndPoint is not IPEndPoint remote ||
            remote.AddressFamily != AddressFamily.InterNetwork ||
            !IPAddress.Loopback.Equals(remote.Address))
        {
            await WriteResponseAsync(client.GetStream(), accepted: false, ct).ConfigureAwait(false);
            throw new InvalidOperationException("The OAuth callback did not originate from IPv4 localhost.");
        }

        try
        {
            var (requestTarget, host) = await ReadRequestAsync(client.GetStream(), ct).ConfigureAwait(false);
            var callback = OAuthCallbackParser.Parse(requestTarget, host, expectedState, expectedIssuer);
            await WriteResponseAsync(client.GetStream(), accepted: true, ct).ConfigureAwait(false);
            return callback;
        }
        catch
        {
            try
            {
                await WriteResponseAsync(client.GetStream(), accepted: false, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
            }
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _listener.Stop();
        }
        return ValueTask.CompletedTask;
    }

    private static async Task<(string RequestTarget, string Host)> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken ct)
    {
        var buffer = new byte[MaximumHeaderBytes + 1];
        var total = 0;
        var headerEnd = -1;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)
                .ConfigureAwait(false);
            if (read == 0)
                throw new InvalidOperationException("The OAuth callback request was incomplete.");
            total += read;
            headerEnd = FindHeaderEnd(buffer.AsSpan(0, total));
            if (headerEnd >= 0) break;
        }
        if (headerEnd < 0 || headerEnd > MaximumHeaderBytes || total != headerEnd + 4)
            throw new InvalidOperationException("The OAuth callback request exceeded the size limit.");

        return ParseRequestHeader(buffer.AsSpan(0, headerEnd));
    }

    internal static (string RequestTarget, string Host) ParseRequestHeader(ReadOnlySpan<byte> headerBytes)
    {
        if (headerBytes.Length > MaximumHeaderBytes)
            throw new InvalidOperationException("The OAuth callback request exceeded the size limit.");
        foreach (var value in headerBytes)
        {
            if (value > 0x7f)
                throw new InvalidOperationException("The OAuth callback request is invalid.");
        }

        var header = Encoding.ASCII.GetString(headerBytes);
        var lines = header.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', StringSplitOptions.None);
        if (requestLine.Length != 3 || requestLine[0] != "GET" || requestLine[2] != "HTTP/1.1")
            throw new InvalidOperationException("The OAuth callback method is invalid.");

        string? host = null;
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0 || char.IsWhiteSpace(line[0]))
                throw new InvalidOperationException("The OAuth callback headers are invalid.");
            var name = line[..separator];
            var value = line[(separator + 1)..].Trim();
            if (name.Equals("host", StringComparison.OrdinalIgnoreCase))
            {
                if (host is not null)
                    throw new InvalidOperationException("The OAuth callback contains duplicate Host headers.");
                host = value;
            }
            if (name.Equals("content-length", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The OAuth callback must not contain a request body.");
        }

        return (requestLine[1], host ?? string.Empty);
    }

    private static int FindHeaderEnd(ReadOnlySpan<byte> buffer)
    {
        for (var index = 0; index <= buffer.Length - 4; index++)
        {
            if (buffer[index] == '\r' && buffer[index + 1] == '\n' &&
                buffer[index + 2] == '\r' && buffer[index + 3] == '\n')
                return index;
        }
        return -1;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, bool accepted, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(accepted
            ? "<!doctype html><meta charset=\"utf-8\"><title>Agentweaver sign-in</title><p>Sign-in complete. You can close this window.</p>"
            : "<!doctype html><meta charset=\"utf-8\"><title>Agentweaver sign-in</title><p>Sign-in response was rejected. Return to the CLI and try again.</p>");
        var status = accepted ? "200 OK" : "400 Bad Request";
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Content-Security-Policy: default-src 'none'; frame-ancestors 'none'\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}

internal static class OAuthCallbackParser
{
    private const int MaximumRequestLength = 8192;
    private const int MaximumErrorDescriptionLength = 2048;
    private static readonly HashSet<string> AllowedParameters =
        new(["code", "state", "iss", "error", "error_description"], StringComparer.Ordinal);

    public static OAuthCallback Parse(
        string requestTarget,
        string host,
        string expectedState,
        string expectedIssuer)
    {
        if (string.IsNullOrEmpty(requestTarget) || requestTarget.Length > MaximumRequestLength)
            throw new InvalidOperationException("The OAuth callback request exceeded the size limit.");
        if (!string.Equals(host, $"{LoopbackOAuthCallbackListener.Host}:{LoopbackOAuthCallbackListener.Port}",
                StringComparison.Ordinal))
            throw new InvalidOperationException("The OAuth callback Host is invalid.");
        if (requestTarget.Contains('#'))
            throw new InvalidOperationException("The OAuth callback request is invalid.");

        var queryStart = requestTarget.IndexOf('?');
        var path = queryStart < 0 ? requestTarget : requestTarget[..queryStart];
        if (!string.Equals(path, LoopbackOAuthCallbackListener.Path, StringComparison.Ordinal))
            throw new InvalidOperationException("The OAuth callback path is invalid.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (queryStart >= 0)
        {
            var query = requestTarget[(queryStart + 1)..];
            if (query.Length == 0)
                throw new InvalidOperationException("The OAuth callback parameters are invalid.");
            foreach (var pair in query.Split('&', StringSplitOptions.None))
            {
                var separator = pair.IndexOf('=');
                if (separator <= 0)
                    throw new InvalidOperationException("The OAuth callback parameters are invalid.");
                var key = Decode(pair[..separator]);
                var value = Decode(pair[(separator + 1)..]);
                if (!AllowedParameters.Contains(key) || !values.TryAdd(key, value))
                    throw new InvalidOperationException("The OAuth callback contains unexpected or duplicate parameters.");
            }
        }

        // State is mandatory and compared in constant time; when the Broker supplies iss, it must match discovery.
        if (!values.TryGetValue("state", out var state) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expectedState)))
            throw new InvalidOperationException("The OAuth callback state is invalid.");
        if (values.TryGetValue("iss", out var issuer) &&
            !string.Equals(issuer, expectedIssuer, StringComparison.Ordinal))
            throw new InvalidOperationException("The OAuth callback issuer is invalid.");

        if (values.TryGetValue("error", out var error))
        {
            if (values.ContainsKey("code") ||
                values.TryGetValue("error_description", out var description) &&
                description.Length > MaximumErrorDescriptionLength)
                throw new InvalidOperationException("The OAuth callback error response is invalid.");
            return new OAuthCallback(null, error);
        }

        if (values.ContainsKey("error_description") ||
            !values.TryGetValue("code", out var code) ||
            code.Length is 0 or > 4096 ||
            code.Any(char.IsControl))
            throw new InvalidOperationException("The OAuth callback did not contain one valid authorization code.");
        return new OAuthCallback(code, null);
    }

    private static string Decode(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%') continue;
            if (index + 2 >= value.Length ||
                !Uri.IsHexDigit(value[index + 1]) ||
                !Uri.IsHexDigit(value[index + 2]))
                throw new InvalidOperationException("The OAuth callback parameters are invalid.");
            index += 2;
        }
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException exception)
        {
            throw new InvalidOperationException("The OAuth callback parameters are invalid.", exception);
        }
    }
}
