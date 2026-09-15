import { mkdir, readdir, readFile, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { listDiagramSources } from './diagram-sources.mjs';
import { specToDrawio } from './drawio-generator.mjs';
import { cardContentMetrics } from './fluent-tokens.mjs';
import { createElkLayout } from './fluent-elk-layout.mjs';
import { fitSourceLayout } from './fluent-source-layout.mjs';
import { defaultWorkflowLayout } from './default-workflow-layout.mjs';
import { inspectFluentSource } from './fluent-validation.mjs';
import { toSpec as workflowToSpec } from './workflows-to-graphspec.mjs';
import { parse as parseYaml } from 'yaml';
import { createHash } from 'node:crypto';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..','..');
const out=path.join(root,'docs','diagrams','reviews','catalog-integration');
await mkdir(path.join(out,'candidates'),{recursive:true});
const report={status:'planning',entries:[]};
const priorPath=path.join(out,'repair-plan.json');
const prior=existsSync(priorPath)?JSON.parse(await readFile(priorPath,'utf8')):{entries:[]};
const selected=new Set(process.argv.slice(2));
const ledgerPath=path.join(out,'promotions.json');
const promoted=existsSync(ledgerPath)?JSON.parse(await readFile(ledgerPath,'utf8')):[];
const workflows=new Map();
const workflowDirectory=path.join(root,'packages','Agentweaver.Squad','Catalog','Resources','workflows');
for(const file of await readdir(workflowDirectory)) if(file.endsWith('.yaml')) {
  const workflow=parseYaml(await readFile(path.join(workflowDirectory,file),'utf8'));
  workflows.set(`workflow-${workflow.id}`,{spec:workflowToSpec(workflow),context:[],
    source:path.relative(root,path.join(workflowDirectory,file))});
}
const defaultPath=path.join(root,'apps','Agentweaver.Api','Workflows','DefaultWorkflowTemplate.cs');
const defaultText=await readFile(defaultPath,'utf8');
const defaultYaml=defaultText.match(/public const string Yaml\s*=\s*"""([\s\S]*?)"""/)?.[1];
if(!defaultYaml) throw new Error('Cannot locate authoritative default workflow YAML');
const defaultWorkflow=parseYaml(defaultYaml.split('\n').map(line=>line.replace(/^ {8}/,'')).join('\n'));
const defaultSpec=workflowToSpec(defaultWorkflow);
const defaultLabels={
  agent:['Agent work','bot','green'],rai:['RAI gate','bot','green'],review:['Human review','bot','green'],
  merge:['Merge','bot','green'],'push-pr':['Publish / reuse PR','branch','green'],scribe:['Scribe','bot','green'],
  done:['Done','window','teal'],declined:['Terminal: declined','window','teal'],
  'safety-failed':['Terminal: safety failed','window','teal'],
};
for(const node of defaultSpec.nodes)if(defaultLabels[node.id]) {
  const [label,icon,tone]=defaultLabels[node.id];
  Object.assign(node,{label,icon,badge:{tone}});delete node.subLabel;
}
workflows.set('canonical-default-workflow',{spec:defaultSpec,context:[],source:path.relative(root,defaultPath)});
const normalize = id => String(id).replace(/^(node-|n-)/,'').replace(/(-card|-node)$/,'').toLowerCase();
const combined=[];
for(const relative of [
  'canonical-pod-process-boundaries/content-models.json',
  'coordinator-internals-fig4/owned-models-v2.json',
  'experience-00-overview-fig1/experience-content-models.json',
]) {
  const file=path.join(root,'docs','diagrams','reviews',relative);
  if(existsSync(file)) combined.push({source:path.relative(root,file),models:JSON.parse(await readFile(file,'utf8'))});
}
function project(model) {
  if(!model||typeof model!=='object') return null;
  if(model.kind==='sequence'&&model.participants&&model.steps) return {spec:model,context:[]};
  if(model.participants&&model.messages) {
    const participant=n=>({id:n.id,label:n.title??n.label,subLabel:n.sub??n.subtitle,meta:n.meta,
      shape:n.shape,icon:n.icon??'box',badge:{text:n.pill??'Participant',tone:n.tone??'neutral'}});
    return {spec:{kind:'sequence',title:model.title,participants:model.participants.map(participant),
      steps:model.messages.map(m=>({type:'message',from:m[0]??m.from,to:m[1]??m.to,
        label:m[2]??m.label,line:m.line??'solid',arrow:m.arrow??'filled'}))},
      context:[{field:'notes',value:model.notes??[]}]};
  }
  if(!Array.isArray(model.nodes)||!Array.isArray(model.edges)) return null;
  const context=[];
  if(model.nodes.every(Array.isArray)) {
    const named=model.edges.some(e=>Array.isArray(e)&&typeof e[0]==='string');
    model={...model,nodes:model.nodes.map(n=>named?{
      id:n[0],title:n[1],subtitle:n[2],meta:n[3],library:n[4]==='custom'?undefined:n[4],
    }:({
      title:n[0],subtitle:n[1],meta:n[2],symbol:n[3],tone:n[4],badge:n[5],evidence:n[6],
    }))};
  }
  const positional=model.nodes.every(n=>!n.id)&&model.edges.every(e=>Number.isInteger(Array.isArray(e)?e[0]:e.source));
  const icons={person:'globe',account:'globe',process:'window',product:'box',custom:'box',pod:'bot',
    gateway:'route',service:'server',database:'database',store:'database',decision:'branch',
    event:'route',document:'window',cloud:'box'};
  const nodes=model.nodes.map((n,index)=>{
    const id=n.id??(positional?`n${index}`:undefined);
    if(!id) throw new Error('Semantic node missing stable id');
    for(const key of ['facts','lines','detail','description']) if(n[key]) context.push({node:id,field:key,value:n[key]});
    return {id,label:n.label??n.title??id,subLabel:n.subLabel??n.subtitle??n.sub,
      meta:n.meta??n.metadata,icon:icons[n.icon??n.symbol]??n.icon??'box',shape:n.shape,
      library:n.library,
      classification:n.classification??((n.icon??n.symbol)==='cloud'?'native:cloud':undefined),
      badge:typeof n.badge==='object'?n.badge:{text:typeof n.badge==='string'?n.badge:n.pill??n.kind??'Component',
        tone:typeof n.tone==='number'?['lavender','teal','green','marigold','neutral'][n.tone]:
          n.tone??({live:'green',gate:'teal',action:'lavender',terminal:'teal',agent:'green'}[n.kind])??'neutral'}};
  });
  const endpoint=id=>positional?`n${id}`:id;
  const edges=model.edges.map(e=>Array.isArray(e)?{from:endpoint(e[0]),to:endpoint(e[1]),label:e[2],loopback:e[5]===true}:
    {from:endpoint(e.from??e.source),to:endpoint(e.to??e.target),label:e.label??'',loopback:e.loopback??e.returning??e.revision??false});
  for(const key of ['takeaway','scope','notes','note','footer','groups']) if(model[key]) context.push({field:key,value:model[key]});
  return {spec:{title:model.title,nodes,edges,direction:model.direction??'TB'},context};
}
async function findModel(name) {
  const dir=path.join(root,'docs','diagrams','reviews',name);
  const candidates=['final-content-model.json','fullfluent-remediation/content-model.json',
    'fluent-pitch-remediation/content-model.json','content-model.json','evidence.json','content-models.json'];
  for(const file of candidates) {
    const candidate=path.join(dir,file);
    if(!existsSync(candidate)) continue;
    const raw=JSON.parse(await readFile(candidate,'utf8'));
    const model=(typeof raw.model==='object'?raw.model:undefined)??raw[name]??raw.models?.[name]??raw;
    const projected=project(model);
    if(projected) return {...projected,raw,source:path.relative(root,candidate)};
  }
  for(const store of combined) {
    const model=store.models[name]??store.models.models?.[name]??store.models.diagrams?.find(d=>d.name===name);
    const projected=project(model);
    if(projected) return {...projected,raw:model,source:store.source};
  }
  const source=path.join(root,'docs','diagrams','src',`${name}.drawio`);
  const recovered=spawnSync(process.env.PYTHON??'python',['-B',path.join(root,'scripts','docs','recover-drawio-semantics.py'),source],
    {input:'[]',encoding:'utf8'});
  if(recovered.status===0) {
    const data=JSON.parse(recovered.stdout);
    if(data.model.nodes.length&&!data.unbound_edges.length) return {
      spec:{...data.model,title:name},context:[],source:path.relative(root,source),recovered_from_xml:true};
  }
  return null;
}
const sourceList=await listDiagramSources(path.join(root,'docs','diagrams','src'));
const proposed='canonical-pod-process-boundaries';
const proposedDraft=path.join(root,'docs','diagrams','reviews',proposed,`${proposed}-pass-01.drawio`);
if(!sourceList.some(s=>s.name===proposed)&&existsSync(proposedDraft))sourceList.push({name:proposed,path:proposedDraft,kind:'drawio'});
for(const source of sourceList) {
  const previous=prior.entries.find(e=>e.name===source.name);
  if(selected.size&&!selected.has(source.name)&&previous){report.entries.push(previous);continue;}
  const published=promoted.findLast(e=>e.name===source.name);
  if(published&&previous&&createHash('sha256').update(await readFile(source.path)).digest('hex')===published.source_sha256) {
    const current=inspectFluentSource(await readFile(source.path,'utf8'));
    if(!current.errors.length&&!current.uncertainty.length) {
      report.entries.push({...previous,status:'published-validated'});continue;
    }
    const model=JSON.parse(await readFile(path.join(out,'candidates',`${source.name}.model.json`),'utf8'));
    const xml=specToDrawio(model,{name:source.name}),checked=inspectFluentSource(xml);
    await writeFile(path.join(root,previous.candidate),xml);
    report.entries.push({...previous,status:'blocked-review',style_errors:checked.errors,
      uncertainty:checked.uncertainty,
      correction_reason:'Regenerated the preserved reviewed model for newly enforced visual constraints; prior copy and topology are unchanged.'});
    continue;
  }
  const entry={name:source.name,source:path.relative(root,source.path),kind:source.kind,uncertainty:[]};
  report.entries.push(entry);
  if(source.name==='canonical-coordinator-architecture') {
    entry.status='protected-retained';
    entry.reason='Completed coordinator pilot is explicitly excluded from editing by the request.';
    continue;
  }
  try {
    const raw=await readFile(source.path,'utf8');
    let projected;
    if(workflows.has(source.name)) projected=workflows.get(source.name);
    else if(source.kind==='json') projected=await findModel(source.name)??{spec:JSON.parse(raw),context:[],source:entry.source};
    else {
      projected=await findModel(source.name);
      if(!projected) {continue;}
      if(/id=["'](?:life\d|lifeline-)|shape=umlLifeline/.test(raw)&&projected.spec.kind!=='sequence') {
        entry.uncertainty.push('Sequence layout detected, but recovered model does not preserve typed ordered steps/activations/fragments.');continue;
      }
      // Compare direct endpoints without guessing card/activation aliases.
      const sequence=projected.spec.kind==='sequence';
      const endpointId=id=>normalize(sequence?id.replace(/^activation\d+-/,''):id);
      const endpoints=[...raw.matchAll(/<mxCell\b([^>]*\bedge=["']1["'][^>]*)>/g)].map(m=>{
        const from=m[1].match(/\bsource=["']([^"']+)["']/)?.[1],to=m[1].match(/\btarget=["']([^"']+)["']/)?.[1];
        return from&&to?`${endpointId(from)}->${endpointId(to)}`:null;
      }).filter(Boolean);
      const expected=(sequence?projected.spec.steps.filter(s=>s.type==='message'):projected.spec.edges??[])
        .map(e=>`${normalize(e.from)}->${normalize(e.to)}`);
      if(!sequence){endpoints.sort();expected.sort();}
      if(JSON.stringify(endpoints)!==JSON.stringify(expected)) {
        entry.uncertainty.push('Recovered semantic model and public XML endpoints differ; do not replace until reconciled.');
        entry.endpoint_comparison={public:endpoints,model:expected};
      }
      if(projected.context.length) {
        entry.uncertainty.push('Context/constraints require explicit visible-copy or adjacent-prose placement; none is silently discarded.');
        entry.context=projected.context;
      }
    }
    entry.model_source=projected.source;
    entry.context=projected.context;
    if(projected.context.length&&!entry.uncertainty.some(s=>s.startsWith('Context/constraints')))entry.uncertainty.push(
      'Context/constraints require explicit visible-copy or adjacent-prose placement; none is silently discarded.');
    const draftPath=path.join(root,'docs','diagrams','reviews',source.name,`${source.name}-pass-01.drawio`);
    const geometrySource=source.kind==='drawio'?source.path:existsSync(draftPath)?draftPath:null;
    if(geometrySource&&projected.spec.nodes) {
      const recovery=spawnSync(process.env.PYTHON??'python',['-B',path.join(root,'scripts','docs','recover-drawio-semantics.py'),geometrySource],
        {input:JSON.stringify(projected.spec.nodes),encoding:'utf8'});
      if(recovery.status!==0)throw new Error(recovery.stderr);
      const semantic=JSON.parse(recovery.stdout);
      projected.sourceGeometry=semantic;
      entry.explicit_bindings=semantic.bindings;
      entry.preserved_visible_copy=semantic.visible_copy;
      if(source.kind==='drawio'&&!semantic.unbound_edges.length && semantic.model.edges.length && !workflows.has(source.name)) {
        const pairs=edges=>edges.map(e=>`${e.from}->${e.to}`).sort().join('|');
        if(pairs(projected.spec.edges)!==pairs(semantic.model.edges))projected.spec.edges=semantic.model.edges;
        entry.topology_resolution='Preserved direct endpoints, labels, revision styles and bidirectionality from final grounded Astra XML.';
        entry.uncertainty=entry.uncertainty.filter(s=>!s.startsWith('Recovered semantic model and public XML endpoints differ'));
      }
      for(const node of projected.spec.nodes) {
        if(!workflows.has(source.name)) {
          if(semantic.native_shapes[node.id])node.shape=semantic.native_shapes[node.id];
          if(semantic.native_styles[node.id])node.nativeStyle=semantic.native_styles[node.id];
          if(semantic.tones[node.id])node.badge={...node.badge,tone:semantic.tones[node.id]};
        }
      }
      if(semantic.groups.length&&!workflows.has(source.name)) {
        projected.spec.groups=semantic.groups.map(({id,label,parent})=>({id,label,parent}));
        for(const node of projected.spec.nodes)node.group=semantic.membership[node.id];
      }
      entry.recovered_groups=semantic.groups;
      entry.junction_expansion=semantic.junction_expansion;
    }
    entry.extended_context=[...(entry.context??[])];
    for(const node of projected.spec.nodes??projected.spec.participants??[]) {
      if(node.meta&&String(node.meta).length>38) {
        entry.extended_context.push({node:node.id,field:'meta',value:node.meta});delete node.meta;
      }
      if(node.subLabel&&cardContentMetrics(node).content>96) {
        entry.extended_context.push({node:node.id,field:'subtitle',value:node.subLabel});delete node.subLabel;
      }
    }
    if(projected.spec.nodes) {
      delete projected.spec.routing;
      projected.spec.layoutSnapshot=null;
      if(source.name==='canonical-default-workflow')projected.spec.layoutSnapshot=defaultWorkflowLayout(projected.spec);
      else if(projected.sourceGeometry) {
        try {projected.spec.layoutSnapshot=fitSourceLayout(projected.spec,projected.sourceGeometry);}
        catch(error) {
          entry.route_correction_reason=error.message;
          try {projected.spec.layoutSnapshot=fitSourceLayout(projected.spec,projected.sourceGeometry,{reroute:true});}
          catch(routeError) {
            entry.layout_recovery_error=routeError.message;
            projected.spec.layoutSnapshot=await createElkLayout(projected.spec);
          }
        }
      }
      projected.spec.layoutSnapshot??=await createElkLayout(projected.spec);
    }
    let xml=specToDrawio(projected.spec,{name:source.name});
    let checked=inspectFluentSource(xml);
    if(checked.errors.length&&projected.sourceGeometry&&projected.spec.nodes&&source.name!=='canonical-default-workflow') {
      entry.layout_attempts=[{strategy:'preserved-geometry',errors:checked.errors}];
      try {
        const alternative={...projected.spec,layoutSnapshot:fitSourceLayout(projected.spec,projected.sourceGeometry,{reroute:true})};
        const alternateXml=specToDrawio(alternative,{name:source.name}),validation=inspectFluentSource(alternateXml);
        entry.layout_attempts.push({strategy:'preserved-placement-separated-ports',errors:validation.errors});
        if(validation.errors.length<checked.errors.length) {
          projected.spec=alternative;xml=alternateXml;checked=validation;
        }
      } catch(error){entry.layout_attempts.push({strategy:'preserved-placement-separated-ports',error:error.message});}
      for(const [routeOrder,extraGap] of [['returns-first',0],['source',0],['reverse',0],['source',64],['reverse',96]]) {
        if(!checked.errors.length)break;
        try {
          const alternative={...projected.spec,layoutSnapshot:fitSourceLayout(projected.spec,projected.sourceGeometry,
            {reroute:true,compact:true,routeOrder,extraGap})};
          const alternateXml=specToDrawio(alternative,{name:source.name}),validation=inspectFluentSource(alternateXml);
          entry.layout_attempts.push({strategy:`compact-source-${routeOrder}-${extraGap}`,errors:validation.errors});
          if(validation.errors.length<checked.errors.length) {
            projected.spec=alternative;xml=alternateXml;checked=validation;
          }
        } catch(error){entry.layout_attempts.push({strategy:`compact-source-${routeOrder}`,error:error.message});}
      }
      if(checked.errors.length) {
        try {
          const alternative={...projected.spec,layoutSnapshot:await createElkLayout(projected.spec)};
          const alternateXml=specToDrawio(alternative,{name:source.name}),validation=inspectFluentSource(alternateXml);
          entry.layout_attempts.push({strategy:'elk-preserved-hierarchy',errors:validation.errors});
          if(validation.errors.length<checked.errors.length) {
            projected.spec=alternative;xml=alternateXml;checked=validation;
          }
        } catch(error){entry.layout_attempts.push({strategy:'elk-preserved-hierarchy',error:error.message});}
      }
    }
    const destination=path.join(out,'candidates',`${source.name}.drawio`);
    await writeFile(destination,xml);
    await writeFile(path.join(out,'candidates',`${source.name}.model.json`),JSON.stringify(projected.spec,null,2)+'\n');
    const validation=path.join(out,'candidates',`${source.name}.validation.json`);
    await writeFile(validation,JSON.stringify(checked,null,2)+'\n');
    const result=checked;
    entry.style_errors=result.errors;
    entry.uncertainty.push(...result.uncertainty);
    entry.candidate=path.relative(root,destination);
    entry.status=entry.uncertainty.length||result.errors.length?'blocked-review':'ready-for-visual-inspection';
  } catch(error) {
    entry.status='blocked-recovery';
    entry.error=error.message;
  }
}
for(const entry of prior.entries)if(entry.status==='retired-merged'&&!report.entries.some(e=>e.name===entry.name))report.entries.push(entry);
for(const entry of report.entries) entry.status??='blocked-recovery';
report.summary=Object.fromEntries([...new Set(report.entries.map(e=>e.status))].map(s=>[s,report.entries.filter(e=>e.status===s).length]));
report.status=report.entries.every(e=>e.status==='ready-for-visual-inspection')?'ready-for-visual-inspection':'blocked-review';
await writeFile(path.join(out,'repair-plan.json'),JSON.stringify(report,null,2)+'\n');
console.log(JSON.stringify(report.summary,null,2));
console.log('No public sources, PNGs, hashes, consumers or global audit files were written.');
