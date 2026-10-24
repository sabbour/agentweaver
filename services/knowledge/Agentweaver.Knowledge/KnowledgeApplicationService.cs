using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;

namespace Agentweaver.Knowledge;

/// <summary>Applies authorization, provider pinning, validation, and audit rules around Memory-provider operations.</summary>
public sealed class KnowledgeApplicationService(
    ProjectsConfigClient projects,
    KnowledgeProviderBindingService bindings,
    AcceptedEffectRelay relay,
    IMemoryProvider memory,
    KnowledgeRuntimeOptions options,
    MemoryContextCompiler contextCompiler)
{
    /// <summary>Creates a record after validating input and rechecking authority and provider binding.</summary>
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
            projectId, runId, ProjectAuthorizationPermission.AccessPrivateKnowledge, cancellationToken)
            .ConfigureAwait(false);
        await RevalidateProviderAsync(
            projectId, runId, context, ProjectAuthorizationPermission.AccessPrivateKnowledge, cancellationToken)
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

    /// <summary>Searches the selected provider and rechecks authority before returning private records.</summary>
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
            projectId, runId, ProjectAuthorizationPermission.AccessPrivateKnowledge, cancellationToken)
            .ConfigureAwait(false);
        var result = await context.Provider.SearchAsync(
            new KnowledgeRecordQuery(projectId, agentId, kind, query, includeInactive, page, pageSize),
            cancellationToken).ConfigureAwait(false);
        await RevalidateProviderAsync(
            projectId, runId, context, ProjectAuthorizationPermission.AccessPrivateKnowledge, cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    /// <summary>Searches project-wide Knowledge through existing, currently authorized run-provider bindings.</summary>
    public async Task<ProjectKnowledgeRecordPage> SearchProjectAsync(
        string projectId,
        KnowledgeRecordKind? kind,
        string? query,
        bool includeInactive,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ValidateResourceIdentifier(projectId, nameof(projectId));
        if ((kind is not null && !Enum.IsDefined(kind.Value)) ||
            query?.Length > 256 || query?.Any(char.IsControl) == true ||
            page is < 1 or > 100 || pageSize is < 1 or > 100)
            throw new KnowledgeApiException(
                "invalid_knowledge_search",
                "Knowledge search filters or paging values are invalid.",
                StatusCodes.Status400BadRequest);

        var authority = await projects.GetCurrentProjectAuthorityAsync(projectId, cancellationToken)
            .ConfigureAwait(false);
        ProjectsConfigClient.RequireProjectPermission(
            authority, projectId, ProjectAuthorizationPermission.AccessPrivateKnowledge);
        // Broad discovery still requires run-selection permission; agent history uses its narrower private read.
        ProjectsConfigClient.RequireRunSelectionPermission(authority, projectId);
        return await SearchAuthorizedProjectRunsAsync(
            projectId, null, kind, query, includeInactive, page, pageSize, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Reads recent private Memory records for one agent without exposing run identifiers.</summary>
    public async Task<ProjectAgentHistoryResponse> ReadProjectAgentHistoryAsync(
        string projectId,
        string agentId,
        CancellationToken cancellationToken)
    {
        ValidateResourceIdentifier(projectId, nameof(projectId));
        ValidateAgent(agentId);
        var authority = await projects.GetCurrentProjectAuthorityAsync(projectId, cancellationToken)
            .ConfigureAwait(false);
        ProjectsConfigClient.RequireProjectPermission(
            authority, projectId, ProjectAuthorizationPermission.AccessPrivateKnowledge);

        // The selected agent narrows the private read; no run IDs or other agents leave this owner.
        var page = await SearchAuthorizedProjectRunsAsync(
            projectId,
            agentId,
            KnowledgeRecordKind.Memory,
            query: null,
            includeInactive: true,
            page: 1,
            pageSize: ProjectAgentHistoryContract.MaximumItems,
            cancellationToken: cancellationToken,
            requirePrivateKnowledgePermission: true).ConfigureAwait(false);
        return new ProjectAgentHistoryResponse(
            projectId,
            agentId,
            page.Items.Select(item => new ProjectAgentHistoryEntry(
                    item.Record.RecordId,
                    item.Record.Type,
                    item.Record.Title,
                    item.Record.Content,
                    item.Record.UpdatedAt))
                .ToImmutableArray());
    }

    private async Task<ProjectKnowledgeRecordPage> SearchAuthorizedProjectRunsAsync(
        string projectId,
        string? agentId,
        KnowledgeRecordKind? kind,
        string? query,
        bool includeInactive,
        int page,
        int pageSize,
        CancellationToken cancellationToken,
        bool requirePrivateKnowledgePermission = false)
    {
        var runIds = await bindings.ListBoundRunIdsAsync(projectId, cancellationToken).ConfigureAwait(false);
        var resolvedRuns = new List<(string RunId, AuthorizedProvider Context)>(runIds.Length);
        foreach (var runId in runIds)
        {
            // ResolveExisting prevents discovery from creating a provider pin for a run with no prior Knowledge use.
            var context = await ResolveProviderAsync(
                projectId,
                runId,
                ProjectAuthorizationPermission.AccessPrivateKnowledge,
                cancellationToken,
                requireExistingBinding: true,
                requirePrivateKnowledgePermission: requirePrivateKnowledgePermission).ConfigureAwait(false);
            resolvedRuns.Add((runId, context));
        }

        // Several runs may share one provider resource; querying it once avoids duplicate records and counts.
        var sources = resolvedRuns
            .GroupBy(item => SearchSourceKey.For(item.Context.Binding))
            .Select(group => group.OrderBy(item => item.RunId, StringComparer.Ordinal).First())
            .ToArray();
        var offset = checked((page - 1) * pageSize);
        var lastIndex = checked(offset + pageSize);
        var results = new List<ProjectKnowledgeRecord>();
        long totalCount = 0;
        foreach (var source in sources)
        {
            var sourceRecords = new List<KnowledgeRecord>();
            var sourceTotalCount = 0;
            for (var sourcePage = 1; sourceRecords.Count < lastIndex; sourcePage++)
            {
                var sourceResult = await source.Context.Provider.SearchAsync(
                    new KnowledgeRecordQuery(
                        projectId, agentId, kind, query, includeInactive, sourcePage, pageSize),
                    cancellationToken).ConfigureAwait(false);
                if (sourcePage == 1)
                    sourceTotalCount = sourceResult.TotalCount;
                sourceRecords.AddRange(sourceResult.Items);
                if (sourceResult.Items.Length < pageSize ||
                    sourceRecords.Count >= sourceTotalCount)
                    break;
            }

            totalCount += sourceTotalCount;
            results.AddRange(sourceRecords
                .Select(record => new ProjectKnowledgeRecord(source.RunId, record)));
        }

        // Recheck every run, including shared-resource runs, so a revocation during search fails the whole response.
        foreach (var (runId, context) in resolvedRuns)
            await RevalidateProviderAsync(
                projectId,
                runId,
                context,
                ProjectAuthorizationPermission.AccessPrivateKnowledge,
                cancellationToken,
                requirePrivateKnowledgePermission).ConfigureAwait(false);

        if (totalCount > int.MaxValue)
            throw new KnowledgeProviderUnavailableException(
                "The project-wide Knowledge result count exceeds the supported response range.");
        // Merge every source window before paging so each page is one global ordered slice.
        var items = results
            .OrderByDescending(item => item.Record.UpdatedAt)
            .ThenBy(item => item.Record.RecordId)
            .ThenBy(item => item.Record.AgentId, StringComparer.Ordinal)
            .ThenBy(item => item.AccessRunId, StringComparer.Ordinal)
            .Skip(offset)
            .Take(pageSize)
            .ToImmutableArray();
        return new ProjectKnowledgeRecordPage(items, (int)totalCount, page, pageSize);
    }

    /// <summary>Reads one project record and returns not-found when it belongs to a different agent.</summary>
    public async Task<KnowledgeRecord> ReadAsync(
        string projectId,
        string runId,
        string agentId,
        Guid recordId,
        CancellationToken cancellationToken)
    {
        ValidateAgent(agentId);
        var context = await ResolveProviderAsync(
            projectId, runId, ProjectAuthorizationPermission.AccessPrivateKnowledge, cancellationToken)
            .ConfigureAwait(false);
        var record = await context.Provider.ReadAsync(projectId, recordId, cancellationToken)
            .ConfigureAwait(false);
        await RevalidateProviderAsync(
            projectId, runId, context, ProjectAuthorizationPermission.AccessPrivateKnowledge, cancellationToken)
            .ConfigureAwait(false);
        return RequireAgentRecord(record, agentId);
    }

    /// <summary>Checks record ownership before exposing its immutable revision history.</summary>
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
            projectId, runId, ProjectAuthorizationPermission.AccessPrivateKnowledge, cancellationToken)
            .ConfigureAwait(false);
        _ = RequireAgentRecord(
            await context.Provider.ReadAsync(projectId, recordId, cancellationToken).ConfigureAwait(false),
            agentId);
        var result = await context.Provider.ReadRevisionsAsync(
            projectId, recordId, page, pageSize, cancellationToken).ConfigureAwait(false);
        await RevalidateProviderAsync(
            projectId, runId, context, ProjectAuthorizationPermission.AccessPrivateKnowledge, cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    /// <summary>Updates a supported record kind using the caller's expected revision and idempotency key.</summary>
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
            projectId, runId, ProjectAuthorizationPermission.AccessPrivateKnowledge, cancellationToken)
            .ConfigureAwait(false);
        var existing = RequireAgentRecord(
            await context.Provider.ReadAsync(projectId, recordId, cancellationToken).ConfigureAwait(false),
            agentId);
        if (existing.Kind is not (KnowledgeRecordKind.Memory or KnowledgeRecordKind.SessionContext or
                KnowledgeRecordKind.Decision))
            throw new KnowledgeApiException(
                "record_update_not_supported",
                "Proposals change only through explicit promotion or rejection.",
                StatusCodes.Status409Conflict);
        if (existing.Kind != KnowledgeRecordKind.Decision &&
            (request.State == KnowledgeRecordState.Superseded || request.SupersededByRecordId is not null))
            throw new KnowledgeApiException(
                "invalid_replacement",
                "Only a Decision can be superseded by another Decision.",
                StatusCodes.Status409Conflict);
        await RevalidateProviderAsync(
            projectId, runId, context, ProjectAuthorizationPermission.AccessPrivateKnowledge, cancellationToken)
            .ConfigureAwait(false);
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
                NormalizeOptional(request.Reason),
                request.SupersededByRecordId),
            idempotencyKey,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Restores an eligible record from history as a new pending-trust revision.</summary>
    public async Task<KnowledgeRecordWriteResult> RestoreAsync(
        string projectId,
        string runId,
        string agentId,
        Guid recordId,
        RestoreKnowledgeRecordRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAgent(agentId);
        ValidateRestoreRequest(request);
        var context = await ResolveProviderAsync(
            projectId, runId, ProjectAuthorizationPermission.AccessPrivateKnowledge, cancellationToken)
            .ConfigureAwait(false);
        var existing = RequireAgentRecord(
            await context.Provider.ReadAsync(projectId, recordId, cancellationToken).ConfigureAwait(false),
            agentId);
        if (existing.Kind is not (KnowledgeRecordKind.Memory or KnowledgeRecordKind.Decision))
            throw new KnowledgeApiException(
                "record_restore_not_supported",
                "Only Memory and Decision records can be restored from revision history.",
                StatusCodes.Status409Conflict);
        await RevalidateProviderAsync(
            projectId, runId, context, ProjectAuthorizationPermission.AccessPrivateKnowledge, cancellationToken)
            .ConfigureAwait(false);
        return await context.Provider.RestoreAsync(
            new KnowledgeRecordRestore(
                projectId,
                recordId,
                request.ExpectedRevision,
                request.Revision,
                ActorFingerprint(context.Authority),
                NormalizeOptional(request.Reason)),
            idempotencyKey,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Approves an active Decision only after confirming ownership and current permission.</summary>
    public async Task<KnowledgeRecordWriteResult> ApproveDecisionAsync(
        string projectId,
        string runId,
        string agentId,
        Guid recordId,
        ApproveKnowledgeDecisionRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAgent(agentId);
        ValidateApprovalRequest(request);
        var context = await ResolveProviderAsync(
            projectId, runId, ProjectAuthorizationPermission.AccessPrivateKnowledge, cancellationToken)
            .ConfigureAwait(false);
        var existing = RequireAgentRecord(
            await context.Provider.ReadAsync(projectId, recordId, cancellationToken).ConfigureAwait(false),
            agentId);
        if (existing.Kind != KnowledgeRecordKind.Decision)
            throw new KnowledgeApiException(
                "decision_not_found",
                "The requested Decision was not found for this project and agent.",
                StatusCodes.Status404NotFound);
        await RevalidateProviderAsync(
            projectId, runId, context, ProjectAuthorizationPermission.AccessPrivateKnowledge, cancellationToken)
            .ConfigureAwait(false);
        return await context.Provider.ApproveDecisionAsync(
            new KnowledgeDecisionApproval(
                projectId,
                recordId,
                request.ExpectedRevision,
                ActorFingerprint(context.Authority),
                NormalizeOptional(request.Reason)),
            idempotencyKey,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Exports the authorized agent's records and immutable revision history.</summary>
    public async Task<KnowledgeRecordTransferBundle> ExportAsync(
        string projectId,
        string runId,
        string agentId,
        CancellationToken cancellationToken)
    {
        ValidateAgent(agentId);
        var context = await ResolveProviderAsync(
            projectId, runId, ProjectAuthorizationPermission.AccessPrivateKnowledge, cancellationToken)
            .ConfigureAwait(false);
        var bundle = await context.Provider.ExportAsync(projectId, agentId, cancellationToken)
            .ConfigureAwait(false);
        await RevalidateProviderAsync(
            projectId, runId, context, ProjectAuthorizationPermission.AccessPrivateKnowledge, cancellationToken)
            .ConfigureAwait(false);
        return bundle;
    }

    /// <summary>Imports only a validated transfer matching this route's project and agent.</summary>
    public async Task<KnowledgeRecordImportResult> ImportAsync(
        string projectId,
        string runId,
        string agentId,
        KnowledgeRecordTransferBundle request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAgent(agentId);
        if (!string.Equals(request.ProjectId, projectId, StringComparison.Ordinal) ||
            !string.Equals(request.AgentId, agentId, StringComparison.Ordinal))
            throw new KnowledgeApiException(
                "invalid_knowledge_transfer",
                "Knowledge transfer project and agent scope must match the authorized route.",
                StatusCodes.Status400BadRequest);
        var context = await ResolveProviderAsync(
            projectId, runId, ProjectAuthorizationPermission.AccessPrivateKnowledge, cancellationToken)
            .ConfigureAwait(false);
        await RevalidateProviderAsync(
            projectId, runId, context, ProjectAuthorizationPermission.AccessPrivateKnowledge, cancellationToken)
            .ConfigureAwait(false);
        return await context.Provider.ImportAsync(
            request,
            runId,
            ActorFingerprint(context.Authority),
            idempotencyKey,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Promotes a pending Proposal, records its accepted effect, and attempts ordered delivery.</summary>
    /// <remarks>A successful promotion remains committed if downstream delivery is pending.</remarks>
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
            ProjectAuthorizationPermission.AccessPrivateKnowledge,
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
        await RevalidateProviderAsync(
            projectId, runId, context, ProjectAuthorizationPermission.AccessPrivateKnowledge, cancellationToken)
            .ConfigureAwait(false);
        var permission = ProjectsConfigClient.GetEffectiveProjectPermission(
            context.Authority, projectId, ProjectAuthorizationPermission.AccessPrivateKnowledge);
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

    /// <summary>Reads a receipt by global ID only when current write authority matches its recorded caller bounds.</summary>
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

    /// <summary>Reads a receipt within the route's existing run binding and current write authority.</summary>
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
        await RevalidateProviderAsync(
            projectId, runId, context, ProjectAuthorizationPermission.WriteProjects, cancellationToken)
            .ConfigureAwait(false);
        return receipt;
    }

    /// <summary>Rejects a pending Proposal after checking its source run and the current caller authority.</summary>
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
            projectId, runId, ProjectAuthorizationPermission.AccessPrivateKnowledge, cancellationToken)
            .ConfigureAwait(false);
        var proposal = RequireAgentRecord(
            await context.Provider.ReadAsync(projectId, proposalId, cancellationToken).ConfigureAwait(false),
            agentId);
        if (proposal.Kind != KnowledgeRecordKind.Proposal)
            throw new KnowledgeApiException(
                "proposal_not_found",
                "The requested proposal was not found for this project and agent.",
                StatusCodes.Status404NotFound);
        await RevalidateProviderAsync(
            projectId, runId, context, ProjectAuthorizationPermission.AccessPrivateKnowledge, cancellationToken)
            .ConfigureAwait(false);
        return await context.Provider.RejectProposalAsync(
            projectId,
            runId,
            proposalId,
            request.ExpectedRevision,
            ActorFingerprint(context.Authority),
            idempotencyKey,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Compiles eligible context within both configured defaults and the run's current prompt limit.</summary>
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
            projectId, runId, ProjectAuthorizationPermission.AccessPrivateKnowledge, cancellationToken)
            .ConfigureAwait(false);
        var allowedTokens = Math.Min(
            options.DefaultContextTokens,
            context.Selection.RunLimits.MaxPromptTokens ?? options.DefaultContextTokens);
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
        var compilation = contextCompiler.Compile(
            candidates,
            projectId,
            agentId,
            runId,
            relevanceText,
            itemLimit,
            tokenLimit);
        await RevalidateProviderAsync(
            projectId, runId, context, ProjectAuthorizationPermission.AccessPrivateKnowledge, cancellationToken)
            .ConfigureAwait(false);
        return compilation;
    }

    private async Task<AuthorizedProvider> ResolveProviderAsync(
        string projectId,
        string runId,
        ProjectAuthorizationPermission permission,
        CancellationToken cancellationToken,
        bool requireExistingBinding = false,
        bool requirePrivateKnowledgePermission = false)
    {
        ValidateResourceIdentifier(projectId, nameof(projectId));
        ValidateResourceIdentifier(runId, nameof(runId));
        var authority = await projects.GetCurrentAuthorityAsync(projectId, runId, cancellationToken)
            .ConfigureAwait(false);
        ProjectsConfigClient.RequireProjectPermission(authority, projectId, permission);
        var provider = requirePrivateKnowledgePermission
            ? await bindings.ResolveExistingForPrivateKnowledgeAsync(
                authority, projectId, runId, cancellationToken).ConfigureAwait(false)
            : requireExistingBinding
                ? await bindings.ResolveExistingAndVerifyAsync(
                    authority, projectId, runId, cancellationToken).ConfigureAwait(false)
                : await bindings.ResolveAndVerifyAsync(
                    authority, projectId, runId, cancellationToken).ConfigureAwait(false);
        return new AuthorizedProvider(authority, provider.Provider, provider.Selection, provider.Binding);
    }

    /// <summary>Re-fetches caller authority and run selection to prevent stale authorization or provider use.</summary>
    private async Task RevalidateProviderAsync(
        string projectId,
        string runId,
        AuthorizedProvider expected,
        ProjectAuthorizationPermission permission,
        CancellationToken cancellationToken,
        bool requirePrivateKnowledgePermission = false)
    {
        var authority = await projects.GetCurrentAuthorityAsync(projectId, runId, cancellationToken)
            .ConfigureAwait(false);
        ProjectsConfigClient.RequireProjectPermission(
            authority, projectId, permission);
        if (!string.Equals(authority.Issuer, expected.Authority.Issuer, StringComparison.Ordinal) ||
            !string.Equals(authority.ActorId, expected.Authority.ActorId, StringComparison.Ordinal) ||
            !string.Equals(authority.TenantId, expected.Authority.TenantId, StringComparison.Ordinal) ||
            !string.Equals(authority.BoundProjectId, expected.Authority.BoundProjectId, StringComparison.Ordinal) ||
            !string.Equals(authority.BoundRunId, expected.Authority.BoundRunId, StringComparison.Ordinal))
            throw new KnowledgeApiException(
                "authority_changed",
                "The current Knowledge caller authority changed while the request was in progress.",
                StatusCodes.Status403Forbidden);

        var current = requirePrivateKnowledgePermission
            ? await bindings.ResolveExistingForPrivateKnowledgeAsync(
                authority, projectId, runId, cancellationToken).ConfigureAwait(false)
            : await bindings.ResolveExistingAndVerifyAsync(
                authority, projectId, runId, cancellationToken).ConfigureAwait(false);
        if (!SameProviderBinding(expected.Binding, current.Binding) ||
            !ReferenceEquals(expected.Provider, current.Provider) ||
            expected.Selection.ProjectRevision != current.Selection.ProjectRevision ||
            expected.Selection.ProjectConfigurationRevision != current.Selection.ProjectConfigurationRevision ||
            expected.Selection.PlatformRuntimeRevision != current.Selection.PlatformRuntimeRevision ||
            !string.Equals(
                expected.Selection.ContextRevision, current.Selection.ContextRevision, StringComparison.Ordinal))
            throw new KnowledgeApiException(
                "memory_provider_binding_conflict",
                "The Knowledge provider binding changed while the request was in progress.",
                StatusCodes.Status409Conflict);
    }

    /// <summary>Compares all pin fields that define a run's immutable provider/resource binding.</summary>
    private static bool SameProviderBinding(
        PinnedProviderBinding expected,
        PinnedProviderBinding current) =>
        string.Equals(expected.RunId, current.RunId, StringComparison.Ordinal) &&
        expected.Seam == current.Seam &&
        string.Equals(expected.ProviderId, current.ProviderId, StringComparison.Ordinal) &&
        expected.AdapterVersion == current.AdapterVersion &&
        expected.OptionsSchemaVersion == current.OptionsSchemaVersion &&
        string.Equals(expected.OptionsRevision, current.OptionsRevision, StringComparison.Ordinal) &&
        expected.Hosting == current.Hosting &&
        expected.Resource == current.Resource &&
        expected.NegotiatedCapabilities.SetEquals(current.NegotiatedCapabilities);

    /// <summary>Identifies provider bindings that point to the same searchable backing resource.</summary>
    private sealed record SearchSourceKey(ProviderResourceRef Resource)
    {
        // Legacy and current pins can negotiate different capabilities for one backing store.
        public static SearchSourceKey For(PinnedProviderBinding binding) => new(binding.Resource);
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
            request.Reason?.Length > 1_024 || request.Reason?.Any(char.IsControl) == true ||
            request.SupersededByRecordId == Guid.Empty ||
            request.Importance is not ("low" or "medium" or "high") ||
            request.Tags.IsDefault || request.Tags.Length > 32 ||
            request.State is not (KnowledgeRecordState.Active or KnowledgeRecordState.Archived or
                KnowledgeRecordState.Superseded) ||
            (request.State == KnowledgeRecordState.Superseded && request.SupersededByRecordId is null) ||
            (request.State != KnowledgeRecordState.Superseded && request.SupersededByRecordId is not null))
            throw new KnowledgeApiException(
                "invalid_knowledge_record",
                "Record update fields or expected revision are invalid.",
                StatusCodes.Status400BadRequest);
    }

    private static void ValidateRestoreRequest(RestoreKnowledgeRecordRequest request)
    {
        if (request.ExpectedRevision < 1 || request.Revision < 1 ||
            request.Reason?.Length > 1_024 || request.Reason?.Any(char.IsControl) == true)
            throw new KnowledgeApiException(
                "invalid_restore_revision",
                "A positive current revision, historical revision, and valid reason are required.",
                StatusCodes.Status400BadRequest);
    }

    private static void ValidateApprovalRequest(ApproveKnowledgeDecisionRequest request)
    {
        if (request.ExpectedRevision < 1 ||
            request.Reason?.Length > 1_024 || request.Reason?.Any(char.IsControl) == true)
            throw new KnowledgeApiException(
                "invalid_decision_revision",
                "A positive expected revision and valid reason are required.",
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
        ProjectRunSelectionResponse Selection,
        PinnedProviderBinding Binding);

    private sealed record ResolvedContext(
        ProjectAuthorizationContextResponse Authority,
        IMemoryProvider Provider,
        ProjectRunSelectionResponse Selection);
}
