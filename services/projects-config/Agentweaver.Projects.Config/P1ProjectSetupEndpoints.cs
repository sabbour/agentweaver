using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Projects.Config;

internal sealed record P1ProjectSetupFunctionResult(
    ProjectSetupReceipt Receipt,
    ProjectSetupReadback Readback);

internal static class P1ProjectSetupEndpoints
{
    private const int MaxBodyProbeBytes = 1;
    private static readonly TimeSpan BodyProbeTimeout = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions FunctionJsonOptions = CreateFunctionJsonOptions();

    public static IEndpointRouteBuilder MapP1ProjectSetupEndpoint(this IEndpointRouteBuilder app)
    {
        var invocations = app.MapGroup("/api/p1/invocations").RequireAuthorization();
        invocations.MapPost("/{invocationId:guid}/setup", HandleSetupAsync)
            .WithName("CreateP1InvocationSetup");
        invocations.MapGet("/{invocationId:guid}/setup", HandleSetupAsync)
            .WithName("ReadP1InvocationSetup");
        return app;
    }

    private static async Task<IResult> HandleSetupAsync(
        Guid invocationId,
        HttpContext context,
        ProjectAuthorizationOwner authorizationOwner,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        using var bodyProbeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bodyProbeTimeout.CancelAfter(BodyProbeTimeout);
        try
        {
            if (invocationId == Guid.Empty ||
                context.Request.QueryString.HasValue ||
                await HasNonemptyBodyAsync(context.Request, bodyProbeTimeout.Token).ConfigureAwait(false))
                throw ProjectConfigException.Invalid(
                    "invalid_project_setup_request",
                    "The P1 setup request accepts only its invocation ID and tenant header.");

            var tenantValues = context.Request.Headers[ProjectAuthorizationOwner.TenantSelectorHeader];
            if (tenantValues.Count != 1 || string.IsNullOrWhiteSpace(tenantValues[0]))
                throw ProjectConfigException.Invalid(
                    "invalid_project_setup_request",
                    $"Exactly one {ProjectAuthorizationOwner.TenantSelectorHeader} header is required.");

            var caller = await authorizationOwner.ResolveAsync(
                context.User,
                [tenantValues[0]],
                cancellationToken).ConfigureAwait(false);
            caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
            caller.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
            caller.RequireScope(ProjectAuthorizationOwner.OrchestratorScope);
            caller.RequireUnboundRequest();

            var tenantAdminGrants = caller.Grants.Where(grant =>
                grant.ResourceType == ProjectAuthorityResourceType.Tenant &&
                string.Equals(grant.ResourceId, caller.TenantId, StringComparison.Ordinal) &&
                grant.Role == ProjectAuthorityRole.TenantAdmin).ToArray();
            if (tenantAdminGrants.Length != 1)
                throw ProjectConfigException.Forbidden();

            var result = HttpMethods.IsPost(context.Request.Method)
                ? await IssueAsync(
                    dataSource, caller, invocationId, tenantAdminGrants[0], cancellationToken)
                    .ConfigureAwait(false)
                : await ReadAsync(
                    dataSource, caller, invocationId, tenantAdminGrants[0], cancellationToken)
                    .ConfigureAwait(false);
            ValidateResult(result, caller, invocationId);
            return Results.Ok(result.Receipt);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem(
                title: "request_body_timeout",
                detail: "The P1 setup request body did not complete within the allowed time.",
                statusCode: StatusCodes.Status408RequestTimeout,
                extensions: new Dictionary<string, object?> { ["code"] = "request_body_timeout" });
        }
        catch (ProjectConfigException exception)
        {
            return Results.Problem(
                title: exception.Code,
                detail: exception.Message,
                statusCode: exception.StatusCode,
                extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
        }
    }

    internal static async Task<P1ProjectSetupFunctionResult> IssueAsync(
        NpgsqlDataSource dataSource,
        ProjectAuthorizationContext caller,
        Guid invocationId,
        ProjectAuthorizationGrant tenantAdminGrant,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(caller);
        if (invocationId == Guid.Empty ||
            tenantAdminGrant.ResourceType != ProjectAuthorityResourceType.Tenant ||
            tenantAdminGrant.ResourceId != caller.TenantId ||
            tenantAdminGrant.Role != ProjectAuthorityRole.TenantAdmin)
            throw ProjectConfigException.Invalid(
                "invalid_project_setup_request",
                "The P1 setup invocation or TenantAdmin binding is invalid.");

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                """
                SELECT projects_config.issue_p1_project(
                    @invocation_id,
                    @issuer,
                    @subject,
                    @tenant_id,
                    @membership_id,
                    @expected_membership_revision,
                    @tenant_admin_assignment_id,
                    @expected_tenant_admin_assignment_revision)
                """,
                connection);
            AddBindingParameters(command, caller, invocationId, tenantAdminGrant);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var json = value as string
                ?? throw new InvalidOperationException("The P1 setup issue function returned no JSON receipt.");
            return JsonSerializer.Deserialize<P1ProjectSetupFunctionResult>(json, FunctionJsonOptions)
                ?? throw new InvalidOperationException("The P1 setup issue function returned an empty receipt.");
        }
        catch (PostgresException exception)
        {
            throw MapPostgresException(exception);
        }
    }

    internal static async Task<P1ProjectSetupFunctionResult> ReadAsync(
        NpgsqlDataSource dataSource,
        ProjectAuthorizationContext caller,
        Guid invocationId,
        ProjectAuthorizationGrant tenantAdminGrant,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(caller);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                """
                SELECT projects_config.read_p1_project(
                    @invocation_id,
                    @issuer,
                    @subject,
                    @tenant_id,
                    @membership_id,
                    @expected_membership_revision,
                    @tenant_admin_assignment_id,
                    @expected_tenant_admin_assignment_revision)
                """,
                connection);
            AddBindingParameters(command, caller, invocationId, tenantAdminGrant);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var json = value as string
                ?? throw new InvalidOperationException("The P1 setup read function returned no JSON receipt.");
            return JsonSerializer.Deserialize<P1ProjectSetupFunctionResult>(json, FunctionJsonOptions)
                ?? throw new InvalidOperationException("The P1 setup read function returned an empty receipt.");
        }
        catch (PostgresException exception)
        {
            throw MapPostgresException(exception);
        }
    }

    private static void AddBindingParameters(
        NpgsqlCommand command,
        ProjectAuthorizationContext caller,
        Guid invocationId,
        ProjectAuthorizationGrant tenantAdminGrant)
    {
        command.Parameters.AddWithValue("invocation_id", NpgsqlDbType.Uuid, invocationId);
        command.Parameters.AddWithValue("issuer", NpgsqlDbType.Text, caller.Issuer);
        command.Parameters.AddWithValue("subject", NpgsqlDbType.Text, caller.ActorId);
        command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Text, caller.TenantId);
        command.Parameters.AddWithValue("membership_id", NpgsqlDbType.Uuid, caller.MembershipId);
        command.Parameters.AddWithValue(
            "expected_membership_revision", NpgsqlDbType.Bigint, caller.MembershipRevision);
        command.Parameters.AddWithValue(
            "tenant_admin_assignment_id", NpgsqlDbType.Uuid, tenantAdminGrant.AssignmentId);
        command.Parameters.AddWithValue(
            "expected_tenant_admin_assignment_revision", NpgsqlDbType.Bigint, tenantAdminGrant.Revision);
    }

    private static ProjectConfigException MapPostgresException(PostgresException exception) =>
        exception.SqlState switch
        {
            PostgresErrorCodes.InsufficientPrivilege => ProjectConfigException.Forbidden(),
            PostgresErrorCodes.InvalidParameterValue =>
                ProjectConfigException.Invalid(
                    "invalid_project_setup_request",
                    "The P1 setup invocation or current authority binding is invalid."),
            PostgresErrorCodes.ObjectNotInPrerequisiteState =>
                ProjectConfigException.Conflict(
                    "project_setup_receipt_stale",
                    "The setup receipt no longer matches an active project and its original authority assignments."),
            PostgresErrorCodes.UniqueViolation =>
                ProjectConfigException.Conflict(
                    "project_setup_invocation_conflict",
                    "The invocation already has a different immutable setup receipt."),
            _ => throw exception,
        };

    private static void ValidateResult(
        P1ProjectSetupFunctionResult result,
        ProjectAuthorizationContext caller,
        Guid invocationId)
    {
        var receipt = result.Receipt;
        var readback = result.Readback;
        if (receipt.InvocationId != invocationId ||
            receipt.Issuer != caller.Issuer ||
            receipt.Subject != caller.ActorId ||
            receipt.TenantId != caller.TenantId ||
            receipt.MembershipId != caller.MembershipId ||
            receipt.MembershipRevision != caller.MembershipRevision ||
            receipt.CreatedByActorId != caller.ActorId ||
            receipt.ProjectRevision <= 0 ||
            receipt.ProjectConfigurationRevision <= 0 ||
            receipt.AssignmentId == Guid.Empty ||
            receipt.AssignmentRevision <= 0 ||
            receipt.AuthorityAuditEventId == Guid.Empty ||
            receipt.OwnerAssignmentId is not { } ownerAssignmentId ||
            ownerAssignmentId == Guid.Empty ||
            receipt.OwnerAssignmentRevision is not > 0 ||
            receipt.OwnerAuthorityAuditEventId is not { } ownerAuditId ||
            ownerAuditId == Guid.Empty ||
            !readback.ProjectExists ||
            readback.ProjectState != ProjectLifecycleState.Active ||
            readback.ProjectRevision != receipt.ProjectRevision ||
            readback.ProjectConfigurationRevision != receipt.ProjectConfigurationRevision ||
            !readback.InitialConfigurationExists ||
            !readback.AssignmentExists ||
            readback.AssignmentState != ProjectAuthorityRecordState.Active ||
            readback.AssignmentRevision != receipt.AssignmentRevision ||
            !readback.AuthorityAuditExists ||
            readback.OwnerAssignmentId != receipt.OwnerAssignmentId ||
            !readback.OwnerAssignmentExists ||
            readback.OwnerAssignmentState != ProjectAuthorityRecordState.Active ||
            readback.OwnerAssignmentRevision != receipt.OwnerAssignmentRevision ||
            !readback.OwnerAuthorityAuditExists)
            throw ProjectConfigException.Conflict(
                "project_setup_receipt_stale",
                "The setup receipt no longer matches an active project and its original authority assignments.");
    }

    private static JsonSerializerOptions CreateFunctionJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static async Task<bool> HasNonemptyBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is > 0)
            return true;
        if (request.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != true)
            return false;

        var probe = new byte[MaxBodyProbeBytes];
        return await request.Body.ReadAsync(probe, cancellationToken).ConfigureAwait(false) > 0;
    }
}
