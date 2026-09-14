import { readFile, writeFile, readdir, unlink, mkdir, copyFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { createDiagramStamp } from '../../../../scripts/docs/diagram-sources.mjs';
import { requireFluentSource } from '../../../../scripts/docs/fluent-validation.mjs';

const here=path.dirname(fileURLToPath(import.meta.url)),root=path.resolve(here,'..','..','..','..');
const names=process.argv.slice(2);
if(!names.length)throw new Error('Explicit inspected diagram names are required');
const plan=JSON.parse(await readFile(path.join(here,'repair-plan.json'),'utf8'));
const hash=data=>createHash('sha256').update(data).digest('hex');
const escape=value=>String(value??'').replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;');
const pages=new Set();
for(const file of await readdir(path.join(root,'.github','skills','docs-diagram-audit','reports'))) {
  if(!/^plan-.*\.json$/.test(file))continue;
  const shard=JSON.parse(await readFile(path.join(root,'.github','skills','docs-diagram-audit','reports',file),'utf8'));
  for(const page of shard.document_paths)pages.add(page);
}
const approved=JSON.parse(await readFile(path.join(here,'inspection-approvals.json'),'utf8'));
const prepared=[];
for(const name of names) {
  const entry=plan.entries.find(e=>e.name===name),approval=approved[name];
  if(!entry?.candidate||entry.style_errors?.length||!approval)throw new Error(`${name}: incomplete review`);
  if(entry.uncertainty.some(reason=>typeof reason!=='string'||!reason.startsWith('Context/constraints')))
    throw new Error(`${name}: unresolved semantic uncertainty`);
  const xml=await readFile(path.join(root,entry.candidate));
  const png=await readFile(path.join(here,'rendered',`${name}.png`));
  if(hash(xml)!==approval.source_sha256||hash(png)!==approval.png_sha256)throw new Error(`${name}: inspected artifacts changed`);
  requireFluentSource(xml.toString('utf8'),name);
  const reviewDir=path.join(root,'docs','diagrams','reviews',name);
  let priorEvidence=false;
  for(const file of await readdir(reviewDir,{recursive:true}))if(path.basename(file)==='iteration-manifest.json') {
    const manifestPath=path.join(reviewDir,file),manifest=JSON.parse(await readFile(manifestPath,'utf8'));
    if(manifest.diagram!==name)continue;
    const check=spawnSync('python',['-B',path.join(root,'.github','skills','docs-diagram-iterate','scripts','validate_iteration_manifest.py'),manifestPath],{encoding:'utf8'});
    if(check.status===0){priorEvidence=true;break;}
  }
  if(!priorEvidence)throw new Error(`${name}: minimum single-agent pitch/iteration evidence is required before publication`);
  const model=JSON.parse(await readFile(path.join(here,'candidates',`${name}.model.json`),'utf8'));
  const labels=new Map((model.nodes??model.participants??[]).map(n=>[n.id,n.label]));
  const elementLabel=id=>{
    const node=[...labels.keys()].sort((a,b)=>b.length-a.length).find(n=>id===n||id.startsWith(`${n}-`));
    return node?labels.get(node):id;
  };
  const rows=(entry.preserved_visible_copy??[]).filter(item=>
    !/^\d+$/.test(item.text)&&
    !/(?:^eyebrow$|^notation$|-badge$|-pill$|-ordinal$|-source-location$|-source-id$|-source-type$)/.test(item.cell)&&
    !xml.toString('utf8').includes(`value="${escape(item.text)}"`)).map(item=>[elementLabel(item.cell),item.text]);
  for(const item of entry.extended_context??[])rows.push([item.node??item.field,
    typeof item.value==='string'?item.value:Array.isArray(item.value)?item.value.join('; '):JSON.stringify(item.value)]);
  const seen=new Set(),unique=rows.filter(([,text])=>text&&!seen.has(text)&&seen.add(text));
  const context=`<!-- diagram-context:${name}:start -->\n<details id="diagram-context-${name}" v-pre>\n<summary>Diagram details and constraints</summary>\n<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>\n`+
    unique.map(([element,text])=>`<tr><td>${escape(element)}</td><td>${escape(text)}</td></tr>`).join('\n')+
    '\n</tbody></table>\n</details>\n'+`<!-- diagram-context:${name}:end -->`;
  prepared.push({name,entry,model,xml,png,context,hasContext:unique.length>0,approval});
}
const consumers=[],pageEdits=[];
for(const relative of pages) {
  const file=path.join(root,relative);
  if(!existsSync(file))continue;
  let text=await readFile(file,'utf8'),changed=false;
  for(const item of prepared) {
    if(!text.includes(`${item.name}.png`))continue;
    const start=`<!-- diagram-context:${item.name}:start -->`,end=`<!-- diagram-context:${item.name}:end -->`;
    const previous=text.indexOf(start);
    if(previous>=0) {
      const last=text.indexOf(end,previous);
      if(last<0)throw new Error(`${relative}: incomplete managed diagram context`);
      text=text.slice(0,previous)+text.slice(last+end.length);
    }
    if(item.hasContext)text=text.trimEnd()+`\n\n${item.context}\n`;
    text=text.replaceAll(`src/${item.name}.json`,`src/${item.name}.drawio`);
    changed=true;consumers.push({name:item.name,page:relative});
  }
  if(changed)pageEdits.push({file,text});
}
for(const item of prepared)if(item.hasContext&&!consumers.some(c=>c.name===item.name))
  throw new Error(`${item.name}: no owned consumer for preserved context`);
const originals=path.join(here,'originals');
await mkdir(originals,{recursive:true});
for(const {file,text} of pageEdits)await writeFile(file,text);
for(const item of prepared) {
  const source=path.join(root,'docs','diagrams','src',`${item.name}.drawio`);
  const png=path.join(root,'docs','diagrams',`${item.name}.png`);
  for(const original of [source,png,path.join(root,'docs','diagrams',`${item.name}.hash.txt`),
    path.join(root,'docs','diagrams','src',`${item.name}.json`)]) {
    const backup=path.join(originals,path.basename(original));
    if(existsSync(original)&&!existsSync(backup))await copyFile(original,backup);
  }
  await writeFile(source,item.xml);await writeFile(png,item.png);
  const stamp=await createDiagramStamp({name:item.name,kind:'drawio',path:source},source,png,{rendererVersion:'31.4.5'});
  await writeFile(path.join(root,'docs','diagrams',`${item.name}.hash.txt`),JSON.stringify(stamp,null,2)+'\n');
  for(const retired of [path.join(root,'docs','diagrams','src',`${item.name}.json`),
    path.join(root,'docs','diagrams','drawio','generated',`${item.name}.drawio`)])if(existsSync(retired))await unlink(retired);
  const correction={diagram:item.name,source_sha256:item.approval.source_sha256,png_sha256:item.approval.png_sha256,
    prior_model:item.entry.model_source,prior_pitch_iteration_records:'retained unchanged',
    style_contract:'approved classicThin 8 / React optical Fluent',inspection:item.approval.inspection,
    inspection_notes:item.approval.notes,
    preserved_copy:item.entry.preserved_visible_copy??[],context_pages:consumers.filter(c=>c.name===item.name).map(c=>c.page),
    single_agent_exception:'User forbids subagents and waives repeat article research for existing valid evidence.',
    arrow_trace:(item.model.edges??item.model.steps??[]).map((e,i)=>({id:`e${i}`,from:e.from,to:e.to,label:e.label??'',result:'clean'}))};
  await writeFile(path.join(root,'docs','diagrams','reviews',item.name,'fluent-correction.json'),JSON.stringify(correction,null,2)+'\n');
  item.entry.status='published-validated';
  item.entry.context_resolution='Preserved outside the compact canvas in managed consumer details; complete text ledger retained in fluent-correction.json.';
  item.entry.uncertainty=[];
}
const ledgerPath=path.join(here,'promotions.json');
const ledger=existsSync(ledgerPath)?JSON.parse(await readFile(ledgerPath,'utf8')):[];
for(const item of prepared) {
  const record={name:item.name,source_sha256:item.approval.source_sha256,
    png_sha256:item.approval.png_sha256,consumers:consumers.filter(c=>c.name===item.name).map(c=>c.page)};
  const index=ledger.findIndex(e=>e.name===item.name);
  if(index<0)ledger.push(record);else ledger[index]=record;
}
await writeFile(ledgerPath,JSON.stringify(ledger,null,2)+'\n');
plan.summary=Object.fromEntries([...new Set(plan.entries.map(e=>e.status))].map(status=>[status,plan.entries.filter(e=>e.status===status).length]));
await writeFile(path.join(here,'repair-plan.json'),JSON.stringify(plan,null,2)+'\n');
console.log(`Published ${prepared.length} inspected canonical triples; updated ${new Set(consumers.map(c=>c.page)).size} consumer pages.`);
