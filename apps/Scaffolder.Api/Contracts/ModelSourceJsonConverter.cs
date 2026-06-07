using System.Text.Json;
using System.Text.Json.Serialization;
using Scaffolder.Domain;

namespace Scaffolder.Api.Contracts;

/// <summary>
/// Serializes <see cref="ModelSource"/> using its stable API string form
/// ("github-copilot" / "microsoft-foundry") rather than the CLR enum name, so
/// event payloads and API responses stay consistent with the request contract.
/// </summary>
public sealed class ModelSourceJsonConverter : JsonConverter<ModelSource>
{
    public override ModelSource Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString() ?? throw new JsonException("model source must be a non-null string");
        return ModelSourceExtensions.FromApiString(value);
    }

    public override void Write(Utf8JsonWriter writer, ModelSource value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToApiString());
    }
}
