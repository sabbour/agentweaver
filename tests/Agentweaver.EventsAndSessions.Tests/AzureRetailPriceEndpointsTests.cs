using System.Net;
using System.Security.Claims;
using System.Text;
using Agentweaver.EventsAndSessions;
using Agentweaver.EventsAndSessions.Cost;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

public sealed class AzureRetailPriceEndpointsTests
{
    private const string ProjectsAudience = "agentweaver.projects";
    private const string ActorId = "33333333-3333-3333-3333-333333333333";

    [Fact]
    public async Task AuthorizesThroughProjectsBeforeSearchingAzureRetail()
    {
        var callOrder = new List<string>();
        var projectsHandler = new ResponseHandler(_ =>
        {
            callOrder.Add("projects");
            return NoContent();
        });
        var retailHandler = new ResponseHandler(_ =>
        {
            callOrder.Add("retail");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"Items":[{"armRegionName":"eastus","serviceName":"Foundry Models","currencyCode":"USD",
                    "type":"Consumption","meterId":"meter-1","meterName":"gpt input","productName":"Models",
                    "skuName":"Input","unitOfMeasure":"1M","retailPrice":5,"tierMinimumUnits":0}]}
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var projects = CreateProjectsClient(projectsHandler);
        using var lookup = new AzureRetailPriceLookup(retailHandler);
        var context = CreateRequestContext("?region=EASTUS&search=gpt");

        var result = await AzureRetailPriceEndpoints.SearchAsync(
            "project-1", context, projects, lookup, Configuration(), CancellationToken.None);

        Assert.Equal(["projects", "retail"], callOrder);
        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var body = Assert.IsType<AzureRetailPriceLookupResult>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.Equal("eastus", body.Region);
        Assert.Equal(5m, Assert.Single(body.Items).UsdPer1M);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task DeniedProjectsCheckStopsTheAzureRetailRequest()
    {
        var retailHandler = new ResponseHandler(_ =>
            throw new InvalidOperationException("Azure Retail must not be called after denial."));
        var projects = CreateProjectsClient(new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using var lookup = new AzureRetailPriceLookup(retailHandler);
        var context = CreateRequestContext("?region=eastus&search=gpt");

        var result = await AzureRetailPriceEndpoints.SearchAsync(
            "project-1", context, projects, lookup, Configuration(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
    }

    [Theory]
    [InlineData("?region=eastus&search=gpt&other=x")]
    [InlineData("?region=eastus&region=westus&search=gpt")]
    [InlineData("?region=eastus")]
    public async Task RejectsUnknownRepeatedOrMissingQueryValuesBeforeProjectsCall(string query)
    {
        var projectsHandler = new ResponseHandler(_ =>
            throw new InvalidOperationException("Projects must not be called for an invalid query."));
        var projects = CreateProjectsClient(projectsHandler);
        using var lookup = new AzureRetailPriceLookup(new ResponseHandler(_ =>
            throw new InvalidOperationException("Azure Retail must not be called for an invalid query.")));
        var context = CreateRequestContext(query);

        var result = await AzureRetailPriceEndpoints.SearchAsync(
            "project-1", context, projects, lookup, Configuration(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
    }

    private static ProjectsAuthorizationContextClient CreateProjectsClient(HttpMessageHandler handler) =>
        new(new HttpClient(handler), new ProjectsAuthorizationContextOptions(
            "https://projects.internal/", ProjectsAudience, "https://identity.example"));

    private static DefaultHttpContext CreateRequestContext(string query)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("sub", ActorId),
                    new Claim("project_id", "project-1"),
                    new Claim("aud", ProjectsAudience),
                    new Claim("aud", "events"),
                    new Claim("scope", "api.read")
                ],
                "validated-bearer"))
        };
        context.Request.Headers.Authorization = "Bearer current-caller-token";
        context.Request.Headers["X-Agentweaver-Tenant"] = "tenant-1";
        context.Request.QueryString = new QueryString(query);
        return context;
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Identity:Audience"] = "events" }).Build();

    private static HttpResponseMessage NoContent() =>
        new(HttpStatusCode.NoContent)
        {
            Headers = { CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true } }
        };

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
