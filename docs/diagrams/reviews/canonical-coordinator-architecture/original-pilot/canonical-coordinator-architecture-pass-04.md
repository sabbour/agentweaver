# Pass 4: final correction-only review and complete arrow trace

Inspected the pass-3 input PNG, then saved a distinct byte-identical source and exported
a fresh pass-4 PNG with pinned draw.io Desktop 31.4.5. Opened the actual 1664 x 1172 PNG
and its 794 x 559 A5 print-size sheet. No remaining orientation, overlap, symbol clipping,
page overflow, endpoint, direction, label association or routing defect was found.
All nine connectors were traced on the actual output as follows.

Paths below are relative to `apps/Agentweaver.Api/`, except explicitly prefixed tests.
Each clean result includes verification of direction, head, endpoints, label and route.

| Arrow | Source -> target: relationship | Endpoint and route trace | Evidence | Result |
| --- | --- | --- | --- | --- |
| e-start | entry -> coordinator: start parent run | Right side of Entry points to left side of Coordinator; right-pointing head; short top-row gutter; start label belongs to this line | `Endpoints/ProjectEndpoints.cs:1473-1494`; `Coordinator/CoordinatorPickupService.cs:186-246` | clean |
| e-handoff | coordinator -> dispatch: hand off persisted WorkPlan | Coordinator right side to Dispatch left side; right-pointing head; separate top-row gutter; does not point into its group boundary | `Coordinator/CoordinatorRunService.cs:1172-1188,1241-1248` | clean |
| e-launch | dispatch -> children: launch dependency-ready child work | Dispatch bottom center to Child runs top center; downward head; label clear of center persistence lanes | `Coordinator/CoordinatorDispatchService.cs:384-427,870-906` | clean |
| e-output | children -> assembly: usable outputs via dispatch | Child runs left side to Collective assembly right side; left-pointing head; shaft unobscured; two-line label below identifies logical handoff, not a direct child-to-service call | `Runs/RunWorkflowFactory.cs:791-816`; `Coordinator/CoordinatorDispatchService.cs:977-989` | clean |
| e-plan-state | coordinator -> state: persist spec and plan | Coordinator bottom quarter to Durable state top midpoint; westbound lane at y=317; down-pointing head at actual store card; label avoids Access/Durable titles | `Coordinator/CoordinatorWorkflowFactory.cs:103-116`; `Coordinator/CoordinatorOrchestratorExecutor.cs:1860-1911` | clean |
| e-review-state | assembly -> state: record aggregate review | Assembly left side to Durable state right side; leftward head; lower gutter with label below shaft; does not point to a child or a memory guarantee | `Coordinator/CoordinatorAssemblyService.cs:1195-1248` | clean |
| e-dispatch-state | dispatch -> state: persist execution status | Dispatch lower quarter to store top at 72% width; westbound center lane y=334; store endpoint is distinct from spec/plan endpoint | `Coordinator/CoordinatorDispatchService.cs:977-989` | clean |
| e-state-recovery | state -> dispatch: recover persisted plan | Store right side through x=270/y=343 gutter, then x=564 north to Dispatch left lower side; right-pointing head; actual arc bridge where it crosses status-write lane, not a junction or masked gap | `Coordinator/CoordinatorReconciler.cs:117-133`; `Coordinator/CoordinatorDispatchService.cs:615-651`; `tests/Agentweaver.Tests/Coordinator/CoordinatorLeaseHeartbeatTests.cs:74-119` | clean |
| e-revision | assembly -> coordinator: review feedback enters steering | Assembly bottom at 80% width, south to y=549, east to x=817, north to y=84, west then down into Coordinator top at 92%; sole dashed marigold outer rail; label wholly on page and not on the group title | `Coordinator/CoordinatorAssemblyService.cs:2320-2368` | clean |

No dots are drawn: none of the nine relationships needs a real split/merge junction.
The one perpendicular state-lane crossing uses `jumpStyle=arc`, visibly rendered by
draw.io. Other routes do not cross unrelated cards or text. Marigold is used only for
the semantic revision rail and its legend swatch.

Remaining orientation defects: **0**. Overlap defects: **0**. Arrow defects: **0**.
No further correction pass is needed. This is a clean visual result; the separate
shared-inventory write-scope conflict remains unresolved in `research-and-contract.md`.
