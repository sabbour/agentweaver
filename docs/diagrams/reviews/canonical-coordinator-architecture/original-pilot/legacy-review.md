# Coordinator architecture review

## Evidence

- `apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:38-40` — confirmation advances the outcome specification while revision returns it for another draft.
- `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:48-55` — the dispatcher starts the dependency-ready frontier as parallel child runs and observes their terminal states.
- `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:64-82` — collective assembly operates on the combined outputs and gates integration, review, merge, and scribe work.
- `apps/Agentweaver.Api/Coordinator/CollectiveAssemblyPipeline.cs:17-29` — the assembly pipeline builds the integration branch, merges once, and invokes the shared scribe executor.

## Iterations

1. Initial composition — replaced the linear eight-card graph with four responsibility bands: intake, coordinate, execute, and assure.
2. Geometry pass — used a 1400 × 840 page, consistent 112 px cards, 20–30 px internal gutters, and a single left-to-right narrative.
3. Connector pass — routed the main lifecycle through orthogonal connectors and placed the changes-requested path on a dashed marigold outer rail.
4. Correctness pass — traced request/backlog input through OutcomeSpec, WorkPlan, dependency-ready child execution, collective assembly, review, merge, and recorded learnings.
