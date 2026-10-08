extern alias ProjectsConfig;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using ProjectsConfig::Agentweaver.Projects.Config;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed partial class ProjectsConfigBrokerAuthorizationTests
{
    private async Task<CopilotConnectionReceipt> LinkRuntimeCopilotConnectionAsync(
        ProjectsConfigResourceServer projects, string ownerToken, ControlledCopilotConnection transport,
        ProjectAuthorityResourceType connectionScope = ProjectAuthorityResourceType.Platform, string scopeId = "default")
    {
        await using var factory = new IdentityBrokerWebApplicationFactory(
            _connectionString, _fakeIdp, signingCertificate: _signingCertificate,
            configure: transport.ConfigureSettings,
            configureServices: services => transport.ConfigureServices(services, projects.CreateHandler));
        using var broker = factory.CreateClient(new()
        {
            BaseAddress = new Uri("https://broker.test/"), AllowAutoRedirect = false
        });
        using var startResponse = await SendJsonAsync(
            broker, HttpMethod.Post, "/internal/connections/copilot-user/begin",
            ownerToken, new BeginCopilotConnectionRequest(connectionScope, scopeId));
        await AssertStatusAsync(startResponse, HttpStatusCode.OK);
        Assert.True(startResponse.Headers.CacheControl?.NoStore);
        using (var responseJson = System.Text.Json.JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal("pending", responseJson.RootElement.GetProperty("connection").GetProperty("state").GetString());
            Assert.False(responseJson.RootElement.TryGetProperty("callbackCookie", out _));
        }
        var start = await startResponse.Content.ReadFromJsonAsync<CopilotConnectionStart>(CoordinationJsonOptions);
        Assert.NotNull(start);
        var state = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(
            start.AuthorizationUri.Query)["state"].ToString();
        var input = new CompleteCopilotConnectionRequest(state, "controlled-code");
        using var completed = await SendJsonAsync(
            broker, HttpMethod.Post, "/internal/connections/copilot-user/complete", ownerToken, input);
        await AssertStatusAsync(completed, HttpStatusCode.OK);
        using (var responseJson = System.Text.Json.JsonDocument.Parse(await completed.Content.ReadAsStringAsync()))
            Assert.Equal("connected", responseJson.RootElement.GetProperty("state").GetString());
        var connection = await completed.Content.ReadFromJsonAsync<CopilotConnectionReceipt>(CoordinationJsonOptions);
        Assert.NotNull(connection);
        Assert.Equal(start.Connection.ConnectionId, connection.ConnectionId);
        Assert.Equal(connectionScope, connection.Scope);
        Assert.Equal(scopeId, connection.ScopeId);
        Assert.Equal(3, connection.Revision);
        Assert.Equal(CopilotConnectionState.Connected, connection.State);
        using var replay = await SendJsonAsync(
            broker, HttpMethod.Post, "/internal/connections/copilot-user/complete", ownerToken, input);
        await AssertStatusAsync(replay, HttpStatusCode.Forbidden);
        Assert.Equal(1, transport.Exchanges);
        Assert.Equal(1, transport.Writes);
        Assert.DoesNotContain(ControlledCopilotConnection.UserAccessToken, string.Join('\n', factory.LogMessages));
        Assert.DoesNotContain(ControlledCopilotConnection.UserRefreshToken, string.Join('\n', factory.LogMessages));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityBrokerDbContext>();
        var durable = await db.CopilotConnections.AsNoTracking().SingleAsync();
        Assert.Equal("42", durable.GitHubUserId);
        Assert.Equal($"copilot-user-{connection.ConnectionId:N}", durable.SecretId);
        Assert.Equal("version-1", durable.SecretVersion);
        Assert.Equal(3, await db.CopilotConnectionRevisions.CountAsync());
        return connection;
    }

    private async Task<CopilotConnectionReceipt> RefreshRuntimeCopilotConnectionAsync(
        ProjectsConfigResourceServer projects, string ownerToken,
        ControlledCopilotConnection transport, CopilotConnectionReceipt linked)
    {
        await using var factory = new IdentityBrokerWebApplicationFactory(
            _connectionString, _fakeIdp, signingCertificate: _signingCertificate,
            configure: transport.ConfigureSettings,
            configureServices: services => transport.ConfigureServices(services, projects.CreateHandler));
        using var broker = factory.CreateClient(new() { BaseAddress = new Uri("https://broker.test/") });
        using var response = await SendJsonAsync(
            broker, HttpMethod.Post, "/internal/connections/copilot-user/refresh", ownerToken,
            new ChangeCopilotConnectionRequest(linked.ConnectionId, linked.Revision));
        await AssertStatusAsync(response, HttpStatusCode.OK);
        var rotated = await response.Content.ReadFromJsonAsync<CopilotConnectionReceipt>(CoordinationJsonOptions);
        Assert.NotNull(rotated);
        Assert.Equal(linked.ConnectionId, rotated.ConnectionId);
        Assert.Equal(linked.Scope, rotated.Scope);
        Assert.Equal(linked.ScopeId, rotated.ScopeId);
        Assert.Equal(linked.Revision + 2, rotated.Revision);
        Assert.Equal(CopilotConnectionState.Connected, rotated.State);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityBrokerDbContext>();
        var current = await db.CopilotConnections.AsNoTracking().SingleAsync();
        Assert.Equal("42", current.GitHubUserId);
        Assert.Equal($"copilot-user-{linked.ConnectionId:N}", current.SecretId);
        Assert.Equal("version-2", current.SecretVersion);
        Assert.Equal(5, await db.CopilotConnectionRevisions.CountAsync());
        Assert.Equal(2, transport.Exchanges);
        Assert.Equal(2, transport.Writes);
        Assert.NotEqual(ControlledCopilotConnection.UserAccessToken, transport.AccessToken);
        Assert.NotEqual(ControlledCopilotConnection.UserRefreshToken, transport.RefreshToken);
        Assert.DoesNotContain(transport.AccessToken, string.Join('\n', factory.LogMessages));
        Assert.DoesNotContain(transport.RefreshToken, string.Join('\n', factory.LogMessages));
        return rotated;
    }

    [Fact]
    public async Task CopilotProjectConnectionUsesCurrentProjectOwnerAndRetainsHistoryOnDisconnect()
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath, _signingCertificate.Password);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            _connectionString, new X509SecurityKey(certificate));
        var ownerToken = await IssueTokenAsync(
            "projects.admin", [TenantId], "copilot-project-owner", null, null, []);
        var ownerId = SingleClaim(new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
            .ReadJwtToken(ownerToken).Claims, "sub");
        var membership = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, ownerId, TenantId);
        var tenantRole = await AssignRoleAsync(projects.PrivilegedFixtureDataSource, membership.MembershipId,
            ProjectAuthorityResourceType.Tenant, TenantId, ProjectAuthorityRole.TenantAdmin);
        using var created = await SendJsonAsync(projects.Client, HttpMethod.Post, "/api/projects/",
            ownerToken, new CreateProjectRequest { Name = "Copilot project connection" });
        await AssertStatusAsync(created, HttpStatusCode.Created);
        var project = await created.Content.ReadFromJsonAsync<ProjectSummary>(CoordinationJsonOptions);
        Assert.NotNull(project);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, membership.MembershipId,
            ProjectAuthorityResourceType.Project, project.ProjectId, ProjectAuthorityRole.Owner);
        await RevokeRoleAsync(projects.PrivilegedFixtureDataSource, tenantRole.AssignmentId, tenantRole.Revision);
        using var transport = new ControlledCopilotConnection();
        var linked = await LinkRuntimeCopilotConnectionAsync(
            projects, ownerToken, transport, ProjectAuthorityResourceType.Project, project.ProjectId);
        var rotated = await RefreshRuntimeCopilotConnectionAsync(projects, ownerToken, transport, linked);
        await using var factory = new IdentityBrokerWebApplicationFactory(
            _connectionString, _fakeIdp, signingCertificate: _signingCertificate,
            configure: transport.ConfigureSettings,
            configureServices: services => transport.ConfigureServices(services, projects.CreateHandler));
        using var broker = factory.CreateClient(new() { BaseAddress = new Uri("https://broker.test/") });
        using var platformDenied = await SendJsonAsync(broker, HttpMethod.Post,
            "/internal/connections/copilot-user/begin", ownerToken,
            new BeginCopilotConnectionRequest(ProjectAuthorityResourceType.Platform, "default"));
        await AssertStatusAsync(platformDenied, HttpStatusCode.Forbidden);
        using var status = await SendAsync(broker, HttpMethod.Get,
            $"/internal/connections/copilot-user/{linked.ConnectionId:D}", ownerToken, [TenantId]);
        await AssertStatusAsync(status, HttpStatusCode.OK);
        Assert.Equal(rotated, await status.Content.ReadFromJsonAsync<CopilotConnectionReceipt>(CoordinationJsonOptions));
        using var disconnected = await SendJsonAsync(broker, HttpMethod.Post,
            "/internal/connections/copilot-user/revoke", ownerToken,
            new ChangeCopilotConnectionRequest(linked.ConnectionId, rotated.Revision));
        await AssertStatusAsync(disconnected, HttpStatusCode.OK);
        using var replay = await SendJsonAsync(broker, HttpMethod.Post,
            "/internal/connections/copilot-user/revoke", ownerToken,
            new ChangeCopilotConnectionRequest(linked.ConnectionId, rotated.Revision));
        await AssertStatusAsync(replay, HttpStatusCode.Forbidden);
        using var refreshDenied = await SendJsonAsync(broker, HttpMethod.Post,
            "/internal/connections/copilot-user/refresh", ownerToken,
            new ChangeCopilotConnectionRequest(linked.ConnectionId, rotated.Revision + 1));
        await AssertStatusAsync(refreshDenied, HttpStatusCode.Forbidden);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityBrokerDbContext>();
        var current = await db.CopilotConnections.AsNoTracking().SingleAsync();
        Assert.Equal(CopilotConnectionState.Revoked, current.State);
        Assert.Equal("version-2", current.SecretVersion);
        Assert.Equal(6, await db.CopilotConnectionRevisions.CountAsync());
        Assert.Equal(2, transport.Writes);
        Assert.Equal(2, transport.Exchanges);
    }

    [Fact]
    public async Task CopilotCallbackRequiresBrowserNonceAndSameSubjectBeforeSingleUseExchange()
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath, _signingCertificate.Password);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            _connectionString, new X509SecurityKey(certificate));
        var ownerToken = await IssueTokenAsync(
            "projects.admin", [TenantId], "copilot-callback-owner", null, null, ["platform_admin"]);
        var ownerId = SingleClaim(new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
            .ReadJwtToken(ownerToken).Claims, "sub");
        var membership = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, ownerId, TenantId);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, membership.MembershipId,
            ProjectAuthorityResourceType.Platform, "default", ProjectAuthorityRole.PlatformAdmin);
        var foreignToken = await IssueTokenAsync(
            "projects.admin", [TenantId], "copilot-callback-foreign", null, null, ["platform_admin"]);
        using var transport = new ControlledCopilotConnection();
        await using var factory = new IdentityBrokerWebApplicationFactory(
            _connectionString, _fakeIdp, signingCertificate: _signingCertificate,
            configure: transport.ConfigureSettings,
            configureServices: services => transport.ConfigureServices(services, projects.CreateHandler));
        using var broker = factory.CreateClient(new()
        {
            BaseAddress = new Uri("https://broker.test/"), HandleCookies = false, AllowAutoRedirect = false
        });
        using var begin = await SendJsonAsync(
            broker, HttpMethod.Post, "/internal/connections/copilot-user/begin",
            ownerToken, new BeginCopilotConnectionRequest(ProjectAuthorityResourceType.Platform, "default"));
        await AssertStatusAsync(begin, HttpStatusCode.OK);
        var setCookie = Assert.Single(begin.Headers.GetValues("Set-Cookie"));
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=300", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", setCookie, StringComparison.OrdinalIgnoreCase);
        var cookie = setCookie.Split(';')[0];
        Assert.StartsWith(CopilotConnectionEndpoints.CallbackCookieName + "=", cookie);
        var started = await begin.Content.ReadFromJsonAsync<CopilotConnectionStart>(CoordinationJsonOptions);
        Assert.NotNull(started);
        var state = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(
            started.AuthorizationUri.Query)["state"].ToString();
        Assert.DoesNotContain(ownerToken, state);
        Assert.DoesNotContain(cookie.Split('=')[1], state);
        foreach (var (token, callbackState, callbackCookie) in new[]
        {
            (ownerToken, state, (string?)null),
            (ownerToken, state, CopilotConnectionEndpoints.CallbackCookieName + "=" + new string('0', 64)),
            (foreignToken, state, cookie),
            (ownerToken, state + "tampered", cookie)
        })
        {
            using var rejected = await CompleteCopilotCallbackAsync(
                broker, token, new(callbackState, "controlled-code"), callbackCookie);
            await AssertStatusAsync(rejected, HttpStatusCode.Forbidden);
        }
        Assert.Equal(0, transport.Exchanges);
        Assert.Equal(0, transport.Writes);
        using var accepted = await CompleteCopilotCallbackAsync(
            broker, ownerToken, new(state, "controlled-code"), cookie);
        await AssertStatusAsync(accepted, HttpStatusCode.OK);
        using var replay = await CompleteCopilotCallbackAsync(
            broker, ownerToken, new(state, "controlled-code"), cookie);
        await AssertStatusAsync(replay, HttpStatusCode.Forbidden);
        using var foreignStatus = await SendAsync(broker, HttpMethod.Get,
            $"/internal/connections/copilot-user/{started.Connection.ConnectionId:D}", foreignToken, [TenantId]);
        await AssertStatusAsync(foreignStatus, HttpStatusCode.Forbidden);
        Assert.Equal(1, transport.Exchanges);
        Assert.Equal(1, transport.Writes);
        Assert.DoesNotContain(cookie.Split('=')[1], string.Join('\n', factory.LogMessages));
    }

    private static async Task<HttpResponseMessage> CompleteCopilotCallbackAsync(
        HttpClient broker, string token, CompleteCopilotConnectionRequest input, string? cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/connections/copilot-user/complete")
        {
            Content = JsonContent.Create(input, options: CoordinationJsonOptions)
        };
        AddBearerAndTenant(request, token, TenantId);
        if (cookie is not null)
            request.Headers.Add("Cookie", cookie);
        return await broker.SendAsync(request);
    }

    [Theory]
    [InlineData("transient", CopilotConnectionState.TransientUnavailable, "copilot_connection_exchange_failed")]
    [InlineData("rejected", CopilotConnectionState.ReconnectRequired, "copilot_connection_refresh_rejected")]
    [InlineData("uncertain", CopilotConnectionState.RefreshIndeterminate, "copilot_connection_upstream_unavailable")]
    [InlineData("installation", CopilotConnectionState.RefreshIndeterminate, "copilot_connection_user_credential_invalid")]
    [InlineData("account", CopilotConnectionState.RefreshIndeterminate, "copilot_connection_identity_changed")]
    [InlineData("oauth-array", CopilotConnectionState.RefreshIndeterminate, "copilot_connection_upstream_contract_invalid")]
    [InlineData("oauth-null", CopilotConnectionState.RefreshIndeterminate, "copilot_connection_upstream_contract_invalid")]
    [InlineData("user-array", CopilotConnectionState.RefreshIndeterminate, "copilot_connection_upstream_contract_invalid")]
    [InlineData("user-null", CopilotConnectionState.RefreshIndeterminate, "copilot_connection_upstream_contract_invalid")]
    [InlineData("expires-string", CopilotConnectionState.RefreshIndeterminate, "copilot_connection_user_credential_invalid")]
    [InlineData("expires-null", CopilotConnectionState.RefreshIndeterminate, "copilot_connection_user_credential_invalid")]
    [InlineData("refresh-expires-string", CopilotConnectionState.RefreshIndeterminate, "copilot_connection_user_credential_invalid")]
    [InlineData("refresh-expires-null", CopilotConnectionState.RefreshIndeterminate, "copilot_connection_user_credential_invalid")]
    [InlineData("user-id-string", CopilotConnectionState.RefreshIndeterminate, "copilot_connection_user_verification_failed")]
    [InlineData("user-id-null", CopilotConnectionState.RefreshIndeterminate, "copilot_connection_user_verification_failed")]
    public async Task CopilotRefreshFailuresKeepCommittedVersionAndPreventUnsafeReplay(
        string fault, CopilotConnectionState expectedState, string error)
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath, _signingCertificate.Password);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            _connectionString, new X509SecurityKey(certificate));
        var ownerToken = await IssueTokenAsync(
            "projects.admin", [TenantId], "copilot-refresh-owner", null, null, ["platform_admin"]);
        var ownerId = SingleClaim(new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
            .ReadJwtToken(ownerToken).Claims, "sub");
        var membership = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, ownerId, TenantId);
        await AssignRoleAsync(projects.PrivilegedFixtureDataSource, membership.MembershipId,
            ProjectAuthorityResourceType.Platform, "default", ProjectAuthorityRole.PlatformAdmin);
        using var transport = new ControlledCopilotConnection();
        var linked = await LinkRuntimeCopilotConnectionAsync(projects, ownerToken, transport);
        var tokenReply = JsonSerializer.SerializeToNode(new
        {
            access_token = ControlledCopilotConnection.UserAccessToken,
            token_type = "bearer", expires_in = 3600,
            refresh_token = ControlledCopilotConnection.UserRefreshToken, refresh_token_expires_in = 86400
        })!.AsObject();
        switch (fault)
        {
            case "transient":
                transport.ExchangeStatus = HttpStatusCode.ServiceUnavailable;
                break;
            case "rejected":
                transport.ExchangeStatus = HttpStatusCode.BadRequest;
                transport.ExchangeError = "invalid_grant";
                break;
            case "uncertain":
                transport.LoseExchangeResponse = true;
                break;
            case "installation":
                transport.InstallationToken = true;
                break;
            case "account":
                transport.GitHubUserId = 99;
                break;
            case "oauth-array":
                transport.ExchangeJson = "[]";
                break;
            case "oauth-null":
                transport.ExchangeJson = "null";
                break;
            case "user-array":
                transport.UserJson = "[]";
                break;
            case "user-null":
                transport.UserJson = "null";
                break;
            case "expires-string":
            case "expires-null":
                tokenReply["expires_in"] = fault == "expires-string" ? JsonValue.Create("3600") : null;
                transport.ExchangeJson = tokenReply.ToJsonString();
                break;
            case "refresh-expires-string":
            case "refresh-expires-null":
                tokenReply["refresh_token_expires_in"] = fault == "refresh-expires-string"
                    ? JsonValue.Create("86400") : null;
                transport.ExchangeJson = tokenReply.ToJsonString();
                break;
            case "user-id-string":
                transport.UserJson = """{"id":"42","type":"User"}""";
                break;
            case "user-id-null":
                transport.UserJson = """{"id":null,"type":"User"}""";
                break;
        }
        await using var factory = new IdentityBrokerWebApplicationFactory(
            _connectionString, _fakeIdp, signingCertificate: _signingCertificate,
            configure: transport.ConfigureSettings,
            configureServices: services => transport.ConfigureServices(services, projects.CreateHandler));
        using var broker = factory.CreateClient(new() { BaseAddress = new Uri("https://broker.test/") });
        using var failed = await SendJsonAsync(broker, HttpMethod.Post,
            "/internal/connections/copilot-user/refresh", ownerToken,
            new ChangeCopilotConnectionRequest(linked.ConnectionId, linked.Revision));
        await AssertStatusAsync(failed, fault == "uncertain"
            ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Forbidden);
        Assert.True(failed.Headers.CacheControl?.NoStore);
        var failureBody = await failed.Content.ReadAsStringAsync();
        using (var failureJson = JsonDocument.Parse(failureBody))
            Assert.Equal(error, failureJson.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain(transport.AccessToken, failureBody);
        Assert.DoesNotContain(transport.RefreshToken, failureBody);
        Assert.DoesNotContain(ControlledCopilotConnection.UserAccessToken, failureBody);
        Assert.DoesNotContain(ControlledCopilotConnection.UserRefreshToken, failureBody);
        Assert.DoesNotContain(ControlledCopilotConnection.ClientSecret, failureBody);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityBrokerDbContext>();
        var current = await db.CopilotConnections.AsNoTracking().SingleAsync();
        Assert.Equal(expectedState, current.State);
        Assert.Equal(linked.Revision + 2, current.Revision);
        Assert.Equal("42", current.GitHubUserId);
        Assert.Equal($"copilot-user-{linked.ConnectionId:N}", current.SecretId);
        Assert.Equal("version-1", current.SecretVersion);
        Assert.Equal(linked.ConnectionId, current.ConnectionId);
        Assert.Equal(1, transport.Writes);
        Assert.Equal(2, transport.Exchanges);
        if (fault.StartsWith("oauth-", StringComparison.Ordinal) ||
            fault.StartsWith("expires-", StringComparison.Ordinal) ||
            fault.StartsWith("refresh-expires-", StringComparison.Ordinal))
            Assert.Equal(1, transport.UserReads);
        if (fault.StartsWith("user-", StringComparison.Ordinal))
            Assert.Equal(2, transport.UserReads);
        if (expectedState == CopilotConnectionState.TransientUnavailable)
        {
            transport.ExchangeStatus = HttpStatusCode.OK;
            using var recovered = await SendJsonAsync(broker, HttpMethod.Post,
                "/internal/connections/copilot-user/refresh", ownerToken,
                new ChangeCopilotConnectionRequest(linked.ConnectionId, current.Revision));
            await AssertStatusAsync(recovered, HttpStatusCode.OK);
            Assert.Equal(3, transport.Exchanges);
            Assert.Equal(2, transport.Writes);
        }
        else
        {
            using var retry = await SendJsonAsync(broker, HttpMethod.Post,
                "/internal/connections/copilot-user/refresh", ownerToken,
                new ChangeCopilotConnectionRequest(linked.ConnectionId, current.Revision));
            await AssertStatusAsync(retry, HttpStatusCode.Forbidden);
            Assert.Equal(2, transport.Exchanges);
            Assert.Equal(1, transport.Writes);
        }
        using var revoked = await SendJsonAsync(broker, HttpMethod.Post,
            "/internal/connections/copilot-user/revoke", ownerToken,
            new ChangeCopilotConnectionRequest(linked.ConnectionId,
                current.Revision + (expectedState == CopilotConnectionState.TransientUnavailable ? 2 : 0)));
        await AssertStatusAsync(revoked, HttpStatusCode.OK);
        var revokedReceipt = await revoked.Content.ReadFromJsonAsync<CopilotConnectionReceipt>(CoordinationJsonOptions);
        Assert.NotNull(revokedReceipt);
        Assert.Equal(CopilotConnectionState.Revoked, revokedReceipt.State);
        Assert.DoesNotContain(transport.AccessToken, string.Join('\n', factory.LogMessages));
        Assert.DoesNotContain(transport.RefreshToken, string.Join('\n', factory.LogMessages));
        Assert.DoesNotContain(ControlledCopilotConnection.ClientSecret, string.Join('\n', factory.LogMessages));
        Assert.DoesNotContain(ControlledCopilotConnection.UserAccessToken, string.Join('\n', factory.LogMessages));
        Assert.DoesNotContain(ControlledCopilotConnection.UserRefreshToken, string.Join('\n', factory.LogMessages));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopilotConnectionRotationIsSerializedAndCurrentCoreRevocationPreventsPublication(
        bool revokeDuringSqlPublication)
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _signingCertificate.PfxPath, _signingCertificate.Password);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            _connectionString, new X509SecurityKey(certificate));
        var ownerToken = await IssueTokenAsync(
            "projects.admin", [TenantId], "copilot-link-owner", null, null, ["platform_admin"]);
        var ownerId = SingleClaim(new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
            .ReadJwtToken(ownerToken).Claims, "sub");
        var membership = await AddMembershipAsync(projects.PrivilegedFixtureDataSource, ownerId, TenantId);
        var role = await AssignRoleAsync(projects.PrivilegedFixtureDataSource, membership.MembershipId,
            ProjectAuthorityResourceType.Platform, "default", ProjectAuthorityRole.PlatformAdmin);
        using var transport = new ControlledCopilotConnection();
        var linked = await LinkRuntimeCopilotConnectionAsync(projects, ownerToken, transport);
        await using var factory = new IdentityBrokerWebApplicationFactory(
            _connectionString, _fakeIdp, signingCertificate: _signingCertificate,
            configure: transport.ConfigureSettings,
            configureServices: services => transport.ConfigureServices(services, projects.CreateHandler));
        using var broker = factory.CreateClient(new() { BaseAddress = new Uri("https://broker.test/") });
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.BeforeWriteResponse = async cancellationToken =>
        {
            held.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var request = new ChangeCopilotConnectionRequest(linked.ConnectionId, linked.Revision);
        var refresh = SendJsonAsync(broker, HttpMethod.Post, "/internal/connections/copilot-user/refresh",
            ownerToken, request);
        await held.Task.WaitAsync(timeout.Token);
        try
        {
            using var competing = await SendJsonAsync(
                broker, HttpMethod.Post, "/internal/connections/copilot-user/refresh", ownerToken, request);
            await AssertStatusAsync(competing, HttpStatusCode.Forbidden);
            if (revokeDuringSqlPublication)
            {
                await using var publicationConnection = await _nativeDataSource.OpenConnectionAsync(timeout.Token);
                await using var transaction = await publicationConnection.BeginTransactionAsync(timeout.Token);
                await using var holdPublication = new Npgsql.NpgsqlCommand("""
                    SELECT "ConnectionId" FROM identity_broker.copilot_connections
                    WHERE "ConnectionId" = @connection FOR UPDATE
                    """, publicationConnection, transaction);
                holdPublication.Parameters.AddWithValue("connection", linked.ConnectionId);
                Assert.Equal(linked.ConnectionId, await holdPublication.ExecuteScalarAsync(timeout.Token));
                release.TrySetResult();
                await WaitForCopilotPublicationWaitAsync(timeout.Token);
                await RevokeRoleAsync(projects.PrivilegedFixtureDataSource, role.AssignmentId, role.Revision);
                await transaction.CommitAsync(timeout.Token);
            }
            else
            {
                await RevokeRoleAsync(projects.PrivilegedFixtureDataSource, role.AssignmentId, role.Revision);
            }
        }
        finally
        {
            release.TrySetResult();
        }
        using var denied = await refresh.WaitAsync(timeout.Token);
        await AssertStatusAsync(denied, HttpStatusCode.Forbidden);
        Assert.Equal(2, transport.Exchanges);
        Assert.Equal(2, transport.Writes);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityBrokerDbContext>();
        var current = await db.CopilotConnections.AsNoTracking().SingleAsync();
        Assert.Equal(CopilotConnectionState.RefreshIndeterminate, current.State);
        Assert.Equal("version-1", current.SecretVersion);
        Assert.Equal(linked.ConnectionId, current.ConnectionId);
        using var retry = await SendJsonAsync(
            broker, HttpMethod.Post, "/internal/connections/copilot-user/refresh", ownerToken,
            request with { ExpectedRevision = current.Revision });
        await AssertStatusAsync(retry, HttpStatusCode.Forbidden);
        Assert.Equal(2, transport.Exchanges);
        await using var connection = await _nativeDataSource.OpenConnectionAsync();
        await using var audit = new Npgsql.NpgsqlCommand("""
            SELECT string_agg(to_jsonb(row)::text, '') FROM identity_broker.copilot_connections row
            """, connection);
        var stored = (string)(await audit.ExecuteScalarAsync())!;
        Assert.DoesNotContain(ControlledCopilotConnection.UserAccessToken, stored);
        Assert.DoesNotContain(ControlledCopilotConnection.UserRefreshToken, stored);
    }

    private async Task WaitForCopilotPublicationWaitAsync(CancellationToken cancellationToken)
    {
        await using var observer = await _nativeDataSource.OpenConnectionAsync(cancellationToken);
        for (var attempt = 0; attempt < 400; attempt++)
        {
            await using var wait = new Npgsql.NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_stat_activity
                    WHERE datname = current_database() AND wait_event_type = 'Lock'
                      AND query LIKE 'UPDATE%copilot_connections%'
                )
                """, observer);
            if ((bool)(await wait.ExecuteScalarAsync(cancellationToken))!)
                return;
            await Task.Delay(25, cancellationToken);
        }
        Assert.Fail("The actual Copilot publication UPDATE did not wait on its PostgreSQL row lock.");
    }
}
