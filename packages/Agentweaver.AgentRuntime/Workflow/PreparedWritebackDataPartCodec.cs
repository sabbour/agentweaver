using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Agentweaver.AgentRuntime.Workflow;

/// <summary>
/// Immutable result of a pod-local writable turn. A non-null write-back ref identifies the
/// platform-created commit that the API must fast-forward onto the authoritative run worktree.
/// </summary>
public sealed record PreparedWriteback(
    string RunId,
    string SourceRef,
    string? WritebackRef,
    string BaseCommitSha,
    string ResultCommitSha,
    string ResultTreeSha,
    int ChangedPathCount)
{
    public bool HasChanges => ChangedPathCount > 0;
}

/// <summary>Optional turn-agent seam used by the worker to retrieve pod-prepared write-back data.</summary>
public interface IPreparedWritebackSource
{
    PreparedWriteback? TakePreparedWriteback();
}

/// <summary>Encodes pod-local write-back descriptors as dedicated A2A data content.</summary>
public static class PreparedWritebackDataPartCodec
{
    public const string MediaType = "application/x-agentweaver-prepared-writeback+json";

    public static DataContent Encode(PreparedWriteback writeback)
    {
        ArgumentNullException.ThrowIfNull(writeback);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            writeback,
            PreparedWritebackJsonContext.Default.PreparedWriteback);
        return new DataContent(new ReadOnlyMemory<byte>(bytes), MediaType);
    }

    public static PreparedWriteback? TryDecode(DataContent content)
    {
        if (!string.Equals(content.MediaType, MediaType, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var data = content.Data;
        if (data.IsEmpty)
            return null;

        try
        {
            return JsonSerializer.Deserialize(
                data.Span,
                PreparedWritebackJsonContext.Default.PreparedWriteback);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

[JsonSerializable(typeof(PreparedWriteback))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class PreparedWritebackJsonContext : JsonSerializerContext;
