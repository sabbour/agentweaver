import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { models } from './batch-models.mjs';
import { drawioExportArgs, verifyDrawioVersion, createDiagramStamp } from '../../../../scripts/docs/diagram-sources.mjs';
const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '../../../..');
const cli = path.join(repo, 'docs/diagrams/reviews/canonical-api-host/renderer/desktop/draw.io.exe');
const profile = path.join(here, 'desktop-profile');
const version = verifyDrawioVersion({command:cli,prefixArgs:[`--user-data-dir=${profile}`]});
if (version !== '31.4.5') throw new Error('Pinned Desktop version mismatch');
const template = fs.readFileSync(path.join(repo,'docs/diagrams/drawio/fluent-template.drawio'),'utf8');
const library = fs.readFileSync(path.join(repo,'docs/diagrams/drawio/fluent-library.xml'),'utf8');
if (!template.includes('mxGraphModel') || !library.includes('Agentweaver Fluent card')) throw new Error('Design assets changed');
const mode = process.argv[2];
const selected = process.argv.slice(3);
const esc = s => String(s).replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;');
const palettes = [['#d2ccf8','#3f3682'],['#a6e9ed','#00666d'],['#9fd89f','#0e700e'],['#f9e2ae','#835b00']];
const reviews = name => path.join(repo,'docs/diagrams/reviews',name);
const outfile = (name,pass,ext) => path.join(reviews(name),`${name}-${pass}.${ext}`);
function build(m, detailed) {
  const c = [];
  const v = (id,value,style,x,y,w,h,parent='1') => c.push(`<mxCell id="${id}" value="${esc(value)}" style="${style}" vertex="1" parent="${parent}"><mxGeometry x="${x}" y="${y}" width="${w}" height="${h}" as="geometry"/></mxCell>`);
  const text = (id,value,x,y,w,h,size=11,color='#272320',bold=false,parent='1') =>
    v(id,value,`text;html=1;whiteSpace=wrap;fillColor=none;strokeColor=none;fontFamily=Segoe UI;fontSize=${size};fontColor=${color};fontStyle=${bold?1:0};align=left;verticalAlign=middle;spacing=0;`,x,y,w,h,parent);
  text('title',m.title,28,16,771,30,23,'#272320',true);
  const edge = (id,source,target,label,points=[],anchors='',offset=[0,0]) =>
    c.push(`<mxCell id="${id}" value="${esc(label)}" style="edgeStyle=orthogonalEdgeStyle;rounded=1;html=1;endArrow=block;endFill=1;strokeColor=#746d68;strokeWidth=1.5;fontFamily=Segoe UI;fontSize=10;fontColor=#3f3935;labelBackgroundColor=#fdfbf8;labelBorderColor=none;orthogonalLoop=1;jettySize=auto;jumpStyle=arc;jumpSize=7;${anchors}" edge="1" parent="1" source="${source}" target="${target}"><mxGeometry relative="1" as="geometry">${points.length?`<Array as="points">${points.map(([x,y])=>`<mxPoint x="${x}" y="${y}"/>`).join('')}</Array>`:''}<mxPoint x="${offset[0]}" y="${offset[1]}" as="offset"/></mxGeometry></mxCell>`);
  if (!detailed) {
    for (let i=0;i<2;i++) {
      const node = m.nodes[i];
      v(`pitch-${i}`,`<b>${i ? m.groups[1] : m.groups[0]}</b><br>${node.title}<br><span style="font-size:11px;color:#746d68">${i ? 'BACKEND CONTRACT' : 'SOURCE-BOUND CONTEXT'}</span>`,
        'rounded=1;arcSize=16;html=1;whiteSpace=wrap;fillColor=#fdfbf8;strokeColor=#e2ddd9;shadow=1;fontFamily=Segoe UI;fontSize=16;fontColor=#272320;align=left;spacingLeft=48;spacingRight=12;',
        36+i*431,223,324,132);
      v(`pitch-${i}-accent`,'','rounded=1;fillColor=#00666d;strokeColor=none;',0,0,5,132,`pitch-${i}`);
      v(`pitch-${i}-icon`,'',`shape=${node.shape};fillColor=#a6e9ed;strokeColor=#00666d;strokeWidth=1.5;`,14,50,26,28,`pitch-${i}`);
    }
    // Coarse scope association has no arrowhead: the detailed pass supplies exact directed relations.
    c.push('<mxCell id="pitch-scope" value="related scopes" style="edgeStyle=orthogonalEdgeStyle;rounded=1;endArrow=none;strokeColor=#746d68;fontFamily=Segoe UI;fontSize=11;labelBackgroundColor=#fdfbf8;" edge="1" parent="1" source="pitch-0" target="pitch-1"><mxGeometry relative="1" as="geometry"/></mxCell>');
  } else {
    text('takeaway',m.takeaway,28,47,771,21,12,'#635c57');
    for(let col=0;col<2;col++) {
      v(`group-${col}`,'','rounded=1;absoluteArcSize=1;arcSize=16;fillColor=#f8f4f1;strokeColor=#e2ddd9;',28+col*431,72,340,468);
      text(`group-title-${col}`,m.groups[col],40+col*431,75,316,17,9,'#635c57',true);
    }
    for(const [i,node] of m.nodes.entries()) {
      const x=36+(i%2)*431,y=98+Math.floor(i/2)*110;
      const [bg,fg]=palettes[Math.floor(i/2)];
      node.box={x,y,w:324,h:92};
      v(node.id,'','rounded=1;absoluteArcSize=1;arcSize=16;fillColor=#fdfbf8;strokeColor=#ece7e3;strokeWidth=1;shadow=1;',x,y,324,92);
      v(`${node.id}-accent`,'',`rounded=1;fillColor=${fg};strokeColor=none;`,0,0,5,92,node.id);
      v(`${node.id}-icon`,'',`shape=${node.shape};${node.shape==='cylinder3'?'size=8;boundedLbl=1;backgroundOutline=1;':''}fillColor=${bg};strokeColor=${fg};strokeWidth=1.5;`,14,12,26,26,node.id);
      text(`${node.id}-title`,node.title,49,9,261,22,15,'#272320',true,node.id);
      text(`${node.id}-subtitle`,node.subtitle,49,34,261,19,11,'#635c57',false,node.id);
      v(`${node.id}-divider`,'','fillColor=#ece7e3;strokeColor=none;',14,55,296,1,node.id);
      text(`${node.id}-detail`,node.detail,14,59,296,15,10,'#3f3935',false,node.id);
      v(`${node.id}-meta`,node.meta,'text;html=1;whiteSpace=wrap;fillColor=none;strokeColor=none;fontFamily=Cascadia Code;fontSize=8;fontColor=#746d68;align=left;spacing=0;',14,77,220,13,node.id);
      v(`${node.id}-pill`,node.badge,`rounded=1;arcSize=100;fillColor=${bg};strokeColor=none;fontFamily=Segoe UI;fontSize=8;fontStyle=1;fontColor=${fg};`,239,77,71,13,node.id);
    }
    for(const relation of m.edges) {
      const a=m.nodes.find(n=>n.id===relation.source).box,b=m.nodes.find(n=>n.id===relation.target).box;
      const same=a.x===b.x, adjacent=Math.abs(a.y-b.y)<=110;
      let points=[],anchors='',offset=relation.labelOffset??[0,0];
      const sy=relation.exitY??0.5,ty=relation.entryY??0.5;
      if(same && adjacent && relation.route!=='outer') {
        const down=b.y>a.y;
        anchors=`exitX=0.5;exitY=${down?1:0};entryX=0.5;entryY=${down?0:1};`;
        offset=[Math.max(38,relation.label.length*3+14),0];
      } else if(same) {
        const left=relation.side?relation.side==='left':a.x<400,lane=relation.lane??(left?19:808);
        anchors=`exitX=${left?0:1};exitY=${sy};entryX=${left?0:1};entryY=${ty};`;
        points=[[lane,a.y+a.h*sy],[lane,b.y+b.h*ty]];
      } else if(a.y===b.y) {
        const right=b.x>a.x;
        anchors=`exitX=${right?1:0};exitY=${sy};entryX=${right?0:1};entryY=${ty};`;
      } else {
        const right=b.x>a.x,lane=relation.lane??413;
        anchors=`exitX=${right?1:0};exitY=${sy};entryX=${right?0:1};entryY=${ty};`;
        points=[[lane,a.y+a.h*sy],[lane,b.y+b.h*ty]];
      }
      edge(relation.id,relation.source,relation.target,relation.label,points,anchors,offset);
    }
    text('scope',m.scope,28,550,771,23,10,'#635c57');
  }
  return `<?xml version="1.0" encoding="UTF-8"?>\n<mxfile host="Agentweaver" version="31.4.5" compressed="false"><diagram id="${m.name}" name="${esc(m.title)}"><mxGraphModel page="1" pageScale="1" pageWidth="827" pageHeight="583" background="#efeae7"><root><mxCell id="0"/><mxCell id="1" parent="0"/>${c.join('\n')}</root></mxGraphModel></diagram></mxfile>\n`;
}
for(const m of models.filter(m=>!selected.length||selected.includes(m.name))) {
  fs.mkdirSync(reviews(m.name),{recursive:true});
  if(mode==='pitch'||mode==='pass-01') {
    if(mode==='pass-01' && fs.existsSync(outfile(m.name,mode,'drawio'))) {
      let number=1;
      while(fs.existsSync(outfile(m.name,`pass-01-candidate-${String(number).padStart(2,'0')}`,'drawio'))) number++;
      const candidate=`pass-01-candidate-${String(number).padStart(2,'0')}`;
      for(const ext of ['drawio','png']) fs.renameSync(outfile(m.name,mode,ext),outfile(m.name,candidate,ext));
      fs.renameSync(outfile(m.name,'pass-01-print','png'),outfile(m.name,candidate+'-print','png'));
    }
    fs.writeFileSync(outfile(m.name,mode,'drawio'),build(m,mode!=='pitch'),{flag:'wx'});
    fs.writeFileSync(path.join(reviews(m.name),'content-model.json'),JSON.stringify(m,null,2)+'\n');
  } else if(/^pass-0[234]$/.test(mode)) {
    const prev=`pass-0${Number(mode.at(-1))-1}`;
    fs.copyFileSync(outfile(m.name,prev,'drawio'),outfile(m.name,mode,'drawio'),fs.constants.COPYFILE_EXCL);
    if(mode==='pass-02' && ['frontend-fig2','projects-fig2'].includes(m.name)) {
      let source=fs.readFileSync(outfile(m.name,mode,'drawio'),'utf8');
      if(m.name==='frontend-fig2') {
        source=source.replace(/(<mxCell id="router-to-assets"[\s\S]*?<mxGeometry )relative="1"/,
          '$1x="0.4" relative="1"');
      } else {
        source=source.replace(/<mxCell id="project-to-team"[\s\S]*?<\/mxCell>/,
          cell=>cell.replace('exitY=0.5;','exitY=0.82;').replace('<mxPoint x="397" y="144"/>','<mxPoint x="397" y="173.44"/>'));
      }
      fs.writeFileSync(outfile(m.name,mode,'drawio'),source);
    }
  } else if(mode==='promote') {
    const legacy=path.join(repo,'docs/diagrams/src',m.name+'.json');
    if(fs.existsSync(legacy)) {
      fs.copyFileSync(legacy,path.join(reviews(m.name),'legacy-review.json'));
      fs.unlinkSync(legacy);
    }
    const src=path.join(repo,'docs/diagrams/src',m.name+'.drawio');
    const generated=path.join(repo,'docs/diagrams/drawio/generated',m.name+'.drawio');
    const png=path.join(repo,'docs/diagrams',m.name+'.png');
    fs.copyFileSync(outfile(m.name,'pass-04','drawio'),src);
    fs.copyFileSync(src,generated);
    fs.copyFileSync(outfile(m.name,'pass-04','png'),png);
    const stamp=await createDiagramStamp({path:src,kind:'drawio'},generated,png,{rendererVersion:version});
    fs.writeFileSync(path.join(repo,'docs/diagrams',m.name+'.hash.txt'),JSON.stringify(stamp,null,2)+'\n');
    console.log(`Promoted ${m.name}`);
    continue;
  } else throw new Error('Unsupported mode');
  execFileSync(cli,[`--user-data-dir=${profile}`,...drawioExportArgs(outfile(m.name,mode,'drawio'),outfile(m.name,mode,'png'),'png')],{stdio:'pipe',timeout:90000});
  execFileSync('python',[path.join(repo,'docs/diagrams/reviews/canonical-api-host/preview.py'),outfile(m.name,mode,'png')],{stdio:'pipe'});
  console.log(`${m.name} ${mode}: exported (inspection not yet recorded)`);
}
