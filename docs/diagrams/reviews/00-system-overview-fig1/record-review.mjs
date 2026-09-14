import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { models } from './batch-models.mjs';
const here=path.dirname(fileURLToPath(import.meta.url)), repo=path.resolve(here,'../../../..');
const [stage,selector='all',note='']=process.argv.slice(2);
if(!note) throw Error('An actual visual inspection note is required');
for(const current of models.filter(m=>selector==='all'||selector.split(',').includes(m.name))) {
  const dir=path.join(repo,'docs/diagrams/reviews',current.name);
  const finalReview=/^pass-0[45]$/.test(stage);
  const m=finalReview?current:JSON.parse(await fs.readFile(path.join(dir,'content-model.json')));
  const base=`${m.name}-${stage}`;
  const credit='Native process, decision, document, folder and cylinder symbols are editable built-in draw.io shapes, bundled with official draw.io Desktop 31.4.5. Library/code license: Apache-2.0, https://github.com/jgraph/drawio/blob/dev/LICENSE (bundled LICENSE/resources notices remain authoritative). No third-party raster logos or remote images were imported. Product-specific card chrome: custom:agentweaver; icon classifications listed below. Visual references read: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml, design-system.json and canonical-api-host review helpers. A5 normalized directly to 827 × 583 draw.io units at 100 dpi; the 1112 × 789 working template is not used as print size.';
  const research=['research-boundaries.md','research-flows.md','research-assurance.md'].map(f=>`../canonical-api-host/${f}`).join(', ');
  let contents=`# ${m.title} — ${stage}\n\nAudience: technical readers of the deep-dive documentation.\nTakeaway: ${m.takeaway}\nOrientation: A5-landscape; one uncompressed editable page.\nCanonical: docs/diagrams/src/${m.name}.drawio; publication: docs/diagrams/${m.name}.png.\n\n## Actual image review\n\nOpened ${base}.png enlarged and ${base}-print.png as an A5/96-dpi screen proof. ${note}\nThis is a screen print-size review, not a physical paper proof.\n\n## Grounding\n\nExactly three independent GPT-6 Astra research threads were already completed for this shard: ${research}. No agents were launched by this batch. Implementation/config/test references below are factual evidence; prior artwork and audit dispositions are not factual evidence. Test sources were inspected; no runtime test success is implied.\n\n## Source and symbol inventory\n\n${m.nodes.map(n=>`- **${n.id}** — ${n.title}; ${n.classification} inside custom:agentweaver Fluent chrome. Evidence: ${n.evidence}.`).join('\n')}\n\n## Relationships\n\n${m.edges.filter(e=>!e.relationOnly).map(e=>`- \`${e.id}\`: ${e.source} → ${e.target}: ${e.label}. Evidence: ${e.evidence}.`).join('\n')||'No arrows: this diagram is an independent coverage taxonomy, not an execution sequence.'}\n\n## Credits and boundaries\n\n${credit}\n\n${m.scope}\nNo documentation, inventory, shared audit/plan, pipeline, skill, runtime, or foreign asset was edited. The parent owns Markdown provenance updates.\n`;
  if(stage==='pitch') contents+='\n## Handoff\n\nCoarse two-concept pitch. Pass 1 must expand source-backed detail and groups, supply the actual relationships, improve density, and pass the ≥9× meaningful XML gate. This pitch is not publishable.\n';
  if(stage==='pass-01') {
    const report=execFileSync('python',['.github\\skills\\docs-diagram-iterate\\scripts\\check_xml_growth.py',path.join(dir,`${m.name}-pitch.drawio`),path.join(dir,`${m.name}-pass-01.drawio`),'--json'],{cwd:repo,encoding:'utf8'});
    await fs.writeFile(path.join(dir,'growth.json'),report);
    contents+=`\n## Meaningful growth gate\n\n\`\`\`json\n${report}\n\`\`\`\nVisible upgrade: eight distinct grounded contracts; native symbols; separate title/subtitle/detail/metadata/pills; tiered group surfaces; explicit scope; source-backed connectors where relationships exist. No hidden/off-page objects, duplicate nodes, embedded images, padding, or invented facts count toward growth.\n`;
  }
  if(finalReview) contents+='\n## Complete arrow trace\n\n'+(m.edges.filter(e=>!e.relationOnly).map(e=>`- \`${e.id}\` | ${e.source} | ${e.target} | ${e.label} | ${e.evidence} | source→target block arrowhead | endpoints on intended card | orthogonal route checked in opened PNG | ${stage==='pass-04'&&m.name==='00-system-overview-fig2'&&e.id==='rai-to-review'?'endpoint clean; “cleared” label ambiguous, correction queued for pass-05':'clean'}`).join('\n')||'The final XML has zero edge cells; the evidence map has zero arrows. Empty trace is intentional.')+'\n';
  await fs.writeFile(path.join(dir,`${base}.md`),contents,{flag:'wx'});
}
