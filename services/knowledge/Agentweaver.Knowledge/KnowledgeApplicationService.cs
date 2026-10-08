using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;

namespace Agentweaver.Knowledge;

public sealed class KnowledgeApplicationService(
    ProjectsConfigClient projects,
    KnowledgeProviderBindingService bindings,
    AcceptedEffectRelay relay,
    IMemoryProvider memory,
    KnowledgeRuntimeOptions options,
    MemoryContextCompiler contextCompiler)
{
    public async Task<KnowledgeRecordWriteResult> CreateAsync(
        string projectId,
        string runId,
        string agentId,
        CreateKnowledgeRecordRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAgent(agentId);
        ValidateCreateRequest(request);
        var context = await ResolveProviderAsync(
            projectId, runId, ProjectAuthorizationPermission.WriteProjects, cancellationToken)
            .ConfigureAwait(false);
        var input = new KnowledgeRecordCreate(
            projectId,
            agentId,
            request.Kind,
            request.Type.Trim(),
            NormalizeOptional(request.Title),
            request.Content,
            NormalizeOptional(request.Rationale),
            request.Importance,
            request.Tags,
            runId,
            null,
            ActorFingerprint(context.Authority),
            request.Kind == KnowledgeRecordKind.Proposal ? "proposal_created" : "created");
        return await context.Provider.CreateAsync(input, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<KnowledgeRecordPage> SearchAsync(
        string projectId,
        string runId,
        string agentId,
        KnowledgeRecordKind? kind,
        string? query,
        bool includeInactive,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ValidateAgent(agentId);
        var context = await ResolveProviderAsync(
            projectId, runId, ProjectAuthorizationPermission.WriteProjects, cancellationToken)
            .ConfigureAwait(false);
        return await context.Provider.SearchAsync(
            new KnowledgeRecordQuery(projectId, agentId, kind, query, includeInactive, page, pageSize),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<KnowledgeRecord> ReadAsync(
        string projectId,
        string runId,
        string agentId,
        Guid recordId,
        CancellationToken cancellationToken)
    {
        ValidateAgent(agentId);
        var context = await ResolveProviderAsync(
            projectId, runId, ProjectAuthorizationPermission.WriteProjects, cancellationToken)
            .ConfigureAwait(false);
        var record = await context.Provider.ReadAsync(projectId, recordId, cancellationToken)
            .ConfigureAwait(false);
        return RequireAgentRecord(record, agentId);
    }

    public async Task<KnowledgeRecordRevisionPage> ReadRevisionsAsync(
        string projectId,
        string runId,
        string agentId,
        Guid recordId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ValidateAgent(agentId);
        var context = await ResolveProviderAsync(
            projectId, runId, ProjectAuthorizationPermission.WriteProjects, cancellationToken)
            .ConfigureAwait(false);
        _ = RequireAgentRecord(
            await context.Provider.ReadAsync(projectId, recordId, cancellationToken).ConfigureAwait(false),
            agentId);
        return await context.Provider.ReadRevisionsAsync(
            projectId, recordId, page, pageSize, cancellationToken).ConfigureAwait(false);
    }

    public async Task<KnowledgeRecordWriteResult> UpdateAsync(
        string projectId,
        string runId,
        string agentId,
        Guid recordId,
        UpdateKnowledgeRecordRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAgent(agentId);
        ValidateUpdateRequest(request);
        var context = await ResolveProviderAsync(
            projectId, runId, ProjectAuthorizationPermission.WriteProjects, cancellationToken)
            .ConfigureAwait(false);
        var existing = RequireAgentRecord(
            await context.Provider.ReadAsync(projectId, recordId, cancellationToken).ConfigureAwait(false),
            agentId);
        if (existing.Kind is not (KnowledgeRecordKind.Memory or KnowledgeRecordKind.SessionContext))
            throw new KnowledgeApiException(
                "record_update_not_supported",
                "Proposals change only through explicit promotion or rejection; decisions are immutable.",
                StatusCodes.Status409Conflict);
        return await context.Provider.UpdateAsync(
            new KnowledgeRecordUpdate(
                projectId,
                recordId,
                request.ExpectedRevision,
                request.Type.Trim(),
                NormalizeOptional(request.Title),
                request.Content,
                NormalizeOptional(request.Rationale),
                request.Importance,
                request.Tags,
                request.State,
                ActorFingerprint(context.Authority),
                "updated"),
            idempotencyKey,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<KnowledgeProposalPromotionResult> PromoteAsync(
        string projectId,
        string runId,
        string agentId,
        Guid proposalId,
        PromoteKnowledgeProposalRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAgent(agentId);
        var context = await ResolveProviderAsync(
            projectId,
            runId,
            ProjectAuthorizationPermission.WriteProjects,
            cancellationToken,
            requireExistingBinding: true)
            .ConfigureAwait(false);
        var proposal = RequireAgentRecord(
            await context.Provider.ReadAsync(projectId, proposalId, cancellationToken).ConfigureAwait(false),
            agentId);
        if (proposal.Kind != KnowledgeRecordKind.Proposal)
            throw new KnowledgeApiException(
                "proposal_not_found",
                "The requested proposal was not found for this project and agent.",
                StatusCodes.Status404NotFound);
        var permission = ProjectsConfigClient.GetEffectiveProjectPermission(
            context.Authority, projectId, ProjectAuthorizationPermission.WriteProjects);
        var result = await context.Provider.PromoteProposalAsync(
            projectId,
            runId,
            proposalId,
            request.ExpectedRevision,
            ActorFingerprint(context.Authority),
            new AcceptedEffectAuthorizationBounds(
                context.Authority.Issuer,
                context.Authority.ActorId,
                context.Authority.TenantId,
                context.Authority.BoundProjectId,
                context.Authority.BoundRunId,
                permission.ResourceType,
                permission.ResourceId,
                permission.RoleRevision,
                context.Authority.MembershipRevision,
                context.Selection.ProjectRevision,
                context.Selection.ProjectConfigurationRevision,
                context.Selection.ContextRevision),
            idempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (result.Status != KnowledgeWriteStatus.Updated || result.OutboxEventId is not { } receiptId)
            return result;

        var delivery = await relay.TryDeliverAsync(
            context.Provider, projectId, runId, receiptId, cancellationToken).ConfigureAwait(false);
        return result with
        {
            Delivery = delivery.Delivery,
            DeliveryCode = delivery.Code,
            RequiredAudienceSubject = delivery.RequiredAudienceSubject,
            RequiredAudience = delivery.RequiredAudience,
            DeliveryAcknowledgment = delivery.Acknowledgment
        };
    }

    public async Task<AcceptedEffectReceipt> ReadAcceptedEffectReceiptAsync(
        Guid receiptId,
        CancellationToken cancellationToken)
    {
        var receipt = await memory.ReadAcceptedEffectReceiptAsync(receiptId, cancellationToken)
            .ConfigureAwait(false);
        if (receipt is null)
            throw new KnowledgeApiException(
                "accepted_effect_receipt_not_found",
                "The accepted-effect receipt was not found.",
                StatusCodes.Status404NotFound);

        var authority = await projects.GetCurrentAuthorityAsync(
            receipt.ProjectId, receipt.RunId, cancellationToken).ConfigureAwait(false);
        ProjectsConfigClient.RequireProjectPermission(
            authority, receipt.ProjectId, ProjectAuthorizationPermission.WriteProjects);
        if (!string.Equals(authority.ActorId, receipt.Subject, StringComparison.Ordinal) ||
            !string.Equals(authority.Issuer, receipt.Issuer, StringComparison.Ordinal) ||
            !string.Equals(authority.TenantId, receipt.TenantId, StringComparison.Ordinal) ||
            !string.Equals(authority.BoundProjectId, receipt.BoundProjectId, StringComparison.Ordinal) ||
            !string.Equals(authority.BoundRunId, receipt.BoundRunId, StringComparison.Ordinal))
            throw new KnowledgeApiException(
                "accepted_effect_receipt_not_found",
                "The accepted-effect receipt was not found.",
                StatusCodes.Status404NotFound);
        return receipt;
    }

    public async Task<AcceptedEffectReceipt> ReadAcceptedEffectReceiptAsync(
        string projectId,
        string runId,
        Guid receiptId,
        CancellationToken cancellationToken)
    {
        var context = await ResolveProviderAsync(
            projectId,
            runId,
            ProjectAuthorizationPermission.WriteProjects,
            cancellationToken,
            requireExistingBinding: true).ConfigureAwait(false);
        var receipt = await context.Provider.ReadAcceptedEffectReceiptAsync(
            projectId, runId, receiptId, cancellationToken).ConfigureAwait(false);
        if (receipt is null)
            throw new KnowledgeApiException(
                "accepted_effect_receipt_not_found",
                "The accepted-effect receipt was not found.",
                StatusCodes.Status404NotFound);
        if (!string.Equals(receipt.ProjectId, projectId, StringComparison.Ordinal) ||
            !string.Equals(receipt.RunId, runId, StringComparison.Ordinal) ||
            !string.Equals(context.Authority.ActorId, receipt.Subject, StringComparison.Ordinal) ||
            !string.Equals(context.Authority.Issuer, receipt.Issuer, StringComparison.Ordinal) ||
            !string.Equals(context.Authority.TenantId, receipt.TenantId, StringComparison.Ordinal) ||
            !string.Equals(context.Authority.BoundProjectId, receipt.BoundProjectId, StringComparison.Ordinal) ||
            !string.Equals(context.Authority.BoundRunId, receipt.BoundRunId, StringComparison.Ordinal))
            throw new KnowledgeApiException(
                "accepted_effect_receipt_not_found",
                "The accepted-effect receipt was not found.",
                StatusCodes.Status404NotFound);
        return receipt;
    }

    public async Task<KnowledgeRecordWriteResult> RejectAsync(
        string projectId,
        string runId,
        string agentId,
        Guid proposalId,
        RejectKnowledgeProposalRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAgent(agentId);
        var context = await ResolveProviderAsync(
            projectId, runId, ProjectAuthorizationPermission.WriteProjects, cancellationToken)
            .ConfigureAwait(false);
        var proposal = RequireAgentRecord(
            await context.Provider.ReadAsync(projectId, proposalId, cancellationToken).ConfigureAwait(false),
            agentId);
        if (proposal.Kind != KnowledgeRecordKind.Proposal)
            throw new KnowledgeApiException(
                "proposal_not_found",
                "The requested proposal was not found for this project and agent.",
                StatusCodes.Status404NotFound);
        return await context.Provider.RejectProposalAsync(
            projectId,
            runId,
            proposalId,
            request.ExpectedRevision,
            ActorFingerprint(context.Authority),
            idempotencyKey,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<MemoryContextCompilation?> CompileContextAsync(
        string projectId,
        string runId,
        string agentId,
        string? relevanceText,
        int? maximumItems,
        int? maximumTokens,
        CancellationToken cancellationToken)
    {
        ValidateAgent(agentId);
        var context = await ResolveProviderAsync(
            projectId, runId, ProjectAuthorizationPermission.WriteProjects, cancellationToken)
            .ConfigureAwait(false);
        var allowedTokens = Math.Min(options.DefaultContextTokens, context.Selection.RunLimits.MaxPromptTokens);
        var itemLimit = maximumItems ?? options.DefaultContextItems;
        var tokenLimit = maximumTokens ?? allowedTokens;
        if (itemLimit < 1 || itemLimit > options.DefaultContextItems ||
            tokenLimit < 1 || tokenLimit > allowedTokens)
            throw new KnowledgeApiException(
                "context_budget_exceeded",
                "Requested context limits may only narrow the configured Knowledge budget and current project run limit.",
                StatusCodes.Status400BadRequest);
        var candidates = await context.Provider.ReadContextCandidatesAsync(
            projectId,
            agentId,
            runId,
            options.MaximumContextCandidates,
            cancellationToken).ConfigureAwait(false);
        return contextCompiler.Compile(
            candidates,
            projectId,
            agentId,
            runId,
            relevanceText,
            itemLimit,
            tokenLimit);
    }

    private async Task<AuthorizedProvider> ResolveProviderAsync(
        string projectId,
        string runId,
        ProjectAuthorizationPermission permission,
        CancellationToken cancellationToken,
        bool requireExistingBinding = false)
    {
        ValidateResourceIdentifier(projectId, nameof(projectId));
        ValidateResourceIdentifier(runId, nameof(runId));
        var authority = await projects.GetCurrentAuthorityAsync(projectId, runId, cancellationToken)
            .ConfigureAwait(false);
        ProjectsConfigClient.RequireProjectPermission(authority, projectId, permission);
        var provider = requireExistingBinding
            ? await bindings.ResolveExistingAndVerifyAsync(
                authority, projectId, runId, cancellationToken).ConfigureAwait(false)
            : await bindings.ResolveAndVerifyAsync(
                authority, projectId, runId, cancellationToken).ConfigureAwait(false);
        return new AuthorizedProvider(authority, provider.Provider, provider.Selection);
    }

    private static KnowledgeRecord RequireAgentRecord(KnowledgeRecord? record, string agentId)
    {
        if (record is null || !string.Equals(record.AgentId, agentId, StringComparison.Ordinal))
            throw new KnowledgeApiException(
                "record_not_found",
                "The requested Knowledge record was not found for this project and agent.",
                StatusCodes.Status404NotFound);
        return record;
    }

    private static void ValidateCreateRequest(CreateKnowledgeRecordRequest request)
    {
        if (!Enum.IsDefined(request.Kind) || request.Kind == KnowledgeRecordKind.Decision ||
            string.IsNullOrWhiteSpace(request.Type) || request.Type.Length > 64 ||
            request.Type.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(request.Content) || request.Content.Length > 40_000 ||
            request.Title?.Length > 512 || request.Rationale?.Length > 8_000 ||
            request.Importance is not ("low" or "medium" or "high") ||
            request.Tags.IsDefault || request.Tags.Length > 32 ||
            (request.Kind == KnowledgeRecordKind.Proposal && string.IsNullOrWhiteSpace(request.Title)))
            throw new KnowledgeApiException(
                "invalid_knowledge_record",
                "Record kind, type, title, content, importance, or tags are invalid.",
                StatusCodes.Status400BadRequest);
    }

    private static void ValidateUpdateRequest(UpdateKnowledgeRecordRequest request)
    {
        if (request.ExpectedRevision < 1 ||
            string.IsNullOrWhiteSpace(request.Type) || request.Type.Length > 64 ||
            request.Type.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(request.Content) || request.Content.Length > 40_000 ||
            request.Title?.Length > 512 || request.Rationale?.Length > 8_000 ||
            request.Importance is not ("low" or "medium" or "high") ||
            request.Tags.IsDefault || request.Tags.Length > 32 ||
            request.State is not (KnowledgeRecordState.Active or KnowledgeRecordState.Archived))
            throw new KnowledgeApiException(
                "invalid_knowledge_record",
                "Record update fields or expected revision are invalid.",
                StatusCodes.Status400BadRequest);
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ActorFingerprint(ProjectAuthorizationContextResponse authority) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{authority.Issuer}\n{authority.ActorId}"))).ToLowerInvariant();

    private static void ValidateAgent(string agentId) =>
        ValidateResourceIdentifier(agentId, nameof(agentId));

    private static void ValidateResourceIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new KnowledgeApiException(
                "invalid_identifier",
                $"The {name} value is invalid.",
                StatusCodes.Status400BadRequest);
    }

    private sealed record AuthorizedProvider(
        ProjectAuthorizationContextResponse Authority,
        IMemoryProvider Provider,
        ProjectRunSelectionResponse Selection);

    private sealed record ResolvedContext(
        ProjectAuthorizationContextResponse Authority,
        IMemoryProvider Provider,
        ProjectRunSelectionResponse Selection);
}
