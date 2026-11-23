using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Agentweaver.Cli.Api;

internal sealed record ImportTargetContext(string Issuer, string TenantId, string ActorId, Guid InstallationId);
internal sealed class ImportTargetMismatchException : Exception;

internal static class ByokImportContract
{
    private static readonly string[] TargetFields =
    [
        "contractVersion", "issuer", "actorId", "tenantId", "membershipRevision",
        "boundProjectId", "boundRunId", "effectiveAuthority", "installationId",
    ];
    private static readonly string[] ReceiptFields =
    [
        "id", "name", "type", "base_url", "model", "wire_api", "azure_api_version",
        "reasoning_efforts", "prompt_capacity_tokens", "scope", "projectId", "state",
        "revision", "configurationHash", "has_api_key", "is_active", "hasCredential",
        "reference", "rate_card_id", "rate_card_version",
    ];
    private static readonly string[] RequestFields =
    [
        "operationId", "expectedRevision", "targetInstallationId", "reference", "name", "type",
        "base_url", "model", "wire_api", "azure_api_version", "reasoning_efforts", "prompt_capacity_tokens",
        "rate_card_id", "rate_card_version",
    ];
    private static readonly HashSet<string> UnderstoodKeywords = new(StringComparer.Ordinal)
    {
        "type", "anyOf", "enum", "const", "properties", "required", "additionalProperties",
        "dependentRequired", "items", "minItems", "maxItems", "minLength", "maxLength",
        "pattern", "format", "minimum", "maximum", "description", "title",
    };

    internal static void RequireTarget(JsonObject document)
    {
        var operations = GatewayContract.Parse(document);
        var target = Operation(operations, "getAuthorizationTargetContext", "GET",
            "/api/v1/authorization/target-context", false, 64 * 1024);
        if (!RequiresBearer(target.Document["security"] ?? document["security"])) throw Invalid();
        var contextSchema = Resolve(document, ResponseSchema(target));
        RequireShape(contextSchema, TargetFields, TargetFields);
        var targetProperties = (JsonObject)contextSchema["properties"]!;
        foreach (var name in new[] { "issuer", "actorId", "tenantId", "installationId" })
            if (!Allows(document, targetProperties[name], JsonValue.Create(name == "installationId"
                    ? "00112233-4455-4677-8899-aabbccddeeff" : "value"))) throw Invalid();
        if (!Allows(document, targetProperties["contractVersion"], JsonValue.Create(1)) ||
            !Allows(document, targetProperties["membershipRevision"], JsonValue.Create(1)) ||
            !Allows(document, targetProperties["boundProjectId"], null) ||
            !Allows(document, targetProperties["boundRunId"], null) ||
            !Allows(document, targetProperties["effectiveAuthority"], new JsonArray()))
            throw Invalid();

        var authority = Resolve(document, targetProperties["effectiveAuthority"]);
        var grant = Resolve(document, authority["items"]);
        RequireShape(grant, ["resourceType", "resourceId", "permissions"], ["resourceType", "resourceId", "permissions"]);
        var grantProperties = (JsonObject)grant["properties"]!;
        foreach (var type in new[] { "platform", "tenant", "project" })
            if (!Allows(document, grantProperties["resourceType"], JsonValue.Create(type))) throw Invalid();
        var permissions = Resolve(document, grantProperties["permissions"]);
        var permission = Resolve(document, permissions["items"]);
        RequireShape(permission, ["permission", "roleRevision"], ["permission", "roleRevision"]);
        var permissionProperties = (JsonObject)permission["properties"]!;
        foreach (var name in new[]
        {
            "readProjects", "writeProjects", "createProjects", "readRunSelection", "acceptRunSelection",
            "cancelRun", "accessPrivateKnowledge", "readPlatformRuntimeDefaults", "writePlatformRuntimeDefaults",
        })
            if (!Allows(document, permissionProperties["permission"], JsonValue.Create(name))) throw Invalid();
    }

