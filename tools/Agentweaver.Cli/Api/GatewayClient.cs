using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Agentweaver.Cli.Api;

public sealed partial class GatewayClient : IGatewayClient
{
    private const int MaximumOpenApiBytes = 2 * 1024 * 1024;
    private const int MaximumRequestBytes = 64 * 1024 * 1024;
    private const int MaximumResponseBytes = 64 * 1024 * 1024;
    private const int DefaultRequestBytes = 1024 * 1024;
    private const int DefaultResponseBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> TokenCountNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "inputTokens", "outputTokens", "cachedTokens", "cacheWriteTokens",
            "reasoningTokens", "maxPromptTokens", "promptTokens", "totalTokens",
        };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
    };
    private static readonly HashSet<string> ReservedHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "authorization", "cookie", "x-agentweaver-tenant", "x-correlation-id",
            "x-agentweaver-project-authorization",
        };
    private static readonly string[] CorrelationHeaders =
        ["traceparent", "request-id", "x-request-id", "x-correlation-id"];
    private static readonly Regex PathParameterRegex = new(
        @"\{(?<name>[^}:]+)(?::[^}]+)?\}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly ICliAuthentication? _authentication;
    private readonly Uri _gateway;
    private readonly TimeSpan _requestTimeout;
    private readonly object _callsLock = new();
    private readonly List<JsonObject> _calls = [];
    private IReadOnlyDictionary<string, GatewayOperation> _operations =
        new Dictionary<string, GatewayOperation>(StringComparer.Ordinal);

    public GatewayClient(
        HttpClient httpClient,
        CliIdentity identity,
        ICliAuthentication? authentication = null)
        : this(httpClient, identity, authentication, RequestTimeout)
    {
    }

    internal GatewayClient(
        HttpClient httpClient,
        CliIdentity identity,
        ICliAuthentication? authentication,
        TimeSpan requestTimeout)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (requestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _httpClient = httpClient;
        Identity = CliConfiguration.ValidateIdentity(identity);
        _authentication = authentication;
        _gateway = ValidateGateway(identity.Target.Gateway);
        _requestTimeout = requestTimeout;
    }

    public CliIdentity Identity { get; }

    public IReadOnlyList<JsonObject> Calls
    {
        get
        {
            lock (_callsLock)
                return _calls.Select(call => (JsonObject)call.DeepClone()).ToArray();
        }
    }

    public async Task<JsonObject> DiscoverAsync(CancellationToken ct)
    {
        _operations = new Dictionary<string, GatewayOperation>(StringComparer.Ordinal);
        var call = await SendAsync(
            Identity,
            "GET",
            "/openapi/v1.json",
            null,
            null,
            authenticated: false,
            maximumResponseBytes: MaximumOpenApiBytes,
            acceptedMediaTypes: ["application/json"],
            responseHeaders: [],
            onEventChunk: null,
            ct).ConfigureAwait(false);
        if (call.Status != (int)HttpStatusCode.OK ||
            call.Body is not JsonObject document)
        {
            throw new InvalidDataException("The live Gateway OpenAPI document is missing or invalid.");
        }

        var operations = GatewayContract.Parse(document);
        _operations = operations;
        return document;
    }

    public async Task<JsonObject> ValidateAuthorizationContextAsync(CancellationToken ct)
    {
        var operations = await EnsureDiscoveredAsync(ct).ConfigureAwait(false);
        if (!operations.TryGetValue("getAuthorizationContext", out var operation) ||
            operation.Path != "/api/v1/authorization/context" ||
            operation.Method != "GET")
        {
            throw new InvalidDataException("The live Gateway contract does not expose authorization context.");
        }

        var bearer = await GetBearerAsync(Identity, ct).ConfigureAwait(false);
        var result = await InvokeCoreAsync(
            operation,
            new GatewayInvocation(),
            Identity,
            bearer,
            projectBearer: null,
            skipAuthorizationPreflight: true,
            ct).ConfigureAwait(false);
        if (result.Status != (int)HttpStatusCode.OK ||
            result.Body is not JsonObject context)
        {
            throw new UnauthorizedAccessException("The Gateway authorization-context request was denied or invalid.");
        }

        ValidateContext(context, Identity);
        return context;
    }

    public async Task<GatewayResponse> InvokeAsync(
        string operationId,
        GatewayInvocation invocation,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(invocation);

        var operations = await EnsureDiscoveredAsync(ct).ConfigureAwait(false);
        if (!operations.TryGetValue(operationId, out var operation))
            throw new InvalidOperationException("The requested operation was not found in the live Gateway contract.");

        var (path, query, requestHeaders, bodyBytes) = PrepareRequest(operation, invocation);
        var primaryBearer = await GetBearerAsync(Identity, ct).ConfigureAwait(false);
        var callerContext = operationId == "getAuthorizationContext"
            ? null
            : await ReadAuthorizationContextAsync(Identity, primaryBearer, ct).ConfigureAwait(false);

        string? projectBearer = null;
        if (operation.Parameters.Any(parameter =>
                parameter.Location == "header" &&
                parameter.Name.Equals("X-Agentweaver-Project-Authorization", StringComparison.OrdinalIgnoreCase)))
        {
            if (callerContext is null)
                throw new InvalidOperationException("Dual-authorization operations require a prior tenant authorization check.");
            var projectIdentity = CreateProjectAuthorizationIdentity();
            projectBearer = await GetBearerAsync(projectIdentity, ct).ConfigureAwait(false);
            var projectContext = await ReadAuthorizationContextAsync(
                projectIdentity,
                projectBearer,
                ct).ConfigureAwait(false);
            if (!string.Equals(
                    GatewayContract.String(callerContext, "actorId"),
                    GatewayContract.String(projectContext, "actorId"),
                    StringComparison.Ordinal) ||
                !string.Equals(
                    GatewayContract.String(callerContext, "tenantId"),
                    GatewayContract.String(projectContext, "tenantId"),
                    StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException(
                    "The run and project authorization credentials do not belong to the same actor and tenant.");
            }
        }

        var response = await InvokeCoreAsync(
            operation,
            invocation with
            {
                PathParameters = invocation.PathParameters,
                Query = query,
                Headers = requestHeaders,
                Body = invocation.Body,
            },
            Identity,
            primaryBearer,
            projectBearer,
            skipAuthorizationPreflight: true,
            ct,
            pathOverride: path,
            bodyBytesOverride: bodyBytes).ConfigureAwait(false);
        if (operationId == "getAuthorizationContext" &&
            response.Status == (int)HttpStatusCode.OK &&
            response.Body is JsonObject context)
        {
            ValidateContext(context, Identity);
        }
        return response;
    }

    private async Task<GatewayResponse> InvokeCoreAsync(
        GatewayOperation operation,
        GatewayInvocation invocation,
        CliIdentity identity,
        string bearer,
        string? projectBearer,
        bool skipAuthorizationPreflight,
        CancellationToken ct,
        string? pathOverride = null,
        byte[]? bodyBytesOverride = null)
    {
        if (!skipAuthorizationPreflight)
            await ValidateAuthorizationContextAsync(ct).ConfigureAwait(false);

        var (path, query, requestHeaders, bodyBytes) = pathOverride is null
            ? PrepareRequest(operation, invocation)
            : (pathOverride, invocation.Query ?? EmptyParameters, invocation.Headers ?? EmptyParameters, bodyBytesOverride);
        return await SendAsync(
            identity,
            operation.Method,
            path,
            bodyBytes,
            bearer,
            authenticated: true,
            maximumResponseBytes: operation.MaximumResponseBytes,
            acceptedMediaTypes: operation.ResponseMediaTypes,
            responseHeaders: operation.ResponseHeaders,
            onEventChunk: invocation.OnEventChunk,
            ct,
            requestHeaders,
            projectBearer,
            operation.MaximumRequestBytes,
            operation.OperationId).ConfigureAwait(false);
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyParameters =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static GatewayParameter FindParameter(GatewayOperation operation, string location, string name) =>
        operation.Parameters.FirstOrDefault(parameter =>
            parameter.Location == location &&
            (location == "header"
                ? parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                : parameter.Name == name))
        ?? throw new InvalidDataException($"The live Gateway operation does not define path parameter {name}.");

    private async Task<IReadOnlyDictionary<string, GatewayOperation>> EnsureDiscoveredAsync(CancellationToken ct)
    {
        if (_operations.Count == 0)
            await DiscoverAsync(ct).ConfigureAwait(false);
        return _operations;
    }

    private async Task<JsonObject> ReadAuthorizationContextAsync(
        CliIdentity identity,
        string bearer,
        CancellationToken ct)
    {
        var operations = await EnsureDiscoveredAsync(ct).ConfigureAwait(false);
        if (!operations.TryGetValue("getAuthorizationContext", out var operation))
            throw new InvalidDataException("The live Gateway contract does not expose authorization context.");
        var result = await InvokeCoreAsync(
            operation,
            new GatewayInvocation(),
            identity,
            bearer,
            projectBearer: null,
            skipAuthorizationPreflight: true,
            ct).ConfigureAwait(false);
        if (result.Status != (int)HttpStatusCode.OK || result.Body is not JsonObject context)
            throw new UnauthorizedAccessException("The Gateway authorization-context request was denied or invalid.");
        ValidateContext(context, identity);
        return context;
    }

    private async Task<string> GetBearerAsync(CliIdentity identity, CancellationToken ct)
    {
        if (_authentication is null)
            throw new InvalidOperationException("Sign in with `agentweaver auth login` before invoking API operations.");
        var token = await _authentication.GetBearerAsync(identity, InteractionPolicy.Never, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token) ||
            token.Length > 16 * 1024 ||
            token.Any(char.IsWhiteSpace) ||
            token.Contains('\r') ||
            token.Contains('\n'))
        {
            throw new InvalidOperationException("The authentication provider did not return a valid bearer token.");
        }
        return token;
    }

    private CliIdentity CreateProjectAuthorizationIdentity()
    {
        var scopes = Identity.Scopes
            .Where(scope => scope is not "projects.admin" and not "projects.orchestrator")
            .Append("projects.admin")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return Identity with { Scopes = scopes, ProjectId = null, RunId = null };
    }

    private static void ValidateContext(JsonObject context, CliIdentity identity)
    {
        string[] requiredKeys =
        [
            "contractVersion", "issuer", "actorId", "tenantId", "membershipRevision",
            "boundProjectId", "boundRunId", "effectiveAuthority",
        ];
        if (context.Count != requiredKeys.Length ||
            requiredKeys.Any(key => !context.ContainsKey(key)) ||
            context["contractVersion"]?.GetValue<int>() != 1 ||
            string.IsNullOrWhiteSpace(GatewayContract.String(context, "issuer")) ||
            string.IsNullOrWhiteSpace(GatewayContract.String(context, "actorId")) ||
            GatewayContract.String(context, "tenantId") != identity.Target.TenantId ||
            !TryPositiveInteger(context["membershipRevision"], out _) ||
            !OptionalIdentifier(context["boundProjectId"], out var projectId) ||
            !OptionalIdentifier(context["boundRunId"], out var runId) ||
            projectId != identity.ProjectId ||
            runId != identity.RunId ||
            runId is not null && projectId is null ||
            context["effectiveAuthority"] is not JsonArray authority ||
            authority.Any(grant => !ValidAuthorityGrant(grant)))
        {
            throw new UnauthorizedAccessException(
                "The Gateway authorization context does not match the selected tenant and project/run target.");
        }

        if (!Uri.TryCreate(GatewayContract.String(context, "issuer"), UriKind.Absolute, out var issuer) ||
            !string.Equals(
                issuer.AbsoluteUri.TrimEnd('/'),
                identity.Target.Issuer.AbsoluteUri.TrimEnd('/'),
                StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The Gateway authorization context came from a different issuer.");
        }
    }

    private static bool ValidAuthorityGrant(JsonNode? node)
    {
        if (node is not JsonObject grant ||
            grant.Count != 3 ||
            GatewayContract.String(grant, "resourceType") is not ("platform" or "tenant" or "project") ||
            string.IsNullOrWhiteSpace(GatewayContract.String(grant, "resourceId")) ||
            grant["permissions"] is not JsonArray permissions)
            return false;

        var names = new HashSet<string>(StringComparer.Ordinal)
        {
            "readProjects", "writeProjects", "createProjects", "readRunSelection",
            "acceptRunSelection", "cancelRun", "accessPrivateKnowledge", "readPlatformRuntimeDefaults",
            "writePlatformRuntimeDefaults",
        };
        return permissions.All(permission =>
            permission is JsonObject item &&
            item.Count == 2 &&
            item["permission"] is JsonValue permissionValue &&
            permissionValue.TryGetValue<string>(out var name) &&
            names.Contains(name) &&
            TryPositiveInteger(item["roleRevision"], out _));
    }

    private static bool OptionalIdentifier(JsonNode? node, out string? identifier)
    {
        if (node is null)
        {
            identifier = null;
            return true;
        }
        if (node is JsonValue value &&
            value.TryGetValue<string>(out var text) &&
            !string.IsNullOrWhiteSpace(text))
        {
            identifier = text;
            return true;
        }
        identifier = null;
        return false;
    }

    private static bool TryPositiveInteger(JsonNode? node, out long number)
    {
        number = 0;
        return node is JsonValue value && value.TryGetValue<long>(out number) && number > 0;
    }

    private (
        string Path,
        IReadOnlyDictionary<string, string> Query,
        IReadOnlyDictionary<string, string> Headers,
        byte[]? BodyBytes) PrepareRequest(GatewayOperation operation, GatewayInvocation invocation)
    {
        var paths = invocation.PathParameters ?? EmptyParameters;
        var query = invocation.Query ?? EmptyParameters;
        var headers = (invocation.Headers ?? EmptyParameters)
            .ToDictionary(parameter => parameter.Key, parameter => parameter.Value, StringComparer.OrdinalIgnoreCase);
        var templateNames = PathParameterRegex.Matches(operation.Path)
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);
        if (paths.Keys.Any(name => !templateNames.Contains(name)))
            throw new InvalidOperationException("An API path parameter is not advertised by the live operation.");

        var path = PathParameterRegex.Replace(operation.Path, match =>
        {
            var name = match.Groups["name"].Value;
            if (!paths.TryGetValue(name, out var value))
                throw new InvalidOperationException($"Missing required path parameter: {name}");
            var parameter = FindParameter(operation, "path", name);
            var normalized = ValidateParameter(parameter, value);
            if (normalized is "." or "..")
                throw new InvalidOperationException("Dot-segment API path parameters are not allowed.");
            ValidateBoundSelector(name, normalized);
            return Uri.EscapeDataString(normalized);
        });

        var queryDefinitions = operation.Parameters
            .Where(parameter => parameter.Location == "query")
            .ToDictionary(parameter => parameter.Name, StringComparer.Ordinal);
        if (query.Keys.Any(name => !queryDefinitions.ContainsKey(name)))
            throw new InvalidOperationException("An API query parameter is not advertised by the live operation.");
        foreach (var parameter in queryDefinitions.Values.Where(parameter => parameter.Required))
        {
            if (!query.ContainsKey(parameter.Name))
                throw new InvalidOperationException($"Missing required query parameter: {parameter.Name}");
        }

        var normalizedQuery = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in query)
        {
            var normalized = ValidateParameter(queryDefinitions[name], value);
            ValidateBoundSelector(name, normalized);
            normalizedQuery.Add(name, normalized);
        }

        var headerDefinitions = operation.Parameters
            .Where(parameter => parameter.Location == "header")
            .ToDictionary(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var name in headers.Keys)
        {
            if (ReservedHeaders.Contains(name) || !headerDefinitions.ContainsKey(name))
                throw new InvalidOperationException("An API request header is not advertised or is reserved for identity.");
        }
        foreach (var parameter in headerDefinitions.Values)
        {
            if (parameter.Name.Equals("X-Agentweaver-Tenant", StringComparison.OrdinalIgnoreCase) ||
                parameter.Name.Equals("X-Agentweaver-Project-Authorization", StringComparison.OrdinalIgnoreCase))
                continue;
            if (parameter.Required && !headers.ContainsKey(parameter.Name))
                throw new InvalidOperationException($"Missing required header: {parameter.Name}");
            if (headers.TryGetValue(parameter.Name, out var value))
                _ = ValidateParameter(parameter, value);
        }

        if (!operation.HasRequestBody && invocation.Body is not null)
            throw new InvalidOperationException("This API operation does not accept a request body.");
        if (operation.BodyRequired && invocation.Body is null)
            throw new InvalidOperationException("This API operation requires a JSON request body.");
        if (invocation.Body is JsonObject body)
        {
            if (body["projectId"] is JsonValue projectValue &&
                projectValue.TryGetValue<string>(out var projectId))
                ValidateBoundSelector("projectId", projectId);
            if (body["runId"] is JsonValue runValue &&
                runValue.TryGetValue<string>(out var runId))
                ValidateBoundSelector("runId", runId);
        }

        var bodyBytes = invocation.Body?.ToJsonString(JsonOptions) is { } json
            ? Encoding.UTF8.GetBytes(json)
            : null;
        if (bodyBytes?.Length > operation.MaximumRequestBytes)
            throw new InvalidOperationException($"The JSON request body exceeds the advertised {operation.MaximumRequestBytes}-byte limit.");

        return (AddQuery(path, normalizedQuery), normalizedQuery, headers, bodyBytes);
    }

    private static string AddQuery(string path, IReadOnlyDictionary<string, string> query)
    {
        if (query.Count == 0)
            return path;
        var encoded = string.Join(
            "&",
            query.Select(parameter =>
                $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));
        return path + "?" + encoded;
    }

    private void ValidateBoundSelector(string name, string value)
    {
        if (Identity.ProjectId is not null && name.Equals("projectId", StringComparison.OrdinalIgnoreCase) &&
            value != Identity.ProjectId ||
            Identity.RunId is not null && name.Equals("runId", StringComparison.OrdinalIgnoreCase) &&
            value != Identity.RunId)
        {
            throw new InvalidOperationException("The API request does not match the selected project/run target.");
        }
    }

    private static string ValidateParameter(GatewayParameter parameter, string value)
    {
        var type = GatewayContract.String(parameter.Schema, "type") ?? "string";
        string normalized;
        switch (type)
        {
            case "integer":
                if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) ||
                    Math.Abs((double)integer) > 9_007_199_254_740_991d)
                    throw new InvalidOperationException($"Parameter {parameter.Name} must be a safe integer.");
                normalized = integer.ToString(CultureInfo.InvariantCulture);
                break;
            case "number":
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                    !double.IsFinite(number))
                    throw new InvalidOperationException($"Parameter {parameter.Name} must be a finite number.");
                normalized = number.ToString("R", CultureInfo.InvariantCulture);
                break;
            case "boolean":
                if (!bool.TryParse(value, out var boolean))
                    throw new InvalidOperationException($"Parameter {parameter.Name} must be true or false.");
                normalized = boolean ? "true" : "false";
                break;
            default:
                normalized = value;
                break;
        }

        if (parameter.Schema["enum"] is JsonArray values &&
            !values.Any(item => EnumValueEquals(item, normalized, type)))
        {
            throw new InvalidOperationException($"Parameter {parameter.Name} is not an allowed value.");
        }

        if (parameter.Schema["minLength"] is JsonValue minLength &&
            minLength.TryGetValue<int>(out var minimumLength) &&
            normalized.Length < minimumLength ||
            parameter.Schema["maxLength"] is JsonValue maxLength &&
            maxLength.TryGetValue<int>(out var maximumLength) &&
            normalized.Length > maximumLength ||
            parameter.Schema["minimum"] is JsonValue minimum &&
            minimum.TryGetValue<double>(out var minimumValue) &&
            type is "integer" or "number" &&
            double.Parse(normalized, CultureInfo.InvariantCulture) < minimumValue ||
            parameter.Schema["maximum"] is JsonValue maximum &&
            maximum.TryGetValue<double>(out var maximumValue) &&
            type is "integer" or "number" &&
            double.Parse(normalized, CultureInfo.InvariantCulture) > maximumValue)
        {
            throw new InvalidOperationException($"Parameter {parameter.Name} is outside its allowed shape.");
        }
        if (parameter.Schema["pattern"] is JsonValue patternValue &&
            patternValue.TryGetValue<string>(out var pattern))
        {
            try
            {
                if (!Regex.IsMatch(normalized, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                    throw new InvalidOperationException($"Parameter {parameter.Name} is outside its allowed shape.");
            }
            catch (RegexMatchTimeoutException exception)
            {
                throw new InvalidOperationException($"Parameter {parameter.Name} did not match the advertised pattern.", exception);
            }
        }
        return normalized;
    }

    private static bool EnumValueEquals(JsonNode? node, string value, string type)
    {
        if (node is not JsonValue enumValue)
            return false;
        return type switch
        {
            "integer" when enumValue.TryGetValue<long>(out var integer) =>
                value == integer.ToString(CultureInfo.InvariantCulture),
            "number" when enumValue.TryGetValue<double>(out var number) =>
                double.TryParse(value, CultureInfo.InvariantCulture, out var parsed) && parsed.Equals(number),
            "boolean" when enumValue.TryGetValue<bool>(out var boolean) =>
                value == (boolean ? "true" : "false"),
            _ when enumValue.TryGetValue<string>(out var text) => value == text,
            _ => false,
        };
    }

    private async Task<GatewayResponse> SendAsync(
        CliIdentity identity,
        string method,
        string pathAndQuery,
        byte[]? body,
        string? bearer,
        bool authenticated,
        int maximumResponseBytes,
        IReadOnlyList<string> acceptedMediaTypes,
        IReadOnlyList<string> responseHeaders,
        Func<string, ValueTask>? onEventChunk,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? additionalHeaders = null,
        string? projectBearer = null,
        int maximumRequestBytes = MaximumRequestBytes,
        string? operationId = null)
    {
        var requestId = Guid.NewGuid();
        var started = Stopwatch.GetTimestamp();
        var evidence = new JsonObject
        {
            ["requestId"] = requestId.ToString("D"),
            ["method"] = method,
            ["path"] = pathAndQuery.Split('?', 2)[0],
            ["query"] = RedactQuery(ParseQuery(pathAndQuery)),
            ["requestBody"] = body is null ? null : RedactJson(ParseJson(body)),
            ["status"] = null,
            ["traceId"] = null,
            ["responseBody"] = null,
            ["responseHeaders"] = new JsonObject(),
            ["contentType"] = null,
            ["operationId"] = operationId,
        };

        try
        {
            if (body?.Length > maximumRequestBytes || body?.Length > MaximumRequestBytes)
                throw new InvalidOperationException($"The JSON request body exceeds its {maximumRequestBytes}-byte limit.");
            var target = new Uri(_gateway, pathAndQuery);
            if (!SameOrigin(_gateway, target) || target.UserInfo.Length > 0)
                throw new InvalidOperationException("Refusing to send a Gateway request outside the configured origin.");

            using var request = new HttpRequestMessage(new HttpMethod(method), target);
            request.Headers.Accept.Clear();
            foreach (var mediaType in acceptedMediaTypes.Count == 0 ? ["application/json"] : acceptedMediaTypes)
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(mediaType));
            request.Headers.TryAddWithoutValidation("X-Correlation-Id", requestId.ToString("D"));
            if (authenticated)
            {
                if (bearer is null || identity.Target.TenantId.Length == 0)
                    throw new InvalidOperationException("Authenticated API calls require an explicit tenant and broker identity.");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", identity.Target.TenantId);
                if (projectBearer is not null)
                    request.Headers.TryAddWithoutValidation("X-Agentweaver-Project-Authorization", "Bearer " + projectBearer);
            }

            if (additionalHeaders is not null)
            {
                foreach (var (name, value) in additionalHeaders)
                {
                    if (ReservedHeaders.Contains(name) ||
                        name.Equals("X-Agentweaver-Project-Authorization", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Request headers cannot override the selected auth identity.");
                    if (!request.Headers.TryAddWithoutValidation(name, value))
                        throw new InvalidOperationException($"The advertised request header {name} is invalid.");
                }
            }
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_requestTimeout);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            evidence["status"] = (int)response.StatusCode;
            var contentType = GatewayContract.NormalizeMediaType(
                response.Content.Headers.ContentType?.ToString() ?? string.Empty);
            evidence["contentType"] = contentType;
            evidence["traceId"] = FindCorrelationId(response);
            var capturedHeaders = CaptureHeaders(response, responseHeaders);
            evidence["responseHeaders"] = ToJsonObject(capturedHeaders);

            if (response.Content.Headers.ContentLength is { } contentLength &&
                contentLength > maximumResponseBytes)
            {
                throw new InvalidDataException($"The Gateway response exceeded its {maximumResponseBytes}-byte limit.");
            }

            var (bytes, textBody) = await ReadResponseAsync(
                response,
                maximumResponseBytes,
                contentType == "text/event-stream" ? onEventChunk : null,
                contentType == "text/event-stream" ? ct : timeout.Token).ConfigureAwait(false);
            JsonNode? responseBody = null;
            byte[]? binaryBody = null;
            if (contentType == "application/octet-stream")
            {
                binaryBody = bytes;
                evidence["responseBody"] = new JsonObject
                {
                    ["mediaType"] = contentType,
                    ["bytes"] = bytes.Length,
                };
            }
            else if (bytes.Length > 0)
            {
                if (contentType is "application/json" or "application/problem+json")
                {
                    responseBody = ParseJson(bytes);
                    evidence["responseBody"] = RedactJson(responseBody);
                }
                else
                {
                    responseBody = JsonValue.Create(textBody);
                    evidence["responseBody"] = RedactText(textBody);
                }
            }

            if ((int)response.StatusCode is >= 200 and < 300 &&
                bytes.Length > 0 &&
                acceptedMediaTypes.Count > 0 &&
                !acceptedMediaTypes.Contains(contentType, StringComparer.Ordinal))
            {
                throw new InvalidDataException("The Gateway response media type was not advertised by the live operation.");
            }
            return new GatewayResponse(
                (int)response.StatusCode,
                responseBody,
                capturedHeaders,
                evidence,
                binaryBody);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            evidence["error"] = RedactText(exception.Message);
            if (exception is OperationCanceledException && ct.IsCancellationRequested)
                throw;
            throw new GatewayTransportException(
                exception is OperationCanceledException && !ct.IsCancellationRequested
                    ? "The Gateway request timed out."
                    : RedactText(exception.Message),
                exception);
        }
        finally
        {
            evidence["elapsedMs"] = Math.Max(0, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            AddCall(evidence);
        }
    }

    private async Task<(byte[] Bytes, string Text)> ReadResponseAsync(
        HttpResponseMessage response,
        int maximumBytes,
        Func<string, ValueTask>? onEventChunk,
        CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        var chars = new char[8192];
        var decoder = Encoding.UTF8.GetDecoder();
        var isEventStream = onEventChunk is not null;
        var eventRedactor = isEventStream ? new SseEventRedactor(onEventChunk!) : null;
        while (true)
        {
            var count = await stream.ReadAsync(bytes, ct).ConfigureAwait(false);
            if (count == 0)
                break;
            if (buffer.Length + count > maximumBytes || buffer.Length + count > MaximumResponseBytes)
                throw new InvalidDataException($"The Gateway response exceeded its {maximumBytes}-byte limit.");
            buffer.Write(bytes, 0, count);
            if (isEventStream)
            {
                decoder.Convert(bytes, 0, count, chars, 0, chars.Length, flush: false,
                    out _, out var charsUsed, out _);
                if (charsUsed > 0)
                    await eventRedactor!.WriteAsync(new string(chars, 0, charsUsed)).ConfigureAwait(false);
            }
        }

        var raw = buffer.ToArray();
        var text = Encoding.UTF8.GetString(raw);
        if (isEventStream)
        {
            decoder.Convert([], 0, 0, chars, 0, chars.Length, flush: true,
                out _, out var charsUsed, out _);
            if (charsUsed > 0)
                await eventRedactor!.WriteAsync(new string(chars, 0, charsUsed)).ConfigureAwait(false);
            await eventRedactor!.CompleteAsync().ConfigureAwait(false);
        }
        return (raw, text);
    }

    private sealed class SseEventRedactor(Func<string, ValueTask> write)
    {
        private readonly StringBuilder _event = new();

        public async ValueTask WriteAsync(string chunk)
        {
            foreach (var character in chunk)
            {
                _event.Append(character);
                if (EndsEvent(_event))
                {
                    await write(RedactText(_event.ToString())).ConfigureAwait(false);
                    _event.Clear();
                }
            }
        }

        public async ValueTask CompleteAsync()
        {
            if (_event.Length == 0)
                return;
            await write(RedactText(_event.ToString())).ConfigureAwait(false);
            _event.Clear();
        }

        private static bool EndsEvent(StringBuilder value) =>
            value.Length >= 2 &&
            (value[^1] == '\n' && value[^2] == '\n' ||
             value[^1] == '\r' && value[^2] == '\r') ||
            value.Length >= 4 &&
            value[^4] == '\r' && value[^3] == '\n' &&
            value[^2] == '\r' && value[^1] == '\n';
    }

    private IReadOnlyDictionary<string, string> CaptureHeaders(HttpResponseMessage response, IReadOnlyList<string> names)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var value = response.Headers.TryGetValues(name, out var responseValues)
                ? string.Join(",", responseValues)
                : response.Content.Headers.TryGetValues(name, out var contentValues)
                    ? string.Join(",", contentValues)
                    : null;
            if (value is not null && !ReservedHeaders.Contains(name) && !IsSensitiveName(name))
                result[name] = RedactText(value);
        }
        return result;
    }

    private static JsonObject ToJsonObject(IReadOnlyDictionary<string, string> values)
    {
        var result = new JsonObject();
        foreach (var (name, value) in values)
            result[name] = value;
        return result;
    }

    private static string? FindCorrelationId(HttpResponseMessage response)
    {
        foreach (var name in CorrelationHeaders)
        {
            if (response.Headers.TryGetValues(name, out var values))
                return RedactText(values.FirstOrDefault());
        }
        return null;
    }

    private static Dictionary<string, string> ParseQuery(string pathAndQuery)
    {
        var query = pathAndQuery.Split('?', 2).Skip(1).FirstOrDefault();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query))
            return result;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var name = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            result[name] = value;
        }
        return result;
    }

    private static JsonObject RedactQuery(IReadOnlyDictionary<string, string> query)
    {
        var result = new JsonObject();
        foreach (var (name, value) in query)
            result[name] = IsSensitiveName(name) ? "[REDACTED]" : RedactText(value);
        return result;
    }

    private static JsonNode? RedactJson(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var result = new JsonObject();
            var hasSensitiveDescriptor = obj.Any(item =>
                IsDescriptorKey(item.Key) &&
                item.Value is JsonValue descriptor &&
                descriptor.TryGetValue<string>(out var text) &&
                IsSensitiveDescriptor(text));
            foreach (var (name, value) in obj)
            {
                result[name] = (IsSensitiveName(name) &&
                    !(TokenCountNames.Contains(name) && IsSafeTokenCount(value)) ||
                    hasSensitiveDescriptor && IsDescriptorValueKey(name))
                    ? RedactSensitiveValue(value)
                    : RedactJson(value);
            }
            return result;
        }
        if (node is JsonArray array)
        {
            if (array.Count == 2 &&
                array[0] is JsonValue descriptor &&
                descriptor.TryGetValue<string>(out var descriptorText) &&
                IsSensitiveDescriptor(descriptorText))
            {
                return new JsonArray(array[0]?.DeepClone(), RedactSensitiveValue(array[1]));
            }
            var result = new JsonArray();
            foreach (var value in array)
                result.Add(RedactJson(value));
            return result;
        }
        if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
            return JsonValue.Create(RedactText(text));
        return node?.DeepClone();
    }

    private static JsonNode? RedactSensitiveValue(JsonNode? value)
    {
        if (value is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var (name, item) in obj)
                result[name] = RedactSensitiveValue(item);
            return result;
        }
        if (value is JsonArray array)
        {
            var result = new JsonArray();
            foreach (var item in array)
                result.Add(RedactSensitiveValue(item));
            return result;
        }
        return JsonValue.Create("[REDACTED]");
    }

    private static bool IsSafeTokenCount(JsonNode? value) =>
        value is null ||
        value is JsonValue json && json.TryGetValue<double>(out var number) &&
        double.IsFinite(number) && number >= 0;

    private static bool IsDescriptorKey(string name) =>
        name.Equals("descriptor", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("header", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("key", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("label", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("name", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("type", StringComparison.OrdinalIgnoreCase);

    private static bool IsDescriptorValueKey(string name) =>
        name.Equals("val", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("value", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("values", StringComparison.OrdinalIgnoreCase);

    private static bool IsSensitiveDescriptor(string value)
    {
        var descriptor = value.Trim();
        return SensitiveDescriptorRegex().IsMatch(descriptor) ||
               SensitiveKeyDescriptorRegex().IsMatch(descriptor) ||
               descriptor.Equals("api", StringComparison.OrdinalIgnoreCase) ||
               descriptor.Equals("provider", StringComparison.OrdinalIgnoreCase);
    }

    internal static string RedactText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;
        if (value.TrimStart() is { Length: > 0 } trimmed && trimmed[0] is '{' or '[')
        {
            try
            {
                var json = JsonNode.Parse(value, documentOptions: new JsonDocumentOptions { MaxDepth = 32 });
                if (json is JsonObject or JsonArray)
                    return RedactJson(json)!.ToJsonString(JsonOptions);
            }
            catch (JsonException)
            {
                // Continue with text redaction for strings that only contain JSON fragments.
            }
        }
        var redacted = UrlRegex().Replace(value, SanitizeUrl);
        redacted = BearerRegex().Replace(redacted, "$1[REDACTED]");
        redacted = JwtRegex().Replace(redacted, "[REDACTED]");
        redacted = GitHubTokenRegex().Replace(redacted, "[REDACTED_GITHUB_TOKEN]");
        redacted = QuerySecretRegex().Replace(redacted, "$1[REDACTED]");
        redacted = SecretAssignmentRegex().Replace(redacted, "$1[REDACTED]");
        return SecretCanaryRegex().Replace(redacted, "[REDACTED_CANARY]");
    }

    private static string SanitizeUrl(Match match) =>
        Uri.TryCreate(match.Value, UriKind.Absolute, out var url)
            ? url.GetLeftPart(UriPartial.Path)
            : "[REDACTED_URL]";

    private static bool IsSensitiveName(string name)
    {
        var normalized = new string(name.Where(char.IsLetterOrDigit).ToArray());
        return normalized.Contains("token", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("executionkey", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("providerkey", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("privatekey", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("kubeconfig", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("storagestate", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("signedurl", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonNode? ParseJson(byte[] bytes)
    {
        try
        {
            return JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            return JsonValue.Create(Encoding.UTF8.GetString(bytes));
        }
    }

    private void AddCall(JsonObject evidence)
    {
        lock (_callsLock)
            _calls.Add((JsonObject)evidence.DeepClone());
    }

    private static Uri ValidateGateway(Uri gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        if (!gateway.IsAbsoluteUri ||
            gateway.UserInfo.Length > 0 ||
            gateway.Query.Length > 0 ||
            gateway.Fragment.Length > 0 ||
            gateway.AbsolutePath != "/" ||
            !(gateway.Scheme == Uri.UriSchemeHttps ||
              gateway.Scheme == Uri.UriSchemeHttp && gateway.IsLoopback))
        {
            throw new ArgumentException("Gateway URL must be an HTTPS origin root (HTTP is allowed only for loopback fixtures).");
        }
        return gateway;
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    [GeneratedRegex(@"(?i)(\bbearer\s+)[A-Za-z0-9._~+/-]+=*")]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"(?i)([?&](?:access[_-]?token|refresh[_-]?token|id[_-]?token|client[_-]?secret|authorization|password|code|state|api[_-]?key|execution[_-]?key|provider[_-]?key)=)[^&\s]+")]
    private static partial Regex QuerySecretRegex();

    [GeneratedRegex(@"https?://[^\s""'\\]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"(?i)((?:\\?[""']?)(?:authorization|cookie|access[_-]?token|refresh[_-]?token|id[_-]?token|token|credentials?|api[_-]?key|execution[_-]?key|provider[_-]?key|client[_-]?secret|secret|password|kubeconfig|storage[_-]?state|signed[_-]?url)(?:\\?[""']?)\s*[:=]\s*(?:\\?[""']?))(?:bearer\s+)?[^""'`,;\s}\]]+")]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex(@"\bgh(?:p|o|u|s|r)_[A-Za-z0-9_]{20,}\b")]
    private static partial Regex GitHubTokenRegex();

    [GeneratedRegex(@"\b(?:(?:credential|secret|token|bearer)[_-]?canary|canary[_-]?(?:credential|secret|token|bearer))(?:[-_][A-Za-z0-9]+)*\b", RegexOptions.IgnoreCase)]
    private static partial Regex SecretCanaryRegex();

    [GeneratedRegex(@"(?i)(?:^|[ _-])(?:authorization|cookie|token|credential|secret|password|kubeconfig|storagestate|signedurl)$")]
    private static partial Regex SensitiveDescriptorRegex();

    [GeneratedRegex(@"(?i)(?:^|[ _-])(?:api|execution|provider)[ _-]?key$")]
    private static partial Regex SensitiveKeyDescriptorRegex();
}

public sealed class GatewayTransportException(string message, Exception innerException)
    : IOException(message, innerException)
{
}
