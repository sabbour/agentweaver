# guide-example-scenarios-fig3: pass 04

Mode: correction-only. Reviewer/model: GPT-6 Astra (`gpt-6-astra`).
Actual inspected evidence: `guide-example-scenarios-fig3-pass-04.png` enlarged and
`guide-example-scenarios-fig3-pass-04-print.png` at approximate A5 print scale.
The images were opened, not inferred solely from XML.

Aligned m4 and m5 waypoint x values exactly to their top anchors (211.1 and 245.6). Actual export now has three separate downward heads into Observe. Traced all seven arrows; no Ready-to-explicit-start chain and no invented request-changes boolean.

Observed remaining issue clusters: orientation/in-page 0,
overlap/fitting 0, arrows 0.
These are human inspection findings, not an automated overlap-score claim.
Passes 2-4 preserve semantic node/edge identity; only visibility, fit, labels and
endpoint/routing defects are corrected. No expansion to meet a later growth target.

Draw.io SHA256: `74d64385933fbb7f6a0fd45bd64421bd1a14368deccc5b09f13ea71023924efc`.
PNG SHA256: `b107c493bae1f145c673138c581a04517f78ca5919319234dd02325163381c28`.

## Complete arrow trace

- **m1** `auth -> project`: Rightward platform authorization to project selection; repository capability remains separate. Evidence: apps/Agentweaver.Mcp/Tools/CoordinatorTools.cs:14-190; RunTools.cs:137-302,366-394; TeamTools.cs:12-79
- **m2** `project -> team`: Rightward project preparation to confirmed team; team_cast proposal needs confirmation. Evidence: apps/Agentweaver.Mcp/Tools/CoordinatorTools.cs:14-190; RunTools.cs:137-302,366-394; TeamTools.cs:12-79
- **m3** `manual -> observe`: Manual launch bottom down to Observe top at 50%; confirm label, separate head. Evidence: apps/Agentweaver.Mcp/Tools/CoordinatorTools.cs:14-190; RunTools.cs:137-302,366-394; TeamTools.cs:12-79
- **m4** `direct -> observe`: Direct start bottom via y=371 to Observe top at 77%; exact x=211.1 produces downward head. Evidence: apps/Agentweaver.Mcp/Tools/CoordinatorTools.cs:14-190; RunTools.cs:137-302,366-394; TeamTools.cs:12-79
- **m5** `backlog -> observe`: Heartbeat reservation bottom via y=380 to Observe top at 92%; exact x=245.6, no rail junction. Evidence: apps/Agentweaver.Api/Coordinator/CoordinatorPickupService.cs:183-245
- **m6** `observe -> files`: Rightward Observe to file inspection; list artifacts before run_get_file. Evidence: apps/Agentweaver.Mcp/Tools/CoordinatorTools.cs:14-190; RunTools.cs:137-302,366-394; TeamTools.cs:12-79
- **m7** `files -> review`: Rightward inspected files to gated boolean review; does not guarantee success or expose request changes. Evidence: apps/Agentweaver.Mcp/Tools/CoordinatorTools.cs:14-190; RunTools.cs:137-302,366-394; TeamTools.cs:12-79

Every actual XML edge is covered exactly once. PNG inspection checked source, destination, direction, head visibility, label association, crossings and false junctions. Final identified defects: zero.
