using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Domain;
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

/// <summary>Worker-side state of the explicit pod publication envelope.</summary>
public enum PreparedWritebackEnvelopeStatus
{
    NotRequired = 0,
    Missing = 1,
    Invalid = 2,
    Valid = 3,
}

/// <summary>One-shot publication-envelope result consumed after a remote agent turn.</summary>
public sealed record PreparedWritebackEnvelope(
    PreparedWritebackEnvelopeStatus Status,
    PreparedWriteback? Writeback = null);

/// <summary>Optional turn-agent seam used by the worker to retrieve pod publication state.</summary>
public interface IPreparedWritebackSource
{
    PreparedWritebackEnvelope TakePreparedWritebackEnvelope();
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
        if (!IsWritebackContent(content))
            return null;

        var data = content.Data;
        if (data.IsEmpty)
            return null;

        try
        {
            var writeback = JsonSerializer.Deserialize(
                data.Span,
                PreparedWritebackJsonContext.Default.PreparedWriteback);
            return IsValid(writeback) ? writeback : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool IsWritebackContent(DataContent content) =>
        string.Equals(content.MediaType, MediaType, StringComparison.OrdinalIgnoreCase);

    public static PreparedWritebackEnvelope DecodeEnvelope(DataContent content)
    {
        var writeback = TryDecode(content);
        return writeback is null
            ? new PreparedWritebackEnvelope(PreparedWritebackEnvelopeStatus.Invalid)
            : new PreparedWritebackEnvelope(PreparedWritebackEnvelopeStatus.Valid, writeback);
    }

    private static bool IsValid(PreparedWriteback? writeback)
    {
        if (writeback is null
            || string.IsNullOrWhiteSpace(writeback.RunId)
            || string.IsNullOrWhiteSpace(writeback.SourceRef)
            || !PodLocalExecutionWorkspace.IsGitObjectId(writeback.BaseCommitSha)
            || !PodLocalExecutionWorkspace.IsGitObjectId(writeback.ResultCommitSha)
            || !PodLocalExecutionWorkspace.IsGitObjectId(writeback.ResultTreeSha)
            || writeback.ChangedPathCount < 0)
        {
            return false;
        }

        if (writeback.HasChanges)
        {
            return writeback.WritebackRef?.StartsWith(
                    PodLocalExecutionWorkspace.WritebackRefPrefix,
                    StringComparison.Ordinal) == true
                && !string.Equals(
                    writeback.ResultCommitSha,
                    writeback.BaseCommitSha,
                    StringComparison.OrdinalIgnoreCase);
        }

        return writeback.WritebackRef is null
            && string.Equals(
                writeback.ResultCommitSha,
                writeback.BaseCommitSha,
                StringComparison.OrdinalIgnoreCase);
    }
}

[JsonSerializable(typeof(PreparedWriteback))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class PreparedWritebackJsonContext : JsonSerializerContext;
