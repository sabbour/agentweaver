using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Cli.Api;
using Agentweaver.Cli.Auth;

namespace Agentweaver.Cli;

internal static class CopilotConnectionCommands
{
    private const string CallbackCookieName = "__Host-agentweaver-copilot-link";
    private const string BrowserCallbackPath = "/auth/github/copilot-app/callback";
    private const int MaximumResponseBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 16,
    };
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 16,
    };

    internal static async Task<int> RunAsync(
        string[] args,
        CliRuntimeOptions runtime,
        HttpClient httpClient,
        TextWriter output,
        CancellationToken ct,
        IOAuthCallbackListenerFactory? callbackListeners = null,
        ICliSystemBrowser? browser = null)
    {
        if (args.Length == 0)
            throw new CliUsageException("Choose `status`, `connect`, `refresh`, or `disconnect`.");
        var command = args[0];
        if (command is not ("status" or "connect" or "refresh" or "disconnect"))
            throw new CliUsageException($"Unknown Copilot command: {command}");

        var parsed = CliOptions.Parse(args[1..], new HashSet<string>(StringComparer.Ordinal));
        var commandOptions = command switch
        {
            "status" => new[] { "scope", "scope-id", "after-connection-id" },
            "connect" => new[] { "scope", "scope-id" },
            _ => new[] { "connection-id" },
        };
        parsed.RequireOnly(commandOptions.Concat(CliConfiguration.TargetOptionNames).ToArray());
        if (parsed.Single("project-id") is not null || parsed.Single("run-id") is not null)
            throw new CliUsageException("Copilot commands use an unbound identity. Do not set --project-id or --run-id.");

        (string Scope, string ScopeId)? scope = command is "status" or "connect" ? ReadScope(parsed) : null;
        var after = command == "status" ? ReadCursor(parsed.Single("after-connection-id")) : null;
        var connectionId = command is "refresh" or "disconnect"
            ? ReadConnectionId(parsed.Single("connection-id"))
            : Guid.Empty;
        var identity = CliConfiguration.ReadIdentity(
            ["projects.admin"], options: parsed, unbound: true);
        var authentication = runtime.Authentication
            ?? throw new InvalidOperationException("Sign in with `agentweaver auth login --scope projects.admin` first.");
        var gateway = (runtime.GatewayClientFactory ??
            new GatewayClientFactory(httpClient, authentication)).Create(identity);
        await gateway.ValidateAuthorizationContextAsync(ct).ConfigureAwait(false);
        var bearer = await authentication.GetBearerAsync(identity, InteractionPolicy.Never, ct).ConfigureAwait(false);
        var transport = new CopilotConnectionTransport(httpClient, identity, bearer);

        switch (command)
        {
            case "status":
                await WriteJsonAsync(output, await transport.ListAsync(
                    scope!.Value.Scope, scope.Value.ScopeId, after, ct).ConfigureAwait(false)).ConfigureAwait(false);
                break;
            case "connect":
            {
                var connection = await ConnectAsync(
                    transport, identity, scope!.Value, ct,
                    callbackListeners ?? new OAuthCallbackListenerFactory(),
                    browser ?? new SystemBrowser()).ConfigureAwait(false);
                await WriteJsonAsync(output, connection).ConfigureAwait(false);
                break;
            }
            case "refresh":
            {
                var current = await transport.ReadStatusAsync(connectionId, ct).ConfigureAwait(false);
                var refreshed = await transport.ChangeAsync(
                    "/refresh", new CopilotConnectionMutation(current.ConnectionId, current.Revision), ct)
                    .ConfigureAwait(false);
                await WriteJsonAsync(output, refreshed).ConfigureAwait(false);
                break;
            }
            case "disconnect":
            {
                var current = await transport.ReadStatusAsync(connectionId, ct).ConfigureAwait(false);
                var revoked = await transport.ChangeAsync(
                    "/revoke", new CopilotConnectionMutation(current.ConnectionId, current.Revision), ct)
                    .ConfigureAwait(false);
                await WriteJsonAsync(output, revoked).ConfigureAwait(false);
                break;
            }
        }
        return 0;
    }

    private static async Task<CopilotConnectionReceipt> ConnectAsync(
        CopilotConnectionTransport transport,
        CliIdentity identity,
        (string Scope, string ScopeId) scope,
        CancellationToken ct,
        IOAuthCallbackListenerFactory callbackListeners,
        ICliSystemBrowser browser)
    {
        await using var listener = callbackListeners.Start();
        var (started, callbackCookie) = await transport.BeginAsync(
            scope.Scope, scope.ScopeId, listener.RedirectUri, ct).ConfigureAwait(false);
        if (started.Connection.Scope != scope.Scope ||
            started.Connection.ScopeId != scope.ScopeId ||
            started.Connection.State != "pending")
            throw new InvalidDataException("The Gateway returned an unexpected Copilot connection.");

        var state = ReadAuthorizationState(started.AuthorizationUri);
        await browser.OpenAsync(started.AuthorizationUri, ct).ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        OAuthCallback callback;
        try
        {
            callback = await listener.WaitAsync(state, identity.Target.Issuer.AbsoluteUri, wait.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("GitHub sign-in timed out. Start the connection again.");
        }
        if (callback.Error is not null && callback.Error != "access_denied")
            throw new InvalidDataException("The GitHub callback returned an unsupported error.");
        if (callback.Code is { Length: > 2048 })
            throw new InvalidDataException("The GitHub authorization code exceeded the size limit.");
        return await transport.CompleteAsync(
            state, callback.Code, callback.Error, callbackCookie, ct).ConfigureAwait(false);
    }

    private static (string Scope, string ScopeId) ReadScope(CliOptions parsed)
    {
        var scope = parsed.Single("scope");
        var scopeId = parsed.Single("scope-id");
        if (scope == "platform")
        {
            if (scopeId is not null and not "default")
                throw new CliUsageException("Platform Copilot connections use the scope ID `default`.");
            return (scope, "default");
        }
        if (scope != "project" || string.IsNullOrWhiteSpace(scopeId) ||
            scopeId.Length > 256 ||
            scopeId.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new CliUsageException("Use `--scope project --scope-id <id>` or `--scope platform`.");
        return (scope, scopeId);
    }

    private static Guid? ReadCursor(string? value)
    {
        if (value is null) return null;
        if (!Guid.TryParseExact(value, "D", out var cursor) || cursor == Guid.Empty)
            throw new CliUsageException("--after-connection-id must be a non-empty GUID.");
        return cursor;
    }

    private static Guid ReadConnectionId(string? value) =>
        Guid.TryParseExact(value, "D", out var connectionId) && connectionId != Guid.Empty
            ? connectionId
            : throw new CliUsageException("Set --connection-id to a non-empty GUID.");

    private static string ReadAuthorizationState(Uri authorizationUri)
    {
        if (!authorizationUri.IsAbsoluteUri ||
            authorizationUri.Scheme != Uri.UriSchemeHttps ||
            authorizationUri.Host != "github.com" ||
            !authorizationUri.IsDefaultPort ||
            authorizationUri.AbsolutePath != "/login/oauth/authorize" ||
            authorizationUri.UserInfo.Length != 0 ||
            authorizationUri.Fragment.Length != 0)
            throw new InvalidDataException("The Broker returned an invalid GitHub authorization address.");

        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var pair in authorizationUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
                throw new InvalidDataException("The Broker returned an invalid GitHub authorization address.");
            var key = DecodeQueryValue(pair[..separator]);
            var value = DecodeQueryValue(pair[(separator + 1)..]);
            if (!values.TryGetValue(key, out var entries))
                values[key] = entries = [];
            entries.Add(value);
        }
        if (!values.TryGetValue("redirect_uri", out var redirectUris) || redirectUris.Count != 1 ||
            !Uri.TryCreate(redirectUris[0], UriKind.Absolute, out var redirectUri) ||
            redirectUri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(redirectUri.Host) ||
            redirectUri.AbsolutePath != BrowserCallbackPath ||
            redirectUri.UserInfo.Length != 0 ||
            redirectUri.Query.Length != 0 ||
            redirectUri.Fragment.Length != 0 ||
            !values.TryGetValue("state", out var states) || states.Count != 1 ||
            string.IsNullOrWhiteSpace(states[0]) || states[0].Length > 8192 ||
            states[0].Any(char.IsControl))
            throw new InvalidDataException("The Broker returned an invalid GitHub authorization address.");
        return states[0];
    }

    private static string DecodeQueryValue(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException exception)
        {
            throw new InvalidDataException("The Broker returned an invalid GitHub authorization address.", exception);
        }
    }

    private static async Task WriteJsonAsync(TextWriter output, object value)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(value, OutputJson)).ConfigureAwait(false);
    }

    private sealed record CopilotConnectionReceipt(
        Guid ConnectionId, long Revision, string Scope, string ScopeId, string State, DateTimeOffset FreshUntil);
    private sealed record CopilotConnectionPage(
        IReadOnlyList<CopilotConnectionReceipt> Items, Guid? NextAfterConnectionId);
    private sealed record CopilotConnectionStart(
        CopilotConnectionReceipt Connection, Uri AuthorizationUri);
    private sealed record CopilotConnectionMutation(Guid ConnectionId, long ExpectedRevision);

    private sealed class CopilotConnectionTransport(HttpClient httpClient, CliIdentity identity, string bearer)
    {
        public async Task<CopilotConnectionPage> ListAsync(
            string scope, string scopeId, Guid? afterConnectionId, CancellationToken ct)
        {
            var query = $"?scope={Uri.EscapeDataString(scope)}&scopeId={Uri.EscapeDataString(scopeId)}&pageSize=50" +
                (afterConnectionId is null
                    ? string.Empty
                    : $"&afterConnectionId={afterConnectionId.Value:D}");
            var response = await SendJsonAsync(
                HttpMethod.Get, "/list" + query, null, null, ct).ConfigureAwait(false);
            return Deserialize<CopilotConnectionPage>(response);
        }

        public async Task<CopilotConnectionReceipt> ReadStatusAsync(Guid connectionId, CancellationToken ct)
        {
            var response = await SendJsonAsync(
                HttpMethod.Get, $"/{connectionId:D}", null, null, ct).ConfigureAwait(false);
            return Deserialize<CopilotConnectionReceipt>(response);
        }

        public async Task<CopilotConnectionReceipt> ChangeAsync(
            string action, CopilotConnectionMutation mutation, CancellationToken ct)
        {
            var response = await SendJsonAsync(
                HttpMethod.Post, action, mutation, null, ct).ConfigureAwait(false);
            return Deserialize<CopilotConnectionReceipt>(response);
        }

        public async Task<(CopilotConnectionStart Start, string CallbackCookie)> BeginAsync(
            string scope, string scopeId, Uri callbackUri, CancellationToken ct)
        {
            var request = new JsonObject
            {
                ["scope"] = scope,
                ["scopeId"] = scopeId,
                ["cliCallbackUri"] = callbackUri.AbsoluteUri,
            };
            using var response = await SendResponseAsync(
                HttpMethod.Post, "/begin", request, null, ct).ConfigureAwait(false);
            var cookie = ReadCallbackCookie(response);
            return (Deserialize<CopilotConnectionStart>(
                await ReadResponseAsync(response, ct).ConfigureAwait(false)), cookie);
        }

        public async Task<CopilotConnectionReceipt> CompleteAsync(
            string state, string? code, string? error, string callbackCookie, CancellationToken ct)
        {
            var request = new JsonObject
            {
                ["state"] = state,
                ["code"] = code,
            };
            if (error is not null)
                request["error"] = error;
            using var response = await SendResponseAsync(
                HttpMethod.Post, "/complete", request, callbackCookie, ct)
                .ConfigureAwait(false);
            return Deserialize<CopilotConnectionReceipt>(
                await ReadResponseAsync(response, ct).ConfigureAwait(false));
        }

        private async Task<byte[]> SendJsonAsync(
            HttpMethod method,
            string path,
            object? body,
            string? callbackCookie,
            CancellationToken ct)
        {
            using var response = await SendResponseAsync(
                method, path, body, callbackCookie, ct).ConfigureAwait(false);
            return await ReadResponseAsync(response, ct).ConfigureAwait(false);
        }

        private async Task<HttpResponseMessage> SendResponseAsync(
            HttpMethod method,
            string path,
            object? body,
            string? callbackCookie,
            CancellationToken ct)
        {
            if (callbackCookie is not null &&
                (path != "/complete" || callbackCookie.Length != 64 ||
                 callbackCookie.Any(character => !Uri.IsHexDigit(character))))
                throw new InvalidDataException("The Copilot callback cookie is invalid.");

            var requestUri = new Uri(identity.Target.Gateway, "/api/connections/copilot-user/v1" + path);
            if (!string.Equals(
                    requestUri.GetLeftPart(UriPartial.Authority),
                    identity.Target.Gateway.GetLeftPart(UriPartial.Authority),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The Copilot request is outside the selected Gateway.");
            using var request = new HttpRequestMessage(method, requestUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", identity.Target.TenantId);
            if (callbackCookie is not null)
                request.Headers.TryAddWithoutValidation("Cookie", $"{CallbackCookieName}={callbackCookie}");

            byte[]? bodyBytes = null;
            try
            {
                if (body is not null)
                {
                    bodyBytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
                    request.Content = new ByteArrayContent(bodyBytes);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                }
                var response = await httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    using (response)
                    {
                        var responseBytes = await ReadResponseAsync(response, ct).ConfigureAwait(false);
                        try
                        {
                            throw CreateFailure(response.StatusCode, responseBytes);
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(responseBytes);
                        }
                    }
                }
                return response;
            }
            finally
            {
                if (bodyBytes is not null)
                    CryptographicOperations.ZeroMemory(bodyBytes);
            }
        }

        private static string ReadCallbackCookie(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var values))
                throw new InvalidDataException("The Broker did not return the Copilot callback cookie.");
            var cookies = values.ToArray();
            var prefix = CallbackCookieName + "=";
            var matching = cookies.Select(value => value.Split(';', 2)[0].Trim())
                .Where(value => value.StartsWith(prefix, StringComparison.Ordinal))
                .ToArray();
            if (matching.Length != 1 || cookies.Length != 1)
                throw new InvalidDataException("The Broker returned an invalid Copilot callback cookie.");
            var cookie = matching[0][prefix.Length..];
            if (cookie.Length != 64 || cookie.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidDataException("The Broker returned an invalid Copilot callback cookie.");
            return cookie;
        }

        private static async Task<byte[]> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
        {
            if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
                throw new InvalidDataException("The Copilot response exceeded the size limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var content = new MemoryStream();
            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                    if (read == 0) break;
                    if (content.Length + read > MaximumResponseBytes)
                        throw new InvalidDataException("The Copilot response exceeded the size limit.");
                    await content.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                return content.ToArray();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }

        private static T Deserialize<T>(byte[] bytes)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(bytes, Json)
                    ?? throw new InvalidDataException("The Gateway returned an invalid Copilot response.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        private static Exception CreateFailure(HttpStatusCode status, byte[] body)
        {
            string? code = null;
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var name in new[] { "error", "code" })
                    {
                        if (document.RootElement.TryGetProperty(name, out var value) &&
                            value.ValueKind == JsonValueKind.String)
                        {
                            var candidate = value.GetString();
                            if (candidate is { Length: > 0 and <= 80 } &&
                                candidate.All(character => char.IsAsciiLetterOrDigit(character) ||
                                    character is '_' or '-'))
                            {
                                code = candidate;
                                break;
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
            }
            return new InvalidOperationException(
                code is null
                    ? $"Copilot request failed (HTTP {(int)status})."
                    : $"Copilot request failed (HTTP {(int)status}, {code}).");
        }
    }
}
