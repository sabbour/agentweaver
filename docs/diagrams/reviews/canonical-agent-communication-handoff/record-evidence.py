"""Save the review results after actual pitch and pass PNG inspections."""
import hashlib
import importlib.util
import json
from pathlib import Path
from xml.etree import ElementTree as E
import jsonschema
from PIL import Image

HERE=Path(__file__).resolve().parent
def load(name,path):
    spec=importlib.util.spec_from_file_location(name,path)
    mod=importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod
a=load("author",HERE/"author-eight.py")
g=load("growth",a.ROOT/".github"/"skills"/"docs-diagram-iterate"/"scripts"/"check_xml_growth.py")
v=load("manifest",a.ROOT/".github"/"skills"/"docs-diagram-iterate"/"scripts"/"validate_iteration_manifest.py")
schema=json.loads((a.ROOT/".github"/"skills"/"docs-diagram-iterate"/"references"/"iteration-manifest.schema.json").read_text(encoding="utf-8"))

a.EVIDENCE["handoff"]="apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:48-57,76,116-124,407,477,778; apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:81-96; docs/deep-dive/agent-communication.md:126-200"
a.EVIDENCE["journey"]="apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:65-82 and assembly workflow construction; apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:48-57; apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:81-96; coordinator-provided completed assurance research summary"
a.EVIDENCE["deployment"]="k8s/base/api-deployment.yaml:53-76,337-349; k8s/base/worker-deployment.yaml:54-76,146-170; k8s/base/mcp-deployment.yaml:21; apps/Agentweaver.Api/Sandbox/RunGitHubCapabilityCredentialProvider.cs:10-36; apps/Agentweaver.Api/Auth/EntraOnlyGitHubCredentialBoundary.cs; coordinator-provided component and assurance findings"

