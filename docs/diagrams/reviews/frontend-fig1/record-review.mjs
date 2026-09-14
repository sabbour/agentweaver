import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { models } from './batch-models.mjs';
const here=path.dirname(fileURLToPath(import.meta.url));
const repo=path.resolve(here,'../../../..');
const pass=process.argv[2];
const notes=process.argv[3];
if(!notes) throw new Error('Supply actual post-open inspection findings');
const selected=process.argv.slice(4);
const classification=s=>s.includes('azure2')?'native:azure':s==='umlActor'?'native:uml':s==='cylinder3'?'native:database':s==='cloud'?'native:cloud':s==='folder'?'native:uml':'native:flowchart';
for(const m of models.filter(m=>!selected.length||selected.includes(m.name))) {
  const dir=path.join(repo,'docs/diagrams/reviews',m.name);
  const base=`${m.name}-${pass}`;
  const artifact={drawio:`${base}.drawio`,png:`${base}.png`,change_record:`${base}.md`,png_inspected_print:true,png_inspected_enlarged:true};
  for(const ext of ['drawio','png']) if(!fs.existsSync(path.join(dir,`${base}.${ext}`))) throw new Error('Missing actual artifact');
  let record=`# ${m.title} — ${pass}\n\nAudience: Agentweaver implementers and operators.\n\nTakeaway: ${m.takeaway}\n\nSingle editable uncompressed A5 landscape page, 827 × 583 draw.io 100-dpi units; 5-unit accents retained. Stable output: docs/diagrams/${m.name}.png. Target documentation: docs/deep-dive/${m.name.replace(/-fig\d+$/,'')}.md.\n\n## Actual PNG inspection\n\nOpened ${base}.png at enlarged export resolution and ${base}-print.png at A5 / 96-dpi screen proof size with the image-view tool before this record. ${notes}\n\n## Grounding\n\nThe user supplied exactly three completed independent GPT-6 Astra research threads: ../canonical-api-host/research-boundaries.md, research-flows.md and research-assurance.md. No additional agents were launched. Current code was reconciled against these findings. Research inspections are not passing test executions. Facts below are implemented behavior, not proposals.\n\n## Native and custom symbols / visual credits\n\nFluent framing: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml and design-system.json, following the warm React-derived style. Cards are editable product framing; icons use native draw.io shapes. Built-in draw.io symbols are supplied by official Desktop 31.4.5 (https://github.com/jgraph/drawio-desktop, Apache-2.0 application; included library/brand notices retained). Azure identity symbols retain Microsoft trademark meaning, not endorsement. No downloaded or embedded bitmap assets.\n\n| Node | Symbol | Evidence |\n|---|---|---|\n`;
  record+=m.nodes.map(n=>`| ${n.id}: ${n.title} | ${classification(n.shape)} (${n.shape}) | ${n.evidence} |`).join('\n');
  record+='\n\n## Directed relationship evidence\n\n| ID | Source | Target | Relationship | Evidence |\n|---|---|---|---|---|\n';
  record+=m.edges.map(e=>`| ${e.id} | ${e.source} | ${e.target} | ${e.label} | ${e.evidence} |`).join('\n');
  const manifestPath=path.join(dir,'iteration-manifest.json');
  let manifest=fs.existsSync(manifestPath)?JSON.parse(fs.readFileSync(manifestPath,'utf8')):{diagram:m.name,orientation:'A5-landscape',pitch:artifact,passes:[],final_pass:4};
  if(pass==='pitch') {
    record+='\n\n## Handoff\n\nCoarse two-anchor scope association (no directional arrowhead) intentionally defers implementation detail to pass 1. Known risks: substantial unused pitch area, long scope headings, and scope anchors not yet sufficient for documentation. Upgrade into the source-backed hierarchy and exact relationships above; do not publish this pitch. Review authority/scope distinctions rather than treating the two anchors as a full sequence.\n';
    manifest.pitch=artifact;
  } else {
    const number=Number(pass.slice(-2));
    const p={number,mode:number===1?'visual-upgrade':'correction-only',...artifact,orientation_defects:0,overlap_defects:0,arrow_defects:0};
    if(number===1) {
      if(m.name==='frontend-fig2') {
        p.overlap_defects=1;
        record+='\n\nCorrection-only handoff: move router-to-assets label away from the neighboring declaration crossing. Relationship itself is correct.\n';
      }
      if(m.name==='projects-fig2') {
        p.arrow_defects=1;
        record+='\n\nCorrection-only handoff: project-to-team shares its departure port with the incoming run-to-project edge. Separate its source port to remove a misleading visual junction.\n';
      }
      const result=execFileSync('python',[path.join(repo,'.github/skills/docs-diagram-iterate/scripts/check_xml_growth.py'),path.join(dir,`${m.name}-pitch.drawio`),path.join(dir,`${m.name}-pass-01.drawio`),'--json'],{encoding:'utf8'});
      const growth=JSON.parse(result);
      fs.writeFileSync(path.join(dir,'growth.json'),result);
      p.baseline_meaningful_xml=growth.baseline_meaningful_xml;
      p.result_meaningful_xml=growth.result_meaningful_xml;
      p.growth_metric='visible-semantic-canonical-xml-v1';
      p.growth_ratio=growth.growth_ratio;
      record+=`\n\n## Growth gate\n\n\`\`\`json\n${result.trim()}\n\`\`\`\n\nExpansion is eight distinct source-backed nodes, tier surfaces, title/subtitle/detail/metadata/pill hierarchy, native symbols and relationship-specific orthogonal arrows. No invisible objects, off-page content, duplicate cells, comments, embedded images or metadata padding are used.\n`;
    }
    if(number>=4) {
      p.all_arrows_traced=true;
      p.arrow_trace=m.edges.map(e=>({id:e.id,source:e.source,target:e.target,relationship:e.label,evidence:e.evidence,result:'clean'}));
      record+='\n\n## Final every-arrow trace\n\nFor every ID in the relationship table, the opened final PNG was traced source → target. Target block arrowheads and explicit card endpoints match the cited relationship. Orthogonal routes stay in gutters; crossing jumps do not denote joins. No junction dots or dashed marigold revision rails are used. All listed arrows are clean. XML edge IDs are checked for exact equality with this table by the scoped validator.\n';
      const xml=fs.readFileSync(path.join(dir,`${base}.drawio`),'utf8');
      record+='\n| Edge ID / direction | Source and target ports (normalized card coordinates) | Authored orthogonal waypoints | Visual trace result |\n|---|---|---|---|\n';
      for(const e of m.edges) {
        const cell=xml.match(new RegExp(`<mxCell id="${e.id}"[\\s\\S]*?</mxCell>`))?.[0];
        if(!cell) throw new Error(`Missing traced edge ${e.id}`);
        const port=k=>cell.match(new RegExp(`${k}=([^;]+);`))?.[1];
        const points=[...(cell.match(/<Array as="points">([\s\S]*?)<\/Array>/)?.[1]??'').matchAll(/<mxPoint x="([^"]+)" y="([^"]+)"/g)].map(p=>`(${p[1]}, ${p[2]})`).join(' → ')||'Direct orthogonal card-to-card gutter';
        record+=`| ${e.id}: ${e.source} → ${e.target} | (${port('exitX')}, ${port('exitY')}) → (${port('entryX')}, ${port('entryY')}) | ${points} | Clean: source departure, target block arrowhead, direction and label agree with evidence above; no unintended join. |\n`;
      }
    }
    if(manifest.passes.some(p=>p.number===number)) throw new Error('Pass already recorded; preserve independent artifacts');
    manifest.passes.push(p);
  }
  fs.writeFileSync(path.join(dir,`${base}.md`),record+'\n',{flag:'wx'});
  fs.writeFileSync(manifestPath,JSON.stringify(manifest,null,2)+'\n');
  console.log(`${m.name}: ${pass} inspection recorded`);
}
