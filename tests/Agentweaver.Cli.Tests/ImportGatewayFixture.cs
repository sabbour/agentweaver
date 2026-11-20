using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Gateway;

namespace Agentweaver.Cli.Tests;

internal sealed class ImportGatewayFixture : HttpMessageHandler, ICliAuthentication
{
    internal readonly List<(string Method, string Path, string? Bearer, string? Tenant)> Requests = [];
    internal readonly List<JsonObject> PostBodies = [];
    internal readonly Dictionary<string, (string Body, string Actor, JsonObject Receipt)> Receipts = new(StringComparer.Ordinal);
    internal JsonObject Document { get; } = MakeDocument();
    internal JsonObject Target { get; } = Context(includeInstallation: true);
    internal JsonObject Caller { get; } = Context(includeInstallation: false);
    internal JsonObject? Provider { get; set; }
    internal bool NoStore { get; set; } = true;
    internal string? RawTarget { get; set; }
    internal string TargetMediaType { get; set; } = "application/json";
    internal HttpStatusCode TargetStatus { get; set; } = HttpStatusCode.OK;
    internal int BearerCalls { get; private set; }
    internal int TargetCalls { get; private set; }
    internal string? OwnerInstallation { get; set; }
    internal Action<int>? BeforeTarget { get; set; }
    internal Func<int, JsonObject, HttpResponseMessage, HttpResponseMessage>? AfterCreate { get; set; }
    internal (HttpStatusCode Status, string Code)? Rejection { get; set; }
    internal HttpClient Client => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add((request.Method.Method, path, request.Headers.Authorization?.Parameter,
            request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenants) ? tenants.Single() : null));
        if (path == "/api/v1/authorization/target-context")
        {
            TargetCalls++;
            BeforeTarget?.Invoke(TargetCalls);
        }
        if (request.Method == HttpMethod.Post && path == "/api/v1/platform/byok-providers")
        {
            var input = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            PostBodies.Add(input.DeepClone().AsObject());
            if (OwnerInstallation is not null && input["targetInstallationId"]!.GetValue<string>() != OwnerInstallation)
                return Error(HttpStatusCode.Conflict, "target_installation_mismatch");
            if (Rejection is { } rejection) return Error(rejection.Status, rejection.Code);
            var operation = input["operationId"]!.GetValue<string>();
            var actor = Caller["actorId"]!.GetValue<string>();
            var fingerprint = input.ToJsonString();
            JsonObject receipt;
            if (Receipts.TryGetValue(operation, out var saved))
            {
                if (saved.Body != fingerprint || saved.Actor != actor)
                    return Error(HttpStatusCode.Conflict, "model_provider_draft_conflict");
                receipt = saved.Receipt;
            }
            else
            {
                receipt = MakeReceipt(input);
                Receipts.Add(operation, (fingerprint, actor, receipt));
            }
            var createResponse = Json(receipt);
            return AfterCreate?.Invoke(PostBodies.Count, input, createResponse) ?? createResponse;
        }
        var body = path switch
        {
            "/openapi/v1.json" => Document,
            "/api/v1/authorization/context" => Caller,
            "/api/v1/authorization/target-context" => Target,
            _ when path.StartsWith("/api/v1/platform/byok-providers/", StringComparison.Ordinal) => Provider,
            _ => null,
        };
        var target = path == "/api/v1/authorization/target-context";
        var response = new HttpResponseMessage(body is null ? HttpStatusCode.NotFound : target ? TargetStatus : HttpStatusCode.OK)
        {
            Content = new StringContent(target && RawTarget is not null ? RawTarget : body?.ToJsonString() ?? "{}",
                Encoding.UTF8, target ? TargetMediaType : "application/json"),
        };
        if (target && NoStore)
            response.Headers.TryAddWithoutValidation("Cache-Control", "no-store");
        return response;
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string code) =>
        new(status) { Content = new StringContent(new JsonObject { ["code"] = code }.ToJsonString(),
            Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Json(JsonObject value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };

    private static JsonObject MakeReceipt(JsonObject request) => new()
    {
        ["id"] = "byok-" + Guid.Parse(request["operationId"]!.GetValue<string>()).ToString("N"),
        ["name"] = request["name"]!.DeepClone(), ["type"] = request["type"]!.DeepClone(),
        ["base_url"] = request["base_url"]!.DeepClone(), ["model"] = request["model"]!.DeepClone(),
        ["wire_api"] = request["wire_api"]!.DeepClone(), ["azure_api_version"] = request["azure_api_version"]?.DeepClone(),
        ["reasoning_efforts"] = new JsonArray(request["reasoning_efforts"]!.AsArray()
            .Select(item => item!.GetValue<string>()).Order(StringComparer.Ordinal)
            .Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
        ["prompt_capacity_tokens"] = request["prompt_capacity_tokens"]?.DeepClone(),
        ["scope"] = "platform", ["projectId"] = null, ["state"] = "Draft", ["revision"] = 1,
        ["configurationHash"] = new string('a', 64), ["has_api_key"] = false, ["is_active"] = false,
        ["hasCredential"] = false, ["reference"] = request["reference"]!.DeepClone(),
        ["rate_card_id"] = null, ["rate_card_version"] = null,
    };

    public Task<string> GetBearerAsync(CliIdentity identity, InteractionPolicy interaction, CancellationToken ct)
    {
        Assert.Equal(InteractionPolicy.Never, interaction);
        Assert.Null(identity.ProjectId);
        Assert.Null(identity.RunId);
        Assert.Contains("api.read", identity.Scopes);
        Assert.Contains("projects.admin", identity.Scopes);
        return Task.FromResult($"safe-bearer-{++BearerCalls}");
    }
    public Task<CliAuthStatus> LoginAsync(CliIdentity identity, CancellationToken ct) => throw new NotSupportedException();
    public Task<CliAuthStatus> StatusAsync(CliIdentity identity, CancellationToken ct) => throw new NotSupportedException();
    public Task LogoutAsync(CliIdentity identity, CancellationToken ct) => throw new NotSupportedException();

    private static JsonObject Context(bool includeInstallation)
    {
        var result = new JsonObject
        {
            ["contractVersion"] = 1,
            ["issuer"] = "https://issuer.example/",
            ["actorId"] = "actor-1",
            ["tenantId"] = "tenant-1",
            ["membershipRevision"] = 1,
            ["boundProjectId"] = null,
            ["boundRunId"] = null,
            ["effectiveAuthority"] = new JsonArray(),
        };
        if (includeInstallation) result["installationId"] = "00112233-4455-4677-8899-aabbccddeeff";
        return result;
    }

    private static JsonObject MakeDocument()
    {
        var openApi = typeof(GatewayEndpoints).Assembly.GetType("Agentweaver.Gateway.GatewayOpenApi")!;
        var emitted = openApi.GetMethod("CreateDocument", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, null);
        var document = JsonSerializer.SerializeToNode(emitted, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        document["components"]!["schemas"]!["ImportTargetContext"] = JsonNode.Parse("""
            {"type":"object","additionalProperties":false,
             "required":["contractVersion","issuer","actorId","tenantId","membershipRevision","boundProjectId","boundRunId","effectiveAuthority","installationId"],
             "properties":{
               "contractVersion":{"type":"integer","const":1},
               "issuer":{"type":"string"},"actorId":{"type":"string"},"tenantId":{"type":"string"},
               "membershipRevision":{"type":"integer","minimum":1},
               "boundProjectId":{"anyOf":[{"type":"string"},{"type":"null"}]},
               "boundRunId":{"anyOf":[{"type":"string"},{"type":"null"}]},
               "installationId":{"type":"string","format":"uuid"},
               "effectiveAuthority":{"type":"array","items":{"$ref":"#/components/schemas/ImportTargetGrant"}}
             }}
            """);
        document["components"]!["schemas"]!["ImportTargetGrant"] = JsonNode.Parse("""
            {"type":"object","additionalProperties":false,"required":["resourceType","resourceId","permissions"],
             "properties":{"resourceType":{"type":"string","enum":["platform","tenant","project"]},
             "resourceId":{"type":"string"},
             "permissions":{"type":"array","items":{"$ref":"#/components/schemas/ImportTargetPermission"}}}}
            """);
        document["components"]!["schemas"]!["ImportTargetPermission"] = JsonNode.Parse("""
            {"type":"object","additionalProperties":false,"required":["permission","roleRevision"],
             "properties":{"permission":{"type":"string","enum":["readProjects","writeProjects","createProjects","readRunSelection","acceptRunSelection","cancelRun","accessPrivateKnowledge","readPlatformRuntimeDefaults","writePlatformRuntimeDefaults"]},
             "roleRevision":{"type":"integer","minimum":1}}}
            """);
        document["paths"]!["/api/v1/authorization/target-context"] = JsonNode.Parse("""
            {"get":{"operationId":"getAuthorizationTargetContext","parameters":[],
             "x-agentweaver-max-response-bytes":65536,
             "responses":{"200":{"description":"context","headers":{"Cache-Control":{"schema":{"type":"string"}}},
             "content":{"application/json":{"schema":{"$ref":"#/components/schemas/ImportTargetContext"}}}}}}}
            """);
        return document;
    }
}
