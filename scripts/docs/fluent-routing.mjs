import { DESIGN_SYSTEM } from './fluent-tokens.mjs';

const near=(a,b)=>Math.abs(a-b)<.01;
const length=(a,b)=>Math.abs(a.x-b.x)+Math.abs(a.y-b.y);
const intersects=(a,b)=>a.x<b.x+b.width&&b.x<a.x+a.width&&a.y<b.y+b.height&&b.y<a.y+a.height;
const inflate=(b,n)=>({x:b.x-n,y:b.y-n,width:b.width+2*n,height:b.height+2*n});
const segmentBox=(a,b)=>({x:Math.min(a.x,b.x)-.01,y:Math.min(a.y,b.y)-.01,width:Math.abs(a.x-b.x)+.02,height:Math.abs(a.y-b.y)+.02});
const segments=ps=>ps.slice(1).map((p,i)=>[ps[i],p]).filter(([a,b])=>length(a,b)>.01);

class Heap {
  items=[];
  push(value) {
    const a=this.items;a.push(value);
    let i=a.length-1;
    while(i&&a[(i-1)>>1].score>value.score){a[i]=a[(i-1)>>1];i=(i-1)>>1;}
    a[i]=value;
  }
  pop() {
    const a=this.items,first=a[0],last=a.pop();
    if(a.length) {
      let i=0;
      while(i*2+1<a.length) {
        let child=i*2+1;
        if(child+1<a.length&&a[child+1].score<a[child].score)child++;
        if(a[child].score>=last.score)break;
        a[i]=a[child];i=child;
      }
      a[i]=last;
    }
    return first;
  }
}

function simplify(points) {
  const out=[];
  for(const point of points) {
    if(out.length&&length(point,out.at(-1))<.01)continue;
    while(out.length>1) {
      const a=out.at(-2),b=out.at(-1);
      if((near(a.x,b.x)&&near(b.x,point.x))||(near(a.y,b.y)&&near(b.y,point.y)))out.pop();
      else break;
    }
    out.push(point);
  }
  return out;
}

function wrap(text) {
  const lines=[];
  for(const original of String(text??'').split('\n')) {
    let line='';
    for(const word of original.split(/\s+/).filter(Boolean)) {
      if(line&&line.length+word.length+1>27){lines.push(line);line='';}
      line+=(line?' ':'')+word;
    }
    if(line)lines.push(line);
  }
  return lines.join('\n');
}

function assignPorts(spec,cards,headings) {
  const buckets=new Map(),refs=[];
  const all=[...cards.values()],middle=(Math.min(...all.map(b=>b.x))+Math.max(...all.map(b=>b.x+b.width)))/2;
  const add=(edge,which,node,side,other)=>{
    const box=cards.get(node),capacity=s=>Math.floor((['left','right'].includes(s)?box.height-28:box.width*.6)/20)-1;
    const alternatives=[side,...['left','right','top','bottom'].filter(s=>s!==side)];
    side=alternatives.find(s=>(buckets.get(`${node}:${s}`)?.length??0)<capacity(s)&&
      !(s==='top'&&headings.some(h=>intersects(h,{x:box.x+box.width*.2,y:box.y-44,width:box.width*.6,height:44}))))??side;
    const key=`${node}:${side}`,ref={edge,which,node,side,other};
    if(!buckets.has(key))buckets.set(key,[]);
    buckets.get(key).push(ref);refs.push(ref);
  };
  spec.edges.forEach((e,index)=>{
    const a=cards.get(e.from),b=cards.get(e.to);
    const dx=b.x+b.width/2-a.x-a.width/2,dy=b.y+b.height/2-a.y-a.height/2;
    let from,to;
    if(e.from===e.to){from='right';to='top';}
    else if(e.loopback||dy<-a.height){from=to=(a.x+b.x+a.width)/2<middle?'left':'right';}
    else if(dy>(a.height+62)*1.8){from=to=(a.x+b.x+a.width)/2<middle?'left':'right';}
    else if(Math.abs(dy)<Math.min(a.height,b.height)){from=dx>=0?'right':'left';to=dx>=0?'left':'right';}
    else {from=dy>=0?'bottom':'top';to=dy>=0?'top':'bottom';}
    add(index,'from',e.from,from,b);add(index,'to',e.to,to,a);
  });
  for(const bucket of buckets.values()) {
    bucket.sort((a,b)=>a.other.y-b.other.y||a.other.x-b.other.x||a.edge-b.edge);
    bucket.forEach((ref,index)=>{
      const b=cards.get(ref.node),vertical=['left','right'].includes(ref.side);
      const fraction=(index+1)/(bucket.length+1);
      const span=vertical?b.height-28:b.width*.6,offset=vertical?14:b.width*.2;
      ref.point=vertical?{x:b.x+(ref.side==='right'?b.width:0),y:b.y+offset+span*fraction}:
        {x:b.x+offset+span*fraction,y:b.y+(ref.side==='bottom'?b.height:0)};
      const normal={left:[-1,0],right:[1,0],top:[0,-1],bottom:[0,1]}[ref.side];
      ref.stub={x:ref.point.x+normal[0]*32,y:ref.point.y+normal[1]*32};
    });
  }
  return spec.edges.map((_,i)=>({from:refs.find(r=>r.edge===i&&r.which==='from'),to:refs.find(r=>r.edge===i&&r.which==='to')}));
}