    internal static void Require(JsonObject document, IReadOnlyList<(ImportBinding Binding, Guid Id, JsonObject Body)> prepared)
    {
        RequireTarget(document);
        var operations = GatewayContract.Parse(document);
        var create = Operation(operations, "createPlatformByokProviderDraft", "POST",
            "/api/v1/platform/byok-providers", true, 4 * 1024 * 1024);
        if (create.MaximumRequestBytes > 32 * 1024 ||
            !RequiresBearer(create.Document["security"] ?? document["security"])) throw Invalid();
        var requestSchema = Resolve(document,
            create.Document["requestBody"]?["content"]?["application/json"]?["schema"]);
        RequireShape(requestSchema, RequestFields,
            ["operationId", "expectedRevision", "name", "type", "base_url", "model"], exactRequired: false);
        RequirePair(requestSchema, "rate_card_id", "rate_card_version");
        RequirePair(requestSchema, "reference", "targetInstallationId");
        var requestProperties = (JsonObject)requestSchema["properties"]!;
        if (!ReceiptTypes(document, requestProperties["rate_card_id"], "string", true) ||
            !ReceiptTypes(document, requestProperties["rate_card_version"], "string", true))
            throw Invalid();
        foreach (var item in prepared)
            if (!Allows(document, requestSchema, item.Body)) throw Invalid();
        var receiptSchema = Resolve(document, ResponseSchema(create));
        var requiredReceipt = ReceiptFields.Except(["reference", "rate_card_id", "rate_card_version"]).ToArray();
        RequireShape(receiptSchema, ReceiptFields, requiredReceipt, exactRequired: false);
        var properties = (JsonObject)receiptSchema["properties"]!;
        foreach (var field in ReceiptFields)
        {
            var type = field switch
            {
                "revision" or "prompt_capacity_tokens" => "integer",
                "has_api_key" or "is_active" or "hasCredential" => "boolean",
                "reasoning_efforts" => "array",
                _ => "string",
            };
            var nullable = field is "wire_api" or "azure_api_version" or "prompt_capacity_tokens" or
                "projectId" or "rate_card_id" or "rate_card_version";
            if (!ReceiptTypes(document, properties[field], type, nullable)) throw Invalid();
        }
        if (!KnownHashPatterns(document, properties["configurationHash"]) ||
            !AllowsEveryHash(document, properties["configurationHash"])) throw Invalid();
        foreach (var (binding, id, body) in prepared)
        {
            var receipt = new JsonObject
            {
                ["id"] = "byok-" + id.ToString("N"),
                ["name"] = body["name"]!.DeepClone(), ["type"] = binding.Type,
                ["base_url"] = binding.BaseUrl, ["model"] = binding.Model, ["wire_api"] = binding.WireApi,
                ["azure_api_version"] = binding.AzureApiVersion,
                ["reasoning_efforts"] = new JsonArray(binding.Efforts.Order(StringComparer.Ordinal)
                    .Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()),
                ["prompt_capacity_tokens"] = binding.Capacity,
                ["scope"] = "platform", ["projectId"] = null, ["state"] = "Draft", ["revision"] = 1,
                ["configurationHash"] = new string('a', 64), ["has_api_key"] = false,
                ["is_active"] = false, ["hasCredential"] = false, ["reference"] = binding.Reference,
                ["rate_card_id"] = null, ["rate_card_version"] = null,
            };
            if (!Allows(document, receiptSchema, receipt)) throw Invalid();
        }
    }

    private static GatewayOperation Operation(
        IReadOnlyDictionary<string, GatewayOperation> operations, string id, string method, string path,
        bool body, int maxResponse)
    {
        if (!operations.TryGetValue(id, out var operation) || operation.Method != method || operation.Path != path ||
            operation.HasRequestBody != body || operation.BodyRequired != body ||
            operation.MaximumResponseBytes > maxResponse ||
            !operation.ResponseMediaTypes.Contains("application/json", StringComparer.Ordinal) ||
            !operation.ResponseHeaders.Contains("Cache-Control", StringComparer.OrdinalIgnoreCase) ||
            operation.Document["responses"] is not JsonObject responses ||
            responses["200"]?["headers"] is not JsonObject headers ||
            !headers.Any(h => h.Key.Equals("Cache-Control", StringComparison.OrdinalIgnoreCase)) ||
            responses.Any(r => r.Key.Length == 3 && r.Key[0] == '2' && r.Key != "200") ||
            operation.Parameters.Any(p => p.Location == "header" &&
                p.Name.Equals("X-Agentweaver-Project-Authorization", StringComparison.OrdinalIgnoreCase)) ||
            operation.Parameters.Any(p => p.Required &&
                !(p.Location == "header" && p.Name.Equals("X-Agentweaver-Tenant", StringComparison.OrdinalIgnoreCase))))
            throw Invalid();
        return operation;
    }

