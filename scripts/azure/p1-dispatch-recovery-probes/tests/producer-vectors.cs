#:project ..\..\..\..\packages\Agentweaver.Identity\Agentweaver.Identity.csproj
#:property PublishAot=false
#:property RestorePackagesWithLockFile=false

using System.Text.Json;
using Agentweaver.Identity;

var hash = new string('b', 64);
var id = Guid.Parse("12345678-1234-1234-1234-123456789abc");
var vectors = new List<object>();
foreach (var kind in new[] { RuntimeNumericLimitKind.ModelTurns, RuntimeNumericLimitKind.ToolCalls })
{
    foreach (var checkpoint in new[] { false, true })
    {
        var receipt = RuntimeNumericLimitDenialReceipt.Seal(new(
            1, id, "project-a", "run-a", "root-a", "session-a", id, 7, 7, id, hash,
            id, checkpoint ? "rev \"quoted\" + <tag> & '\u00e9`\n\t\\\U0001F600" : "1",
            9, hash, 3, kind, 2, 2, id, hash, checkpoint ? id : null,
            checkpoint ? "checkpoint-a" : null, checkpoint ? 4 : null, checkpoint ? hash : null,
            checkpoint ? 5 : null, checkpoint ? "plan-a" : null, checkpoint ? "association-a" : null,
            hash, hash, DateTimeOffset.Parse(checkpoint
                ? "2026-10-27T13:00:00.1234567-07:00" : "2026-10-27T20:00:00+00:00"), string.Empty));
        vectors.Add(new
        {
            Receipt = receipt,
            CanonicalPayload = JsonSerializer.Serialize(receipt with { CanonicalPayloadHash = string.Empty })
        });
    }
}
Console.WriteLine(JsonSerializer.Serialize(vectors, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