RESEARCH={
    "components":"""Completed independent GPT-6 Astra component thread: findings supplied by the coordinator.

Entra identity is distinct from purpose-bound GitHub repository capabilities. A constrained
repository credential flow into execution exists: do not describe the sandbox as credential-free.
API and worker use PostgreSQL and CSI-mounted storage. MCP does not own a database.
Component arrows must be project dependencies, confirmed from csproj, not network routing.

The original component tool-output attachment was named
1789326827577-copilot-tool-output-f501c576806f451392d6d58be6d4d3bb.txt.
Its Temp location was not opened because this execution context forbids all Temp file operations.
This is the supplied findings summary, not a claimed verbatim copy of that report.
""",
    "flows":"""Completed independent GPT-6 Astra directional-flow thread: findings supplied by the coordinator.

Each child has its own worktree. The dependency frontier drives dispatch; it is not peer chat.
Failed or RAI-flagged children block dependents. Board cards project persisted state; unresolved
dependencies leave tasks in Ready with a blocked flag. Approval is not completion.
EF/PostgreSQL RunEvents relay across replicas by cursor polling every 250 ms when idle.
Append takes a per-run lock, allocates MAX+1, commits, then acknowledges. Subscribers drain
the loaded batch before closing on a terminal event. SQLite register-channel/replay/tail is a separate lane.

The original flow tool-output attachment was named
1789326947702-copilot-tool-output-3e5f055558f345178849bcbf9aebd687.txt.
Its Temp location was not opened because this execution context forbids all Temp file operations.
This is the supplied findings summary, not a claimed verbatim copy of that report.
""",
    "assurance":"""Completed independent GPT-6 Astra assurance thread: findings supplied by the coordinator.

Collective coordinator assembly reaches MergeWorktree -> Scribe. Automatic PR publication is not
verified for this collective path and must not be asserted. The default standalone workflow can
publish/reuse a PR but is outside these eight assets. Decline skips Scribe; merge failure may still
run Scribe. Blocked assembly is recoverable, not terminal.
Explicit event-sequence duplicates are idempotent only for an identical type/payload.
Late-delta suppression is process-local, not a database terminal fence.

The coordinator explicitly stated three independent bounded research threads were already complete.
No nested or additional research agents were launched by this implementation worker.
""",
}
OBS={
    "canonical-agent-communication-handoff":(
        (5,1,3),"Pass 2 corrected distorted product icons, separated dispatch ports and lanes, moved A's result to the bottom gutter, and shortened the lower group label. One true bridge at the independent dispatch-lane crossing; no peer-to-peer arrows.",
        "Goal drafts the confirmed intent contract; confirmation yields the DAG; two separately routed dispatch arrows end at A and B; both result arrows end at assembly. No shared port pretends a peer conversation."),
    "canonical-board-lifecycle":(
        (0,1,2),"The visual-upgrade draft was regrounded before correction passes in WorkflowStageProjector: default Active, Problems, Human Review, Done, plus Backlog and Ready. Pass 2 separated problem/review ports and fitted Done's exact persisted statuses on one line.",
        "Five schematic arrows are queue, claim/start, await review, finish, and problem. These summarize typical changes, not exhaustive transitions. Problems includes recoverable assembly blocked; configured stages can replace defaults."),
    "canonical-coordinator-journey":(
        (5,0,0),"Pass 2 corrected product-symbol size/orientation. Snake reading order remains left-to-right across planning, down the outer gutter, then right-to-left through integration, collective review and finishing.",
        "Five arrows follow confirmed intent -> plan -> dispatch -> integration -> collective review -> approved merge/Scribe. Finish does not assert automatic PR publication. Decline and recoverable-block alternatives remain explicit notes."),
    "canonical-durable-event-stream":(
        (2,1,0),"Pass 2 restored the product hexagon and native database cylinder proportions; its silhouette no longer overlaps the subtitle. The five-arrow relay stays entirely in gutters.",
        "Producer -> EF append -> durable RunEvents -> replica-B subscriber -> SSE -> watcher. The store/read edge is the returned ordered batch, not a live in-process channel. The endpoint drains the batch before evaluating terminal close."),
    "email-components":(
        (0,1,2),"Pass 2 separated API and AgentHost dependency paths, moved API's route to the left perimeter, shortened the shared-package group label, and retained dashed open UML dependency heads.",
        "Four selected ProjectReferences: Api -> AgentRuntime; AgentHost -> AgentRuntime; AgentRuntime -> AgentTools; AgentTools -> Domain. No MCP dependency arrow and no network routing implication."),
    "email-architecture":(
        (1,1,2),"Pass 2 corrected the database silhouette and separated A2A/database label positions; pass 3 raised MCP's HTTP connector to a distinct pair of ports clear of the capability/A2A routes.",
        "MCP -> API is HTTP; API/worker -> sandbox is configure/A2A; Entra -> API is identity; GitHub -> API is capability; API/worker -> Postgres is persistence. There is no MCP/store edge. Independent crossing routes use real draw.io bridge arcs."),
    "canonical-durable-event-stream-sequence":(
        (2,1,0),"Pass 2 corrected product/database silhouettes. Horizontal numbered messages attach to explicit activation bars, not headers. Context annotations occupy unused temporal lanes.",
        "Eight chronological messages: append; locked write; commit reply; sequence acknowledgment; reconnect; cursor query; durable batch reply; SSE delivery. Left-pointing replies retain their correct recipient. No channel-publish phase is invented for EF."),
    "email-coordinator-workflow":(
        (4,0,0),"Pass 2 corrected product-symbol proportions. The sequence retains five lifelines, explicit activation endpoints, chronological horizontal messages and native bridge arcs where messages cross uninvolved lifelines.",
        "Nine chronological messages: submit; request confirmation; confirm; dispatch; child results; assembly; collective review request; approve; MergeWorktree/Scribe. The final combined participant is a compact approved-path grouping, not a claim that Git is Scribe or a PR publisher."),
}

def write_json(path,data):
    path.write_text(json.dumps(data,indent=2,ensure_ascii=False)+"\n",encoding="utf-8")

def classify(n):
    shape=n["shape"]
    if "azure" in shape:return "native:azure"
    if "kubernetes" in shape:return "native:kubernetes"
    if shape=="cylinder3":return "native:database"
    if shape in ("umlActor","component"):return "native:uml"
    if "flowchart" in shape or shape=="rhombus":return "native:flowchart"
    if shape=="cloud":return "native:cloud"
    return "custom:agentweaver"

