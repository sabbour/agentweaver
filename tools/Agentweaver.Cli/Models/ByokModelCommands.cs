using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Abstractions;

namespace Agentweaver.Cli;

/// <summary>
/// Plans and applies one BYOK Azure OpenAI model binding in the deployment source files. The commands
/// never read or write an API key: the key stays in Key Vault and the plan holds only its versioned reference.
/// </summary>
internal static class ByokModelCommands
{
    internal const string AgentHostFile = "profiles/agenthost/runtime-config.example.yaml";
    internal const string EventsFile = "runtime-configs/events-and-sessions-runtime-config.json";
    internal const string ProjectsFile = "runtime-configs/projects-config-runtime-config.json";
    internal const string RetailPricesUrl = "https://prices.azure.com/api/retail/prices";
    private const string PlanKind = "agentweaver.byok-model-plan";
    private const string OverlayKind = "agentweaver.model-bindings-overlay";
    private const string BindingPrefix = "AgentHost__ModelBindings__";
    private const string RevisionKey = "AgentHost__ModelBindingsRevision";
    private const string AzureCostPrefix = "EventsAndSessions__Cost__Azure__";
    private const string ModelRatesPrefix = AzureCostPrefix + "RateCard__ModelRates__";
    private static readonly string[] Efforts = ["low", "medium", "high", "xhigh"];
    private static readonly string[] AzureHostSuffixes =
        [".openai.azure.com", ".cognitiveservices.azure.com", ".services.ai.azure.com"];

