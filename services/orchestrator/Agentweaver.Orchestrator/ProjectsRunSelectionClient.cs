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

internal sealed class ProjectsRunSelectionClient(
    HttpClient httpClient,
    OrchestratorOptions options,
    IReviewedRemoteToolSnapshotResolver snapshotStore)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public async Task<EffectiveRunSelection> ReadAcceptedSelectionAsync(
        HttpContext context,
        string projectId,
        string runId,
        CancellationToken cancellationToken) =>
        (await ReadSelectionWithAuthorityAsync(
            context, projectId, runId, "acceptRunSelection", cancellationToken).ConfigureAwait(false)).Selection;

    public async Task<EffectiveRunSelection> ReadSelectionForReadAsync(
        HttpContext context,
        string projectId,
        string runId,
        CancellationToken cancellationToken) =>
        (await ReadSelectionWithAuthorityAsync(
            context, projectId, runId, "readRunSelection", cancellationToken).ConfigureAwait(false)).Selection;

    public Task<AuthorizedRunSelection> ReadAcceptedSelectionWithAuthorityAsync(
        HttpContext context,
        string projectId,
        string runId,
        CancellationToken cancellationToken) =>
        ReadSelectionWithAuthorityAsync(
            context, projectId, runId, "acceptRunSelection", cancellationToken, refreshAuthority: true);

    internal Task<AuthorizedRunSelection> ReadSelectionForReadWithAuthorityAsync(
        HttpContext context, string projectId, string runId, CancellationToken cancellationToken) =>
        ReadSelectionWithAuthorityAsync(
            context, projectId, runId, "readRunSelection", cancellationToken, refreshAuthority: true);

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

    private static CoordinationException InvalidRuntimeSkillContent() =>
        new("runtime_skill_content_invalid", StatusCodes.Status502BadGateway);

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
        var scope = CoordinationIdentity.RequireRunScope(context.User);
        if (scope.ProjectId != projectId || scope.RunId != runId ||
            !HasAudience(context.User, options.ProjectsAudience))
            throw new CoordinationException("accepted_run_caller_mismatch", StatusCodes.Status403Forbidden);
        var owner = RequireOwnerUri(options.ProjectsOwnerBaseAddress);
        var bearer = CoordinationIdentity.RequireBearer(context);
        var tenant = CoordinationIdentity.ReadTenantSelector(context);
        var authorization = await ReadAuthorizationAsync(owner, bearer, tenant, cancellationToken)
            .ConfigureAwait(false);

        if (authorization.ContractVersion != 1 ||
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
                    permission.Permission == requiredPermission && permission.RoleRevision > 0)))
            throw new CoordinationException("run_selection_permission_denied", StatusCodes.Status403Forbidden);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(owner, $"/api/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}/selection"));
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
                authorization = await ReadAuthorizationAsync(owner, bearer, tenant, cancellationToken)
                    .ConfigureAwait(false);
                if (authorization.ContractVersion != 1 ||
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
                            permission.Permission == requiredPermission && permission.RoleRevision > 0)))
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

    private static CoordinationException InvalidReviewedRemoteToolSelection() =>
        new("projects_run_selection_contract_invalid", StatusCodes.Status502BadGateway);

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

    private static bool HasAudience(System.Security.Claims.ClaimsPrincipal principal, string expected) =>
        principal.FindAll("aud").Any(claim => claim.Value == expected);
}
