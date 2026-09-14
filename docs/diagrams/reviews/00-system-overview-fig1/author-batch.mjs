import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { drawioExportArgs, verifyDrawioVersion, createDiagramStamp } from '../../../../scripts/docs/diagram-sources.mjs';
import { models } from './batch-models.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '../../../..');
const cli = path.join(repo, 'docs/diagrams/reviews/canonical-api-host/renderer/desktop/draw.io.exe');
const [mode, selector = 'all'] = process.argv.slice(2);
const selected = models.filter(m => selector === 'all' || selector.split(',').includes(m.name));
if (!selected.length) throw Error('Unknown owned name');
const plan = JSON.parse(await fs.readFile(path.join(repo, '.github/skills/docs-diagram-audit/reports/plan-deep-dive-core.json')));
const allowed = plan.exclusive_asset_paths;
function owned(file) {
  const rel = path.relative(repo, file).replaceAll('\\', '/');
  if (!allowed.some(p => p.endsWith('/') ? rel.startsWith(p) : rel === p)) throw Error(`Unowned path: ${file}`);
}
async function write(file, contents, exclusive = false) {
  owned(file);
  await fs.mkdir(path.dirname(file), { recursive: true });
  await fs.writeFile(file, contents, exclusive ? { flag: 'wx' } : {});
}
const template = await fs.readFile(path.join(repo,'docs/diagrams/drawio/fluent-template.drawio'),'utf8');
const library = await fs.readFile(path.join(repo,'docs/diagrams/drawio/fluent-library.xml'),'utf8');
if (!template.includes('#efeae7') || !library.includes('Agentweaver Fluent card')) throw Error('Design assets changed');
const version = verifyDrawioVersion({ command:cli, prefixArgs:[`--user-data-dir=${path.join(here,'desktop-profile')}`] });
const esc = v => String(v).replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;');
const tones = [['#d2ccf8','#3f3682'],['#a6e9ed','#00666d'],['#9fd89f','#0e700e'],['#f9e2ae','#835b00']];
function source(m, pitch) {
  const cells = [], positions = {};
  const vertex = (id,value,style,x,y,w,h,parent='1') => cells.push(`<mxCell id="${id}" value="${esc(value)}" style="${style}" vertex="1" parent="${parent}"><mxGeometry x="${x}" y="${y}" width="${w}" height="${h}" as="geometry"/></mxCell>`);
  const text = (id,value,x,y,w,h,size=12,color='#272320',bold=false,parent='1') =>
    vertex(id,value,`text;html=1;whiteSpace=wrap;fillColor=none;strokeColor=none;fontFamily=Segoe UI;fontSize=${size};fontColor=${color};fontStyle=${bold?1:0};align=left;verticalAlign=middle;spacing=0;`,x,y,w,h,parent);
  text('title',m.title,28,16,772,39,23,'#272320',true);
  if (pitch) {
    m.pitch.forEach((p,i) => {
      const [title,sub,meta] = p.split('|'), id=`pitch-${i}`;
      vertex(id,`<b>${title}</b><br>${sub}<br><span style="font-size:11px;color:#746d68">${meta}</span>`,
        'rounded=1;arcSize=16;html=1;whiteSpace=wrap;fillColor=#fdfbf8;strokeColor=#e2ddd9;shadow=1;fontFamily=Segoe UI;fontSize=18;fontColor=#272320;spacingLeft=48;spacingRight=8;align=left;',42+i*414,226,326,135);
      vertex(id+'-accent','','fillColor=#00666d;strokeColor=none;',0,0,5,135,id);
      vertex(id+'-icon','','shape=process;fillColor=#a6e9ed;strokeColor=#00666d;strokeWidth=1.5;',14,55,25,25,id);
      vertex(id+'-badge',i?'RESULT':'SCOPE','rounded=1;arcSize=100;fillColor=#a6e9ed;strokeColor=none;fontFamily=Segoe UI;fontSize=9;fontColor=#00666d;',242,9,70,18,id);
    });
  } else {
    text('takeaway',m.takeaway,28,57,770,28,12,'#635c57');
    const byColumn = ['data-persistence-fig1','canonical-testing-boundary'].includes(m.name);
    for(let i=0;i<2;i++) {
      vertex(`group-${i}`,'','rounded=1;arcSize=16;fillColor='+ (i?'#f8f4f1':'#e7e1dc')+';strokeColor=#e2ddd9;strokeWidth=1;',
        byColumn?20+i*414:20,byColumn?92:92+i*220,byColumn?393:787,byColumn?443:218);
      text(`group-${i}-title`,m.groups[i],byColumn?34+i*414:34,byColumn?96:96+i*220,370,18,10,'#635c57',true);
    }
    for(const [i,n] of m.nodes.entries()) {
      const x=36+(i%2)*414,y=[118,217,338,437][Math.floor(i/2)],w=343,h=87;
      positions[n.id]={x,y,w,h};
      const [bg,fg]=tones[Math.floor(i/2)];
      vertex(n.id,'','rounded=1;absoluteArcSize=1;arcSize=16;fillColor=#fdfbf8;strokeColor=#ece7e3;strokeWidth=1;shadow=1;',x,y,w,h);
      vertex(n.id+'-accent','',`fillColor=${fg};strokeColor=none;`,0,0,5,h,n.id);
      vertex(n.id+'-icon','',`shape=${n.shape};${n.shape==='cylinder3'?'size=6;boundedLbl=1;backgroundOutline=1;':''}fillColor=${bg};strokeColor=${fg};strokeWidth=1.5;`,14,12,25,25,n.id);
      text(n.id+'-title',n.title,48,7,w-59,25,16,'#272320',true,n.id);
      text(n.id+'-subtitle',n.sub,48,31,w-59,18,11.5,'#635c57',false,n.id);
      vertex(n.id+'-divider','','fillColor=#ece7e3;strokeColor=none;',14,52,w-28,1,n.id);
      text(n.id+'-detail',n.detail,14,54,w-28,16,10.5,'#3f3935',false,n.id);
      vertex(n.id+'-metadata',n.meta,'text;html=1;whiteSpace=wrap;fillColor=none;strokeColor=none;fontFamily=Cascadia Code;fontSize=8.5;fontColor=#746d68;align=left;verticalAlign=middle;spacing=0;',14,71,w-106,13,n.id);
      vertex(n.id+'-badge',n.badge,`rounded=1;arcSize=100;fillColor=${bg};strokeColor=none;fontFamily=Segoe UI;fontSize=8;fontStyle=1;fontColor=${fg};`,w-88,71,74,13,n.id);
    }
    let outsideLeft=0,outsideRight=0,crossLane=0;
    for(const edge of m.edges.filter(e=>!e.relationOnly)) {
      const a=positions[edge.source], b=positions[edge.target];
      let anchors='', points=[];
      if(a.y===b.y) {
        const forward=b.x>a.x;
        anchors=`exitX=${forward?1:0};exitY=0.5;entryX=${forward?0:1};entryY=0.5;`;
      } else if(a.x===b.x && Math.abs(a.y-b.y)<125 && !edge.route) {
        anchors=`exitX=0.5;exitY=${b.y>a.y?1:0};entryX=0.5;entryY=${b.y>a.y?0:1};`;
      } else if(a.x!==b.x) {
        const lane=[392,412,432][crossLane++%3];
        anchors=`exitX=${a.x<400?1:0};exitY=0.72;entryX=${b.x<400?1:0};entryY=0.72;`;
        points=[[lane,a.y+a.h*.72],[lane,b.y+b.h*.72]];
      } else {
        let lane;
        if(a.x<400) {
          lane=26-(outsideLeft++*5); anchors='exitX=0;exitY=0.5;entryX=0;entryY=0.5;';
          points=[[lane,a.y+a.h/2],[lane,b.y+b.h/2]];
        } else {
          lane=799+(outsideRight++*5); anchors='exitX=1;exitY=0.5;entryX=1;entryY=0.5;';
          points=[[lane,a.y+a.h/2],[lane,b.y+b.h/2]];
        }
      }
      const label = a.x===b.x && Math.abs(a.y-b.y)<125 ? edge.label.split(' ')[0] : edge.label.replace(/^(.{8,14}) /,'$1<br>');
      cells.push(`<mxCell id="${edge.id}" value="${esc(label)}" style="edgeStyle=orthogonalEdgeStyle;rounded=1;html=1;endArrow=block;endFill=1;strokeColor=${edge.returning?'#d39300':'#746d68'};strokeWidth=1.4;fontFamily=Segoe UI;fontSize=9;fontColor=#3f3935;labelBackgroundColor=#fdfbf8;labelBorderColor=none;orthogonalLoop=1;jettySize=auto;jumpStyle=arc;jumpSize=6;${edge.returning?'dashed=1;dashPattern=8 6;':''}${anchors}" edge="1" parent="1" source="${edge.source}" target="${edge.target}"><mxGeometry relative="1" as="geometry">${points.length?`<Array as="points">${points.map(([x,y])=>`<mxPoint x="${x}" y="${y}"/>`).join('')}</Array>`:''}</mxGeometry></mxCell>`);
    }
    if(m.name==='testing-strategy-fig1') {
      vertex('prerequisite-symbol','','shape=note;fillColor=#f9e2ae;strokeColor=#835b00;strokeWidth=1;',28,538,18,20);
      text('prerequisites','Prerequisites differ: .NET / Node locally; Docker for Postgres; a configured live target for staging.',53,537,740,23,11,'#3f3935');
    }
    text('scope',m.scope,28,560,771,19,10,'#635c57');
  }
  return `<?xml version="1.0" encoding="UTF-8"?>\n<mxfile host="Agentweaver" version="31.4.5" compressed="false"><diagram id="${m.name}" name="${esc(m.title)}"><mxGraphModel page="1" pageScale="1" pageWidth="827" pageHeight="583" background="#efeae7" grid="1" gridSize="10"><root><mxCell id="0"/><mxCell id="1" parent="0"/>${cells.join('\n')}</root></mxGraphModel></diagram></mxfile>\n`;
}
for(const m of selected) {
  const dir=path.join(repo,'docs/diagrams/reviews',m.name);
  await fs.mkdir(dir,{recursive:true});
  const stage=mode==='pitch'?'pitch':`pass-${String(Number(mode)).padStart(2,'0')}`;
  if(mode==='pitch'||mode==='1') {
    await write(path.join(dir,`${m.name}-${stage}.drawio`),source(m,mode==='pitch'),true);
    await write(path.join(dir,'content-model.json'),JSON.stringify(m,null,2)+'\n');
  } else if(/^[2345]$/.test(mode)) {
    const prior=path.join(dir,`${m.name}-pass-${String(Number(mode)-1).padStart(2,'0')}.drawio`);
    await write(path.join(dir,`${m.name}-${stage}.drawio`),await fs.readFile(prior),true);
    if(mode==='2') execFileSync('python',[path.join(here,'correct.py'),path.join(dir,`${m.name}-${stage}.drawio`)],{cwd:repo,stdio:'inherit'});
    if(mode==='3') execFileSync('python',[path.join(here,'correct-three.py'),path.join(dir,`${m.name}-${stage}.drawio`)],{cwd:repo,stdio:'inherit'});
    if(mode==='4') execFileSync('python',[path.join(here,'correct-four.py'),path.join(dir,`${m.name}-${stage}.drawio`)],{cwd:repo,stdio:'inherit'});
    if(mode==='5') execFileSync('python',[path.join(here,'correct-five.py'),path.join(dir,`${m.name}-${stage}.drawio`)],{cwd:repo,stdio:'inherit'});
  } else if(mode==='promote') {
    const manifest=JSON.parse(await fs.readFile(path.join(dir,'iteration-manifest.json')));
    execFileSync('python',['.github\\skills\\docs-diagram-iterate\\scripts\\validate_iteration_manifest.py',path.join(dir,'iteration-manifest.json')],{cwd:repo,stdio:'inherit'});
    const final=manifest.passes.at(-1);
    const src=path.join(repo,'docs/diagrams/src',`${m.name}.drawio`);
    const json=path.join(repo,'docs/diagrams/src',`${m.name}.json`);
    if(await fs.stat(json).catch(()=>false)) {
      await write(path.join(dir,'legacy-source.json'),await fs.readFile(json));
      owned(json); await fs.unlink(json);
    }
    await write(src,await fs.readFile(path.join(dir,final.drawio)));
    const generated=path.join(repo,'docs/diagrams/drawio/generated',`${m.name}.drawio`);
    await write(generated,await fs.readFile(src));
    const png=path.join(repo,'docs/diagrams',`${m.name}.png`);
    await write(png,await fs.readFile(path.join(dir,final.png)));
    const stamp=await createDiagramStamp({name:m.name,kind:'drawio',path:src},generated,png,{rendererVersion:version});
    await write(path.join(repo,'docs/diagrams',`${m.name}.hash.txt`),JSON.stringify(stamp,null,2)+'\n');
    continue;
  } else throw Error('Modes: pitch, 1, 2, 3, 4, promote');
  const drawio=path.join(dir,`${m.name}-${stage}.drawio`), png=path.join(dir,`${m.name}-${stage}.png`);
  execFileSync(cli,[`--user-data-dir=${path.join(dir,'desktop-profile')}`,...drawioExportArgs(drawio,png,'png')],{cwd:repo,stdio:'pipe',timeout:90000});
  if(!(await fs.stat(png)).size) throw Error('Missing PNG');
  execFileSync('python',[path.join(here,'proof.py'),png],{cwd:repo,stdio:'inherit'});
  console.log(`${m.name} ${stage}: exported; pending actual inspection`);
}
