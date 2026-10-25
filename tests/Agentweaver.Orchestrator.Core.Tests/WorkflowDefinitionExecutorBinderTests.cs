using System.Reflection;
using Agentweaver.Orchestrator.Core;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class WorkflowDefinitionExecutorBinderTests
{
    [Fact]
    public void BindResolvesFixedOpenAndPlatformExecutorsWithoutChangingTheirRoles()
    {
        var fixedExecutor = new FixedExecutor();
        var openExecutor = new OpenExecutor();
        var platformExecutor = new PlatformExecutor(WorkflowPlatformGate.IndependentReview);
        var registry = new WorkflowStepExecutorRegistry(
        [
            new(WorkflowStepExecutorType.Fixed, fixedExecutor),
            new(WorkflowStepExecutorType.Open, openExecutor),
            new(
                WorkflowStepExecutorType.ForPlatformGate(WorkflowPlatformGate.IndependentReview),
                platformExecutor)
        ]);
        var snapshot = WorkflowTestData.Snapshot();

        var bound = WorkflowDefinitionExecutorBinder.Bind(snapshot, registry);

        Assert.Same(snapshot, bound.Snapshot);
        Assert.Equal(3, bound.Steps.Length);
        var fixedStep = Assert.IsType<BoundWorkflowStep.Fixed>(bound.Steps[0]);
        Assert.Same(fixedExecutor, fixedStep.Executor);
        Assert.Same(snapshot.Definition.Steps[0].FixedWork, fixedStep.Definition.FixedWork);
        var openStep = Assert.IsType<BoundWorkflowStep.Open>(bound.Steps[1]);
        Assert.Same(openExecutor, openStep.Executor);
        Assert.Null(openStep.Definition.FixedWork);
        var platformStep = Assert.IsType<BoundWorkflowStep.Platform>(bound.Steps[2]);
        Assert.Equal(WorkflowPlatformGate.IndependentReview, platformStep.Gate);
        Assert.Same(platformExecutor, platformStep.Executor);
        Assert.Null(platformStep.Definition.FixedWork);
    }

    [Fact]
    public void BindDoesNotUseOpenExecutorForRequiredFixedWork()
    {
        var registry = new WorkflowStepExecutorRegistry(
        [
            new(WorkflowStepExecutorType.Open, new OpenExecutor())
        ]);

        var exception = Assert.Throws<WorkflowExecutorBindingException>(
            () => WorkflowDefinitionExecutorBinder.Bind(WorkflowTestData.Snapshot(), registry));

        Assert.Equal("setup", exception.StepId);
        Assert.Contains("fixed", exception.Message);
        Assert.Contains("no executor is registered", exception.Message);
    }

    [Fact]
    public void BindRequiresAnOpenExecutorForOpenSteps()
    {
        var registry = new WorkflowStepExecutorRegistry(
        [
            new(WorkflowStepExecutorType.Fixed, new FixedExecutor()),
            new(
                WorkflowStepExecutorType.ForPlatformGate(WorkflowPlatformGate.IndependentReview),
                new PlatformExecutor(WorkflowPlatformGate.IndependentReview))
        ]);

        var exception = Assert.Throws<WorkflowExecutorBindingException>(
            () => WorkflowDefinitionExecutorBinder.Bind(WorkflowTestData.Snapshot(), registry));

        Assert.Equal("implement", exception.StepId);
        Assert.Contains("open", exception.Message);
    }

    [Fact]
    public void BindRequiresTheExecutorRegisteredForTheExactPlatformGate()
    {
        var registry = new WorkflowStepExecutorRegistry(
        [
            new(WorkflowStepExecutorType.Fixed, new FixedExecutor()),
            new(WorkflowStepExecutorType.Open, new OpenExecutor())
        ]);

        var exception = Assert.Throws<WorkflowExecutorBindingException>(
            () => WorkflowDefinitionExecutorBinder.Bind(WorkflowTestData.Snapshot(), registry));

        Assert.Equal("review", exception.StepId);
        Assert.Contains("IndependentReview", exception.Message);
    }

    [Fact]
    public void RegistryRejectsAnExecutorRegisteredUnderTheWrongStepType()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new WorkflowStepExecutorRegistry(
            [
                new(WorkflowStepExecutorType.Fixed, new OpenExecutor())
            ]));

        Assert.Contains("matching executor contract", exception.Message);
    }

    [Fact]
    public void BindRejectsUnknownStepModeAndPlatformGateValues()
    {
        var unknownModeSnapshot = UnvalidatedSnapshot(
            WorkflowTestData.Definition(
                WorkflowDefinitionOrigin.BuiltIn,
                WorkflowTestData.Open("unknown-mode", 0, []) with
                {
                    Mode = (WorkflowStepMode)999
                }));
        var unknownGateSnapshot = UnvalidatedSnapshot(
            WorkflowTestData.Definition(
                WorkflowDefinitionOrigin.BuiltIn,
                WorkflowTestData.Platform("unknown-gate", 0, []) with
                {
                    PlatformGate = (WorkflowPlatformGate)999
                }));
        var registry = new WorkflowStepExecutorRegistry([]);

        var modeException = Assert.Throws<WorkflowExecutorBindingException>(
            () => WorkflowDefinitionExecutorBinder.Bind(unknownModeSnapshot, registry));
        var gateException = Assert.Throws<WorkflowExecutorBindingException>(
            () => WorkflowDefinitionExecutorBinder.Bind(unknownGateSnapshot, registry));

        Assert.Equal("unknown-mode", modeException.StepId);
        Assert.Contains("mode value '999'", modeException.Message);
        Assert.Equal("unknown-gate", gateException.StepId);
        Assert.Contains("platform gate value '999'", gateException.Message);
    }

    private static WorkflowDefinitionSnapshot UnvalidatedSnapshot(WorkflowDefinition definition)
    {
        // Validation correctly rejects these malformed definitions; this bypass checks the binder's fail-closed defense.
        var constructor = typeof(WorkflowDefinitionSnapshot).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(WorkflowDefinition)],
            modifiers: null);
        return (WorkflowDefinitionSnapshot)constructor!.Invoke([definition]);
    }

    private sealed class FixedExecutor : IFixedWorkflowStepExecutor;

    private sealed class OpenExecutor : IOpenWorkflowStepExecutor;

    private sealed class PlatformExecutor(WorkflowPlatformGate gate) : IPlatformWorkflowStepExecutor
    {
        public WorkflowPlatformGate Gate { get; } = gate;
    }
}