    private static JsonNode? ResponseSchema(GatewayOperation operation) =>
        operation.Document["responses"]?["200"]?["content"]?["application/json"]?["schema"];

    private static bool RequiresBearer(JsonNode? security) =>
        security is JsonArray { Count: 1 } alternatives &&
        alternatives[0] is JsonObject { Count: 1 } requirement &&
        requirement.First() is var scheme &&
        scheme.Key.Equals("Bearer", StringComparison.OrdinalIgnoreCase) &&
        scheme.Value is JsonArray { Count: 0 };

    private static JsonObject Resolve(JsonObject document, JsonNode? node)
    {
        for (var depth = 0; depth < 8; depth++)
        {
            if (node is not JsonObject schema) throw Invalid();
            if (schema["$ref"] is not JsonValue reference) return schema;
            if (schema.Count != 1) throw Invalid();
            if (!reference.TryGetValue<string>(out var value) ||
                !value.StartsWith("#/components/schemas/", StringComparison.Ordinal)) throw Invalid();
            node = document["components"]?["schemas"]?[value["#/components/schemas/".Length..]];
        }
        throw Invalid();
    }

    private static void RequireShape(JsonObject schema, string[] fields, string[] required, bool exactRequired = true)
    {
        if (schema["type"]?.GetValue<string>() != "object" ||
            schema["additionalProperties"]?.GetValue<bool>() != false ||
            schema["properties"] is not JsonObject properties ||
            properties.Count != fields.Length || fields.Any(name => !properties.ContainsKey(name)) ||
            schema["required"] is not JsonArray requiredArray)
            throw Invalid();
        var actual = requiredArray.Select(item => item?.GetValue<string>()).ToArray();
        if (actual.Any(item => item is null || !fields.Contains(item, StringComparer.Ordinal)) ||
            actual.Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            required.Any(name => !actual.Contains(name, StringComparer.Ordinal)) ||
            exactRequired && actual.Length != required.Length)
            throw Invalid();
    }

    private static void RequirePair(JsonObject schema, string field, string requiredField)
    {
        if (schema["dependentRequired"] is not JsonObject dependencies ||
            dependencies[field] is not JsonArray fieldRequirements ||
            fieldRequirements.Count != 1 ||
            fieldRequirements[0] is not JsonValue fieldValue ||
            !fieldValue.TryGetValue<string>(out var fieldName) ||
            fieldName != requiredField ||
            dependencies[requiredField] is not JsonArray reverseRequirements ||
            reverseRequirements.Count != 1 ||
            reverseRequirements[0] is not JsonValue reverseValue ||
            !reverseValue.TryGetValue<string>(out var reverseName) ||
            reverseName != field)
            throw Invalid();
    }

    private static bool ReceiptTypes(JsonObject document, JsonNode? node, string expected, bool nullable, int depth = 0)
    {
        if (depth >= 32) return false;
        var schema = Resolve(document, node);
        if (schema.ContainsKey("anyOf"))
            return schema.All(item => item.Key is "anyOf" or "description" or "title") &&
                schema["anyOf"] is JsonArray { Count: > 0 } alternatives &&
                alternatives.All(item => ReceiptTypes(document, item, expected, nullable, depth + 1));

        var type = GatewayContract.String(schema, "type");
        if (type != expected && !(nullable && type == "null")) return false;
        if (schema.Any(item => item.Key switch
        {
            "type" or "description" or "title" or "enum" or "const" => false,
            "minLength" or "maxLength" or "pattern" => type != "string",
            "format" => type is not ("string" or "integer"),
            "minimum" or "maximum" => type != "integer",
            "items" or "minItems" or "maxItems" => type != "array",
            _ => true,
        })) return false;
        if (schema.ContainsKey("enum") && schema["enum"] is not JsonArray { Count: > 0 }) return false;
        foreach (var key in new[] { "minLength", "maxLength", "minItems", "maxItems" })
            if (schema.ContainsKey(key) &&
                (schema[key] is not JsonValue value || !value.TryGetValue<int>(out var number) || number < 0))
                return false;
        foreach (var key in new[] { "minimum", "maximum" })
            if (schema.ContainsKey(key) &&
                (schema[key] is not JsonValue value || !value.TryGetValue<long>(out _)))
                return false;
        if (schema.ContainsKey("pattern") && GatewayContract.String(schema, "pattern") is null) return false;
        if (schema.ContainsKey("format") &&
            (type == "string" ? GatewayContract.String(schema, "format") != "uuid" :
                GatewayContract.String(schema, "format") is not ("int32" or "int64"))) return false;
        return type != "array" || ReceiptTypes(document, schema["items"], "string", false, depth + 1);
    }

