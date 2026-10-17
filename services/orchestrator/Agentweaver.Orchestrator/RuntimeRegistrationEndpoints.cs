using Agentweaver.Identity;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Registers runtime-owner services and exposes authenticated runtime registration and skill endpoints.
/// </summary>
public static class RuntimeRegistrationEndpoints
{
    /// <summary>
    /// Registers Environment-backed runtime registration services when an owner address is configured.
    /// </summary>
    /// <param name="services">The service collection receiving owner stores, clients, and runtime services.</param>
    /// <param name="configuration">Configuration containing the Environment owner address.</param>
    /// <param name="options">Orchestrator identity and database settings.</param>
    /// <returns><see langword="true"/> when the runtime registration owner is enabled.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the configured owner address is not a valid absolute HTTPS address.</exception>
    public static bool AddRuntimeRegistrationOwner(
        this IServiceCollection services, IConfiguration configuration, OrchestratorOptions options)
    {
        var address = configuration["Orchestrator:RuntimeRegistration:EnvironmentOwnerAddress"];
        if (address is null)
            return false;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("The Environment runtime owner address must be an absolute HTTPS URI.");
        services.AddSingleton(new RuntimeRegistrationOwnerOptions(RuntimeOwnerHttpTransport.RequireOwnerAddress(uri)));
        services.AddSingleton(services => new RuntimeRegistrationStore(
            services.GetRequiredService<Npgsql.NpgsqlDataSource>(), options.Schema,
            services.GetRequiredService<TimeProvider>()));
        services.AddHttpClient<RuntimeEnvironmentContextClient>(client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateHandler);
        services.AddHttpClient("runtime-suspend-environment", client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateHandler);
        services.AddHttpClient("runtime-suspend-host", client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateHandler);
        services.AddScoped(serviceProvider => new RuntimeWorkspaceSuspendHttpClient(
            serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("runtime-suspend-environment"),
            serviceProvider.GetRequiredService<RuntimeRegistrationOwnerOptions>().EnvironmentOwnerAddress,
            serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddScoped(serviceProvider => new RuntimeWorkspaceResumeHttpClient(
            serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("runtime-suspend-environment"),
            serviceProvider.GetRequiredService<RuntimeRegistrationOwnerOptions>().EnvironmentOwnerAddress));
        services.AddScoped(serviceProvider => new RuntimeResumeBootstrapHttpClient(
            serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("runtime-suspend-environment"),
            serviceProvider.GetRequiredService<RuntimeRegistrationOwnerOptions>().EnvironmentOwnerAddress));
        services.AddScoped<RuntimeRegistrationOwner>();
        services.AddScoped<RuntimeSkillContentOwner>();
        services.AddScoped<RuntimeActionOwner>();
        return true;
    }

    /// <summary>
    /// Maps protected registration, skill, and runtime action routes.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same route builder after endpoint registration.</returns>
    public static IEndpointRouteBuilder MapRuntimeRegistrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
            "/internal/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/runtime-registrations",
            async (string projectId, string runId, string sessionId, RegisterRuntimeRequest request,
                HttpContext context, [Microsoft.AspNetCore.Mvc.FromServices] RuntimeRegistrationOwner owner,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(context, () => owner.RegisterAsync(
                    context, projectId, runId, sessionId, request, cancellationToken)).ConfigureAwait(false))
            .RequireAuthorization();
        endpoints.MapGet(
            "/internal/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/runtime-registrations",
            async (string projectId, string runId, string sessionId, HttpContext context,
                [Microsoft.AspNetCore.Mvc.FromServices] RuntimeRegistrationOwner owner,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(context, () => owner.ReadCurrentForSessionAsync(
                    context, projectId, runId, sessionId, cancellationToken)).ConfigureAwait(false))
            .RequireAuthorization();
        endpoints.MapGet("/internal/runtime/registrations/{runtimeInstanceId:guid}",
            async (Guid runtimeInstanceId, HttpContext context,
                [Microsoft.AspNetCore.Mvc.FromServices] RuntimeRegistrationOwner owner,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(context, () => owner.ReadCurrentAsync(
                    context, runtimeInstanceId, cancellationToken)).ConfigureAwait(false))
            .RequireAuthorization();
        endpoints.MapGet("/internal/runtime/registrations/{runtimeInstanceId:guid}/skills",
            async (Guid runtimeInstanceId, HttpContext context,
                [Microsoft.AspNetCore.Mvc.FromServices] RuntimeSkillContentOwner owner,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(context, () => owner.ReadCurrentAsync(
                    context, runtimeInstanceId, cancellationToken)).ConfigureAwait(false))
            .RequireAuthorization();
        endpoints.MapRuntimeActionEndpoints();
        return endpoints;
    }

    /// <summary>
    /// Converts owner results and expected failures into no-store HTTP responses.
    /// </summary>
    /// <typeparam name="T">The successful response type.</typeparam>
    /// <param name="context">The current request context used to set response headers.</param>
    /// <param name="action">The owner operation to execute.</param>
    /// <returns>An OK response or a structured error response for known failures.</returns>
    private static async Task<IResult> ExecuteAsync<T>(HttpContext context, Func<Task<T>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            return Results.Ok(await action().ConfigureAwait(false));
        }
        catch (CoordinationException exception)
        {
            return Results.Json(new { error = exception.Code }, statusCode: exception.StatusCode);
        }
        catch (RuntimeAuthorizationException exception)
        {
            return Results.Json(new { error = exception.Code }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { error = "invalid_runtime_registration_request" });
        }
        catch (HttpRequestException)
        {
            return Results.Json(new { error = "runtime_owner_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            return Results.Json(new { error = "runtime_owner_timeout" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
