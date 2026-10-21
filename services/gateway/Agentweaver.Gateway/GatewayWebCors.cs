using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Gateway;

/// <summary>Creates per-route browser policies limited to the configured Web origin and required headers.</summary>
public static class GatewayWebCors
{
    public const string OpenApiPolicyName = "GatewayWebOpenApi";
    public const string CopilotConnectionsPolicyName = "GatewayWebCopilotConnections";

    /// <summary>Returns the route-specific CORS policy key used when mapping a catalog route.</summary>
    public static string PolicyName(GatewayRoute route) => $"GatewayWeb:{route.OperationId}";

    /// <summary>Registers narrow CORS policies for OpenAPI, connection flows, and catalogued API operations.</summary>
    public static IServiceCollection AddGatewayWebCors(
        this IServiceCollection services,
        GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        var origin = options.WebOrigin.GetLeftPart(UriPartial.Authority);
        services.AddCors(cors =>
        {
            cors.AddPolicy(OpenApiPolicyName, policy => policy
                .WithOrigins(origin)
                .WithMethods("GET")
                .WithHeaders("Accept"));
            cors.AddPolicy(CopilotConnectionsPolicyName, policy => policy
                .WithOrigins(origin)
                .WithMethods("GET", "POST")
                .WithHeaders("Authorization", "Accept", "Content-Type", "X-Agentweaver-Tenant")
                .AllowCredentials());
            foreach (var route in GatewayRouteCatalog.Routes)
            {
                var headers = new List<string> { "Authorization", "Accept" };
                if (route.HasJsonBody) headers.Add("Content-Type");
                if (route.ForwardTenantSelector) headers.Add("X-Agentweaver-Tenant");
                if (RequiresIdempotencyKey(route.OperationId)) headers.Add("Idempotency-Key");

                cors.AddPolicy(PolicyName(route), policy =>
                {
                    policy
                        .WithOrigins(origin)
                        .WithMethods(route.Method)
                        .WithHeaders(headers.ToArray());
                    if (!route.ExposedResponseHeaders.IsDefaultOrEmpty)
                        policy.WithExposedHeaders(route.ExposedResponseHeaders.ToArray());
                });
            }
        });

        return services;
    }

    /// <summary>Lists owner writes whose retries require an idempotency key header.</summary>
    private static bool RequiresIdempotencyKey(string operationId) => operationId is
        "createProjectRun" or
        "cancelProjectRun" or
        "createKnowledgeRecord" or
        "updateKnowledgeRecord" or
        "restoreKnowledgeRecord" or
        "approveKnowledgeDecision" or
        "importKnowledgeRecords" or
        "promoteKnowledgeProposal" or
        "rejectKnowledgeProposal";
}
