extern alias AzureIdentity;

using System.Security.Claims;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using WorkloadIdentityCredential = AzureIdentity::Azure.Identity.WorkloadIdentityCredential;

namespace Agentweaver.Projects.Config;

internal sealed class P1ProjectSetupAuthorityCapability : IDisposable
{
    private readonly NpgsqlDataSource? dataSource;
    private readonly DbContextOptions<ProjectsConfigDbContext>? dbOptions;
    private readonly PrivilegedProjectSetupApproval? approval;

    private P1ProjectSetupAuthorityCapability(
        NpgsqlDataSource? dataSource,
        DbContextOptions<ProjectsConfigDbContext>? dbOptions,
        PrivilegedProjectSetupApproval? approval)
    {
        this.dataSource = dataSource;
        this.dbOptions = dbOptions;
        this.approval = approval;
    }

    internal P1ProjectSetupAuthorityCapability(
        DbContextOptions<ProjectsConfigDbContext> dbOptions,
        PrivilegedProjectSetupApproval approval)
        : this(null, dbOptions, approval)
    {
    }

    public bool IsAvailable => dbOptions is not null && approval is not null;

    public static P1ProjectSetupAuthorityCapability CreateOptional(
        IConfiguration configuration,
        string runtimeConnectionString)
    {
        var authority = P1ProjectSetupAuthorityConfiguration.ReadOptional(
            configuration, runtimeConnectionString);
        if (authority is null)
            return new P1ProjectSetupAuthorityCapability(null, null, null);

        var identityOptions = ProjectsConfigPostgresDataSource.ReadWorkloadIdentityOptions(
            configuration, "ProjectsConfig:AuthorityDatabase:WorkloadIdentity");
        var identity = new WorkloadIdentityCredential(identityOptions);
        var source = ProjectsConfigPostgresDataSource.Create(authority.ConnectionString, identity);
        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(
                source,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;
        return new P1ProjectSetupAuthorityCapability(
            source, options, authority.Approval);
    }

    public async Task VerifyMigrationsAppliedAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            return;

        await ProjectsConfigMigrator.VerifyMigrationsAppliedAsync(
            dataSource!, dbOptions!, cancellationToken).ConfigureAwait(false);
        await ProjectsConfigMigrator.VerifyP1SetupAuthorityAsync(
            dataSource!, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectSetupReceipt> CreateSetupReceiptAsync(
        ClaimsPrincipal principal,
        Guid invocationId,
        string tenantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!IsAvailable)
            throw new ProjectConfigException(
                "project_setup_authority_unavailable",
                "The P1 setup authority capability is not configured.",
                StatusCodes.Status503ServiceUnavailable);
        if (!string.Equals(tenantId, approval!.ApprovedTenantId, StringComparison.Ordinal))
            throw ProjectConfigException.Forbidden();

        await using var db = new ProjectsConfigDbContext(dbOptions!);
        var caller = await new ProjectAuthorizationOwner(
                db, new ProjectsConfigIdentityOptions(approval.Authority.AbsoluteUri))
            .ResolveAsync(principal, [tenantId], cancellationToken)
            .ConfigureAwait(false);
        approval.RequireApprovedCaller(caller);

        var result = await new ProjectsConfigPrivilegedAuthorityStore(dbOptions!, TimeProvider.System)
            .ExecuteProjectSetupOperationAsync(
                caller,
                new ProjectSetupOperationRequest(
                    1,
                    invocationId,
                    ProjectSetupOperationKind.Setup,
                    tenantId,
                    ExpectedProjectRevision: null,
                    ExpectedAssignmentRevision: null),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.OperationKind != ProjectSetupOperationKind.Setup ||
            result.Setup is null ||
            result.SetupReadback is null ||
            result.Cleanup is not null)
            throw new InvalidOperationException("The project setup authority returned an incomplete setup receipt.");

        ValidateReceipt(result.Setup, result.SetupReadback, caller, invocationId, tenantId);
        return result.Setup;
    }

    public void Dispose()
    {
        dataSource?.Dispose();
    }

    private static void ValidateReceipt(
        ProjectSetupReceipt receipt,
        ProjectSetupReadback readback,
        ProjectAuthorizationContext caller,
        Guid invocationId,
        string tenantId)
    {
        if (receipt.InvocationId != invocationId ||
            !string.Equals(receipt.Issuer, caller.Issuer, StringComparison.Ordinal) ||
            !string.Equals(receipt.Subject, caller.ActorId, StringComparison.Ordinal) ||
            !string.Equals(receipt.TenantId, tenantId, StringComparison.Ordinal) ||
            receipt.MembershipId != caller.MembershipId ||
            receipt.MembershipRevision != caller.MembershipRevision ||
            !string.Equals(receipt.CreatedByActorId, caller.ActorId, StringComparison.Ordinal) ||
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
}

internal static class P1ProjectSetupEndpoints
{
    private const int MaxBodyProbeBytes = 1;

    public static IEndpointRouteBuilder MapP1ProjectSetupEndpoint(this IEndpointRouteBuilder app)
    {
        var invocations = app.MapGroup("/api/p1/invocations").RequireAuthorization();
        invocations.MapPost("/{invocationId:guid}/setup", async (
            Guid invocationId,
            HttpContext context,
            P1ProjectSetupAuthorityCapability authority,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                if (invocationId == Guid.Empty ||
                    context.Request.QueryString.HasValue ||
                    await HasNonemptyBodyAsync(context.Request, cancellationToken).ConfigureAwait(false))
                    throw ProjectConfigException.Invalid(
                        "invalid_project_setup_request",
                        "The P1 setup request accepts only its invocation ID and tenant header.");

                var tenantValues = context.Request.Headers[ProjectAuthorizationOwner.TenantSelectorHeader];
                if (tenantValues.Count != 1 || string.IsNullOrWhiteSpace(tenantValues[0]))
                    throw ProjectConfigException.Invalid(
                        "invalid_project_setup_request",
                        $"Exactly one {ProjectAuthorizationOwner.TenantSelectorHeader} header is required.");

                var receipt = await authority.CreateSetupReceiptAsync(
                    context.User,
                    invocationId,
                    tenantValues[0]!,
                    cancellationToken).ConfigureAwait(false);
                return Results.Ok(receipt);
            }
            catch (ProjectConfigException exception)
            {
                return Results.Problem(
                    title: exception.Code,
                    detail: exception.Message,
                    statusCode: exception.StatusCode,
                    extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
            }
        }).WithName("CreateP1InvocationSetup");
        return app;
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
