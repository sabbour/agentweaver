using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentweaver.Abstractions;

/// <summary>Task categories used to select the model for a workflow step or runtime turn.</summary>
[JsonConverter(typeof(ModelTaskKindJsonConverter))]
public enum ModelTaskKind
{
    Planning,
    Review,
    Design,
    Security,
    CreativeWriting,
    Complex,
    CodeWriting,
    Straightforward
}

/// <summary>Serializes task categories using the camel-case form used by runtime contracts.</summary>
public sealed class ModelTaskKindJsonConverter : JsonStringEnumConverter<ModelTaskKind>
{
    /// <summary>Creates a converter with stable camel-case task names.</summary>
    public ModelTaskKindJsonConverter() : base(JsonNamingPolicy.CamelCase) { }
}

/// <summary>One accepted model reference and effort resolved for a task, role, and optional project agent.</summary>
public sealed record ModelTaskSelectionReference(
    ModelTaskKind TaskKind,
    string? RoleId,
    string? AgentId,
    string ModelSelectionReference,
    string ReasoningEffort = "high");

/// <summary>Bounds the accepted route matrix and distinct model choices in a run snapshot.</summary>
public static class ModelTaskSelectionLimits
{
    /// <summary>Allows up to 512 assignment scopes across the eight supported task categories.</summary>
    public const int MaximumRoutes = 4096;

    /// <summary>Keeps distinct accepted model choices within the existing eligibility bound.</summary>
    public const int MaximumAcceptedSelections = 256;
}
