using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentweaver.Api.Contracts;

/// <summary>
/// Shared serialization settings used for event payloads and API responses.
/// Enum values for <see cref="Agentweaver.Domain.ModelSource"/> use the API
/// string form; all other enums use their camelCase name. No naming policy is
/// applied to object members, so each contract controls its own field casing
/// through explicit names.
/// </summary>
public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = Build();

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        options.Converters.Add(new ModelSourceJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
