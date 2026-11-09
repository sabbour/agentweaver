using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Cli.Tests;

public sealed class ByokModelCommandsTests : IDisposable
{
    private const string V1Endpoint = "https://example-resource.openai.azure.com/openai/v1";
    private const string ResourceEndpoint = "https://example-resource.openai.azure.com/";
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, $"byok-{Guid.NewGuid():N}");

    public ByokModelCommandsTests()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(repository.FullName, "Agentweaver.slnx")))
            repository = repository.Parent!;
        foreach (var file in new[] { ByokModelCommands.AgentHostFile, ByokModelCommands.EventsFile, ByokModelCommands.ProjectsFile })
        {
            var target = Path.Combine(_directory, "k8s", file);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(repository.FullName, "deploy", "k8s", file), target);
        }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("openai", V1Endpoint, "completions", null)]
    [InlineData("openai", V1Endpoint, "responses", null)]
    [InlineData("azure", ResourceEndpoint, "responses", "2025-04-01-preview")]
    public async Task PlanPinsTheSameBindingHashThatAgentHostAndEventsLoadAndApplyWritesIt(
        string providerType, string endpoint, string wireApi, string? apiVersion)
    {
        var input = Input(providerType, endpoint, wireApi, apiVersion);
        var (code, output, error) = await RunPlanAsync(input);
        Assert.True(code == 0, error);
        Assert.DoesNotContain("\"content\"", output, StringComparison.Ordinal);
        var plan = JsonNode.Parse(File.ReadAllText(PlanPath))!.AsObject();
        var files = plan["files"]!.AsArray().ToDictionary(
            file => file!["path"]!.GetValue<string>(), file => file!["content"]!.GetValue<string>());

        // Load the planned files the same way AgentHost and Events bind them.
        var agentHostJson = files[ByokModelCommands.AgentHostFile].Split('\n')
            .SkipWhile(line => !line.StartsWith("  appsettings.json", StringComparison.Ordinal)).Skip(1)
            .Select(line => line.TrimEnd('\r')).Select(line => line.Length >= 4 ? line[4..] : line);
        var agentHost = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', agentHostJson)))).Build();
        var eventsData = JsonNode.Parse(files[ByokModelCommands.EventsFile])!["data"]!.AsObject();
        var events = new ConfigurationBuilder().AddInMemoryCollection(eventsData.Select(item =>
            KeyValuePair.Create(item.Key.Replace("__", ":", StringComparison.Ordinal), (string?)item.Value!.GetValue<string>())))
            .Build();
        var pin = plan["projectConfiguration"]!["modelSelection"]!["modelBindingPin"]!.Deserialize<RuntimeModelBindingPin>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
            })!;
        foreach (var configuration in new IConfiguration[] { agentHost, events })
        {
            var resolver = new RuntimeModelBindingsResolver(
                configuration[RuntimeModelBindingsResolver.RevisionConfigurationKey]!,
                RuntimeModelBindingsResolver.ReadBindings(configuration));
            Assert.Equal(pin.ConfigurationHash, resolver.ConfigurationHash);
            var binding = resolver.Resolve("azure-gpt", ModelSourceMode.Byok, pin);
            Assert.Equal(new RuntimeByokProvider(providerType, new Uri(endpoint), wireApi, apiVersion), binding.Provider);
            Assert.Equal(["low", "medium", "high"], binding.SupportedReasoningEfforts.ToArray());
            Assert.Equal(providerType == "openai", binding.Provider!.IsAzureOpenAIV1Endpoint);
        }

        Assert.Equal("2.5", eventsData["EventsAndSessions__Cost__Azure__RateCard__ModelRates__gpt-5.4__input"]!.GetValue<string>());
        Assert.Equal("0.25", eventsData["EventsAndSessions__Cost__Azure__RateCard__ModelRates__gpt-5.4__cachedInput"]!.GetValue<string>());
        Assert.Equal("15", eventsData["EventsAndSessions__Cost__Azure__RateCard__ModelRates__gpt-5.4__output"]!.GetValue<string>());
        Assert.Equal("byok.tokens", eventsData["EventsAndSessions__Cost__Azure__RateCard__MeterSource"]!.GetValue<string>());
        Assert.Equal("true", JsonNode.Parse(files[ByokModelCommands.ProjectsFile])!["data"]!
            ["AgentHost__ModelBindings__azure-gpt__Enabled"]!.GetValue<string>());
        var budget = plan["projectConfiguration"]!["runLimits"]!["spendBudgets"]![0]!;
        Assert.Equal(("run", "byok.tokens", "USD", 5m),
            (budget["scope"]!.GetValue<string>(), budget["meterSource"]!.GetValue<string>(),
             budget["unit"]!.GetValue<string>(), budget["hardLimit"]!.GetValue<decimal>()));
        Assert.Equal("fedcba9876543210fedcba9876543210",
            plan["projectConfiguration"]!["modelSelection"]!["credentialReference"]!["version"]!.GetValue<string>());

        var applied = await RunAsync(["models", "byok", "apply", "--plan", PlanPath]);
        Assert.True(applied.Code == 0, applied.Error);
        foreach (var (relative, content) in files)
            Assert.Equal(content, File.ReadAllText(Path.Combine(_directory, "k8s", relative)));
        var stale = await RunAsync(["models", "byok", "apply", "--plan", PlanPath]);
        Assert.Equal(1, stale.Code);
        Assert.Contains("changed after the plan was made", stale.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanWritesAModelBindingsFileForDeploymentWithoutChangingTheSource()
    {
        var agentHostPath = Path.Combine(_directory, "k8s", ByokModelCommands.AgentHostFile);
        var productText = File.ReadAllText(agentHostPath);
        var inputPath = Path.Combine(_directory, "byok.json");
        File.WriteAllText(inputPath, Input("openai", V1Endpoint, "responses", null).ToJsonString());
        string[] plan = ["models", "byok", "plan", "--input", inputPath, "--source", Path.Combine(_directory, "k8s"),
            "--output", PlanPath, "--overlay-output", OverlayPath];

        var (code, _, error) = await RunAsync(plan);
        Assert.True(code == 0, error);
        Assert.Equal(productText, File.ReadAllText(agentHostPath));
        var overlay = JsonNode.Parse(File.ReadAllText(OverlayPath))!.AsObject();
        var product = JsonNode.Parse(string.Join('\n', productText.Split('\n')
            .SkipWhile(line => !line.StartsWith("  appsettings.json", StringComparison.Ordinal)).Skip(1)
            .Select(line => line.TrimEnd('\r')).Select(line => line.Length >= 4 ? line[4..] : line)))!["AgentHost"]!;
        Assert.Equal("agentweaver.model-bindings-overlay", overlay["kind"]!.GetValue<string>());
        Assert.Equal(product["ModelBindingsRevision"]!.GetValue<string>(), overlay["baseRevision"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(product["ModelBindings"], overlay["baseModelBindings"]));
        Assert.Equal("byok-r1", overlay["revision"]!.GetValue<string>());
        Assert.Equal(["azure-gpt"], overlay["modelBindings"]!.AsObject().Select(item => item.Key));
        Assert.Equal("Byok", overlay["modelBindings"]!["azure-gpt"]!["SourceMode"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"gpt-5.4":{"input":"2.5","cachedInput":"0.25","output":"15"}}"""),
            overlay["modelRates"]));
        Assert.DoesNotContain("fedcba9876543210fedcba9876543210", overlay.ToJsonString(), StringComparison.Ordinal);

        Assert.Equal(0, (await RunAsync(["models", "byok", "apply", "--plan", PlanPath])).Code);
        File.Delete(PlanPath);
        File.Delete(OverlayPath);
        var next = Input("openai", V1Endpoint, "responses", null);
        next["revision"] = "byok-r2";
        File.WriteAllText(inputPath, next.ToJsonString());
        var rejected = await RunAsync(plan);
        Assert.Equal(1, rejected.Code);
        Assert.Contains("already has a BYOK binding", rejected.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(OverlayPath));
    }

    [Theory]
    [InlineData("apiKey", "\"secret-value\"", "unsupported field 'apiKey'")]
    [InlineData("wireApi", null, "'wireApi' is required")]
    [InlineData("reasoningEfforts", "[\"none\",\"low\"]", "'reasoningEfforts' must list")]
    [InlineData("endpoint", "\"https://example-resource.openai.azure.com/openai/v1?api-key=x\"", "'endpoint' must be")]
    [InlineData("endpoint", "\"https://example.com/openai/v1\"", "'endpoint' must be")]
    [InlineData("endpoint", "\"https://example-resource.openai.azure.com:8443/openai/v1\"", "'endpoint' must be")]
    [InlineData("azureApiVersion", "\"2025-04-01-preview\"", "Use providerType openai")]
    [InlineData("credentialReference", "{\"id\":\"byok-key\",\"version\":\"latest\"}", "exact 32-character version")]
    [InlineData("deployment", "\"gpt:5.4\"", "'deployment' may contain only")]
    public async Task PlanRejectsSecretsAndIncompleteOrUnsupportedProviderSettings(
        string field, string? value, string message)
    {
        var input = Input("openai", V1Endpoint, "completions", null);
        if (value is null)
            input.Remove(field);
        else
            input[field] = JsonNode.Parse(value);
        var (code, _, error) = await RunPlanAsync(input);
        Assert.Equal(1, code);
        Assert.Contains(message, error, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", error, StringComparison.Ordinal);
        Assert.False(File.Exists(PlanPath));
    }

    [Fact]
    public async Task PlanReadsAzureRetailPricesPerMeterAndNormalizesToUsdPerMillionTokens()
    {
        var input = Input("openai", V1Endpoint, "completions", null);
        var meters = new Dictionary<string, (string Unit, decimal Price)>
        {
            ["11111111-1111-1111-1111-111111111111"] = ("1M", 2.5m),
            ["22222222-2222-2222-2222-222222222222"] = ("1K", 0.00025m),
            ["33333333-3333-3333-3333-333333333333"] = ("1M", 15m),
        };
        input["pricing"] = new JsonObject
        {
            ["source"] = "azure-retail-prices",
            ["region"] = "swedencentral",
            ["meterIds"] = new JsonObject
            {
                ["input"] = "11111111-1111-1111-1111-111111111111",
                ["cachedInput"] = "22222222-2222-2222-2222-222222222222",
                ["output"] = "33333333-3333-3333-3333-333333333333",
            },
        };
        var handler = new RetailPricesHandler(meters);
        var (code, _, error) = await RunPlanAsync(input, new HttpClient(handler));
        Assert.True(code == 0, error);
        Assert.Equal(3, handler.Filters.Count);
        Assert.All(handler.Filters, filter => Assert.Contains("armRegionName eq 'swedencentral'", filter, StringComparison.Ordinal));
        var pricing = JsonNode.Parse(File.ReadAllText(PlanPath))!["pricing"]!;
        Assert.Equal("azure-retail-prices.standard.swedencentral", pricing["rateCardId"]!.GetValue<string>());
        Assert.Equal((2.5m, 0.25m, 15m), (pricing["usdPer1M"]!["input"]!.GetValue<decimal>(),
            pricing["usdPer1M"]!["cachedInput"]!.GetValue<decimal>(), pricing["usdPer1M"]!["output"]!.GetValue<decimal>()));
    }

    [Fact]
    public async Task PricesFollowsRetailPagesOnlyOnTheRetailHost()
    {
        const string nextPage = "https://prices.azure.com:443/api/retail/prices?currencyCode=USD&$skip=1000";
        var pages = new PagedHandler(nextPage);
        var listed = await RunAsync(["models", "byok", "prices", "--region", "swedencentral", "--search", "5.4"],
            new HttpClient(pages));
        Assert.True(listed.Code == 0, listed.Error);
        Assert.Equal(2, pages.Requests);
        Assert.Equal(2, JsonNode.Parse(listed.Output)!.AsArray().Count);

        var offHost = await RunAsync(["models", "byok", "prices", "--region", "swedencentral", "--search", "5.4"],
            new HttpClient(new PagedHandler("https://example.com/api/retail/prices?$skip=1000")));
        Assert.Equal(1, offHost.Code);
        Assert.Contains("unexpected pages", offHost.Error, StringComparison.Ordinal);
    }

    private string PlanPath => Path.Combine(_directory, "plan.json");

    private string OverlayPath => Path.Combine(_directory, "model-bindings.json");

    private async Task<(int Code, string Output, string Error)> RunPlanAsync(JsonObject input, HttpClient? client = null)
    {
        var inputPath = Path.Combine(_directory, "byok.json");
        File.WriteAllText(inputPath, input.ToJsonString());
        return await RunAsync(["models", "byok", "plan", "--input", inputPath,
            "--source", Path.Combine(_directory, "k8s"), "--output", PlanPath], client);
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(string[] args, HttpClient? client = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await CliApplication.RunAsync(args, new CliRuntimeOptions(HttpClient: client, Output: output, Error: error));
        return (code, output.ToString(), error.ToString());
    }

    private static JsonObject Input(string providerType, string endpoint, string wireApi, string? apiVersion)
    {
        var input = new JsonObject
        {
            ["reference"] = "azure-gpt",
            ["deployment"] = "gpt-5.4",
            ["providerType"] = providerType,
            ["endpoint"] = endpoint,
            ["wireApi"] = wireApi,
            ["reasoningEfforts"] = new JsonArray("high", "low", "medium"),
            ["defaultReasoningEffort"] = "high",
            ["revision"] = "byok-r1",
            ["credentialReference"] = new JsonObject
            {
                ["id"] = "byok-azure-openai-key",
                ["version"] = "fedcba9876543210fedcba9876543210",
            },
            ["pricing"] = new JsonObject
            {
                ["source"] = "manual",
                ["inputUsdPer1M"] = 2.5,
                ["cachedInputUsdPer1M"] = 0.25,
                ["outputUsdPer1M"] = 15,
                ["sourceUrl"] = "https://azure.microsoft.com/pricing/details/cognitive-services/openai-service/",
                ["retrievedOn"] = "2026-11-09",
            },
            ["spendBudget"] = new JsonObject
            {
                ["budgetId"] = "byok-run-usd",
                ["hardLimitUsd"] = 5,
                ["reservePerDispatchUsd"] = 0.5,
            },
        };
        if (apiVersion is not null)
            input["azureApiVersion"] = apiVersion;
        return input;
    }

    private sealed class PagedHandler(string nextPage) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            var body = new JsonObject
            {
                ["Items"] = new JsonArray(new JsonObject
                {
                    ["meterId"] = $"{Requests}0000000-0000-0000-0000-000000000000",
                    ["meterName"] = $"5.4 page {Requests}",
                    ["currencyCode"] = "USD",
                    ["tierMinimumUnits"] = 0,
                    ["retailPrice"] = 1m,
                    ["unitOfMeasure"] = "1M",
                }),
                ["NextPageLink"] = Requests == 1 ? nextPage : null,
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class RetailPricesHandler(IReadOnlyDictionary<string, (string Unit, decimal Price)> meters)
        : HttpMessageHandler
    {
        public List<string> Filters { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https://prices.azure.com/api/retail/prices", request.RequestUri!.GetLeftPart(UriPartial.Path));
            var filter = Uri.UnescapeDataString(request.RequestUri.Query);
            Filters.Add(filter);
            var (meterId, (unit, price)) = meters.Single(item => filter.Contains($"meterId eq '{item.Key}'", StringComparison.Ordinal));
            var body = new JsonObject
            {
                ["Items"] = new JsonArray(new JsonObject
                {
                    ["meterId"] = meterId,
                    ["currencyCode"] = "USD",
                    ["tierMinimumUnits"] = 0,
                    ["retailPrice"] = price,
                    ["unitOfMeasure"] = unit,
                }),
                ["NextPageLink"] = null,
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            });
        }
    }
}
