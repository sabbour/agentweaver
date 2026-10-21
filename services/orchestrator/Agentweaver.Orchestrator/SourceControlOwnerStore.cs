using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Agentweaver.SourceControl;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Owns PostgreSQL persistence for immutable repository pins, merge intents, webhook delivery deduplication, and verified output captures.
/// </summary>
internal sealed class SourceControlOwnerStore(
    NpgsqlDataSource dataSource,
    string schema,
    ProviderCatalog providerCatalog,
    ProviderResolver providerResolver)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };
    private static readonly TimeSpan MergeLockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Validates a non-reserved lowercase PostgreSQL schema name before quoting it for fixed table names.
    /// </summary>
    private readonly string _schema = QuoteSchema(schema);
    private string Pins => $"{_schema}.source_control_repository_pins";
    private string Intents => $"{_schema}.source_control_merge_intents";
    private string Grants => $"{_schema}.executable_action_grants";
    private string OutputCaptures => $"{_schema}.source_control_output_captures";
    private string PullRequestRecords => $"{_schema}.source_control_pull_requests";

    /// <summary>
    /// Records a pull request that the pinned run created or reused, for the stored project pull request list.
    /// </summary>
    /// <returns>True when the row was written; false when migration 032 isn't applied yet.</returns>
    /// <remarks>
    /// The write is an upsert keyed by project, run, pin, and pull request number, so a retried create or
    /// reuse is idempotent. It refreshes the title, URL, state, branches, and commits. A row recorded as
    /// <c>created</c> stays <c>created</c>, and <c>recorded_at</c> never changes. State is <c>merged</c>
    /// when the provider reports a merge, and otherwise the provider's open or closed state.
    /// </remarks>
    public async Task<bool> RecordPullRequestAsync(
        SourceControlRepositoryPin pin,
        SourceControlPullRequest pullRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pin);
        ArgumentNullException.ThrowIfNull(pullRequest);
        var state = pullRequest.Merged ? "merged" : pullRequest.State.ToLowerInvariant();
        if (state is not ("open" or "closed" or "merged"))
            throw new ArgumentException("The pull request state can't be recorded.", nameof(pullRequest));
        var disposition = pullRequest.Disposition switch
        {
            SourceControlPullRequestDisposition.Created => "created",
            SourceControlPullRequestDisposition.Reused => "reused",
            _ => throw new ArgumentException("Only created or reused pull requests are recorded.", nameof(pullRequest))
        };

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (await SourceControlPullRequestRecords.ReadCoverageStartAsync(
                connection, _schema, cancellationToken).ConfigureAwait(false) is null)
            return false;
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {PullRequestRecords} (
                project_id, run_id, pin_id, provider_id, repository_owner, repository_name,
                pull_request_number, html_url, title, state, head_branch, head_sha, base_branch, base_sha,
                disposition, recorded_at, updated_at)
            VALUES (
                @project, @run, @pin, @provider, @owner, @name,
                @number, @url, @title, @state, @headBranch, @headSha, @baseBranch, @baseSha,
                @disposition, clock_timestamp(), clock_timestamp())
            ON CONFLICT (project_id, run_id, pin_id, pull_request_number) DO UPDATE SET
                html_url = EXCLUDED.html_url,
                title = EXCLUDED.title,
                state = EXCLUDED.state,
                head_branch = EXCLUDED.head_branch,
                head_sha = EXCLUDED.head_sha,
                base_branch = EXCLUDED.base_branch,
                base_sha = EXCLUDED.base_sha,
                disposition = CASE
                    WHEN {PullRequestRecords}.disposition = 'created' THEN 'created'
                    ELSE EXCLUDED.disposition
                END,
                updated_at = greatest(clock_timestamp(), {PullRequestRecords}.recorded_at)
            """, connection);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, pin.AcceptedRun.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, pin.AcceptedRun.RunId);
        command.Parameters.AddWithValue("pin", NpgsqlDbType.Varchar, pin.PinId);
        command.Parameters.AddWithValue("provider", NpgsqlDbType.Varchar, pin.ProviderBinding.ProviderId);
        command.Parameters.AddWithValue("owner", NpgsqlDbType.Varchar, pin.Repository.Owner);
        command.Parameters.AddWithValue("name", NpgsqlDbType.Varchar, pin.Repository.Name);
        command.Parameters.AddWithValue("number", NpgsqlDbType.Bigint, pullRequest.Number);
        command.Parameters.AddWithValue("url", NpgsqlDbType.Varchar, pullRequest.HtmlUrl.AbsoluteUri);
        command.Parameters.AddWithValue("title", NpgsqlDbType.Text, (object?)pullRequest.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("state", NpgsqlDbType.Varchar, state);
        command.Parameters.AddWithValue("headBranch", NpgsqlDbType.Varchar, pullRequest.HeadBranch);
        command.Parameters.AddWithValue("headSha", NpgsqlDbType.Varchar, pullRequest.HeadSha);
        command.Parameters.AddWithValue("baseBranch", NpgsqlDbType.Varchar, pullRequest.BaseBranch);
        command.Parameters.AddWithValue("baseSha", NpgsqlDbType.Varchar, pullRequest.BaseSha);
        command.Parameters.AddWithValue("disposition", NpgsqlDbType.Varchar, disposition);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Creates a stable bounded merge-intent identifier from project, run, session, and idempotency key without embedding the key.
    /// </summary>
    public static string CreateIntentId(SessionIdentity identity, string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            identity.ProjectId + "\0" + identity.RunId + "\0" + identity.SessionId + "\0" + idempotencyKey));
        return "source-merge-" + Convert.ToHexString(digest.AsSpan(0, 20)).ToLowerInvariant();
    }

    /// <summary>
    /// Derives the stable approval request identifier from its merge intent.
    /// </summary>
    public static string CreateApprovalRequestId(string intentId) =>
        "source-approval-" + intentId;

    /// <summary>
    /// Creates a stable pin identifier from the accepted project, run, and selection digest.
    /// </summary>
    public static string CreateRepositoryPinId(SourceControlAcceptedRunBinding acceptedRun) =>
        "source-pin-" +
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(acceptedRun.ProjectId + "\0" + acceptedRun.RunId + "\0" +
                                   acceptedRun.AcceptedSelectionHash)).AsSpan(0, 20))
            .ToLowerInvariant();

    /// <summary>
    /// Finds a prior intent only after validating the active owner binding; a record from another actor, selection, tenant, or fence is hidden as unavailable.
    /// </summary>
    public async Task<SourceControlMergeIntentSnapshot?> FindMergeIntentByIdempotencyKeyAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(idempotencyKey);
        var selectionHash = HashSelection(selection.Selection);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var owner = await ReadOwnerBindingAsync(
            connection,
            transaction: null,
            actor,
            identity,
            selection.Authorization.TenantId,
            selectionHash,
            forUpdate: false,
            cancellationToken).ConfigureAwait(false);
        if (owner.ExecutionState == "completed" || owner.SessionLifecycle != "active")
            throw new CoordinationException(
                "source_control_run_inactive", StatusCodes.Status409Conflict);

        var snapshot = await ReadIntentByIdempotencyKeyAsync(
            connection,
            transaction: null,
            identity,
            idempotencyKey,
            forUpdate: false,
            cancellationToken).ConfigureAwait(false);
        if (snapshot is not null &&
            (snapshot.AcceptedRun.Issuer != actor.Issuer ||
             snapshot.AcceptedRun.Subject != actor.Subject ||
             snapshot.AcceptedRun.TenantId != selection.Authorization.TenantId ||
             snapshot.AcceptedRun.AcceptedSelectionHash != selectionHash ||
             snapshot.AcceptedRun.Fence != owner.RunFence))
            throw new CoordinationException(
                "source_control_intent_unavailable", StatusCodes.Status404NotFound);
        return snapshot;
    }

    /// <summary>
    /// Validates the owner decision and immutable repository pin, inserts the intent in one PostgreSQL transaction, and rejects an idempotency key reused for different request data.
    /// </summary>
    public async Task<SourceControlMergeIntentSnapshot> PersistMergeIntentAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        SourceControlMergeIntentRequest intentRequest,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(intentRequest);
        ValidateIdempotencyKey(idempotencyKey);
        if (identity.ProjectId != selection.Selection.ProjectId ||
            identity.RunId != selection.Selection.RunId ||
            intentRequest.AcceptedRun.ProjectId != identity.ProjectId ||
            intentRequest.AcceptedRun.RunId != identity.RunId ||
            intentRequest.AcceptedRun.RootSessionId != identity.SessionId ||
            intentRequest.AcceptedRun.Issuer != actor.Issuer ||
            intentRequest.AcceptedRun.Subject != actor.Subject)
            throw new CoordinationException(
                "source_control_intent_binding_invalid", StatusCodes.Status403Forbidden);

        var selectionHash = HashSelection(selection.Selection);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var owner = await ReadOwnerBindingAsync(
            connection,
            transaction,
            actor,
            identity,
            selection.Authorization.TenantId,
            selectionHash,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false);
        EnsureCurrentBinding(owner, selection, intentRequest.AcceptedRun);
        if (owner.StateVersion != intentRequest.SourceStateVersion ||
            owner.DecisionState != "accepted")
            throw new CoordinationException(
                "source_control_intent_stale_decision", StatusCodes.Status409Conflict);

        var pin = intentRequest.RepositoryPin;
        await PersistRepositoryPinRowAsync(
            connection, transaction, identity, pin, cancellationToken).ConfigureAwait(false);

        var storedPin = await ReadPinAsync(
            connection,
            transaction,
            identity,
            selectionHash,
            cancellationToken).ConfigureAwait(false);
        if (storedPin is null || !SamePin(storedPin, pin))
            throw new CoordinationException(
                "source_control_pin_conflict", StatusCodes.Status409Conflict);

        var pullRequest = intentRequest.PullRequest;
        var intentJson = JsonSerializer.Serialize(new
        {
            intentRequest.IntentId,
            intentRequest.ApprovalRequestId,
            intentRequest.SourceStateVersion,
            intentRequest.WorkflowId,
            intentRequest.DefinitionRevision,
            intentRequest.WorkPlanId,
            intentRequest.AssemblyRequestId,
            intentRequest.WorkflowStepId,
            intentRequest.PullRequest.Number,
            intentRequest.PullRequest.HeadBranch,
            intentRequest.PullRequest.HeadSha,
            intentRequest.PullRequest.BaseBranch,
            intentRequest.PullRequest.BaseSha,
            intentRequest.Method,
            intentRequest.CreatedAt
        }, JsonOptions);
        await using (var insertIntent = new NpgsqlCommand($"""
            INSERT INTO {Intents}
                (project_id, run_id, intent_id, session_id, pin_id, issuer, actor_id, tenant_id,
                 accepted_selection_hash, project_revision, project_configuration_revision,
                 platform_runtime_revision, context_revision, execution_fence,
                 source_state_version, source_decision_id, source_request_id, idempotency_key,
                 workflow_id, definition_revision, work_plan_id, assembly_request_id,
                 workflow_step_id, approval_request_id, pull_request_number,
                 head_branch, head_sha, base_branch, base_sha, merge_method,
                 intent_request, intent_state, created_at)
            VALUES
                (@project, @run, @intent, @session, @pin, @issuer, @actor, @tenant,
                 @selectionHash, @projectRevision, @configurationRevision,
                 @platformRevision, @contextRevision, @fence,
                 @sourceStateVersion, @sourceDecision, @sourceRequest, @idempotency,
                 @workflow, @definitionRevision, @workPlan, @assemblyRequest,
                 @workflowStep, @approvalRequest, @pullRequest,
                 @headBranch, @headSha, @baseBranch, @baseSha, @mergeMethod,
                 @intentRequest, 'approval_pending', @createdAt)
            ON CONFLICT DO NOTHING
            """, connection, transaction))
        {
            AddScope(insertIntent, identity);
            insertIntent.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
            AddAcceptedRun(insertIntent, intentRequest.AcceptedRun);
            insertIntent.Parameters.AddWithValue("intent", NpgsqlDbType.Varchar, intentRequest.IntentId);
            insertIntent.Parameters.AddWithValue("pin", NpgsqlDbType.Varchar, pin.PinId);
            insertIntent.Parameters.AddWithValue(
                "sourceStateVersion", NpgsqlDbType.Bigint, intentRequest.SourceStateVersion);
            insertIntent.Parameters.AddWithValue("sourceDecision", NpgsqlDbType.Uuid, owner.DecisionId);
            insertIntent.Parameters.AddWithValue(
                "sourceRequest", NpgsqlDbType.Varchar, intentRequest.AssemblyRequestId);
            insertIntent.Parameters.AddWithValue("idempotency", NpgsqlDbType.Varchar, idempotencyKey);
            insertIntent.Parameters.AddWithValue("workflow", NpgsqlDbType.Varchar, intentRequest.WorkflowId);
            insertIntent.Parameters.AddWithValue(
                "definitionRevision", NpgsqlDbType.Varchar, intentRequest.DefinitionRevision);
            insertIntent.Parameters.AddWithValue("workPlan", NpgsqlDbType.Varchar, intentRequest.WorkPlanId);
            insertIntent.Parameters.AddWithValue(
                "assemblyRequest", NpgsqlDbType.Varchar, intentRequest.AssemblyRequestId);
            insertIntent.Parameters.AddWithValue(
                "workflowStep", NpgsqlDbType.Varchar, intentRequest.WorkflowStepId);
            insertIntent.Parameters.AddWithValue(
                "approvalRequest", NpgsqlDbType.Varchar, intentRequest.ApprovalRequestId);
            insertIntent.Parameters.AddWithValue("pullRequest", NpgsqlDbType.Bigint, pullRequest.Number);
            insertIntent.Parameters.AddWithValue(
                "headBranch", NpgsqlDbType.Varchar, pullRequest.HeadBranch);
            insertIntent.Parameters.AddWithValue("headSha", NpgsqlDbType.Varchar, pullRequest.HeadSha);
            insertIntent.Parameters.AddWithValue(
                "baseBranch", NpgsqlDbType.Varchar, pullRequest.BaseBranch);
            insertIntent.Parameters.AddWithValue("baseSha", NpgsqlDbType.Varchar, pullRequest.BaseSha);
            insertIntent.Parameters.AddWithValue(
                "mergeMethod", NpgsqlDbType.Varchar, ToDatabaseMergeMethod(intentRequest.Method));
            insertIntent.Parameters.AddWithValue("intentRequest", NpgsqlDbType.Jsonb, intentJson);
            insertIntent.Parameters.AddWithValue(
                "createdAt", NpgsqlDbType.TimestampTz, intentRequest.CreatedAt);
            await insertIntent.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var saved = await ReadIntentByIdempotencyKeyAsync(
            connection, transaction, identity, idempotencyKey, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        if (saved is null || !MatchesRequest(saved, actor, selection, intentRequest))
            throw new CoordinationException(
                "source_control_intent_idempotency_conflict", StatusCodes.Status409Conflict);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return saved;
    }

    /// <summary>
    /// Reads an intent for an active run and returns it only when actor, tenant, root session, selection digest, and fence match.
    /// </summary>
    public async Task<SourceControlMergeIntentSnapshot> ReadMergeIntentAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        string intentId,
        CancellationToken cancellationToken)
    {
        if (!IsIdentifier(intentId))
            throw new CoordinationException(
                "source_control_intent_invalid", StatusCodes.Status400BadRequest);
        var selectionHash = HashSelection(selection.Selection);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var owner = await ReadOwnerBindingAsync(
            connection,
            transaction: null,
            actor,
            identity,
            selection.Authorization.TenantId,
            selectionHash,
            forUpdate: false,
            cancellationToken).ConfigureAwait(false);
        if (owner.ExecutionState == "completed" || owner.SessionLifecycle != "active")
            throw new CoordinationException(
                "source_control_run_inactive", StatusCodes.Status409Conflict);

        var snapshot = await ReadIntentByIdAsync(
            connection, transaction: null, identity, intentId, forUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null ||
            snapshot.AcceptedRun.Issuer != actor.Issuer ||
            snapshot.AcceptedRun.Subject != actor.Subject ||
            snapshot.AcceptedRun.TenantId != selection.Authorization.TenantId ||
            snapshot.AcceptedRun.RootSessionId != identity.SessionId ||
            snapshot.AcceptedRun.AcceptedSelectionHash != selectionHash ||
            snapshot.AcceptedRun.Fence != owner.RunFence)
            throw new CoordinationException(
                "source_control_intent_unavailable", StatusCodes.Status404NotFound);
        return snapshot;
    }

    /// <summary>
    /// Reads and locks one merge intent using the caller-owned connection and transaction.
    /// </summary>
    internal async Task<SourceControlMergeIntentSnapshot?> ReadMergeIntentInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity identity,
        string intentId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
            throw new ArgumentException("The transaction must belong to the supplied connection.", nameof(transaction));
        if (!IsIdentifier(intentId))
            throw new CoordinationException(
                "source_control_intent_invalid", StatusCodes.Status400BadRequest);
        return await ReadIntentByIdAsync(
            connection, transaction, identity, intentId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Locks the current owner binding and merge intent, then verifies both saved run bindings before returning evidence to prerequisite evaluation.
    /// </summary>
    internal async Task<(SourceControlMergeIntentSnapshot? Intent, OwnerBindingSnapshot CurrentOwner)>
        ReadPrerequisiteMergeIntentInTransactionAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CoordinationActor actor,
            SessionIdentity identity,
            string tenantId,
            string acceptedSelectionHash,
            long executionFence,
            string intentId,
            CancellationToken cancellationToken)
    {
        if (!IsIdentifier(intentId))
            throw new CoordinationException(
                "source_control_intent_invalid", StatusCodes.Status400BadRequest);
        var owner = await ReadPrerequisiteOwnerBindingInTransactionAsync(
            connection, transaction, actor, identity, tenantId, acceptedSelectionHash,
            executionFence, cancellationToken).ConfigureAwait(false);
        var intent = await ReadIntentByIdAsync(
            connection, transaction, identity, intentId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        if (intent is null)
            return (null, owner);
        if (!MatchesPrerequisiteAcceptedRun(
                intent.AcceptedRun, actor, identity, tenantId, acceptedSelectionHash, executionFence) ||
            !MatchesPrerequisiteAcceptedRun(
                intent.Pin.AcceptedRun, actor, identity, tenantId, acceptedSelectionHash, executionFence))
            throw new CoordinationException(
                "source_control_intent_unavailable", StatusCodes.Status404NotFound);
        return (intent, owner);
    }

    /// <summary>
    /// Returns the repository pin for the current actor and selection, or a not-found error when no pin is available.
    /// </summary>
    public async Task<SourceControlRepositoryPin> ReadRepositoryPinAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        CancellationToken cancellationToken)
    {
        var pin = await FindRepositoryPinAsync(
            actor, identity, selection, cancellationToken).ConfigureAwait(false);
        return pin ?? throw new CoordinationException(
            "source_control_pin_unavailable", StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// Requires the current accepted decision, active owner rows, matching pin, source owner binding, and execution fence within the caller transaction.
    /// </summary>
    internal async Task RequireBuildTestInputInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        long stateVersion,
        long executionFence,
        SandboxBuildTestWorkspaceSource source,
        CancellationToken cancellationToken)
    {
        var selectionHash = HashSelection(selection.Selection);
        var owner = await ReadPrerequisiteOwnerBindingInTransactionAsync(
            connection, transaction, actor, identity, selection.Authorization.TenantId,
            selectionHash, executionFence, cancellationToken).ConfigureAwait(false);
        if (owner.StateVersion != stateVersion || owner.DecisionState != "accepted" ||
            owner.ExecutionState is not ("idle" or "active" or "blocked") || owner.SessionLifecycle != "active")
            throw new CoordinationException(
                "source_control_run_binding_changed", StatusCodes.Status409Conflict);
        var pin = await ReadPinAsync(
            connection, transaction, identity, selectionHash, cancellationToken).ConfigureAwait(false)
            ?? throw new CoordinationException(
                "source_control_pin_unavailable", StatusCodes.Status404NotFound);
        EnsureCurrentBinding(owner, selection, pin.AcceptedRun);
        SourceControlEndpoints.ValidateBuildTestSourceOwner(
            source, actor, identity, selection.Authorization.TenantId, selectionHash, pin, executionFence);
    }

    /// <summary>
    /// Reads a pin only for the active owner binding and validates its accepted run before returning it.
    /// </summary>
    public async Task<SourceControlRepositoryPin?> FindRepositoryPinAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        CancellationToken cancellationToken)
    {
        var selectionHash = HashSelection(selection.Selection);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var owner = await ReadOwnerBindingAsync(
            connection,
            transaction: null,
            actor,
            identity,
            selection.Authorization.TenantId,
            selectionHash,
            forUpdate: false,
            cancellationToken).ConfigureAwait(false);
        if (owner.ExecutionState == "completed" || owner.SessionLifecycle != "active")
            throw new CoordinationException(
                "source_control_run_inactive", StatusCodes.Status409Conflict);

        var pin = await ReadPinAsync(
            connection, transaction: null, identity, selectionHash, cancellationToken)
            .ConfigureAwait(false);
        if (pin is not null)
            EnsureCurrentBinding(owner, selection, pin.AcceptedRun);
        return pin;
    }

    /// <summary>
    /// Validates capture sizes, hashes, and owner bindings, then stores a pending capture in PostgreSQL; a workspace can have only one unresolved capture.
    /// </summary>
    internal async Task<SourceControlOutputCaptureRecord> RegisterOutputCaptureAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        SourceControlRepositoryPin pin,
        long expectedStateVersion,
        GitWorkspaceCaptureDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(pin);
        ArgumentNullException.ThrowIfNull(document);
        if (identity.ProjectId != selection.Selection.ProjectId ||
            identity.RunId != selection.Selection.RunId ||
            pin.AcceptedRun.ProjectId != identity.ProjectId ||
            pin.AcceptedRun.RunId != identity.RunId ||
            pin.AcceptedRun.RootSessionId != identity.SessionId ||
            pin.AcceptedRun.Issuer != actor.Issuer ||
            pin.AcceptedRun.Subject != actor.Subject ||
            document.Manifest.RunId != identity.RunId ||
            document.Manifest.RepositoryId != pin.ProviderBinding.Resource.ResourceId ||
            document.Manifest.ResourceGeneration != pin.ProviderBinding.Resource.Generation)
            throw new CoordinationException(
                "source_control_output_capture_binding_invalid", StatusCodes.Status403Forbidden);
        if (document.ManifestBytes.Length is 0 or > ProducedRunCaptureLimits.MaximumManifestBytes ||
            document.PatchBytes.Length > ProducedRunCaptureLimits.MaximumDiffBytes ||
            document.PackageBytes.Length is < ProducedRunCaptureLimits.PackageHeaderBytes or
                > ProducedRunCaptureLimits.MaximumPackageBytes ||
            GitWorkspaceCapturePackage.Hash(document.ManifestBytes) != document.ManifestSha256 ||
            GitWorkspaceCapturePackage.Hash(document.PatchBytes) != document.PatchSha256 ||
            GitWorkspaceCapturePackage.Hash(document.PackageBytes) != document.PackageSha256)
            throw new CoordinationException(
                "source_control_output_capture_integrity_invalid", StatusCodes.Status400BadRequest);

        var selectionHash = HashSelection(selection.Selection);
        var captureIdentity = ProducedRunCaptureContractValidation.CreateIdentity(
            identity,
            pin.PinId,
            selectionHash,
            document.Manifest.WorkspaceId,
            document.Manifest.WorkspaceIncarnationId,
            document.Manifest.BranchName,
            document.Manifest.RepositoryId,
            document.Manifest.ResourceGeneration,
            document.Manifest.BaseSha,
            document.Manifest.OutputTreeSha,
            document.ManifestSha256);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var owner = await ReadOwnerBindingAsync(
            connection,
            transaction,
            actor,
            identity,
            selection.Authorization.TenantId,
            selectionHash,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false);
        if (owner.ExecutionState is "failed" or "indeterminate" ||
            owner.ExecutionState is not ("idle" or "active" or "blocked"))
            throw new CoordinationException(
                "source_control_output_run_unavailable", StatusCodes.Status409Conflict);
        var storedPin = await ReadPinAsync(
            connection, transaction, identity, selectionHash, cancellationToken).ConfigureAwait(false);
        if (storedPin is null || !SamePin(storedPin, pin))
            throw new CoordinationException(
                "source_control_output_capture_pin_changed", StatusCodes.Status409Conflict);
        EnsureCurrentBinding(owner, selection, pin.AcceptedRun);
        if (owner.StateVersion != expectedStateVersion || owner.DecisionState != "accepted")
            throw new CoordinationException(
                "source_control_output_capture_authority_changed", StatusCodes.Status409Conflict);

        var manifest = document.Manifest;
        var unresolved = await ReadPendingOutputCaptureForWorkspaceAsync(
            connection,
            transaction,
            identity,
            manifest.WorkspaceId,
            selectionHash,
            cancellationToken).ConfigureAwait(false);
        if (unresolved is not null)
        {
            if (!CaptureMatchesDocument(unresolved, actor, selection, pin, document, captureIdentity))
                throw new CoordinationException(
                    "source_control_output_capture_pending_conflict", StatusCodes.Status409Conflict);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return unresolved;
        }

        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {OutputCaptures}
                (project_id, run_id, session_id, capture_id, event_id, pin_id, issuer, actor_id,
                 tenant_id, accepted_selection_hash, workspace_id, workspace_incarnation_id,
                 branch_name, repository_id, resource_generation, base_sha, output_tree_sha, manifest_sha256,
                 manifest_byte_length, manifest_bytes, patch_sha256, patch_byte_length, patch_bytes,
                 package_sha256, package_byte_length, package_bytes)
            VALUES
                (@project, @run, @session, @capture, @event, @pin, @issuer, @actor,
                 @tenant, @selectionHash, @workspace, @incarnation,
                 @branch, @repository, @generation, @baseSha, @treeSha, @manifestSha,
                 @manifestLength, @manifest, @patchSha, @patchLength, @patch,
                 @packageSha, @packageLength, @package)
            ON CONFLICT (project_id, run_id, session_id, capture_id) DO NOTHING
            """, connection, transaction))
        {
            AddScope(insert, identity);
            insert.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
            insert.Parameters.AddWithValue("capture", NpgsqlDbType.Varchar, captureIdentity.CaptureId);
            insert.Parameters.AddWithValue("event", NpgsqlDbType.Uuid, captureIdentity.EventId);
            insert.Parameters.AddWithValue("pin", NpgsqlDbType.Varchar, pin.PinId);
            insert.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, actor.Issuer);
            insert.Parameters.AddWithValue("actor", NpgsqlDbType.Varchar, actor.Subject);
            insert.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, selection.Authorization.TenantId);
            insert.Parameters.AddWithValue("selectionHash", NpgsqlDbType.Char, selectionHash);
            insert.Parameters.AddWithValue("workspace", NpgsqlDbType.Varchar, manifest.WorkspaceId);
            insert.Parameters.AddWithValue(
                "incarnation", NpgsqlDbType.Uuid, manifest.WorkspaceIncarnationId);
            insert.Parameters.AddWithValue("branch", NpgsqlDbType.Varchar, manifest.BranchName);
            insert.Parameters.AddWithValue("repository", NpgsqlDbType.Varchar, manifest.RepositoryId);
            insert.Parameters.AddWithValue("generation", NpgsqlDbType.Bigint, manifest.ResourceGeneration);
            insert.Parameters.AddWithValue("baseSha", NpgsqlDbType.Char, manifest.BaseSha);
            insert.Parameters.AddWithValue("treeSha", NpgsqlDbType.Char, manifest.OutputTreeSha);
            insert.Parameters.AddWithValue("manifestSha", NpgsqlDbType.Char, document.ManifestSha256);
            insert.Parameters.AddWithValue(
                "manifestLength", NpgsqlDbType.Bigint, document.ManifestBytes.LongLength);
            insert.Parameters.AddWithValue("manifest", NpgsqlDbType.Bytea, document.ManifestBytes);
            insert.Parameters.AddWithValue("patchSha", NpgsqlDbType.Char, document.PatchSha256);
            insert.Parameters.AddWithValue("patchLength", NpgsqlDbType.Bigint, document.PatchBytes.LongLength);
            insert.Parameters.AddWithValue("patch", NpgsqlDbType.Bytea, document.PatchBytes);
            insert.Parameters.AddWithValue("packageSha", NpgsqlDbType.Char, document.PackageSha256);
            insert.Parameters.AddWithValue("packageLength", NpgsqlDbType.Bigint, document.PackageBytes.LongLength);
            insert.Parameters.AddWithValue("package", NpgsqlDbType.Bytea, document.PackageBytes);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var record = await ReadOutputCaptureRowAsync(
            connection, transaction, identity, captureIdentity.CaptureId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CoordinationException(
                "source_control_output_capture_unavailable", StatusCodes.Status503ServiceUnavailable);
        if (!CaptureMatchesDocument(record, actor, selection, pin, document, captureIdentity))
            throw new CoordinationException(
                "source_control_output_capture_conflict", StatusCodes.Status409Conflict);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    /// <summary>
    /// Reads and validates a pending output capture for a workspace and accepted-selection digest.
    /// </summary>
    internal async Task<SourceControlOutputCaptureRecord?> ReadPendingOutputCaptureAsync(
        SessionIdentity identity,
        string workspaceId,
        string acceptedSelectionHash,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var record = await ReadPendingOutputCaptureForWorkspaceAsync(
            connection,
            transaction: null,
            identity,
            workspaceId,
            acceptedSelectionHash,
            cancellationToken).ConfigureAwait(false);
        if (record is not null)
            ValidateOutputCaptureRecord(record);
        return record;
    }

    /// <summary>
    /// Finds the single pending capture under a workspace, optionally locking rows; multiple pending rows or inconsistent proof data fail closed.
    /// </summary>
    private async Task<SourceControlOutputCaptureRecord?> ReadPendingOutputCaptureForWorkspaceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        SessionIdentity identity,
        string workspaceId,
        string acceptedSelectionHash,
        CancellationToken cancellationToken)
    {
        var lockSuffix = transaction is null ? string.Empty : " FOR UPDATE";
        var captureIds = new List<string>(2);
        await using (var command = new NpgsqlCommand($"""
            SELECT capture_id
            FROM {OutputCaptures}
            WHERE project_id = @project AND run_id = @run AND session_id = @session
              AND workspace_id = @workspace AND accepted_selection_hash = @selectionHash
              AND capture_state = 'pending'
            ORDER BY captured_at, capture_id
            LIMIT 2
            {lockSuffix}
            """, connection, transaction))
        {
            AddScope(command, identity);
            command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
            command.Parameters.AddWithValue("workspace", NpgsqlDbType.Varchar, workspaceId);
            command.Parameters.AddWithValue("selectionHash", NpgsqlDbType.Char, acceptedSelectionHash);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                captureIds.Add(reader.GetString(0));
        }

        if (captureIds.Count > 1)
            throw new CoordinationException(
                "source_control_output_capture_pending_ambiguous", StatusCodes.Status409Conflict);
        if (captureIds.Count == 0)
            return null;

        var record = await ReadOutputCaptureRowAsync(
            connection, transaction, identity, captureIds[0], forUpdate: transaction is not null, cancellationToken)
            .ConfigureAwait(false);
        if (record is null ||
            record.State != "pending" ||
            record.Proof.WorkspaceId != workspaceId ||
            record.Proof.AcceptedSelectionHash != acceptedSelectionHash)
            throw new CoordinationException(
                "source_control_output_capture_corrupt", StatusCodes.Status503ServiceUnavailable);
        return record;
    }

    /// <summary>
    /// Reads a capture by validated identifier and returns it only when its selection digest matches and all stored bytes verify.
    /// </summary>
    internal async Task<SourceControlOutputCaptureRecord?> ReadOutputCaptureAsync(
        SessionIdentity identity,
        string captureId,
        string acceptedSelectionHash,
        CancellationToken cancellationToken)
    {
        if (!IsOutputCaptureId(captureId))
            throw new CoordinationException(
                "source_control_output_capture_invalid", StatusCodes.Status400BadRequest);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var record = await ReadOutputCaptureRowAsync(
            connection, transaction: null, identity, captureId, forUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        if (record is null ||
            !string.Equals(record.Proof.AcceptedSelectionHash, acceptedSelectionHash, StringComparison.Ordinal))
            return null;
        ValidateOutputCaptureRecord(record);
        return record;
    }

    /// <summary>
    /// Reads and locks a capture using the caller transaction after validating its identifier and selection digest.
    /// </summary>
    internal async Task<SourceControlOutputCaptureRecord?> ReadOutputCaptureInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity identity,
        string captureId,
        string acceptedSelectionHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
            throw new ArgumentException("The transaction must belong to the supplied connection.", nameof(transaction));
        if (!IsOutputCaptureId(captureId) ||
            acceptedSelectionHash.Length != 64 ||
            !acceptedSelectionHash.All(Uri.IsHexDigit))
            throw new CoordinationException(
                "source_control_output_capture_invalid", StatusCodes.Status400BadRequest);
        var record = await ReadOutputCaptureRowAsync(
            connection, transaction, identity, captureId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        if (record is null ||
            !string.Equals(
                record.Proof.AcceptedSelectionHash, acceptedSelectionHash, StringComparison.Ordinal))
            return null;
        ValidateOutputCaptureRecord(record);
        return record;
    }

    /// <summary>
    /// Locks owner and capture rows, then checks the proof against actor, tenant, selection, fence, and current repository pin.
    /// </summary>
    internal async Task<(SourceControlOutputCaptureRecord? Capture, OwnerBindingSnapshot CurrentOwner)>
        ReadPrerequisiteOutputCaptureInTransactionAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CoordinationActor actor,
            SessionIdentity identity,
            string tenantId,
            string acceptedSelectionHash,
            long executionFence,
            string captureId,
            CancellationToken cancellationToken)
    {
        if (!IsOutputCaptureId(captureId))
            throw new CoordinationException(
                "source_control_output_capture_invalid", StatusCodes.Status400BadRequest);
        var owner = await ReadPrerequisiteOwnerBindingInTransactionAsync(
            connection, transaction, actor, identity, tenantId, acceptedSelectionHash,
            executionFence, cancellationToken).ConfigureAwait(false);
        var capture = await ReadOutputCaptureRowAsync(
            connection, transaction, identity, captureId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false);
        if (capture is null)
            return (null, owner);

        ValidateOutputCaptureRecord(capture);
        var pin = await ReadPinAsync(
            connection, transaction, identity, acceptedSelectionHash, cancellationToken)
            .ConfigureAwait(false);
        var proof = capture.Proof;
        if (proof.Identity != identity ||
            proof.ActorIssuer != actor.Issuer ||
            proof.ActorSubject != actor.Subject ||
            proof.TenantId != tenantId ||
            proof.AcceptedSelectionHash != acceptedSelectionHash ||
            pin is null ||
            proof.SourceControlPinId != pin.PinId ||
            !MatchesPrerequisiteAcceptedRun(
                pin.AcceptedRun, actor, identity, tenantId, acceptedSelectionHash, executionFence))
            throw new CoordinationException(
                "source_control_output_capture_unavailable", StatusCodes.Status404NotFound);
        return (capture, owner);
    }

    /// <summary>
    /// Returns validated admitted proofs for the accepted run, with a hard limit of 100 proofs.
    /// </summary>
    internal async Task<ImmutableArray<ProducedRunCaptureProof>>
        ReadPrerequisiteOutputCaptureProofsInTransactionAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CoordinationActor actor,
            SessionIdentity identity,
            string tenantId,
            string acceptedSelectionHash,
            long executionFence,
            CancellationToken cancellationToken)
    {
        const int maximumProofs = 100;
        _ = await ReadPrerequisiteOwnerBindingInTransactionAsync(
            connection,
            transaction,
            actor,
            identity,
            tenantId,
            acceptedSelectionHash,
            executionFence,
            cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT project_id, run_id, session_id, capture_id, event_id, pin_id, issuer, actor_id,
                tenant_id, accepted_selection_hash, workspace_id, workspace_incarnation_id,
                branch_name, repository_id, resource_generation, base_sha, output_tree_sha, manifest_sha256,
                manifest_byte_length, patch_sha256, patch_byte_length, package_sha256,
                package_byte_length, captured_at
            FROM {OutputCaptures}
            WHERE project_id = @project AND run_id = @run AND session_id = @session
              AND accepted_selection_hash = @selectionHash AND capture_state = 'admitted'
            ORDER BY captured_at DESC, capture_id DESC
            LIMIT @limit
            """, connection, transaction);
        AddScope(command, identity);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
        command.Parameters.AddWithValue("selectionHash", NpgsqlDbType.Char, acceptedSelectionHash);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, maximumProofs + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var proofs = ImmutableArray.CreateBuilder<ProducedRunCaptureProof>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var proof = new ProducedRunCaptureProof(
                ProducedRunCaptureLimits.ContractVersion,
                new SessionIdentity(reader.GetString(0), reader.GetString(1), reader.GetString(2)),
                reader.GetString(3),
                reader.GetGuid(4),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(5),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetGuid(11),
                reader.GetString(12),
                reader.GetString(13),
                reader.GetInt64(14),
                reader.GetString(15),
                reader.GetString(16),
                reader.GetString(17),
                reader.GetInt64(18),
                reader.GetString(19),
                reader.GetInt64(20),
                reader.GetString(21),
                reader.GetInt64(22),
                reader.GetFieldValue<DateTimeOffset>(23));
            try
            {
                ProducedRunCaptureContractValidation.Validate(proof);
            }
            catch (ArgumentException)
            {
                throw new CoordinationException(
                    "source_control_output_capture_corrupt", StatusCodes.Status503ServiceUnavailable);
            }
            if (proof.Identity != identity ||
                proof.ActorIssuer != actor.Issuer ||
                proof.ActorSubject != actor.Subject ||
                proof.TenantId != tenantId ||
                proof.AcceptedSelectionHash != acceptedSelectionHash)
                throw new CoordinationException(
                    "source_control_output_capture_unavailable", StatusCodes.Status404NotFound);
            proofs.Add(proof);
        }

        if (proofs.Count > maximumProofs)
            throw new CoordinationException(
                "source_control_output_capture_limit_exceeded", StatusCodes.Status409Conflict);
        return proofs.ToImmutable();
    }

    /// <summary>
    /// Reads admitted capture summaries in descending time and identifier order using a bounded keyset cursor, validating each stored record.
    /// </summary>
    internal async Task<ImmutableArray<SourceControlOutputCaptureSummary>> ReadOutputCapturePageAsync(
        SessionIdentity identity,
        string acceptedSelectionHash,
        DateTimeOffset? beforeCapturedAt,
        string? beforeCaptureId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100 ||
            (beforeCapturedAt is null) != (beforeCaptureId is null) ||
            beforeCaptureId is not null && !IsOutputCaptureId(beforeCaptureId))
            throw new CoordinationException(
                "source_control_output_capture_page_invalid", StatusCodes.Status400BadRequest);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = ImmutableArray.CreateBuilder<SourceControlOutputCaptureSummary>();
        await using var command = new NpgsqlCommand($"""
            SELECT project_id, run_id, session_id, capture_id, event_id, pin_id, issuer, actor_id,
                tenant_id, accepted_selection_hash, workspace_id, workspace_incarnation_id,
                branch_name, repository_id, resource_generation, base_sha, output_tree_sha, manifest_sha256,
                manifest_byte_length, manifest_bytes, patch_sha256, patch_byte_length, patch_bytes,
                package_sha256, package_byte_length, capture_state, object_key, event_position,
                captured_at, admitted_at, package_bytes
            FROM {OutputCaptures}
            WHERE project_id = @project AND run_id = @run AND session_id = @session
              AND accepted_selection_hash = @selectionHash AND capture_state = 'admitted'
              AND (@beforeAt IS NULL OR captured_at < @beforeAt OR
                   (captured_at = @beforeAt AND capture_id < @beforeCapture))
            ORDER BY captured_at DESC, capture_id DESC
            LIMIT @limit
            """, connection);
        AddScope(command, identity);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
        command.Parameters.AddWithValue("selectionHash", NpgsqlDbType.Char, acceptedSelectionHash);
        command.Parameters.AddWithValue(
            "beforeAt", NpgsqlDbType.TimestampTz, (object?)beforeCapturedAt ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "beforeCapture", NpgsqlDbType.Varchar, (object?)beforeCaptureId ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var record = ReadOutputCaptureRow(reader);
            ValidateOutputCaptureRecord(record);
            rows.Add(new(
                record.Proof,
                record.EventPosition ?? throw new CoordinationException(
                    "source_control_output_capture_corrupt", StatusCodes.Status503ServiceUnavailable)));
        }
        return rows.ToImmutable();
    }

    /// <summary>
    /// Returns a capture proof only when its selection and tenant match the requested Events scope.
    /// </summary>
    internal async Task<ProducedRunCaptureProof> ReadOutputCaptureProofForEventsAsync(
        SessionIdentity identity,
        string captureId,
        string acceptedSelectionHash,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var record = await ReadOutputCaptureAsync(
            identity, captureId, acceptedSelectionHash, cancellationToken).ConfigureAwait(false);
        if (record is null || record.Proof.TenantId != tenantId)
            throw new CoordinationException(
                "source_control_output_capture_unavailable", StatusCodes.Status404NotFound);
        return record.Proof;
    }

    /// <summary>
    /// Checks the Events journal acknowledgment and current run binding, then atomically changes a matching pending capture to admitted; matching retries are idempotent.
    /// </summary>
    internal async Task<SourceControlOutputCaptureRecord> AdmitOutputCaptureAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        SourceControlRepositoryPin pin,
        long expectedStateVersion,
        string captureId,
        ProducedRunCaptureJournalEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ProducedRunCaptureContractValidation.Validate(entry.Capture);
        if (entry.Position < 1 ||
            entry.Capture.Identity != identity ||
            entry.Capture.CaptureId != captureId)
            throw new CoordinationException(
                "source_control_output_capture_journal_invalid", StatusCodes.Status502BadGateway);
        var package = ProducedRunCaptureContractValidation.CreatePackageReference(entry.Capture);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var selectionHash = HashSelection(selection.Selection);
        var owner = await ReadOwnerBindingAsync(
            connection,
            transaction,
            actor,
            identity,
            selection.Authorization.TenantId,
            selectionHash,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false);
        if (owner.ExecutionState is not ("idle" or "active" or "blocked"))
            throw new CoordinationException(
                "source_control_output_run_unavailable", StatusCodes.Status409Conflict);
        var storedPin = await ReadPinAsync(
            connection, transaction, identity, selectionHash, cancellationToken).ConfigureAwait(false);
        if (storedPin is null || !SamePin(storedPin, pin))
            throw new CoordinationException(
                "source_control_output_capture_pin_changed", StatusCodes.Status409Conflict);
        EnsureCurrentBinding(owner, selection, pin.AcceptedRun);
        if (owner.StateVersion != expectedStateVersion || owner.DecisionState != "accepted")
            throw new CoordinationException(
                "source_control_output_capture_authority_changed", StatusCodes.Status409Conflict);

        var record = await ReadOutputCaptureRowAsync(
            connection, transaction, identity, captureId, forUpdate: true, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CoordinationException(
                "source_control_output_capture_unavailable", StatusCodes.Status404NotFound);
        if (record.Proof != entry.Capture ||
            package.ByteLength != record.Proof.PackageByteLength)
            throw new CoordinationException(
                "source_control_output_capture_journal_conflict", StatusCodes.Status502BadGateway);

        if (record.State == "pending")
        {
            await using var update = new NpgsqlCommand($"""
                UPDATE {OutputCaptures}
                SET capture_state = 'admitted', object_key = @key,
                    event_position = @position, admitted_at = clock_timestamp()
                WHERE project_id = @project AND run_id = @run AND session_id = @session
                  AND capture_id = @capture AND capture_state = 'pending'
                """, connection, transaction);
            AddScope(update, identity);
            update.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
            update.Parameters.AddWithValue("capture", NpgsqlDbType.Varchar, captureId);
            update.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, package.Key.Value);
            update.Parameters.AddWithValue("position", NpgsqlDbType.Bigint, entry.Position);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new CoordinationException(
                    "source_control_output_capture_journal_conflict", StatusCodes.Status409Conflict);
        }
        else if (record.State != "admitted" ||
                 record.ObjectKey != package.Key.Value ||
                 record.EventPosition != entry.Position)
            throw new CoordinationException(
                "source_control_output_capture_journal_conflict", StatusCodes.Status409Conflict);

        var admitted = await ReadOutputCaptureRowAsync(
            connection, transaction, identity, captureId, forUpdate: false, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CoordinationException(
                "source_control_output_capture_unavailable", StatusCodes.Status503ServiceUnavailable);
        if (admitted.State != "admitted" ||
            admitted.ObjectKey != package.Key.Value ||
            admitted.EventPosition != entry.Position)
            throw new CoordinationException(
                "source_control_output_capture_journal_conflict", StatusCodes.Status409Conflict);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return admitted;
    }

    /// <summary>
    /// Persists one repository pin only while the owner decision and accepted run are current, then verifies the stored pin matches the proposal.
    /// </summary>
    public async Task<SourceControlRepositoryPin> PersistRepositoryPinAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        SourceControlRepositoryPin pin,
        long expectedStateVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (identity.ProjectId != selection.Selection.ProjectId ||
            identity.RunId != selection.Selection.RunId ||
            pin.AcceptedRun.ProjectId != identity.ProjectId ||
            pin.AcceptedRun.RunId != identity.RunId ||
            pin.AcceptedRun.RootSessionId != identity.SessionId ||
            pin.AcceptedRun.Issuer != actor.Issuer ||
            pin.AcceptedRun.Subject != actor.Subject)
            throw new CoordinationException(
                "source_control_pin_binding_invalid", StatusCodes.Status403Forbidden);

        var selectionHash = HashSelection(selection.Selection);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var owner = await ReadOwnerBindingAsync(
            connection,
            transaction,
            actor,
            identity,
            selection.Authorization.TenantId,
            selectionHash,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false);
        EnsureCurrentBinding(owner, selection, pin.AcceptedRun);
        if (owner.StateVersion != expectedStateVersion || owner.DecisionState != "accepted")
            throw new CoordinationException(
                "source_control_pin_stale_decision", StatusCodes.Status409Conflict);

        await PersistRepositoryPinRowAsync(
            connection, transaction, identity, pin, cancellationToken).ConfigureAwait(false);
        var stored = await ReadPinAsync(
            connection, transaction, identity, selectionHash, cancellationToken).ConfigureAwait(false);
        if (stored is null || !SamePin(stored, pin))
            throw new CoordinationException(
                "source_control_pin_conflict", StatusCodes.Status409Conflict);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return stored;
    }

    /// <summary>
    /// Stores one signed delivery per repository and delivery identifier, and accepts duplicates only when run, pin, event, and payload digest match.
    /// </summary>
    public async Task<bool> RecordWebhookDeliveryAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        SourceControlRepositoryPin pin,
        GitHubWebhookEnvelope envelope,
        string payloadSha256,
        long expectedStateVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pin);
        ArgumentNullException.ThrowIfNull(envelope);
        if (payloadSha256.Length != 64 || !payloadSha256.All(Uri.IsHexDigit))
            throw new CoordinationException(
                "source_control_webhook_digest_invalid", StatusCodes.Status400BadRequest);

        var selectionHash = HashSelection(selection.Selection);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var owner = await ReadOwnerBindingAsync(
            connection,
            transaction,
            actor,
            identity,
            selection.Authorization.TenantId,
            selectionHash,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false);
        if (owner.ExecutionState == "completed" || owner.SessionLifecycle != "active")
            throw new CoordinationException(
                "source_control_run_inactive", StatusCodes.Status409Conflict);
        EnsureCurrentBinding(owner, selection, pin.AcceptedRun);
        if (owner.StateVersion != expectedStateVersion || owner.DecisionState != "accepted")
            throw new CoordinationException(
                "source_control_webhook_authority_changed", StatusCodes.Status409Conflict);

        var storedPin = await ReadPinAsync(
            connection, transaction, identity, selectionHash, cancellationToken).ConfigureAwait(false);
        if (storedPin is null ||
            !SamePin(storedPin, pin) ||
            !SameRepositoryIdentity(pin.Repository, envelope.Repository) ||
            pin.ProviderRepositoryId != envelope.ProviderRepositoryId)
            throw new CoordinationException(
                "source_control_webhook_binding_changed", StatusCodes.Status409Conflict);

        var inserted = false;
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_schema}.source_control_webhook_deliveries
                (repository_owner, repository_name, delivery_id, event_name,
                 project_id, run_id, pin_id, accepted_selection_hash, execution_fence,
                 payload_sha256, delivery_state)
            VALUES
                (@owner, @repository, @delivery, @event,
                 @project, @run, @pin, @selectionHash, @fence,
                 @payloadHash, 'accepted')
            ON CONFLICT (repository_owner, repository_name, delivery_id) DO NOTHING
            """, connection, transaction))
        {
            AddScope(insert, identity);
            insert.Parameters.AddWithValue("owner", NpgsqlDbType.Varchar, pin.Repository.Owner);
            insert.Parameters.AddWithValue("repository", NpgsqlDbType.Varchar, pin.Repository.Name);
            insert.Parameters.AddWithValue("delivery", NpgsqlDbType.Varchar, envelope.DeliveryId);
            insert.Parameters.AddWithValue("event", NpgsqlDbType.Varchar, envelope.EventName);
            insert.Parameters.AddWithValue("pin", NpgsqlDbType.Varchar, pin.PinId);
            insert.Parameters.AddWithValue("selectionHash", NpgsqlDbType.Char, selectionHash);
            insert.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, pin.AcceptedRun.Fence);
            insert.Parameters.AddWithValue("payloadHash", NpgsqlDbType.Char, payloadSha256);
            inserted = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }

        await using (var read = new NpgsqlCommand($"""
            SELECT project_id, run_id, pin_id, accepted_selection_hash,
                   execution_fence, payload_sha256, event_name, delivery_state
            FROM {_schema}.source_control_webhook_deliveries
            WHERE repository_owner = @owner AND repository_name = @repository
              AND delivery_id = @delivery
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("owner", NpgsqlDbType.Varchar, pin.Repository.Owner);
            read.Parameters.AddWithValue("repository", NpgsqlDbType.Varchar, pin.Repository.Name);
            read.Parameters.AddWithValue("delivery", NpgsqlDbType.Varchar, envelope.DeliveryId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.GetString(0) != identity.ProjectId ||
                reader.GetString(1) != identity.RunId ||
                reader.GetString(2) != pin.PinId ||
                reader.GetString(3).TrimEnd() != selectionHash ||
                reader.GetInt64(4) != pin.AcceptedRun.Fence ||
                reader.GetString(5).TrimEnd() != payloadSha256 ||
                reader.GetString(6) != envelope.EventName ||
                reader.GetString(7) is not ("accepted" or "completed"))
                throw new CoordinationException(
                    "source_control_webhook_delivery_conflict", StatusCodes.Status409Conflict);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return !inserted;
    }

    /// <summary>
    /// Takes a timed PostgreSQL advisory lock keyed by normalized repository name and returns a lease that releases the lock with its connection.
    /// </summary>
    public async Task<SourceControlRepositoryMergeLock> AcquireRepositoryMergeLockAsync(
        SourceControlRepositoryIdentity repository,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var key = repository.FullName.ToLowerInvariant();
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await using (var setLockTimeout = new NpgsqlCommand(
                             $"SET lock_timeout = '{(long)MergeLockTimeout.TotalMilliseconds}ms'",
                             connection))
            {
                await setLockTimeout.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var command = new NpgsqlCommand(
                             "SELECT pg_advisory_lock(hashtext('agentweaver.source-control.merge'), hashtext(@repository))",
                             connection)
                         {
                             CommandTimeout = checked((int)MergeLockTimeout.TotalSeconds + 5)
                         })
            {
                command.Parameters.AddWithValue("repository", NpgsqlDbType.Text, key);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var resetLockTimeout = new NpgsqlCommand("RESET lock_timeout", connection))
                await resetLockTimeout.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new SourceControlRepositoryMergeLock(connection, key);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new CoordinationException(
                "source_control_merge_lock_timeout",
                StatusCodes.Status503ServiceUnavailable,
                exception);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Uses a conditional update to start a merge only when the approved intent, accepted run, decision, and referenced active grant remain current.
    /// </summary>
    public async Task MarkMergeStartedAsync(
        SessionIdentity identity,
        SourceControlMergeIntentSnapshot intent,
        ExecutableActionGrantReference grantReference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(grantReference);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            UPDATE {Intents} AS i
            SET intent_state = 'merge_started', updated_at = clock_timestamp()
            WHERE i.project_id = @project AND i.run_id = @run AND i.intent_id = @intent
              AND i.session_id = @session AND i.intent_state = 'approved'
              AND i.approval_decision_id = @approvalDecision
              AND i.approval_state_version = @approvalVersion
              AND i.execution_fence = @fence
              AND i.accepted_selection_hash = @selectionHash
              AND EXISTS (
                  SELECT 1 FROM {_schema}.accepted_runs AS r
                  WHERE r.project_id = i.project_id AND r.run_id = i.run_id
                    AND r.execution_fence = i.execution_fence
                    AND r.accepted_selection_hash = i.accepted_selection_hash
                    AND r.execution_state <> 'completed')
              AND EXISTS (
                  SELECT 1 FROM {_schema}.coordination_sessions AS s
                  WHERE s.project_id = i.project_id AND s.run_id = i.run_id
                    AND s.session_id = i.session_id AND s.parent_session_id IS NULL
                    AND s.execution_fence = i.execution_fence AND s.lifecycle_state = 'active')
              AND EXISTS (
                  SELECT 1 FROM {_schema}.coordinator_decisions AS d
                  WHERE d.project_id = i.project_id AND d.run_id = i.run_id
                    AND d.decision_id = i.approval_decision_id
                    AND d.state_version = i.approval_state_version
                    AND d.decision_state = 'accepted')
              AND EXISTS (
                  SELECT 1 FROM {Grants} AS g
                  WHERE g.project_id = i.project_id AND g.run_id = i.run_id
                    AND g.source_control_intent_id = i.intent_id
                    AND g.grant_id = @grant AND g.revision = @revision
                    AND g.is_current AND g.grant_state = 'active'
                    AND g.expires_at > clock_timestamp())
            """, connection);
        AddScope(command, identity);
        command.Parameters.AddWithValue("intent", NpgsqlDbType.Varchar, intent.IntentId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, intent.AcceptedRun.RootSessionId);
        command.Parameters.AddWithValue("approvalDecision", NpgsqlDbType.Uuid, intent.ApprovalDecisionId!.Value);
        command.Parameters.AddWithValue(
            "approvalVersion", NpgsqlDbType.Bigint, intent.ApprovalStateVersion!.Value);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, intent.AcceptedRun.Fence);
        command.Parameters.AddWithValue(
            "selectionHash", NpgsqlDbType.Char, intent.AcceptedRun.AcceptedSelectionHash);
        command.Parameters.AddWithValue("grant", NpgsqlDbType.Varchar, grantReference.GrantId);
        command.Parameters.AddWithValue("revision", NpgsqlDbType.Varchar, grantReference.Revision);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new CoordinationException(
                "source_control_intent_not_current", StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Changes only an approved intent to conflict when authority fails before the remote merge starts.
    /// </summary>
    public async Task RecordPreEffectMergeConflictAsync(
        SessionIdentity identity,
        string intentId,
        string failureCode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            UPDATE {Intents}
            SET intent_state = 'conflict', last_failure_code = @failureCode,
                updated_at = clock_timestamp()
            WHERE project_id = @project AND run_id = @run AND intent_id = @intent
              AND intent_state = 'approved'
            """, connection);
        AddScope(command, identity);
        command.Parameters.AddWithValue("intent", NpgsqlDbType.Varchar, intentId);
        command.Parameters.AddWithValue("failureCode", NpgsqlDbType.Varchar, failureCode);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new CoordinationException(
                "source_control_intent_not_current", StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Marks an approved intent conflicted only when a newer run fence supersedes it and its grant has been fully superseded.
    /// </summary>
    public async Task<bool> RecordStaleFenceMergeConflictAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        SourceControlMergeIntentSnapshot expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.State != "approved" ||
            expected.ApprovalDecisionId is null ||
            expected.ApprovalStateVersion is null ||
            expected.GrantReference is null)
            return false;

        var acceptedRun = expected.AcceptedRun;
        var effective = selection.Selection;
        var selectionHash = HashSelection(effective);
        if (identity.ProjectId != effective.ProjectId ||
            identity.RunId != effective.RunId ||
            acceptedRun.Issuer != actor.Issuer ||
            acceptedRun.Subject != actor.Subject ||
            acceptedRun.TenantId != selection.Authorization.TenantId ||
            acceptedRun.ProjectId != identity.ProjectId ||
            acceptedRun.RunId != identity.RunId ||
            acceptedRun.RootSessionId != identity.SessionId ||
            acceptedRun.AcceptedSelectionHash != selectionHash ||
            acceptedRun.ProjectRevision != effective.ProjectRevision ||
            acceptedRun.ProjectConfigurationRevision != effective.ProjectConfigurationRevision ||
            acceptedRun.PlatformRuntimeRevision != effective.PlatformRuntimeRevision ||
            acceptedRun.ContextRevision != effective.ContextRevision)
            return false;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        OwnerBindingSnapshot owner;
        try
        {
            owner = await ReadOwnerBindingAsync(
                connection,
                transaction,
                actor,
                identity,
                selection.Authorization.TenantId,
                selectionHash,
                forUpdate: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (CoordinationException exception) when (
            exception.StatusCode is StatusCodes.Status404NotFound or StatusCodes.Status409Conflict)
        {
            return false;
        }

        if (owner.ExecutionState == "completed" ||
            owner.SessionLifecycle != "active" ||
            owner.RunFence <= acceptedRun.Fence ||
            owner.StateVersion <= expected.ApprovalStateVersion.Value)
            return false;

        var stored = await ReadIntentByIdAsync(
            connection,
            transaction,
            identity,
            expected.IntentId,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false);
        if (stored is null ||
            stored.AcceptedRun != acceptedRun ||
            stored.ApprovalDecisionId != expected.ApprovalDecisionId ||
            stored.ApprovalStateVersion != expected.ApprovalStateVersion ||
            stored.ApprovalReceipt != expected.ApprovalReceipt)
            return false;

        var failureCode = "source_control_run_binding_changed";
        if (stored.State == "conflict" && stored.LastFailureCode == failureCode)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (stored.State != "approved" || stored.GrantReference is not null ||
            !await IsSupersededMergeGrantAsync(
                connection, transaction, identity, expected, cancellationToken).ConfigureAwait(false))
            return false;

        await using var update = new NpgsqlCommand($"""
            UPDATE {Intents}
            SET intent_state = 'conflict', last_failure_code = @failureCode,
                updated_at = clock_timestamp()
            WHERE project_id = @project AND run_id = @run AND intent_id = @intent
              AND accepted_selection_hash = @selectionHash
              AND execution_fence = @fence
              AND approval_decision_id = @approvalDecision
              AND approval_state_version = @approvalVersion
              AND intent_state = 'approved'
            """, connection, transaction);
        AddScope(update, identity);
        update.Parameters.AddWithValue("intent", NpgsqlDbType.Varchar, expected.IntentId);
        update.Parameters.AddWithValue("selectionHash", NpgsqlDbType.Char, selectionHash);
        update.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, acceptedRun.Fence);
        update.Parameters.AddWithValue(
            "approvalDecision", NpgsqlDbType.Uuid, expected.ApprovalDecisionId.Value);
        update.Parameters.AddWithValue(
            "approvalVersion", NpgsqlDbType.Bigint, expected.ApprovalStateVersion.Value);
        update.Parameters.AddWithValue("failureCode", NpgsqlDbType.Varchar, failureCode);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            return false;

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Confirms the referenced grant is superseded and that no current grant remains for the intent.
    /// </summary>
    private async Task<bool> IsSupersededMergeGrantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity identity,
        SourceControlMergeIntentSnapshot expected,
        CancellationToken cancellationToken)
    {
        var grant = expected.GrantReference
            ?? throw new ArgumentException("An approved merge intent must have a grant.", nameof(expected));
        await using var command = new NpgsqlCommand($"""
            SELECT EXISTS (
                SELECT 1 FROM {Grants} AS g
                WHERE g.project_id = @project AND g.run_id = @run
                  AND g.source_control_intent_id = @intent
                  AND g.grant_id = @grant AND g.revision = @revision
                  AND NOT g.is_current AND g.grant_state = 'superseded')
              AND NOT EXISTS (
                SELECT 1 FROM {Grants} AS g
                WHERE g.project_id = @project AND g.run_id = @run
                  AND g.source_control_intent_id = @intent AND g.is_current)
            """, connection, transaction);
        AddScope(command, identity);
        command.Parameters.AddWithValue("intent", NpgsqlDbType.Varchar, expected.IntentId);
        command.Parameters.AddWithValue("grant", NpgsqlDbType.Varchar, grant.GrantId);
        command.Parameters.AddWithValue("revision", NpgsqlDbType.Varchar, grant.Revision);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Changes a merge-started intent to merged, conflict, or outcome-uncertain and records its commit or failure code.
    /// </summary>
    public async Task RecordMergeOutcomeAsync(
        SessionIdentity identity,
        string intentId,
        string outcome,
        string? mergeSha,
        string? failureCode,
        CancellationToken cancellationToken)
    {
        if (outcome is not ("merged" or "outcome_uncertain" or "conflict"))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            UPDATE {Intents}
            SET intent_state = @outcome, merge_sha = @mergeSha,
                last_failure_code = @failureCode, updated_at = clock_timestamp()
            WHERE project_id = @project AND run_id = @run AND intent_id = @intent
              AND intent_state = 'merge_started'
            """, connection);
        AddScope(command, identity);
        command.Parameters.AddWithValue("intent", NpgsqlDbType.Varchar, intentId);
        command.Parameters.AddWithValue("outcome", NpgsqlDbType.Varchar, outcome);
        command.Parameters.AddWithValue(
            "mergeSha", NpgsqlDbType.Varchar, (object?)mergeSha ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "failureCode", NpgsqlDbType.Varchar, (object?)failureCode ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new CoordinationException(
                "source_control_merge_outcome_unavailable", StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Marks a merge-started intent outcome-uncertain after interruption; it does not claim whether the remote merge completed.
    /// </summary>
    public async Task MarkInterruptedMergeUncertainAsync(
        SessionIdentity identity,
        string intentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            UPDATE {Intents}
            SET intent_state = 'outcome_uncertain',
                last_failure_code = 'process_interrupted',
                updated_at = clock_timestamp()
            WHERE project_id = @project AND run_id = @run AND intent_id = @intent
              AND intent_state = 'merge_started'
            """, connection);
        AddScope(command, identity);
        command.Parameters.AddWithValue("intent", NpgsqlDbType.Varchar, intentId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one stored capture and optionally takes a row lock; mismatched project, run, session, or capture identity is treated as corruption.
    /// </summary>
    private async Task<SourceControlOutputCaptureRecord?> ReadOutputCaptureRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        SessionIdentity identity,
        string captureId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        if (forUpdate && transaction is null)
            throw new ArgumentException("A transaction is required for an update lock.", nameof(transaction));
        var lockSuffix = forUpdate ? " FOR UPDATE" : string.Empty;
        await using var command = new NpgsqlCommand($"""
            SELECT project_id, run_id, session_id, capture_id, event_id, pin_id, issuer, actor_id,
                tenant_id, accepted_selection_hash, workspace_id, workspace_incarnation_id,
                branch_name, repository_id, resource_generation, base_sha, output_tree_sha, manifest_sha256,
                manifest_byte_length, manifest_bytes, patch_sha256, patch_byte_length, patch_bytes,
                package_sha256, package_byte_length, capture_state, object_key, event_position, captured_at,
                admitted_at, package_bytes
            FROM {OutputCaptures}
            WHERE project_id = @project AND run_id = @run
              AND session_id = @session AND capture_id = @capture
            {lockSuffix}
            """, connection, transaction);
        AddScope(command, identity);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
        command.Parameters.AddWithValue("capture", NpgsqlDbType.Varchar, captureId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var record = ReadOutputCaptureRow(reader);
        if (record.Proof.Identity != identity || record.Proof.CaptureId != captureId)
            throw new CoordinationException(
                "source_control_output_capture_corrupt", StatusCodes.Status503ServiceUnavailable);
        return record;
    }

    /// <summary>
    /// Maps a database row into a proof and capture record, preserving nullable admission fields.
    /// </summary>
    private static SourceControlOutputCaptureRecord ReadOutputCaptureRow(NpgsqlDataReader reader)
    {
        var identity = new SessionIdentity(reader.GetString(0), reader.GetString(1), reader.GetString(2));
        var proof = new ProducedRunCaptureProof(
            ProducedRunCaptureLimits.ContractVersion,
            identity,
            reader.GetString(3),
            reader.GetGuid(4),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(5),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetGuid(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetInt64(14),
            reader.GetString(15),
            reader.GetString(16),
            reader.GetString(17),
            reader.GetInt64(18),
            reader.GetString(20),
            reader.GetInt64(21),
            reader.GetString(23),
            reader.GetInt64(24),
            reader.GetFieldValue<DateTimeOffset>(28));
        return new SourceControlOutputCaptureRecord(
            proof,
            reader.GetString(25),
            reader.IsDBNull(26) ? null : reader.GetString(26),
            reader.IsDBNull(27) ? null : reader.GetInt64(27),
            reader.IsDBNull(29) ? null : reader.GetFieldValue<DateTimeOffset>(29),
            reader.GetFieldValue<byte[]>(19),
            reader.GetFieldValue<byte[]>(22),
            reader.GetFieldValue<byte[]>(30));
    }

    /// <summary>
    /// Validates the proof, admission state, object reference, stored byte digests, manifest bindings, and reconstructed package length.
    /// </summary>
    private static void ValidateOutputCaptureRecord(SourceControlOutputCaptureRecord record)
    {
        try
        {
            ProducedRunCaptureContractValidation.Validate(record.Proof);
            if (record.State is not ("pending" or "admitted") ||
                (record.State == "pending" &&
                 (record.ObjectKey is not null || record.EventPosition is not null || record.AdmittedAt is not null)) ||
                (record.State == "admitted" &&
                 (record.ObjectKey is null || record.EventPosition is null or < 1 || record.AdmittedAt is null)))
                throw new ArgumentException("The capture admission state is invalid.");
            if (record.ObjectKey is not null)
                ProducedRunCaptureContractValidation.ValidatePackageReference(
                    new SessionObjectReference(
                        new ObjectKey(record.ObjectKey),
                        ProducedRunCaptureContractValidation.ObjectPurpose,
                        record.Proof.PackageByteLength),
                    record.Proof);

            var proof = record.Proof;
            if (record.ManifestBytes.LongLength != proof.ManifestByteLength ||
                GitWorkspaceCapturePackage.Hash(record.ManifestBytes) != proof.ManifestSha256 ||
                record.PatchBytes.LongLength != proof.PatchByteLength ||
                GitWorkspaceCapturePackage.Hash(record.PatchBytes) != proof.PatchSha256)
                throw new ArgumentException("The stored capture manifest or patch digest is invalid.");

            var manifest = GitWorkspaceCapturePackage.ParseManifest(record.ManifestBytes);
            GitWorkspaceCapturePackage.VerifyPackage(
                record.PackageBytes, manifest, proof.PackageSha256);
            if (manifest.RunId != proof.Identity.RunId ||
                manifest.WorkspaceId != proof.WorkspaceId ||
                manifest.WorkspaceIncarnationId != proof.WorkspaceIncarnationId ||
                manifest.BranchName != proof.BranchName ||
                manifest.RepositoryId != proof.RepositoryId ||
                manifest.ResourceGeneration != proof.ResourceGeneration ||
                manifest.BaseSha != proof.BaseSha ||
                manifest.OutputTreeSha != proof.OutputTreeSha ||
                PackageLength(manifest) != proof.PackageByteLength)
                throw new ArgumentException("The stored capture manifest binding is invalid.");
        }
        catch (Exception exception) when (
            exception is ArgumentException or OverflowException or JsonException)
        {
            throw new CoordinationException(
                "source_control_output_capture_corrupt",
                StatusCodes.Status503ServiceUnavailable,
                innerException: exception);
        }
    }

    /// <summary>
    /// Compares each capture owner, workspace, digest, length, and stored byte sequence with a proposed capture document.
    /// </summary>
    private static bool CaptureMatchesDocument(
        SourceControlOutputCaptureRecord record,
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        SourceControlRepositoryPin pin,
        GitWorkspaceCaptureDocument document,
        ProducedRunCaptureIdentity captureIdentity)
    {
        ValidateOutputCaptureRecord(record);
        var proof = record.Proof;
        var manifest = document.Manifest;
        return proof.CaptureId == captureIdentity.CaptureId &&
            proof.EventId == captureIdentity.EventId &&
            proof.ActorIssuer == actor.Issuer &&
            proof.ActorSubject == actor.Subject &&
            proof.TenantId == selection.Authorization.TenantId &&
            proof.SourceControlPinId == pin.PinId &&
            proof.AcceptedSelectionHash == HashSelection(selection.Selection) &&
            proof.WorkspaceId == manifest.WorkspaceId &&
            proof.WorkspaceIncarnationId == manifest.WorkspaceIncarnationId &&
            proof.BranchName == manifest.BranchName &&
            proof.RepositoryId == manifest.RepositoryId &&
            proof.ResourceGeneration == manifest.ResourceGeneration &&
            proof.BaseSha == manifest.BaseSha &&
            proof.OutputTreeSha == manifest.OutputTreeSha &&
            proof.ManifestSha256 == document.ManifestSha256 &&
            proof.ManifestByteLength == document.ManifestBytes.LongLength &&
            proof.PatchSha256 == document.PatchSha256 &&
            proof.PatchByteLength == document.PatchBytes.LongLength &&
            proof.PackageSha256 == document.PackageSha256 &&
            proof.PackageByteLength == document.PackageBytes.LongLength &&
            record.ManifestBytes.AsSpan().SequenceEqual(document.ManifestBytes) &&
            record.PatchBytes.AsSpan().SequenceEqual(document.PatchBytes) &&
            record.PackageBytes.AsSpan().SequenceEqual(document.PackageBytes);
    }

    /// <summary>
    /// Computes the encoded package length from manifest entries and rejects overflow or a package beyond the configured maximum.
    /// </summary>
    private static long PackageLength(GitWorkspaceCapturedOutputManifest manifest)
    {
        long packageLength = ProducedRunCaptureLimits.PackageHeaderBytes;
        foreach (var file in manifest.Files)
        {
            packageLength = checked(
                packageLength + ProducedRunCaptureLimits.PackageFileLengthBytes + file.ByteLength);
            if (packageLength > ProducedRunCaptureLimits.MaximumPackageBytes)
                throw new ArgumentException("The capture package exceeds the configured size limit.");
        }
        return packageLength;
    }

    /// <summary>
    /// Accepts only the fixed Source Control capture prefix and lowercase hexadecimal identifier suffix.
    /// </summary>
    private static bool IsOutputCaptureId(string? captureId) =>
        captureId is { Length: 42 } &&
        captureId.StartsWith("sc-output-", StringComparison.Ordinal) &&
        captureId[10..].All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// Loads the serialized repository pin for one project, run, and accepted-selection digest, restoring its provider binding.
    /// </summary>
    private async Task<SourceControlRepositoryPin?> ReadPinAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        SessionIdentity identity,
        string selectionHash,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT pin FROM {Pins}
            WHERE project_id = @project AND run_id = @run
              AND accepted_selection_hash = @selectionHash
            """, connection, transaction);
        AddScope(command, identity);
        command.Parameters.AddWithValue("selectionHash", NpgsqlDbType.Char, selectionHash);
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return json is null ? null : RestorePin(json);
    }

    /// <summary>
    /// Inserts a pin and its non-secret binding fields without replacing an existing pin for the same accepted selection.
    /// </summary>
    private async Task PersistRepositoryPinRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity identity,
        SourceControlRepositoryPin pin,
        CancellationToken cancellationToken)
    {
        var pinJson = JsonSerializer.Serialize(CapturePin(pin), JsonOptions);
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {Pins}
                (project_id, run_id, pin_id, session_id, issuer, actor_id, tenant_id,
                 accepted_selection_hash, project_revision, project_configuration_revision,
                 platform_runtime_revision, context_revision, execution_fence,
                 provider_id, provider_resource_id, provider_resource_generation,
                 repository_owner, repository_name, provider_repository_id,
                 api_secret_id, api_secret_version, checkout_secret_id, checkout_secret_version,
                 webhook_secret_id, webhook_secret_version,
                 github_app_connection_id, github_app_connection_revision,
                 github_app_installation_id, github_app_permission_digest, github_app_selection_hash,
                 pin, created_at)
            VALUES
                (@project, @run, @pin, @session, @issuer, @actor, @tenant,
                 @selectionHash, @projectRevision, @configurationRevision,
                 @platformRevision, @contextRevision, @fence,
                 @provider, @resource, @generation,
                 @owner, @repository, @repositoryId,
                 @apiSecretId, @apiSecretVersion, @checkoutSecretId, @checkoutSecretVersion,
                 @webhookSecretId, @webhookSecretVersion,
                 @githubAppConnectionId, @githubAppConnectionRevision,
                 @githubAppInstallationId, @githubAppPermissionDigest, @githubAppSelectionHash,
                 @pinJson, @createdAt)
            ON CONFLICT (project_id, run_id, accepted_selection_hash) DO NOTHING
            """, connection, transaction);
        AddScope(insert, identity);
        insert.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
        AddAcceptedRun(insert, pin.AcceptedRun);
        insert.Parameters.AddWithValue("pin", NpgsqlDbType.Varchar, pin.PinId);
        insert.Parameters.AddWithValue("provider", NpgsqlDbType.Varchar, pin.ProviderBinding.ProviderId);
        insert.Parameters.AddWithValue(
            "resource", NpgsqlDbType.Varchar, pin.ProviderBinding.Resource.ResourceId);
        insert.Parameters.AddWithValue(
            "generation", NpgsqlDbType.Bigint, pin.ProviderBinding.Resource.Generation);
        insert.Parameters.AddWithValue("owner", NpgsqlDbType.Varchar, pin.Repository.Owner);
        insert.Parameters.AddWithValue("repository", NpgsqlDbType.Varchar, pin.Repository.Name);
        insert.Parameters.AddWithValue("repositoryId", NpgsqlDbType.Bigint, pin.ProviderRepositoryId);
        AddOptionalSecretReference(insert, "api", pin.ApiCredential?.Secret);
        AddOptionalSecretReference(insert, "checkout", pin.CheckoutCredential?.Secret);
        AddOptionalSecretReference(insert, "webhook", pin.WebhookCredential?.Secret);
        AddOptionalAppBinding(insert, pin.GitHubAppBinding);
        insert.Parameters.AddWithValue("pinJson", NpgsqlDbType.Jsonb, pinJson);
        insert.Parameters.AddWithValue("createdAt", NpgsqlDbType.TimestampTz, pin.PinnedAt);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads one intent by its idempotency key together with the saved pin and any current active grant.
    /// </summary>
    private async Task<SourceControlMergeIntentSnapshot?> ReadIntentByIdempotencyKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        SessionIdentity identity,
        string idempotencyKey,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var lockSuffix = forUpdate ? " FOR UPDATE OF i" : string.Empty;
        await using var command = new NpgsqlCommand($"""
            SELECT i.intent_id, i.session_id, i.pin_id, i.issuer, i.actor_id, i.tenant_id,
                   i.accepted_selection_hash, i.project_revision, i.project_configuration_revision,
                   i.platform_runtime_revision, i.context_revision, i.execution_fence,
                   i.source_state_version, i.source_decision_id, i.source_request_id,
                   i.idempotency_key, i.workflow_id, i.definition_revision, i.work_plan_id,
                   i.assembly_request_id, i.workflow_step_id, i.approval_request_id,
                   i.pull_request_number, i.head_branch, i.head_sha, i.base_branch, i.base_sha,
                   i.merge_method, i.intent_state, i.approval_decision_id,
                   i.approval_state_version, i.approval_receipt, i.merge_sha,
                   i.last_failure_code, i.created_at, p.pin,
                   grant_ref.grant_id, grant_ref.revision
            FROM {Intents} AS i
            INNER JOIN {Pins} AS p
              ON p.project_id = i.project_id AND p.run_id = i.run_id AND p.pin_id = i.pin_id
            LEFT JOIN LATERAL (
                SELECT g.grant_id, g.revision
                FROM {Grants} AS g
                WHERE g.project_id = i.project_id AND g.run_id = i.run_id
                  AND g.source_control_intent_id = i.intent_id
                  AND g.is_current AND g.grant_state = 'active'
                  AND g.expires_at > clock_timestamp()
                ORDER BY g.created_at DESC
                LIMIT 1
            ) AS grant_ref ON true
            WHERE i.project_id = @project AND i.run_id = @run
              AND i.idempotency_key = @idempotency
            {lockSuffix}
            """, connection, transaction);
        AddScope(command, identity);
        command.Parameters.AddWithValue("idempotency", NpgsqlDbType.Varchar, idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadIntentSnapshot(reader)
            : null;
    }

    /// <summary>
    /// Loads one intent by identifier together with its pin and grant reference, optionally locking the intent row.
    /// </summary>
    private async Task<SourceControlMergeIntentSnapshot?> ReadIntentByIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        SessionIdentity identity,
        string intentId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var lockSuffix = forUpdate ? " FOR UPDATE OF i" : string.Empty;
        await using var command = new NpgsqlCommand($"""
            SELECT i.intent_id, i.session_id, i.pin_id, i.issuer, i.actor_id, i.tenant_id,
                   i.accepted_selection_hash, i.project_revision, i.project_configuration_revision,
                   i.platform_runtime_revision, i.context_revision, i.execution_fence,
                   i.source_state_version, i.source_decision_id, i.source_request_id,
                   i.idempotency_key, i.workflow_id, i.definition_revision, i.work_plan_id,
                   i.assembly_request_id, i.workflow_step_id, i.approval_request_id,
                   i.pull_request_number, i.head_branch, i.head_sha, i.base_branch, i.base_sha,
                   i.merge_method, i.intent_state, i.approval_decision_id,
                   i.approval_state_version, i.approval_receipt, i.merge_sha,
                   i.last_failure_code, i.created_at, p.pin,
                   grant_ref.grant_id, grant_ref.revision
            FROM {Intents} AS i
            INNER JOIN {Pins} AS p
              ON p.project_id = i.project_id AND p.run_id = i.run_id AND p.pin_id = i.pin_id
            LEFT JOIN LATERAL (
                SELECT g.grant_id, g.revision
                FROM {Grants} AS g
                WHERE g.project_id = i.project_id AND g.run_id = i.run_id
                  AND g.source_control_intent_id = i.intent_id
                  AND g.is_current AND g.grant_state = 'active'
                ORDER BY g.created_at DESC
                LIMIT 1
            ) AS grant_ref ON true
            WHERE i.project_id = @project AND i.run_id = @run AND i.intent_id = @intent
            {lockSuffix}
            """, connection, transaction);
        AddScope(command, identity);
        command.Parameters.AddWithValue("intent", NpgsqlDbType.Varchar, intentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadIntentSnapshot(reader)
            : null;
    }

    /// <summary>
    /// Exposes the locked owner snapshot for the caller transaction after checking accepted run and fence.
    /// </summary>
    internal Task<OwnerBindingSnapshot> ReadPrerequisiteOwnerBindingSnapshotInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CoordinationActor actor,
        SessionIdentity identity,
        string tenantId,
        string acceptedSelectionHash,
        long executionFence,
        CancellationToken cancellationToken) =>
        ReadPrerequisiteOwnerBindingInTransactionAsync(
            connection,
            transaction,
            actor,
            identity,
            tenantId,
            acceptedSelectionHash,
            executionFence,
            cancellationToken);

    /// <summary>
    /// Requires the supplied connection and transaction to match, locks owner rows, and validates tenant, selection digest, root session, and fence.
    /// </summary>
    private async Task<OwnerBindingSnapshot> ReadPrerequisiteOwnerBindingInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CoordinationActor actor,
        SessionIdentity identity,
        string tenantId,
        string acceptedSelectionHash,
        long executionFence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(actor);
        if (!ReferenceEquals(transaction.Connection, connection))
            throw new ArgumentException("The transaction must belong to the supplied connection.", nameof(transaction));
        if (string.IsNullOrWhiteSpace(tenantId) ||
            acceptedSelectionHash is not { Length: 64 } ||
            !acceptedSelectionHash.All(Uri.IsHexDigit) ||
            executionFence < 1)
            throw new CoordinationException(
                "source_control_run_binding_changed", StatusCodes.Status409Conflict);

        var owner = await ReadOwnerBindingAsync(
            connection,
            transaction,
            actor,
            identity,
            tenantId,
            acceptedSelectionHash,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false);
        if (owner.RunFence != executionFence ||
            owner.SessionFence != executionFence ||
            owner.RootSessionId != identity.SessionId)
            throw new CoordinationException(
                "source_control_run_binding_changed", StatusCodes.Status409Conflict);
        return owner;
    }

    /// <summary>
    /// Compares a saved accepted-run binding with the exact actor, tenant, session, selection digest, and execution fence.
    /// </summary>
    private static bool MatchesPrerequisiteAcceptedRun(
        SourceControlAcceptedRunBinding acceptedRun,
        CoordinationActor actor,
        SessionIdentity identity,
        string tenantId,
        string acceptedSelectionHash,
        long executionFence) =>
        acceptedRun.Issuer == actor.Issuer &&
        acceptedRun.Subject == actor.Subject &&
        acceptedRun.TenantId == tenantId &&
        acceptedRun.ProjectId == identity.ProjectId &&
        acceptedRun.RunId == identity.RunId &&
        acceptedRun.RootSessionId == identity.SessionId &&
        acceptedRun.AcceptedSelectionHash == acceptedSelectionHash &&
        acceptedRun.Fence == executionFence;

    /// <summary>
    /// Reads or locks accepted-run, root-session, and latest-decision rows, then rejects actor, tenant, selection, writer, or fence mismatches.
    /// </summary>
    private async Task<OwnerBindingSnapshot> ReadOwnerBindingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CoordinationActor actor,
        SessionIdentity identity,
        string tenantId,
        string selectionHash,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        OwnerBindingSnapshot binding;
        if (forUpdate)
        {
            if (transaction is null)
                throw new ArgumentException("A transaction is required for an update lock.", nameof(transaction));
            var owner = await LockOwnerRowsAsync(
                connection, transaction, identity, cancellationToken).ConfigureAwait(false);
            var decision = await ReadLatestOwnerDecisionAsync(
                connection, transaction, identity, cancellationToken).ConfigureAwait(false)
                ?? throw new CoordinationException(
                    "source_control_run_unavailable", StatusCodes.Status404NotFound);
            binding = new OwnerBindingSnapshot(
                owner.AcceptedSelectionHash,
                owner.AcceptedIssuer,
                owner.AcceptedSubject,
                owner.TenantId,
                owner.RunFence,
                owner.ExecutionState,
                owner.RootSessionId,
                owner.SessionFence,
                owner.WriterIssuer,
                owner.WriterSubject,
                owner.SessionLifecycle,
                decision.DecisionId,
                decision.StateVersion,
                decision.DecisionState);
        }
        else
        {
            await using var command = new NpgsqlCommand($"""
                SELECT r.accepted_selection_hash, r.accepted_by_issuer, r.accepted_by_subject,
                       COALESCE(r.tenant_id, ''), r.execution_fence, r.execution_state,
                       s.session_id, s.execution_fence, s.writer_issuer, s.writer_subject,
                       s.lifecycle_state, d.decision_id, d.state_version, d.decision_state
                FROM {_schema}.accepted_runs AS r
                INNER JOIN {_schema}.coordination_sessions AS s
                  ON s.project_id = r.project_id AND s.run_id = r.run_id
                 AND s.session_id = @session AND s.parent_session_id IS NULL
                INNER JOIN LATERAL (
                    SELECT decision_id, state_version, decision_state
                    FROM {_schema}.coordinator_decisions
                    WHERE project_id = r.project_id AND run_id = r.run_id AND session_id = s.session_id
                    ORDER BY state_version DESC
                    LIMIT 1
                ) AS d ON true
                WHERE r.project_id = @project AND r.run_id = @run
                """, connection, transaction);
            AddScope(command, identity);
            command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new CoordinationException(
                    "source_control_run_unavailable", StatusCodes.Status404NotFound);
            binding = new OwnerBindingSnapshot(
                reader.GetString(0).TrimEnd(),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt64(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetGuid(11),
                reader.GetInt64(12),
                reader.GetString(13));
        }

        if (binding.AcceptedIssuer != actor.Issuer ||
            binding.AcceptedSubject != actor.Subject ||
            binding.TenantId != tenantId ||
            binding.AcceptedSelectionHash != selectionHash ||
            binding.RootSessionId != identity.SessionId ||
            binding.WriterIssuer != actor.Issuer ||
            binding.WriterSubject != actor.Subject ||
            binding.RunFence != binding.SessionFence)
            throw new CoordinationException(
                "source_control_run_binding_changed", StatusCodes.Status409Conflict);
        return binding;
    }

    /// <summary>
    /// Locks the accepted run and its root coordination session so concurrent state changes cannot pass a stale owner check.
    /// </summary>
    private async Task<OwnerBindingScope> LockOwnerRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity identity,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT r.accepted_selection_hash, r.accepted_by_issuer, r.accepted_by_subject,
                   COALESCE(r.tenant_id, ''), r.execution_fence, r.execution_state,
                   s.session_id, s.execution_fence, s.writer_issuer, s.writer_subject,
                   s.lifecycle_state
            FROM {_schema}.accepted_runs AS r
            INNER JOIN {_schema}.coordination_sessions AS s
              ON s.project_id = r.project_id AND s.run_id = r.run_id
             AND s.session_id = @session AND s.parent_session_id IS NULL
            WHERE r.project_id = @project AND r.run_id = @run
            FOR UPDATE OF r, s
            """, connection, transaction);
        AddScope(command, identity);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new CoordinationException(
                "source_control_run_unavailable", StatusCodes.Status404NotFound);
        return new OwnerBindingScope(
            reader.GetString(0).TrimEnd(),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt64(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10));
    }

    /// <summary>
    /// Reads the newest decision identifier, state version, and decision state for the root session.
    /// </summary>
    private async Task<(Guid DecisionId, long StateVersion, string DecisionState)?> ReadLatestOwnerDecisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity identity,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT decision_id, state_version, decision_state
            FROM {_schema}.coordinator_decisions
            WHERE project_id = @project AND run_id = @run AND session_id = @session
            ORDER BY state_version DESC
            LIMIT 1
            """, connection, transaction);
        AddScope(command, identity);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, identity.SessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetGuid(0), reader.GetInt64(1), reader.GetString(2))
            : null;
    }

    /// <summary>
    /// Requires owner rows and the accepted run to match current selection revisions, root session, tenant, fence, and accepted active state.
    /// </summary>
    private static void EnsureCurrentBinding(
        OwnerBindingSnapshot owner,
        AuthorizedRunSelection selection,
        SourceControlAcceptedRunBinding acceptedRun)
    {
        var effective = selection.Selection;
        if (owner.ExecutionState == "completed" ||
            owner.SessionLifecycle != "active" ||
            owner.DecisionState != "accepted" ||
            acceptedRun.AcceptedSelectionHash != owner.AcceptedSelectionHash ||
            acceptedRun.ProjectRevision != effective.ProjectRevision ||
            acceptedRun.ProjectConfigurationRevision != effective.ProjectConfigurationRevision ||
            acceptedRun.PlatformRuntimeRevision != effective.PlatformRuntimeRevision ||
            acceptedRun.ContextRevision != effective.ContextRevision ||
            acceptedRun.Fence != owner.RunFence ||
            acceptedRun.TenantId != selection.Authorization.TenantId ||
            acceptedRun.ProjectId != effective.ProjectId ||
            acceptedRun.RunId != effective.RunId ||
            acceptedRun.RootSessionId != owner.RootSessionId)
            throw new CoordinationException(
                "source_control_run_binding_changed", StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Reconstructs a merge intent and verifies that the serialized pin and row-level accepted-run binding agree.
    /// </summary>
    private SourceControlMergeIntentSnapshot ReadIntentSnapshot(NpgsqlDataReader reader)
    {
        var pin = RestorePin(reader.GetString(35))
            ?? throw new CoordinationException(
                "source_control_pin_contract_invalid", StatusCodes.Status503ServiceUnavailable);
        CoordinatorGateDecisionReceipt? approvalReceipt = null;
        if (!reader.IsDBNull(31))
        {
            approvalReceipt = JsonSerializer.Deserialize<CoordinatorGateDecisionReceipt>(
                reader.GetString(31), JsonOptions)
                ?? throw new CoordinationException(
                    "source_control_approval_contract_invalid", StatusCodes.Status503ServiceUnavailable);
        }
        var grantReference = !reader.IsDBNull(36) && !reader.IsDBNull(37)
            ? new ExecutableActionGrantReference(reader.GetString(36), reader.GetString(37))
            : null;
        var acceptedRun = new SourceControlAcceptedRunBinding(
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            pin.AcceptedRun.ProjectId,
            pin.AcceptedRun.RunId,
            reader.GetString(1),
            reader.GetString(6).TrimEnd(),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetString(10),
            reader.GetInt64(11));
        if (pin.AcceptedRun != acceptedRun)
            throw new CoordinationException(
                "source_control_intent_contract_invalid", StatusCodes.Status503ServiceUnavailable);
        return new SourceControlMergeIntentSnapshot(
            reader.GetString(0),
            acceptedRun,
            pin,
            reader.GetInt64(12),
            reader.GetGuid(13),
            reader.GetString(14),
            reader.GetString(15),
            reader.GetString(16),
            reader.GetString(17),
            reader.GetString(18),
            reader.GetString(19),
            reader.GetString(20),
            reader.GetString(21),
            reader.GetInt64(22),
            reader.GetString(23),
            reader.GetString(24),
            reader.GetString(25),
            reader.GetString(26),
            ParseDatabaseMergeMethod(reader.GetString(27)),
            reader.GetString(28),
            reader.IsDBNull(29) ? null : reader.GetGuid(29),
            reader.IsDBNull(30) ? null : reader.GetInt64(30),
            approvalReceipt,
            reader.IsDBNull(32) ? null : reader.GetString(32),
            reader.IsDBNull(33) ? null : reader.GetString(33),
            reader.GetFieldValue<DateTimeOffset>(34),
            grantReference);
    }

    /// <summary>
    /// Serializes immutable provider, repository, GitHub App, and versioned secret-reference data without credential values.
    /// </summary>
    private static StoredRepositoryPin CapturePin(SourceControlRepositoryPin pin) =>
        new(
            pin.PinId,
            pin.AcceptedRun,
            pin.ProviderBinding.ProviderId,
            pin.ProviderBinding.AdapterVersion.ToString(),
            pin.ProviderBinding.OptionsSchemaVersion,
            pin.ProviderBinding.OptionsRevision,
            pin.ProviderBinding.Hosting,
            pin.ProviderBinding.Resource.ResourceId,
            pin.ProviderBinding.Resource.Generation,
            pin.ProviderBinding.NegotiatedCapabilities.Order(StringComparer.Ordinal).ToArray(),
            pin.Repository.Owner,
            pin.Repository.Name,
            CaptureOptionalCredential(pin.ApiCredential),
            CaptureOptionalCredential(pin.CheckoutCredential),
            CaptureOptionalCredential(pin.WebhookCredential),
            pin.GitHubAppBinding is { } appBinding
                ? new StoredGitHubAppBinding(
                    appBinding.IdentityConnectionId,
                    appBinding.IdentityConnectionRevision,
                    appBinding.InstallationId,
                    appBinding.PermissionDigest,
                    appBinding.IdentityRepositorySelectionHash,
                    appBinding.IssueWriteGranted)
                : null,
            pin.ProviderRepositoryId,
            pin.DefaultBranch,
            pin.IsPrivate,
            pin.PinnedAt);

    /// <summary>
    /// Deserializes a pin and resolves its provider version and capabilities through the configured catalog; invalid or unavailable bindings fail closed.
    /// </summary>
    private SourceControlRepositoryPin RestorePin(string json) =>
        RestorePin(json, providerCatalog, providerResolver);

    /// <summary>
    /// Deserializes a pin and resolves its provider version and capabilities through the configured catalog; invalid or unavailable bindings fail closed.
    /// </summary>
    internal static SourceControlRepositoryPin RestorePin(
        string json,
        ProviderCatalog providerCatalog,
        ProviderResolver providerResolver)
    {
        ArgumentNullException.ThrowIfNull(providerCatalog);
        ArgumentNullException.ThrowIfNull(providerResolver);
        var stored = JsonSerializer.Deserialize<StoredRepositoryPin>(json, JsonOptions)
            ?? throw new CoordinationException(
                "source_control_pin_contract_invalid", StatusCodes.Status503ServiceUnavailable);
        if (!Version.TryParse(stored.AdapterVersion, out var adapterVersion) ||
            stored.OptionsSchemaVersion < 1 ||
            stored.ResourceGeneration < 1 ||
            stored.NegotiatedCapabilities.Length == 0 ||
            stored.NegotiatedCapabilities.Any(string.IsNullOrWhiteSpace) ||
            stored.NegotiatedCapabilities.Distinct(StringComparer.Ordinal).Count() !=
                stored.NegotiatedCapabilities.Length)
            throw new CoordinationException(
                "source_control_pin_contract_invalid", StatusCodes.Status503ServiceUnavailable);

        var negotiatedCapabilities = stored.NegotiatedCapabilities
            .ToImmutableHashSet(StringComparer.Ordinal);
        var resolution = providerResolver.Resolve(new ProviderResolutionRequest(
            ProviderSeam.SourceControl,
            providerCatalog.TryGetDefault(ProviderSeam.SourceControl, out var defaultProviderId) &&
            string.Equals(defaultProviderId, stored.ProviderId, StringComparison.Ordinal)
                ? null
                : stored.ProviderId,
            adapterVersion!,
            stored.OptionsSchemaVersion,
            negotiatedCapabilities));
        var candidate = resolution.IsSuccess ? resolution.Value?.Candidate : null;
        if (candidate is null ||
            candidate.ProviderId != stored.ProviderId ||
            candidate.OptionsRevision != stored.OptionsRevision ||
            candidate.Hosting != stored.Hosting)
            throw new CoordinationException(
                "source_control_pin_provider_unavailable", StatusCodes.Status503ServiceUnavailable);

        var resource = new ProviderResourceRef(
            ProviderSeam.SourceControl,
            stored.ProviderId,
            stored.ResourceId,
            stored.ResourceGeneration);
        var negotiation = new ResourceNegotiation(resource, negotiatedCapabilities);
        var bindingResult = providerResolver.Pin(
            stored.AcceptedRun.RunId, candidate, stored.ResourceId, negotiation);
        var binding = bindingResult.IsSuccess ? bindingResult.Value : null;
        if (binding is null)
            throw new CoordinationException(
                "source_control_pin_provider_unavailable", StatusCodes.Status503ServiceUnavailable);

        return new SourceControlRepositoryPin(
            stored.PinId,
            stored.AcceptedRun,
            binding,
            new SourceControlRepositoryIdentity(stored.RepositoryOwner, stored.RepositoryName),
            stored.ApiCredential is null ? null : RestoreCredential(stored.ApiCredential),
            stored.CheckoutCredential is null ? null : RestoreCredential(stored.CheckoutCredential),
            stored.WebhookCredential is null ? null : RestoreCredential(stored.WebhookCredential),
            stored.ProviderRepositoryId,
            stored.DefaultBranch,
            stored.IsPrivate,
            stored.PinnedAt,
            stored.GitHubAppBinding is { } appBinding
                ? new SourceControlGitHubAppBinding(
                    appBinding.IdentityConnectionId,
                    appBinding.IdentityConnectionRevision,
                    appBinding.InstallationId,
                    appBinding.PermissionDigest,
                    appBinding.IdentityRepositorySelectionHash,
                    appBinding.IssueWriteGranted)
                : null);
    }

    /// <summary>
    /// Stores only a credential’s secret identifier, version, and purpose.
    /// </summary>
    private static StoredCredentialReference? CaptureOptionalCredential(
        SourceControlCredentialReference? credential) =>
        credential is null
            ? null
            : new StoredCredentialReference(credential.Secret.Id, credential.Secret.Version, credential.Purpose);

    /// <summary>
    /// Rebuilds a credential reference from its stored exact-version secret metadata.
    /// </summary>
    private static SourceControlCredentialReference RestoreCredential(
        StoredCredentialReference credential) =>
        new(new SecretRef(credential.SecretId, credential.SecretVersion), credential.Purpose);

    /// <summary>
    /// Checks whether a previously stored idempotent intent represents the same actor, selection, pin, workflow step, pull-request revisions, and method.
    /// </summary>
    private static bool MatchesRequest(
        SourceControlMergeIntentSnapshot saved,
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        SourceControlMergeIntentRequest request) =>
        saved.IntentId == request.IntentId &&
        saved.ApprovalRequestId == request.ApprovalRequestId &&
        saved.AcceptedRun.Issuer == actor.Issuer &&
        saved.AcceptedRun.Subject == actor.Subject &&
        saved.AcceptedRun.TenantId == selection.Authorization.TenantId &&
        saved.AcceptedRun.AcceptedSelectionHash == request.AcceptedRun.AcceptedSelectionHash &&
        saved.AcceptedRun.Fence == request.AcceptedRun.Fence &&
        saved.Pin.PinId == request.RepositoryPin.PinId &&
        saved.SourceStateVersion == request.SourceStateVersion &&
        saved.WorkflowId == request.WorkflowId &&
        saved.DefinitionRevision == request.DefinitionRevision &&
        saved.WorkPlanId == request.WorkPlanId &&
        saved.AssemblyRequestId == request.AssemblyRequestId &&
        saved.WorkflowStepId == request.WorkflowStepId &&
        saved.PullRequestNumber == request.PullRequest.Number &&
        saved.HeadBranch == request.PullRequest.HeadBranch &&
        saved.HeadSha == request.PullRequest.HeadSha &&
        saved.BaseBranch == request.PullRequest.BaseBranch &&
        saved.BaseSha == request.PullRequest.BaseSha &&
        saved.Method == request.Method;

    /// <summary>
    /// Compares all immutable run, provider, repository, credential-reference, GitHub App, and timestamp fields of two pins.
    /// </summary>
    private static bool SamePin(SourceControlRepositoryPin stored, SourceControlRepositoryPin proposed) =>
        stored.PinId == proposed.PinId &&
        stored.AcceptedRun == proposed.AcceptedRun &&
        SameProviderBinding(stored.ProviderBinding, proposed.ProviderBinding) &&
        SameRepositoryIdentity(stored.Repository, proposed.Repository) &&
        SameCredential(stored.ApiCredential, proposed.ApiCredential) &&
        SameCredential(stored.CheckoutCredential, proposed.CheckoutCredential) &&
        SameCredential(stored.WebhookCredential, proposed.WebhookCredential) &&
        SameGitHubAppBinding(stored.GitHubAppBinding, proposed.GitHubAppBinding) &&
        stored.ProviderRepositoryId == proposed.ProviderRepositoryId &&
        stored.DefaultBranch == proposed.DefaultBranch &&
        stored.IsPrivate == proposed.IsPrivate &&
        stored.PinnedAt == proposed.PinnedAt;

    /// <summary>
    /// Compares repository owner and name without case sensitivity.
    /// </summary>
    private static bool SameRepositoryIdentity(
        SourceControlRepositoryIdentity left,
        SourceControlRepositoryIdentity right) =>
        string.Equals(left.Owner, right.Owner, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Compares provider, adapter, options, hosting, resource generation, run, and negotiated capabilities.
    /// </summary>
    private static bool SameProviderBinding(
        PinnedProviderBinding left,
        PinnedProviderBinding right) =>
        left.RunId == right.RunId &&
        left.Seam == right.Seam &&
        left.ProviderId == right.ProviderId &&
        left.AdapterVersion == right.AdapterVersion &&
        left.OptionsSchemaVersion == right.OptionsSchemaVersion &&
        left.OptionsRevision == right.OptionsRevision &&
        left.Hosting == right.Hosting &&
        left.Resource == right.Resource &&
        left.NegotiatedCapabilities.SetEquals(right.NegotiatedCapabilities);

    /// <summary>
    /// Compares optional credential references by exact secret identifier, version, and purpose.
    /// </summary>
    private static bool SameCredential(
        SourceControlCredentialReference? left,
        SourceControlCredentialReference? right) =>
        left is null
            ? right is null
            : right is not null &&
              left.Purpose == right.Purpose &&
              left.Secret.Id == right.Secret.Id &&
              left.Secret.Version == right.Secret.Version;

    /// <summary>
    /// Compares GitHub App connection, installation, permission digest, repository-selection hash, and issue-write permission.
    /// </summary>
    private static bool SameGitHubAppBinding(
        SourceControlGitHubAppBinding? left,
        SourceControlGitHubAppBinding? right) =>
        left is null
            ? right is null
            : right is not null &&
              left.IdentityConnectionId == right.IdentityConnectionId &&
              left.IdentityConnectionRevision == right.IdentityConnectionRevision &&
              left.InstallationId == right.InstallationId &&
              left.PermissionDigest == right.PermissionDigest &&
              left.IdentityRepositorySelectionHash == right.IdentityRepositorySelectionHash &&
              left.IssueWriteGranted == right.IssueWriteGranted;

    /// <summary>
    /// Converts a stored merge-method value to the contract enum and rejects unknown database values as invalid stored state.
    /// </summary>
    private static SourceControlMergeMethod ParseDatabaseMergeMethod(string value) =>
        value switch
        {
            "merge" => SourceControlMergeMethod.Merge,
            "squash" => SourceControlMergeMethod.Squash,
            "rebase" => SourceControlMergeMethod.Rebase,
            _ => throw new CoordinationException(
                "source_control_intent_contract_invalid", StatusCodes.Status503ServiceUnavailable)
        };

    /// <summary>
    /// Converts a defined merge-method enum to its persisted value and rejects undefined values.
    /// </summary>
    private static string ToDatabaseMergeMethod(SourceControlMergeMethod value) =>
        value switch
        {
            SourceControlMergeMethod.Merge => "merge",
            SourceControlMergeMethod.Squash => "squash",
            SourceControlMergeMethod.Rebase => "rebase",
            _ => throw new CoordinationException(
                "source_control_merge_method_invalid", StatusCodes.Status400BadRequest)
        };

    /// <summary>
    /// Adds project and run parameters to a database command.
    /// </summary>
    private static void AddScope(NpgsqlCommand command, SessionIdentity identity)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, identity.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, identity.RunId);
    }

    /// <summary>
    /// Adds accepted actor, tenant, selection digest, revisions, and execution fence parameters to a database command.
    /// </summary>
    private static void AddAcceptedRun(
        NpgsqlCommand command,
        SourceControlAcceptedRunBinding acceptedRun)
    {
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, acceptedRun.Issuer);
        command.Parameters.AddWithValue("actor", NpgsqlDbType.Varchar, acceptedRun.Subject);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, acceptedRun.TenantId);
        command.Parameters.AddWithValue(
            "selectionHash", NpgsqlDbType.Char, acceptedRun.AcceptedSelectionHash);
        command.Parameters.AddWithValue("projectRevision", NpgsqlDbType.Bigint, acceptedRun.ProjectRevision);
        command.Parameters.AddWithValue(
            "configurationRevision", NpgsqlDbType.Bigint, acceptedRun.ProjectConfigurationRevision);
        command.Parameters.AddWithValue(
            "platformRevision", NpgsqlDbType.Bigint, acceptedRun.PlatformRuntimeRevision);
        command.Parameters.AddWithValue(
            "contextRevision", NpgsqlDbType.Varchar, acceptedRun.ContextRevision);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, acceptedRun.Fence);
    }

    /// <summary>
    /// Adds a required secret identifier and exact version to a database command.
    /// </summary>
    private static void AddSecretReference(NpgsqlCommand command, string prefix, SecretRef secret)
    {
        command.Parameters.AddWithValue(prefix + "SecretId", NpgsqlDbType.Varchar, secret.Id);
        command.Parameters.AddWithValue(prefix + "SecretVersion", NpgsqlDbType.Varchar, secret.Version);
    }

    /// <summary>
    /// Adds nullable secret identifier and exact version parameters to a database command.
    /// </summary>
    private static void AddOptionalSecretReference(
        NpgsqlCommand command,
        string prefix,
        SecretRef? secret)
    {
        command.Parameters.AddWithValue(
            prefix + "SecretId", NpgsqlDbType.Varchar, (object?)secret?.Id ?? DBNull.Value);
        command.Parameters.AddWithValue(
            prefix + "SecretVersion", NpgsqlDbType.Varchar, (object?)secret?.Version ?? DBNull.Value);
    }

    /// <summary>
    /// Adds nullable GitHub App connection and permission-binding parameters to a database command.
    /// </summary>
    private static void AddOptionalAppBinding(
        NpgsqlCommand command,
        SourceControlGitHubAppBinding? binding)
    {
        command.Parameters.AddWithValue(
            "githubAppConnectionId",
            NpgsqlDbType.Varchar,
            (object?)binding?.IdentityConnectionId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "githubAppConnectionRevision",
            NpgsqlDbType.Bigint,
            (object?)binding?.IdentityConnectionRevision ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "githubAppInstallationId",
            NpgsqlDbType.Bigint,
            (object?)binding?.InstallationId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "githubAppPermissionDigest",
            NpgsqlDbType.Char,
            (object?)binding?.PermissionDigest ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "githubAppSelectionHash",
            NpgsqlDbType.Char,
            (object?)binding?.IdentityRepositorySelectionHash ?? DBNull.Value);
    }

    /// <summary>
    /// Computes a SHA-256 digest of the exact accepted selection snapshot text for stable run binding.
    /// </summary>
    internal static string HashSelection(EffectiveRunSelection selection) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(selection.Snapshot.GetRawText())));

    /// <summary>
    /// Checks a bounded identifier against the allowed ASCII character set used by owner records.
    /// </summary>
    internal static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    /// <summary>
    /// Rejects an idempotency key that is not a valid bounded owner identifier.
    /// </summary>
    private static void ValidateIdempotencyKey(string value)
    {
        if (!IsIdentifier(value))
            throw new CoordinationException(
                "source_control_idempotency_key_invalid", StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Validates a non-reserved lowercase PostgreSQL schema name before quoting it for fixed table names.
    /// </summary>
    private static string QuoteSchema(string value)
    {
        if (value is null ||
            !System.Text.RegularExpressions.Regex.IsMatch(
                value, "^[a-z][a-z0-9_]{0,62}\\z",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            value is "public" or "pg_catalog" or "information_schema" ||
            value.StartsWith("pg_", StringComparison.Ordinal))
            throw new ArgumentException("A non-reserved lowercase schema identifier is required.", nameof(value));
        return $"\"{value}\"";
    }

    /// <summary>
    /// The locked owner-row fields used to validate run, session, actor, and execution-fence consistency.
    /// </summary>
    private sealed record OwnerBindingScope(
        string AcceptedSelectionHash,
        string AcceptedIssuer,
        string AcceptedSubject,
        string TenantId,
        long RunFence,
        string ExecutionState,
        string RootSessionId,
        long SessionFence,
        string WriterIssuer,
        string WriterSubject,
        string SessionLifecycle);

    /// <summary>
    /// The accepted run, root session, latest decision, and owner fields checked before Source Control state is read or changed.
    /// </summary>
    internal sealed record OwnerBindingSnapshot(
        string AcceptedSelectionHash,
        string AcceptedIssuer,
        string AcceptedSubject,
        string TenantId,
        long RunFence,
        string ExecutionState,
        string RootSessionId,
        long SessionFence,
        string WriterIssuer,
        string WriterSubject,
        string SessionLifecycle,
        Guid DecisionId,
        long StateVersion,
        string DecisionState);

    /// <summary>
    /// The JSON-safe repository pin, including provider revisions and secret references but no credential values.
    /// </summary>
    private sealed record StoredRepositoryPin(
        string PinId,
        SourceControlAcceptedRunBinding AcceptedRun,
        string ProviderId,
        string AdapterVersion,
        int OptionsSchemaVersion,
        string OptionsRevision,
        ProviderHostingPattern Hosting,
        string ResourceId,
        long ResourceGeneration,
        string[] NegotiatedCapabilities,
        string RepositoryOwner,
        string RepositoryName,
        StoredCredentialReference? ApiCredential,
        StoredCredentialReference? CheckoutCredential,
        StoredCredentialReference? WebhookCredential,
        StoredGitHubAppBinding? GitHubAppBinding,
        long ProviderRepositoryId,
        string DefaultBranch,
        bool IsPrivate,
        DateTimeOffset PinnedAt);

    /// <summary>
    /// The persisted connection, installation, permissions, and selection digest for a GitHub App repository pin.
    /// </summary>
    private sealed record StoredGitHubAppBinding(
        string IdentityConnectionId,
        long IdentityConnectionRevision,
        long InstallationId,
        string PermissionDigest,
        string IdentityRepositorySelectionHash,
        bool IssueWriteGranted = false);

    /// <summary>
    /// The secret identifier, exact version, and purpose stored with a repository pin.
    /// </summary>
    private sealed record StoredCredentialReference(
        string SecretId,
        string SecretVersion,
        string Purpose);
}

/// <summary>
/// A persisted capture proof, admission state, Events reference, and repository artifact bytes.
/// </summary>
internal sealed record SourceControlOutputCaptureRecord(
    ProducedRunCaptureProof Proof,
    string State,
    string? ObjectKey,
    long? EventPosition,
    DateTimeOffset? AdmittedAt,
    byte[] ManifestBytes,
    byte[] PatchBytes,
    byte[] PackageBytes);

/// <summary>
/// The proof and Events journal position used to page admitted captures.
/// </summary>
internal sealed record SourceControlOutputCaptureSummary(
    ProducedRunCaptureProof Proof,
    long EventPosition);

/// <summary>
/// The durable merge request, approval receipt, current grant reference, and recorded outcome for one intent.
/// </summary>
internal sealed record SourceControlMergeIntentSnapshot(
    string IntentId,
    SourceControlAcceptedRunBinding AcceptedRun,
    SourceControlRepositoryPin Pin,
    long SourceStateVersion,
    Guid SourceDecisionId,
    string SourceRequestId,
    string IdempotencyKey,
    string WorkflowId,
    string DefinitionRevision,
    string WorkPlanId,
    string AssemblyRequestId,
    string WorkflowStepId,
    string ApprovalRequestId,
    long PullRequestNumber,
    string HeadBranch,
    string HeadSha,
    string BaseBranch,
    string BaseSha,
    SourceControlMergeMethod Method,
    string State,
    Guid? ApprovalDecisionId,
    long? ApprovalStateVersion,
    CoordinatorGateDecisionReceipt? ApprovalReceipt,
    string? MergeSha,
    string? LastFailureCode,
    DateTimeOffset CreatedAt,
    ExecutableActionGrantReference? GrantReference);

/// <summary>
/// An asynchronous lease for the PostgreSQL advisory lock that serializes merges to one repository.
/// </summary>
internal sealed class SourceControlRepositoryMergeLock(
    NpgsqlConnection connection,
    string repositoryKey) : IAsyncDisposable
{
    /// <summary>
    /// Releases the repository advisory lock and always disposes its dedicated PostgreSQL connection.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(hashtext('agentweaver.source-control.merge'), hashtext(@repository))",
                connection);
            command.Parameters.AddWithValue("repository", NpgsqlDbType.Text, repositoryKey);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
