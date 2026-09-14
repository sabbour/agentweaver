using Agentweaver.Api.Auth;
using Microsoft.OpenApi;

namespace Agentweaver.Api.Endpoints;

/// <summary>
/// Declares the <c>If-Model-Provider-Key</c> precondition header on every endpoint that is guarded
/// by <see cref="AiExecutionPlanService"/>. Without this, an OpenAPI-guided client can discover the
/// route but has no way to learn how to satisfy the 409 <c>ai_execution_context_required</c> it gets
/// back — the failure mode reported in issue #1316.
/// </summary>
internal static class AiExecutionContextOpenApi
{
    /// <summary>
    /// Documents the short-lived execution key header for a guarded route.
    /// </summary>
    /// <param name="builder">The route being registered.</param>
    /// <param name="operation">
    /// The <see cref="AiOperationCatalog"/> operation name to prepare the context for. A route that
    /// picks between operations at request time names both, for example "orchestration or agent_turn".
    /// </param>
    /// <param name="required">
    /// <see langword="false"/> when the route only consults a model for some request shapes, in which
    /// case the header is required for those shapes and ignored otherwise. Describe the condition in
    /// <paramref name="condition"/>.
    /// </param>
    /// <param name="condition">
    /// A short clause describing when the header is needed, used only when <paramref name="required"/>
    /// is <see langword="false"/>.
    /// </param>
    internal static RouteHandlerBuilder RequiresAiExecutionContext(
        this RouteHandlerBuilder builder,
        string operation,
        bool required = true,
        string? condition = null)
    {
        var description = required
            ? $"Short-lived execution key returned by POST /api/ai/execution-context for the {operation} operation."
            : $"Short-lived execution key returned by POST /api/ai/execution-context for the {operation} operation. "
                + $"Required {condition}; ignored otherwise.";

        return builder.AddOpenApiOperationTransformer((op, _, _) =>
        {
            op.Parameters ??= [];
            op.Parameters.Add(new OpenApiParameter
            {
                Name = AiExecutionPlanHeaders.ProviderKey,
                In = ParameterLocation.Header,
                Required = required,
                Description = description,
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            });
            return Task.CompletedTask;
        });
    }
}
