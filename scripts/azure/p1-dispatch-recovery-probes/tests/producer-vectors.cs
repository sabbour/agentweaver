#:project ..\..\..\..\packages\Agentweaver.Identity\Agentweaver.Identity.csproj
#:property PublishAot=false
#:property RestorePackagesWithLockFile=false

using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Identity;

var hash = new string('b', 64);
var id = Guid.Parse("12345678-1234-1234-1234-123456789abc");
var vectors = new List<object>();
var denials = new List<RuntimeNumericLimitDenialReceipt>();
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
        denials.Add(receipt);
        vectors.Add(new
        {
            Receipt = receipt,
            CanonicalPayload = JsonSerializer.Serialize(receipt with { CanonicalPayloadHash = string.Empty })
        });
    }
}

var runtimeInstanceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
var attemptId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
var sourceGrantId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
var binding = new RuntimeBinding(
    "https://broker.test/", "dddddddd-dddd-dddd-dddd-dddddddddddd", "tenant", "project-a", "run-a",
    "root-a", "agent", "turn", 1, 1, 1, "context:1", new string('a', 64), 1,
    "environment", "placement-uid", 1, "hosted-profile",
    new Uri("https://runtime.test/configure"),
    new Uri("https://orchestrator.test/runtime/observations"))
{
    AgentId = RuntimeActionContract.CoordinatorRootStepId,
    MaxModelTurns = 3,
    EnvironmentCurrentFencingGeneration = 4,
    EnvironmentProviderFencingGeneration = 7
};
var registration = new RuntimeRegistration(
    runtimeInstanceId, 7, binding, RuntimeRegistrationState.Active,
    DateTimeOffset.Parse("2099-01-01T00:00:00+00:00"));
RuntimeContractValidation.Validate(registration);
var action = new RuntimeActionRequest(
    1, runtimeInstanceId, registration.Revision, binding.ExecutionFence,
    attemptId, "model.turn", hash)
{ DispatchId = attemptId };
var sendStateHash = RuntimeContractValidation.Hash("empty-send-state"u8);
var denial = RuntimeNumericLimitDenialReceipt.Seal(new(
    1, Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
    binding.ProjectId, binding.RunId, binding.SessionId, binding.SessionId,
    runtimeInstanceId, registration.Revision, registration.Revision,
    sourceGrantId, hash, attemptId, "1", binding.ExecutionFence,
    binding.AcceptedSelectionHash, 2, RuntimeNumericLimitKind.ModelTurns,
    binding.MaxModelTurns.Value, binding.MaxModelTurns.Value, attemptId,
    RuntimeActionContract.Hash(action), attemptId, null, null, null, null, null, null,
    sendStateHash, sendStateHash,
    DateTimeOffset.Parse("2026-10-27T20:00:00.1234567+00:00"), string.Empty));
var cursorHash = RuntimeContractValidation.Hash("unchanged-native-cursor"u8);
var companion = RuntimeNumericLimitNativeNoSendReceipt.Seal(new(
    1, RuntimeNumericLimitNativeNoSendIdentity.ReceiptId(
        binding.ProjectId, binding.RunId, attemptId, denial.ReceiptId),
    binding.ProjectId, binding.RunId, binding.SessionId, binding.SessionId,
    runtimeInstanceId, registration.Revision, binding.ExecutionFence,
    attemptId, attemptId, RuntimeActionContract.Hash(action), denial.ReceiptId,
    denial.CanonicalPayloadHash, RuntimeContractValidation.RegistrationHash(registration),
    hash, sourceGrantId, 4, hash, cursorHash, cursorHash, 0, true,
    DateTimeOffset.Parse("2026-10-28T13:00:00.1234567-07:00"), string.Empty));
RuntimeNumericLimitNativeNoSendContract.ValidatePair(companion, denial, registration, action);

var httpOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
httpOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
var readResults = new
{
    modelTurnsWithNull = new RuntimeNumericLimitDenialReadResult(denials[1], null),
    toolCallsWithNull = new RuntimeNumericLimitDenialReadResult(denials[3], null),
    modelTurnsWithNativeNoSend = new
    {
        Result = new RuntimeNumericLimitDenialReadResult(denial, companion),
        CanonicalPayload = JsonSerializer.Serialize(companion with { CanonicalPayloadHash = string.Empty })
    }
};
Console.WriteLine(JsonSerializer.Serialize(new { denialVectors = vectors, readResults }, httpOptions));
