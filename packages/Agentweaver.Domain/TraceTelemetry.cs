namespace Agentweaver.Domain;

/// <summary>
/// Bounded, public-safe trace dimensions emitted by Agentweaver activities.
/// The contract deliberately contains only identifiers, enum-like operational state, booleans,
/// and counters. Prompts, credentials, raw tool arguments/results, and arbitrary payload data
/// must never be added here.
/// </summary>
public static class TraceTelemetry
{
    // OpenTelemetry GenAI semantic conventions.
    public const string AgentName = "gen_ai.agent.name";
    public const string OperationName = "gen_ai.operation.name";
    public const string RequestModel = "gen_ai.request.model";
    public const string ResponseModel = "gen_ai.response.model";
    public const string InputTokens = "gen_ai.usage.input_tokens";
    public const string OutputTokens = "gen_ai.usage.output_tokens";
    public const string TotalTokens = "gen_ai.usage.total_tokens";
    public const string ToolName = "gen_ai.tool.name";
    public const string ToolCallId = "tool.call.id";
    public const string ToolSuccess = "gen_ai.tool.call.success";

    // Agentweaver trace contract.
    public const string SpanKind = "agentweaver.span.kind";
    public const string RunId = "run.id";
    public const string LegacyRunId = "run_id";
    public const string ProjectId = "project.id";
    public const string SessionId = "agentweaver.session.id";
    public const string WorkflowRunId = "agentweaver.workflow.run.id";
    public const string ProviderSource = "agentweaver.provider.source";
    public const string ProviderKind = "agentweaver.provider.kind";
    public const string ProviderType = "agentweaver.provider.type";
    public const string ProviderScope = "agentweaver.provider.scope";
    public const string RoutingDecision = "agentweaver.routing.decision";
    public const string PolicyDecision = "agentweaver.policy.decision";
    public const string AuthorizationDecision = "agentweaver.authorization.decision";
    public const string PolicyShellEnabled = "agentweaver.policy.shell.enabled";
    public const string PolicyNetworkEnabled = "agentweaver.policy.network.enabled";
    public const string PolicyAutoApproveTools = "agentweaver.policy.auto_approve_tools";
    public const string SandboxBackend = "agentweaver.sandbox.backend";
    public const string SandboxIsolated = "agentweaver.sandbox.isolated";
    public const string RuntimePurpose = "agentweaver.runtime.purpose";
    public const string Status = "agentweaver.status";
    public const string ErrorType = "error.type";
    public const string NanoAiu = "agentweaver.aiu.nano";
    public const string ProcessStartedAt = "agentweaver.execution.process.started_at";
    public const string ProcessEndedAt = "agentweaver.execution.process.ended_at";
    public const string HostProcessCpuMs = "agentweaver.execution.host_process.cpu_ms";
    public const string HostProcessWorkingSetBytes = "agentweaver.execution.host_process.working_set_bytes";
    public const string HostProcessPeakWorkingSetBytes = "agentweaver.execution.host_process.peak_working_set_bytes";

    public const string DecisionAllowed = "allowed";
    public const string DecisionDenied = "denied";
    public const string DecisionApprovalRequired = "approval_required";
    public const string DecisionApproved = "approved";
    public const string DecisionAutoApproved = "auto_approved";
    public const string DecisionEvaluationError = "evaluation_error";

    public static string PurposeValue(AgentHostPurpose purpose) => purpose switch
    {
        AgentHostPurpose.AssemblyBuildTest => "assembly_build_test",
        AgentHostPurpose.ImplementationTurn => "implementation_turn",
        AgentHostPurpose.OperatorAssistant => "operator_assistant",
        _ => "default",
    };
}
