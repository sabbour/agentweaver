using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class RuntimeRunAdmissionClientTests
{
    private static readonly OrchestratorOptions Options = new(
        "https://broker.test/", "https://orchestrator.test", "https://projects.test/",
        "https://projects.test", "https://events.test/", "https://events.test");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public async Task ForwardsOnlyTheTrustedSelectionHashToTheFixedNoStoreOwner()
    {
        var selection = Selection();
        var receipt = CoordinatorRunAdmissionTests.Receipt(selection);
        var handler = new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://events.test/internal/projects/project/runs/run/usage/copilot-run-admission",
                request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer test-bearer", request.Headers.Authorization!.ToString());
            Assert.Equal("tenant-1", request.Headers.GetValues("X-Agentweaver-Tenant").Single());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(2, body.RootElement.EnumerateObject().Count());
            Assert.Equal(RuntimeRunAdmissionContract.SelectionHash(selection.Selection.Snapshot),
                body.RootElement.GetProperty("acceptedSelectionHash").GetString());
            return Response(request, receipt);
        });
        using var http = new HttpClient(handler);
        var result = await new RuntimeRunAdmissionClient(http, Options, TimeProvider.System)
            .ReadAsync(Context(), selection, default);
        Assert.Equal(receipt.ModelSelection, result.ModelSelection);
        Assert.Equal(receipt.ModelBindingPin, result.ModelBindingPin);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("audience")]
    [InlineData("scope")]
    [InlineData("owner")]
    [InlineData("expiry")]
    public async Task InvalidCallerOrOwnerCannotMakeAnAdmissionRequest(string fault)
    {
        var context = Context(expired: fault == "expiry", audience: fault == "audience" ? "foreign" : Options.EventsAudience,
            runId: fault == "scope" ? "foreign" : "run");
        var handler = new Handler(_ => throw new InvalidOperationException("No owner request is authorized."));
        using var http = new HttpClient(handler);
        var options = fault == "owner" ? Options with { EventsOwnerBaseAddress = "http://events.test/" } : Options;
        await Assert.ThrowsAsync<CoordinationException>(() =>
            new RuntimeRunAdmissionClient(http, options, TimeProvider.System).ReadAsync(context, Selection(), default));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("cache")]
    [InlineData("model")]
    [InlineData("connection")]
    [InlineData("selection")]
    [InlineData("unpriced")]
    public async Task ChangedUnavailableOrCacheableOwnerReceiptDeniesAdmission(string fault)
    {
        var selection = Selection();
        var receipt = CoordinatorRunAdmissionTests.Receipt(selection);
        receipt = fault switch
        {
            "model" => receipt with { ModelBindingPin = receipt.ModelBindingPin with { ModelId = "unknown" } },
            "connection" => receipt with
            {
                ModelSelection = receipt.ModelSelection with { ConnectionId = Guid.NewGuid() }
            },
            "selection" => receipt with { AcceptedSelectionHash = new string('a', 64) },
            "unpriced" => receipt with { CostBinding = null },
            _ => receipt
        };
        var handler = new Handler(request =>
        {
            var response = Response(request, receipt);
            if (fault == "redirect")
                response.StatusCode = HttpStatusCode.Redirect;
            if (fault == "cache")
                response.Headers.CacheControl = null;
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<CoordinationException>(() =>
            new RuntimeRunAdmissionClient(http, Options, TimeProvider.System)
                .ReadAsync(Context(), selection, default));
        Assert.Equal(1, handler.Calls);
    }

    private static AuthorizedRunSelection Selection() => new(
        new("project", "run", 1, 1, 1, "context-v1", CoordinatorRunAdmissionTests.Selection("project", "run")),
        new(1, Options.Issuer, "33333333-3333-3333-3333-333333333333", "tenant-1", 1,
            "project", "run", [new("project", "project", [new("acceptRunSelection", 1)])]));

    private static HttpContext Context(bool expired = false, string? audience = null, string runId = "run")
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer test-bearer";
        context.Request.Headers["X-Agentweaver-Tenant"] = "tenant-1";
        context.User = new(new ClaimsIdentity(
        [
            new("sub", "33333333-3333-3333-3333-333333333333"), new("iss", Options.Issuer),
            new("aud", audience ?? Options.EventsAudience), new("scope", "api.read projects.orchestrator"),
            new("project_id", "project"), new("run_id", runId)
        ], "test"));
        context.RequestServices = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(new Authentication(expired))
            .BuildServiceProvider();
        return context;
    }

    private static HttpResponseMessage Response(HttpRequestMessage request, RuntimeRunAdmissionReceipt receipt)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request, Content = JsonContent.Create(receipt, options: JsonOptions)
        };
        response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return response;
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return respond(request);
        }
    }

    private sealed class Authentication(bool expired) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                context.User, new AuthenticationProperties
                {
                    ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 5)
                }, "test")));
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotSupportedException();
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotSupportedException();
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) =>
            throw new NotSupportedException();
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotSupportedException();
    }
}
