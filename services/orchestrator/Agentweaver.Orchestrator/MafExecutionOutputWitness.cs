using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

internal static class MafExecutionOutputWitness
{
    internal static (
        MafBacklogOutputSetSnapshot OutputSet,
        ImmutableArray<string> MissingFixedAssociationIds) CreateCompletePlanOutputSet(
            WorkPlanSnapshot plan,
            MafExecutionCheckpointSnapshot? checkpoint,
            SessionIdentity root)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(root.ProjectId) ||
            string.IsNullOrWhiteSpace(root.RunId) ||
            string.IsNullOrWhiteSpace(root.SessionId) ||
            string.IsNullOrWhiteSpace(plan.Plan.Id) ||
            plan.Plan.Items.IsDefault ||
            plan.Workflow.Definition.Steps.IsDefault)
            throw InvalidOutputSet();

        var planId = plan.Plan.Id;
        var state = checkpoint?.State;
        if (checkpoint is not null)
        {
            MafExecutionCheckpointContract.ValidateState(checkpoint.State);
            if (checkpoint.Info.SessionId != root.SessionId ||
                !string.Equals(checkpoint.State.WorkPlanId, planId, StringComparison.Ordinal))
                throw InvalidOutputSet();
        }

        var associations = state?.FixedWorkAssociations ??
            ImmutableDictionary<string, MafExecutionFixedWorkAssociation>.Empty
                .WithComparers(StringComparer.Ordinal);
        var fixedSteps = plan.Workflow.Definition.Steps
            .Where(step => step.Mode == WorkflowStepMode.Fixed)
            .ToDictionary(step => step.Id, StringComparer.Ordinal);
        var seenAssociations = new HashSet<string>(StringComparer.Ordinal);
        var obligations = new List<MafBacklogOutputObligation>();
        var seenObligations = new HashSet<MafBacklogOutputObligation>();
        var missing = ImmutableArray.CreateBuilder<string>();

        foreach (var item in plan.Plan.Items)
            AddOutputs(item.Id, item.DeclaredOutputs);

        foreach (var step in plan.Workflow.Definition.Steps.Where(step => step.BuildTestCommand is not null))
        {
            var associationId = MafExecutionIds.CreateBuildTestAssociationId(root, planId, step.Id);
            if (plan.Plan.Items.Any(item => item.Id == associationId) || associations.ContainsKey(associationId))
                throw InvalidOutputSet();
            if (state is null || !state.BuildTestIntents.ContainsKey(step.Id))
                missing.Add(associationId);
            var receipt = state?.BuildTestReceipts.GetValueOrDefault(step.Id);
            AddOutputs(associationId, step.BuildTestCommand!.Outputs
                .Where(output => output.Required ||
                    receipt?.Outputs.Any(evidence => evidence.Name == output.Name && evidence.Exists) == true)
                .Select(output => output.RelativePath).ToImmutableArray());
        }

        foreach (var association in associations.Values)
        {
            if (!string.Equals(association.WorkPlanId, planId, StringComparison.Ordinal) ||
                !fixedSteps.TryGetValue(association.StepId, out var step) ||
                step.FixedWork is not { } expectedSpecification ||
                !string.Equals(
                    association.AssociationId,
                    MafExecutionIds.CreateFixedWorkAssociationId(
                        root, planId, step.Id, association.ActivationRevision),
                    StringComparison.Ordinal) ||
                !string.Equals(
                    association.ChildSessionId,
                    MafExecutionIds.CreateChildSessionId(root, planId, association.AssociationId),
                    StringComparison.Ordinal) ||
                !FixedWorkSpecificationsMatch(expectedSpecification, association.Specification) ||
                !seenAssociations.Add(association.AssociationId))
                throw InvalidOutputSet();
        }

        foreach (var step in fixedSteps.Values)
        {
            if (step.FixedWork is not { } specification)
                throw InvalidOutputSet();
            var selected = associations.Values
                .Where(association => string.Equals(association.StepId, step.Id, StringComparison.Ordinal))
                .OrderBy(association => association.ActivationRevision)
                .ToArray();
            if (selected.Length == 0)
            {
                var missingId = MafExecutionIds.CreateFixedWorkAssociationId(
                    root, planId, step.Id, activationRevision: 1);
                missing.Add(missingId);
                AddOutputs(missingId, specification.DeclaredOutputs);
                continue;
            }

            foreach (var association in selected)
                AddOutputs(association.AssociationId, specification.DeclaredOutputs);
        }

        var ordered = obligations
            .OrderBy(obligation => obligation.WorkItemId, StringComparer.Ordinal)
            .ThenBy(obligation => obligation.OutputPath, StringComparer.Ordinal)
            .ToImmutableArray();
        var bytes = SerializeOutputSet(planId, ordered);
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return (
            new MafBacklogOutputSetSnapshot(planId, digest, ordered),
            missing.Order(StringComparer.Ordinal).ToImmutableArray());

        void AddOutputs(string workItemId, ImmutableArray<string> paths)
        {
            if (string.IsNullOrWhiteSpace(workItemId) || paths.IsDefault)
                throw InvalidOutputSet();
            foreach (var path in paths)
            {
                if (!WorkflowDefinitionValidator.IsValidOutputPath(path))
                    throw new CoordinationException(
                        "maf_execution_output_path_invalid", StatusCodes.Status409Conflict);
                var obligation = new MafBacklogOutputObligation(workItemId, path);
                if (!seenObligations.Add(obligation))
                    throw new CoordinationException(
                        "maf_execution_output_obligation_duplicate", StatusCodes.Status409Conflict);
                obligations.Add(obligation);
            }
        }
    }

    internal static byte[] SerializeCanonicalOutputSet(MafBacklogOutputSetSnapshot outputSet)
    {
        ArgumentNullException.ThrowIfNull(outputSet);
        if (string.IsNullOrWhiteSpace(outputSet.WorkPlanId) ||
            outputSet.Obligations.IsDefault ||
            outputSet.Obligations.Any(obligation =>
                string.IsNullOrWhiteSpace(obligation.WorkItemId) ||
                !WorkflowDefinitionValidator.IsValidOutputPath(obligation.OutputPath)) ||
            outputSet.Obligations.Distinct().Count() != outputSet.Obligations.Length ||
            !outputSet.Obligations.SequenceEqual(outputSet.Obligations
                .OrderBy(obligation => obligation.WorkItemId, StringComparer.Ordinal)
                .ThenBy(obligation => obligation.OutputPath, StringComparer.Ordinal)))
            throw InvalidOutputSet();

        var bytes = SerializeOutputSet(outputSet.WorkPlanId, outputSet.Obligations);
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(outputSet.Digest, digest, StringComparison.Ordinal))
            throw InvalidOutputSet();
        return bytes;
    }

    private static byte[] SerializeOutputSet(
        string workPlanId,
        ImmutableArray<MafBacklogOutputObligation> obligations)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", 1);
            writer.WriteString("workPlanId", workPlanId);
            writer.WriteStartArray("obligations");
            foreach (var obligation in obligations)
            {
                writer.WriteStartObject();
                writer.WriteString("workItemId", obligation.WorkItemId);
                writer.WriteString("outputPath", obligation.OutputPath);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static bool FixedWorkSpecificationsMatch(
        FixedWorkSpecification expected,
        FixedWorkSpecification actual) =>
        expected.Title == actual.Title &&
        expected.Task == actual.Task &&
        expected.RoleId == actual.RoleId &&
        expected.Phase == actual.Phase &&
        expected.IsolationChoice == actual.IsolationChoice &&
        expected.DeclaredOutputs.SequenceEqual(actual.DeclaredOutputs, StringComparer.Ordinal);

    private static CoordinationException InvalidOutputSet() =>
        new("maf_execution_output_set_invalid", StatusCodes.Status409Conflict);
}
