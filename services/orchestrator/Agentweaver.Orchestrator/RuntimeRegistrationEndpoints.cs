using Agentweaver.Identity;

namespace Agentweaver.Orchestrator;

public static class RuntimeRegistrationEndpoints
{
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
        services.AddScoped<RuntimeRegistrationOwner>();
        services.AddScoped<RuntimeActionOwner>();
        return true;
    }

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
        endpoints.MapGet("/internal/runtime/registrations/{runtimeInstanceId:guid}",
            async (Guid runtimeInstanceId, HttpContext context,
                [Microsoft.AspNetCore.Mvc.FromServices] RuntimeRegistrationOwner owner,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(context, () => owner.ReadCurrentAsync(
                    context, runtimeInstanceId, cancellationToken)).ConfigureAwait(false))
            .RequireAuthorization();
        endpoints.MapRuntimeActionEndpoints();
        return endpoints;
    }

    private static async Task<IResult> ExecuteAsync(HttpContext context, Func<Task<RuntimeRegistration>> action)
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
