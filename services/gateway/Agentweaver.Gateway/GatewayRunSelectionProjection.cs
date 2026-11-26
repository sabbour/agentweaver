using System.Buffers;
using System.Text.Json;

namespace Agentweaver.Gateway;

/// <summary>Identifies a run-selection owner payload that violates the Gateway's public projection contract.</summary>
internal sealed class GatewayRunSelectionProjectionException : Exception
{
    public GatewayRunSelectionProjectionException(string code) : base(code) => Code = code;

    public string Code { get; }
}

/// <summary>Projects public run-selection responses to omit BYOK credential references and versions.</summary>
internal static class GatewayRunSelectionProjection
{
    private const string ContractInvalidCode = "run_selection_owner_contract_invalid";

    /// <summary>Validates run-selection JSON and removes private credential references and versions.</summary>
    public static byte[] Project(ReadOnlyMemory<byte> utf8Json)
    {
        try
        {
            using var document = JsonDocument.Parse(
                utf8Json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw InvalidProjection();

            var buffer = new ArrayBufferWriter<byte>(utf8Json.Length);
            using var writer = new Utf8JsonWriter(buffer);
            var hasModelSelection = false;
            var hasModelSelections = false;
            var hasProjectConfiguration = false;

            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                if (IsName(property.Name, "modelSelection"))
                {
                    hasModelSelection = true;
                    writer.WritePropertyName(property.Name);
                    WriteModelSelection(writer, property.Value, allowNull: false);
                }
                else if (IsName(property.Name, "modelSelections"))
                {
                    hasModelSelections = true;
                    writer.WritePropertyName(property.Name);
                    WriteEffectiveModelSelections(writer, property.Value);
                }
                else if (IsName(property.Name, "projectConfiguration"))
                {
                    hasProjectConfiguration = true;
                    writer.WritePropertyName(property.Name);
                    WriteProjectConfiguration(writer, property.Value);
                }
                else
                {
                    property.WriteTo(writer);
                }
            }
            writer.WriteEndObject();

            if (!hasModelSelection || !hasModelSelections || !hasProjectConfiguration)
                throw InvalidProjection();

            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw InvalidProjection();
        }
    }

    private static void WriteEffectiveModelSelections(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw InvalidProjection();

        writer.WriteStartArray();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw InvalidProjection();

            var hasSelection = false;
            writer.WriteStartObject();
            foreach (var property in item.EnumerateObject())
            {
                if (IsName(property.Name, "selection"))
                {
                    hasSelection = true;
                    writer.WritePropertyName(property.Name);
                    WriteModelSelection(writer, property.Value, allowNull: false);
                }
                else
                {
                    property.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
            if (!hasSelection)
                throw InvalidProjection();
        }
        writer.WriteEndArray();
    }

    private static void WriteProjectConfiguration(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw InvalidProjection();

        writer.WriteStartObject();
        foreach (var property in element.EnumerateObject())
        {
            if (IsName(property.Name, "modelSelection"))
            {
                writer.WritePropertyName(property.Name);
                WriteModelSelection(writer, property.Value, allowNull: true);
            }
            else if (IsName(property.Name, "modelTaskOverrides") ||
                     IsName(property.Name, "modelRoleOverrides") ||
                     IsName(property.Name, "modelAgentOverrides"))
            {
                writer.WritePropertyName(property.Name);
                WriteSelectionOverrides(writer, property.Value);
            }
            else
            {
                property.WriteTo(writer);
            }
        }
        writer.WriteEndObject();
    }

    private static void WriteSelectionOverrides(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            writer.WriteNullValue();
            return;
        }
        if (element.ValueKind != JsonValueKind.Array)
            throw InvalidProjection();

        writer.WriteStartArray();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw InvalidProjection();

            writer.WriteStartObject();
            foreach (var property in item.EnumerateObject())
            {
                if (IsName(property.Name, "selection"))
                {
                    writer.WritePropertyName(property.Name);
                    WriteModelSelection(writer, property.Value, allowNull: true);
                }
                else
                {
                    property.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteModelSelection(
        Utf8JsonWriter writer, JsonElement element, bool allowNull)
    {
        if (element.ValueKind == JsonValueKind.Null && allowNull)
        {
            writer.WriteNullValue();
            return;
        }
        if (element.ValueKind != JsonValueKind.Object)
            throw InvalidProjection();

        writer.WriteStartObject();
        foreach (var property in element.EnumerateObject())
        {
            if (IsName(property.Name, "credentialReference"))
                continue;

            if (IsName(property.Name, "providerBindingRevision"))
            {
                writer.WritePropertyName(property.Name);
                WriteProviderBindingRevision(writer, property.Value);
            }
            else
            {
                property.WriteTo(writer);
            }
        }
        writer.WriteEndObject();
    }

    private static void WriteProviderBindingRevision(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            writer.WriteNullValue();
            return;
        }
        if (element.ValueKind != JsonValueKind.Object)
            throw InvalidProjection();

        writer.WriteStartObject();
        foreach (var property in element.EnumerateObject())
        {
            if (IsName(property.Name, "credentialReference") ||
                IsName(property.Name, "credentialVersion"))
                continue;

            property.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private static bool IsName(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static GatewayRunSelectionProjectionException InvalidProjection() =>
        new(ContractInvalidCode);
}
