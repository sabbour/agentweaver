# Pass 4 - final correction and complete arrow trace

Opened the pass-3 input, saved a distinct unchanged source, exported pass 4 through Desktop31.4.5,
then opened its actual enlarged PNG and A5/96-dpi proof. No correction was needed. All source
vertices and waypoints remain inside the single 827 x 583 A5 landscape page.

| ID | Source -> target | Meaning/evidence | Direction, endpoints, route | Result |
| --- | --- | --- | --- | --- |
| startup-host | startup -> role | Prepares HTTP host; Program.cs:1200-1326 | Right, attached card sides, isolated startup gutter | Clean |
| request-policy | client -> middleware | Request enters policy; Program.cs:1274-1295 | Right, correct side ports, clear of classification note | Clean |
| policy-handler | middleware -> handler | Dispatch after policy; Program.cs:1295-1326 | Down, bottom/top ports, row gutter | Clean |
| handler-service | handler -> services | Delegate operation; BlueprintEndpoints.cs:100-150 | Left, lower parallel lane, correct card sides | Clean |
| service-store | services -> stores | Read/write; Program.cs:1026-1075 | Down, attached top/bottom ports, label in gutter | Clean |
| service-adapters | services -> adapters | Invoke external effects; RunOrchestrator.cs:277-317 | Right/down/right, lower service port and central gutter | Clean |
| store-result | stores -> services | Return rows; Program.cs:1026-1075 | Left/up/right, separate outer marigold return rail | Clean |
| service-result | services -> handler | Return result; BlueprintEndpoints.cs:143-150 | Right, upper parallel lane; distinct from delegation | Clean |
| handler-dto | handler -> client | Project HTTP response; BlueprintEndpoints.cs:143-150 | Outer right/top/left rail; attaches to client, not group | Clean |

File roots are `apps/Agentweaver.Api`, with endpoint/source subdirectories as recorded fully in
`content-model.json`. All nine XML edge IDs appear exactly once above and in the manifest.
Arrowheads are filled targets, with no accidental source arrowhead. Dashed marigold means return,
not an ordinary forward call. No crossings or logical split/merge junctions occur, so bridge arcs
and junction dots are not needed. No line obscures a card or another label.

Orientation defects: 0. Overlap defects: 0. Arrow defects: 0. Final pass: 4.
All correction sources are byte-identical to the accepted A5 pass-1 source; each PNG was exported
and inspected separately. Publication is limited to this reviewed diagram, not approval of any
shared canonical referenced elsewhere in the shard.
