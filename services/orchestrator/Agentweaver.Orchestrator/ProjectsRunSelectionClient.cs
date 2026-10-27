using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Reads accepted run selection and authorization from the Projects owner.
/// It also resolves pinned remote-tool snapshots and verifies the runtime skill projection against accepted pins.
/// </summary>
internal sealed class ProjectsRunSelectionClient(
    HttpClient httpClient,
    OrchestratorOptions options,
    IReviewedRemoteToolSnapshotResolver snapshotStore)
{
    private const int MaxP1SetupReceiptBytes = 16 * 1024;
    private static readonly string[] P1SetupReceiptPropertyNames =
    [
        "invocationId", "issuer", "subject", "tenantId", "membershipId", "membershipRevision",
        "projectId", "createdByActorId", "projectRevision", "projectConfigurationRevision",
        "assignmentId", "assignmentRevision", "authorityAuditEventId", "createdAt",
        "ownerAssignmentId", "ownerAssignmentRevision", "ownerAuthorityAuditEventId"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>Reads current project authority without requiring an accepted execution.</summary>
    /// <exception cref="CoordinationException">The caller is run-bound or lacks the current project permission.</exception>
    internal async Task<ProjectsAuthorizationContext> ReadBacklogProjectAuthorityAsync(
        HttpContext context, string projectId, bool write, CancellationToken cancellationToken)
    {
        CoordinationIdentity.ValidateIdentity(projectId, "project_id");
        var actor = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        var projects = context.User.FindAll("project_id").ToArray();
        var scopes = context.User.FindAll("scope").Concat(context.User.FindAll("scp"))
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);
        if (context.User.HasClaim(claim => claim.Type is "run_id" or "purpose") ||
            projects.Length > 1 || projects.Length == 1 && projects[0].Value != projectId ||
            !CoordinationIdentity.HasAudience(context.User, options.Audience) ||
            !CoordinationIdentity.HasAudience(context.User, options.ProjectsAudience) ||
            !scopes.Contains("api.read") || write && !scopes.Contains("projects.admin"))
            throw new CoordinationException("backlog_project_permission_denied", StatusCodes.Status403Forbidden);
        var authority = await ReadAuthorizationAsync(
            RequireOwnerUri(options.ProjectsOwnerBaseAddress), CoordinationIdentity.RequireBearer(context),
            CoordinationIdentity.ReadTenantSelector(context), cancellationToken).ConfigureAwait(false);
        if (authority.ContractVersion != 1 || authority.Issuer != actor.Issuer ||
            authority.ActorId != actor.Subject || string.IsNullOrWhiteSpace(authority.TenantId) ||
            authority.MembershipRevision < 1 || authority.BoundRunId is not null ||
            authority.BoundProjectId is not null && authority.BoundProjectId != projectId ||
            authority.EffectiveAuthority.IsDefault ||
            !authority.EffectiveAuthority.Any(item =>
                (item.ResourceType == "project" && item.ResourceId == projectId ||
                 item.ResourceType == "tenant" && item.ResourceId == authority.TenantId) &&
                !item.Permissions.IsDefault && item.Permissions.Any(permission =>
                    permission.Permission == (write ? "writeProjects" : "readProjects") &&
                    permission.RoleRevision > 0)))
            throw new CoordinationException("backlog_project_permission_denied", StatusCodes.Status403Forbidden);
        if (!authority.EffectiveAuthority.Any(item =>
            item.ResourceType == "project" && item.ResourceId == projectId &&
            !item.Permissions.IsDefault && item.Permissions.Any(permission =>
                permission.Permission == (write ? "writeProjects" : "readProjects") && permission.RoleRevision > 0)))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(RequireOwnerUri(options.ProjectsOwnerBaseAddress),
                    $"/api/projects/{Uri.EscapeDataString(projectId)}"));
            request.Headers.Authorization = CoordinationIdentity.RequireBearer(context);
            request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", authority.TenantId);
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                throw new CoordinationException("backlog_project_permission_denied", StatusCodes.Status403Forbidden);
            if (response.StatusCode != HttpStatusCode.OK || response.Headers.CacheControl?.NoStore != true)
                throw new CoordinationException("backlog_project_unavailable", StatusCodes.Status502BadGateway);
            try
            {
                using var project = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (project.RootElement.ValueKind != JsonValueKind.Object ||
                    !project.RootElement.TryGetProperty("projectId", out var id) ||
                    id.ValueKind != JsonValueKind.String || id.GetString() != projectId)
                    throw new CoordinationException("backlog_project_contract_invalid", StatusCodes.Status502BadGateway);
            }
            catch (JsonException)
            {
                throw new CoordinationException("backlog_project_contract_invalid", StatusCodes.Status502BadGateway);
            }
        }
        return authority;
    }

    internal async Task<IssueSyncProjectProof> ReadTeamSyncProjectProofAsync(
        HttpContext context,
        string projectId,
        ProjectsAuthorizationContext authorization,
        CancellationToken cancellationToken)
    {
        // Resolve the current repository from Projects instead of trusting repo identity supplied by the caller.
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(RequireOwnerUri(options.ProjectsOwnerBaseAddress),
                $"/api/projects/{Uri.EscapeDataString(projectId)}/issue-sync-authority"));
        request.Headers.Authorization = CoordinationIdentity.RequireBearer(context);
        var tenant = CoordinationIdentity.ReadTenantSelector(context);
        if (tenant is not null)
            request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", tenant);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            throw new CoordinationException("team_sync_project_permission_denied", StatusCodes.Status403Forbidden);
        if (response.StatusCode != HttpStatusCode.OK || response.Headers.CacheControl?.NoStore != true)
            throw new CoordinationException("team_sync_project_unavailable", StatusCodes.Status502BadGateway);

        try
        {
            await response.Content.LoadIntoBufferAsync(64 * 1024, cancellationToken).ConfigureAwait(false);
            var proof = await response.Content.ReadFromJsonAsync<IssueSyncProjectProof>(
                JsonOptions, cancellationToken).ConfigureAwait(false);
            if (proof is null ||
                proof.ProjectId != projectId ||
                proof.TenantId != authorization.TenantId ||
                proof.CreatorId != authorization.ActorId ||
                string.IsNullOrWhiteSpace(proof.ConnectionId) ||
                proof.Repository is null)
                throw new CoordinationException(
                    "team_sync_project_contract_invalid", StatusCodes.Status502BadGateway);
            return proof;
        }
        catch (JsonException exception)
        {
            throw new CoordinationException(
                "team_sync_project_contract_invalid", StatusCodes.Status502BadGateway, exception);
        }
    }

    internal async Task<ProjectTeamSyncProjection> ReadTeamSyncProjectProjectionAsync(
        HttpContext context,
        string projectId,
        ProjectsAuthorizationContext authorization,
        IssueSyncProjectProof expectedProof,
        long? expectedConfigurationRevision,
        CancellationToken cancellationToken)
    {
        // Projects owns the only team roster; an optional revision fence rejects stale previews before repository writes.
        var revisionQuery = expectedConfigurationRevision is { } revision
            ? "?expectedConfigurationRevision=" + revision.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(RequireOwnerUri(options.ProjectsOwnerBaseAddress),
                $"/api/projects/{Uri.EscapeDataString(projectId)}/team-sync-projection{revisionQuery}"));
        request.Headers.Authorization = CoordinationIdentity.RequireBearer(context);
        var tenant = CoordinationIdentity.ReadTenantSelector(context);
        if (tenant is not null)
            request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", tenant);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            throw new CoordinationException("team_sync_project_permission_denied", StatusCodes.Status403Forbidden);
        if (response.StatusCode == HttpStatusCode.Conflict && expectedConfigurationRevision is not null)
            throw new CoordinationException("team_sync_configuration_changed", StatusCodes.Status409Conflict);
        if (response.StatusCode != HttpStatusCode.OK || response.Headers.CacheControl?.NoStore != true)
            throw new CoordinationException("team_sync_project_unavailable", StatusCodes.Status502BadGateway);

        try
        {
            await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            var projection = await response.Content.ReadFromJsonAsync<ProjectTeamSyncProjection>(
                JsonOptions, cancellationToken).ConfigureAwait(false);
            if (projection is null ||
                projection.ProjectId != projectId ||
                string.IsNullOrWhiteSpace(projection.ProjectName) ||
                projection.ProjectName.Length > 160 ||
                projection.Members.IsDefault ||
                projection.Members.Any(member => member is null || string.IsNullOrWhiteSpace(member.AgentId) ||
                    string.IsNullOrWhiteSpace(member.Name) || string.IsNullOrWhiteSpace(member.Role) ||
                    member.Charter is null || member.Order < 0) ||
                projection.Members.Select(member => member.AgentId).Distinct(StringComparer.Ordinal).Count() !=
                    projection.Members.Length)
                throw new CoordinationException(
                    "team_sync_project_contract_invalid", StatusCodes.Status502BadGateway);
            if (projection.ConfigurationRevision < 1 ||
                expectedConfigurationRevision is { } expected && projection.ConfigurationRevision != expected)
                throw new CoordinationException(
                    "team_sync_configuration_changed", StatusCodes.Status409Conflict);
            if (projection.Authority != expectedProof)
                throw new CoordinationException(
                    "team_sync_project_authority_changed", StatusCodes.Status409Conflict);
            return projection;
        }
        catch (JsonException exception)
        {
            throw new CoordinationException(
                "team_sync_project_contract_invalid", StatusCodes.Status502BadGateway, exception);
        }
    }

    /// <summary>Resolves fresh project authority before creating a run without a run-bound token.</summary>
    internal async Task<ProjectsAuthorizationContext> ReadProjectRunAuthorityAsync(
        HttpContext context, string projectId, CancellationToken cancellationToken,
        string requiredPermission = "acceptRunSelection")
    {
        var caller = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        CoordinationIdentity.RequireScopes(context.User);
        CoordinationIdentity.ValidateIdentity(projectId, "project_id");
        var projects = context.User.FindAll("project_id").Take(2).ToArray();
        if (projects.Length > 1 || projects.Length == 1 && projects[0].Value != projectId ||
            context.User.FindAll("run_id").Any() ||
            !CoordinationIdentity.HasAudience(context.User, options.ProjectsAudience) ||
            !CoordinationIdentity.HasAudience(context.User, options.Audience))
            throw new CoordinationException("caller_binding_mismatch", StatusCodes.Status403Forbidden);
        var owner = RequireOwnerUri(options.ProjectsOwnerBaseAddress);
        var bearer = CoordinationIdentity.RequireBearer(context);
        var tenant = CoordinationIdentity.ReadTenantSelector(context);
        var authority = await ReadAuthorizationAsync(owner, bearer, tenant, cancellationToken).ConfigureAwait(false);
        if (authority.ContractVersion != 1 || authority.Issuer != caller.Issuer ||
            authority.ActorId != caller.Subject || authority.MembershipRevision < 1 ||
            string.IsNullOrWhiteSpace(authority.TenantId) || authority.BoundRunId is not null ||
            authority.BoundProjectId is not null && authority.BoundProjectId != projectId ||
            authority.EffectiveAuthority.IsDefault ||
            !authority.EffectiveAuthority.Any(item =>
                (item.ResourceType == "project" && item.ResourceId == projectId ||
                 requiredPermission == "cancelRun" && item.ResourceType == "tenant" &&
                    item.ResourceId == authority.TenantId) && !item.Permissions.IsDefault &&
                item.Permissions.Any(permission => permission.Permission == requiredPermission &&
                    permission.RoleRevision > 0)))
            throw new CoordinationException(requiredPermission == "acceptRunSelection"
                ? "project_orchestrator_role_required_acceptRunSelection"
                : "run_cancel_permission_denied", StatusCodes.Status403Forbidden);
        return authority;
    }

    /// <summary>Creates or replays the interactive selection using the caller bearer and current Projects configuration.</summary>
    internal async Task CreateInteractiveSelectionAsync(
        HttpContext context, string projectId, string runId, long expectedRevision,
        CancellationToken cancellationToken)
    {
        var owner = RequireOwnerUri(options.ProjectsOwnerBaseAddress);
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(owner,
            $"/api/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}/interactive-selection"));
        request.Headers.Authorization = CoordinationIdentity.RequireBearer(context);
        var tenant = CoordinationIdentity.ReadTenantSelector(context);
        if (tenant is not null)
            request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", tenant);
        request.Content = JsonContent.Create(new { expectedProjectConfigRevision = expectedRevision });
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new CoordinationException("run_selection_permission_denied", StatusCodes.Status403Forbidden);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new CoordinationException("project_not_found", StatusCodes.Status404NotFound);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
            throw new CoordinationException("run_selection_conflict", (int)response.StatusCode);
        if (response.StatusCode != HttpStatusCode.OK || response.Headers.CacheControl?.NoStore != true)
            throw new CoordinationException("projects_run_selection_unavailable", StatusCodes.Status502BadGateway);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("projectId", out var project) || project.GetString() != projectId ||
            !root.TryGetProperty("runId", out var run) || run.GetString() != runId)
            throw new CoordinationException("projects_run_selection_contract_invalid", StatusCodes.Status502BadGateway);
    }

    /// <summary>Reads an existing setup receipt with the same unbound caller bearer before persisting a run request.</summary>
    internal async Task<P1ProjectSetupReceiptSnapshot> ReadP1SetupReceiptForCreationAsync(
        HttpContext context,
        string projectId,
        Guid invocationId,
        ProjectsAuthorizationContext authorization,
        long expectedProjectConfigurationRevision,
        CancellationToken cancellationToken)
    {
        var caller = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        RequireP1SetupAuthorization(
            caller, authorization, projectId, expectedRunId: null, options, context);
        var owner = RequireOwnerUri(options.ProjectsOwnerBaseAddress);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(owner,
            $"/api/p1/invocations/{invocationId:D}/setup"));
        request.Headers.Authorization = CoordinationIdentity.RequireBearer(context);
        request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", authorization.TenantId);
        var receipt = await ReadP1SetupReceiptAsync(request, invocationId, cancellationToken).ConfigureAwait(false);
        RequireP1SetupReceiptMatches(receipt, caller, authorization, projectId,
            expectedRunId: null, expectedProjectConfigurationRevision, expectedProjectRevision: null);
        return receipt;
    }

    /// <summary>Revalidates the original receipt through Projects' run-bound authority verifier.</summary>
    internal async Task<P1ProjectSetupReceiptSnapshot> ReadP1SetupReceiptForAcceptedRunAsync(
        HttpContext context,
        AuthorizedRunSelection selection,
        Guid invocationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var projectId = selection.Selection.ProjectId;
        var runId = selection.Selection.RunId;
        var caller = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        RequireP1SetupAuthorization(
            caller, selection.Authorization, projectId, runId, options, context);
        var owner = RequireOwnerUri(options.ProjectsOwnerBaseAddress);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(owner,
            $"/internal/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}/" +
            $"p1-setup/{invocationId:D}"));
        request.Headers.Authorization = CoordinationIdentity.RequireBearer(context);
        request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", selection.Authorization.TenantId);
        var receipt = await ReadP1SetupReceiptAsync(request, invocationId, cancellationToken).ConfigureAwait(false);
        RequireP1SetupReceiptMatches(receipt, caller, selection.Authorization, projectId, runId,
            selection.Selection.ProjectConfigurationRevision, selection.Selection.ProjectRevision);
        return receipt;
    }

    private async Task<P1ProjectSetupReceiptSnapshot> ReadP1SetupReceiptAsync(
        HttpRequestMessage request,
        Guid invocationId,
        CancellationToken cancellationToken)
    {
        if (invocationId == Guid.Empty)
            throw new CoordinationException("p1_setup_receipt_invalid", StatusCodes.Status400BadRequest);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new CoordinationException("p1_setup_receipt_permission_denied", StatusCodes.Status403Forbidden);
        if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Conflict)
            throw new CoordinationException("p1_setup_receipt_stale", StatusCodes.Status409Conflict);
        if (response.StatusCode != HttpStatusCode.OK || response.Headers.CacheControl?.NoStore != true)
            throw new CoordinationException("p1_setup_receipt_unavailable", StatusCodes.Status502BadGateway);
        if (response.Content.Headers.ContentLength is > MaxP1SetupReceiptBytes)
            throw new CoordinationException("p1_setup_receipt_contract_invalid", StatusCodes.Status502BadGateway);
        try
        {
            await response.Content.LoadIntoBufferAsync(MaxP1SetupReceiptBytes, cancellationToken)
                .ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                stream, new JsonDocumentOptions { MaxDepth = 8 }, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw InvalidP1SetupReceipt();
            var names = new HashSet<string>(StringComparer.Ordinal);
            // Validate the wire shape before deserialization can hide duplicate or missing receipt fields.
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name))
                    throw InvalidP1SetupReceipt();
            if (names.Count != P1SetupReceiptPropertyNames.Length ||
                P1SetupReceiptPropertyNames.Any(name => !names.Contains(name)))
                throw InvalidP1SetupReceipt();
            var receipt = root.Deserialize<P1ProjectSetupReceiptSnapshot>(JsonOptions);
            if (receipt is null || receipt.InvocationId != invocationId ||
                receipt.MembershipId == Guid.Empty || receipt.AssignmentId == Guid.Empty ||
                receipt.AuthorityAuditEventId == Guid.Empty ||
                receipt.OwnerAssignmentId == Guid.Empty ||
                receipt.OwnerAuthorityAuditEventId == Guid.Empty ||
                receipt.MembershipRevision < 1 || receipt.ProjectRevision < 1 ||
                receipt.ProjectConfigurationRevision < 1 || receipt.AssignmentRevision < 1 ||
                (receipt.OwnerAssignmentId is null) != (receipt.OwnerAssignmentRevision is null) ||
                receipt.OwnerAssignmentRevision is <= 0 ||
                !HasBoundedText(receipt.Issuer, 2048) || !HasBoundedText(receipt.Subject, 512) ||
                !HasBoundedText(receipt.TenantId, 256) || !HasBoundedText(receipt.ProjectId, 256) ||
                !HasBoundedText(receipt.CreatedByActorId, 256) || receipt.CreatedAt == default)
                throw InvalidP1SetupReceipt();
            return receipt;
        }
        catch (JsonException exception)
        {
            throw new CoordinationException(
                "p1_setup_receipt_contract_invalid", StatusCodes.Status502BadGateway, exception);
        }
        catch (HttpRequestException exception)
        {
            throw new CoordinationException(
                "p1_setup_receipt_contract_invalid", StatusCodes.Status502BadGateway, exception);
        }
    }

    private static void RequireP1SetupAuthorization(
        CoordinationActor caller,
        ProjectsAuthorizationContext authorization,
        string projectId,
        string? expectedRunId,
        OrchestratorOptions options,
        HttpContext context)
    {
        var boundProjectIds = context.User.FindAll("project_id").Take(2).ToArray();
        var boundRunIds = context.User.FindAll("run_id").Take(2).ToArray();
        if (authorization.ContractVersion != 1 || authorization.Issuer != caller.Issuer ||
            authorization.ActorId != caller.Subject || string.IsNullOrWhiteSpace(authorization.TenantId) ||
            authorization.MembershipRevision < 1 || authorization.BoundProjectId !=
                (expectedRunId is null ? null : projectId) ||
            authorization.BoundRunId != expectedRunId ||
            !CoordinationIdentity.HasAudience(context.User, options.Audience) ||
            !CoordinationIdentity.HasAudience(context.User, options.ProjectsAudience) ||
            boundProjectIds.Length != (expectedRunId is null ? 0 : 1) ||
            boundProjectIds.Length == 1 && boundProjectIds[0].Value != projectId ||
            boundRunIds.Length != (expectedRunId is null ? 0 : 1) ||
            boundRunIds.Length == 1 && boundRunIds[0].Value != expectedRunId ||
            !HasScope(context.User, "api.read") ||
            expectedRunId is null && context.User.HasClaim(claim => claim.Type == "run_id") ||
            !HasScope(context.User, "projects.orchestrator") ||
            expectedRunId is null && !HasScope(context.User, "projects.admin"))
            throw new CoordinationException("p1_setup_receipt_permission_denied", StatusCodes.Status403Forbidden);
    }

    private static bool HasScope(System.Security.Claims.ClaimsPrincipal principal, string scope) =>
        principal.FindAll("scope").Concat(principal.FindAll("scp"))
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(scope, StringComparer.Ordinal);

    private static bool HasBoundedText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength;

    private static void RequireP1SetupReceiptMatches(
        P1ProjectSetupReceiptSnapshot receipt,
        CoordinationActor caller,
        ProjectsAuthorizationContext authorization,
        string projectId,
        string? expectedRunId,
        long expectedProjectConfigurationRevision,
        long? expectedProjectRevision)
    {
        if (receipt.Issuer != caller.Issuer || receipt.Subject != caller.Subject ||
            receipt.TenantId != authorization.TenantId ||
            receipt.MembershipRevision != authorization.MembershipRevision ||
            receipt.ProjectId != projectId || receipt.CreatedByActorId != caller.Subject ||
            receipt.ProjectConfigurationRevision != expectedProjectConfigurationRevision ||
            expectedProjectRevision is { } revision && receipt.ProjectRevision != revision ||
            expectedRunId is not null &&
                (authorization.BoundProjectId != projectId || authorization.BoundRunId != expectedRunId))
            throw new CoordinationException("p1_setup_receipt_stale", StatusCodes.Status409Conflict);
    }

    private static CoordinationException InvalidP1SetupReceipt() =>
        new("p1_setup_receipt_contract_invalid", StatusCodes.Status502BadGateway);

    /// <summary>
    /// Reads fresh caller-wide authority for cross-run attention. Only unbound tokens are accepted;
    /// a project or run binding does not narrow this caller-wide endpoint.
    /// </summary>
    internal async Task<ProjectsAuthorizationContext> ReadNotificationAuthorityAsync(
        HttpContext context, CancellationToken cancellationToken)
    {
        var caller = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        var scopes = context.User.FindAll("scope").Concat(context.User.FindAll("scp"))
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!scopes.Contains("api.read", StringComparer.Ordinal) ||
            !HasAudience(context.User, options.Audience) ||
            !HasAudience(context.User, options.ProjectsAudience) ||
            context.User.HasClaim(claim => claim.Type is "project_id" or "run_id"))
            throw new CoordinationException("caller_binding_mismatch", StatusCodes.Status403Forbidden);
        var tenant = CoordinationIdentity.ReadTenantSelector(context);
        var authority = await ReadAuthorizationAsync(
            RequireOwnerUri(options.ProjectsOwnerBaseAddress), CoordinationIdentity.RequireBearer(context),
            tenant, cancellationToken).ConfigureAwait(false);
        if (authority.ContractVersion != 1 || authority.Issuer != options.Issuer ||
            authority.ActorId != caller.Subject || string.IsNullOrWhiteSpace(authority.TenantId) ||
            (tenant is not null && tenant != authority.TenantId) || authority.MembershipRevision < 1 ||
            authority.BoundProjectId is not null || authority.BoundRunId is not null ||
            authority.EffectiveAuthority.IsDefault ||
            authority.EffectiveAuthority.Any(item => item is null || item.Permissions.IsDefault ||
                item.Permissions.Any(permission => permission is null)))
            throw new CoordinationException("projects_authorization_contract_invalid", StatusCodes.Status502BadGateway);
        return authority;
    }

    /// <summary>
    /// Reads the run selection after requiring the caller's project-level accept-selection permission.
    /// </summary>
    public async Task<EffectiveRunSelection> ReadAcceptedSelectionAsync(
        HttpContext context,
        string projectId,
        string runId,
        CancellationToken cancellationToken) =>
        (await ReadSelectionWithAuthorityAsync(
            context, projectId, runId, "acceptRunSelection", cancellationToken).ConfigureAwait(false)).Selection;

    /// <summary>
    /// Reads the run selection after requiring the caller's project-level read-selection permission.
    /// </summary>
    public async Task<EffectiveRunSelection> ReadSelectionForReadAsync(
        HttpContext context,
        string projectId,
        string runId,
        CancellationToken cancellationToken) =>
        (await ReadSelectionWithAuthorityAsync(
            context, projectId, runId, "readRunSelection", cancellationToken).ConfigureAwait(false)).Selection;

    /// <summary>
    /// Reads accepted selection and refreshed authorization evidence for a dispatch or owner write.
    /// </summary>
    public Task<AuthorizedRunSelection> ReadAcceptedSelectionWithAuthorityAsync(
        HttpContext context,
        string projectId,
        string runId,
        CancellationToken cancellationToken) =>
        ReadSelectionWithAuthorityAsync(
            context, projectId, runId, "acceptRunSelection", cancellationToken, refreshAuthority: true);

    /// <summary>
    /// Reads a run selection for a read-only operation and refreshes its authorization evidence.
    /// </summary>
    internal Task<AuthorizedRunSelection> ReadSelectionForReadWithAuthorityAsync(
        HttpContext context, string projectId, string runId, CancellationToken cancellationToken) =>
        ReadSelectionWithAuthorityAsync(
            context, projectId, runId, "readRunSelection", cancellationToken, refreshAuthority: true);

    /// <summary>Reads the accepted selection using fresh unbound Contributor, Owner, or tenant-admin cancellation authority.</summary>
    internal Task<AuthorizedRunSelection> ReadSelectionForCancelAsync(
        HttpContext context, string projectId, string runId, CancellationToken cancellationToken) =>
        ReadSelectionWithAuthorityAsync(
            context, projectId, runId, "cancelRun", cancellationToken, refreshAuthority: true);

    /// <summary>
    /// Reads the agent's run skills and verifies their revisions and digests against accepted configuration pins.
    /// </summary>
    /// <param name="context">The authenticated request context forwarded to Projects.</param>
    /// <param name="registration">The runtime registration whose actor, agent, selection, and fence must match.</param>
    /// <param name="cancellationToken">Cancels the Projects and authorization calls.</param>
    /// <returns>The ordered, validated skill content for the assigned agent.</returns>
    /// <exception cref="CoordinationException">Thrown when caller authority, selection, owner response, or skill pins are invalid or stale.</exception>
    /// <remarks>Authorization is reread after the skill response, so an intervening role change rejects the projection.</remarks>
    public async Task<ImmutableArray<SkillRuntimeContentV1>> ReadAcceptedRunSkillsAsync(
        HttpContext context,
        RuntimeRegistration registration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        RuntimeContractValidation.Validate(registration);
        var binding = registration.Binding;
        var accepted = await ReadSelectionForReadWithAuthorityAsync(
            context, binding.ProjectId, binding.RunId, cancellationToken).ConfigureAwait(false);
        RequireSkillReadAuthority(accepted.Authorization, binding);
        var selection = accepted.Selection;
        if (selection.ProjectRevision != binding.ProjectRevision ||
            selection.ProjectConfigurationRevision != binding.ProjectConfigurationRevision ||
            selection.PlatformRuntimeRevision != binding.PlatformRuntimeRevision ||
            selection.ContextRevision != binding.ContextRevision ||
            CoordinationOwnerStore.HashSelection(selection).ToLowerInvariant() != binding.AcceptedSelectionHash)
            throw new CoordinationException(
                "runtime_skill_content_binding_mismatch", StatusCodes.Status403Forbidden);
        if (binding.AgentId == CoordinationEndpoints.CoordinatorRuntimeAgentId &&
            binding.WorkflowStepId is null)
        {
            // Coordinator roots are not cast agents and receive no project-assigned agent skills.
            return [];
        }
        var pins = ReadAssignedSkillPins(selection.Snapshot, binding.AgentId);

        var owner = RequireOwnerUri(options.ProjectsOwnerBaseAddress);
        var bearer = CoordinationIdentity.RequireBearer(context);
        var tenant = CoordinationIdentity.ReadTenantSelector(context);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(owner,
            $"/api/projects/{Uri.EscapeDataString(binding.ProjectId)}/runs/{Uri.EscapeDataString(binding.RunId)}" +
            $"/agents/{Uri.EscapeDataString(binding.AgentId)}/skills"));
        request.Headers.Authorization = bearer;
        if (tenant is not null)
            request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", tenant);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new CoordinationException("run_selection_permission_denied", StatusCodes.Status403Forbidden);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
            throw new CoordinationException("runtime_skill_content_unavailable", StatusCodes.Status409Conflict);
        if (response.StatusCode != HttpStatusCode.OK || response.Headers.CacheControl?.NoStore != true)
            throw InvalidRuntimeSkillContent();

        ImmutableArray<SkillRuntimeContentV1> skills;
        try
        {
            skills = await response.Content.ReadFromJsonAsync<ImmutableArray<SkillRuntimeContentV1>>(
                JsonOptions, cancellationToken).ConfigureAwait(false);
            if (skills.IsDefault || skills.Any(skill => skill is null ||
                skill.Resources.IsDefault || skill.Resources.Any(resource => resource is null)))
                throw InvalidRuntimeSkillContent();
            SkillRuntimeContentContract.ValidateProjection(new SkillRuntimeContentProjectionV1(
                SkillRuntimeContentContract.CurrentVersion,
                registration.RuntimeInstanceId,
                registration.Revision,
                binding.ProjectConfigurationRevision,
                binding.ExecutionFence,
                binding.TenantId,
                binding.ProjectId,
                binding.RunId,
                binding.SessionId,
                binding.AgentId,
                binding.AcceptedSelectionHash,
                skills));
        }
        catch (JsonException)
        {
            throw InvalidRuntimeSkillContent();
        }
        catch (ArgumentException)
        {
            throw InvalidRuntimeSkillContent();
        }
        if (skills.Length != pins.Length || skills.Where((skill, index) =>
            skill.SkillId != pins[index].SkillId || skill.Revision != pins[index].Revision ||
            skill.ContentDigest != pins[index].ContentDigest).Any())
            throw InvalidRuntimeSkillContent();

        var authority = await ReadAuthorizationAsync(owner, bearer, tenant, cancellationToken)
            .ConfigureAwait(false);
        RequireSkillReadAuthority(authority, binding);
        return skills;
    }

    /// <summary>
    /// Reads the current project-scope spend budgets so that a tightened project budget applies to the next dispatch.
    /// </summary>
    /// <param name="context">The authenticated request context forwarded to Projects.</param>
    /// <param name="projectId">The project that owns the budgets.</param>
    /// <param name="runId">The run whose dispatch is being admitted.</param>
    /// <param name="cancellationToken">Cancels the Projects call.</param>
    /// <returns>The validated current project-scope budgets.</returns>
    /// <exception cref="CoordinationException">Thrown when the caller is denied or the owner response is invalid.</exception>
    public async Task<CurrentProjectSpendBudgets> ReadCurrentProjectSpendBudgetsAsync(
        HttpContext context,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        // Call Projects as the original caller: forward the caller's bearer token and tenant
        // selector, so Projects applies its own authorization to the read.
        var owner = RequireOwnerUri(options.ProjectsOwnerBaseAddress);
        var bearer = CoordinationIdentity.RequireBearer(context);
        var tenant = CoordinationIdentity.ReadTenantSelector(context);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(owner,
            $"/api/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}/spend-budgets"));
        request.Headers.Authorization = bearer;
        if (tenant is not null)
            request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", tenant);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        // A denied read blocks the dispatch. It is never treated as "no budgets".
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new CoordinationException("run_selection_permission_denied", StatusCodes.Status403Forbidden);
        // A Projects owner without the spend budget route has no project budgets to enforce.
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new(projectId, 1, 1, []);
        // Budgets must be current, so a cacheable response is rejected like any other unexpected status.
        if (response.StatusCode != HttpStatusCode.OK || response.Headers.CacheControl?.NoStore != true)
            throw InvalidSelectionContract();
        try
        {
            // A null body, malformed JSON, or an invalid budget set is a broken owner contract (502).
            var current = await response.Content.ReadFromJsonAsync<CurrentProjectSpendBudgets>(
                JsonOptions, cancellationToken).ConfigureAwait(false) ?? throw InvalidSelectionContract();
            ValidateCurrentProjectSpendBudgets(current, projectId);
            return current;
        }
        catch (JsonException)
        {
            throw InvalidSelectionContract();
        }
    }

    /// <summary>
    /// Requires a current project budget response for the requested project that contains only valid project-scope budgets.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when the response is malformed.</exception>
    internal static void ValidateCurrentProjectSpendBudgets(CurrentProjectSpendBudgets current, string projectId)
    {
        try
        {
            // The response must be for the requested project, with real revisions. Revision 1 is
            // the lowest valid value.
            if (current is null || current.ProjectId != projectId || current.ProjectConfigurationRevision < 1 ||
                current.PlatformRuntimeRevision < 1)
                throw InvalidSelectionContract();
            // Apply the shared budget rules: each budget must be valid, and budget IDs and buckets must be unique.
            SpendBudgetContract.ValidateSet(current.Budgets);
            // This route returns project budgets only. Run and agent budgets come from the accepted
            // selection, so any other scope here is a contract error.
            if (current.Budgets.Any(budget => budget.Scope != SpendBudgetScopes.Project))
                throw InvalidSelectionContract();
        }
        catch (ArgumentException)
        {
            // Budget validation failures are reported as an invalid owner response.
            throw InvalidSelectionContract();
        }
    }

    private static CoordinationException InvalidSelectionContract() =>
        new("projects_run_selection_contract_invalid", StatusCodes.Status502BadGateway);

    /// <summary>
    /// Requires the Projects authorization context to grant this runtime binding read-selection access.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when actor, tenant, project, run, membership, or permission binding differs.</exception>
    private static void RequireSkillReadAuthority(
        ProjectsAuthorizationContext authority,
        RuntimeBinding binding)
    {
        if (authority.ContractVersion != 1 ||
            authority.Issuer != binding.ActorIssuer ||
            authority.ActorId != binding.ActorId ||
            authority.TenantId != binding.TenantId ||
            authority.BoundProjectId != binding.ProjectId ||
            authority.BoundRunId != binding.RunId ||
            authority.MembershipRevision < 1 ||
            authority.EffectiveAuthority.IsDefault ||
            !authority.EffectiveAuthority.Any(item =>
                item.ResourceType == "project" && item.ResourceId == binding.ProjectId &&
                !item.Permissions.IsDefault && item.Permissions.Any(permission =>
                    permission.Permission == "readRunSelection" && permission.RoleRevision > 0)))
            throw new CoordinationException("run_selection_permission_denied", StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Extracts enabled skill revision and digest pins assigned to the selected agent.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when accepted skill configuration is missing or malformed.</exception>
    private static ImmutableArray<(string SkillId, long Revision, string ContentDigest)> ReadAssignedSkillPins(
        JsonElement selection,
        string agentId)
    {
        if (!selection.TryGetProperty("projectConfiguration", out var configuration) ||
            configuration.ValueKind != JsonValueKind.Object ||
            !configuration.TryGetProperty("casting", out var casting) ||
            casting.ValueKind != JsonValueKind.Array)
            throw InvalidRuntimeSkillContent();
        if (!casting.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty("agentId", out var agent) && agent.ValueKind == JsonValueKind.String &&
            agent.GetString() == agentId))
            throw new CoordinationException(
                "runtime_skill_content_binding_mismatch", StatusCodes.Status403Forbidden);
        if (!configuration.TryGetProperty("skills", out var skills) || skills.ValueKind == JsonValueKind.Null)
            return [];
        if (skills.ValueKind != JsonValueKind.Array ||
            skills.GetArrayLength() > SkillRuntimeContentContract.MaxSkillCount)
            throw InvalidRuntimeSkillContent();
        var pins = new List<(string SkillId, long Revision, string ContentDigest, int Order)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var skill in skills.EnumerateArray())
        {
            if (skill.ValueKind != JsonValueKind.Object ||
                !skill.TryGetProperty("enabled", out var enabled) ||
                enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw InvalidRuntimeSkillContent();
            if (!enabled.GetBoolean())
                continue;
            if (!skill.TryGetProperty("revision", out var revision) || revision.ValueKind != JsonValueKind.Number ||
                !revision.TryGetInt64(out var revisionValue) ||
                revisionValue < 1 || !skill.TryGetProperty("contentDigest", out var digest) ||
                digest.ValueKind != JsonValueKind.String ||
                !skill.TryGetProperty("agentIds", out var agents) || agents.ValueKind != JsonValueKind.Array)
                throw new CoordinationException(
                    "runtime_skill_content_unavailable", StatusCodes.Status409Conflict);
            if (agents.GetArrayLength() == 0 || agents.EnumerateArray().Any(agent =>
                agent.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(agent.GetString())) ||
                !skill.TryGetProperty("skillId", out var id) || id.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(id.GetString()) ||
                !skill.TryGetProperty("order", out var order) || order.ValueKind != JsonValueKind.Number ||
                !order.TryGetInt32(out var orderValue) ||
                orderValue < 0 || digest.GetString() is not { Length: 64 } digestValue ||
                digestValue.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
                !seen.Add(id.GetString()!))
                throw InvalidRuntimeSkillContent();
            if (agents.EnumerateArray().Any(agent => agent.GetString() == agentId))
                pins.Add((id.GetString()!, revisionValue, digestValue, orderValue));
        }
        return pins.OrderBy(pin => pin.Order).ThenBy(pin => pin.SkillId, StringComparer.Ordinal)
            .Select(pin => (pin.SkillId, pin.Revision, pin.ContentDigest)).ToImmutableArray();
    }

    /// <summary>
    /// Creates the bad-gateway error used for an invalid runtime skill response or pin set.
    /// </summary>
    private static CoordinationException InvalidRuntimeSkillContent() =>
        new("runtime_skill_content_invalid", StatusCodes.Status502BadGateway);

    /// <summary>
    /// Reads and validates the current run selection using the requested permission and authority policy.
    /// </summary>
    /// <param name="requiredPermission">The project permission required before selection access.</param>
    /// <param name="refreshAuthority">Whether authorization is reread after resolving selection references.</param>
    /// <returns>The effective selection and the Projects authorization context that admitted it.</returns>
    /// <exception cref="CoordinationException">Thrown when caller scope, owner response, selection contract, or permission is invalid.</exception>
    private async Task<AuthorizedRunSelection> ReadSelectionWithAuthorityAsync(
        HttpContext context,
        string projectId,
        string runId,
        string requiredPermission,
        CancellationToken cancellationToken,
        bool refreshAuthority = false)
    {
        var caller = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        CoordinationIdentity.RequireScopes(context.User);
        var cancelling = requiredPermission == "cancelRun";
        // Cancellation uses fresh project authority; ordinary owner operations require the exact run binding.
        if (!cancelling)
        {
            var scope = CoordinationIdentity.RequireRunScope(context.User);
            if (scope.ProjectId != projectId || scope.RunId != runId)
                throw new CoordinationException("accepted_run_caller_mismatch", StatusCodes.Status403Forbidden);
        }
        if (!HasAudience(context.User, options.ProjectsAudience))
            throw new CoordinationException("accepted_run_caller_mismatch", StatusCodes.Status403Forbidden);
        var owner = RequireOwnerUri(options.ProjectsOwnerBaseAddress);
        var bearer = CoordinationIdentity.RequireBearer(context);
        var tenant = CoordinationIdentity.ReadTenantSelector(context);
        var authorization = cancelling
            ? await ReadProjectRunAuthorityAsync(context, projectId, cancellationToken, "cancelRun")
            : await ReadAuthorizationAsync(owner, bearer, tenant, cancellationToken).ConfigureAwait(false);

        if (!cancelling && (authorization.ContractVersion != 1 ||
            authorization.Issuer != options.Issuer ||
            authorization.ActorId != caller.Subject ||
            authorization.BoundProjectId != projectId ||
            authorization.BoundRunId != runId ||
            authorization.MembershipRevision < 1 ||
            authorization.EffectiveAuthority.IsDefault ||
            !authorization.EffectiveAuthority.Any(authority =>
                authority.ResourceType == "project" &&
                authority.ResourceId == projectId &&
                !authority.Permissions.IsDefault &&
                authority.Permissions.Any(permission =>
                    permission.Permission == requiredPermission && permission.RoleRevision > 0))))
            throw new CoordinationException("run_selection_permission_denied", StatusCodes.Status403Forbidden);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(owner, $"/api/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}/" +
                (cancelling ? "cancellation-selection" : "selection")));
        request.Headers.Authorization = bearer;
        if (tenant is not null)
            request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", tenant);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new CoordinationException("run_selection_permission_denied", StatusCodes.Status403Forbidden);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new CoordinationException("accepted_run_not_found", StatusCodes.Status404NotFound);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new CoordinationException("projects_run_selection_unavailable", StatusCodes.Status502BadGateway);

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new CoordinationException("projects_run_selection_contract_invalid", StatusCodes.Status502BadGateway);
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("projectId", out var storedProject) ||
                storedProject.GetString() != projectId ||
                !root.TryGetProperty("runId", out var storedRun) ||
                storedRun.GetString() != runId ||
                !root.TryGetProperty("projectRevision", out var projectRevision) ||
                !projectRevision.TryGetInt64(out var projectRevisionValue) || projectRevisionValue < 1 ||
                !root.TryGetProperty("projectConfigurationRevision", out var configRevision) ||
                !configRevision.TryGetInt64(out var configRevisionValue) || configRevisionValue < 1 ||
                !root.TryGetProperty("platformRuntimeRevision", out var platformRevision) ||
                !platformRevision.TryGetInt64(out var platformRevisionValue) || platformRevisionValue < 1 ||
                !root.TryGetProperty("contextRevision", out var contextRevision) ||
                string.IsNullOrWhiteSpace(contextRevision.GetString()))
                throw new CoordinationException(
                    "projects_run_selection_contract_invalid", StatusCodes.Status502BadGateway);
            await ResolveReviewedRemoteToolSnapshotsAsync(
                root, projectId, cancellationToken).ConfigureAwait(false);
            if (refreshAuthority)
            {
                // Snapshot and remote-tool reads can outlast an authority revision; refresh before returning.
                authorization = cancelling
                    ? await ReadProjectRunAuthorityAsync(context, projectId, cancellationToken, "cancelRun")
                    : await ReadAuthorizationAsync(owner, bearer, tenant, cancellationToken).ConfigureAwait(false);
                if (!cancelling && (authorization.ContractVersion != 1 ||
                    authorization.Issuer != options.Issuer ||
                    authorization.ActorId != caller.Subject ||
                    authorization.BoundProjectId != projectId ||
                    authorization.BoundRunId != runId ||
                    authorization.MembershipRevision < 1 ||
                    authorization.EffectiveAuthority.IsDefault ||
                    !authorization.EffectiveAuthority.Any(authority =>
                        authority.ResourceType == "project" &&
                        authority.ResourceId == projectId &&
                        !authority.Permissions.IsDefault &&
                        authority.Permissions.Any(permission =>
                            permission.Permission == requiredPermission && permission.RoleRevision > 0))))
                    throw new CoordinationException("run_selection_permission_denied", StatusCodes.Status403Forbidden);
            }
            return new AuthorizedRunSelection(
                new EffectiveRunSelection(
                    projectId,
                    runId,
                    projectRevisionValue,
                    configRevisionValue,
                    platformRevisionValue,
                    contextRevision.GetString()!,
                    root.Clone()),
                authorization);
        }
    }

    /// <summary>
    /// Reads fresh Projects authorization and requires project-level read access for a project-scoped read.
    /// </summary>
    /// <param name="context">The authenticated request whose bearer and tenant selector are forwarded to Projects.</param>
    /// <param name="projectId">The project named by the request path.</param>
    /// <param name="cancellationToken">Cancels the Projects call.</param>
    /// <returns>The validated authorization context, including the tenant used to filter owner rows.</returns>
    /// <exception cref="CoordinationException">The caller, token binding, owner response, or permission is invalid.</exception>
    /// <remarks>
    /// Accepts <c>readProjects</c> or <c>readRunSelection</c> on the project, or tenant-level <c>readProjects</c>.
    /// A project-bound token must name the same project. A run-bound token remains bound to its run.
    /// </remarks>
    internal async Task<ProjectsAuthorizationContext> ReadProjectReadAuthorityAsync(
        HttpContext context,
        string projectId,
        CancellationToken cancellationToken)
    {
        var caller = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        CoordinationIdentity.RequireReadScope(context.User);
        var boundProjects = context.User.FindAll("project_id").Take(2).ToArray();
        var boundRuns = context.User.FindAll("run_id").Take(2).ToArray();
        if (!HasAudience(context.User, options.Audience) ||
            !HasAudience(context.User, options.ProjectsAudience) ||
            boundProjects.Length > 1 || boundRuns.Length > 1 ||
            (boundProjects.Length == 1 && boundProjects[0].Value != projectId) ||
            (boundRuns.Length == 1 && boundProjects.Length == 0))
            throw new CoordinationException("caller_binding_mismatch", StatusCodes.Status403Forbidden);
        var owner = RequireOwnerUri(options.ProjectsOwnerBaseAddress);
        var bearer = CoordinationIdentity.RequireBearer(context);
        var tenant = CoordinationIdentity.ReadTenantSelector(context);
        var authorization = await ReadAuthorizationAsync(owner, bearer, tenant, cancellationToken)
            .ConfigureAwait(false);
        if (authorization.ContractVersion != 1 ||
            authorization.Issuer != options.Issuer ||
            authorization.ActorId != caller.Subject ||
            string.IsNullOrWhiteSpace(authorization.TenantId) ||
            (tenant is not null && authorization.TenantId != tenant) ||
            authorization.MembershipRevision < 1 ||
            (authorization.BoundProjectId is not null && authorization.BoundProjectId != projectId) ||
            (authorization.BoundRunId is not null && authorization.BoundProjectId is null) ||
            authorization.BoundProjectId != (boundProjects.Length == 1 ? boundProjects[0].Value : null) ||
            authorization.BoundRunId != (boundRuns.Length == 1 ? boundRuns[0].Value : null) ||
            !CanReadProject(authorization, projectId))
            throw new CoordinationException("project_read_permission_denied", StatusCodes.Status403Forbidden);
        return authorization;
    }

    /// <summary>
    /// Reads fresh Projects authorization for a caller-scoped read that spans projects.
    /// </summary>
    /// <param name="context">The authenticated request whose bearer and tenant selector are forwarded to Projects.</param>
    /// <param name="cancellationToken">Cancels the Projects call.</param>
    /// <returns>The validated authorization context. Use <see cref="CanReadProject"/> per project.</returns>
    /// <exception cref="CoordinationException">The caller, token binding, or owner response is invalid.</exception>
    /// <remarks>
    /// The token must not be bound to a project or run, because the result covers every project the caller can read.
    /// </remarks>
    internal async Task<ProjectsAuthorizationContext> ReadCallerAuthorityAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var caller = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        CoordinationIdentity.RequireReadScope(context.User);
        if (!HasAudience(context.User, options.Audience) ||
            !HasAudience(context.User, options.ProjectsAudience) ||
            context.User.HasClaim(claim => claim.Type is "project_id" or "run_id"))
            throw new CoordinationException("caller_binding_mismatch", StatusCodes.Status403Forbidden);
        var owner = RequireOwnerUri(options.ProjectsOwnerBaseAddress);
        var bearer = CoordinationIdentity.RequireBearer(context);
        var tenant = CoordinationIdentity.ReadTenantSelector(context);
        var authorization = await ReadAuthorizationAsync(owner, bearer, tenant, cancellationToken)
            .ConfigureAwait(false);
        if (authorization.ContractVersion != 1 ||
            authorization.Issuer != options.Issuer ||
            authorization.ActorId != caller.Subject ||
            string.IsNullOrWhiteSpace(authorization.TenantId) ||
            (tenant is not null && authorization.TenantId != tenant) ||
            authorization.MembershipRevision < 1 ||
            authorization.BoundProjectId is not null ||
            authorization.BoundRunId is not null ||
            authorization.EffectiveAuthority.IsDefault)
            throw new CoordinationException("projects_authorization_contract_invalid", StatusCodes.Status502BadGateway);
        return authorization;
    }

    /// <summary>
    /// Returns whether validated authority grants read access to one project.
    /// </summary>
    /// <remarks>
    /// Accepts <c>readProjects</c> or <c>readRunSelection</c> on the project, or tenant-level <c>readProjects</c>.
    /// Every accepted grant must carry a positive role revision.
    /// </remarks>
    internal static bool CanReadProject(ProjectsAuthorizationContext authorization, string projectId) =>
        !authorization.EffectiveAuthority.IsDefault &&
        authorization.EffectiveAuthority.Any(authority =>
            !authority.Permissions.IsDefault &&
            ((authority.ResourceType == "project" && authority.ResourceId == projectId &&
                authority.Permissions.Any(permission =>
                    permission.Permission is "readProjects" or "readRunSelection" &&
                    permission.RoleRevision > 0)) ||
             (authority.ResourceType == "tenant" && authority.ResourceId == authorization.TenantId &&
                authority.Permissions.Any(permission =>
                    permission.Permission == "readProjects" && permission.RoleRevision > 0))));

    /// <summary>
    /// Resolves every reviewed remote-tool snapshot reference before accepting the selection contract.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when references are malformed, duplicated, outside the project, or unavailable.</exception>
    private async Task ResolveReviewedRemoteToolSnapshotsAsync(
        JsonElement selection,
        string projectId,
        CancellationToken cancellationToken)
    {
        if (!selection.TryGetProperty("projectConfiguration", out var projectConfiguration) ||
            projectConfiguration.ValueKind == JsonValueKind.Null)
            return;
        if (projectConfiguration.ValueKind != JsonValueKind.Object)
            throw InvalidReviewedRemoteToolSelection();
        if (!projectConfiguration.TryGetProperty("reviewedRemoteToolSnapshots", out var references) ||
            references.ValueKind == JsonValueKind.Null)
            return;
        if (references.ValueKind != JsonValueKind.Array)
            throw InvalidReviewedRemoteToolSelection();

        var seen = new HashSet<(string ProjectId, Guid SnapshotId)>();
        foreach (var element in references.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw InvalidReviewedRemoteToolSelection();

            ReviewedRemoteToolSnapshotReference reference;
            try
            {
                reference = JsonSerializer.Deserialize<ReviewedRemoteToolSnapshotReference>(
                    element.GetRawText(), JsonOptions)
                    ?? throw new JsonException();
            }
            catch (JsonException)
            {
                throw InvalidReviewedRemoteToolSelection();
            }
            catch (ArgumentException)
            {
                throw InvalidReviewedRemoteToolSelection();
            }

            if (reference.ProjectId != projectId ||
                !seen.Add((reference.ProjectId, reference.SnapshotId)))
                throw InvalidReviewedRemoteToolSelection();

            ReviewedRemoteToolSnapshot? resolved;
            try
            {
                resolved = await snapshotStore.ResolveAsync(reference, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or JsonException or ArgumentException)
            {
                throw new CoordinationException(
                    "reviewed_remote_tool_snapshot_invalid", StatusCodes.Status502BadGateway);
            }
            if (resolved is null)
                throw new CoordinationException(
                    "reviewed_remote_tool_snapshot_unavailable", StatusCodes.Status409Conflict);
        }
    }

    /// <summary>
    /// Creates the bad-gateway error used for a malformed reviewed-tool snapshot selection.
    /// </summary>
    private static CoordinationException InvalidReviewedRemoteToolSelection() =>
        new("projects_run_selection_contract_invalid", StatusCodes.Status502BadGateway);

    /// <summary>
    /// Reads the Projects authorization context with the caller's bearer and optional tenant selector.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when Projects denies the call or returns an invalid no-store contract.</exception>
    private async Task<ProjectsAuthorizationContext> ReadAuthorizationAsync(
        Uri owner,
        AuthenticationHeaderValue bearer,
        string? tenant,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri(owner, "/api/authorization/context"));
        request.Headers.Authorization = bearer;
        if (tenant is not null)
            request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", tenant);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new CoordinationException("projects_authorization_denied", StatusCodes.Status403Forbidden);
        if (response.StatusCode != HttpStatusCode.OK || response.Headers.CacheControl?.NoStore != true)
            throw new CoordinationException("projects_authorization_unavailable", StatusCodes.Status502BadGateway);
        try
        {
            return await response.Content.ReadFromJsonAsync<ProjectsAuthorizationContext>(
                JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new CoordinationException(
                    "projects_authorization_contract_invalid", StatusCodes.Status502BadGateway);
        }
        catch (JsonException)
        {
            throw new CoordinationException(
                "projects_authorization_contract_invalid", StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>
    /// Sends a Projects request without following redirects and maps transport failures to an owner-unavailable error.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw new CoordinationException("projects_authorization_unavailable", StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>
    /// Resolves the configured Projects owner only when it is an absolute HTTPS origin without credentials or extra URI parts.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when the configured owner address is not a trusted origin.</exception>
    private static Uri RequireOwnerUri(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo))
            throw new CoordinationException(
                "projects_owner_configuration_invalid", StatusCodes.Status503ServiceUnavailable);
        return uri;
    }

    /// <summary>
    /// Checks whether the caller's claims include the requested audience.
    /// </summary>
    private static bool HasAudience(System.Security.Claims.ClaimsPrincipal principal, string expected) =>
        principal.FindAll("aud").Any(claim => claim.Value == expected);
}
