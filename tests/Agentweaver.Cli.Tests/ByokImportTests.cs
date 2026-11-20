using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Agentweaver.Abstractions;
using Agentweaver.Cli.Api;

namespace Agentweaver.Cli.Tests;

public sealed class ByokImportTests : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, $"import-{Guid.NewGuid():N}");

    public ByokImportTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static JsonObject Overlay() => JsonNode.Parse("""
        {
          "kind":"agentweaver.model-bindings-overlay",
          "schemaVersion":1,
          "baseRevision":"product-v1",
          "baseModelBindings":{"hosted":{"ModelId":"hosted:1","SourceMode":"HostedCopilot"}},
          "revision":"custom-v1",
          "modelBindings":{
            "Zulu":{"ModelId":"model-1","SourceMode":"Byok","Provider":{"Type":"openai","BaseUrl":"https://api.example.com/v1","WireApi":"responses"},"SupportedReasoningEfforts":["high","low"],"PromptCapacityTokens":1000},
            "Alpha":{"ModelId":"model-1","SourceMode":"Byok","Provider":{"Type":"azure","BaseUrl":"https://model.example.com/","WireApi":"completions","AzureApiVersion":"2025-01-01"},"SupportedReasoningEfforts":["medium"]}
          },
          "rateCard":{"id":"price-card","version":"2026-01-01"},
          "modelRates":{"model-1":{"input":"2.5","cachedInput":"0.25","output":"15"}}
        }
        """)!.AsObject();

    private static byte[] Bytes(JsonObject value) => Encoding.UTF8.GetBytes(value.ToJsonString());

    [Fact]
    public void OverlayBuildsSortedBodiesWithoutKeysOrPrices()
    {
        var rows = ByokImportOverlay.Parse(Bytes(Overlay()));
        Assert.Equal(["Alpha", "Zulu"], rows.Select(x => x.Reference));
        var body = rows[1].Body(Guid.Parse("2cdff177-aea3-5681-be3e-3e9ede44d1b4"),
            Guid.Parse("00112233-4455-4677-8899-aabbccddeeff"));
        Assert.Equal("Zulu", body["reference"]!.GetValue<string>());
        Assert.Equal("model-1", body["model"]!.GetValue<string>());
        Assert.Equal(["high", "low"], body["reasoning_efforts"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(1000, body["prompt_capacity_tokens"]!.GetValue<int>());
        Assert.False(body.ContainsKey("rate_card_id"));
        Assert.DoesNotContain("2.5", body.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Alpha", "2cdff177-aea3-5681-be3e-3e9ede44d1b4")]
    [InlineData("alpha", "a595353c-1cf5-57b9-ab9d-8638e5dd39b5")]
    public void OperationIdMatchesFrozenVectors(string reference, string expected)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("overlay-test-bytes"));
        Assert.Equal(expected, ByokImportOverlay.OperationId("https://issuer.example/", "tenant-1",
            Guid.Parse("00112233-4455-4677-8899-aabbccddeeff"), digest, reference).ToString("D"));
    }

    [Fact]
    public void OperationIdSeparatesInstallationIssuerTenantAndInput()
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("overlay-test-bytes"));
        var installation = Guid.Parse("00112233-4455-4677-8899-aabbccddeeff");
        Guid Id(string issuer, string tenant, Guid target, byte[] hash) =>
            ByokImportOverlay.OperationId(issuer, tenant, target, hash, "Alpha");
        var original = Id("https://issuer.example", "tenant-1", installation, digest);
        Assert.Equal("88302088-dc13-505f-9451-16474da2e71e",
            Id("https://issuer.example", "tenant-1", Guid.Parse("00112233-4455-4677-8899-aabbccddee00"), digest).ToString());
        Assert.Equal("3764e336-d62b-56cb-9f7b-d47bd8f57a90", Id("https://issuer.example", "tenant-2", installation, digest).ToString());
        Assert.Equal("396df090-e015-5b45-8f45-7091f00335bc", Id("https://other.example", "tenant-1", installation, digest).ToString());
        Assert.NotEqual(original, Id("https://issuer.example", "tenant-1", installation, SHA256.HashData([1])));
        Assert.NotEqual(
            ByokImportOverlay.OperationId("https://issuer.example/a", "bc", installation, digest, "Alpha"),
            ByokImportOverlay.OperationId("https://issuer.example/ab", "c", installation, digest, "Alpha"));
        Assert.Throws<InvalidDataException>(() =>
            ByokImportOverlay.OperationId("https://issuer.example", "tenant-1", installation, [1], "Alpha"));
    }

    [Theory]
    [InlineData("1.0", 1)]
    [InlineData("1e3", 1000)]
    [InlineData("1e0000000000000000000000000000", 1)]
    [InlineData("2147483647", 2147483647)]
    public void CapacityAcceptsExactPositiveIntegers(string raw, int expected)
    {
        var json = Overlay().ToJsonString().Replace("\"PromptCapacityTokens\":1000", "\"PromptCapacityTokens\":" + raw);
        Assert.Equal(expected, ByokImportOverlay.Parse(Encoding.UTF8.GetBytes(json))[1].Capacity);
    }

    [Theory]
    [InlineData("1.00000000000000001")]
    [InlineData("2147483648")]
    [InlineData("1e999999999999999999999")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("null")]
    public void CapacityRejectsInvalidValues(string raw)
    {
        var json = Overlay().ToJsonString().Replace("\"PromptCapacityTokens\":1000", "\"PromptCapacityTokens\":" + raw);
        Assert.Throws<InvalidDataException>(() => ByokImportOverlay.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("1.0", true)]
    [InlineData("1e0", true)]
    [InlineData("1.00000000000000001", false)]
    [InlineData("2", false)]
    public void SchemaVersionUsesExactNumericEquality(string raw, bool accepted)
    {
        var json = Overlay().ToJsonString().Replace("\"schemaVersion\":1", "\"schemaVersion\":" + raw);
        if (accepted) Assert.Equal(2, ByokImportOverlay.Parse(Encoding.UTF8.GetBytes(json)).Count);
        else Assert.Throws<InvalidDataException>(() => ByokImportOverlay.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("input")]
    [InlineData("output")]
    [InlineData("cachedInput")]
    public void RateMatchesJavaScriptFloatingPointBoundaries(string category)
    {
        foreach (var (value, valid) in new[]
        {
            (new string('9', 309), true),
            ("0." + new string('0', 400) + "1", false),
            ("0." + new string('0', 323) + "5", true),
            ("0", false),
        })
        {
            var overlay = Overlay();
            overlay["modelRates"]!["model-1"]![category] = value;
            if (valid) Assert.Equal(2, ByokImportOverlay.Parse(Bytes(overlay)).Count);
            else Assert.Throws<InvalidDataException>(() => ByokImportOverlay.Parse(Bytes(overlay)));
        }
    }

    [Fact]
    public void RejectsCollisionsUnexpectedFieldsAndMissingRates()
    {
        var collision = Overlay();
        collision["baseModelBindings"]!["zULu"] = new JsonObject { ["ModelId"] = "hosted", ["SourceMode"] = "HostedCopilot" };
        Assert.Throws<InvalidDataException>(() => ByokImportOverlay.Parse(Bytes(collision)));
        var secret = Overlay();
        secret["modelBindings"]!["Zulu"]!["Provider"]!["apiKey"] = "secret-canary";
        Assert.Throws<InvalidDataException>(() => ByokImportOverlay.Parse(Bytes(secret)));
        var missing = Overlay();
        missing["modelRates"]!.AsObject().Clear();
        Assert.Throws<InvalidDataException>(() => ByokImportOverlay.Parse(Bytes(missing)));
    }

    [Fact]
    public void RejectsAReferenceEndingInNewline()
    {
        var overlay = Overlay();
        var row = overlay["modelBindings"]!["Zulu"]!.DeepClone();
        overlay["modelBindings"]!.AsObject().Remove("Zulu");
        overlay["modelBindings"]!["Zulu\n"] = row;
        Assert.Throws<InvalidDataException>(() => ByokImportOverlay.Parse(Bytes(overlay)));
    }

    [Fact]
    public void RejectsDuplicateEscapedPropertiesBomAndMalformedUnicode()
    {
        var json = Overlay().ToJsonString();
        var duplicate = json.Replace("\"kind\":", "\"\\u006bind\":\"duplicate\",\"kind\":");
        Assert.Throws<InvalidDataException>(() => ByokImportOverlay.Parse(Encoding.UTF8.GetBytes(duplicate)));
        var nestedDuplicate = json.Replace("\"Type\":\"openai\"", "\"\\u0054ype\":\"openai\",\"Type\":\"openai\"");
        Assert.Throws<InvalidDataException>(() => ByokImportOverlay.Parse(Encoding.UTF8.GetBytes(nestedDuplicate)));
        Assert.Throws<InvalidDataException>(() => ByokImportOverlay.Parse([0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(json)]));
        var invalid = json.Replace("product-v1", "\\ud800");
        Assert.Throws<InvalidDataException>(() => ByokImportOverlay.Parse(Encoding.UTF8.GetBytes(invalid)));
        var invalidUtf8 = Encoding.UTF8.GetBytes(json.Replace("product-v1", "BADUTF8"));
        var marker = Encoding.UTF8.GetBytes("BADUTF8");
        var position = invalidUtf8.AsSpan().IndexOf(marker);
        invalidUtf8[position] = 0xff;
        Assert.Throws<InvalidDataException>(() => ByokImportOverlay.Parse(invalidUtf8));
    }

    [Fact]
    public void RejectsOversizeAndExcessiveDepth()
    {
        Assert.Throws<InvalidDataException>(() => ByokImportOverlay.Parse(new byte[ByokImportOverlay.MaximumFileBytes + 1]));
        var json = new string('[', 34) + "0" + new string(']', 34);
        Assert.Throws<InvalidDataException>(() => ByokImportOverlay.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public async Task OverlayCorpusMatchesDeploymentParser()
    {
        var corpus = new List<string> { Overlay().ToJsonString() };
        void Add(Action<JsonObject> change)
        {
            var value = Overlay();
            change(value);
            corpus.Add(value.ToJsonString());
        }
        Add(x => x["kind"] = "wrong");
        Add(x => x["revision"] = "product-v1");
        Add(x => x["modelRates"]!["model-1"]!["input"] = "0");
        Add(x => x["modelRates"]!["model-1"]!["output"] = new string('9', 309));
        Add(x => x["modelRates"]!["model-1"]!["input"] = "0." + new string('0', 400) + "1");
        Add(x => x["modelRates"]!["model-1"]!["cachedInput"] = "0." + new string('0', 323) + "5");
        Add(x => x["modelRates"]!.AsObject().Clear());
        Add(x => x["modelBindings"]!["Zulu"]!["SupportedReasoningEfforts"] = new JsonArray("low", "low"));
        Add(x => x["modelBindings"]!["Alpha"]!["Provider"]!["secret"] = "canary");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Agentweaver.slnx"))) root = root.Parent!;
        var script = """
            import {pathToFileURL} from 'node:url';
            import {readFileSync} from 'node:fs';
            const {parseModelBindingsOverlay}=await import(pathToFileURL(process.argv[1]));
            const items=JSON.parse(readFileSync(0,'utf8'));
            console.log(JSON.stringify(items.map(item=>{try {parseModelBindingsOverlay(item);return true;} catch{return false;}})));
            """;
        var start = new ProcessStartInfo("node")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("--input-type=module");
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(Path.Combine(root.FullName, "scripts", "release", "model-bindings-overlay.mjs"));
        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(corpus));
        process.StandardInput.Close();
        var actual = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        var js = JsonSerializer.Deserialize<bool[]>(actual)!;
        var native = corpus.Select(item =>
        {
            try { ByokImportOverlay.Parse(Encoding.UTF8.GetBytes(item)); return true; }
            catch (InvalidDataException) { return false; }
        });
        Assert.Equal(js, native);
    }

    [Theory]
    [InlineData("https://api.example.com/v1", "openai", true)]
    [InlineData("https://bücher.example/v1", "openai", true)]
    [InlineData("https://example.com///", "azure", true)]
    [InlineData("https://example.com/path", "azure", false)]
    [InlineData("https://user:pass@example.com/", "openai", false)]
    [InlineData("https://example.com/?key=secret", "openai", false)]
    [InlineData("https://127.0.0.1/", "openai", false)]
    [InlineData("https://example.com:444/", "openai", false)]
    [InlineData("https://vault.azure.net/", "openai", false)]
    public void ImportUsesSharedProviderUrlRule(string url, string type, bool expected)
    {
        Assert.Equal(expected, ModelProviderPublicUrl.IsSafe(url, type));
        var overlay = Overlay();
        var provider = overlay["modelBindings"]!["Zulu"]!["Provider"]!;
        provider["Type"] = type;
        provider["BaseUrl"] = url;
        if (type == "azure") provider["AzureApiVersion"] = "2025-01-01";
        if (expected) Assert.Equal(2, ByokImportOverlay.Parse(Bytes(overlay)).Count);
        else Assert.Throws<InvalidDataException>(() => ByokImportOverlay.Parse(Bytes(overlay)));
    }

    [Fact]
    public async Task ValidImportReturnsExactDraftMappings()
    {
        var bytes = Bytes(Overlay());
        var path = Path.Combine(_directory, "overlay.json");
        await File.WriteAllBytesAsync(path, bytes);
        var context = await WriteContextAsync();
        var digest = Convert.ToHexString(SHA256.HashData(bytes));
        var output = new StringWriter();
        var error = new StringWriter();
        using var handler = new ImportGatewayFixture();
        var result = await CliApplication.RunAsync(
            ["models", "byok", "import", "--model-bindings", path, "--file-sha256", digest, "--expected-context", context,
                "--gateway-url", "https://gateway.example/", "--broker-issuer", "https://issuer.example/",
                "--api-audience", "https://gateway.example/", "--tenant-id", "tenant-1"],
            new CliRuntimeOptions(HttpClient: handler.Client, Authentication: handler, Output: output, Error: error));
        Assert.Equal(0, result);
        var summary = JsonNode.Parse(output.ToString())!;
        Assert.Equal(digest.ToLowerInvariant(), summary["fileSha256"]!.GetValue<string>());
        Assert.Equal(["Alpha", "Zulu"], summary["completed"]!.AsArray().Select(item => item!["reference"]!.GetValue<string>()));
        Assert.Null(summary["stop"]);
        Assert.Equal(2, handler.PostBodies.Count);
        Assert.Equal(["Alpha", "Zulu"], handler.PostBodies.Select(body => body["reference"]!.GetValue<string>()));
        Assert.Equal(3, handler.TargetCalls);
        Assert.Equal("safe-bearer-1", handler.Requests[1].Bearer);
        Assert.Equal(handler.Requests[1].Bearer, handler.Requests[2].Bearer);
        Assert.Equal(3, handler.BearerCalls);
        foreach (var post in handler.Requests.Select((request, index) => (request, index)).Where(x => x.request.Method == "POST"))
            Assert.Equal(post.request.Bearer, handler.Requests[post.index - 1].Bearer);
        Assert.Equal("tenant-1", handler.Requests[2].Tenant);
        Assert.DoesNotContain("https://", output.ToString() + error, StringComparison.Ordinal);
        Assert.DoesNotContain(path, output.ToString() + error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(512)]
    public async Task AdvertisedBodyLimitRejectsLaterRowBeforeAnyPost(int limit)
    {
        var overlay = Overlay();
        overlay["modelBindings"]!["Zulu"]!["Provider"]!["BaseUrl"] = "https://api.example.com/" + new string('a', 300);
        var rows = ByokImportOverlay.Parse(Bytes(overlay));
        var id = Guid.Parse("00112233-4455-4677-8899-aabbccddeeff");
        Assert.True(Encoding.UTF8.GetByteCount(rows[0].Body(id, id).ToJsonString()) <= limit);
        Assert.True(Encoding.UTF8.GetByteCount(rows[1].Body(id, id).ToJsonString()) > limit);
        var path = Path.Combine(_directory, "overlay.json");
        await File.WriteAllBytesAsync(path, Bytes(overlay));
        var context = await WriteContextAsync();
        using var gateway = new ImportGatewayFixture();
        gateway.Document["paths"]!["/api/v1/platform/byok-providers"]!["post"]!["x-agentweaver-max-request-body-bytes"] = limit;
        var result = await RunImportAsync(gateway, path, context);
        Assert.Equal(1, result.Code);
        Assert.Empty(gateway.PostBodies);
        Assert.Empty(gateway.Receipts);
        Assert.Empty(JsonNode.Parse(result.Output)!["completed"]!.AsArray());
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("type")]
    [InlineData("base_url")]
    [InlineData("model")]
    [InlineData("wire_api")]
    [InlineData("azure_api_version")]
    [InlineData("reasoning_efforts")]
    [InlineData("prompt_capacity_tokens")]
    [InlineData("scope")]
    [InlineData("projectId")]
    [InlineData("state")]
    [InlineData("revision")]
    [InlineData("configurationHash")]
    [InlineData("has_api_key")]
    [InlineData("is_active")]
    [InlineData("hasCredential")]
    [InlineData("reference")]
    [InlineData("rate_card_id")]
    [InlineData("rate_card_version")]
    public async Task DiscoveryRejectsIncompatibleReceiptFieldBeforeAnyPost(string field)
    {
        var path = await WriteOverlayAsync();
        var context = await WriteContextAsync();
        using var gateway = new ImportGatewayFixture();
        var properties = gateway.Document["components"]!["schemas"]!["ModelProviderDraft"]!["properties"]!;
        properties[field] = new JsonObject { ["type"] = field is "has_api_key" or "is_active" or "hasCredential" ? "string" : "boolean" };
        var result = await RunImportAsync(gateway, path, context);
        Assert.Equal(1, result.Code);
        Assert.Equal("import_target_unavailable", JsonNode.Parse(result.Output)!["stop"]!["code"]!.GetValue<string>());
        Assert.Empty(gateway.PostBodies);
        Assert.Empty(gateway.Receipts);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("type")]
    [InlineData("base_url")]
    [InlineData("model")]
    [InlineData("wire_api")]
    [InlineData("azure_api_version")]
    [InlineData("reasoning_efforts")]
    [InlineData("prompt_capacity_tokens")]
    [InlineData("scope")]
    [InlineData("projectId")]
    [InlineData("state")]
    [InlineData("revision")]
    [InlineData("configurationHash")]
    [InlineData("has_api_key")]
    [InlineData("is_active")]
    [InlineData("hasCredential")]
    [InlineData("reference")]
    [InlineData("rate_card_id")]
    [InlineData("rate_card_version")]
    public async Task DiscoveryRejectsExtraReceiptTypeAlternativesBeforeAnyPost(string field)
    {
        var path = await WriteOverlayAsync();
        var context = await WriteContextAsync();
        using var gateway = new ImportGatewayFixture();
        var properties = gateway.Document["components"]!["schemas"]!["ModelProviderDraft"]!["properties"]!;
        properties[field] = new JsonObject
        {
            ["anyOf"] = new JsonArray(properties[field]!.DeepClone(),
                new JsonObject { ["type"] = field is "revision" or "has_api_key" or "is_active" or "hasCredential" ? "string" : "boolean" }),
        };
        var result = await RunImportAsync(gateway, path, context);
        Assert.Equal(1, result.Code);
        Assert.Equal("import_target_unavailable", JsonNode.Parse(result.Output)!["stop"]!["code"]!.GetValue<string>());
        Assert.Empty(gateway.PostBodies);
        Assert.Empty(gateway.Receipts);
    }

    [Theory]
    [InlineData("revision-null")]
    [InlineData("revision-type-array")]
    [InlineData("revision-null-type-array")]
    [InlineData("revision-nullable")]
    [InlineData("revision-nested-extra")]
    [InlineData("revision-unknown-branch")]
    [InlineData("revision-unknown-format-branch")]
    [InlineData("effort-item")]
    [InlineData("effort-array")]
    [InlineData("nested-unknown")]
    public async Task DiscoveryRejectsUnsafeReceiptSchemaVariantsBeforeAnyPost(string mutation)
    {
        var path = await WriteOverlayAsync();
        var context = await WriteContextAsync();
        using var gateway = new ImportGatewayFixture();
        var properties = gateway.Document["components"]!["schemas"]!["ModelProviderDraft"]!["properties"]!;
        switch (mutation)
        {
            case "revision-null":
                properties["revision"] = JsonNode.Parse("""{"anyOf":[{"type":"integer"},{"type":"null"}]}""");
                break;
            case "revision-type-array":
                properties["revision"] = JsonNode.Parse("""{"type":["integer","string"]}""");
                break;
            case "revision-null-type-array":
                properties["revision"] = JsonNode.Parse("""{"type":["integer","null"]}""");
                break;
            case "revision-nullable":
                properties["revision"]!["nullable"] = true;
                break;
            case "revision-nested-extra":
                properties["revision"] = JsonNode.Parse(
                    """{"anyOf":[{"type":"integer"},{"anyOf":[{"type":"integer"},{"type":"string"}]}]}""");
                break;
            case "revision-unknown-branch":
                properties["revision"] = JsonNode.Parse(
                    """{"anyOf":[{"type":"integer"},{"type":"integer","not":{"const":2}}]}""");
                break;
            case "revision-unknown-format-branch":
                properties["revision"] = JsonNode.Parse(
                    """{"anyOf":[{"type":"integer"},{"type":"integer","format":"unknown"}]}""");
                break;
            case "effort-item":
                properties["reasoning_efforts"]!["items"] = JsonNode.Parse(
                    """{"anyOf":[{"type":"string"},{"type":"integer"}]}""");
                break;
            case "effort-array":
                properties["reasoning_efforts"] = JsonNode.Parse(
                    """{"anyOf":[{"type":"array","items":{"type":"string"}},{"type":"string"}]}""");
                break;
            case "nested-unknown":
                properties["reasoning_efforts"]!["items"]!["not"] = new JsonObject();
                break;
        }
        var result = await RunImportAsync(gateway, path, context);
        Assert.Equal(1, result.Code);
        Assert.Equal("import_target_unavailable", JsonNode.Parse(result.Output)!["stop"]!["code"]!.GetValue<string>());
        Assert.Empty(gateway.PostBodies);
        Assert.Empty(gateway.Receipts);
    }

    [Theory]
    [InlineData("""{"type":"string","minLength":64,"maxLength":64,"pattern":"^[0-9a-f]{64}$"}""")]
    [InlineData("""{"type":"string","minLength":64,"maxLength":64,"pattern":"^[a-f0-9]{64}$"}""")]
    [InlineData("""{"type":"string","minLength":0,"maxLength":100,"pattern":"[0-9a-f]{64}"}""")]
    [InlineData("""{"type":"string","minLength":0,"maxLength":100,"pattern":"[a-f0-9]{64}"}""")]
    [InlineData("""{"type":"string","pattern":"^[0-9a-f]{64}(?![\\s\\S])"}""")]
    [InlineData("""{"type":"string","pattern":"^[a-f0-9]{64}(?![\\s\\S])"}""")]
    [InlineData("""{"anyOf":[{"type":"string","const":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},{"type":"string","minLength":64,"maxLength":64}]}""")]
    public async Task DiscoveryAcceptsHashesWithCompatibleConstraints(string schema)
    {
        var path = await WriteOverlayAsync();
        var context = await WriteContextAsync();
        using var gateway = new ImportGatewayFixture();
        gateway.Document["components"]!["schemas"]!["ModelProviderDraft"]!["properties"]!["configurationHash"] = JsonNode.Parse(schema);
        gateway.AfterCreate = (_, _, response) =>
        {
            var receipt = JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!;
            receipt["configurationHash"] = string.Concat(Enumerable.Repeat("0123456789abcdef", 4));
            response.Content = new StringContent(receipt.ToJsonString(), Encoding.UTF8, "application/json");
            return response;
        };
        var result = await RunImportAsync(gateway, path, context);
        Assert.Equal(0, result.Code);
        Assert.Null(JsonNode.Parse(result.Output)!["stop"]);
        Assert.Equal(2, JsonNode.Parse(result.Output)!["completed"]!.AsArray().Count);
        Assert.Equal(2, gateway.PostBodies.Count);
    }

    [Theory]
    [InlineData("""{"type":"string","minLength":65}""")]
    [InlineData("""{"type":"string","maxLength":63}""")]
    [InlineData("""{"type":"string","pattern":"^a{64}$"}""")]
    [InlineData("""{"type":"string","pattern":"^[0-9a-f]{63}$"}""")]
    [InlineData("""{"type":"string","pattern":"^[a-e0-9]{64}$"}""")]
    [InlineData("""{"type":"string","pattern":"^[a-f0-8]{64}$"}""")]
    [InlineData("""{"type":"string","pattern":"^[a-f0-9]{64"}""")]
    [InlineData("""{"type":"string","pattern":"^(?:[a-f0-9]){64}$"}""")]
    [InlineData("""{"type":"string","minLength":65,"pattern":"^[a-f0-9]{64}$"}""")]
    [InlineData("""{"type":"string","maxLength":63,"pattern":"^[a-f0-9]{64}$"}""")]
    [InlineData("""{"anyOf":[{"type":"string"},{"type":"string","pattern":"^a{64}$"}]}""")]
    [InlineData("""{"anyOf":[{"type":"string","pattern":"^[a-f0-9]{64}$"},{"type":"string","pattern":"^[a-e0-9]{64}$"}]}""")]
    [InlineData("""{"type":"string","not":{"const":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}}""")]
    [InlineData("""{"type":"string","minLength":"64"}""")]
    public async Task DiscoveryRejectsRestrictiveOrUnsupportedHashBeforeAnyPost(string schema)
    {
        var path = await WriteOverlayAsync();
        var context = await WriteContextAsync();
        using var gateway = new ImportGatewayFixture();
        gateway.Document["components"]!["schemas"]!["ModelProviderDraft"]!["properties"]!["configurationHash"] = JsonNode.Parse(schema);
        var result = await RunImportAsync(gateway, path, context);
        Assert.Equal(1, result.Code);
        Assert.Equal("import_target_unavailable", JsonNode.Parse(result.Output)!["stop"]!["code"]!.GetValue<string>());
        Assert.Empty(gateway.PostBodies);
        Assert.Empty(gateway.Receipts);
    }

    [Theory]
    [InlineData("projectId")]
    [InlineData("azure_api_version")]
    [InlineData("prompt_capacity_tokens")]
    [InlineData("rate_card_id")]
    [InlineData("rate_card_version")]
    public async Task DiscoveryRejectsNonNullableReceiptFieldBeforeAnyPost(string field)
    {
        var path = await WriteOverlayAsync();
        var context = await WriteContextAsync();
        using var gateway = new ImportGatewayFixture();
        gateway.Document["components"]!["schemas"]!["ModelProviderDraft"]!["properties"]![field] =
            new JsonObject { ["type"] = field == "prompt_capacity_tokens" ? "integer" : "string" };
        var result = await RunImportAsync(gateway, path, context);
        Assert.Equal(1, result.Code);
        Assert.Empty(gateway.PostBodies);
        Assert.Empty(gateway.Receipts);
    }

    [Fact]
    public async Task DiscoveryRejectsRestrictedUnknownReceiptHashBeforeAnyPost()
    {
        var path = await WriteOverlayAsync();
        var context = await WriteContextAsync();
        using var gateway = new ImportGatewayFixture();
        gateway.Document["components"]!["schemas"]!["ModelProviderDraft"]!["properties"]!["configurationHash"] =
            new JsonObject { ["type"] = "string", ["const"] = new string('a', 64) };
        var result = await RunImportAsync(gateway, path, context);
        Assert.Equal(1, result.Code);
        Assert.Empty(gateway.PostBodies);
        Assert.Empty(gateway.Receipts);
    }

    [Theory]
    [InlineData("missing-200")]
    [InlineData("other-success")]
    [InlineData("missing-cache-header")]
    [InlineData("request-not-required")]
    [InlineData("response-wrong-media")]
    public async Task DiscoveryRejectsIncompatibleCreateSemanticsBeforeAnyPost(string mutation)
    {
        var path = await WriteOverlayAsync();
        var context = await WriteContextAsync();
        using var gateway = new ImportGatewayFixture();
        var operation = gateway.Document["paths"]!["/api/v1/platform/byok-providers"]!["post"]!;
        var responses = operation["responses"]!.AsObject();
        switch (mutation)
        {
            case "missing-200": responses.Remove("200"); break;
            case "other-success": responses["201"] = responses["200"]!.DeepClone(); break;
            case "missing-cache-header": responses["200"]!["headers"]!.AsObject().Remove("Cache-Control"); break;
            case "request-not-required": operation["requestBody"]!["required"] = false; break;
            case "response-wrong-media":
                var content = responses["200"]!["content"]!.AsObject();
                content["text/plain"] = content["application/json"]!.DeepClone();
                content.Remove("application/json");
                break;
        }
        var result = await RunImportAsync(gateway, path, context);
        Assert.Equal(1, result.Code);
        Assert.Equal("import_target_unavailable", JsonNode.Parse(result.Output)!["stop"]!["code"]!.GetValue<string>());
        Assert.Empty(gateway.PostBodies);
        Assert.Empty(gateway.Receipts);
    }

    [Fact]
    public async Task ImportDoesNotRetainRepeatedGatewayEvidence()
    {
        var overlay = Overlay();
        var rows = overlay["modelBindings"]!.AsObject();
        var template = rows["Zulu"]!.DeepClone();
        rows.Clear();
        for (var i = 0; i < 150; i++) rows[$"row-{i:D4}"] = template.DeepClone();
        var path = Path.Combine(_directory, "overlay.json");
        await File.WriteAllBytesAsync(path, Bytes(overlay));
        var context = await WriteContextAsync();
        using var gateway = new ImportGatewayFixture();
        var grants = new JsonArray();
        for (var i = 0; i < 150; i++)
            grants.Add(new JsonObject { ["resourceType"] = "project", ["resourceId"] = new string('x', 100),
                ["permissions"] = new JsonArray(new JsonObject { ["permission"] = "readProjects", ["roleRevision"] = 1 }) });
        gateway.Caller["effectiveAuthority"] = grants;
        gateway.Target["effectiveAuthority"] = grants.DeepClone();
        var factory = new ImportClientFactory(gateway);
        var bytes = await File.ReadAllBytesAsync(path);
        var output = new StringWriter();
        var result = await CliApplication.RunAsync(
            ["models", "byok", "import", "--model-bindings", path, "--file-sha256", Convert.ToHexString(SHA256.HashData(bytes)),
                "--expected-context", context, "--gateway-url", "https://gateway.example/", "--broker-issuer", "https://issuer.example/",
                "--api-audience", "https://gateway.example/", "--tenant-id", "tenant-1"],
            new CliRuntimeOptions(GatewayClientFactory: factory, Output: output, Error: new StringWriter()));
        Assert.Equal(0, result);
        Assert.Equal(150, gateway.PostBodies.Count);
        Assert.Equal(151, gateway.TargetCalls);
        Assert.Equal(150, JsonNode.Parse(output.ToString())!["completed"]!.AsArray().Count);
        Assert.Single(factory.Client!.Calls);
    }

    [Theory]
    [InlineData("missing-operation")]
    [InlineData("missing-pin-schema")]
    [InlineData("old-permission-schema")]
    [InlineData("wrong-method")]
    [InlineData("anonymous-security")]
    [InlineData("issuer")]
    [InlineData("actorId")]
    [InlineData("tenantId")]
    [InlineData("boundProjectId")]
    [InlineData("boundRunId")]
    [InlineData("installationId")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("nested-invalid")]
    [InlineData("wrong-version")]
    [InlineData("non-json")]
    [InlineData("denied")]
    [InlineData("no-store")]
    public async Task DiscoveryAndTargetContextRejectIncompatibleGatewaysBeforePost(string failure)
    {
        var bytes = Bytes(Overlay());
        var path = Path.Combine(_directory, "overlay.json");
        await File.WriteAllBytesAsync(path, bytes);
        var context = await WriteContextAsync();
        using var fixture = new ImportGatewayFixture();
        switch (failure)
        {
            case "missing-operation":
                fixture.Document["paths"]!.AsObject().Remove("/api/v1/authorization/target-context");
                break;
            case "missing-pin-schema":
                fixture.Document["components"]!["schemas"]!["ModelProviderDraftPlatformCreateRequest"]!["properties"]!
                    .AsObject().Remove("targetInstallationId");
                break;
            case "old-permission-schema":
                var permissions = fixture.Document["components"]!["schemas"]!["ImportTargetPermission"]!["properties"]!["permission"]!["enum"]!.AsArray();
                permissions.Remove(permissions.First(item => item!.GetValue<string>() == "cancelRun"));
                break;
            case "wrong-method":
                var route = fixture.Document["paths"]!["/api/v1/authorization/target-context"]!.AsObject();
                route["post"] = route["get"]!.DeepClone();
                route.Remove("get");
                break;
            case "anonymous-security":
                fixture.Document["paths"]!["/api/v1/authorization/target-context"]!["get"]!["security"] =
                    new JsonArray(new JsonObject(), new JsonObject { ["Bearer"] = new JsonArray() });
                break;
            case "no-store":
                fixture.NoStore = false;
                break;
            case "duplicate":
                fixture.RawTarget = fixture.Target.ToJsonString().Replace("\"actorId\":", "\"actorId\":\"secret-canary\",\"actorId\":");
                break;
            case "nested-invalid":
                fixture.Target["effectiveAuthority"] = new JsonArray(new JsonObject
                {
                    ["resourceType"] = "project", ["resourceId"] = "project-1",
                    ["permissions"] = new JsonArray(new JsonObject { ["permission"] = "cancelRun", ["roleRevision"] = -1 }),
                });
                break;
            case "wrong-version":
                fixture.Target["contractVersion"] = 2;
                break;
            case "non-json":
                fixture.TargetMediaType = "text/plain";
                break;
            case "denied":
                fixture.TargetStatus = HttpStatusCode.Forbidden;
                fixture.RawTarget = """{"code":"secret-canary"}""";
                break;
            default:
                fixture.Target[failure] = failure == "installationId" ? "00112233-4455-4677-8899-AABBCCDDEEFF" : "secret-canary";
                break;
        }
        var output = new StringWriter();
        var error = new StringWriter();
        var result = await CliApplication.RunAsync(
            ["models", "byok", "import", "--model-bindings", path, "--expected-context", context,
                "--file-sha256", Convert.ToHexString(SHA256.HashData(bytes)),
                "--gateway-url", "https://gateway.example/", "--broker-issuer", "https://issuer.example/",
                "--api-audience", "https://gateway.example/", "--tenant-id", "tenant-1"],
            new CliRuntimeOptions(HttpClient: fixture.Client, Authentication: fixture, Output: output, Error: error));
        Assert.Equal(1, result);
        var expectedCode = failure is "issuer" or "actorId" or "tenantId" or "boundProjectId" or "boundRunId" or "wrong-version" or "nested-invalid"
            ? "expected_context_mismatch" : "import_target_unavailable";
        Assert.Equal(expectedCode, JsonNode.Parse(output.ToString())!["stop"]!["code"]!.GetValue<string>());
        Assert.All(fixture.Requests, r => Assert.Equal("GET", r.Method));
        Assert.DoesNotContain("secret-canary", output.ToString() + error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReceiptValidationReturnsOnlyVerifiedMapping()
    {
        var binding = ByokImportOverlay.Parse(Bytes(Overlay()))[1];
        var operation = Guid.Parse("2cdff177-aea3-5681-be3e-3e9ede44d1b4");
        var receipt = JsonNode.Parse("""
            {"id":"byok-2cdff177aea35681be3e3e9ede44d1b4","name":"Zulu","type":"openai",
             "base_url":"https://api.example.com/v1","model":"model-1","wire_api":"responses",
             "azure_api_version":null,"reasoning_efforts":["high","low"],"prompt_capacity_tokens":1000,
             "scope":"platform","projectId":null,"state":"Draft","revision":1,
             "configurationHash":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
             "has_api_key":false,"is_active":false,"hasCredential":false,"reference":"Zulu",
             "rate_card_id":null,"rate_card_version":null}
            """)!.AsObject();
        using var fixture = new ImportGatewayFixture { Provider = receipt };
        var identity = new CliIdentity(new CliTarget(new Uri("https://gateway.example/"),
            new Uri("https://issuer.example/"), "agentweaver-cli", "https://gateway.example/", "tenant-1"),
            ["openid", "api.read", "offline_access", "projects.admin"]);
        var gateway = new GatewayClient(fixture.Client, identity, fixture);
        await gateway.DiscoverAsync(CancellationToken.None);
        var response = await gateway.InvokeAsync("readPlatformByokProviderDraft",
            new GatewayInvocation(PathParameters: new Dictionary<string, string> { ["providerId"] = "byok-" + operation.ToString("N") }),
            CancellationToken.None);
        Assert.Equal(200, response.Status);
        var projected = ByokImportContract.Receipt(response.Body, binding, operation)!;
        Assert.Equal("Zulu", projected["reference"]!.GetValue<string>());
        Assert.Equal("byok-2cdff177aea35681be3e3e9ede44d1b4", projected["providerId"]!.GetValue<string>());
        Assert.Equal(["operationId", "providerId", "reference", "revision"], projected.Select(x => x.Key).Order(StringComparer.Ordinal));
        Assert.All(fixture.Requests, r => Assert.Equal("GET", r.Method));
        foreach (var (field, value) in new (string, JsonNode?)[]
        {
            ("id", JsonValue.Create("wrong")), ("reference", JsonValue.Create("wrong")), ("scope", JsonValue.Create("project")),
            ("projectId", JsonValue.Create("p")), ("state", JsonValue.Create("Available")), ("revision", JsonValue.Create(2)),
            ("has_api_key", JsonValue.Create(true)), ("is_active", JsonValue.Create(true)), ("hasCredential", JsonValue.Create(true)),
            ("rate_card_id", JsonValue.Create("price")), ("rate_card_version", JsonValue.Create("price")),
            ("name", JsonValue.Create("wrong")), ("type", JsonValue.Create("azure")), ("base_url", JsonValue.Create("https://other.example/")),
            ("model", JsonValue.Create("wrong")), ("wire_api", JsonValue.Create("completions")),
            ("azure_api_version", JsonValue.Create("wrong")), ("prompt_capacity_tokens", JsonValue.Create(9)),
            ("reasoning_efforts", new JsonArray("low", "high")), ("configurationHash", JsonValue.Create("invalid")),
        })
        {
            var altered = receipt.DeepClone().AsObject();
            altered[field] = value;
            Assert.Null(ByokImportContract.Receipt(JsonNode.Parse(altered.ToJsonString()), binding, operation));
        }
        var missing = receipt.DeepClone().AsObject();
        missing.Remove("is_active");
        Assert.Null(ByokImportContract.Receipt(missing, binding, operation));
        var extra = receipt.DeepClone().AsObject();
        extra["api_key"] = "secret-canary";
        Assert.Null(ByokImportContract.Receipt(extra, binding, operation));
    }

    [Fact]
    public async Task CaptureWritesExactContextToNewPrivateFile()
    {
        using var gateway = new ImportGatewayFixture();
        gateway.Document["paths"]!["/api/v1/platform/byok-providers"]!.AsObject().Remove("post");
        var path = Path.Combine(_directory, "captured.json");
        var output = new StringWriter();
        var error = new StringWriter();
        string[] command = ["models", "byok", "import-context", "--output", path,
            "--gateway-url", "https://gateway.example/", "--broker-issuer", "https://issuer.example/",
            "--api-audience", "https://gateway.example/", "--tenant-id", "tenant-1"];
        var runtime = new CliRuntimeOptions(HttpClient: gateway.Client, Authentication: gateway, Output: output, Error: error);
        Assert.Equal(0, await CliApplication.RunAsync(command, runtime));
        var context = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.Equal(["actorId", "installationId", "issuer", "tenantId"], context.Select(x => x.Key).Order(StringComparer.Ordinal));
        Assert.Equal("https://issuer.example", context["issuer"]!.GetValue<string>());
        Assert.Equal("actor-1", context["actorId"]!.GetValue<string>());
        Assert.Equal("00112233-4455-4677-8899-aabbccddeeff", context["installationId"]!.GetValue<string>());
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.DoesNotContain("actor-1", output.ToString() + error, StringComparison.Ordinal);
        Assert.DoesNotContain("00112233", output.ToString() + error, StringComparison.Ordinal);
        Assert.All(gateway.Requests, request => Assert.Equal("GET", request.Method));
        Assert.Equal(1, await CliApplication.RunAsync(command, runtime));
        Assert.Equal("actor-1", JsonNode.Parse(await File.ReadAllTextAsync(path))!["actorId"]!.GetValue<string>());
    }

    [Fact]
    public async Task DocumentedTrustedIdComparisonStopsBeforeImportOnMismatch()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Agentweaver.slnx"))) root = root.Parent!;
        var guide = await File.ReadAllTextAsync(Path.Combine(root.FullName, "docs", "guide", "manage-model-providers.md"));
        var blocks = Regex.Matches(guide, @"```powershell\s*\r?\n([\s\S]*?)\r?\n\s*```")
            .Select(match => match.Groups[1].Value).ToArray();
        var capture = blocks.Single(block => block.Contains("$trustedInstallationId", StringComparison.Ordinal));
        var import = blocks.Single(block => block.Contains("models byok import --model-bindings", StringComparison.Ordinal));
        var overlay = await WriteOverlayAsync();
        var context = Path.Combine(_directory, "guide-context.json");
        var marker = Path.Combine(_directory, "guide-imported");
        foreach (var matches in new[] { false, true })
        {
            if (File.Exists(context)) File.Delete(context);
            if (File.Exists(marker)) File.Delete(marker);
            var trusted = matches ? "00112233-4455-4677-8899-aabbccddeeff" : "00112233-4455-4677-8899-aabbccddee00";
            var script = $$"""
                $ErrorActionPreference = 'Stop'
                function agentweaver {
                    if ($args[2] -eq 'import-context') {
                        [System.IO.File]::WriteAllText($args[4], '{"installationId":"00112233-4455-4677-8899-aabbccddeeff"}')
                        $global:LASTEXITCODE = 0
                    } else { [System.IO.File]::WriteAllText('{{marker}}', 'sent') }
                }
                {{capture.Replace(@"C:\path\bindings.json", overlay, StringComparison.Ordinal)
                    .Replace(@"C:\path\import-context.json", context, StringComparison.Ordinal)
                    .Replace("<trusted-installation-id>", trusted, StringComparison.Ordinal)}}
                {{import}}
                """;
            var scriptPath = Path.Combine(_directory, "guide.ps1");
            await File.WriteAllTextAsync(scriptPath, script);
            var start = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", scriptPath })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(matches, process.ExitCode == 0);
            Assert.Equal(matches, File.Exists(marker));
        }
    }

    [Theory]
    [InlineData("missing", "expected_context_missing")]
    [InlineData("duplicate", "expected_context_invalid")]
    [InlineData("partial", "expected_context_invalid")]
    [InlineData("actor", "expected_context_mismatch")]
    [InlineData("tenant", "expected_context_mismatch")]
    [InlineData("issuer", "expected_context_mismatch")]
    [InlineData("installation", "expected_context_mismatch")]
    public async Task InvalidOrChangedExpectedContextStopsBeforeNetworkWrites(string failure, string code)
    {
        using var gateway = new ImportGatewayFixture();
        var overlay = await WriteOverlayAsync();
        var context = await WriteContextAsync();
        switch (failure)
        {
            case "missing": File.Delete(context); break;
            case "duplicate": await File.WriteAllTextAsync(context, """
                {"issuer":"https://issuer.example","tenantId":"tenant-1","actorId":"actor-1","actorId":"actor-1","installationId":"00112233-4455-4677-8899-aabbccddeeff"}
                """); break;
            case "partial": await File.WriteAllTextAsync(context, """{"actorId":"actor-1"}"""); break;
            default:
                var saved = JsonNode.Parse(await File.ReadAllTextAsync(context))!.AsObject();
                var field = failure switch
                {
                    "actor" => "actorId", "tenant" => "tenantId",
                    "issuer" => "issuer", _ => "installationId",
                };
                saved[field] = failure switch
                {
                    "actor" => "other-actor", "tenant" => "other-tenant",
                    "issuer" => "https://other.example", _ => "00112233-4455-4677-8899-aabbccddee00",
                };
                await File.WriteAllTextAsync(context, saved.ToJsonString());
                break;
        }
        var result = await RunImportAsync(gateway, overlay, context);
        Assert.Equal(1, result.Code);
        Assert.Equal(code, JsonNode.Parse(result.Output)!["stop"]!["code"]!.GetValue<string>());
        Assert.Empty(gateway.PostBodies);
        if (failure is "missing" or "duplicate" or "partial") Assert.Empty(gateway.Requests);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LostCommittedResponseReplaysExactBodiesAndContinues(int lostAt)
    {
        using var gateway = new ImportGatewayFixture();
        var overlay = await WriteOverlayAsync();
        var context = await WriteContextAsync();
        gateway.AfterCreate = (count, _, response) =>
        {
            if (count != lostAt) return response;
            response.Dispose();
            throw new HttpRequestException("secret-canary");
        };
        var first = await RunImportAsync(gateway, overlay, context);
        var firstResult = JsonNode.Parse(first.Output)!;
        Assert.Equal(1, first.Code);
        Assert.Equal(lostAt - 1, firstResult["completed"]!.AsArray().Count);
        Assert.Equal("unknown", firstResult["stop"]!["outcome"]!.GetValue<string>());
        Assert.Equal(lostAt, gateway.PostBodies.Count);
        var original = gateway.PostBodies.Select(body => body.ToJsonString()).ToArray();
        gateway.AfterCreate = null;
        var retry = await RunImportAsync(gateway, overlay, context);
        Assert.Equal(0, retry.Code);
        Assert.Equal(2, JsonNode.Parse(retry.Output)!["completed"]!.AsArray().Count);
        Assert.Equal(original, gateway.PostBodies.Skip(lostAt).Take(lostAt).Select(body => body.ToJsonString()));
        Assert.Equal(2, gateway.Receipts.Count);
        Assert.DoesNotContain("secret-canary", first.Output + first.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetryAgainstDifferentInstallationSendsNoPost()
    {
        using var gateway = new ImportGatewayFixture();
        var overlay = await WriteOverlayAsync();
        var context = await WriteContextAsync();
        gateway.AfterCreate = (_, _, response) =>
        {
            response.Dispose();
            throw new HttpRequestException("lost");
        };
        var first = await RunImportAsync(gateway, overlay, context);
        Assert.Equal("unknown", JsonNode.Parse(first.Output)!["stop"]!["outcome"]!.GetValue<string>());
        var originalBody = Assert.Single(gateway.PostBodies).ToJsonString();
        gateway.AfterCreate = null;
        gateway.Target["installationId"] = "00112233-4455-4677-8899-aabbccddee00";
        gateway.OwnerInstallation = "00112233-4455-4677-8899-aabbccddee00";
        var wrong = await RunImportAsync(gateway, overlay, context);
        Assert.Equal("expected_context_mismatch", JsonNode.Parse(wrong.Output)!["stop"]!["code"]!.GetValue<string>());
        Assert.Single(gateway.PostBodies);
        gateway.Target["installationId"] = "00112233-4455-4677-8899-aabbccddeeff";
        gateway.OwnerInstallation = "00112233-4455-4677-8899-aabbccddeeff";
        var recovered = await RunImportAsync(gateway, overlay, context);
        Assert.Equal(0, recovered.Code);
        Assert.Equal(originalBody, gateway.PostBodies[1].ToJsonString());
    }

    [Fact]
    public async Task ActorChangeKeepsOperationIdButCannotReplayAnotherActorsReceipt()
    {
        using var gateway = new ImportGatewayFixture();
        var overlay = await WriteOverlayAsync();
        var context = await WriteContextAsync();
        gateway.AfterCreate = (_, _, response) =>
        {
            response.Dispose();
            throw new HttpRequestException("lost");
        };
        await RunImportAsync(gateway, overlay, context);
        var original = Assert.Single(gateway.PostBodies)["operationId"]!.GetValue<string>();
        gateway.AfterCreate = null;
        gateway.Caller["actorId"] = "actor-2";
        gateway.Target["actorId"] = "actor-2";
        var contextJson = JsonNode.Parse(await File.ReadAllTextAsync(context))!.AsObject();
        contextJson["actorId"] = "actor-2";
        await File.WriteAllTextAsync(context, contextJson.ToJsonString());
        var retry = await RunImportAsync(gateway, overlay, context);
        Assert.Equal("model_provider_draft_conflict", JsonNode.Parse(retry.Output)!["stop"]!["code"]!.GetValue<string>());
        Assert.Equal(original, gateway.PostBodies[1]["operationId"]!.GetValue<string>());
        Assert.Single(gateway.Receipts);
    }

    [Fact]
    public async Task CancellationAfterCommitIsUnknownAndRecoverable()
    {
        using var gateway = new ImportGatewayFixture();
        using var cancellation = new CancellationTokenSource();
        var overlay = await WriteOverlayAsync();
        var context = await WriteContextAsync();
        gateway.AfterCreate = (_, _, response) =>
        {
            response.Dispose();
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };
        var result = await RunImportAsync(gateway, overlay, context, ct: cancellation.Token);
        Assert.Equal(130, result.Code);
        Assert.Equal("unknown", JsonNode.Parse(result.Output)!["stop"]!["outcome"]!.GetValue<string>());
        Assert.Single(gateway.Receipts);
        gateway.AfterCreate = null;
        Assert.Equal(0, (await RunImportAsync(gateway, overlay, context)).Code);
    }

    [Fact]
    public async Task PostCommit503IsUnknownAndReplaysOriginalReceipt()
    {
        using var gateway = new ImportGatewayFixture();
        var overlay = await WriteOverlayAsync();
        var context = await WriteContextAsync();
        gateway.AfterCreate = (_, _, response) =>
        {
            response.Dispose();
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("""{"code":"model_provider_draft_unavailable"}""", Encoding.UTF8, "application/json"),
            };
        };
        var first = await RunImportAsync(gateway, overlay, context);
        Assert.Equal("unknown", JsonNode.Parse(first.Output)!["stop"]!["outcome"]!.GetValue<string>());
        Assert.Contains("original context file, overlay, digest and arguments", first.Error, StringComparison.Ordinal);
        Assert.Single(gateway.Receipts);
        var original = Assert.Single(gateway.PostBodies).ToJsonString();
        gateway.AfterCreate = null;
        var retry = await RunImportAsync(gateway, overlay, context);
        Assert.Equal(0, retry.Code);
        Assert.Equal(original, gateway.PostBodies[1].ToJsonString());
    }

    [Fact]
    public async Task TargetSwitchAfterFirstReceiptStopsBeforeSecondPost()
    {
        using var gateway = new ImportGatewayFixture();
        var overlay = await WriteOverlayAsync();
        var context = await WriteContextAsync();
        gateway.BeforeTarget = count =>
        {
            if (count == 3) gateway.Target["installationId"] = "00112233-4455-4677-8899-aabbccddee00";
        };
        var result = await RunImportAsync(gateway, overlay, context);
        var summary = JsonNode.Parse(result.Output)!;
        Assert.Equal(1, result.Code);
        Assert.Single(summary["completed"]!.AsArray());
        Assert.Equal("Zulu", summary["stop"]!["reference"]!.GetValue<string>());
        Assert.Equal("expected_context_mismatch", summary["stop"]!["code"]!.GetValue<string>());
        Assert.Single(gateway.PostBodies);
    }

    [Fact]
    public async Task OwnerPinRejectsRoutingChangeAfterContextRead()
    {
        using var gateway = new ImportGatewayFixture { OwnerInstallation = "00112233-4455-4677-8899-aabbccddee00" };
        var result = await RunImportAsync(gateway, await WriteOverlayAsync(), await WriteContextAsync());
        var summary = JsonNode.Parse(result.Output)!;
        Assert.Equal(1, result.Code);
        Assert.Equal("target_installation_mismatch", summary["stop"]!["code"]!.GetValue<string>());
        Assert.Equal("00112233-4455-4677-8899-aabbccddeeff", Assert.Single(gateway.PostBodies)["targetInstallationId"]!.GetValue<string>());
        Assert.Empty(gateway.Receipts);
    }

    [Theory]
    [InlineData(400, "model_provider_draft_invalid_request", "rejected")]
    [InlineData(413, "request_body_too_large", "rejected")]
    [InlineData(415, "model_provider_draft_unsupported_media_type", "rejected")]
    [InlineData(409, "reference_conflict", "rejected")]
    [InlineData(409, "model_provider_draft_conflict", "rejected")]
    [InlineData(409, "target_installation_mismatch", "rejected")]
    [InlineData(409, "reference_reconciliation_required", "rejected")]
    [InlineData(409, "model_provider_draft_metadata_requires_update", "unknown")]
    [InlineData(409, "secret-canary", "unknown")]
    [InlineData(503, "model_provider_draft_unavailable", "unknown")]
    public async Task ClassifiesWriteResponsesWithoutEchoingServerText(int status, string code, string outcome)
    {
        using var gateway = new ImportGatewayFixture { Rejection = ((HttpStatusCode)status, code) };
        var result = await RunImportAsync(gateway, await WriteOverlayAsync(), await WriteContextAsync());
        var stop = JsonNode.Parse(result.Output)!["stop"]!;
        Assert.Equal(1, result.Code);
        Assert.Equal(outcome, stop["outcome"]!.GetValue<string>());
        Assert.Equal(outcome == "unknown" ? "import_result_unknown" : code, stop["code"]!.GetValue<string>());
        Assert.Equal(outcome == "unknown", stop.AsObject().ContainsKey("expectedProviderId"));
        Assert.Single(gateway.PostBodies);
        Assert.DoesNotContain("secret-canary", result.Output + result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LostSummaryReplaysTheOriginalReceipts()
    {
        using var gateway = new ImportGatewayFixture();
        var overlay = await WriteOverlayAsync();
        var context = await WriteContextAsync();
        var first = await RunImportAsync(gateway, overlay, context, output: new FailingWriter());
        Assert.Equal(1, first.Code);
        Assert.Equal(2, gateway.Receipts.Count);
        var retry = await RunImportAsync(gateway, overlay, context);
        Assert.Equal(0, retry.Code);
        Assert.Equal(2, JsonNode.Parse(retry.Output)!["completed"]!.AsArray().Count);
        Assert.Equal(gateway.PostBodies.Take(2).Select(x => x.ToJsonString()),
            gateway.PostBodies.Skip(2).Select(x => x.ToJsonString()));
    }

    [Fact]
    public async Task InvalidLastRowAndWrongDigestCauseNoRequests()
    {
        var overlay = Overlay();
        overlay["modelBindings"]!["Zulu"]!["Provider"]!["apiKey"] = "secret-canary";
        var bytes = Bytes(overlay);
        var path = Path.Combine(_directory, "overlay.json");
        await File.WriteAllBytesAsync(path, bytes);
        var context = await WriteContextAsync();
        foreach (var digest in new[] { Convert.ToHexString(SHA256.HashData(bytes)), new string('0', 64) })
        {
            var handler = new CountingHandler();
            var output = new StringWriter();
            var error = new StringWriter();
            var result = await CliApplication.RunAsync(
                ["models", "byok", "import", "--model-bindings", path, "--file-sha256", digest, "--expected-context", context],
                new CliRuntimeOptions(HttpClient: new HttpClient(handler), Output: output, Error: error));
            Assert.Equal(1, result);
            Assert.Equal(0, handler.Requests);
            Assert.Equal("import_invalid_input", JsonNode.Parse(output.ToString())!["stop"]!["code"]!.GetValue<string>());
            Assert.DoesNotContain("secret-canary", output.ToString() + error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ImportRejectsSymbolicLinksAndPipesWithoutWaiting()
    {
        var target = Path.Combine(_directory, "target.json");
        var bytes = Bytes(Overlay());
        await File.WriteAllBytesAsync(target, bytes);
        var digest = Convert.ToHexString(SHA256.HashData(bytes));
        var context = await WriteContextAsync();
        var paths = new List<string> { _directory };
        if (!OperatingSystem.IsWindows())
        {
            var link = Path.Combine(_directory, "link.json");
            File.CreateSymbolicLink(link, target);
            paths.Add(link);
            var pipe = Path.Combine(_directory, "pipe");
            using var create = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { pipe } })!;
            await create.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, create.ExitCode);
            paths.Add(pipe);
        }
        foreach (var path in paths)
        {
            using var gateway = new ImportGatewayFixture();
            var output = new StringWriter();
            var error = new StringWriter();
            var command = Task.Run(() => CliApplication.RunAsync(
                ["models", "byok", "import", "--model-bindings", path, "--file-sha256", digest,
                    "--expected-context", context, "--gateway-url", "https://gateway.example/",
                    "--broker-issuer", "https://issuer.example/", "--api-audience", "https://gateway.example/",
                    "--tenant-id", "tenant-1"],
                new CliRuntimeOptions(HttpClient: gateway.Client, Authentication: gateway, Output: output, Error: error)));
            var result = await command.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(gateway.PostBodies);
            Assert.Equal(1, result);
            Assert.Equal("import_invalid_input", JsonNode.Parse(output.ToString())!["stop"]!["code"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task HelpShowsBothImportForms()
    {
        var output = new StringWriter();
        Assert.Equal(0, await CliApplication.RunAsync(["--help"], new CliRuntimeOptions(Output: output)));
        Assert.Contains("agentweaver models byok import-context --output <new-context-file>", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("agentweaver models byok import --model-bindings <file> --file-sha256 <digest>", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--expected-context <context-file>", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--start", "--start")]
    [InlineData("--start", "secret-canary")]
    [InlineData("--project-id", "secret-canary")]
    public async Task ImportRejectsInvalidOptionsWithoutEchoingInput(string option, string value)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var result = await CliApplication.RunAsync(
            ["models", "byok", "import", option, value],
            new CliRuntimeOptions(Output: output, Error: error));
        Assert.Equal(1, result);
        Assert.Equal("import_invalid_input", JsonNode.Parse(output.ToString())!["stop"]!["code"]!.GetValue<string>());
        Assert.DoesNotContain("secret-canary", output.ToString() + error, StringComparison.Ordinal);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }

    }

    private sealed class ImportClientFactory(ImportGatewayFixture gateway) : IGatewayClientFactory
    {
        internal GatewayClient? Client { get; private set; }
        public IGatewayClient Create(CliIdentity identity) =>
            Client = new GatewayClient(gateway.Client, identity, gateway);
    }

    private async Task<string> WriteContextAsync()
    {
        var path = Path.Combine(_directory, "context.json");
        await File.WriteAllTextAsync(path, """
            {"issuer":"https://issuer.example","tenantId":"tenant-1","actorId":"actor-1","installationId":"00112233-4455-4677-8899-aabbccddeeff"}
            """);
        return path;
    }

    private async Task<string> WriteOverlayAsync()
    {
        var path = Path.Combine(_directory, "overlay.json");
        await File.WriteAllBytesAsync(path, Bytes(Overlay()));
        return path;
    }

    private static async Task<(int Code, string Output, string Error)> RunImportAsync(
        ImportGatewayFixture gateway, string path, string context, TextWriter? output = null, CancellationToken ct = default)
    {
        var digest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
        var writer = output ?? new StringWriter();
        var error = new StringWriter();
        var code = await CliApplication.RunAsync(
            ["models", "byok", "import", "--model-bindings", path, "--file-sha256", digest, "--expected-context", context,
                "--gateway-url", "https://gateway.example/", "--broker-issuer", "https://issuer.example/",
                "--api-audience", "https://gateway.example/", "--tenant-id", "tenant-1"],
            new CliRuntimeOptions(HttpClient: gateway.Client, Authentication: gateway, Output: writer, Error: error), ct);
        return (code, writer.ToString() ?? string.Empty, error.ToString());
    }

    private sealed class FailingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override Task WriteLineAsync(string? value) => throw new IOException("secret-canary");
    }
}
