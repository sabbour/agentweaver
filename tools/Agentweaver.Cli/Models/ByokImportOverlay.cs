using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agentweaver.Abstractions;

namespace Agentweaver.Cli;

internal sealed record ImportBinding(
    string Reference, string Model, string Type, string BaseUrl, string WireApi,
    string? AzureApiVersion, string[] Efforts, int? Capacity)
{
    internal JsonObject Body(Guid operationId, Guid installationId) => new()
    {
        ["operationId"] = operationId.ToString("D"),
        ["expectedRevision"] = 0,
        ["targetInstallationId"] = installationId.ToString("D"),
        ["reference"] = Reference,
        ["name"] = Reference[..Math.Min(Reference.Length, 128)],
        ["type"] = Type,
        ["base_url"] = BaseUrl,
        ["model"] = Model,
        ["wire_api"] = WireApi,
        ["azure_api_version"] = AzureApiVersion,
        ["reasoning_efforts"] = new JsonArray(Efforts.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()),
        ["prompt_capacity_tokens"] = Capacity,
    };
}

internal static class ByokImportOverlay
{
    internal const int MaximumFileBytes = 4 * 1024 * 1024;
    private static readonly Regex Identifier = new(@"\A[A-Za-z0-9._:-]{1,256}\z", RegexOptions.CultureInvariant);
    private static readonly Regex Key = new(@"\A(?!_)(?!.*__)(?!.*_\z)[A-Za-z0-9._-]{1,256}\z", RegexOptions.CultureInvariant);
    private static readonly Regex Rate = new(@"\A(?:0|[1-9][0-9]*)(?:\.[0-9]*[1-9])?\z", RegexOptions.CultureInvariant);
    private static readonly Regex RateId = new(@"\A[A-Za-z0-9._-]{1,128}\z", RegexOptions.CultureInvariant);
    private static readonly Regex Date = new(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}\z", RegexOptions.CultureInvariant);
    private static readonly Regex ApiVersion = new(@"\A[A-Za-z0-9-]{1,32}\z", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ImportEfforts = new(["low", "medium", "high", "xhigh"], StringComparer.Ordinal);
    private static readonly HashSet<string> HostedEfforts = new(["none", "minimal", "low", "medium", "high", "xhigh"], StringComparer.Ordinal);
    private static readonly Guid Namespace = Guid.Parse("c3429bf3-99bf-5bb7-9b54-d04b4dded250");

    internal static bool ValidReference(string reference) => Key.IsMatch(reference);

    internal static IReadOnlyList<ImportBinding> Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumFileBytes ||
            bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
            throw Invalid();
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            CheckTree(document.RootElement);
            var root = document.RootElement;
            Fields(root, ["kind", "schemaVersion", "baseRevision", "baseModelBindings", "revision", "modelBindings", "rateCard", "modelRates"],
                ["kind", "schemaVersion", "baseRevision", "baseModelBindings", "revision", "modelBindings", "rateCard", "modelRates"]);
            if (Text(root, "kind") != "agentweaver.model-bindings-overlay" ||
                PositiveInteger(root.GetProperty("schemaVersion")) != 1)
                throw Invalid();
            var baseRevision = Text(root, "baseRevision");
            var revision = Text(root, "revision");
            if (!Identifier.IsMatch(baseRevision) || !Identifier.IsMatch(revision) || baseRevision == revision)
                throw Invalid();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var snapshot = root.GetProperty("baseModelBindings");
            Object(snapshot);
            foreach (var item in snapshot.EnumerateObject())
            {
                if (!Key.IsMatch(item.Name) || !seen.Add(item.Name)) throw Invalid();
                Fields(item.Value, ["ModelId", "SourceMode", "Provider", "Enabled", "PromptCapacityTokens", "SupportedReasoningEfforts"],
                    ["ModelId", "SourceMode"]);
                if (!Identifier.IsMatch(Text(item.Value, "ModelId")) || Text(item.Value, "SourceMode") != "HostedCopilot")
                    throw Invalid();
                if (item.Value.TryGetProperty("Provider", out var provider) && provider.ValueKind != JsonValueKind.Null)
                    throw Invalid();
                if (item.Value.TryGetProperty("Enabled", out var enabled) &&
                    enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
                if (item.Value.TryGetProperty("PromptCapacityTokens", out var capacity) && capacity.ValueKind != JsonValueKind.Null)
                    _ = PositiveInteger(capacity);
                if (item.Value.TryGetProperty("SupportedReasoningEfforts", out var efforts))
                    _ = ReadEfforts(efforts, HostedEfforts, false);
            }

            var rows = new List<ImportBinding>();
            var bindings = root.GetProperty("modelBindings");
            Object(bindings);
            foreach (var item in bindings.EnumerateObject())
            {
                if (!Key.IsMatch(item.Name) || !seen.Add(item.Name)) throw Invalid();
                var row = item.Value;
                Fields(row, ["ModelId", "SourceMode", "Provider", "SupportedReasoningEfforts", "PromptCapacityTokens"],
                    ["ModelId", "SourceMode", "Provider", "SupportedReasoningEfforts"]);
                var model = Text(row, "ModelId");
                if (!Key.IsMatch(model) || Text(row, "SourceMode") != "Byok") throw Invalid();
                var provider = row.GetProperty("Provider");
                Fields(provider, ["Type", "BaseUrl", "WireApi", "AzureApiVersion"], ["Type", "BaseUrl", "WireApi"]);
                var type = Text(provider, "Type");
                var url = Text(provider, "BaseUrl");
                var wire = Text(provider, "WireApi");
                var version = provider.TryGetProperty("AzureApiVersion", out _) ? Text(provider, "AzureApiVersion") : null;
                if (wire is not ("responses" or "completions") ||
                    !(type == "openai" && version is null || type == "azure" && version is not null && ApiVersion.IsMatch(version)) ||
                    url.Length > 2048 || !ModelProviderPublicUrl.IsSafe(url, type))
                    throw Invalid();
                var efforts = ReadEfforts(row.GetProperty("SupportedReasoningEfforts"), ImportEfforts, true);
                int? capacity = row.TryGetProperty("PromptCapacityTokens", out var cap) ? PositiveInteger(cap) : null;
                var binding = new ImportBinding(item.Name, model, type, url, wire, version, efforts, capacity);
                if (Encoding.UTF8.GetByteCount(binding.Body(Guid.Empty, Guid.Empty).ToJsonString()) > 32 * 1024)
                    throw Invalid();
                rows.Add(binding);
            }
            if (rows.Count == 0) throw Invalid();
            var card = root.GetProperty("rateCard");
            Fields(card, ["id", "version"], ["id", "version"]);
            if (!RateId.IsMatch(Text(card, "id")) || !Date.IsMatch(Text(card, "version"))) throw Invalid();
            var models = rows.Select(r => r.Model).ToHashSet(StringComparer.Ordinal);
            var rates = root.GetProperty("modelRates");
            Object(rates);
            foreach (var entry in rates.EnumerateObject())
            {
                if (!Key.IsMatch(entry.Name) || !models.Remove(entry.Name)) throw Invalid();
                Fields(entry.Value, ["input", "cachedInput", "output"], ["input", "output"]);
                foreach (var rate in entry.Value.EnumerateObject())
                {
                    if (rate.Value.ValueKind != JsonValueKind.String) throw Invalid();
                    var value = rate.Value.GetString()!;
                    if (!Rate.IsMatch(value) ||
                        !double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) ||
                        number <= 0) throw Invalid();
                }
            }
            if (models.Count != 0) throw Invalid();
            return rows.OrderBy(r => r.Reference, StringComparer.Ordinal).ToArray();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            throw Invalid();
        }
    }

    private static void CheckTree(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                CheckUnicode(property.Name);
                if (!names.Add(property.Name)) throw Invalid();
                CheckTree(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckTree(item);
        else if (value.ValueKind == JsonValueKind.String)
            CheckUnicode(value.GetString()!);
    }

    private static void CheckUnicode(string value)
    {
        for (var i = 0; i < value.Length; i++)
            if (char.IsSurrogate(value[i]) &&
                (!char.IsHighSurrogate(value[i]) || ++i >= value.Length || !char.IsLowSurrogate(value[i])))
                throw Invalid();
    }

    private static void Object(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
    }

    private static void Fields(JsonElement value, string[] allowed, string[] required)
    {
        Object(value);
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) throw Invalid();
        foreach (var name in required)
            if (!value.TryGetProperty(name, out _)) throw Invalid();
    }

    private static string Text(JsonElement value, string name)
    {
        var field = value.GetProperty(name);
        if (field.ValueKind != JsonValueKind.String) throw Invalid();
        return field.GetString()!;
    }

    private static string[] ReadEfforts(JsonElement value, HashSet<string> supported, bool nonempty)
    {
        if (value.ValueKind != JsonValueKind.Array) throw Invalid();
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text ||
                !supported.Contains(text) || !seen.Add(text)) throw Invalid();
            result.Add(text);
        }
        if (nonempty && result.Count == 0) throw Invalid();
        return result.ToArray();
    }

    private static int PositiveInteger(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number) throw Invalid();
        var text = value.GetRawText();
        if (text[0] == '-') throw Invalid();
        var exponentIndex = text.IndexOfAny(['e', 'E']);
        var mantissa = exponentIndex < 0 ? text : text[..exponentIndex];
        var exponent = 0;
        if (exponentIndex >= 0)
        {
            var position = exponentIndex + 1;
            var negative = text[position] == '-';
            if (text[position] is '+' or '-') position++;
            for (; position < text.Length; position++)
                exponent = (int)Math.Min(10_000_000L, (long)exponent * 10 + text[position] - '0');
            if (negative) exponent = -exponent;
        }
        var dot = mantissa.IndexOf('.');
        var before = dot < 0 ? mantissa.Length : dot;
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal);
        var first = 0;
        while (first < digits.Length && digits[first] == '0') first++;
        if (first == digits.Length) throw Invalid();
        var last = digits.Length - 1;
        while (digits[last] == '0') last--;
        var integerEnd = (long)before + exponent;
        if (integerEnd <= last || integerEnd - first > 10) throw Invalid();
        long result = 0;
        for (var i = first; i < integerEnd; i++)
            result = result * 10 + (i < digits.Length ? digits[i] - '0' : 0);
        if (result > int.MaxValue) throw Invalid();
        return (int)result;
    }

    internal static Guid OperationId(string issuer, string tenant, Guid installation, byte[] digest, string reference)
    {
        if (installation == Guid.Empty || digest.Length != 32 || string.IsNullOrEmpty(tenant) || !Key.IsMatch(reference))
            throw Invalid();
        using var buffer = new MemoryStream();
        buffer.Write(Namespace.ToByteArray(bigEndian: true));
        WriteText(buffer, new Uri(issuer, UriKind.Absolute).AbsoluteUri.TrimEnd('/'));
        WriteText(buffer, tenant);
        buffer.Write(installation.ToByteArray(bigEndian: true));
        buffer.Write(digest);
        WriteText(buffer, reference);
        var hash = SHA1.HashData(buffer.ToArray());
        hash[6] = (byte)((hash[6] & 0x0f) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    private static void WriteText(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
        stream.Write(length);
        stream.Write(bytes);
    }

    private static InvalidDataException Invalid() => new("The model bindings file is invalid.");
}
