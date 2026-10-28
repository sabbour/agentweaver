using System.Security.Cryptography;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

internal sealed record MafExecutionScribeIntent(
    SessionIdentity Identity,
    string WorkPlanId,
    string StepId,
    string AssociationId,
    string AgentId,
    CoordinationActor Actor,
    string IdempotencyKey,
    string Content);

internal sealed record MafScribeTerminalReceipt(
    string AssociationId, Guid RecordId, int Revision, Guid RevisionId);

internal static class MafScribeContract
{
    internal const int MaximumContentLength = 40_000;
    internal const string RecordType = "workflow-output";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record Output(
        string ProjectId, string RunId, string RootSessionId, string WorkPlanId,
        string AssociationId, string AgentId, string ChildSessionId, string Result);

    internal static MafExecutionScribeIntent CreateIntent(
        SessionIdentity identity, WorkPlanSnapshot plan, MafExecutionCheckpoint checkpoint,
        string stepId, string associationId, CoordinationActor actor)
    {
        if (checkpoint.WorkPlanId != plan.Plan.Id ||
            !plan.Workflow.Definition.Steps.Any(step =>
                step.Id == stepId && step.Mode == WorkflowStepMode.Platform &&
                step.PlatformGate == WorkflowPlatformGate.Scribe) ||
            !checkpoint.Results.TryGetValue(associationId, out var result))
            throw Conflict();

        var item = plan.Plan.Items.SingleOrDefault(item => item.Id == associationId);
        string agentId;
        if (item is not null && !checkpoint.FixedWorkAssociations.ContainsKey(associationId) &&
            checkpoint.Progress.WorkItems.GetValueOrDefault(associationId) == MafExecutionTaskStatus.Succeeded)
        {
            agentId = item.AgentId;
        }
        else if (item is null &&
            checkpoint.FixedWorkAssociations.TryGetValue(associationId, out var fixedWork) &&
            fixedWork.WorkPlanId == plan.Plan.Id &&
            fixedWork.WorkflowId == plan.Workflow.Definition.Id &&
            fixedWork.DefinitionRevision == plan.Workflow.Definition.Revision &&
            fixedWork.ChildSessionId == MafExecutionIds.CreateChildSessionId(
                identity, plan.Plan.Id, associationId) &&
            plan.Workflow.Definition.Steps.Any(step =>
                step.Id == fixedWork.StepId && step.Mode == WorkflowStepMode.Fixed &&
                step.FixedWork == fixedWork.Specification) &&
            checkpoint.Progress.FixedWorkItems.GetValueOrDefault(associationId) == MafExecutionTaskStatus.Succeeded)
        {
            agentId = fixedWork.AgentId;
        }
        else
        {
            throw Conflict();
        }

        var content = JsonSerializer.Serialize(new Output(
            identity.ProjectId, identity.RunId, identity.SessionId, plan.Plan.Id,
            associationId, agentId,
            MafExecutionIds.CreateChildSessionId(identity, plan.Plan.Id, associationId), result), JsonOptions);
        var intent = new MafExecutionScribeIntent(
            identity, plan.Plan.Id, stepId, associationId, agentId, actor,
            CreateKey(identity, plan.Plan.Id, associationId), content);
        ValidateIntent(intent);
        return intent;
    }

    internal static void ValidateIntent(MafExecutionScribeIntent intent)
    {
        if (intent.Actor is null ||
            new[] { intent.Identity.ProjectId, intent.Identity.RunId, intent.Identity.SessionId,
                intent.WorkPlanId, intent.StepId, intent.AssociationId, intent.AgentId,
                intent.Actor.Issuer, intent.Actor.Subject }.Any(string.IsNullOrWhiteSpace) ||
            intent.IdempotencyKey != CreateKey(intent.Identity, intent.WorkPlanId, intent.AssociationId) ||
            string.IsNullOrWhiteSpace(intent.Content))
            throw Conflict();
        if (intent.Content.Length > MaximumContentLength)
            throw new CoordinationException(
                "maf_execution_scribe_payload_limit_exceeded", StatusCodes.Status409Conflict);
        try
        {
            var output = JsonSerializer.Deserialize<Output>(intent.Content, JsonOptions);
            if (output is null || output.ProjectId != intent.Identity.ProjectId ||
                output.RunId != intent.Identity.RunId || output.RootSessionId != intent.Identity.SessionId ||
                output.WorkPlanId != intent.WorkPlanId || output.AssociationId != intent.AssociationId ||
                output.AgentId != intent.AgentId || output.Result is null ||
                output.ChildSessionId != MafExecutionIds.CreateChildSessionId(
                    intent.Identity, intent.WorkPlanId, intent.AssociationId) ||
                JsonSerializer.Serialize(output, JsonOptions) != intent.Content)
                throw Conflict();
        }
        catch (JsonException exception)
        {
            throw new CoordinationException(
                "maf_execution_scribe_intent_conflict", StatusCodes.Status409Conflict, exception);
        }
    }

    internal static void RequireReplay(
        MafExecutionScribeIntent persisted, MafExecutionScribeIntent expected, CoordinationActor actor)
    {
        ValidateIntent(persisted);
        ValidateIntent(expected);
        if (persisted != expected || persisted.Actor != actor)
            throw Conflict();
    }

    private static string CreateKey(SessionIdentity identity, string planId, string associationId) =>
        "scribe-" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new[] { identity.ProjectId, identity.RunId, planId, associationId })));

    private static CoordinationException Conflict() =>
        new("maf_execution_scribe_intent_conflict", StatusCodes.Status409Conflict);
}