    private static bool AllowsEveryHash(JsonObject document, JsonNode? node, int depth = 0)
    {
        if (depth >= 32) return false;
        var schema = Resolve(document, node);
        if (schema["anyOf"] is JsonArray alternatives)
            return alternatives.Any(item => AllowsEveryHash(document, item, depth + 1));
        if (GatewayContract.String(schema, "type") != "string" ||
            schema.Any(item => item.Key is not ("type" or "description" or "title" or "minLength" or "maxLength" or "pattern")))
            return false;
        if (schema.ContainsKey("minLength") &&
            (schema["minLength"] is not JsonValue min || !min.TryGetValue<int>(out var minimum) || minimum < 0 || minimum > 64))
            return false;
        if (schema.ContainsKey("maxLength") &&
            (schema["maxLength"] is not JsonValue max || !max.TryGetValue<int>(out var maximum) || maximum < 64))
            return false;
        return true;
    }

    private static bool KnownHashPatterns(JsonObject document, JsonNode? node, int depth = 0)
    {
        if (depth >= 32) return false;
        var schema = Resolve(document, node);
        if (schema["anyOf"] is JsonArray alternatives)
            return alternatives.All(item => KnownHashPatterns(document, item, depth + 1));
        // shortcut: only these proven patterns are supported; add others when a compatible schema needs them.
        return !schema.ContainsKey("pattern") || GatewayContract.String(schema, "pattern") is
            "[0-9a-f]{64}" or "^[0-9a-f]{64}$" or "^[0-9a-f]{64}(?![\\s\\S])" or
            "[a-f0-9]{64}" or "^[a-f0-9]{64}$" or "^[a-f0-9]{64}(?![\\s\\S])";
    }