    private static readonly JsonSerializerOptions FileJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<int> RunAsync(
        string[] args, CliRuntimeOptions runtime, TextWriter output, CancellationToken ct)
    {
        if (args.Length < 2 || args[0] != "byok" || args[1] is not ("prices" or "plan" or "apply"))
            throw new CliUsageException("Usage: agentweaver models byok prices|plan|apply [options]");
        var parsed = CliOptions.Parse(args[2..], new HashSet<string>(StringComparer.Ordinal));
        switch (args[1])
        {
            case "prices":
            {
                parsed.RequireOnly("region", "search");
                var region = RequireRegion(parsed.Single("region"), "--region");
                var search = parsed.Single("search") ?? throw new CliUsageException("Use --search <meter text>.");
                if (search.Length is 0 or > 64 ||
                    !search.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or ' '))
                    throw new CliUsageException("--search must contain only letters, digits, '.', '-', and spaces.");
                var filter = $"serviceName eq 'Foundry Models' and armRegionName eq '{region}' and " +
                    $"priceType eq 'Consumption' and contains(meterName, '{search}')";
                var items = await ReadRetailPricesAsync(runtime, filter, ct).ConfigureAwait(false);
                var listing = items
                    .OrderBy(item => Text(item, "productName"), StringComparer.Ordinal)
                    .ThenBy(item => Text(item, "meterName"), StringComparer.Ordinal)
                    .Select(item => (JsonNode)new JsonObject
                    {
                        ["meterId"] = Text(item, "meterId"),
                        ["meterName"] = Text(item, "meterName"),
                        ["productName"] = Text(item, "productName"),
                        ["skuName"] = Text(item, "skuName"),
                        ["unitOfMeasure"] = Text(item, "unitOfMeasure"),
                        ["usdPer1M"] = TryUsdPer1M(item, out var price) ? price : (decimal?)null,
                    });
                await output.WriteLineAsync(new JsonArray(listing.ToArray()).ToJsonString(FileJson))
                    .ConfigureAwait(false);
                return 0;
            }
            case "plan":
            {
                parsed.RequireOnly("input", "source", "output", "overlay-output");
                var input = parsed.Single("input") ?? throw new CliUsageException("Use --input <byok.json>.");
                var source = parsed.Single("source") ?? throw new CliUsageException("Use --source <deploy/k8s>.");
                var planPath = parsed.Single("output") ?? throw new CliUsageException("Use --output <new-plan.json>.");
                if (File.Exists(planPath))
                    throw new CliUsageException("The plan output file already exists. Choose a new file.");
                var overlayPath = parsed.Single("overlay-output");
                if (overlayPath is not null && File.Exists(overlayPath))
                    throw new CliUsageException("The model bindings output file already exists. Choose a new file.");
                var request = ByokRequest.Read(JsonNode.Parse(await File.ReadAllTextAsync(input, ct)
                    .ConfigureAwait(false)) as JsonObject);
                var pricing = await ResolvePricingAsync(request.Pricing, runtime, ct).ConfigureAwait(false);
                var plan = CreatePlan(request, pricing, Path.GetFullPath(source), overlayPath is not null);
                await File.WriteAllTextAsync(planPath, plan.ToJsonString(FileJson), ct).ConfigureAwait(false);
                if (overlayPath is not null)
                    await File.WriteAllTextAsync(overlayPath, plan["modelBindingsOverlay"]!.ToJsonString(FileJson), ct)
                        .ConfigureAwait(false);
                var summary = plan.DeepClone().AsObject();
                foreach (var file in summary["files"]!.AsArray())
                    file!.AsObject().Remove("content");
                await output.WriteLineAsync(summary.ToJsonString(FileJson)).ConfigureAwait(false);
                return 0;
            }
            default:
            {
                parsed.RequireOnly("plan");
                var planPath = parsed.Single("plan") ?? throw new CliUsageException("Use --plan <plan.json>.");
                var written = ApplyPlan(JsonNode.Parse(await File.ReadAllTextAsync(planPath, ct)
                    .ConfigureAwait(false)) as JsonObject);
                await output.WriteLineAsync(new JsonObject
                {
                    ["applied"] = true,
                    ["files"] = new JsonArray(written.Select(path => (JsonNode)path).ToArray()),
                }.ToJsonString(FileJson)).ConfigureAwait(false);
                return 0;
            }
        }
    }

    /// <summary>
    /// Builds the reviewed file changes, model pin, and project configuration fragment. With
    /// <paramref name="overlay"/>, also builds the model bindings file that the Kustomize deployment merges.
    /// </summary>
    internal static JsonObject CreatePlan(
        ByokRequest request, ResolvedPricing pricing, string sourceRoot, bool overlay = false)
    {
        var agentHostText = File.ReadAllText(Path.Combine(sourceRoot, AgentHostFile));
        var eventsText = File.ReadAllText(Path.Combine(sourceRoot, EventsFile));
        var projectsText = File.ReadAllText(Path.Combine(sourceRoot, ProjectsFile));

        var (agentHostDocument, writeAgentHost) = ReadAppSettings(agentHostText);
        var agentHost = agentHostDocument["AgentHost"] as JsonObject
            ?? throw new InvalidOperationException("The AgentHost runtime configuration has no AgentHost section.");
        var bindings = agentHost["ModelBindings"] as JsonObject
            ?? throw new InvalidOperationException("The AgentHost runtime configuration has no model bindings.");
        if (agentHost["ModelBindingsRevision"]?.GetValue<string>() == request.Revision)
            throw new CliUsageException("The revision must differ from the current model binding revision.");
        if (bindings[request.Reference] is JsonObject existing &&
            existing["SourceMode"]?.GetValue<string>() != "Byok")
            throw new CliUsageException($"Model reference '{request.Reference}' is not a BYOK binding. Choose another reference.");
        var baseRevision = agentHost["ModelBindingsRevision"]?.GetValue<string>();
        var baseBindings = bindings.DeepClone();
        if (overlay && (bindings.Any(item => item.Value?["SourceMode"]?.GetValue<string>() == "Byok") ||
                eventsText.Contains($"\"{AzureCostPrefix}", StringComparison.Ordinal)))
            throw new CliUsageException(
                "The source already has a BYOK binding or an Azure rate card. Use product deployment source for --overlay-output.");
        bindings[request.Reference] = request.AgentHostBinding();
        agentHost["ModelBindingsRevision"] = request.Revision;

        var events = JsonNode.Parse(eventsText)!.AsObject();
        var projects = JsonNode.Parse(projectsText)!.AsObject();
        var eventsData = events["data"]!.AsObject();
        var resourceGeneration = long.TryParse(
            eventsData[AzureCostPrefix + "ResourceGeneration"]?.GetValue<string>(),
            NumberStyles.None, CultureInfo.InvariantCulture, out var generation) ? generation + 1 : 1;
        events["data"] = ReplaceFlatKeys(eventsData, request.Reference, request.EventsBinding(),
            request.Revision, request.Deployment, pricing, resourceGeneration);
        projects["data"] = ReplaceFlatKeys(projects["data"]!.AsObject(), request.Reference,
            request.ProjectsBinding(), request.Revision, null, null, 0);

        var hash = ModelBindingHash.FromAgentHost(bindings);
        if (hash != ModelBindingHash.FromFlatKeys(events["data"]!.AsObject()))
            throw new InvalidOperationException(
                "The AgentHost and Events model bindings differ. Make them equal before you add a BYOK binding.");
        var references = bindings.Select(item => item.Key).Order(StringComparer.Ordinal);
        var projectReferences = projects["data"]!.AsObject()
            .Where(item => item.Key.StartsWith(BindingPrefix, StringComparison.Ordinal) &&
                item.Key.EndsWith("__SourceMode", StringComparison.Ordinal))
            .Select(item => item.Key[BindingPrefix.Length..^"__SourceMode".Length])
            .Order(StringComparer.Ordinal);
        if (!references.SequenceEqual(projectReferences, StringComparer.Ordinal))
            throw new InvalidOperationException("The AgentHost and Projects model binding references differ.");

        var pin = new JsonObject
        {
            ["contractVersion"] = 1,
            ["modelSelectionReference"] = request.Reference,
            ["modelId"] = request.Deployment,
            ["sourceMode"] = "byok",
            ["configurationRevision"] = request.Revision,
            ["configurationHash"] = hash,
            ["reasoningEffort"] = request.DefaultReasoningEffort,
            ["providerType"] = request.ProviderType,
        };
        var plan = new JsonObject
        {
            ["kind"] = PlanKind,
            ["schemaVersion"] = 1,
            ["source"] = sourceRoot,
            ["modelBindings"] = new JsonObject
            {
                ["reference"] = request.Reference,
                ["revision"] = request.Revision,
                ["configurationHash"] = hash,
            },
            ["pricing"] = pricing.ToJson(),
            ["files"] = new JsonArray(
                FileChange(AgentHostFile, agentHostText, writeAgentHost(agentHostDocument)),
                FileChange(EventsFile, eventsText, WriteJson(events, eventsText)),
                FileChange(ProjectsFile, projectsText, WriteJson(projects, projectsText))),
            ["projectConfiguration"] = new JsonObject
            {
                ["modelSelection"] = new JsonObject
                {
                    ["reference"] = request.Reference,
                    ["sourceMode"] = "byok",
                    ["credentialReference"] = new JsonObject
                    {
                        ["id"] = request.Credential.Id,
                        ["version"] = request.Credential.Version,
                    },
                    ["reasoningEffort"] = request.DefaultReasoningEffort,
                    ["modelBindingPin"] = pin,
                },
                ["runLimits"] = new JsonObject
                {
                    ["spendBudgets"] = new JsonArray(request.SpendBudgetJson()),
                },
            },
        };
        if (overlay)
        {
            var rates = new JsonObject { ["input"] = FormatDecimal(pricing.Input) };
            if (pricing.CachedInput is { } cachedInput)
                rates["cachedInput"] = FormatDecimal(cachedInput);
            rates["output"] = FormatDecimal(pricing.Output);
            plan["modelBindingsOverlay"] = new JsonObject
            {
                ["kind"] = OverlayKind,
                ["schemaVersion"] = 1,
                ["baseRevision"] = baseRevision,
                ["baseModelBindings"] = baseBindings,
                ["revision"] = request.Revision,
                ["modelBindings"] = new JsonObject { [request.Reference] = request.AgentHostBinding() },
                ["rateCard"] = new JsonObject { ["id"] = pricing.RateCardId, ["version"] = pricing.Version },
                ["modelRates"] = new JsonObject { [request.Deployment] = rates },
            };
        }
        return plan;
    }

    /// <summary>Writes planned files only when every file still has its planned starting digest.</summary>
    internal static IReadOnlyList<string> ApplyPlan(JsonObject? plan)
    {
        if (plan?["kind"]?.GetValue<string>() != PlanKind || plan["schemaVersion"]?.GetValue<int>() != 1 ||
            plan["source"]?.GetValue<string>() is not { Length: > 0 } source ||
            plan["files"] is not JsonArray files || files.Count != 3)
            throw new CliUsageException("The plan file is not an Agentweaver BYOK model plan.");
        var changes = files.Select(file =>
        {
            var relative = file?["path"]?.GetValue<string>();
            if (relative is not (AgentHostFile or EventsFile or ProjectsFile))
                throw new CliUsageException("The plan names an unexpected file.");
            var content = file!["content"]?.GetValue<string>()
                ?? throw new CliUsageException("The plan has no file content.");
            if (Sha256(content) != file["afterSha256"]?.GetValue<string>())
                throw new CliUsageException($"The planned content of {relative} does not match its digest.");
            var path = Path.GetFullPath(Path.Combine(source, relative));
            if (!File.Exists(path) || Sha256(File.ReadAllText(path)) != file["beforeSha256"]?.GetValue<string>())
                throw new CliUsageException($"{relative} changed after the plan was made. Make a new plan.");
            return (path, content);
        }).ToArray();
        if (changes.Select(change => change.path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
            throw new CliUsageException("The plan names a file more than once.");
        foreach (var (path, content) in changes)
            File.WriteAllText(path, content);
        return changes.Select(change => change.path).ToArray();
    }

    private static async Task<ResolvedPricing> ResolvePricingAsync(
        ByokPricing pricing, CliRuntimeOptions runtime, CancellationToken ct)
    {
        if (pricing.Manual is { } manual)
            return manual;
        var meters = pricing.MeterIds!;
        var prices = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var (category, meterId) in meters)
        {
            var items = await ReadRetailPricesAsync(runtime,
                $"meterId eq '{meterId}' and armRegionName eq '{pricing.Region}' and priceType eq 'Consumption'",
                ct).ConfigureAwait(false);
            var matches = items.Where(item =>
                (item["tierMinimumUnits"]?.GetValue<decimal>() ?? 0) == 0).ToArray();
            if (matches.Length != 1 || Text(matches[0], "currencyCode") != "USD" ||
                !TryUsdPer1M(matches[0], out var price))
                throw new InvalidOperationException(
                    $"The Azure Retail Prices API did not return one USD consumption price for the {category} meter.");
            prices[category] = price;
        }
        return new ResolvedPricing(
            $"azure-retail-prices.standard.{pricing.Region}",
            DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            prices["input"],
            prices.TryGetValue("cachedInput", out var cached) ? cached : null,
            prices["output"],
            RetailPricesUrl,
            meters);
    }

    private static async Task<List<JsonObject>> ReadRetailPricesAsync(
        CliRuntimeOptions runtime, string filter, CancellationToken ct)
    {
        using var ownedClient = runtime.HttpClient is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            : null;
        var client = runtime.HttpClient ?? ownedClient!;
        var items = new List<JsonObject>();
        string? next = $"{RetailPricesUrl}?currencyCode=USD&$filter={Uri.EscapeDataString(filter)}";
        for (var page = 0; next is not null; page++)
        {
            if (page == 20 || !Uri.TryCreate(next, UriKind.Absolute, out var pageUri) ||
                pageUri.Scheme != Uri.UriSchemeHttps || pageUri.Port != 443 || pageUri.UserInfo.Length != 0 ||
                !pageUri.Host.Equals("prices.azure.com", StringComparison.OrdinalIgnoreCase) ||
                pageUri.AbsolutePath != "/api/retail/prices")
                throw new InvalidOperationException("The Azure Retail Prices API returned too many or unexpected pages.");
            using var response = await client.GetAsync(pageUri, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"The Azure Retail Prices API returned HTTP {(int)response.StatusCode}.");
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            items.AddRange((body?["Items"] as JsonArray ?? []).OfType<JsonObject>());
            next = body?["NextPageLink"]?.GetValue<string>();
        }
        return items;
    }

    private static bool TryUsdPer1M(JsonObject item, out decimal price)
    {
        price = 0;
        var retail = item["retailPrice"] is JsonValue value && value.TryGetValue<decimal>(out var amount)
            ? amount : 0;
        decimal? multiplier = Text(item, "unitOfMeasure") switch
        {
            "1K" => 1000m,
            "1M" => 1m,
            _ => null,
        };
        if (retail <= 0 || multiplier is null)
            return false;
        price = retail * multiplier.Value;
        return true;
    }

    private static string? Text(JsonObject item, string name) =>
        item[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    // Replaces one reference's flat binding keys, sets the revision, and, for Events, updates the Azure rate card.
    private static JsonObject ReplaceFlatKeys(
        JsonObject data, string reference, IReadOnlyList<KeyValuePair<string, string>> binding, string revision,
        string? deployment, ResolvedPricing? pricing, long resourceGeneration)
    {
        var bindingPrefix = $"{BindingPrefix}{reference}__";
        var entries = data.Where(entry =>
                !entry.Key.StartsWith(bindingPrefix, StringComparison.Ordinal) &&
                (pricing is null || !entry.Key.StartsWith(AzureCostPrefix, StringComparison.Ordinal)))
            .Select(entry => KeyValuePair.Create(entry.Key, entry.Value?.DeepClone()))
            .ToList();
        var lastBindingIndex = entries.FindLastIndex(entry => entry.Key.StartsWith(BindingPrefix, StringComparison.Ordinal));
        entries.InsertRange(lastBindingIndex + 1,
            binding.Select(item => KeyValuePair.Create(item.Key, (JsonNode?)item.Value)));
        var result = new JsonObject();
        foreach (var (key, value) in entries)
            result[key] = key == RevisionKey ? revision : value;
        if (pricing is null)
            return result;

        result[AzureCostPrefix + "ResourceId"] = $"azure-cost:{revision}";
        result[AzureCostPrefix + "ResourceGeneration"] = resourceGeneration.ToString(CultureInfo.InvariantCulture);
        result[AzureCostPrefix + "OptionsRevision"] = revision;
        result[AzureCostPrefix + "OptionsSchemaVersion"] = "1";
        result[AzureCostPrefix + "PricingModel"] = "StandardTokens";
        result[AzureCostPrefix + "RateCard__Id"] = pricing.RateCardId;
        result[AzureCostPrefix + "RateCard__Version"] = pricing.Version;
        result[AzureCostPrefix + "RateCard__MeterSource"] = SdkMeterSources.ByokTokens;
        result[AzureCostPrefix + "RateCard__Unit"] = "USD";
        result[AzureCostPrefix + "RateCard__NanoUnitsPerUnit"] = "1000000";
        // Keep the rates of other BYOK models; one rate card prices every Azure OpenAI deployment.
        foreach (var (key, value) in data.Where(entry =>
                     entry.Key.StartsWith(ModelRatesPrefix, StringComparison.Ordinal) &&
                     !entry.Key.StartsWith($"{ModelRatesPrefix}{deployment}__", StringComparison.Ordinal)))
            result[key] = value?.DeepClone();
        result[$"{ModelRatesPrefix}{deployment}__input"] = FormatDecimal(pricing.Input);
        if (pricing.CachedInput is { } cachedInput)
            result[$"{ModelRatesPrefix}{deployment}__cachedInput"] = FormatDecimal(cachedInput);
        result[$"{ModelRatesPrefix}{deployment}__output"] = FormatDecimal(pricing.Output);
        return result;
    }

    private static string FormatDecimal(decimal value) =>
        value.ToString("0.############", CultureInfo.InvariantCulture);

    /// <summary>Reads the JSON block of the AgentHost ConfigMap and returns a writer that keeps the other lines.</summary>
    private static (JsonObject Document, Func<JsonObject, string> Write) ReadAppSettings(string yaml)
    {
        var newline = yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = yaml.Split(newline);
        var start = Array.FindIndex(lines, line => line == "  appsettings.json: |");
        if (start < 0)
            throw new InvalidOperationException("The AgentHost runtime configuration has no appsettings.json block.");
        var end = start + 1;
        while (end < lines.Length && (lines[end].StartsWith("    ", StringComparison.Ordinal) || lines[end].Length == 0))
            end++;
        while (end > start + 1 && lines[end - 1].Length == 0)
            end--;
        var json = string.Join("\n", lines[(start + 1)..end].Select(line => line.Length >= 4 ? line[4..] : line));
        var document = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException("The AgentHost appsettings.json block is not a JSON object.");
        return (document, updated =>
        {
            var block = updated.ToJsonString(FileJson).Split('\n').Select(line => "    " + line.TrimEnd('\r'));
            return string.Join(newline, lines[..(start + 1)].Concat(block).Concat(lines[end..]));
        });
    }

    private static string WriteJson(JsonObject document, string original)
    {
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var text = document.ToJsonString(FileJson).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", newline, StringComparison.Ordinal);
        return original.EndsWith('\n') ? text + newline : text;
    }

    private static JsonObject FileChange(string relative, string before, string after) => new()
    {
        ["path"] = relative,
        ["beforeSha256"] = Sha256(before),
        ["afterSha256"] = Sha256(after),
        ["content"] = after,
    };

    private static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string RequireRegion(string? value, string name) =>
        value is { Length: > 0 and <= 64 } && value.All(char.IsAsciiLetterOrDigit)
            ? value
            : throw new CliUsageException($"{name} must be an Azure region name, such as swedencentral.");

    internal sealed record ByokPricing(
        string? Region, IReadOnlyDictionary<string, string>? MeterIds, ResolvedPricing? Manual);

    internal sealed record ResolvedPricing(
        string RateCardId,
        string Version,
        decimal Input,
        decimal? CachedInput,
        decimal Output,
        string SourceUrl,
        IReadOnlyDictionary<string, string>? MeterIds)
    {
        public JsonObject ToJson()
        {
            var json = new JsonObject
            {
                ["rateCardId"] = RateCardId,
                ["version"] = Version,
                ["sourceUrl"] = SourceUrl,
                ["usdPer1M"] = new JsonObject
                {
                    ["input"] = Input,
                    ["cachedInput"] = CachedInput,
                    ["output"] = Output,
                },
            };
            if (MeterIds is not null)
                json["meterIds"] = new JsonObject(MeterIds.Select(item =>
                    KeyValuePair.Create(item.Key, (JsonNode?)item.Value)));
            return json;
        }
    }

    /// <summary>Validated BYOK input. It contains a Key Vault secret reference and never the key itself.</summary>
    internal sealed record ByokRequest(
        string Reference,
        string Deployment,
        string ProviderType,
        Uri Endpoint,
        string WireApi,
        string? AzureApiVersion,
        IReadOnlyList<string> ReasoningEfforts,
        string DefaultReasoningEffort,
        int? PromptCapacityTokens,
        string Revision,
        SecretRef Credential,
        ByokPricing Pricing,
        SpendBudget Budget)
    {
        private static readonly string[] Fields =
        [
            "reference", "deployment", "providerType", "endpoint", "wireApi", "azureApiVersion",
            "reasoningEfforts", "defaultReasoningEffort", "promptCapacityTokens", "revision",
            "credentialReference", "pricing", "spendBudget",
        ];

        public static ByokRequest Read(JsonObject? input)
        {
            if (input is null)
                throw new CliUsageException("The BYOK input must be a JSON object.");
            RequireFields(input, Fields, "input");
            var reference = KeySegment(input, "reference");
            var deployment = KeySegment(input, "deployment");
            var revision = Identifier(input, "revision");
            var providerType = String(input, "providerType");
            var wireApi = String(input, "wireApi");
            if (wireApi is not ("responses" or "completions"))
                throw new CliUsageException("'wireApi' must be responses or completions.");
            var apiVersion = OptionalString(input, "azureApiVersion");
            if (!Uri.TryCreate(String(input, "endpoint"), UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme != Uri.UriSchemeHttps || endpoint.Port != 443 || endpoint.UserInfo.Length != 0 ||
                endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
                !AzureHostSuffixes.Any(suffix => endpoint.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                throw new CliUsageException(
                    "'endpoint' must be an HTTPS Azure OpenAI endpoint on port 443 without user information, query, or fragment.");
            var path = endpoint.AbsolutePath.TrimEnd('/');
            var validShape = providerType switch
            {
                "openai" => path.Equals("/openai/v1", StringComparison.OrdinalIgnoreCase) && apiVersion is null,
                "azure" => path.Length == 0 && apiVersion is { Length: <= 32 } &&
                    apiVersion.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'),
                _ => false,
            };
            if (!validShape)
                throw new CliUsageException(
                    "Use providerType openai with an endpoint that ends in /openai/v1 and no azureApiVersion, " +
                    "or providerType azure with the resource root endpoint and an azureApiVersion.");

            var efforts = input["reasoningEfforts"] is JsonArray effortArray
                ? effortArray.Select(item => item is JsonValue value && value.TryGetValue<string>(out var text)
                    ? text : throw new CliUsageException("'reasoningEfforts' must contain strings.")).ToArray()
                : throw new CliUsageException("'reasoningEfforts' must be a JSON array.");
            if (efforts.Length == 0 || efforts.Any(effort => !Efforts.Contains(effort)) ||
                efforts.Distinct(StringComparer.Ordinal).Count() != efforts.Length)
                throw new CliUsageException(
                    "'reasoningEfforts' must list unique values from low, medium, high, and xhigh.");
            var defaultEffort = String(input, "defaultReasoningEffort");
            if (!efforts.Contains(defaultEffort))
                throw new CliUsageException("'defaultReasoningEffort' must be one of 'reasoningEfforts'.");
            int? capacity = input["promptCapacityTokens"] is null ? null
                : input["promptCapacityTokens"] is JsonValue capacityValue &&
                  capacityValue.TryGetValue<int>(out var tokens) && tokens > 0
                    ? tokens : throw new CliUsageException("'promptCapacityTokens' must be a positive integer.");

            var credentialNode = input["credentialReference"] as JsonObject
                ?? throw new CliUsageException("'credentialReference' must be an object with id and version.");
            RequireFields(credentialNode, ["id", "version"], "credentialReference");
            var secretId = String(credentialNode, "id");
            var secretVersion = String(credentialNode, "version");
            if (secretId.Length > 127 ||
                !secretId.All(character => char.IsAsciiLetterOrDigit(character) || character == '-') ||
                secretVersion.Length != 32 || !secretVersion.All(char.IsAsciiHexDigit))
                throw new CliUsageException(
                    "'credentialReference' must name a Key Vault secret and its exact 32-character version.");

            return new ByokRequest(reference, deployment, providerType, endpoint, wireApi, apiVersion,
                Efforts.Where(efforts.Contains).ToArray(), defaultEffort, capacity, revision,
                new SecretRef(secretId, secretVersion), ReadPricing(input["pricing"] as JsonObject),
                ReadBudget(input["spendBudget"] as JsonObject));
        }

        public JsonObject AgentHostBinding()
        {
            var provider = new JsonObject
            {
                ["Type"] = ProviderType,
                ["BaseUrl"] = Endpoint.AbsoluteUri,
                ["WireApi"] = WireApi,
            };
            if (AzureApiVersion is not null)
                provider["AzureApiVersion"] = AzureApiVersion;
            var binding = new JsonObject
            {
                ["ModelId"] = Deployment,
                ["SourceMode"] = "Byok",
                ["Provider"] = provider,
                ["SupportedReasoningEfforts"] = new JsonArray(ReasoningEfforts.Select(item => (JsonNode)item).ToArray()),
            };
            if (PromptCapacityTokens is { } capacity)
                binding["PromptCapacityTokens"] = capacity;
            return binding;
        }

        public IReadOnlyList<KeyValuePair<string, string>> EventsBinding()
        {
            var prefix = $"{BindingPrefix}{Reference}__";
            var keys = new List<KeyValuePair<string, string>>
            {
                new(prefix + "ModelId", Deployment),
                new(prefix + "SourceMode", "Byok"),
                new(prefix + "Provider__Type", ProviderType),
                new(prefix + "Provider__BaseUrl", Endpoint.AbsoluteUri),
                new(prefix + "Provider__WireApi", WireApi),
            };
            if (AzureApiVersion is not null)
                keys.Add(new(prefix + "Provider__AzureApiVersion", AzureApiVersion));
            if (PromptCapacityTokens is { } capacity)
                keys.Add(new(prefix + "PromptCapacityTokens", capacity.ToString(CultureInfo.InvariantCulture)));
            keys.AddRange(ReasoningEfforts.Select((effort, index) =>
                new KeyValuePair<string, string>($"{prefix}SupportedReasoningEfforts__{index}", effort)));
            return keys;
        }

        public IReadOnlyList<KeyValuePair<string, string>> ProjectsBinding()
        {
            var prefix = $"{BindingPrefix}{Reference}__";
            return new KeyValuePair<string, string>[] { new(prefix + "SourceMode", "Byok"), new(prefix + "Enabled", "true") }
                .Concat(ReasoningEfforts.Select((effort, index) =>
                    new KeyValuePair<string, string>($"{prefix}SupportedReasoningEfforts__{index}", effort)))
                .ToArray();
        }

        public JsonObject SpendBudgetJson() => new()
        {
            ["budgetId"] = Budget.BudgetId,
            ["revision"] = Budget.Revision,
            ["scope"] = Budget.Scope,
            ["meterSource"] = Budget.MeterSource,
            ["unit"] = Budget.Unit,
            ["softLimit"] = null,
            ["hardLimit"] = Budget.HardLimit,
            ["reservePerDispatch"] = Budget.ReservePerDispatch,
        };

        private static ByokPricing ReadPricing(JsonObject? pricing)
        {
            var source = pricing?["source"] is JsonValue value && value.TryGetValue<string>(out var kind) ? kind : null;
            if (source == "azure-retail-prices")
            {
                RequireFields(pricing!, ["source", "region", "meterIds"], "pricing");
                var region = RequireRegion(String(pricing!, "region"), "'pricing.region'");
                var meters = pricing!["meterIds"] as JsonObject
                    ?? throw new CliUsageException("'pricing.meterIds' must be an object.");
                RequireFields(meters, ["input", "cachedInput", "output"], "pricing.meterIds");
                var meterIds = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var category in new[] { "input", "cachedInput", "output" })
                {
                    if (meters[category] is null && category == "cachedInput")
                        continue;
                    meterIds[category] = Guid.TryParse(String(meters, category), out var meter)
                        ? meter.ToString("D")
                        : throw new CliUsageException($"'pricing.meterIds.{category}' must be an Azure meter ID.");
                }
                return new ByokPricing(region, meterIds, null);
            }
            if (source == "manual")
            {
                RequireFields(pricing!, ["source", "inputUsdPer1M", "cachedInputUsdPer1M", "outputUsdPer1M",
                    "sourceUrl", "retrievedOn"], "pricing");
                if (!Uri.TryCreate(String(pricing!, "sourceUrl"), UriKind.Absolute, out var sourceUrl) ||
                    sourceUrl.Scheme != Uri.UriSchemeHttps)
                    throw new CliUsageException("'pricing.sourceUrl' must be an HTTPS URL.");
                if (!DateOnly.TryParseExact(String(pricing!, "retrievedOn"), "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var retrievedOn))
                    throw new CliUsageException("'pricing.retrievedOn' must be a date in yyyy-MM-dd form.");
                return new ByokPricing(null, null, new ResolvedPricing(
                    "azure-retail-prices.standard.manual-override",
                    retrievedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Price(pricing!, "inputUsdPer1M", required: true)!.Value,
                    Price(pricing!, "cachedInputUsdPer1M", required: false),
                    Price(pricing!, "outputUsdPer1M", required: true)!.Value,
                    sourceUrl.AbsoluteUri,
                    null));
            }
            throw new CliUsageException("'pricing.source' must be azure-retail-prices or manual.");
        }

        private static SpendBudget ReadBudget(JsonObject? budget)
        {
            if (budget is null)
                throw new CliUsageException("'spendBudget' must set budgetId, hardLimitUsd, and reservePerDispatchUsd.");
            RequireFields(budget, ["budgetId", "hardLimitUsd", "reservePerDispatchUsd"], "spendBudget");
            var value = new SpendBudget(
                Identifier(budget, "budgetId"), 1, SpendBudgetScopes.Run, SdkMeterSources.ByokTokens, "USD",
                null, Price(budget, "hardLimitUsd", required: true),
                Price(budget, "reservePerDispatchUsd", required: true)!.Value);
            try
            {
                SpendBudgetContract.Validate(value);
            }
            catch (ArgumentException exception)
            {
                throw new CliUsageException($"'spendBudget' is invalid: {exception.Message}");
            }
            return value;
        }

        private static decimal? Price(JsonObject node, string name, bool required) =>
            node[name] is null && !required ? null
                : node[name] is JsonValue value && value.TryGetValue<decimal>(out var amount) && amount > 0
                    ? amount : throw new CliUsageException($"'{name}' must be a positive number.");

        private static void RequireFields(JsonObject node, IEnumerable<string> allowed, string location)
        {
            var set = allowed.ToHashSet(StringComparer.Ordinal);
            var unexpected = node.Select(item => item.Key).FirstOrDefault(key => !set.Contains(key));
            if (unexpected is not null)
                throw new CliUsageException(
                    $"'{location}' has an unsupported field '{unexpected}'. Do not put an API key in the input. " +
                    "Store the key in Key Vault and use credentialReference.");
        }

        // Reference and deployment become ConfigMap key segments, where ':' and '__' are separators.
        private static string KeySegment(JsonObject node, string name)
        {
            var value = Identifier(node, name);
            return value.Contains(':') || value.Contains("__", StringComparison.Ordinal) ||
                value.StartsWith('_') || value.EndsWith('_')
                ? throw new CliUsageException(
                    $"'{name}' may contain only letters, digits, '.', '-', and single '_' characters inside the name.")
                : value;
        }

        private static string Identifier(JsonObject node, string name)
        {
            var value = String(node, name);
            return value.Length <= 256 &&
                value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':')
                ? value
                : throw new CliUsageException($"'{name}' may contain only letters, digits, '.', '_', '-', and ':'.");
        }

        private static string String(JsonObject node, string name) =>
            node[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0
                ? text
                : throw new CliUsageException($"'{name}' is required and must be a string.");

        private static string? OptionalString(JsonObject node, string name) =>
            node[name] is null ? null : String(node, name);
    }
}

/// <summary>
/// Computes the same model-binding configuration digest as the runtime binding resolver, so the plan
/// can pin the exact AgentHost and Events binding set without loading the service packages.
/// </summary>
internal static class ModelBindingHash
{
    internal sealed record Binding(
        string ModelId, ModelSourceMode SourceMode, bool Enabled, int? PromptCapacityTokens,
        string[] Efforts, string? ProviderType, string? BaseUrl, string? WireApi, string? AzureApiVersion);

    public static string FromAgentHost(JsonObject bindings) => Hash(bindings.ToDictionary(
        item => item.Key,
        item =>
        {
            var binding = item.Value!.AsObject();
            var provider = binding["Provider"] as JsonObject;
            if (provider?["Headers"] is JsonObject { Count: > 0 })
                throw new InvalidOperationException(
                    $"Model binding '{item.Key}' has provider headers. This command does not support them.");
            return new Binding(
                binding["ModelId"]!.GetValue<string>(),
                Enum.Parse<ModelSourceMode>(binding["SourceMode"]!.GetValue<string>()),
                binding["Enabled"]?.GetValue<bool>() ?? true,
                binding["PromptCapacityTokens"]?.GetValue<int>(),
                binding["SupportedReasoningEfforts"]?.AsArray().Select(effort => effort!.GetValue<string>()).ToArray() ?? [],
                provider?["Type"]?.GetValue<string>(),
                provider?["BaseUrl"]?.GetValue<string>(),
                provider is null ? null : provider["WireApi"]?.GetValue<string>() ?? "responses",
                provider?["AzureApiVersion"]?.GetValue<string>());
        }, StringComparer.Ordinal));

    public static string FromFlatKeys(JsonObject data)
    {
        const string prefix = "AgentHost__ModelBindings__";
        var values = data.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(item => item.Key[prefix.Length..], item => item.Value!.GetValue<string>(), StringComparer.Ordinal);
        var references = values.Keys.Select(key => key[..key.IndexOf("__", StringComparison.Ordinal)])
            .Distinct(StringComparer.Ordinal);
        return Hash(references.ToDictionary(reference => reference, reference =>
        {
            string? Value(string name) => values.GetValueOrDefault($"{reference}__{name}");
            var efforts = Enumerable.Range(0, values.Count)
                .Select(index => Value($"SupportedReasoningEfforts__{index}"))
                .TakeWhile(effort => effort is not null).OfType<string>().ToArray();
            var type = Value("Provider__Type");
            return new Binding(
                Value("ModelId") ?? throw new InvalidOperationException($"Events binding '{reference}' has no ModelId."),
                Enum.Parse<ModelSourceMode>(Value("SourceMode")
                    ?? throw new InvalidOperationException($"Events binding '{reference}' has no SourceMode.")),
                Value("Enabled") is not { } enabled || bool.Parse(enabled),
                Value("PromptCapacityTokens") is { } capacity ? int.Parse(capacity, CultureInfo.InvariantCulture) : null,
                efforts,
                type,
                Value("Provider__BaseUrl"),
                type is null ? null : Value("Provider__WireApi") ?? "responses",
                Value("Provider__AzureApiVersion"));
        }, StringComparer.Ordinal));
    }

    // Keep this shape equal to RuntimeModelBindingsResolver; a test compares both digests.
    public static string Hash(IReadOnlyDictionary<string, Binding> bindings)
    {
        var ordered = bindings.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
        var models = ordered.Select(item => new
        {
            Reference = item.Key,
            item.Value.ModelId,
            item.Value.SourceMode,
            item.Value.Enabled,
            Provider = item.Value.ProviderType is { } type ? new
            {
                Type = type,
                BaseUrl = new Uri(item.Value.BaseUrl!).AbsoluteUri,
                WireApi = item.Value.WireApi!,
                item.Value.AzureApiVersion,
                Headers = Array.Empty<KeyValuePair<string, string>>(),
            } : null,
        }).ToArray();
        var capacities = ordered.Where(item => item.Value.PromptCapacityTokens is not null)
            .Select(item => new { Reference = item.Key, item.Value.PromptCapacityTokens }).ToArray();
        var reasoningEfforts = ordered.Where(item => item.Value.Efforts.Length != 0)
            .Select(item => new
            {
                Reference = item.Key,
                SupportedReasoningEfforts = item.Value.Efforts.Order(StringComparer.Ordinal).ToArray(),
            }).ToArray();
        object configuration = (capacities.Length, reasoningEfforts.Length) switch
        {
            (0, 0) => models,
            (_, 0) => new { Models = models, PromptCapacities = capacities },
            (0, _) => new { Models = models, SupportedReasoningEfforts = reasoningEfforts },
            _ => new { Models = models, PromptCapacities = capacities, SupportedReasoningEfforts = reasoningEfforts },
        };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(configuration)));
    }
}
