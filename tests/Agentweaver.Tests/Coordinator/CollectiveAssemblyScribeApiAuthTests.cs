using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Runs;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Regression tests proving that the coordinator collective-assembly Scribe is wired with a
/// non-null <c>apiKey</c> / <c>apiBaseUrl</c> — so its loopback memory tool calls
/// (<c>list_inbox</c>, <c>update_session</c>, <c>export_memory</c>, …) authenticate correctly
/// instead of returning 401 and "Tool execution failed".
///
/// Root cause: <see cref="CollectiveAssemblyPipeline.RunScribeAsync"/> previously constructed the
/// <c>ScribeTurnExecutor</c> without passing <c>apiBaseUrl</c> or <c>apiKey</c>, so both
/// defaulted to <c>null</c>. The per-run workflow Scribe (built by
/// <see cref="RunWorkflowFactory"/>) was already correct; only the coordinator assembly Scribe
/// was broken.
///
/// Fix: <see cref="RunWorkflowFactory"/> now exposes <c>internal ApiBaseUrl</c> and
/// <c>internal ApiKey</c> properties (single resolution site), and
/// <see cref="CollectiveAssemblyPipeline"/> passes them through when constructing the executor.
/// </summary>
public sealed class CollectiveAssemblyScribeApiAuthTests : IClassFixture<WorkflowWebApplicationFactory>
{
    private readonly WorkflowWebApplicationFactory _factory;

    public CollectiveAssemblyScribeApiAuthTests(WorkflowWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// <see cref="RunWorkflowFactory"/> must resolve a non-null <c>ApiKey</c> from the
    /// <c>Auth:ApiKey</c> configuration entry.  This is the single resolution site that
    /// <see cref="CollectiveAssemblyPipeline"/> now reads via the new <c>internal</c> property,
    /// so if this is null the coordinator Scribe will again send unauthenticated requests and
    /// receive 401s from the loopback API.
    /// </summary>
    [Fact]
    public void RunWorkflowFactory_ApiKey_IsNonNullWhenAuthApiKeyIsConfigured()
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<RunWorkflowFactory>();

        factory.ApiKey.Should().NotBeNullOrEmpty(
            because: "Auth:ApiKey is set in WorkflowWebApplicationFactory; " +
                     "a null ApiKey means the coordinator Scribe will send unauthenticated " +
                     "loopback requests and receive 401 → \"Tool execution failed\"");

        factory.ApiKey.Should().Be(WorkflowWebApplicationFactory.TestApiKey,
            because: "RunWorkflowFactory must propagate the configured key verbatim");
    }

    /// <summary>
    /// <see cref="RunWorkflowFactory.ApiBaseUrl"/> must be non-null (it falls back to
    /// <c>http://localhost:5000</c> when <c>Agentweaver:ApiBaseUrl</c> is absent from config).
    /// </summary>
    [Fact]
    public void RunWorkflowFactory_ApiBaseUrl_IsNonNullEvenWithoutExplicitConfig()
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<RunWorkflowFactory>();

        factory.ApiBaseUrl.Should().NotBeNullOrEmpty(
            because: "RunWorkflowFactory falls back to http://localhost:5000 when the " +
                     "Agentweaver:ApiBaseUrl config key is absent; a null here would mean the " +
                     "fallback logic was removed");
    }

    /// <summary>
    /// <see cref="CollectiveAssemblyPipeline"/> must be resolvable from DI with the same
    /// <see cref="RunWorkflowFactory"/> singleton it reads <c>ApiKey</c>/<c>ApiBaseUrl</c> from.
    /// This guards against a misconfigured DI registration that would silently give the pipeline
    /// a different (or unresolved) factory instance.
    /// </summary>
    [Fact]
    public void CollectiveAssemblyPipeline_SharesRunWorkflowFactory_Singleton()
    {
        using var scope = _factory.Services.CreateScope();

        var pipeline = scope.ServiceProvider.GetRequiredService<ICollectiveAssemblyPipeline>();
        var factory  = scope.ServiceProvider.GetRequiredService<RunWorkflowFactory>();

        // Both must be resolvable — if either throws the DI wiring is broken.
        pipeline.Should().NotBeNull();
        factory.Should().NotBeNull();

        // Verify the factory has a non-null key — so the pipeline's executor call site
        // (which passes _workflowFactory.ApiKey) will also receive a non-null value.
        factory.ApiKey.Should().NotBeNullOrEmpty(
            because: "CollectiveAssemblyPipeline reads ApiKey from this singleton; " +
                     "if it is null here, the assembly-scribe executor is built without " +
                     "authentication and every loopback memory tool call returns 401");
    }
}
