import { DESIGN_TOKENS as T, cardContentMetrics } from './fluent-tokens.mjs';
import { routeFluentGraph, placeFluentLabels } from './fluent-routing.mjs';
import { findConnectorJunctions } from './fluent-edge-geometry.mjs';
import { groupCaptionMetrics } from './fluent-group-label.mjs';

const equal=(a,b)=>Math.abs(a-b)<.01;
const distance=(a,b)=>Math.abs(a.x-b.x)+Math.abs(a.y-b.y);
const overlaps=(a,b)=>a.x<b.x+b.width&&b.x<a.x+a.width&&a.y<b.y+b.height&&b.y<a.y+a.height;
const segment=(a,b)=>({x:Math.min(a.x,b.x)-.1,y:Math.min(a.y,b.y)-.1,width:Math.abs(a.x-b.x)+.2,height:Math.abs(a.y-b.y)+.2});
function stretchAxis(values,clusterDistance,minimumStep,compact=false) {
  const clusters=[];
  for(const value of [...values].sort((a,b)=>a-b)) {
    if(clusters.length&&value-clusters.at(-1).at(-1)<clusterDistance)clusters.at(-1).push(value);
    else clusters.push([value]);
  }
  const before=clusters.map(c=>c.reduce((a,b)=>a+b,0)/c.length),after=[];
  before.forEach((v,i)=>after.push(i?after[i-1]+(compact?minimumStep:Math.max(minimumStep,v-before[i-1])):v));
  return value=>{
    if(value<=before[0])return value+after[0]-before[0];
    for(let i=1;i<before.length;i++)if(value<=before[i])return after[i-1]+(value-before[i-1])*(after[i]-after[i-1])/(before[i]-before[i-1]);
    return value+after.at(-1)-before.at(-1);
  };
}
function simplify(points) {
  const out=[];
  for(const p of points) {
    if(out.length&&distance(p,out.at(-1))<.01)continue;
    while(out.length>1) {
      const a=out.at(-2),b=out.at(-1);
      if(equal(a.x,b.x)&&equal(b.x,p.x)||equal(a.y,b.y)&&equal(b.y,p.y))out.pop();
      else break;
    }
    out.push(p);
  }
  return out;
}

