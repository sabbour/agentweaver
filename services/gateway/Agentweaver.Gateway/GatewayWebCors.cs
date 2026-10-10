using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Gateway;

public static class GatewayWebCors
{
    public const string OpenApiPolicyName = "GatewayWebOpenApi";
    public const string CopilotConnectionsPolicyName = "GatewayWebCopilotConnections";

    public static string PolicyName(GatewayRoute route) => $"GatewayWeb:{route.OperationId}";

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

                cors.AddPolicy(PolicyName(route), policy => policy
                    .WithOrigins(origin)
                    .WithMethods(route.Method)
                    .WithHeaders(headers.ToArray()));
            }
        });

        return services;
    }

    private static bool RequiresIdempotencyKey(string operationId) => operationId is
        "createKnowledgeRecord" or
        "updateKnowledgeRecord" or
        "restoreKnowledgeRecord" or
        "approveKnowledgeDecision" or
        "importKnowledgeRecords" or
        "promoteKnowledgeProposal" or
        "rejectKnowledgeProposal";
}