function route(start,end,boxes,occupied,ports) {
  const xs=new Set([start.x,end.x]),ys=new Set([start.y,end.y]);
  for(const b of boxes)for(const n of [44,68,92,116,140,164]) {
    xs.add(b.x-n);xs.add(b.x+b.width+n);ys.add(b.y-n);ys.add(b.y+b.height+n);
  }
  for(const ref of ports){xs.add(ref.stub.x);ys.add(ref.stub.y);}
  const x=[...xs].sort((a,b)=>a-b),y=[...ys].sort((a,b)=>a-b),width=x.length;
  const index=(p)=>y.indexOf(p.y)*width+x.indexOf(p.x);
  const point=i=>({x:x[i%width],y:y[Math.floor(i/width)]});
  const begin=index(start),finish=index(end),heap=new Heap(),costs=new Map(),previous=new Map();
  const first=`${begin}:0`;costs.set(first,0);heap.push({key:first,index:begin,dir:0,score:length(start,end),cost:0});
  const obstacles=boxes.map(b=>inflate(b,12)),cache=new Map();
  const test=(a,b)=>{
    const key=`${a.x},${a.y}:${b.x},${b.y}`;
    if(cache.has(key))return cache.get(key);
    let cost=0;
    const bounds=segmentBox(a,b),vertical=near(a.x,b.x);
    if(obstacles.some(box=>intersects(bounds,box)))cost=Infinity;
    if(Number.isFinite(cost))for(const [p,q] of occupied) {
      const otherVertical=near(p.x,q.x);
      if(vertical===otherVertical) {
        const axis=vertical?'y':'x',fixed=vertical?'x':'y';
        if(near(a[fixed],p[fixed])&&Math.min(Math.max(a[axis],b[axis]),Math.max(p[axis],q[axis]))-
          Math.max(Math.min(a[axis],b[axis]),Math.min(p[axis],q[axis]))>.01){cost=Infinity;break;}
      } else {
        const cross=vertical?{x:a.x,y:p.y}:{x:p.x,y:a.y};
        if(intersects(bounds,segmentBox(p,q))) {
          if(Math.min(length(cross,p),length(cross,q))<18){cost=Infinity;break;}
          cost+=140;
        }
      }
    }
    cache.set(key,cost);return cost;
  };
  while(heap.items.length) {
    const current=heap.pop();
    if(current.cost!==costs.get(current.key))continue;
    if(current.index===finish) {
      const ps=[];let key=current.key;
      while(key){ps.push(point(Number(key.split(':')[0])));key=previous.get(key);}
      return simplify(ps.reverse());
    }
    const a=point(current.index),ix=current.index%width,iy=Math.floor(current.index/width);
    for(const [nx,ny,dir] of [[ix-1,iy,1],[ix+1,iy,1],[ix,iy-1,2],[ix,iy+1,2]]) {
      if(nx<0||ny<0||nx>=x.length||ny>=y.length)continue;
      const ni=ny*width+nx,b=point(ni),crossings=test(a,b);
      if(!Number.isFinite(crossings))continue;
      const turn=current.dir&&current.dir!==dir;
      if(turn&&occupied.some(([p,q])=>intersects(inflate({x:a.x,y:a.y,width:0,height:0},18),segmentBox(p,q))))continue;
      const cost=current.cost+length(a,b)+crossings+(turn?44:0),key=`${ni}:${dir}`;
      if(cost>=(costs.get(key)??Infinity))continue;
      costs.set(key,cost);previous.set(key,current.key);
      heap.push({key,index:ni,dir,cost,score:cost+length(b,end)});
    }
  }
  throw new Error('No collision-free orthogonal route; explicit layout review required');
}