export function fitSourceLayout(spec,semantic,{reroute=false,compact=false,routeOrder='returns-first',extraGap=0}={}) {
  if(spec.nodes.some(n=>!semantic.node_boxes[n.id]))return null;
  const widths=Object.values(semantic.node_boxes).map(b=>b[2]).sort((a,b)=>a-b);
  const scale=T.geometry.cardWidth/widths[Math.floor(widths.length/2)];
  const rawBoxes=Object.values(semantic.node_boxes),heights=rawBoxes.map(b=>b[3]*scale).sort((a,b)=>a-b);
  const labelWidth=Math.min(260,Math.max(56,...spec.edges.map(e=>Math.min(28,String(e.label??'').length)*T.typography.edgeSize*.55+20)));
  const fx=stretchAxis(rawBoxes.map(([x,,w])=>(x+w/2)*scale),T.geometry.cardWidth*.4,T.geometry.cardWidth+labelWidth+24+extraGap,compact);
  const fy=stretchAxis(rawBoxes.map(([,y,,h])=>(y+h/2)*scale),heights[Math.floor(heights.length/2)]*.35,T.geometry.cardThreeLineHeight+112+extraGap,compact);
  const boxes=new Map(spec.nodes.map(n=>{
    const [x,y,w,h]=semantic.node_boxes[n.id],height=cardContentMetrics(n).height;
    return [n.id,{x:fx((x+w/2)*scale)-T.geometry.cardWidth/2,y:fy((y+h/2)*scale)-height/2,width:T.geometry.cardWidth,height}];
  }));
  if(reroute) {
    const ordered=[...boxes.values()].sort((a,b)=>a.y-b.y||a.x-b.x);
    for(let i=1;i<ordered.length;i++)for(let j=0;j<i;j++) {
      const a=ordered[j],b=ordered[i];
      if(overlaps(a,b))b.x=a.x+a.width+72;
    }
  }
  const nodes=spec.nodes.map(n=>({id:n.id,type:'card',position:{x:boxes.get(n.id).x,y:boxes.get(n.id).y},data:n}));
  for(const group of semantic.groups) {
    const [x,y,w,h]=group.bounds;
    nodes.push({id:`group-${group.id}`,type:'band',position:{x:fx(x*scale),y:fy(y*scale)},
      style:{width:fx((x+w)*scale)-fx(x*scale),height:fy((y+h)*scale)-fy(y*scale)},data:{label:group.label,tier:group.parent?2:1}});
    nodes.push({id:`group-${group.id}-label`,type:'bandLabel',position:{x:fx(x*scale)+20,y:fy(y*scale)+8},
      data:{label:group.label,width:fx((x+w)*scale)-fx(x*scale)-40}});
  }
  for(const group of semantic.groups) {
    const members=group.members.map(id=>boxes.get(id)).filter(Boolean);
    const band=nodes.find(n=>n.id===`group-${group.id}`),label=nodes.find(n=>n.id===`group-${group.id}-label`);
    const span=Math.max(...members.map(b=>b.x+b.width))-Math.min(...members.map(b=>b.x))+64;
    const header=Math.max(64,groupCaptionMetrics(group.label,span-48).height+24);
    const x=Math.min(...members.map(b=>b.x))-32,y=Math.min(...members.map(b=>b.y))-header;
    const right=Math.max(...members.map(b=>b.x+b.width))+32,bottom=Math.max(...members.map(b=>b.y+b.height))+24;
    band.position={x,y};band.style={width:right-x,height:bottom-y};
    label.position={x:x+20,y:y+16};label.data.width=right-x-40;
  }
  if(reroute) {
    for(const node of nodes){node.position.x+=160;node.position.y+=80;}
    for(const box of boxes.values()){box.x+=160;box.y+=80;}
    const headings=nodes.filter(n=>n.type==='bandLabel').map(n=>({
      ...n.position,...groupCaptionMetrics(n.data.label,n.data.width-8),
    }));
    return {nodes,edges:routeFluentGraph(spec,boxes,headings,{order:routeOrder}),canvasWidth:fx(semantic.page.width*scale)+320,canvasHeight:fy(semantic.page.height*scale)+160};
  }
  const taken=new Set();
  const edges=spec.edges.map((edge,index)=>{
    const at=semantic.edge_geometry.findIndex((g,i)=>!taken.has(i)&&g.from===edge.from&&g.to===edge.to);
    if(at<0)throw new Error(`${edge.from} -> ${edge.to}: no preserved source route`);
    taken.add(at);
    const g=semantic.edge_geometry[at],a=boxes.get(edge.from),b=boxes.get(edge.to);
    let start={x:a.x+a.width*g.exitX,y:a.y+a.height*g.exitY},end={x:b.x+b.width*g.entryX,y:b.y+b.height*g.entryY};
    let points=g.points.map(p=>({x:fx(p.x*scale),y:fy(p.y*scale)}));
    const perimeter=(point,box,toward)=>{
      if(equal(point.x,box.x)||equal(point.x,box.x+box.width)||equal(point.y,box.y)||equal(point.y,box.y+box.height))return point;
      const dx=toward.x-point.x,dy=toward.y-point.y;
      return Math.abs(dx)>=Math.abs(dy)?{x:box.x+(dx>=0?box.width:0),y:point.y}:
        {x:point.x,y:box.y+(dy>=0?box.height:0)};
    };
    start=perimeter(start,a,points[0]??end);
    end=perimeter(end,b,points.at(-1)??start);
    const horizontalExit=equal(start.x,a.x)||equal(start.x,a.x+a.width);
    const horizontalEntry=equal(end.x,b.x)||equal(end.x,b.x+b.width);
    if(points.length) {
      const first=points[0],last=points.at(-1);
      if(!equal(start.x,first.x)&&!equal(start.y,first.y))points.unshift(horizontalExit?{x:first.x,y:start.y}:{x:start.x,y:first.y});
      if(!equal(end.x,last.x)&&!equal(end.y,last.y))points.push(horizontalEntry?{x:last.x,y:end.y}:{x:end.x,y:last.y});
    } else if(!equal(start.x,end.x)&&!equal(start.y,end.y)) {
      if(horizontalExit) {
        const mid=(start.x+end.x)/2;points=[{x:mid,y:start.y},{x:mid,y:end.y}];
      } else {
        const mid=(start.y+end.y)/2;points=[{x:start.x,y:mid},{x:end.x,y:mid}];
      }
    }
    const lines=[''];
    for(const word of String(edge.label??'').split(/\s+/).filter(Boolean)) {
      if(lines.at(-1)&&lines.at(-1).length+word.length+1>28)lines.push('');
      lines[lines.length-1]+=(lines.at(-1)?' ':'')+word;
    }
    return {id:`e${index}`,source:edge.from,target:edge.to,label:lines.join('\n'),markerEnd:!edge.undirected,
      style:{strokeDasharray:edge.dashed},data:{points:simplify([start,...points,end]),loopback:edge.loopback,junctions:[]}};
  });
  placeFluentLabels(edges,[...boxes.values()]);
  const junctions=findConnectorJunctions(edges.map(e=>({id:e.id,source:e.source,target:e.target,points:e.data.points,loopback:e.data.loopback})));
  for(const edge of edges)edge.data.junctions=junctions.get(edge.id)??[];
  return {nodes,edges,canvasWidth:fx(semantic.page.width*scale),canvasHeight:fy(semantic.page.height*scale)};
}
