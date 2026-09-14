import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { graphSpecToDrawio } from './drawio-generator.mjs';

const script=fileURLToPath(new URL('./normalize-drawio.py',import.meta.url));
const sample=()=>graphSpecToDrawio({
  title:'Contract',nodes:[{id:'a',label:'Agent',icon:'bot',badge:{tone:'green',text:'Runtime'}}],edges:[],
});
async function inspect(xml,normalize=false) {
  const directory=await mkdtemp(path.join(os.tmpdir(),'fluent-contract-'));
  try {
    const source=path.join(directory,'source.drawio'),report=path.join(directory,'report.json'),output=path.join(directory,'output.drawio');
    await writeFile(source,xml);
    const args=['-B',script,source,'--report',report,...(normalize?['--output',output]:[])];
    const process=spawnSync('python',args,{encoding:'utf8'});
    assert.ifError(process.error);
    assert.ok([0,1].includes(process.status),process.stderr);
    const result=JSON.parse(await readFile(report,'utf8'));
    return {...result,output:normalize&&process.status===0?await readFile(output,'utf8'):null};
  } finally {
    await rm(directory,{recursive:true,force:true});
  }
}
test('calibrated editable card passes explicit role/token validation',async()=>{
  assert.equal((await inspect(sample())).status,'passed');
});
test('preserved pilot approval pins every visual and semantic attribute',async()=>{
  const pilot=await readFile(new URL('../../docs/diagrams/src/canonical-coordinator-architecture.drawio',import.meta.url),'utf8');
  assert.equal((await inspect(pilot)).status,'passed');
  for(const changed of [
    pilot.replace('Coordinator architecture','Changed architecture'),
    pilot.replace('x="24"','x="25"'),
    pilot.replace('endArrow=classicThin','endArrow=block'),
    pilot.replace('source="entry"','source="state"'),
    pilot.replace('coordinator-pilot-preserved-v1','unapproved-profile'),
  ]) {
    assert.notEqual(changed,pilot);
    assert.ok((await inspect(changed)).errors.some(e=>e.code==='approved-snapshot-mismatch'));
  }
  assert.equal((await inspect(pilot,true)).output.trim(),pilot.trim());
});
test('other diagrams cannot borrow the preserved pilot approval',async()=>{
  const forged=sample().replace('<mxfile','<mxfile approvedSnapshot="coordinator-pilot-preserved-v1"');
  assert.ok((await inspect(forged)).errors.some(e=>e.code==='approved-snapshot-mismatch'));
});
for(const [name,mutate,code] of [
  ['debug text',s=>s.replace('Agent&lt;/div&gt;','native-libraries: debug&lt;/div&gt;'),'debug-text'],
  ['font family',s=>s.replaceAll('Segoe UI','Arial'),'font-family'],
  ['role size',s=>s.replaceAll('font-size:20px','font-size:44px'),'font-size'],
  ['role weight',s=>s.replaceAll('font-weight:600','font-weight:700'),'font-weight'],
  ['card width',s=>s.replace('width="340"','width="500"'),'card-geometry'],
  ['card inset',s=>s.replace('x="71"','x="15"'),'card-padding'],
  ['card radius',s=>s.replaceAll('arcSize=32','arcSize=64'),'card-surface'],
  ['accent',s=>s.replace('x="5"','x="12"'),'accent-width'],
  ['elevation',s=>s.replace('opacity=2','opacity=25'),'shadow'],
  ['semantic color',s=>s.replace('fillColor=#0e700e','fillColor=#ff0000'),'semantic-color'],
  ['prose',s=>s.replace('Agent&lt;/div&gt;',`${'paragraph '.repeat(30)}&lt;/div&gt;`),'prose-block'],
  ['icon normalization',s=>s.replaceAll('width="28"','width="90"'),'unstyled-symbol'],
  ['icon alignment',s=>s.replace('x="27"','x="32"'),'icon-alignment'],
  ['missing elevation',s=>s.replace(/<mxCell id="node-a-shadow-0"[\s\S]*?<\/mxCell>/,''),'card-anatomy'],
  ['density',s=>s.replace(/(fluentRole="canvas"[\s\S]*?width=")[^"]+/,(_,prefix)=>prefix+'10000'),'weak-density'],
]) {
  test(`rejects ${name}`,async()=>{
    const result=await inspect(mutate(sample()));
    assert.ok(result.errors.some(e=>e.code===code),JSON.stringify(result));
  });
}
test('unknown handwritten roles are preserved rather than guessed',async()=>{
  const xml=sample().replaceAll(/ fluentRole="[^"]+"/g,'');
  const result=await inspect(xml,true);
  assert.equal(result.status,'blocked');
  assert.ok(result.uncertainty.length);
  assert.equal(result.output,null);
});
test('normalization is deterministic and idempotent without changing semantic text',async()=>{
  const original=sample().replaceAll('Segoe UI','Arial').replaceAll('font-size:20px','font-size:40px');
  const first=await inspect(original,true);
  assert.equal(first.status,'passed');
  const second=await inspect(first.output,true);
  assert.equal(first.output,second.output);
  assert.match(second.output,/Agent/);
  assert.match(second.output,/style="group;/);
  assert.match(second.output,/style="text;/);
});
test('badge data does not create unsolicited visual pills',()=>{
  assert.doesNotMatch(sample(),/fluentRole="badge"/);
});

function routeFixture(routes,dot) {
  const anchors=new Map();
  const id=point=>`point-${point.join('-')}`;
  const edges=routes.map((route,index)=>{
    for(const point of [route[0],route.at(-1)]) anchors.set(id(point),point);
    return `<mxCell id="e${index}" fluentRole="connector" edge="1" parent="1"
      source="${id(route[0])}" target="${id(route.at(-1))}"
      style="edgeStyle=none;endArrow=classicThin;endSize=8;jumpStyle=arc;">
      <mxGeometry relative="1" as="geometry"><Array as="points">${route.slice(1,-1)
        .map(([x,y])=>`<mxPoint x="${x}" y="${y}"/>`).join('')}</Array></mxGeometry></mxCell>`;
  });
  return `<mxfile><diagram><mxGraphModel pageWidth="794" pageHeight="559"><root>
    <mxCell id="0"/><mxCell id="1" parent="0"/>
    ${[...anchors].map(([key,[x,y]])=>`<mxCell id="${key}" fluentRole="anchor" vertex="1" parent="1">
      <mxGeometry x="${x-.5}" y="${y-.5}" width="1" height="1" as="geometry"/></mxCell>`).join('')}
    ${edges.join('')}
    ${dot?`<mxCell id="junction" fluentRole="junction" vertex="1" parent="1" style="ellipse;">
      <mxGeometry x="${dot[0]-2.5}" y="${dot[1]-2.5}" width="5" height="5" as="geometry"/></mxCell>`:''}
    </root></mxGraphModel></diagram></mxfile>`;
}
test('accepts a true shared split and rejects its missing junction',async()=>{
  const routes=[[[20,20],[20,80],[150,80]],[[20,20],[20,80],[20,160]]];
  assert.equal((await inspect(routeFixture(routes,[20,80]))).status,'passed');
  assert.ok((await inspect(routeFixture(routes))).errors.some(e=>e.code==='missing-junction'));
});
for(const [name,xml,code] of [
  ['false junction',routeFixture([[[20,80],[150,80]]],[70,80]),'false-junction'],
  ['double arrowheads',routeFixture([[[20,80],[150,80]],[[150,20],[150,80]]]),'double-arrowhead'],
  ['opposed shared stubs',routeFixture([[[20,80],[150,80]],[[150,80],[20,80]]]),'opposed-trunks'],
  ['diagonal connector',routeFixture([[[20,20],[150,80]]]),'non-orthogonal'],
  ['missing bridges',routeFixture([[[20,80],[150,80]],[[80,20],[80,160]]]).replaceAll('jumpStyle=arc','jumpStyle=none'),'missing-bridges'],
  ['bridge too near elbow',routeFixture([[[50,80],[150,80]],[[60,20],[60,160]]]),'ambiguous-crossing'],
]) test(`rejects ${name}`,async()=>{
  assert.ok((await inspect(xml)).errors.some(e=>e.code===code),code);
});
test('rejects an edge label without a matching surface background',async()=>{
  const xml=graphSpecToDrawio({title:'Labels',nodes:[{id:'a',label:'A'},{id:'b',label:'B'}],
    edges:[{from:'a',to:'b',label:'Send'}]}).replace(/(fluentRole="edge-label"[^>]*style="[^"]*)fillColor=#fdfbf8/,'$1fillColor=none');
  assert.ok((await inspect(xml)).errors.some(e=>e.code==='label-background'));
});
test('invalid XML returns an explicit machine-readable failure',async()=>{
  assert.equal((await inspect('<mxfile>')).errors[0].code,'invalid-xml');
});
test('structured metadata cannot leak as object text onto the canvas',()=>{
  assert.throws(()=>graphSpecToDrawio({title:'Invalid copy',nodes:[{id:'a',label:'A',meta:{constraint:'Preserve'}}],edges:[]}),/must be text/);
});
test('canonical graph has A5 paper with explicit print scaling',async()=>{
  const xml=sample();
  assert.match(xml,/pageWidth="794" pageHeight="559"/);
  assert.match(xml,/fluentRole="canvas"/);
  assert.ok((await inspect(xml.replace('pageWidth="794"','pageWidth="900"'))).errors.some(e=>e.code==='paper-size'));
});
test('ordinary graph markers consume the approved type and size',async()=>{
  const xml=graphSpecToDrawio({nodes:[{id:'a',label:'A'},{id:'b',label:'B'}],edges:[{from:'a',to:'b'}]});
  assert.match(xml,/endArrow=classicThin/);
  assert.match(xml,/endSize=8/);
  const report=await inspect(xml.replace('endArrow=classicThin','endArrow=block'));
  assert.ok(report.errors.some(e=>e.code==='arrow-token'));
});
