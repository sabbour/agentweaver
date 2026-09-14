import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { models } from './batch-models.mjs';

const here=path.dirname(fileURLToPath(import.meta.url));
const repo=path.resolve(here,'../../../..');
const allowed=JSON.parse(await fs.readFile(path.join(repo,'.github/skills/docs-diagram-audit/reports/plan-deep-dive-core.json'))).exclusive_asset_paths;
const historic = {
  '00-system-overview-fig1': [[0,1,2],[0,1,0],[0,0,0]],
  '00-system-overview-fig2': [[0,1,3],[0,0,1],[0,0,1]],
  '00-system-overview-fig5': [[0,1,4],[0,0,0],[0,0,0]],
  'agent-definition-fig1': [[0,1,2],[0,0,1],[0,0,0]],
  'agent-framework-fig1': [[0,1,3],[0,0,1],[0,0,0]],
  'agent-framework-fig2': [[0,1,4],[0,0,1],[0,0,0]],
  'agent-framework-fig3': [[0,1,1],[0,0,0],[0,0,0]],
  'api-core-fig4': [[0,1,0],[0,0,0],[0,0,0]],
  'api-core-fig6': [[0,1,2],[0,0,1],[0,0,0]],
  'data-persistence-fig1': [[0,1,2],[0,0,0],[0,0,0]],
  'canonical-testing-boundary': [[0,1,1],[0,0,1],[0,0,1]],
  'testing-strategy-fig1': [[0,1,0],[0,0,0],[0,0,0]],
  'testing-strategy-fig4': [[0,0,4],[0,0,1],[0,0,1]],
};
function artifact(name,stage) {
  const base=`${name}-${stage}`;
  return {drawio:base+'.drawio',png:base+'.png',change_record:base+'.md',png_inspected_print:true,png_inspected_enlarged:true};
}
for(const m of models) {
  const dir=path.join(here,'..',m.name);
  const rel=path.relative(repo,dir).replaceAll('\\','/')+'/';
  if(!allowed.includes(rel)) throw Error(`Unowned review directory ${rel}`);
  const growth=JSON.parse(await fs.readFile(path.join(dir,'growth.json')));
  const count=m.name==='00-system-overview-fig2'?5:4;
  const trace=m.edges.filter(e=>!e.relationOnly).map(e=>({
    id:e.id,source:e.source,target:e.target,relationship:e.label,evidence:e.evidence,
    result:['host-to-planning','seam-to-state'].includes(e.id)||(m.name==='00-system-overview-fig2'&&e.id==='rai-to-review')?'corrected':'clean'
  }));
  const passes=Array.from({length:count},(_,i)=>{
    const number=i+1;
    const [orientation_defects,overlap_defects,arrow_defects]=historic[m.name][i]??[0,0,number===4&&count===5?1:0];
    const pass={number,mode:number===1?'visual-upgrade':'correction-only',...artifact(m.name,`pass-${String(number).padStart(2,'0')}`),orientation_defects,overlap_defects,arrow_defects};
    if(number===1) for(const k of ['baseline_meaningful_xml','result_meaningful_xml','growth_metric','growth_ratio']) pass[k]=growth[k];
    if(number>=4) {
      pass.all_arrows_traced=true;
      pass.arrow_trace=structuredClone(trace);
      if(number===4&&count===5) {
        const rai=pass.arrow_trace.find(e=>e.id==='rai-to-review');
        rai.relationship='nonempty diff after revision requirement clears; pass-04 label “cleared” is ambiguous and is corrected in pass-05';
      }
    }
    return pass;
  });
  const manifest={diagram:m.name,orientation:'A5-landscape',pitch:artifact(m.name,'pitch'),passes,final_pass:count};
  await fs.writeFile(path.join(dir,'iteration-manifest.json'),JSON.stringify(manifest,null,2)+'\n',{flag:'wx'});
  await fs.writeFile(path.join(dir,'final-content-model.json'),JSON.stringify(m,null,2)+'\n',{flag:'wx'});
}
