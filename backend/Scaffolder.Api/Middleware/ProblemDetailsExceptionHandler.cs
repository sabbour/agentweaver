using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Scaffolder.Api.Middleware;

/// <summary>
/// Maps common exception types to appropriate HTTP status codes using RFC 7807 ProblemDetails.
/// </summary>
internal static class ProblemDetailsExceptionHandler
{
    /// <summary>
    /// Maps KeyNotFoundException to 404, InvalidOperationException to 409,
    /// and ArgumentException to 400. All other exceptions return 500.
    /// </summary>
    internal static void Configure(IApplicationBuilder app)
    {
        app.Run(async context =>
        {
            var exceptionFeature = context.Features.Get<IExceptionHandlerFeature>();
            if (exceptionFeature is null) return;

            var exception = exceptionFeature.Error;
            var problemDetails = new ProblemDetails();

            switch (exception)
            {
                case KeyNotFoundException:
                    problemDetails.Status = StatusCodes.Status404NotFound;
                    problemDetails.Title = "Resource not found";
                    problemDetails.Detail = exception.Message;
                    break;
                case InvalidOperationException:
                    problemDetails.Status = StatusCodes.Status409Conflict;
                    problemDetails.Title = "Operation conflict";
                    problemDetails.Detail = exception.Message;
                    break;
                case ArgumentException:
                    problemDetails.Status = StatusCodes.Status400BadRequest;
                    problemDetails.Title = "Invalid request";
                    problemDetails.Detail = exception.Message;
                    break;
                default:
                    problemDetails.Status = StatusCodes.Status500InternalServerError;
                    problemDetails.Title = "An unexpected error occurred";
                    break;
            }

            context.Response.StatusCode = problemDetails.Status ?? 500;
            await context.Response.WriteAsJsonAsync(problemDetails);
        });
    }
}
