import { readFileSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { DESIGN_SYSTEM, DESIGN_TOKENS, cardContentMetrics } from './fluent-tokens.mjs';
import { layout as layoutGraph } from './fluent-graph-layout.mjs';
import { routeFluentGraph, spaceFluentCards } from './fluent-routing.mjs';
import { groupCaptionMetrics } from './fluent-group-label.mjs';
import { resolveFluentIcon } from './fluent-icon-catalog.mjs';

export { DESIGN_TOKENS };
export const DRAWIO_CLI_VERSION = DESIGN_SYSTEM.drawioCliVersion;
export const NATIVE_LIBRARY_REFERENCES = Object.freeze(DESIGN_SYSTEM.nativeLibraries);
const C = DESIGN_TOKENS.colors;
const G = DESIGN_TOKENS.geometry;
const T = DESIGN_TOKENS.typography;

const escape = value => String(value ?? '').replaceAll('&', '&amp;').replaceAll('<', '&lt;')
  .replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&apos;');
const clean = value => String(value).replace(/[^A-Za-z0-9_.-]/g, '_');
const html = value => escape(value).replaceAll('\n', '<br>');
const rect = (x,y,width,height) => `<mxGeometry x="${x}" y="${y}" width="${width}" height="${height}" as="geometry"/>`;
const points = list => `<mxGeometry relative="1" as="geometry"><Array as="points">${list.map(p =>
  `<mxPoint x="${p.x}" y="${p.y}"/>`).join('')}</Array></mxGeometry>`;

function cell(id, role, style, geometry, { value='', parent='1', source, target, edge=false }={}) {
  return `<mxCell id="${escape(id)}" fluentRole="${role}" value="${escape(value)}" style="${escape(style)}" parent="${escape(parent)}" ${edge?'edge':'vertex'}="1"${source?` source="${escape(source)}"`:''}${target?` target="${escape(target)}"`:''}>${geometry}</mxCell>`;
}

function document(name,title,width,height,cells,bounds) {
  if(bounds) {
    width=bounds.width;height=bounds.height;
    cells=cells.map(c=>c.includes('parent="1"')?c.replace(/\bx="([^"]+)" y="([^"]+)"/g,
      (_,x,y)=>`x="${Number(x)-bounds.x}" y="${Number(y)-bounds.y}"`):c);
  }
  const paper=width>=height?{width:794,height:559}:{width:559,height:794};
  const scale=Math.max(width/paper.width,height/paper.height);
  const canvasWidth=paper.width*scale,canvasHeight=paper.height*scale;
  const background=cell('fluent-paper','canvas',rounded(C.canvas,G.cardRadius,'none'),
    rect((canvasWidth-width)/2,(canvasHeight-height)/2,width,height));
  const content=cell('fluent-content','layout','group;connectable=0;',
    rect((canvasWidth-width)/2,(canvasHeight-height)/2,width,height));
  return `<?xml version="1.0" encoding="UTF-8"?>
<mxfile host="Agentweaver" version="${DRAWIO_CLI_VERSION}" compressed="false" fluentProfile="${DESIGN_SYSTEM.calibration.profile}">
<diagram id="${escape(name)}" name="${escape(title)}"><mxGraphModel page="1" pageScale="${scale}" pageWidth="${paper.width}" pageHeight="${paper.height}" background="${C.canvas}" grid="0"><root>
<mxCell id="0"/><mxCell id="1" parent="0"/>
${background}
${content}
${cells.map(c=>c.replace('parent="1"','parent="fluent-content"')).join('\n')}
</root></mxGraphModel></diagram></mxfile>\n`;
}

function rounded(fill, radius=G.cardRadius, stroke=C.stroke) {
  return `rounded=1;absoluteArcSize=1;arcSize=${radius*2};fillColor=${fill};strokeColor=${stroke};strokeWidth=1;shadow=0;`;
}

const nativeStyles = {
  azure:'shape=mxgraph.azure2.app_services;',kubernetes:'shape=mxgraph.kubernetes.pod;',
  c4:'shape=mxgraph.c4.container;',uml:'shape=umlActor;',flowchart:'shape=process;',
  bpmn:'shape=mxgraph.bpmn.shape;symbol=general;',networking:'shape=mxgraph.cisco19.router;',
  database:'shape=cylinder3;',cloud:'shape=cloud;',
};
const kubernetesPrIcons = {
  cluster:'cluster',
  configmap:'cm',
  custom_resource:'crd',
  customresource:'crd',
  daemonset:'ds',
  deployment:'deploy',
  job:'job',
  network_policy:'netpol',
  networkpolicy:'netpol',
  node:'node',
  persistent_volume_claim:'pvc',
  persistentvolumeclaim:'pvc',
  pod:'pod',
  secret:'secret',
  service:'svc',
  service_account:'sa',
  serviceaccount:'sa',
  statefulset:'sts',
};

export function nativeShapeStyle(node) {
  let library = node.library ?? node.classification?.replace(/^native:/,'');
  const text = `${node.id} ${node.label ?? ''}`.toLowerCase();
  if (!library) {
    if (/\b(kubernetes|aks|pod)\b/.test(text)) library='kubernetes';
    else if (/\b(postgres|database|sqlite)\b/.test(text)) library='database';
    else if (/\b(gateway|network|router)\b/.test(text)) library='networking';
  }
  return { library: library ?? 'agentweaver', style: node.shape?`shape=${node.shape};`:nativeStyles[library]??'shape=process;' };
}

function iconStyle(node, color) {
  const normalizeNative=style=>{
    const shape=style.match(/(?:^|;)shape=([^;]+)/)?.[1]??'';
    if(shape.startsWith('mxgraph.kubernetes.')) {
      if(!/\.icon2?$/.test(shape)) {
        const requested=shape.split('.').at(-1).toLowerCase();
        const prIcon=kubernetesPrIcons[requested]??requested;
        style=style.replace(`shape=${shape};`,`shape=mxgraph.kubernetes.icon2;prIcon=${prIcon};kubernetesLabel=0;`);
      }
      return `${style};fillColor=${C.surface};strokeColor=${color};strokeWidth=1.5;`;
    }
    if(shape==='cylinder3')style+='size=6;boundedLbl=1;backgroundOutline=1;';
    return `${style};fillColor=${shape.startsWith('mxgraph.azure')?color:'none'};strokeColor=${color};strokeWidth=1.5;`;
  };
  if(node.nativeStyle?.includes('mxgraph.kubernetes.')||node.nativeStyle?.includes('mxgraph.azure')) {
    return normalizeNative(node.nativeStyle);
  }
  if (['azure','kubernetes'].includes(node.library)
      || ['native:azure','native:kubernetes'].includes(node.classification)) {
    return normalizeNative(nativeShapeStyle(node).style);
  }
  const name = resolveFluentIcon(node);
  const svg = readFileSync(new URL(`../../docs/diagrams/drawio/icons/${name}.svg`,import.meta.url),'utf8')
    .replaceAll('currentColor',color);
  return `shape=image;aspect=fixed;image=data:image/svg+xml,${encodeURIComponent(svg)};`;
}

function textCell(id,role,text,x,y,w,h,size,color,weight=400,parent='1',mono=false) {
  const family=mono?T.monoFamily:T.family;
  const wrapping=role==='group-label'?'white-space:pre;':/\S{29,}/.test(text)?'overflow-wrap:anywhere;':'';
  const value=`<div style="font-family:${family};font-size:${size}px;font-weight:${weight};line-height:1.15;color:${color};${wrapping}">${html(text)}</div>`;
  return cell(id,role,`text;html=1;fillColor=none;strokeColor=none;fontFamily=${family};fontSize=${size};spacing=0;align=left;verticalAlign=top;whiteSpace=wrap;`,
    rect(x,y,w,h),{value,parent});
}

export function cardCells(node,box,parent='1',suffix='') {
  for(const key of ['label','subLabel','meta']) {
    if(node[key]!=null && typeof node[key]!=='string') throw new Error(`Card ${node.id}: ${key} must be text, not a structured value`);
  }
  const id=`node-${clean(node.id)}${suffix}`;
  const tone=DESIGN_TOKENS.badges[node.badge?.tone??node.tone??'neutral'];
  if (!tone) throw new Error(`Unknown semantic tone for ${node.id}`);
  const {x,y,width,height}=box;
  const cells=[cell(id,'card','group;connectable=1;',rect(x,y,width,height),{parent})];
  for (const [index,layer] of DESIGN_SYSTEM.cards.shadowLayers.entries()) {
    cells.push(cell(`${id}-shadow-${index}`,'shadow',rounded('#1c1814',G.cardRadius,'none')+`opacity=${layer.opacity};`,
      rect(0,layer.offsetY,width,height),{parent:id}));
  }
  cells.push(cell(`${id}-accent`,'accent',rounded(tone.foreground,G.cardRadius,'none'),rect(0,0,width,height),{parent:id}),
    cell(`${id}-surface`,'surface',rounded(C.surface),rect(G.accentWidth,0,width-G.accentWidth,height),{parent:id}),
    cell(`${id}-icon`,'icon',iconStyle(node,tone.foreground),rect(G.iconInset,(height-G.iconSize)/2,G.iconSize,G.iconSize),{parent:id}));
  const {title:titleH,subtitle:subH,meta:metaH}=cardContentMetrics(node);
  const top=(height-titleH-subH-metaH)/2-2.5;
  cells.push(textCell(`${id}-title`,'title',node.label??node.id,G.textInset,top,width-G.textInset-G.padding,titleH,T.titleSize,C.ink,600,id));
  if(node.subLabel) cells.push(textCell(`${id}-subtitle`,'subtitle',node.subLabel,G.textInset,top+titleH+2,
    width-G.textInset-G.padding,subH,T.subtitleSize,C.inkMuted,400,id));
  if(node.meta) cells.push(textCell(`${id}-meta`,'meta',node.meta,G.textInset,top+titleH+subH+2,
    width-G.textInset-G.padding,metaH,T.metaSize,C.inkFaint,400,id,true));
  if(node.badge?.visible) cells.push(cell(`${id}-badge`,'badge',rounded(tone.background,12,'none')+
    `fontFamily=${T.family};fontSize=${T.badgeSize};fontColor=${tone.foreground};`,
  rect(width-100,8,82,22),{value:node.badge.text,parent:id}));
  return cells;
}

function notationNodeCell(node,box,parent='1') {
  const id=`node-${clean(node.id)}`;
  const tone=DESIGN_TOKENS.badges[node.badge?.tone??node.tone??'neutral'];
  if(!tone) throw new Error(`Unknown semantic tone for ${node.id}`);
  const shapes={
    decision:'rhombus;perimeter=rhombusPerimeter;',
    terminator:'rounded=1;absoluteArcSize=1;arcSize=80;',
    document:'shape=document;boundedLbl=1;',
    state:'rounded=1;absoluteArcSize=1;arcSize=28;',
    initial:'ellipse;aspect=fixed;',
    final:'ellipse;aspect=fixed;strokeWidth=5;',
  };
  const shape=shapes[node.variant];
  if(!shape) return cardCells(node,box,parent);
  const detail=node.subLabel?`<div style="font-size:${T.subtitleSize}px;font-weight:400;color:${C.inkMuted};margin-top:5px;">${html(node.subLabel)}</div>`:'';
  const meta=node.meta?`<div style="font-family:${T.monoFamily};font-size:${T.metaSize}px;color:${C.inkFaint};margin-top:5px;">${html(node.meta)}</div>`:'';
  const value=`<div style="font-family:${T.family};font-size:${T.titleSize}px;font-weight:600;line-height:1.15;color:${C.ink};text-align:center;">${html(node.label??node.id)}${detail}${meta}</div>`;
  return [cell(id,'notation-node',`${shape}html=1;whiteSpace=wrap;fillColor=${C.surface};strokeColor=${tone.foreground};strokeWidth=2;shadow=1;fontFamily=${T.family};fontSize=${T.titleSize};fontColor=${C.ink};spacing=16;align=center;verticalAlign=middle;`,
    rect(box.x,box.y,box.width,box.height),{value,parent})];
}

function lineStyle({loopback,dashed,arrow=DESIGN_SYSTEM.connectors.arrowType}={}) {
  return `edgeStyle=none;rounded=1;arcSize=${DESIGN_SYSTEM.connectors.cornerRadiusPx};html=1;strokeWidth=${DESIGN_SYSTEM.connectors.strokeWidthPx};`
    +`strokeColor=${loopback?C.loopback:C.connector};endArrow=${arrow};endFill=1;endSize=${DESIGN_SYSTEM.connectors.arrowSizePx};`
    +`fontFamily=${T.family};fontSize=${T.edgeSize};labelBackgroundColor=${C.surface};jumpStyle=arc;jumpSize=${DESIGN_SYSTEM.connectors.bridgeRadiusPx*2};`
    +(loopback||dashed?'dashed=1;dashPattern=8 6;':'');
}

function labelCell(id,label,position) {
  const lines=label.split('\n');
  const width=Math.max(...lines.map(line=>line.length))*T.edgeSize*.55+20;
  const height=lines.length*T.edgeSize*1.15+12;
  return cell(id,'edge-label',rounded(C.surface,6)+`html=1;fontFamily=${T.family};fontSize=${T.edgeSize};fontColor=${C.inkStrong};fontStyle=1;spacing=0;`,
    rect(position.x-width/2,position.y-height/2,width,height),
    {value:`<div style="font-family:${T.family};font-size:${T.edgeSize}px;font-weight:600;line-height:1.15;">${html(label)}</div>`});
}

function validateGraph(spec) {
  if(!Array.isArray(spec.nodes)||!Array.isArray(spec.edges)) throw new Error('Graph spec requires nodes and edges arrays');
  const ids=new Set(spec.nodes.map(n=>n.id));
  if(ids.size!==spec.nodes.length||ids.has(undefined)) throw new Error('Duplicate or missing graph node id');
  if(spec.nodes.some(n=>typeof n.id!=='string'||!n.id) || new Set([...ids].map(clean)).size!==ids.size) {
    throw new Error('Graph node IDs must be nonempty strings with distinct XML identifiers');
  }
  for(const edge of spec.edges) if(!ids.has(edge.from)||!ids.has(edge.to)) throw new Error(`Graph edge references unknown node: ${edge.from} -> ${edge.to}`);
  const groups=new Map((spec.groups??[]).map(g=>[g.id,g]));
  for(const n of spec.nodes) if(n.group&&!groups.has(n.group)) throw new Error(`Graph node ${n.id} references unknown group ${n.group}`);
  for(const group of groups.values()) {
    const seen=new Set([group.id]);
    for(let parent=group.parent;parent;parent=groups.get(parent).parent) {
      if(!groups.has(parent)) throw new Error(`Unknown group parent ${parent}`);
      if(seen.has(parent)) throw new Error(`Graph group hierarchy cycle ${parent}`);
      seen.add(parent);
    }
  }
}

export function graphSpecToDrawio(spec,{name='diagram'}={}) {
  validateGraph(spec);
  const result=spec.layoutSnapshot??layoutGraph(spec);
  const unsupported=result.nodes.filter(node=>!['card','band','bandLabel'].includes(node.type));
  if(unsupported.length) throw new Error(`Layout requires explicit visual bindings for: ${[...new Set(unsupported.map(n=>n.type))].join(', ')}`);
  const cards=new Map(result.nodes.filter(n=>n.type==='card').map(n=>[n.id,{
    ...n.position,width:G.cardWidth,height:cardContentMetrics(n.data).height,
  }]));
  const bands=new Map(result.nodes.filter(n=>n.type==='band').map(n=>[n.id,n]));
  const groupSpecs=new Map((spec.groups??[]).map(g=>[`group-${g.id}`,g]));
  if(spec.routing==='separate-ports') {
    spaceFluentCards(spec,cards);
    const belongs=(node,group)=>{
      for(let id=node.group;id;id=spec.groups.find(g=>g.id===id)?.parent)if(id===group)return true;
      return false;
    };
    for(const [id,band] of bands) {
      const group=groupSpecs.get(id);
      if(!group)continue;
      const members=spec.nodes.filter(n=>belongs(n,group.id)).map(n=>cards.get(n.id));
      if(!members.length)continue;
      const x=Math.min(...members.map(b=>b.x))-G.groupPaddingX,y=Math.min(...members.map(b=>b.y))-G.groupPaddingTop;
      band.position={x,y};
      band.style={width:Math.max(...members.map(b=>b.x+b.width))+G.groupPaddingX-x,
        height:Math.max(...members.map(b=>b.y+b.height))+G.groupPaddingBottom-y};
    }
    result.canvasWidth=Math.max(result.canvasWidth,...[...cards.values()].map(b=>b.x+b.width+80));
  }
  const cells=[];
  for(const [id,band] of bands) {
    const parentSpec=groupSpecs.get(id)?.parent;
    const parent=parentSpec?`group-${parentSpec}`:'1';
    const ancestor=bands.get(parent)?.position??{x:0,y:0};
    const tier=band.data.tier??(parentSpec?2:1);
    cells.push(cell(id,'group',rounded(tier<=1?C.group1:C.group2,G.cardRadius,'none'),
      rect(band.position.x-ancestor.x,band.position.y-ancestor.y,band.style.width,band.style.height),{parent}));
  }
  const junctions=new Map();
  const headingBoxes=[...bands].filter(([id])=>groupSpecs.has(id)).map(([id,band])=>({
    x:band.position.x+24,y:band.position.y+16,width:Math.min(band.style.width-48,groupSpecs.get(id).label.length*T.titleSize*.55),height:28}));
  const routes=spec.routing==='separate-ports'?routeFluentGraph(spec,cards):
    result.edges.map(edge=>({...edge,data:{...edge.data,points:edge.data.points.map(p=>({...p}))}}));
  const terminals=new Map();
  for(const edge of routes) {
    if(!edge.markerEnd || edge.data.loopback) continue;
    const ps=edge.data.points,end=ps.at(-1);
    const key=`${edge.target}:${end.x}:${end.y}`;
    if(!terminals.has(key)) terminals.set(key,[]);
    terminals.get(key).push(edge);
  }
  for(const [index,list] of [...terminals.values()].entries()) {
    if(list.length<2) continue;
    const end=list[0].data.points.at(-1);
    const nearest=list.map(e=>e.data.points.at(-2)).sort((a,b)=>Math.hypot(a.x-end.x,a.y-end.y)-Math.hypot(b.x-end.x,b.y-end.y))[0];
    const distance=Math.hypot(nearest.x-end.x,nearest.y-end.y);
    if(distance<18) continue;
    const join={...nearest};
    const id=`merge-${index}`; junctions.set(id,join);
    for(const edge of list) {
      edge.join=id;
      const ps=edge.data.points,previous=ps.at(-2);
      const bend=join.x===end.x?{x:previous.x,y:join.y}:{x:join.x,y:previous.y};
      ps.splice(-1,1,...(previous.x!==join.x&&previous.y!==join.y?[bend]:[]),join);
    }
    const targetBox=cards.get(list[0].target);
    cells.push(cell(`${id}-trunk`,'connector',lineStyle()+`exitX=0.5;exitY=0.5;exitPerimeter=0;`
      +`entryX=${(end.x-targetBox.x)/targetBox.width};entryY=${(end.y-targetBox.y)/targetBox.height};entryPerimeter=0;`,
      points([]),{edge:true,source:id,target:`node-${clean(list[0].target)}`}));
  }
  for(const edge of routes) {
    const ps=edge.data.points;
    const from=cards.get(edge.source),to=cards.get(edge.target);
    let style=lineStyle({loopback:edge.data.loopback,dashed:edge.style?.strokeDasharray,arrow:edge.join?'none':edge.markerEnd?DESIGN_SYSTEM.connectors.arrowType:'none'});
    if(spec.edges[Number(edge.id.slice(1))]?.bidirectional)style+=`startArrow=${DESIGN_SYSTEM.connectors.arrowType};startSize=${DESIGN_SYSTEM.connectors.arrowSizePx};startFill=1;`;
    const start=ps[0],end=ps.at(-1);
    style+=`exitX=${(start.x-from.x)/from.width};exitY=${(start.y-from.y)/from.height};exitPerimeter=0;`;
    style+=edge.join?'entryX=0.5;entryY=0.5;entryPerimeter=0;':`entryX=${(end.x-to.x)/to.width};entryY=${(end.y-to.y)/to.height};entryPerimeter=0;`;
    cells.push(cell(edge.id,'connector',style,points(ps.slice(1,-1)),
      {edge:true,source:`node-${clean(edge.source)}`,target:edge.join??`node-${clean(edge.target)}`}));
    for(const point of edge.data.junctions??[]) {
      if([...cards.values()].some(b=>point.x>=b.x-.1&&point.x<=b.x+b.width+.1&&point.y>=b.y-.1&&point.y<=b.y+b.height+.1)) continue;
      if(![...junctions.values()].some(p=>p.x===point.x&&p.y===point.y)) junctions.set(`junction-${point.x}-${point.y}`,point);
    }
  }
  const diameter=DESIGN_SYSTEM.connectors.junctionDiameterPx;
  for(const [id,p] of junctions) cells.push(cell(id,'junction',`ellipse;fillColor=${C.connector};strokeColor=none;`,rect(p.x-diameter/2,p.y-diameter/2,diameter,diameter)));
  for(const node of spec.nodes) {
    const box=cards.get(node.id),parent=node.group?`group-${node.group}`:'1',ancestor=bands.get(parent)?.position??{x:0,y:0};
    cells.push(...notationNodeCell(node,{...box,x:box.x-ancestor.x,y:box.y-ancestor.y},parent));
  }
  {
    for(const [id,band] of bands)if(groupSpecs.has(id)) {
      const {text,width,height}=groupCaptionMetrics(groupSpecs.get(id).label,band.style.width-48);
      let position;
      for(const y of [band.position.y+8,band.position.y+16]) {
        for(let x=band.position.x+24;x+width<=band.position.x+band.style.width-16;x+=12) {
          const hit=routes.some(r=>r.data.points.slice(1).some((b,i)=>{
            const a=r.data.points[i];
            return Math.min(a.x,b.x)<x+width+3&&Math.max(a.x,b.x)>x-3&&Math.min(a.y,b.y)<y+height+3&&Math.max(a.y,b.y)>y-3;
          }));
          const cardHit=[...cards.values()].some(b=>b.x<x+width&&b.x+b.width>x&&b.y<y+height&&b.y+b.height>y);
          if(!hit&&!cardHit){position={x,y};break;}
        }
        if(position)break;
      }
      position??={x:band.position.x+24,y:band.position.y+8};
      cells.push(textCell(`${id}-label`,'group-label',text,position.x,position.y,width,height,T.titleSize,C.inkStrong,600));
    }
  }
  for(const edge of routes) if(edge.label&&edge.data.labelPos) {
    const leader=edge.data.labelLeader;
    if(leader)cells.push(cell(`${edge.id}-leader`,'label-leader',
      `edgeStyle=none;endArrow=none;startArrow=none;strokeWidth=1;strokeColor=${C.connector};`,
      `<mxGeometry relative="1" as="geometry"><mxPoint x="${leader.from.x}" y="${leader.from.y}" as="sourcePoint"/>`
      +`<mxPoint x="${leader.to.x}" y="${leader.to.y}" as="targetPoint"/></mxGeometry>`,{edge:true}));
    cells.push(labelCell(`${edge.id}-label`,edge.label,edge.data.labelPos));
  }
  const visible=[...[...cards.values()],...[...bands.values()].map(b=>({...b.position,width:b.style.width,height:b.style.height}))];
  for(const edge of routes) {
    for(const p of edge.data.points)visible.push({...p,width:0,height:0});
    if(edge.label&&edge.data.labelPos) {
      const lines=edge.label.split('\n'),width=Math.max(...lines.map(s=>s.length))*T.edgeSize*.55+20,height=lines.length*T.edgeSize*1.15+12;
      visible.push({x:edge.data.labelPos.x-width/2,y:edge.data.labelPos.y-height/2,width,height});
    }
  }
  const left=Math.min(...visible.map(b=>b.x))-48,top=Math.min(...visible.map(b=>b.y))-48;
  const bounds={x:left,y:top,width:Math.max(...visible.map(b=>b.x+b.width))+48-left,
    height:Math.max(...visible.map(b=>b.y+b.height))+48-top};
  return document(name,spec.title??name,result.canvasWidth,result.canvasHeight,cells,bounds);
}

const architectureRoleIcons = {
  actor: 'person',
  component: 'package',
  service: 'api',
  coordinator: 'coordinator',
  worker: 'worker',
  datastore: 'state',
  workspace: 'workspace',
  external: 'cloud',
};

export function architectureSpecToDrawio(spec,{name='diagram'}={}) {
  if(!Array.isArray(spec.layers)||spec.layers.length<2) throw new Error('Architecture spec requires at least two layers');
  if(!Array.isArray(spec.nodes)||!spec.nodes.length||!Array.isArray(spec.edges)) throw new Error('Architecture spec requires nodes and edges');
  const nodes=new Map(spec.nodes.map(node=>[node.id,node]));
  if(nodes.size!==spec.nodes.length||nodes.has(undefined)) throw new Error('Duplicate or missing architecture node id');
  const assigned=new Set();
  for(const layer of spec.layers) {
    if(!Array.isArray(layer.nodes)||!layer.nodes.length) throw new Error(`Architecture layer ${layer.id} is empty`);
    for(const id of layer.nodes) {
      if(!nodes.has(id)) throw new Error(`Architecture layer ${layer.id} references unknown node ${id}`);
      if(assigned.has(id)) throw new Error(`Architecture node ${id} appears in more than one layer`);
      assigned.add(id);
    }
  }
  if(assigned.size!==nodes.size) throw new Error('Every architecture node must belong to exactly one layer');
  for(const edge of spec.edges) if(!nodes.has(edge.from)||!nodes.has(edge.to)) throw new Error(`Architecture edge references unknown node: ${edge.from} -> ${edge.to}`);

  const margin=48,laneGap=132,lanePadding=24,laneHeader=64,cardGap=34;
  const cardWidth=G.cardWidth;
  const prepared=new Map(spec.nodes.map(node=>[node.id,{
    ...node,
    fluentIcon:node.fluentIcon??architectureRoleIcons[node.role??'component']??'package',
    meta:node.meta??`«${node.role??'component'}»`,
  }]));
  const layerSlots=new Map();
  let maxCards=0;
  for(const layer of spec.layers) {
    const occupied=new Set();
    const slots=new Map();
    layer.nodes.forEach((id,nodeIndex)=>{
      const slot=prepared.get(id).slot??nodeIndex;
      if(!Number.isInteger(slot)||slot<0) throw new Error(`Architecture node ${id} has an invalid slot`);
      if(occupied.has(slot)) throw new Error(`Architecture layer ${layer.id} assigns more than one node to slot ${slot}`);
      occupied.add(slot);
      slots.set(id,slot);
      maxCards=Math.max(maxCards,slot+1);
    });
    layerSlots.set(layer.id,slots);
  }
  const maxCardHeight=Math.max(...[...prepared.values()].map(cardContentMetrics).map(metrics=>metrics.height));
  const laneWidth=cardWidth+lanePadding*2;
  const laneHeight=laneHeader+maxCards*maxCardHeight+Math.max(0,maxCards-1)*cardGap+lanePadding;
  const width=margin*2+spec.layers.length*laneWidth+(spec.layers.length-1)*laneGap;
  const height=margin*2+laneHeight;
  const cells=[],boxes=new Map(),nodeLayer=new Map();

  spec.layers.forEach((layer,index)=>{
    const x=margin+index*(laneWidth+laneGap),y=margin;
    cells.push(cell(`architecture-layer-${clean(layer.id)}`,'group',rounded(C.group1,G.cardRadius,'none'),
      rect(x,y,laneWidth,laneHeight)));
    cells.push(textCell(`architecture-layer-${clean(layer.id)}-label`,'group-label',layer.label,
      x+lanePadding,y+18,laneWidth-lanePadding*2,28,T.titleSize,C.inkStrong,600));
    layer.nodes.forEach(id=>{
      const node=prepared.get(id);
      const cardHeight=cardContentMetrics(node).height;
      const slot=layerSlots.get(layer.id).get(id);
      const box={
        x:x+lanePadding,
        y:y+laneHeader+slot*(maxCardHeight+cardGap)+(maxCardHeight-cardHeight)/2,
        width:cardWidth,
        height:cardHeight,
      };
      boxes.set(id,box);
      nodeLayer.set(id,index);
      cells.push(...cardCells(node,box));
    });
  });

  const outgoing=new Map(),incoming=new Map();
  for(const edge of spec.edges) {
    (outgoing.get(edge.from)??outgoing.set(edge.from,[]).get(edge.from)).push(edge);
    (incoming.get(edge.to)??incoming.set(edge.to,[]).get(edge.to)).push(edge);
  }
  spec.edges.forEach((edge,index)=>{
    const from=boxes.get(edge.from),to=boxes.get(edge.to);
    const sameLayer=nodeLayer.get(edge.from)===nodeLayer.get(edge.to);
    const forward=to.x>from.x;
    const sourceList=outgoing.get(edge.from),targetList=incoming.get(edge.to);
    const sourcePort=(sourceList.indexOf(edge)+1)/(sourceList.length+1);
    const targetPort=(targetList.indexOf(edge)+1)/(targetList.length+1);
    const downward=to.y>from.y;
    const start=sameLayer
      ?{x:from.x+from.width/2,y:downward?from.y+from.height:from.y}
      :{x:forward?from.x+from.width:from.x,y:from.y+from.height*sourcePort};
    const end=sameLayer
      ?{x:to.x+to.width/2,y:downward?to.y:to.y+to.height}
      :{x:forward?to.x:to.x+to.width,y:to.y+to.height*targetPort};
    const midX=(start.x+end.x)/2;
    const route=[start,{x:midX,y:start.y},{x:midX,y:end.y},end];
    const relation=edge.relation??'dependency';
    const style=lineStyle({
      dashed:relation==='dependency',
      arrow:relation==='association'?'none':DESIGN_SYSTEM.connectors.arrowType,
    })+(sameLayer
      ?`exitX=0.5;exitY=${downward?1:0};exitPerimeter=0;entryX=0.5;entryY=${downward?0:1};entryPerimeter=0;`
      :`exitX=${forward?1:0};exitY=${sourcePort};exitPerimeter=0;entryX=${forward?0:1};entryY=${targetPort};entryPerimeter=0;`);
    cells.push(cell(`architecture-edge-${index}`,'connector',style,points(route.slice(1,-1)),{
      edge:true,source:`node-${clean(edge.from)}`,target:`node-${clean(edge.to)}`,
    }));
    if(edge.label) cells.push(labelCell(`architecture-edge-${index}-label`,edge.label,{
      x:sameLayer?start.x+76:midX,y:sameLayer?(start.y+end.y)/2:start.y,
    }));
  });
  return document(name,spec.title??name,width,height,cells);
}

function flatten(steps,depth=0,out=[]) {
  for(const step of steps) {
    if(step.type==='fragment') {
      out.push({type:'fragment-start',operator:step.operator,label:step.label,depth});
      step.sections.forEach((section,i)=>{
        if(i||section.label) out.push({type:'fragment-section',label:section.label,depth});
        flatten(section.steps,depth+1,out);
      });
      out.push({type:'fragment-end',depth});
    } else out.push({...step,depth});
  }
  return out;
}

export function sequenceSpecToDrawio(spec,{name='diagram'}={}) {
  if(!Array.isArray(spec.participants)||!spec.participants.length||!Array.isArray(spec.steps)) throw new Error('Sequence requires participants and steps');
  const ids=new Set(spec.participants.map(p=>p.id));
  if(ids.size!==spec.participants.length) throw new Error('Duplicate sequence participant');
  const steps=flatten(spec.steps),margin=54,gap=56,top=54,h=G.cardThreeLineHeight,stepGap=54;
  for(const s of steps) {
    const references=s.type==='message'?[s.from,s.to]:s.type==='activation'?[s.participant]:s.type==='note'?s.over:[];
    for(const id of references) if(!ids.has(id)) throw new Error(`Sequence references unknown participant ${id}`);
  }
  const width=margin*2+spec.participants.length*G.cardWidth+(spec.participants.length-1)*gap;
  const bodyHeight=steps.reduce((total,step)=>total+(
    step.type==='activation'?0:
    step.type==='fragment-start'||step.type==='fragment-section'?42:
    step.type==='fragment-end'?12:
    step.type==='note'?84:
    stepGap
  ),0);
  const bottom=top+h+Math.max(220,bodyHeight+76),height=bottom+h+margin;
  const xs=new Map(spec.participants.map((p,i)=>[p.id,margin+G.cardWidth/2+i*(G.cardWidth+gap)]));
  const cells=[],activation=new Map();
  for(const p of spec.participants) {
    const x=xs.get(p.id);
    cells.push(...cardCells(p,{x:x-G.cardWidth/2,y:top,width:G.cardWidth,height:h},'1','-top'),
      ...cardCells(p,{x:x-G.cardWidth/2,y:bottom,width:G.cardWidth,height:h},'1','-bottom'));
    cells.push(cell(`lifeline-${p.id}`,'lifeline',lineStyle({dashed:true,arrow:'none'})+'startArrow=none;exitX=0.5;exitY=1;entryX=0.5;entryY=0;',
      points([]),{edge:true,source:`node-${clean(p.id)}-top`,target:`node-${clean(p.id)}-bottom`}));
  }
  let y=top+h+38,number=1;
  const frames=[];
  steps.forEach((s,i)=>{
    if(s.type==='activation') {
      if(s.action==='start') activation.set(s.participant,y);
      else if(activation.has(s.participant)) {
        const start=activation.get(s.participant);
        cells.push(cell(`activation-${i}`,'activation',rounded(C.surface,2,DESIGN_TOKENS.badges.teal.foreground),
          rect(xs.get(s.participant)-5,start,10,Math.max(12,y-start))));
        activation.delete(s.participant);
      }
      return;
    }
    if(s.type==='fragment-start') {
      frames.push({index:i,start:y-14,depth:s.depth,label:`${s.operator}${s.label?` [${s.label}]`:''}`});y+=42;return;
    }
    if(s.type==='fragment-end') {
      const frame=frames.pop();
      if(!frame) throw new Error('Unbalanced sequence fragment');
      cells.unshift(cell(`fragment-${frame.index}`,'fragment',
        `shape=mxgraph.uml.frame;fillColor=none;strokeColor=${C.inkFaint};html=1;fontFamily=${T.family};fontSize=${T.metaSize};align=left;verticalAlign=top;spacing=8;`,
        rect(margin-12+frame.depth*10,frame.start,width-margin*2+24-frame.depth*20,y-frame.start),{value:frame.label}));
      y+=12;return;
    }
    if(s.type==='fragment-section') {
      cells.push(textCell(`section-${i}`,'section',`[${s.label??'else'}]`,margin+s.depth*10,y,width-margin*2,24,T.metaSize,C.inkMuted));y+=42;return;
    }
    if(s.type==='note') {
      const positions=s.over.map(id=>xs.get(id)),left=Math.min(...positions)-100,right=Math.max(...positions)+100;
      cells.push(cell(`note-${i}`,'note',`shape=note;html=1;whiteSpace=wrap;fillColor=${C.group1};strokeColor=${C.stroke};fontFamily=${T.family};fontSize=${T.metaSize};`,
        rect(left,y,right-left,54),{value:html(s.label)}));y+=84;return;
    }
    if(s.type!=='message') throw new Error(`Unsupported sequence step ${s.type}`);
    const from=xs.get(s.from),to=xs.get(s.to),endY=s.from===s.to?y+28:y;
    for(const [side,x,yy] of [['from',from,y],['to',to,endY]]) cells.push(cell(`message-${i}-${side}-anchor`,'anchor',
      'ellipse;opacity=0;fillOpacity=0;strokeOpacity=0;',rect(x-.5,yy-.5,1,1)));
    cells.push(cell(`message-${i}`,'message',lineStyle({dashed:s.line==='dashed',arrow:s.arrow==='open'?'open':s.arrow==='cross'?'cross':DESIGN_SYSTEM.connectors.arrowType}),
      points(s.from===s.to?[{x:from+62,y},{x:from+62,y:endY}]:[]),
      {edge:true,source:`message-${i}-from-anchor`,target:`message-${i}-to-anchor`}));
    cells.push(labelCell(`message-${i}-label`,`${spec.autonumber?`${number++}. `:''}${s.label}`,
      {x:s.from===s.to?from+220:(from+to)/2,y:y-18}));
    y+=stepGap;
  });
  for(const [id,start] of activation) cells.push(cell(`activation-open-${clean(id)}`,'activation',rounded(C.surface,2),
    rect(xs.get(id)-5,start,10,bottom-start)));
  return document(name,spec.title??name,width,height,cells);
}

export function specToDrawio(spec,options) {
  if(spec.kind==='sequence') return sequenceSpecToDrawio(spec,options);
  if(spec.kind==='architecture') return architectureSpecToDrawio(spec,options);
  return graphSpecToDrawio(spec,options);
}

export async function jsonFileToDrawio(sourcePath,options={}) {
  const raw=await readFile(sourcePath,'utf8');
  let spec;
  try { spec=JSON.parse(raw); } catch(error) { throw new Error(`Invalid diagram JSON in ${path.basename(sourcePath)}: ${error.message}`); }
  return specToDrawio(spec,{name:options.name??path.basename(sourcePath,'.json')});
}
