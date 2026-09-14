import assert from 'node:assert/strict';
import { mkdtemp,readFile,writeFile,rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { parse } from 'yaml';
import { graphSpecToDrawio } from './drawio-generator.mjs';
import { inspectFluentSource } from './fluent-validation.mjs';
import { groupCaptionMetrics } from './fluent-group-label.mjs';
import { fitSourceLayout } from './fluent-source-layout.mjs';
import { defaultWorkflowLayout } from './default-workflow-layout.mjs';
import { toSpec } from './workflows-to-graphspec.mjs';

test('native Kubernetes glyphs retain a visible resource inside the Fluent footprint',()=>{
  const xml=graphSpecToDrawio({nodes:[{id:'pod',label:'Pod',library:'kubernetes',shape:'mxgraph.kubernetes.pod'}],edges:[]});
  assert.match(xml,/shape=mxgraph.kubernetes.icon2;prIcon=pod;kubernetesLabel=0/);
  assert.match(xml,/fillColor=#fdfbf8;strokeColor=#635c57/);
  assert.equal(inspectFluentSource(xml).status,'passed');
});

test('group caption wrapping is explicit and reserves its actual text height',()=>{
  const caption=groupCaptionMetrics('AUTHORED CHECKS AND HUMAN WAIT',356);
  assert.equal(caption.text,'AUTHORED CHECKS AND\nHUMAN WAIT');
  assert.ok(caption.width<=356);
  assert.equal(caption.height,46);
  const spec={nodes:[{id:'a',label:'A',group:'g'},{id:'b',label:'B',group:'g'}],
    edges:[],groups:[{id:'g',label:'AUTHORED CHECKS AND HUMAN WAIT'}]};
  const semantic={node_boxes:{a:[0,80,340,104],b:[500,80,340,104]},edge_geometry:[],
    groups:[{id:'g',label:spec.groups[0].label,members:['a','b'],bounds:[0,0,900,250]}],page:{width:1000,height:400}};
  spec.layoutSnapshot=fitSourceLayout(spec,semantic);
  const xml=graphSpecToDrawio(spec);
  assert.match(xml,/white-space:pre/);
  assert.equal(inspectFluentSource(xml).status,'passed');
});

test('A5 captions cannot overlap cards or connectors',()=>{
  const xml=graphSpecToDrawio({nodes:[{id:'a',label:'A'}],edges:[]});
  const caption='<mxCell id="bad-caption" fluentRole="group-label" vertex="1" parent="node-a" value="Caption" style="fontFamily=Segoe UI;fontSize=20;"><mxGeometry x="10" y="10" width="200" height="28" as="geometry"/></mxCell>';
  assert.ok(inspectFluentSource(xml.replace('</root>',caption+'</root>')).errors.some(e=>e.code==='label-collision'));
});

test('source recovery expands true bus junctions with a complete edge provenance path',async()=>{
  const dir=await mkdtemp(path.join(os.tmpdir(),'fluent-recovery-'));
  try {
    const card=(id,x,y)=>`<mxCell id="${id}" vertex="1" parent="1"><mxGeometry x="${x}" y="${y}" width="100" height="50" as="geometry"/></mxCell><mxCell id="${id}-title" value="${id}"/><mxCell id="${id}-accent"/>`;
    const dot=id=>`<mxCell id="${id}" vertex="1" parent="1" style="ellipse;"><mxGeometry x="150" y="150" width="5" height="5" as="geometry"/></mxCell>`;
    const edge=(id,a,b)=>`<mxCell id="${id}" edge="1" source="${a}" target="${b}" parent="1"><mxGeometry as="geometry"/></mxCell>`;
    const xml=`<mxfile><diagram><mxGraphModel pageWidth="794" pageHeight="559"><root><mxCell id="0"/><mxCell id="1" parent="0"/>
      ${card('api',0,0)}${card('worker',120,0)}${card('database',0,300)}${card('files',120,300)}
      ${dot('caller-junction')}${dot('access-junction')}
      ${edge('api-access','api','caller-junction')}${edge('worker-access','worker','caller-junction')}
      ${edge('shared','caller-junction','access-junction')}${edge('database-access','access-junction','database')}
      ${edge('files-access','access-junction','files')}</root></mxGraphModel></diagram></mxfile>`;
    const source=path.join(dir,'source.drawio');await writeFile(source,xml);
    const process=spawnSync('python',['-B',fileURLToPath(new URL('./recover-drawio-semantics.py',import.meta.url)),source],{input:'[]',encoding:'utf8'});
    assert.equal(process.status,0,process.stderr);
    const recovered=JSON.parse(process.stdout);
    assert.equal(recovered.model.nodes.length,4);
    assert.equal(recovered.model.edges.length,4);
    assert.deepEqual(recovered.unbound_edges,[]);
    assert.ok(recovered.model.edges.every(e=>e.source_edges.length===3));
    assert.equal(new Set(recovered.model.edges.flatMap(e=>e.source_edges)).size,5);
  } finally {await rm(dir,{recursive:true,force:true});}
});

test('default layout retains all live transitions including PR publication',async()=>{
  const text=await readFile(new URL('../../apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs',import.meta.url),'utf8');
  const yaml=text.match(/public const string Yaml\s*=\s*"""([\s\S]*?)"""/)[1];
  const spec=toSpec(parse(yaml.split('\n').map(line=>line.replace(/^ {8}/,'')).join('\n')));
  const layout=defaultWorkflowLayout(spec);
  assert.equal(layout.edges.length,spec.edges.length);
  assert.ok(layout.edges.some(e=>e.source==='merge'&&e.target==='push-pr'));
  assert.ok(layout.edges.some(e=>e.source==='push-pr'&&e.target==='scribe'));
});

test('annotation leaders are editable free-endpoint edges, not zero-width invisible vertices',()=>{
  const spec={nodes:[{id:'a',label:'A'},{id:'b',label:'B'}],edges:[{from:'a',to:'b',label:'call'}]};
  spec.layoutSnapshot={nodes:spec.nodes.map((n,i)=>({id:n.id,type:'card',position:{x:100+i*500,y:100},data:n})),
    edges:[{id:'e0',source:'a',target:'b',markerEnd:true,label:'call',style:{},data:{
      points:[{x:440,y:152},{x:600,y:152}],labelPos:{x:520,y:230},
      labelLeader:{from:{x:520,y:152},to:{x:520,y:214}},junctions:[]}}],
    canvasWidth:1040,canvasHeight:350};
  const xml=graphSpecToDrawio(spec);
  assert.match(xml,/<mxCell id="e0-leader"[^>]*edge="1"/);
  assert.match(xml,/as="sourcePoint"/);
  assert.match(xml,/as="targetPoint"/);
  assert.equal(inspectFluentSource(xml).status,'passed');
});
