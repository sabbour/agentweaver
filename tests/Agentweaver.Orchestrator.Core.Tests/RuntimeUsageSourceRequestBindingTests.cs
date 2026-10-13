using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class RuntimeUsageSourceRequestBindingTests
{
    private static readonly JsonSerializerOptions OwnerJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Theory]
    [InlineData(false, StatusCodes.Status400BadRequest)]
    [InlineData(true, StatusCodes.Status200OK)]
    public async Task FixedCreditReadBodyBindingDependsOnRequestBodyDetectionFeature(
        bool includeBodyDetectionFeature, int expectedStatusCode)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.UnmappedMemberHandling =
                JsonUnmappedMemberHandling.Disallow;
            options.SerializerOptions.MaxDepth = 16;
            options.SerializerOptions.Converters.Add(
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });
        await using var app = builder.Build();
        var handlerInvoked = false;
        app.MapPost("/read", (RuntimeFixedCreditGuardReadRequest request) =>
        {
            handlerInvoked = true;
            return Results.Ok(request);
        });
        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>());

        var body = JsonSerializer.SerializeToUtf8Bytes(ReadRequest(), OwnerJson);
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body);
        context.Response.Body = new MemoryStream();
        context.Features.Set<IHttpRequestBodyDetectionFeature>(includeBodyDetectionFeature
            ? new RequestBodyDetectionFeature()
            : null);

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        var responseBody = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.True(context.Response.StatusCode == expectedStatusCode,
            $"Body detection feature present: {includeBodyDetectionFeature}. " +
            $"Expected {expectedStatusCode}; got {context.Response.StatusCode}. Response: {responseBody}");
        Assert.Equal(includeBodyDetectionFeature, handlerInvoked);
    }

    private static RuntimeFixedCreditGuardReadRequest ReadRequest()
    {
        var runtimeInstanceId = Guid.NewGuid();
        var proof = new RuntimeCredentialHttpRequest(
            Guid.NewGuid(), runtimeInstanceId, 1, RuntimeCredentialPurpose.Observe,
            new Uri("https://owner.example/internal/runtime/observations"),
            new string('a', 64), new string('b', 64), DateTimeOffset.UtcNow.AddMinutes(5), Guid.Empty);
        return new(proof, runtimeInstanceId, 1, 1, RuntimeFixedCreditGuardStage.HostPreQueue,
            Guid.NewGuid(), new string('c', 64));
    }

    private sealed class RequestBodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
}
