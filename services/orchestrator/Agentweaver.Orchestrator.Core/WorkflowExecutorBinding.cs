using System.Collections.Immutable;

namespace Agentweaver.Orchestrator.Core;

/// <summary>Marker contract for a registered workflow-step executor.</summary>
public interface IWorkflowStepExecutor;

/// <summary>Executor contract for prescribed fixed work.</summary>
public interface IFixedWorkflowStepExecutor : IWorkflowStepExecutor;

/// <summary>Executor contract for coordinator-proposed open work.</summary>
public interface IOpenWorkflowStepExecutor : IWorkflowStepExecutor;

/// <summary>Executor contract for one platform-owned workflow gate.</summary>
public interface IPlatformWorkflowStepExecutor : IWorkflowStepExecutor
{
    WorkflowPlatformGate Gate { get; }
}

/// <summary>Type key used to register fixed, open, and platform-gate executors.</summary>
public readonly record struct WorkflowStepExecutorType
{
    private WorkflowStepExecutorType(WorkflowStepMode mode, WorkflowPlatformGate? platformGate) =>
        (Mode, PlatformGate) = (mode, platformGate);

    public WorkflowStepMode Mode { get; }
    public WorkflowPlatformGate? PlatformGate { get; }

    /// <summary>Executor type for prescribed fixed work.</summary>
    public static WorkflowStepExecutorType Fixed { get; } =
        new(WorkflowStepMode.Fixed, null);

    /// <summary>Executor type for coordinator-proposed open work.</summary>
    public static WorkflowStepExecutorType Open { get; } =
        new(WorkflowStepMode.Open, null);

    /// <summary>Creates the executor type for a specific platform-owned gate.</summary>
    public static WorkflowStepExecutorType ForPlatformGate(WorkflowPlatformGate gate)
    {
        // Include the gate in the key so one platform executor cannot satisfy a different gate.
        if (!Enum.IsDefined(gate))
            throw new ArgumentOutOfRangeException(nameof(gate), gate, "The platform gate is not supported.");
        return new WorkflowStepExecutorType(WorkflowStepMode.Platform, gate);
    }

    internal bool IsValid =>
        Mode switch
        {
            WorkflowStepMode.Fixed or WorkflowStepMode.Open => PlatformGate is null,
            WorkflowStepMode.Platform => PlatformGate is { } gate && Enum.IsDefined(gate),
            _ => false
        };

    public override string ToString() =>
        Mode == WorkflowStepMode.Platform
            ? $"platform:{PlatformGate}"
            : Mode.ToString().ToLowerInvariant();
}

/// <summary>Fail-closed error raised when an accepted catalog snapshot cannot be bound to executors.</summary>
public sealed class WorkflowExecutorBindingException(string message, string stepId)
    : Exception(message)
{
    public string StepId { get; } = stepId;
}

/// <summary>Immutable registry of executor implementations, keyed by workflow-step executor type.</summary>
public sealed class WorkflowStepExecutorRegistry
{
    private readonly ImmutableDictionary<WorkflowStepExecutorType, IWorkflowStepExecutor> _executors;

    /// <summary>Creates a registry; missing executor types are reported when a snapshot needs them.</summary>
    public WorkflowStepExecutorRegistry(
        IEnumerable<KeyValuePair<WorkflowStepExecutorType, IWorkflowStepExecutor>> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var builder = ImmutableDictionary.CreateBuilder<WorkflowStepExecutorType, IWorkflowStepExecutor>();
        foreach (var (type, executor) in registrations)
        {
            ArgumentNullException.ThrowIfNull(executor);
            if (!type.IsValid)
                throw new ArgumentException($"Executor type '{type}' is not supported.", nameof(registrations));
            // Reject mismatched contracts at registration time instead of during workflow execution.
            ValidateExecutorType(type, executor, nameof(registrations));
            if (!builder.TryAdd(type, executor))
                throw new ArgumentException($"Executor type '{type}' is registered more than once.", nameof(registrations));
        }
        _executors = builder.ToImmutable();
    }

    internal IWorkflowStepExecutor Resolve(WorkflowStepExecutorType type, string stepId)
    {
        if (_executors.TryGetValue(type, out var executor))
            return executor;

        // Never substitute another step mode or platform gate when a required binding is absent.
        var description = type.Mode == WorkflowStepMode.Platform
            ? $"platform gate '{type.PlatformGate}'"
            : $"'{type}'";
        throw new WorkflowExecutorBindingException(
            $"Cannot bind workflow step '{stepId}': no executor is registered for {description}.",
            stepId);
    }

