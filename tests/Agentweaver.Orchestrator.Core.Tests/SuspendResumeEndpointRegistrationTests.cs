using System.Net;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Npgsql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class SuspendResumeEndpointRegistrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentSessionRegistrationReadIsAuthenticatedAndOnlyMappedWhenConfigured(bool configured)
    {
        var builder = WebApplication.CreateBuilder();
        var options = new OrchestratorOptions(
            "https://issuer.test/", "orchestrator-api", "https://projects.test/",
            "projects-api", "https://events.test/", "events-api");
        builder.Services.AddAuthorization();
        if (configured)
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Orchestrator:RuntimeRegistration:EnvironmentOwnerAddress"] = "https://environment.test/"
            });
        var enabled = builder.Services.AddRuntimeRegistrationOwner(builder.Configuration, options);
        await using var app = builder.Build();
        if (enabled)
            app.MapRuntimeRegistrationEndpoints();
        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Where(endpoint =>
                endpoint.RoutePattern.RawText ==
                "/internal/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/runtime-registrations" &&
                endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains("GET")).ToArray();

        Assert.Equal(configured ? 1 : 0, routes.Length);
        if (configured)
            Assert.NotEmpty(Assert.Single(routes).Metadata.GetOrderedMetadata<IAuthorizeData>());
    }

    [Fact]
    public async Task FixedCreditGuardReadRouteIsAuthenticatedWhenUsageSourceIsConfigured()
    {
        var builder = WebApplication.CreateBuilder();
        var options = new OrchestratorOptions(
            "https://issuer.test/", "orchestrator-api", "https://projects.test/",
            "projects-api", "https://events.test/", "events-api");
        builder.Services.AddAuthorization();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Orchestrator:RuntimeRegistration:EnvironmentOwnerAddress"] = "https://environment.test/",
            ["Orchestrator:RuntimeUsage:BrokerOwnerAddress"] = "https://broker.test/"
        });
        Assert.True(builder.Services.AddRuntimeRegistrationOwner(builder.Configuration, options));
        Assert.True(builder.Services.AddRuntimeUsageSource(builder.Configuration));

        await using var app = builder.Build();
        app.MapRuntimeUsageSourceEndpoints();
        var route = Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>(), endpoint => endpoint.RoutePattern.RawText ==
                    "/internal/runtime/turns/fixed-credit-denials/read" &&
                endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains("POST"));

        Assert.NotEmpty(route.Metadata.GetOrderedMetadata<IAuthorizeData>());
    }

    [Fact]
    public void ConfiguredRuntimeOwnerResolvesReadOnlyEgressVerificationClient()
    {
        var options = new OrchestratorOptions(
            "https://issuer.test/", "orchestrator-api", "https://projects.test/",
            "projects-api", "https://events.test/", "events-api");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Orchestrator:RuntimeRegistration:EnvironmentOwnerAddress"] = "https://environment.test/"
            }).Build();
        var services = new ServiceCollection();
        Assert.True(services.AddRuntimeRegistrationOwner(configuration, options));
        // Avoid opening the production backend-CA handler while this test checks only service composition.
        services.Configure<HttpClientFactoryOptions>(
            "runtime-suspend-environment", registration => registration.HttpMessageHandlerBuilderActions.Clear());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<RuntimeEgressVerificationHttpClient>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalRuntimeOwnerControlsOnlyTheInternalSuspendRoute(bool configured)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var options = new OrchestratorOptions(
            "https://issuer.test/", "orchestrator-api", "https://projects.test/",
            "projects-api", "https://events.test/", "events-api");
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ProjectsRunSelectionClient>(_ =>
            throw new InvalidOperationException("Endpoint metadata must not resolve Projects services."));
        builder.Services.AddSingleton(new SessionSuspendResumeCoordinator());
        if (configured)
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Orchestrator:RuntimeRegistration:EnvironmentOwnerAddress"] = "https://environment.test/"
            });
        var enabled = builder.Services.AddRuntimeRegistrationOwner(builder.Configuration, options);
        Assert.Equal(configured, enabled);
        await using var app = builder.Build();
        app.MapSuspendResumeEndpoints();
        if (enabled)
            app.MapRuntimeSuspendResumeEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().ToArray();
        var publicRoutes = endpoints.Where(endpoint =>
            endpoint.RoutePattern.RawText!.StartsWith("/api/projects/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, publicRoutes.Length);
        Assert.All(publicRoutes, endpoint =>
        {
            Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.NotNull(endpoint.Metadata.GetMetadata<IAcceptsMetadata>());
        });
        const string runtimePath = "/internal/runtime/suspend/require-current";
        Assert.Equal(configured, endpoints.Any(endpoint => endpoint.RoutePattern.RawText == runtimePath));

        await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (!configured)
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var response = await client.PostAsync(runtimePath,
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task RuntimeActionOwnerResolvesWithoutUsageSourceAndKeepsGuardChecks()
    {
        var options = new OrchestratorOptions(
            "https://issuer.test/", "orchestrator-api", "https://projects.test/",
            "projects-api", "https://events.test/", "events-api");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Orchestrator:RuntimeRegistration:EnvironmentOwnerAddress"] = "https://environment.test/"
            }).Build();
        var services = new ServiceCollection();

        using var dataSource = new NpgsqlDataSourceBuilder(
            "Host=localhost;Database=agentweaver_test;Username=test;Password=test").Build();
        var providerCatalog = ProviderCatalog.Create([], [], []).Value
            ?? throw new InvalidOperationException("An empty provider catalog could not be created.");
        services.AddSingleton(dataSource);
        services.AddSingleton(options);
        services.AddSingleton(providerCatalog);
        services.AddSingleton<ProviderResolver>();
        services.AddSingleton<AgtPolicyProvider>();
        services.AddHttpContextAccessor();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(serviceProvider => new CoordinatorRunSelectionContextStore(
            serviceProvider.GetRequiredService<NpgsqlDataSource>(), options.Schema,
            serviceProvider.GetRequiredService<ProviderCatalog>(),
            serviceProvider.GetRequiredService<ProviderResolver>(),
            serviceProvider.GetServices<ICoordinatorSandboxResourceProvider>()));
        services.AddSingleton(serviceProvider => new CoordinatorDecisionOwnerStore(
            serviceProvider.GetRequiredService<NpgsqlDataSource>(), options.Schema,
            serviceProvider.GetRequiredService<CoordinatorRunSelectionContextStore>(),
            serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(serviceProvider => new CoordinationOwnerStore(
            serviceProvider.GetRequiredService<NpgsqlDataSource>(), options.Schema,
            serviceProvider.GetRequiredService<TimeProvider>(),
            serviceProvider.GetRequiredService<CoordinatorDecisionOwnerStore>()));
        services.AddSingleton(serviceProvider => new PostgresMafCheckpointStore(
            serviceProvider.GetRequiredService<NpgsqlDataSource>(), options.Schema,
            serviceProvider.GetService<IObjectStore>()));
        services.AddSingleton(serviceProvider => new ReviewedRemoteToolSnapshotStore(
            serviceProvider.GetRequiredService<NpgsqlDataSource>(), options.Schema));
        services.AddSingleton<IReviewedRemoteToolSnapshotResolver>(serviceProvider =>
            serviceProvider.GetRequiredService<ReviewedRemoteToolSnapshotStore>());
        services.AddHttpClient<ProjectsRunSelectionClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient<EventsAddressedMessageClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddTransient<IExecutableActionPolicyEvaluationJournal>(serviceProvider =>
            serviceProvider.GetRequiredService<EventsAddressedMessageClient>());
        services.AddSingleton<ExecutableActionGrantOwnerStore>();
        services.AddSingleton<IExecutableActionGrantOwnerLookup>(serviceProvider =>
            serviceProvider.GetRequiredService<ExecutableActionGrantOwnerStore>());
        services.AddSingleton<IExecutableActionSourceReceiptWriter>(serviceProvider =>
            serviceProvider.GetRequiredService<ExecutableActionGrantOwnerStore>());
        services.AddSingleton<IExecutableActionPolicyEvaluationReceiptWriter>(serviceProvider =>
            serviceProvider.GetRequiredService<ExecutableActionGrantOwnerStore>());
        services.AddScoped(serviceProvider => new ExecutableActionGuard(
            serviceProvider.GetService<AgtPolicyProvider>(),
            serviceProvider.GetService<AgtPolicyProviderOptions>(),
            serviceProvider.GetService<IExecutableActionPolicyEvaluationJournal>(),
            serviceProvider.GetService<IExecutableActionGrantOwnerLookup>(),
            serviceProvider.GetRequiredService<TimeProvider>(),
            serviceProvider.GetService<IExecutableActionSourceReceiptWriter>(),
            serviceProvider.GetService<IExecutableActionPolicyEvaluationReceiptWriter>()));
        Assert.True(services.AddRuntimeRegistrationOwner(configuration, options));
        Assert.False(services.AddRuntimeUsageSource(configuration));
        ConfigureTestOwnerHttpClient(services, nameof(ProjectsRunSelectionClient));
        ConfigureTestOwnerHttpClient(services, nameof(EventsAddressedMessageClient));
        ConfigureTestOwnerHttpClient(services, nameof(RuntimeEnvironmentContextClient));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(RuntimeActionOwner));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(RuntimeUsageSourceStore));
        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var owner = scope.ServiceProvider.GetRequiredService<RuntimeActionOwner>();
        var registration = TestRegistration(null);
        var ordinaryAction = TestAction(registration, "tool.read");

        await owner.RequireCostTurnEnabledAsync(
            new DefaultHttpContext(), registration, ordinaryAction,
            RuntimeFixedCreditGuardStage.ActionAuthorize, CancellationToken.None);

        var fixedZeroRegistration = TestRegistration(0m);
        var modelTurn = TestAction(fixedZeroRegistration, "model.turn");
        var denied = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            owner.RequireCostTurnEnabledAsync(
                new DefaultHttpContext(), fixedZeroRegistration, modelTurn,
                RuntimeFixedCreditGuardStage.ActionAuthorize, CancellationToken.None));
        Assert.Equal("runtime_cost_budget_exhausted", denied.Code);

        var readRequest = new RuntimeFixedCreditGuardReadRequest(
            null, fixedZeroRegistration.RuntimeInstanceId, fixedZeroRegistration.Revision,
            fixedZeroRegistration.Binding.ExecutionFence, RuntimeFixedCreditGuardStage.HostPreQueue,
            modelTurn.EventId, RuntimeActionContract.Hash(modelTurn));
        var unavailable = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            owner.ReadFixedCreditGuardAsync(
                new DefaultHttpContext(), readRequest, CancellationToken.None));
        Assert.Equal("runtime_fixed_credit_guard_unavailable", unavailable.Code);
    }

    private static RuntimeRegistration TestRegistration(decimal? hardCreditLimit)
    {
        var binding = new RuntimeBinding(
            "https://issuer.test/", "actor", "tenant", "project", "run", "session", "agent", "turn",
            1, 1, 1, "context", new string('a', 64), 1, "environment", "placement", 1,
            "profile", new("https://runtime.test/configure"), new("https://runtime.test/observe"))
        {
            ModelSourceMode = ModelSourceMode.HostedCopilot,
            CopilotHardCreditLimit = hardCreditLimit
        };
        return new(Guid.NewGuid(), 1, binding, RuntimeRegistrationState.Active, DateTimeOffset.UtcNow.AddMinutes(5));
    }

    private static RuntimeActionRequest TestAction(RuntimeRegistration registration, string actionId) =>
        new(1, registration.RuntimeInstanceId, registration.Revision,
            registration.Binding.ExecutionFence, Guid.NewGuid(), actionId, new string('b', 64));

    private static void ConfigureTestOwnerHttpClient(IServiceCollection services, string clientName) =>
        services.Configure<HttpClientFactoryOptions>(
            clientName, options => options.HttpMessageHandlerBuilderActions.Clear());
}