export function routeFluentGraph(spec,cards,headings=[],{order='returns-first'}={}) {
  const ports=assignPorts(spec,cards,headings),allPorts=ports.flatMap(p=>[p.from,p.to]);
  const boxes=[...cards.values(),...headings],occupied=[],labels=[],result=[];
  const ordered=[...spec.edges.entries()].sort((a,b)=>order==='reverse'?b[0]-a[0]:
    order==='source'?a[0]-b[0]:Number(Boolean(b[1].loopback))-Number(Boolean(a[1].loopback))||a[0]-b[0]);
  for(const [index,e] of ordered) {
    const {from,to}=ports[index];
    let middle;
    const reserved=allPorts.filter(p=>p!==from&&p!==to).map(p=>inflate(segmentBox(p.point,p.stub),5));
    try {middle=route(from.stub,to.stub,[...boxes,...reserved],occupied,allPorts);}
    catch(error){throw new Error(`${e.from} -> ${e.to}: ${error.message}`);}
    const ps=simplify([from.point,...middle,to.point]);
    occupied.push(...segments(ps));
    result.push({id:`e${index}`,source:e.from,target:e.to,label:wrap(e.label),markerEnd:!e.undirected,
      style:{strokeDasharray:e.dashed},data:{points:ps,loopback:e.loopback,junctions:[]}});
  }
  placeFluentLabels(result,boxes);
  return result.sort((a,b)=>Number(a.id.slice(1))-Number(b.id.slice(1)));
}

export function placeFluentLabels(result,boxes) {
  const labels=[];
  for(const edge of [...result].sort((a,b)=>b.label.length-a.label.length)) {
    const ps=edge.data.points,label=edge.label,size=DESIGN_SYSTEM.typography.connectorLabelPx;
    const others=result.filter(r=>r!==edge).flatMap(r=>segments(r.data.points));
    let labelPos;
    if(label) {
      const lines=label.split('\n'),w=Math.max(...lines.map(s=>s.length))*size*.55+20,h=lines.length*size*1.15+12;
      const options=[];
      for(const [a,b] of segments(ps))for(const fraction of [.5,.3,.7,.1,.9,.2,.8,.4,.6,
        ...Array.from({length:Math.max(0,Math.floor(length(a,b)/8)-3)},(_,i)=>(16+i*8)/length(a,b))]) {
        const p={x:a.x+(b.x-a.x)*fraction,y:a.y+(b.y-a.y)*fraction};
        const box={x:p.x-w/2,y:p.y-h/2,width:w,height:h};
        if(box.x<4||box.y<4||boxes.some(b=>intersects(inflate(b,4),box))||labels.some(b=>intersects(inflate(b,5),box)))continue;
        if(others.some(([p,q])=>intersects(inflate(box,3),segmentBox(p,q))))continue;
        options.push({p,box,score:(near(a.y,b.y)?0:100)+Math.abs(fraction-.5)*20});
      }
      options.sort((a,b)=>a.score-b.score);
      if(!options.length) {
        for(const [a,b] of segments(ps))for(const fraction of [.5,.3,.7,.1,.9]) {
          const origin={x:a.x+(b.x-a.x)*fraction,y:a.y+(b.y-a.y)*fraction};
          for(const gap of [8,24,48,80])for(const sign of [-1,1]) {
            const horizontal=near(a.y,b.y);
            const p={x:origin.x+(horizontal?0:sign*(w/2+gap)),y:origin.y+(horizontal?sign*(h/2+gap):0)};
            const box={x:p.x-w/2,y:p.y-h/2,width:w,height:h};
            const endpoint={x:horizontal?origin.x:origin.x+sign*gap,y:horizontal?origin.y+sign*gap:origin.y};
            const leader=segmentBox(origin,endpoint);
            if(box.x<4||box.y<4||boxes.some(b=>intersects(inflate(b,4),box)||intersects(b,leader))||
              labels.some(b=>intersects(inflate(b,5),box)||intersects(inflate(b,3),leader))||others.some(([a,b])=>intersects(inflate(box,3),segmentBox(a,b))))continue;
            if(others.some(([a,b])=>intersects(leader,segmentBox(a,b))))continue;
            options.push({p,box,leader:{from:origin,to:endpoint},score:gap+Math.abs(fraction-.5)*20});
          }
        }
        options.sort((a,b)=>a.score-b.score);
      }
      if(!options.length)throw new Error(`No clear label position for ${edge.source} -> ${edge.target}: ${label}; route=${JSON.stringify(ps)}`);
      labelPos=options[0].p;labels.push(options[0].box);
      edge.data.labelLeader=options[0].leader;
    }
    edge.data.labelPos=labelPos;
  }
}

export function spaceFluentCards(spec,cards) {
  for(const e of spec.edges) {
    const a=cards.get(e.from),b=cards.get(e.to);
    if(a===b||a.y+a.height<=b.y||b.y+b.height<=a.y)continue;
    const [left,right]=a.x<b.x?[a,b]:[b,a];
    const width=Math.max(...wrap(e.label).split('\n').map(s=>s.length))*DESIGN_SYSTEM.typography.connectorLabelPx*.55+20;
    const needed=Math.max(72,width+24),gap=right.x-left.x-left.width;
    if(gap>=needed)continue;
    const threshold=right.x;
    for(const box of cards.values())if(box.x>=threshold)box.x+=needed-gap;
  }
}