    private static void ValidateExecutorType(
        WorkflowStepExecutorType type,
        IWorkflowStepExecutor executor,
        string parameterName)
    {
        // Platform registrations must name the exact gate; fixed and open registrations use separate contracts.
        var isCompatible = type.Mode switch
        {
            WorkflowStepMode.Fixed => executor is IFixedWorkflowStepExecutor,
            WorkflowStepMode.Open => executor is IOpenWorkflowStepExecutor,
            WorkflowStepMode.Platform =>
                executor is IPlatformWorkflowStepExecutor platformExecutor &&
                platformExecutor.Gate == type.PlatformGate,
            _ => false
        };
        if (!isCompatible)
            throw new ArgumentException(
                $"Executor registered as '{type}' does not implement the matching executor contract.",
                parameterName);
    }
}

/// <summary>A workflow step bound to exactly one executor category.</summary>
public abstract class BoundWorkflowStep
{
    internal BoundWorkflowStep(WorkflowStepDefinition definition) => Definition = definition;

    public WorkflowStepDefinition Definition { get; }

    /// <summary>Prescribed fixed work bound to its fixed-work executor, never to model-proposed work.</summary>
    public sealed class Fixed : BoundWorkflowStep
    {
        internal Fixed(WorkflowStepDefinition definition, IFixedWorkflowStepExecutor executor)
            : base(definition) =>
            Executor = executor;

        public IFixedWorkflowStepExecutor Executor { get; }
    }

    /// <summary>Coordinator-proposed open work bound to its open-work executor.</summary>
    public sealed class Open : BoundWorkflowStep
    {
        internal Open(WorkflowStepDefinition definition, IOpenWorkflowStepExecutor executor)
            : base(definition) =>
            Executor = executor;

        public IOpenWorkflowStepExecutor Executor { get; }
    }

    /// <summary>A platform-owned gate bound to the executor registered for that exact gate.</summary>
    public sealed class Platform : BoundWorkflowStep
    {
        internal Platform(
            WorkflowStepDefinition definition,
            WorkflowPlatformGate gate,
            IPlatformWorkflowStepExecutor executor)
            : base(definition) =>
            (Gate, Executor) = (gate, executor);

        public WorkflowPlatformGate Gate { get; }
        public IPlatformWorkflowStepExecutor Executor { get; }
    }
}

/// <summary>Accepted workflow definition and the executor binding for each catalog step.</summary>
public sealed class BoundWorkflowDefinition
{
    internal BoundWorkflowDefinition(
        WorkflowDefinitionSnapshot snapshot,
        ImmutableArray<BoundWorkflowStep> steps) =>
        (Snapshot, Steps) = (snapshot, steps);

    public WorkflowDefinitionSnapshot Snapshot { get; }
    public ImmutableArray<BoundWorkflowStep> Steps { get; }
}

/// <summary>Binds accepted workflow catalog snapshots to registered step executors without dispatching work.</summary>
public static class WorkflowDefinitionExecutorBinder
{
    /// <summary>Resolves every step in the accepted snapshot or fails without returning a partial binding.</summary>
    public static BoundWorkflowDefinition Bind(
        WorkflowDefinitionSnapshot snapshot,
        WorkflowStepExecutorRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(registry);

        var steps = ImmutableArray.CreateBuilder<BoundWorkflowStep>(snapshot.Definition.Steps.Length);
        foreach (var step in snapshot.Definition.Steps)
        {
            // Resolve from the accepted step's type and gate, never from model-proposed task content.
            switch (step.Mode)
            {
                case WorkflowStepMode.Fixed:
                    steps.Add(new BoundWorkflowStep.Fixed(
                        step,
                        (IFixedWorkflowStepExecutor)registry.Resolve(
                            WorkflowStepExecutorType.Fixed,
                            step.Id)));
                    break;
                case WorkflowStepMode.Open:
                    steps.Add(new BoundWorkflowStep.Open(
                        step,
                        (IOpenWorkflowStepExecutor)registry.Resolve(
                            WorkflowStepExecutorType.Open,
                            step.Id)));
                    break;
                case WorkflowStepMode.Platform:
                    if (step.PlatformGate is not { } gate)
                        throw new WorkflowExecutorBindingException(
                            $"Cannot bind platform step '{step.Id}': no platform gate is declared.",
                            step.Id);
                    if (!Enum.IsDefined(gate))
                        throw new WorkflowExecutorBindingException(
                            $"Cannot bind platform step '{step.Id}': platform gate value '{(int)gate}' is not supported.",
                            step.Id);
                    steps.Add(new BoundWorkflowStep.Platform(
                        step,
                        gate,
                        (IPlatformWorkflowStepExecutor)registry.Resolve(
                            WorkflowStepExecutorType.ForPlatformGate(gate),
                            step.Id)));
                    break;
                default:
                    throw new WorkflowExecutorBindingException(
                        $"Cannot bind workflow step '{step.Id}': mode value '{(int)step.Mode}' is not supported.",
                        step.Id);
            }
        }

        // Publish only after every step resolves so callers never observe a partially bound catalog.
        return new BoundWorkflowDefinition(snapshot, steps.ToImmutable());
    }
}
