using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;

namespace Agentweaver.AgentRuntime;

public static class AgentRuntimeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the agent runtime services. Callers must separately register an
    /// <see cref="Agentweaver.Domain.ISandboxPolicyStore"/> implementation (e.g. the
    /// API's SQLite-backed store) so runners can load per-project sandbox policies.
    /// </summary>
    public static IServiceCollection AddAgentRuntime(this IServiceCollection services)
    {
        // Sandbox executor (singleton — platform probe runs once at startup)
        services.AddSingleton<ISandboxExecutor>(sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger(nameof(SandboxExecutorFactory));
            return SandboxExecutorFactory.Create(logger);
        });

        services.AddSingleton<GitHubCopilotClientFactory>();
        services.AddSingleton<GitHubCopilotAgentRunner>();
        services.AddSingleton<FoundryClientFactory>();
        services.AddSingleton<FoundryAgentRunner>();
        services.AddSingleton<IAgentRunner, AgentRunnerDispatcher>();
        services.AddSingleton<IShellApprovalStore, InMemoryShellApprovalStore>();
        services.AddSingleton<IToolApprovalGate, InMemoryToolApprovalGate>();
        services.AddSingleton<Workflow.IWorkflowAgentFactory, Workflow.WorkflowAgentFactory>();
        return services;
    }
}

