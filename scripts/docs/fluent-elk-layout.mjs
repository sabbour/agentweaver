import ELK from 'elkjs/lib/elk.bundled.js';
import { DESIGN_TOKENS as T, cardContentMetrics } from './fluent-tokens.mjs';

const elk=new ELK();
const wrap=value=>{
  const lines=[''];
  for(const word of String(value??'').split(/\s+/).filter(Boolean)) {
    if(lines.at(-1)&&lines.at(-1).length+word.length+1>27)lines.push('');
    lines[lines.length-1]+=(lines.at(-1)?' ':'')+word;
  }
  return lines;
};

export async function createElkLayout(spec) {
  const nodes=new Map(spec.nodes.map(n=>[n.id,{id:n.id,width:T.geometry.cardWidth,
    height:cardContentMetrics(n).height,ports:[],layoutOptions:{'elk.portConstraints':'FIXED_SIDE'}}]));
  const groups=new Map((spec.groups??[]).map(g=>[g.id,{id:`group-${g.id}`,children:[],
    labels:[{id:`group-${g.id}-label`,text:g.label,width:g.label.length*T.typography.titleSize*.54,height:28,
      layoutOptions:{'elk.nodeLabels.placement':'INSIDE V_TOP H_LEFT'}}],
    layoutOptions:{'elk.algorithm':'layered','elk.direction':'RIGHT',
      'elk.padding':'[top=64,left=48,bottom=44,right=48]','elk.spacing.nodeNode':'62',
      'elk.layered.spacing.nodeNodeBetweenLayers':'72'}}]));
  const children=[];
  for(const n of spec.nodes)(n.group?groups.get(n.group).children:children).push(nodes.get(n.id));
  for(const g of spec.groups??[])(g.parent?groups.get(g.parent).children:children).push(groups.get(g.id));
  const edges=spec.edges.map((e,index)=>{
    const id=`e${index}`,source=nodes.get(e.from),target=nodes.get(e.to);
    const sourceSpec=spec.nodes.find(n=>n.id===e.from),targetSpec=spec.nodes.find(n=>n.id===e.to);
    const horizontal=sourceSpec.group&&sourceSpec.group===targetSpec.group || spec.direction==='LR';
    source.ports.push({id:`${id}-out`,width:0,height:0,layoutOptions:{'elk.port.side':horizontal?'EAST':'SOUTH'}});
    target.ports.push({id:`${id}-in`,width:0,height:0,layoutOptions:{'elk.port.side':horizontal?'WEST':'NORTH'}});
    const lines=wrap(e.label),text=lines.join('\n');
    return {id,sources:[`${id}-out`],targets:[`${id}-in`],layoutOptions:{'elk.layered.priority.direction':e.loopback?'0':'10'},
      labels:text?[{id:`${id}-label`,text,width:Math.max(...lines.map(s=>s.length))*T.typography.edgeSize*.55+20,
        height:lines.length*T.typography.edgeSize*1.15+12,
        layoutOptions:{'elk.edgeLabels.placement':'CENTER'}}]:[]};
  });
  const graph=await elk.layout({id:'fluent-root',children,edges,layoutOptions:{
    'elk.algorithm':'layered','elk.direction':spec.direction==='LR'?'RIGHT':'DOWN',
    'elk.edgeRouting':'ORTHOGONAL','elk.hierarchyHandling':'INCLUDE_CHILDREN',
    'elk.padding':'[top=80,left=80,bottom=80,right=80]','elk.spacing.nodeNode':'62',
    'elk.spacing.edgeNode':'24','elk.spacing.edgeEdge':'24',
    'elk.layered.spacing.edgeNodeBetweenLayers':'32','elk.layered.spacing.edgeEdgeBetweenLayers':'24',
    'elk.layered.spacing.nodeNodeBetweenLayers':'80','elk.layered.mergeEdges':'false',
    'elk.layered.considerModelOrder.strategy':'NODES_AND_EDGES','elk.randomSeed':'1',
    'elk.aspectRatio':'1.4','elk.layered.wrapping.strategy':'OFF',
    'elk.layered.cycleBreaking.strategy':'MODEL_ORDER',
    'elk.layered.wrapping.additionalEdgeSpacing':'24',
    'elk.layered.wrapping.multiEdge.improveCuts':'true',
    'elk.layered.wrapping.multiEdge.improveWrappedEdges':'true',
  }});
  const positions=new Map([['fluent-root',{x:0,y:0}]]),resultNodes=[];
  function visit(parent,x=0,y=0) {
    for(const child of parent.children??[]) {
      const position={x:x+(child.x??0),y:y+(child.y??0)};
      positions.set(child.id,position);
      if(nodes.has(child.id))resultNodes.push({id:child.id,type:'card',position,data:spec.nodes.find(n=>n.id===child.id)});
      else {
        const g=(spec.groups??[]).find(g=>`group-${g.id}`===child.id);
        resultNodes.push({id:child.id,type:'band',position,data:{tier:g.parent?2:1,label:g.label},
          style:{width:child.width,height:child.height}});
        resultNodes.push({id:`${child.id}-label`,type:'bandLabel',
          position:{x:position.x+24,y:position.y+16},data:{label:g.label,width:child.width-48}});
        visit(child,position.x,position.y);
      }
    }
  }
  visit(graph);
  const resultEdges=graph.edges.map(edge=>{
    const original=spec.edges[Number(edge.id.slice(1))],offset=positions.get(edge.container??'fluent-root');
    if(!offset||edge.sections?.length!==1)throw new Error(`${edge.id}: unsupported compound edge section structure`);
    const section=edge.sections[0],points=[section.startPoint,...section.bendPoints??[],section.endPoint]
      .map(p=>({x:p.x+offset.x,y:p.y+offset.y}));
    const label=edge.labels?.[0];
    return {id:edge.id,source:original.from,target:original.to,label:label?.text,markerEnd:!original.undirected,
      style:{strokeDasharray:original.dashed},data:{points,loopback:original.loopback,junctions:[],
        labelPos:label?{x:label.x+offset.x+label.width/2,y:label.y+offset.y+label.height/2}:undefined}};
  });
  return {nodes:resultNodes,edges:resultEdges,canvasWidth:graph.width,canvasHeight:graph.height};
}
