using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Identity;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Agentweaver.Identity.Broker;

internal sealed class BrokerRunBindingAuthority(
    IdentityBrokerOptions brokerOptions,
    X509Certificate2 signingCertificate,
    HttpClient projectsClient,
    TimeProvider timeProvider)
{
    internal const string HttpClientName = "BrokerRunBindingProjects";
    internal const string JwtType = "agentweaver-run-binding-query+jwt";
    internal const string ServiceSubject = "identity-broker";
    internal const string Purpose = "run-binding-query";
    internal const string ProjectsAuthorityKind = "projects-owner";
    internal const string SecretGrantAuthorityKind = "secret-grant";

    internal const string AuthorityKindClaim = "aw:run_binding_authority";
    private const string ProjectsOrchestratorScope = "projects.orchestrator";
    private const string ActorIssuerClaim = "aw:run_binding_actor_issuer";
    private const string ActorSubjectClaim = "aw:run_binding_actor_subject";
    private const string TenantIdClaim = "aw:run_binding_tenant_id";
    private const string MembershipIdClaim = "aw:run_binding_membership_id";
    private const string MembershipRevisionClaim = "aw:run_binding_membership_revision";
    private const string AssignmentIdClaim = "aw:run_binding_assignment_id";
    private const string AssignmentRevisionClaim = "aw:run_binding_assignment_revision";
    private const string ProjectRevisionClaim = "aw:run_binding_project_revision";
    private const string ProjectConfigurationRevisionClaim = "aw:run_binding_project_configuration_revision";
    private const string PlatformRuntimeRevisionClaim = "aw:run_binding_platform_runtime_revision";
    private const string ContextRevisionClaim = "aw:run_binding_context_revision";
    private const string SelectionHashClaim = "aw:run_binding_selection_hash";
    private const int MaximumResponseBytes = 16 * 1024;
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };

    private static readonly string[] ReceiptClaimTypes =
    [
        ActorIssuerClaim,
        ActorSubjectClaim,
        TenantIdClaim,
        MembershipIdClaim,
        MembershipRevisionClaim,
        AssignmentIdClaim,
        AssignmentRevisionClaim,
        ProjectRevisionClaim,
        ProjectConfigurationRevisionClaim,
        PlatformRuntimeRevisionClaim,
        ContextRevisionClaim,
        SelectionHashClaim,
    ];

    public async Task<BrokerRunBindingCheck> CheckAsync(
        Guid userId,
        string projectId,
        string runId,
        BrokerRunBindingReceipt? expectedReceipt,
        CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty ||
            !IsValidIdentifier(projectId) ||
            !IsValidIdentifier(runId) ||
            !TryGetSettings(brokerOptions, out var ownerAddress, out var privateAudience))
            return BrokerRunBindingCheck.Denied;

        var endpoint = new Uri(ownerAddress, $"/internal/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}/broker-run-binding");
        var token = CreateQueryToken(privateAudience, userId, projectId, runId);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, NoCache = true };

        try
        {
            using var response = await projectsClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri != endpoint ||
                (int)response.StatusCode is >= 300 and <= 399)
                return BrokerRunBindingCheck.Unavailable;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or
                HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict)
                return BrokerRunBindingCheck.Denied;
            if (!response.IsSuccessStatusCode ||
                response.Headers.CacheControl?.NoStore != true ||
                !string.Equals(
                    response.Content.Headers.ContentType?.MediaType,
                    "application/json",
                    StringComparison.OrdinalIgnoreCase) ||
                response.Content.Headers.ContentLength is { } length &&
                (length <= 0 || length > MaximumResponseBytes))
                return BrokerRunBindingCheck.Unavailable;

            var bytes = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
                return BrokerRunBindingCheck.Unavailable;
            BrokerRunBindingReceipt? receipt;
            try
            {
                receipt = JsonSerializer.Deserialize<BrokerRunBindingReceipt>(bytes, JsonOptions);
            }
            catch (JsonException)
            {
                return BrokerRunBindingCheck.Unavailable;
            }

            var issuer = new Uri(brokerOptions.Issuer).AbsoluteUri;
            if (receipt is null || !receipt.IsValid)
                return BrokerRunBindingCheck.Unavailable;
            if (receipt.ActorIssuer != issuer ||
                receipt.ActorSubject != userId.ToString("D") ||
                receipt.ProjectId != projectId ||
                receipt.RunId != runId ||
                expectedReceipt is not null && receipt != expectedReceipt)
                return BrokerRunBindingCheck.Denied;

            return BrokerRunBindingCheck.Authorized(receipt);
        }
        catch (HttpRequestException)
        {
            return BrokerRunBindingCheck.Unavailable;
        }
        catch (IOException)
        {
            return BrokerRunBindingCheck.Unavailable;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BrokerRunBindingCheck.Unavailable;
        }
    }

    internal static void AddAuthorityClaim(ClaimsIdentity identity, string authorityKind) =>
        AddAccessTokenClaim(identity, AuthorityKindClaim, authorityKind);

    internal static bool UsesProjectsAuthority(IEnumerable<string> scopes) =>
        scopes.Contains(ProjectsOrchestratorScope, StringComparer.Ordinal);

    internal static bool IsAuthorityKindConsistentWithScopes(
        string? authorityKind,
        IEnumerable<string> scopes) =>
        UsesProjectsAuthority(scopes) == (authorityKind == ProjectsAuthorityKind);

    internal static void AddReceiptClaims(ClaimsIdentity identity, BrokerRunBindingReceipt receipt)
    {
        AddAuthorityClaim(identity, ProjectsAuthorityKind);
        AddAccessTokenClaim(identity, ActorIssuerClaim, receipt.ActorIssuer);
        AddAccessTokenClaim(identity, ActorSubjectClaim, receipt.ActorSubject);
        AddAccessTokenClaim(identity, TenantIdClaim, receipt.TenantId);
        AddAccessTokenClaim(identity, MembershipIdClaim, receipt.MembershipId.ToString("D"));
        AddAccessTokenClaim(identity, MembershipRevisionClaim, receipt.MembershipRevision.ToString(CultureInfo.InvariantCulture));
        AddAccessTokenClaim(identity, AssignmentIdClaim, receipt.AssignmentId.ToString("D"));
        AddAccessTokenClaim(identity, AssignmentRevisionClaim, receipt.AssignmentRevision.ToString(CultureInfo.InvariantCulture));
        AddAccessTokenClaim(identity, ProjectRevisionClaim, receipt.ProjectRevision.ToString(CultureInfo.InvariantCulture));
        AddAccessTokenClaim(identity, ProjectConfigurationRevisionClaim, receipt.ProjectConfigurationRevision.ToString(CultureInfo.InvariantCulture));
        AddAccessTokenClaim(identity, PlatformRuntimeRevisionClaim, receipt.PlatformRuntimeRevision.ToString(CultureInfo.InvariantCulture));
        AddAccessTokenClaim(identity, ContextRevisionClaim, receipt.ContextRevision);
        AddAccessTokenClaim(identity, SelectionHashClaim, receipt.SelectionHash);
    }

    private static void AddAccessTokenClaim(ClaimsIdentity identity, string claimType, string value) =>
        identity.AddClaim(new Claim(claimType, value).SetDestinations(Destinations.AccessToken));

    internal static bool TryReadTicket(
        ClaimsPrincipal principal,
        RequestedRunBinding? binding,
        out string? authorityKind,
        out BrokerRunBindingReceipt? receipt)
    {
        authorityKind = null;
        receipt = null;
        var authorityClaims = principal.FindAll(AuthorityKindClaim).Take(2).ToArray();
        var hasReceiptClaim = ReceiptClaimTypes.Any(type => principal.FindFirst(type) is not null);
        if (authorityClaims.Length == 0)
            return !hasReceiptClaim;
        if (authorityClaims.Length != 1)
            return false;

        authorityKind = authorityClaims[0].Value;
        if (authorityKind == SecretGrantAuthorityKind)
            return binding is not null && !hasReceiptClaim;
        if (authorityKind != ProjectsAuthorityKind || binding is null)
            return false;

        if (!TryReadSingle(ActorIssuerClaim, out var actorIssuer) ||
            !TryReadSingle(ActorSubjectClaim, out var actorSubject) ||
            !TryReadSingle(TenantIdClaim, out var tenantId) ||
            !TryReadSingle(MembershipIdClaim, out var membershipIdText) ||
            !Guid.TryParseExact(membershipIdText, "D", out var membershipId) ||
            !TryReadPositiveLong(MembershipRevisionClaim, out var membershipRevision) ||
            !TryReadSingle(AssignmentIdClaim, out var assignmentIdText) ||
            !Guid.TryParseExact(assignmentIdText, "D", out var assignmentId) ||
            !TryReadPositiveLong(AssignmentRevisionClaim, out var assignmentRevision) ||
            !TryReadPositiveLong(ProjectRevisionClaim, out var projectRevision) ||
            !TryReadPositiveLong(ProjectConfigurationRevisionClaim, out var projectConfigurationRevision) ||
            !TryReadPositiveLong(PlatformRuntimeRevisionClaim, out var platformRuntimeRevision) ||
            !TryReadSingle(ContextRevisionClaim, out var contextRevision) ||
            !TryReadSingle(SelectionHashClaim, out var selectionHash))
            return false;

        if (membershipId == Guid.Empty || assignmentId == Guid.Empty ||
            !IsValidIdentifier(actorSubject) ||
            !Uri.TryCreate(actorIssuer, UriKind.Absolute, out var issuerUri) ||
            issuerUri.Scheme != Uri.UriSchemeHttps ||
            issuerUri.AbsoluteUri != actorIssuer ||
            !IsValidIdentifier(tenantId) ||
            string.IsNullOrWhiteSpace(contextRevision) ||
            contextRevision.Length > 256 ||
            selectionHash.Length != 64 ||
            !selectionHash.All(Uri.IsHexDigit))
            return false;

        receipt = new BrokerRunBindingReceipt(
            actorIssuer,
            actorSubject,
            tenantId,
            membershipId,
            membershipRevision,
            binding.ProjectId,
            binding.RunId,
            assignmentId,
            assignmentRevision,
            projectRevision,
            projectConfigurationRevision,
            platformRuntimeRevision,
            contextRevision,
            selectionHash);
        return true;

        bool TryReadSingle(string type, out string value)
        {
            var claims = principal.FindAll(type).Take(2).ToArray();
            value = claims.Length == 1 ? claims[0].Value : string.Empty;
            return claims.Length == 1;
        }

        bool TryReadPositiveLong(string type, out long value) =>
            TryReadPositiveLongCore(type, out value);

        bool TryReadPositiveLongCore(string type, out long value)
        {
            value = 0;
            return TryReadSingle(type, out var text) &&
                long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
                value > 0;
        }
    }

    internal static BrokerRunBindingTlsIdentity? LoadTlsConfiguration(IdentityBrokerOptions options)
    {
        var runBindingOptions = options.RunBinding;
        if (runBindingOptions is null)
            return null;
        ValidateTlsConfiguration(options);
        return BrokerRunBindingTlsIdentity.Load(runBindingOptions);
    }

    internal static void ValidateTlsConfiguration(IdentityBrokerOptions options)
    {
        if (options.RunBinding is not null && !TryGetSettings(options, out _, out _))
            throw new InvalidOperationException("IdentityBroker run-binding settings are incomplete or invalid.");
    }

    private static bool TryGetSettings(
        IdentityBrokerOptions brokerOptions, out Uri ownerAddress, out string privateAudience)
    {
        ownerAddress = null!;
        privateAudience = string.Empty;
        var options = brokerOptions.RunBinding;
        if (options is null ||
            !Uri.TryCreate(options.ProjectsOwnerAddress, UriKind.Absolute, out var parsedAddress) ||
            !RuntimeContractValidation.IsHttpsEndpoint(parsedAddress) ||
            parsedAddress.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(parsedAddress.Query) ||
            !string.IsNullOrEmpty(parsedAddress.Fragment) ||
            !IsSha256(options.ProjectsTrustBundleSha256) ||
            !IsSha256(options.ProjectsServerCertificateSha256) ||
            !Uri.TryCreate(options.ProjectsAudience, UriKind.Absolute, out var parsedAudience) ||
            !RuntimeContractValidation.IsHttpsEndpoint(parsedAudience) ||
            !IsDistinctFromPublicResources(brokerOptions, parsedAudience))
            return false;

        ownerAddress = RuntimeOwnerHttpTransport.RequireOwnerAddress(parsedAddress);
        privateAudience = parsedAudience.AbsoluteUri;
        return true;
    }

    private static bool IsDistinctFromPublicResources(
        IdentityBrokerOptions brokerOptions, Uri privateAudience)
    {
        var canonicalAudience = privateAudience.AbsoluteUri;
        foreach (var resource in brokerOptions.Clients.SelectMany(client => client.Resources))
        {
            if (string.Equals(resource, canonicalAudience, StringComparison.Ordinal) ||
                Uri.TryCreate(resource, UriKind.Absolute, out var resourceUri) &&
                string.Equals(resourceUri.AbsoluteUri, canonicalAudience, StringComparison.Ordinal))
                return false;
        }

        return !string.Equals(
            brokerOptions.SecretRedemption.Audience,
            canonicalAudience,
            StringComparison.Ordinal);
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private string CreateQueryToken(string audience, Guid userId, string projectId, string runId)
    {
        var now = timeProvider.GetUtcNow();
        var claims = new[]
        {
            new Claim("sub", ServiceSubject),
            new Claim("actor_iss", new Uri(brokerOptions.Issuer).AbsoluteUri),
            new Claim("actor_sub", userId.ToString("D")),
            new Claim("project_id", projectId),
            new Claim("run_id", runId),
            new Claim("purpose", Purpose),
            new Claim(
                JwtRegisteredClaimNames.Iat,
                now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64),
            new Claim("jti", Guid.NewGuid().ToString("N")),
        };
        var token = new JwtSecurityToken(
            new Uri(brokerOptions.Issuer).AbsoluteUri,
            audience,
            claims,
            now.UtcDateTime.AddSeconds(-5),
            now.Add(TokenLifetime).UtcDateTime,
            new SigningCredentials(new X509SecurityKey(signingCertificate), SecurityAlgorithms.RsaSha256));
        token.Header["typ"] = JwtType;
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(
                   buffer.AsMemory(0, (int)Math.Min(buffer.Length, MaximumResponseBytes - bytes.Length + 1)),
                   cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + count > MaximumResponseBytes)
                return null;
            bytes.Write(buffer, 0, count);
        }

        return bytes.ToArray();
    }

    private static bool IsValidIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');
}

