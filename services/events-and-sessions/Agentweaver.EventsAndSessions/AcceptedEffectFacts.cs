using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Persistence.Postgres;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.EventsAndSessions;

public sealed record AcceptedEffectRuntimeOptions(
    Uri IdentityIssuer,
    string EventsAudience,
    Uri KnowledgeBaseAddress,
    Uri ProjectsConfigBaseAddress,
    string Schema)
{
    public static AcceptedEffectRuntimeOptions Read(
        IConfiguration configuration,
        string schema)
    {
        var issuer = ReadHttpsUri(configuration["Identity:Issuer"], "Identity:Issuer");
        var knowledge = ReadHttpsUri(
            configuration["EventsAndSessions:Knowledge:BaseAddress"],
            "EventsAndSessions:Knowledge:BaseAddress");
        var projects = ReadHttpsUri(
            configuration["ProjectsConfig:BaseAddress"], "ProjectsConfig:BaseAddress");
        var audience = configuration["Identity:Audience"];
        if (string.IsNullOrWhiteSpace(audience) || audience.Any(char.IsControl))
            throw new InvalidOperationException("Missing or invalid configuration 'Identity:Audience'.");
        return new AcceptedEffectRuntimeOptions(issuer, audience, knowledge, projects, schema);
    }

    private static Uri ReadHttpsUri(string? value, string key)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length > 0 ||
            uri.Query.Length > 0 ||
            uri.Fragment.Length > 0)
            throw new InvalidOperationException(
                $"'{key}' must be an absolute HTTPS URI without user info, query, or fragment.");
        return uri;
    }
}

public interface IProjectFactJournal
{
    Task<ProjectFactAcknowledgment> AppendAcceptedEffectAsync(
        AcceptedEffectReceipt receipt,
        CancellationToken cancellationToken = default);
}

public sealed class PostgresProjectFactJournal : IProjectFactJournal
{
    private const string InboxConsumer = "events-and-sessions.accepted-effects";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;
    private readonly PostgresOutbox _outbox;

    public PostgresProjectFactJournal(
        NpgsqlDataSource dataSource,
        AcceptedEffectRuntimeOptions options)
    {
        _dataSource = dataSource;
        _schema = $"\"{options.Schema}\"";
        _outbox = new PostgresOutbox(dataSource, options.Schema);
    }

