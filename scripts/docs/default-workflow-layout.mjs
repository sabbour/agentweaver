import { fitSourceLayout } from './fluent-source-layout.mjs';

export function defaultWorkflowLayout(spec) {
  const rows=[['agent','rai'],['terminal-safety-failed','review'],['terminal-declined','merge'],
    ['push-pr','scribe'],[null,'done']];
  const node_boxes={};
  rows.forEach((row,r)=>row.forEach((id,c)=>{if(id)node_boxes[id]=[80+c*420,80+r*220,340,104];}));
  const route=(from,to,exit,entry,points=[])=>({from,to,exitX:exit[0],exitY:exit[1],entryX:entry[0],entryY:entry[1],
    points:points.map(([x,y])=>({x,y}))});
  const geometry=[
    route('agent','rai',[1,.5],[0,.5]),
    route('rai','agent',[1,.3],[.5,0],[[880,111.2],[880,40],[250,40]]),
    route('rai','review',[.5,1],[.5,0]),
    route('rai','terminal-safety-failed',[.2,1],[.5,0],[[568,240],[250,240]]),
    route('rai','scribe',[1,.7],[1,.25],[[920,152.8],[920,766]]),
    route('review','agent',[0,.25],[.5,1],[[460,326],[460,216],[250,216]]),
    route('review','merge',[.5,1],[.5,0]),
    route('review','terminal-declined',[.2,1],[.5,0],[[568,460],[250,460]]),
    route('merge','review',[0,.5],[0,.75],[[440,572],[440,378]]),
    route('merge','push-pr',[.5,1],[.5,0],[[670,680],[250,680]]),
    route('push-pr','scribe',[1,.5],[0,.5]),
    route('scribe','done',[.5,1],[.5,0]),
  ];
  return fitSourceLayout(spec,{node_boxes,groups:[],edge_geometry:geometry,page:{width:1040,height:1144}});
}
