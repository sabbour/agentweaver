using System.Text.Json;
using Agentweaver.Environment;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agentweaver.Environment.Tests;

public sealed class EnvironmentProviderLifecycleReportEndpointTests
{
    [Fact]
    public void ReportModelMatchesTheNamedOwnerAndSandboxLeaseRelationships()
    {
        var options = new DbContextOptionsBuilder<EnvironmentDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        using var context = new EnvironmentDbContext(options);
        var report = context.Model.FindEntityType(typeof(EnvironmentProviderLifecycleReportRow))!;
        var foreignKeys = report.GetForeignKeys().ToDictionary(
            foreignKey => foreignKey.GetConstraintName()!,
            StringComparer.Ordinal);

        Assert.Contains("fk_environment_provider_lifecycle_reports_owners", foreignKeys.Keys);
        var leaseForeignKey = foreignKeys["fk_environment_provider_lifecycle_reports_sandbox_leases"];
        Assert.Equal(
            "sandbox_leases",
            leaseForeignKey.PrincipalEntityType.GetTableName());
        Assert.Equal(
            new[]
            {
                nameof(EnvironmentProviderLifecycleReportRow.TenantId),
                nameof(EnvironmentProviderLifecycleReportRow.ProjectId),
                nameof(EnvironmentProviderLifecycleReportRow.RunId),
                nameof(EnvironmentProviderLifecycleReportRow.EnvironmentId),
                nameof(EnvironmentProviderLifecycleReportRow.ResourceGeneration)
            },
            leaseForeignKey.Properties.Select(property => property.Name));
        Assert.Contains(
            report.GetIndexes(),
            index => index.GetDatabaseName() == "ix_environment_provider_lifecycle_reports_core_operation");
        Assert.Contains(
            report.GetIndexes(),
            index => index.GetDatabaseName() == "ix_environment_provider_lifecycle_reports_pending");
    }

    [Fact]
    public void ProviderLifecycleReportRouteRequiresAuthenticationAndFailsClosed()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        using var app = builder.Build();
        app.MapProviderLifecycleReportEndpoints();

        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>());
        Assert.Equal("/internal/provider-lifecycle/reports", endpoint.RoutePattern.RawText);
        Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.DoesNotContain(endpoint.Metadata, item => item is IAllowAnonymous);
        Assert.Equal(["POST"], endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
    }

    [Fact]
    public async Task ProviderLifecycleReportRouteReturnsUnavailableUntilAnAuthenticatorIsAdmitted()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        using var app = builder.Build();
        app.MapProviderLifecycleReportEndpoints();

        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>());
        var context = new DefaultHttpContext();
        context.RequestServices = app.Services;
        await using var response = new MemoryStream();
        context.Response.Body = response;

        await endpoint.RequestDelegate!(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        response.Position = 0;
        using var body = await JsonDocument.ParseAsync(response);
        Assert.Equal(
            "environment_provider_lifecycle_unavailable",
            body.RootElement.GetProperty("code").GetString());
    }
}
