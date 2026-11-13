using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Orchestrator;

internal static class MafExecutionDispatchRequestBuilder
{
    internal static RuntimeA2ASendRequest Build(
        RuntimeRegistration registration,
        RuntimeHostReadinessReceipt readiness,
        Guid messageId,
        string prompt,
        string workPlanId,
        string associationId,
        string? workItemBindingSha256)
    {
        var proof = new RuntimeHostSessionProof(
            1,
            registration,
            readiness.SourceGrant.GrantId,
            readiness.SourceGrant.Revision,
            RuntimeCredentialPurpose.Observe);
        return new RuntimeA2ASendRequest(new RuntimeA2AMessage(
            "message",
            messageId,
            RuntimeContractValidation.NativeSessionId(registration.Binding),
            "user",
            [new("text", prompt)],
            new(proof, AddressedMessageDeliveryMode.Enqueue)
            {
                ChildDispatch = new(1, workPlanId, associationId)
                {
                    WorkItemBindingSha256 = workItemBindingSha256
                }
            }));
    }
}