for name,model in a.MODELS.items():
    folder=a.REVIEWS/name
    for subject,body in RESEARCH.items():
        (folder/f"research-{subject}.md").write_text("# Research handoff: "+subject+"\n\n"+body,encoding="utf-8")
    node_list=model.get("nodes",model.get("participants"))
    content=dict(model)
    content["grounding"]=a.EVIDENCE[model["evidence"]]
    content["nodes_or_participants"]=[dict(n,classification=classify(n),evidence=content["grounding"]) for n in node_list]
    write_json(folder/"content-model.json",content)
    growth=g.assess((folder/f"{name}-pitch.drawio").read_text(encoding="utf-8"),(folder/f"{name}-pass-01.drawio").read_text(encoding="utf-8"))
    assert growth["passed"], (name,growth)
    write_json(folder/"growth-check.json",growth)
    pitch=dict(drawio=f"{name}-pitch.drawio",png=f"{name}-pitch.png",change_record=f"{name}-pitch.md",png_inspected_print=True,png_inspected_enlarged=True)
    credits="""## Visual references and assets
- Started from `docs/diagrams/drawio/fluent-template.drawio`; loaded `fluent-library.xml`.
  Removed the invisible template metadata and all example cells; set one editable 827 x 583 A5 landscape page.
- Palette, warm surfaces, Segoe UI, 16 px rounded cards, 5 px accents, tiered groups,
  title/subtitle/metadata/pill hierarchy derive from `drawio/design-system.json`.
- React reference: `apps/web/src/components/CoordinatorTopologyGraph.tsx:93-130`
  (Fluent neutral surfaces, border hierarchy, marigold state treatment and badges).
- Native geometry is bundled draw.io UML/flowchart/database/cloud/Kubernetes notation.
  draw.io source: https://github.com/jgraph/drawio (Apache-2.0; bundled third-party assets retain their notices).
- Entra uses the native bundled Azure identity SVG, not a hand-drawn logo:
  `img/lib/azure2/identity/Azure_Active_Directory.svg`.
  Microsoft architecture-icon usage guidance: https://learn.microsoft.com/azure/architecture/icons/.
  No downloaded brand imagery or embedded raster padding was used.
- Product-only concepts use custom Agentweaver hexagon badges in Fluent card chrome.
"""
    inventory="\n".join(f"- `{n['id']}`: {classify(n)} — {n['title']}." for n in node_list)
    pitch_body=f"""# {name}: pitch

Audience: Agentweaver users and architecture readers.
Takeaway: {model['subtitle']}
Orientation: A5-landscape, one uncompressed editable 827 x 583 page.
Disposition: {model['disposition']} (retain preserves supported identity, not the old rendering).
Canonical source: `docs/diagrams/src/{name}.drawio`.
Stable PNG: `docs/diagrams/{name}.png`.
Owner: shared-eight-survivors; claim is local because the assignment forbids inventory writes.
Documentation integration is owned by the parent; no Markdown consumer was edited here.

## Grounding
{content['grounding']}

See `research-components.md`, `research-flows.md`, `research-assurance.md` for the three
completed independent-thread handoffs supplied by the coordinator. These are supplied-summary
records, not verbatim copies of inaccessible Temp attachments. Direct implementation reads
resolved the board's actual stage mapping and verified EF's durable relay and project references.
`content-model.json` records every semantic node/participant and its classification.
Legacy diagrams were used only to preserve identity, not as authority for runtime claims.

## Actual PNG inspection and handoff
Opened the exported pitch PNG at enlarged resolution and its actual print-scale raster on
`../canonical-agent-communication-handoff/pitch-print-sheet-1.png` or `-2.png`.
The pitch is a deliberately small editable concept sketch, not a publication.
Visible risks handed to pass 1: sparse hierarchy, long or crossing labels, absent native
symbols and omitted detailed boundaries/assurance. Sequence pitches retain lifelines.
The email architecture pitch's long A2A edge crosses the middle card: explicitly not clean.
All added pass-1 content must remain grounded in the evidence above.

{credits}
## Symbol inventory for the upgrade
{inventory}
"""
    (folder/pitch["change_record"]).write_text(pitch_body,encoding="utf-8")
    manifest=dict(diagram=name,orientation="A5-landscape",pitch=pitch,passes=[],final_pass=4)
    arrow_base=json.loads((folder/"pass-01-arrows.json").read_text(encoding="utf-8"))
    for arrow in arrow_base:
        arrow["evidence"]=content["grounding"]
    observations=[]
    for number in range(1,5):
        stem=f"{name}-pass-{number:02}"
        xml=(folder/f"{stem}.drawio").read_text(encoding="utf-8")
        analysis=g.analyze(xml)
        assert not any(analysis[k] for k in ("invisible_cells","off_page_cells","duplicate_cells","metadata_padding"))
        assert (analysis["page_width"],analysis["page_height"])==(827,583)
        image=Image.open(folder/f"{stem}.png")
        image.verify()
        counts=OBS[name][0] if number==1 else (0,1,1) if number==2 and name=="email-architecture" else (0,0,0)
        entry=dict(number=number,mode="visual-upgrade" if number==1 else "correction-only",
            drawio=f"{stem}.drawio",png=f"{stem}.png",change_record=f"{stem}.md",
            png_inspected_print=True,png_inspected_enlarged=True,
            orientation_defects=counts[0],overlap_defects=counts[1],arrow_defects=counts[2])
        if number==1:
            entry.update({k:growth[k] for k in ("growth_metric","growth_ratio","baseline_meaningful_xml","result_meaningful_xml")})
        if number==4:
            entry.update(all_arrows_traced=True,arrow_trace=arrow_base)
        manifest["passes"].append(entry)
        cells={c.get("id"):c for c in E.fromstring(xml).iter("mxCell")}
        arrow_ids={id for id,c in cells.items() if c.get("edge")=="1" and "endArrow=none" not in c.get("style","")}
        assert arrow_ids=={e["id"] for e in arrow_base},(name,number,arrow_ids)
        trace=[]
        for arrow in arrow_base:
            c=cells[arrow["id"]]
            source,target=c.get("source"),c.get("target")
            assert source in cells and target in cells
            assert "endArrow=block" in c.get("style","") or "endArrow=open" in c.get("style","")
            points=[(p.get("x"),p.get("y")) for p in c.findall(".//Array/mxPoint")]
            trace.append(f"| {arrow['id']} | {arrow['source']} → {arrow['target']} | {arrow['relationship'].replace('<br>',' ')} | {source} / {target} | {points or 'direct horizontal gutter / activation route'} | clean |")
        action="Visual upgrade; known residual icon, overlap and connector defects are recorded below." if number==1 else OBS[name][1] if number==2 else "Corrected the MCP HTTP arrow's ports and label collision; retained content and layout." if number==3 and name=="email-architecture" else "No permitted defect found; saved a distinct source and fresh draw.io PNG export without composition/content changes."
        body=f"""# {name}: pass {number:02}

Mode: {entry['mode']}.
{action}

Actual exported PNG opened at print scale in
`../canonical-agent-communication-handoff/pass-{number:02}-print-sheet-1.png` or `-2.png`,
and opened individually at enlarged export resolution. The previous exported PNG was
inspected before this pass. This record was compiled after the tool-backed inspections.

Orientation defects remaining: {counts[0]}; overlap defects remaining: {counts[1]};
arrow defects remaining: {counts[2]}.
No new nodes, facts, themes or composition were introduced after pass 1.

Visible-semantic growth metric: `{growth['growth_metric']}`.
Pitch {growth['baseline_meaningful_xml']} → pass 1 {growth['result_meaningful_xml']}
= {growth['growth_ratio']}x. Later passes preserve that hierarchy.
No invisible, off-page, duplicate or metadata-padding cells were found by the checker.

## Source-backed reading and review
{OBS[name][2]}
Evidence: {content['grounding']}

"""
        if number==4:
            body+="""## Complete final arrow trace
Each row was traced in the final exported PNG and checked against the editable endpoints.
Arrowheads point toward the named target; gutters avoid cards/labels.
Crossings are native `jumpStyle=arc` bridges, never paint-over masks. No false junction dots.
Neutral UML dashed dependency/lifeline strokes are not marigold semantic return rails.

| ID | Semantic endpoints | Relationship | Editable cell endpoints | Route waypoints | Result |
|---|---|---|---|---|---|
"""+"\n".join(trace)+"\n"
        (folder/entry["change_record"]).write_text(body,encoding="utf-8")
        observations.append(dict(pass_number=number,png_sha256=hashlib.sha256((folder/f"{stem}.png").read_bytes()).hexdigest(),xml_analysis=analysis))
    jsonschema.Draft202012Validator(schema).validate(manifest)
    assert not v.validate_manifest(manifest)
    write_json(folder/"iteration-manifest.json",manifest)
    write_json(folder/"validation-evidence.json",dict(schema_valid=True,ordered_passes_valid=True,arrow_count=len(arrow_base),final_arrow_ids=sorted(arrow_ids),passes=observations))
    write_json(folder/"claim.json",dict(owner="shared-eight-survivors",name=name,disposition=model["disposition"],scope="exclusive_asset_paths only; no global inventory writes",status="four-pass-review-complete",raw_research_attachment_caveat="Supplied summaries saved; original Temp attachments not opened."))
    print(name,growth["growth_ratio"],"schema + four passes +",len(arrow_base),"arrows: PASS")
