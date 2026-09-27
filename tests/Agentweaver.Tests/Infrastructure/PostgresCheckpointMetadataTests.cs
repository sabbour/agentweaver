using System.Text.Json;
using Agentweaver.Api.Infrastructure.Ef;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace Agentweaver.Tests.Infrastructure;

public sealed class PostgresCheckpointMetadataTests
{
    [Fact]
    public async Task CoordinatorGate_JsonbOrderedCheckpoint_RestoresThroughMafAfterReload()
    {
        var root = Path.Combine(Path.GetTempPath(), $"checkpoint-metadata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var session = Guid.NewGuid().ToString();
            JsonElement payload;
            CheckpointInfo originalCheckpoint;
            using (var writer = new FileSystemJsonCheckpointStore(new DirectoryInfo(Path.Combine(root, "writer"))))
            {
                await using var run = await InProcessExecution.RunStreamingAsync(
                    BuildWorkflow(), "drafted spec", CheckpointManager.CreateJson(writer), session, CancellationToken.None);
                await foreach (var evt in run.WatchStreamAsync(CancellationToken.None))
                    if (evt is RequestInfoEvent)
                        break;
                run.LastCheckpoint.Should().NotBeNull();
                originalCheckpoint = run.LastCheckpoint!;
                payload = await writer.RetrieveCheckpointAsync(session, originalCheckpoint);
            }

            using (var fileReplica = new FileSystemJsonCheckpointStore(new DirectoryInfo(Path.Combine(root, "writer"))))
            {
                await using var fileRun = await InProcessExecution.ResumeStreamingAsync(
                    BuildWorkflow(), originalCheckpoint, CheckpointManager.CreateJson(fileReplica), CancellationToken.None);
                fileRun.Should().NotBeNull("file-backed checkpoints already retain MAF metadata order");
            }

            using var jsonbOrdered = JsonDocument.Parse(SortObjectKeysLikeJsonb(payload));
            var edge = jsonbOrdered.RootElement.GetProperty("workflow").GetProperty("edges")
                .GetProperty("coordinator-draft")[0];
            edge.EnumerateObject().First().Name.Should().NotBe("$type");
            edge.GetProperty("$type").ValueKind.Should().Be(JsonValueKind.Number);

            using var reader = new FileSystemJsonCheckpointStore(new DirectoryInfo(Path.Combine(root, "reader")));
            var normalized = PostgresJsonCheckpointStore.RestoreMetadataOrder(jsonbOrdered.RootElement);
            normalized.GetProperty("workflow").GetProperty("edges")
                .GetProperty("coordinator-draft")[0].EnumerateObject().First().Name.Should().Be("$type");
            JsonElement.DeepEquals(jsonbOrdered.RootElement, normalized).Should().BeTrue(
                "repair must change ordering only, not checkpoint content");
            var checkpoint = await reader.CreateCheckpointAsync(session, normalized);
            await using var resumed = await InProcessExecution.ResumeStreamingAsync(
                BuildWorkflow(), checkpoint, CheckpointManager.CreateJson(reader), CancellationToken.None);
            resumed.Should().NotBeNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RestoreMetadataOrder_KeepsReferenceMetadataBeforePolymorphismAndPreservesValues()
    {
        using var saved = JsonDocument.Parse(
            """{"state":{"value":[{"data":"unchanged","$type":2,"$id":"ref-1"},{"$ref":"ref-1"}]}}""");

        var restored = PostgresJsonCheckpointStore.RestoreMetadataOrder(saved.RootElement);
        var item = restored.GetProperty("state").GetProperty("value")[0];
        item.EnumerateObject().Select(p => p.Name).Should().Equal("$id", "$type", "data");
        JsonElement.DeepEquals(saved.RootElement, restored).Should().BeTrue();
    }

    private static Microsoft.Agents.AI.Workflows.Workflow BuildWorkflow()
    {
        ExecutorBinding draft = new FunctionExecutor<string, string>(
            "coordinator-draft", async (input, _, _) =>
            {
                await Task.Yield();
                return input;
            });
        ExecutorBinding gate = RequestPort.Create<string, string>("coordinator-confirmation-gate");
        return new WorkflowBuilder(draft).AddEdge(draft, gate).Build()!;
    }

    private static string SortObjectKeysLikeJsonb(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteSorted(element, writer);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteSorted(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                    .OrderBy(p => p.Name.Length).ThenBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteSorted(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var child in element.EnumerateArray())
                    WriteSorted(child, writer);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