    public async Task<ProjectFactAcknowledgment> AppendAcceptedEffectAsync(
        AcceptedEffectReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        using var receiptDocument = JsonDocument.Parse(JsonSerializer.Serialize(receipt, JsonOptions));
        var receiptJson = receiptDocument.RootElement.GetRawText();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var admission = await _outbox.AdmitAsync(
            connection,
            transaction,
            InboxConsumer,
            receipt.ReceiptId.ToString("D"),
            cancellationToken);
        if (admission == InboxAdmission.Duplicate)
        {
            var duplicate = await ReadExistingAsync(
                connection, transaction, receipt.ReceiptId, receiptJson, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return duplicate;
        }

        var sequence = await NextPositionAsync(connection, transaction, receipt.ProjectId, cancellationToken);
        var factId = Guid.NewGuid();
        var acknowledgment = new ProjectFactAcknowledgment(
            receipt.ReceiptId,
            receipt.SchemaVersion,
            receipt.EventVersion,
            factId,
            receipt.ProjectId,
            sequence);
        var acknowledgmentJson = JsonSerializer.Serialize(acknowledgment, JsonOptions);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_schema}.project_facts
                (receipt_id, project_id, sequence, fact_id, schema_version, event_version,
                 run_id, effect_id, record_id, record_version, issuer, subject, tenant_id,
                 accepted_at, receipt, acknowledgment, inbox_message_id)
            VALUES
                (@receipt_id, @project, @sequence, @fact, @schema_version, @event_version,
                 @run, @effect, @record, @record_version, @issuer, @subject, @tenant,
                 @accepted_at, @receipt::jsonb, @acknowledgment::jsonb, @inbox_message_id)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("receipt_id", NpgsqlDbType.Uuid, receipt.ReceiptId);
            insert.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, receipt.ProjectId);
            insert.Parameters.AddWithValue("sequence", NpgsqlDbType.Bigint, sequence);
            insert.Parameters.AddWithValue("fact", NpgsqlDbType.Uuid, factId);
            insert.Parameters.AddWithValue("schema_version", NpgsqlDbType.Integer, receipt.SchemaVersion);
            insert.Parameters.AddWithValue("event_version", NpgsqlDbType.Integer, receipt.EventVersion);
            insert.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, receipt.RunId);
            insert.Parameters.AddWithValue("effect", NpgsqlDbType.Uuid, receipt.EffectId);
            insert.Parameters.AddWithValue("record", NpgsqlDbType.Uuid, receipt.RecordId);
            insert.Parameters.AddWithValue("record_version", NpgsqlDbType.Integer, receipt.RecordVersion);
            insert.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, receipt.Issuer);
            insert.Parameters.AddWithValue("subject", NpgsqlDbType.Varchar, receipt.Subject);
            insert.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, receipt.TenantId);
            insert.Parameters.AddWithValue("accepted_at", NpgsqlDbType.TimestampTz, receipt.AcceptedAt);
            insert.Parameters.AddWithValue("receipt", NpgsqlDbType.Jsonb, receiptJson);
            insert.Parameters.AddWithValue("acknowledgment", NpgsqlDbType.Jsonb, acknowledgmentJson);
            insert.Parameters.AddWithValue("inbox_message_id", NpgsqlDbType.Text, receipt.ReceiptId.ToString("D"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return acknowledgment;
    }

    private async Task<long> NextPositionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_schema}.project_fact_streams (project_id, last_position)
            VALUES (@project, 1)
            ON CONFLICT (project_id) DO UPDATE
            SET last_position = {_schema}.project_fact_streams.last_position + 1
            RETURNING last_position
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        return (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("The project fact stream position was not assigned."));
    }

    private async Task<ProjectFactAcknowledgment> ReadExistingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid receiptId,
        string receiptJson,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT receipt = @receipt::jsonb, acknowledgment
            FROM {_schema}.project_facts
            WHERE receipt_id = @receipt_id
            """, connection, transaction);
        command.Parameters.AddWithValue("receipt", NpgsqlDbType.Jsonb, receiptJson);
        command.Parameters.AddWithValue("receipt_id", NpgsqlDbType.Uuid, receiptId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("The project fact inbox receipt has no matching fact.");
        if (!reader.GetBoolean(0))
            throw new ProjectFactConflictException();
        var acknowledgment = JsonSerializer.Deserialize<ProjectFactAcknowledgment>(
            reader.GetString(1), JsonOptions);
        return acknowledgment
            ?? throw new InvalidOperationException("The stored project fact acknowledgment is invalid.");
    }
}

public sealed class KnowledgeAcceptedEffectReceiptClient(
    HttpClient client,
    IHttpContextAccessor contextAccessor)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<AcceptedEffectReceipt> ReadAsync(
        string projectId,
        string runId,
        Guid receiptId,
        CancellationToken cancellationToken)
    {
        var headers = ForwardedHeaders.Read(contextAccessor.HttpContext, knowledgeReceiptOwner: true);
        if (headers.Error is not null)
            throw new ProjectFactApiException(headers.Error, StatusCodes.Status401Unauthorized);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/internal/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}" +
            $"/accepted-effects/{receiptId:D}");
        request.Headers.Authorization = headers.Authorization;
        if (headers.TenantSelector is not null)
            request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", headers.TenantSelector);
        using var response = await SendAsync(client, request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new ProjectFactApiException("accepted_effect_receipt_not_found", StatusCodes.Status404NotFound);
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new ProjectFactApiException("accepted_effect_receipt_forbidden", StatusCodes.Status403Forbidden);
        if (!response.IsSuccessStatusCode)
            throw new ProjectFactApiException(
                response.StatusCode == HttpStatusCode.Unauthorized
                    ? "knowledge_receipt_authority_rejected"
                    : "knowledge_receipt_unavailable",
                response.StatusCode == HttpStatusCode.Unauthorized
                    ? StatusCodes.Status502BadGateway
                    : StatusCodes.Status503ServiceUnavailable);
        try
        {
            return await response.Content.ReadFromJsonAsync<AcceptedEffectReceipt>(
                    JsonOptions, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new ProjectFactApiException(
                    "knowledge_receipt_invalid", StatusCodes.Status502BadGateway);
        }
        catch (JsonException)
        {
            throw new ProjectFactApiException("knowledge_receipt_invalid", StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new ProjectFactApiException("knowledge_receipt_unavailable", StatusCodes.Status503ServiceUnavailable);
        }
    }
}

public sealed class ProjectsConfigAuthorizationClient(
    HttpClient client,
    IHttpContextAccessor contextAccessor,
    AcceptedEffectRuntimeOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<ProjectAuthorizationContextResponse> GetCurrentAuthorityAsync(
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        var context = contextAccessor.HttpContext;
        var headers = ForwardedHeaders.Read(context);
        if (headers.Error is not null)
            throw new ProjectFactApiException(headers.Error, StatusCodes.Status401Unauthorized);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/authorization/context");
        request.Headers.Authorization = headers.Authorization;
        if (headers.TenantSelector is not null)
            request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", headers.TenantSelector);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new ProjectFactApiException("projects_authority_unavailable", StatusCodes.Status503ServiceUnavailable);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new ProjectFactApiException(
                    response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? "missing_current_project_authority"
                        : "projects_authority_unavailable",
                    response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? StatusCodes.Status403Forbidden
                        : StatusCodes.Status503ServiceUnavailable);
            ProjectAuthorizationContextResponse? authority;
            try
            {
                authority = await response.Content.ReadFromJsonAsync<ProjectAuthorizationContextResponse>(
                    JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                throw new ProjectFactApiException(
                    "invalid_authority_response", StatusCodes.Status502BadGateway);
            }
            if (authority is null)
                throw new ProjectFactApiException(
                    "invalid_authority_response", StatusCodes.Status502BadGateway);
            if (authority.ContractVersion != 1 ||
                !string.Equals(authority.Issuer, options.IdentityIssuer.AbsoluteUri, StringComparison.Ordinal) ||
                !string.Equals(authority.ActorId, Claim(context!.User, "sub"), StringComparison.Ordinal) ||
                !string.Equals(authority.BoundProjectId, OptionalClaim(context.User, "project_id"), StringComparison.Ordinal) ||
                !string.Equals(authority.BoundRunId, OptionalClaim(context.User, "run_id"), StringComparison.Ordinal) ||
                authority.MembershipRevision < 1 ||
                !ValidIdentifier(authority.TenantId) ||
                authority.EffectiveAuthority.IsDefault ||
                authority.EffectiveAuthority.Any(resource =>
                    !Enum.IsDefined(resource.ResourceType) ||
                    !ValidIdentifier(resource.ResourceId) ||
                    resource.Permissions.IsDefault ||
                    resource.Permissions.Any(grant =>
                        !Enum.IsDefined(grant.Permission) || grant.RoleRevision < 1)))
                throw new ProjectFactApiException(
                    "invalid_authority_response", StatusCodes.Status502BadGateway);
            if (!HasWritePermission(authority, projectId))
                throw new ProjectFactApiException(
                    "missing_effective_writeprojects", StatusCodes.Status403Forbidden);
            return authority;
        }
    }

    private static bool HasWritePermission(
        ProjectAuthorizationContextResponse authority,
        string projectId) =>
        authority.EffectiveAuthority.Any(resource =>
            (resource.ResourceType == ProjectAuthorityResourceType.Project &&
             string.Equals(resource.ResourceId, projectId, StringComparison.Ordinal) ||
             resource.ResourceType == ProjectAuthorityResourceType.Tenant &&
             string.Equals(resource.ResourceId, authority.TenantId, StringComparison.Ordinal)) &&
            resource.Permissions.Any(permission =>
                permission.Permission == ProjectAuthorizationPermission.WriteProjects));

    private static string Claim(ClaimsPrincipal principal, string type)
    {
        var values = principal.FindAll(type).Take(2).ToArray();
        if (values.Length != 1 || string.IsNullOrWhiteSpace(values[0].Value))
            throw new ProjectFactApiException("caller_identity_invalid", StatusCodes.Status403Forbidden);
        return values[0].Value;
    }

    private static string? OptionalClaim(ClaimsPrincipal principal, string type)
    {
        var values = principal.FindAll(type).Take(2).ToArray();
        if (values.Length > 1)
            throw new ProjectFactApiException("caller_identity_invalid", StatusCodes.Status403Forbidden);
        return values.Length == 0 ? null : values[0].Value;
    }

    private static bool ValidIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');
}

public sealed class AcceptedEffectApplicationService(
    KnowledgeAcceptedEffectReceiptClient receipts,
    ProjectsConfigAuthorizationClient projects,
    IProjectFactJournal journal,
    AcceptedEffectRuntimeOptions options)
{
    public async Task<ProjectFactAcknowledgment> AppendAsync(
        ClaimsPrincipal principal,
        AcceptedEffectDeliveryRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ReceiptId == Guid.Empty ||
            !ValidIdentifier(request.ProjectId) ||
            !ValidIdentifier(request.RunId) ||
            request.SchemaVersion < 1 ||
            request.EventVersion < 1)
            throw new ProjectFactApiException("invalid_request", StatusCodes.Status400BadRequest);
        var caller = ValidateCaller(principal, options);
        var receipt = await receipts.ReadAsync(
            request.ProjectId, request.RunId, request.ReceiptId, cancellationToken);
        if (receipt.ReceiptId != request.ReceiptId)
            throw new ProjectFactConflictException();
        if (!string.Equals(request.ProjectId, receipt.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(request.RunId, receipt.RunId, StringComparison.Ordinal) ||
            request.SchemaVersion != receipt.SchemaVersion ||
            request.EventVersion != receipt.EventVersion)
            throw new ProjectFactConflictException();
        ValidateReceipt(receipt, options);
        ValidateCallerMatchesReceipt(caller, receipt);
        var authority = await projects.GetCurrentAuthorityAsync(
            receipt.ProjectId, receipt.RunId, cancellationToken);
        if (!string.Equals(authority.TenantId, receipt.TenantId, StringComparison.Ordinal))
            throw new ProjectFactApiException("accepted_effect_caller_mismatch", StatusCodes.Status403Forbidden);
        return await journal.AppendAcceptedEffectAsync(receipt, cancellationToken);
    }

    private sealed record CallerBounds(
        string Issuer,
        string Subject,
        string? ProjectId,
        string? RunId,
        string[] TenantAssertions);

    private static CallerBounds ValidateCaller(
        ClaimsPrincipal principal,
        AcceptedEffectRuntimeOptions options)
    {
        var identities = principal.Identities.Where(identity => identity.IsAuthenticated).Take(2).ToArray();
        if (identities.Length != 1)
            throw new ProjectFactApiException("caller_identity_invalid", StatusCodes.Status403Forbidden);

        var claims = identities[0].Claims.ToArray();
        var subjects = claims.Where(claim => claim.Type == "sub").ToArray();
        if (subjects.Length != 1 ||
            !ValidIdentifier(subjects[0].Value) ||
            !TryNormalizeIssuer(subjects[0].Issuer, out var issuer) ||
            !string.Equals(issuer, options.IdentityIssuer.AbsoluteUri, StringComparison.Ordinal))
            throw new ProjectFactApiException("caller_identity_invalid", StatusCodes.Status403Forbidden);

        var issuerClaims = claims.Where(claim => claim.Type == "iss").ToArray();
        var projectClaims = claims.Where(claim => claim.Type == "project_id").ToArray();
        var runClaims = claims.Where(claim => claim.Type == "run_id").ToArray();
        var tenantAssertions = claims.Where(claim =>
                claim.Type is "tenant_id" or "tid" or "http://schemas.microsoft.com/identity/claims/tenantid")
            .Select(claim => claim.Value)
            .ToArray();
        if (issuerClaims.Length > 1 ||
            (issuerClaims.Length == 1 && !HasIssuerValue(issuerClaims[0].Value, issuer)) ||
            claims.Any(claim => claim.Type == "purpose") ||
            projectClaims.Length > 1 ||
            runClaims.Length > 1 ||
            (runClaims.Length == 1 && projectClaims.Length == 0) ||
            projectClaims.Any(claim =>
                !ValidIdentifier(claim.Value) || !HasClaimIssuer(claim, issuer)) ||
            runClaims.Any(claim =>
                !ValidIdentifier(claim.Value) || !HasClaimIssuer(claim, issuer)) ||
            tenantAssertions.Length > 1)
            throw new ProjectFactApiException("caller_identity_invalid", StatusCodes.Status403Forbidden);

        return new CallerBounds(
            issuer,
            subjects[0].Value,
            projectClaims.SingleOrDefault()?.Value,
            runClaims.SingleOrDefault()?.Value,
            tenantAssertions);
    }

    private static void ValidateCallerMatchesReceipt(
        CallerBounds caller,
        AcceptedEffectReceipt receipt)
    {
        if (!string.Equals(caller.Subject, receipt.Subject, StringComparison.Ordinal) ||
            !string.Equals(caller.Issuer, receipt.Issuer, StringComparison.Ordinal) ||
            !string.Equals(caller.ProjectId, receipt.BoundProjectId, StringComparison.Ordinal) ||
            !string.Equals(caller.RunId, receipt.BoundRunId, StringComparison.Ordinal))
            throw new ProjectFactApiException("accepted_effect_caller_mismatch", StatusCodes.Status403Forbidden);
        if (caller.TenantAssertions.Any(tenant =>
                !string.Equals(tenant, receipt.TenantId, StringComparison.Ordinal)))
            throw new ProjectFactApiException("accepted_effect_caller_mismatch", StatusCodes.Status403Forbidden);
    }

    private static void ValidateReceipt(
        AcceptedEffectReceipt receipt,
        AcceptedEffectRuntimeOptions options)
    {
        if (receipt.ReceiptId == Guid.Empty ||
            receipt.SchemaVersion != AcceptedEffectContractVersions.CurrentSchemaVersion ||
            receipt.EventVersion != AcceptedEffectContractVersions.CurrentEventVersion ||
            !ValidIdentifier(receipt.ProjectId) ||
            !ValidIdentifier(receipt.RunId) ||
            receipt.EffectId == Guid.Empty ||
            receipt.RecordId == Guid.Empty ||
            receipt.RecordVersion < 1 ||
            !string.Equals(receipt.Issuer, options.IdentityIssuer.AbsoluteUri, StringComparison.Ordinal) ||
            !ValidIdentifier(receipt.Subject) ||
            !ValidIdentifier(receipt.TenantId) ||
            (receipt.BoundProjectId is not null &&
                !string.Equals(receipt.BoundProjectId, receipt.ProjectId, StringComparison.Ordinal)) ||
            (receipt.BoundRunId is not null &&
                !string.Equals(receipt.BoundRunId, receipt.RunId, StringComparison.Ordinal)) ||
            (receipt.BoundRunId is not null && receipt.BoundProjectId is null) ||
            !Enum.IsDefined(receipt.AuthorizationResourceType) ||
            (receipt.AuthorizationResourceType == ProjectAuthorityResourceType.Project &&
                !string.Equals(receipt.AuthorizationResourceId, receipt.ProjectId, StringComparison.Ordinal)) ||
            (receipt.AuthorizationResourceType == ProjectAuthorityResourceType.Tenant &&
                !string.Equals(receipt.AuthorizationResourceId, receipt.TenantId, StringComparison.Ordinal)) ||
            !ValidIdentifier(receipt.AuthorizationResourceId) ||
            receipt.AuthorizationRevision < 1 ||
            receipt.MembershipRevision < 1 ||
            receipt.ProjectRevision < 1 ||
            receipt.ProjectConfigurationRevision < 1 ||
            !ValidIdentifier(receipt.ContextRevision) ||
            receipt.AcceptedAt == default)
            throw new ProjectFactApiException(
                "accepted_effect_receipt_invalid", StatusCodes.Status502BadGateway);
    }

    private static bool ValidIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');

    private static bool HasClaimIssuer(Claim claim, string expectedIssuer) =>
        TryNormalizeIssuer(claim.Issuer, out var issuer) &&
        string.Equals(issuer, expectedIssuer, StringComparison.Ordinal);

    private static bool HasIssuerValue(string? value, string expectedIssuer) =>
        TryNormalizeIssuer(value, out var issuer) &&
        string.Equals(issuer, expectedIssuer, StringComparison.Ordinal);

    private static bool TryNormalizeIssuer(string? value, out string issuer)
    {
        issuer = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            return false;

        issuer = uri.AbsoluteUri;
        return true;
    }
}

public sealed class ProjectFactConflictException : Exception
{
}

public sealed class ProjectFactApiException(string code, int statusCode) : Exception(code)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

internal static class ForwardedHeaders
{
    private const string KnowledgeAuthorizationHeader = "X-Agentweaver-Knowledge-Authorization";

    public static ForwardedCallerHeaders Read(
        HttpContext? context,
        bool knowledgeReceiptOwner = false)
    {
        var values = knowledgeReceiptOwner
            ? context?.Request.Headers[KnowledgeAuthorizationHeader] ?? default
            : context?.Request.Headers.Authorization ?? default;
        if (knowledgeReceiptOwner && values.Count == 0)
            values = context?.Request.Headers.Authorization ?? default;
        if (values.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(values[0], out var authorization) ||
            !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authorization.Parameter))
            return new ForwardedCallerHeaders(null, null, "caller_token_missing");
        var tenants = context!.Request.Headers["X-Agentweaver-Tenant"];
        if (tenants.Count > 1 ||
            (tenants.Count == 1 && !ValidTenant(tenants[0])))
            return new ForwardedCallerHeaders(null, null, "invalid_tenant_selector");
        return new ForwardedCallerHeaders(
            new AuthenticationHeaderValue("Bearer", authorization.Parameter),
            tenants.Count == 1 ? tenants[0] : null,
            null);
    }

    private static bool ValidTenant(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');
}

internal sealed record ForwardedCallerHeaders(
    AuthenticationHeaderValue? Authorization,
    string? TenantSelector,
    string? Error);