    private static bool Allows(JsonObject document, JsonNode? schemaNode, JsonNode? value)
    {
        var schema = Resolve(document, schemaNode);
        if (schema.Any(item => !UnderstoodKeywords.Contains(item.Key))) return false;
        if (schema["anyOf"] is JsonArray alternatives)
            return schema.All(item => item.Key is "anyOf" or "description" or "title") &&
                alternatives.Any(option => Allows(document, option, value));
        var type = GatewayContract.String(schema, "type");
        if (schema["enum"] is JsonArray values && !values.Any(item => JsonNode.DeepEquals(item, value))) return false;
        if (schema.ContainsKey("const") && !JsonNode.DeepEquals(schema["const"], value)) return false;
        if (value is null) return type == "null";
        if (value is JsonObject obj)
        {
            if (type != "object" || schema["properties"] is not JsonObject properties) return false;
            if (schema["required"] is JsonArray required &&
                required.Any(name => name is not JsonValue v || !v.TryGetValue<string>(out var key) || !obj.ContainsKey(key)))
                return false;
            foreach (var (key, child) in obj)
            {
                if (!properties.ContainsKey(key))
                {
                    if (schema["additionalProperties"]?.GetValue<bool>() == false) return false;
                    continue;
                }
                if (!Allows(document, properties[key], child)) return false;
            }
            if (schema["dependentRequired"] is JsonObject dependencies)
                foreach (var (key, dependent) in dependencies)
                    if (obj.ContainsKey(key) && dependent is JsonArray names &&
                        names.Any(name => name is not JsonValue item || !item.TryGetValue<string>(out var text) || !obj.ContainsKey(text)))
                        return false;
            return true;
        }
        if (value is JsonArray array)
        {
            if (type != "array" || schema["items"] is null) return false;
            if (schema["minItems"] is JsonValue min && min.TryGetValue<int>(out var minimum) && array.Count < minimum ||
                schema["maxItems"] is JsonValue max && max.TryGetValue<int>(out var maximum) && array.Count > maximum)
                return false;
            return array.All(item => Allows(document, schema["items"], item));
        }
        if (value is not JsonValue scalar) return false;
        if (scalar.TryGetValue<string>(out var text))
        {
            if (type != "string") return false;
            if (schema["minLength"] is JsonValue min && min.TryGetValue<int>(out var minimum) && text.Length < minimum ||
                schema["maxLength"] is JsonValue max && max.TryGetValue<int>(out var maximum) && text.Length > maximum)
                return false;
            if (schema["pattern"] is JsonValue pattern && pattern.TryGetValue<string>(out var expression) &&
                !Regex.IsMatch(text, expression, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                return false;
            return GatewayContract.String(schema, "format") switch
            {
                null => true,
                "uuid" => Guid.TryParseExact(text, "D", out _),
                _ => false,
            };
        }
        if (scalar.TryGetValue<bool>(out _)) return type == "boolean";
        if (!long.TryParse(scalar.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ||
            type != "integer" || GatewayContract.String(schema, "format") is not (null or "int32" or "int64")) return false;
        if (schema["minimum"] is JsonValue lower && lower.TryGetValue<long>(out var minNumber) && number < minNumber ||
            schema["maximum"] is JsonValue upper && upper.TryGetValue<long>(out var maxNumber) && number > maxNumber)
            return false;
        return true;
    }

    internal static InvalidDataException Invalid() =>
        new("Provider Draft import is unavailable. The Gateway must expose the authorized target-context and provider-create operations.");

    internal static JsonObject? Receipt(JsonNode? body, ImportBinding binding, Guid operationId)
    {
        try
        {
            if (body is not JsonObject receipt || receipt.Count != ReceiptFields.Length ||
                ReceiptFields.Any(name => !receipt.ContainsKey(name)) ||
                GatewayContract.String(receipt, "id") != "byok-" + operationId.ToString("N") ||
                GatewayContract.String(receipt, "reference") != binding.Reference ||
                GatewayContract.String(receipt, "scope") != "platform" || receipt["projectId"] is not null ||
                GatewayContract.String(receipt, "state") != "Draft" ||
                receipt["revision"] is not JsonValue revision || !revision.TryGetValue<long>(out var number) || number != 1 ||
                !False(receipt["is_active"]) || !False(receipt["has_api_key"]) || !False(receipt["hasCredential"]) ||
                receipt["rate_card_id"] is not null || receipt["rate_card_version"] is not null ||
                GatewayContract.String(receipt, "name") != binding.Reference[..Math.Min(binding.Reference.Length, 128)] ||
                GatewayContract.String(receipt, "type") != binding.Type ||
                GatewayContract.String(receipt, "base_url") != binding.BaseUrl ||
                GatewayContract.String(receipt, "model") != binding.Model ||
                GatewayContract.String(receipt, "wire_api") != binding.WireApi ||
                GatewayContract.String(receipt, "azure_api_version") != binding.AzureApiVersion ||
                !Capacity(receipt["prompt_capacity_tokens"], binding.Capacity) ||
                receipt["reasoning_efforts"] is not JsonArray efforts ||
                !efforts.Select(e => e?.GetValue<string>()).SequenceEqual(binding.Efforts.Order(StringComparer.Ordinal)) ||
                GatewayContract.String(receipt, "configurationHash") is not { Length: 64 } hash ||
                hash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                return null;
            return new JsonObject
            {
                ["reference"] = binding.Reference,
                ["providerId"] = "byok-" + operationId.ToString("N"),
                ["revision"] = 1,
                ["operationId"] = operationId.ToString("D"),
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    private static bool False(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<bool>(out var result) && !result;

    private static bool Capacity(JsonNode? value, int? expected) =>
        expected is null ? value is null :
        value is JsonValue scalar && scalar.TryGetValue<int>(out var number) && number == expected;
}