internal sealed record BrokerRunBindingCheck(
    BrokerRunBindingStatus Status,
    BrokerRunBindingReceipt? Receipt)
{
    public static BrokerRunBindingCheck Denied { get; } =
        new(BrokerRunBindingStatus.Denied, null);

    public static BrokerRunBindingCheck Unavailable { get; } =
        new(BrokerRunBindingStatus.Unavailable, null);

    public static BrokerRunBindingCheck Authorized(BrokerRunBindingReceipt receipt) =>
        new(BrokerRunBindingStatus.Authorized, receipt);
}

internal enum BrokerRunBindingStatus
{
    Authorized,
    Denied,
    Unavailable,
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record BrokerRunBindingReceipt(
    string ActorIssuer,
    string ActorSubject,
    string TenantId,
    Guid MembershipId,
    long MembershipRevision,
    string ProjectId,
    string RunId,
    Guid AssignmentId,
    long AssignmentRevision,
    long ProjectRevision,
    long ProjectConfigurationRevision,
    long PlatformRuntimeRevision,
    string ContextRevision,
    string SelectionHash)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(ActorIssuer) &&
        Uri.TryCreate(ActorIssuer, UriKind.Absolute, out var actorIssuer) &&
        actorIssuer.Scheme == Uri.UriSchemeHttps &&
        actorIssuer.AbsoluteUri == ActorIssuer &&
        Guid.TryParseExact(ActorSubject, "D", out var actorId) &&
        actorId != Guid.Empty &&
        ActorSubject == actorId.ToString("D") &&
        IsValidIdentifier(TenantId) &&
        MembershipId != Guid.Empty &&
        AssignmentId != Guid.Empty &&
        MembershipRevision > 0 &&
        AssignmentRevision > 0 &&
        ProjectRevision > 0 &&
        ProjectConfigurationRevision > 0 &&
        PlatformRuntimeRevision > 0 &&
        !string.IsNullOrWhiteSpace(ContextRevision) &&
        ContextRevision.Length <= 256 &&
        !string.IsNullOrWhiteSpace(SelectionHash) &&
        SelectionHash.Length == 64 &&
        SelectionHash.All(Uri.IsHexDigit);

    private static bool IsValidIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');
}
