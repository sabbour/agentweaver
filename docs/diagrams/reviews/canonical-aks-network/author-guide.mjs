import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { DESIGN_TOKENS } from '../../../../scripts/docs/drawio-generator.mjs';
import { drawioExportArgs, verifyDrawioVersion } from '../../../../scripts/docs/diagram-sources.mjs';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..');
const reviewRoot = path.join(repo, 'docs', 'diagrams', 'reviews');
const cli = path.join(reviewRoot, 'canonical-aks-network', 'renderer-temp', 'desktop', 'draw.io.exe');
const template = fs.readFileSync(path.join(repo, 'docs', 'diagrams', 'drawio', 'fluent-template.drawio'), 'utf8');
const library = fs.readFileSync(path.join(repo, 'docs', 'diagrams', 'drawio', 'fluent-library.xml'), 'utf8');
if (!template.includes('background="#efeae7"') || !library.includes('Agentweaver Fluent card')) throw new Error('Design system changed');
const C = DESIGN_TOKENS.colors;
const tones = DESIGN_TOKENS.badges;
const models = [];
const esc = x => String(x ?? '').replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;');
const k = type => `shape=mxgraph.kubernetes.icon;prIcon=${type};`;
const azure = p => `shape=image;image=img/lib/azure2/${p}.svg;imageAspect=0;aspect=fixed;`;
const symbols = {
  pod: [k('pod'), 'native:kubernetes'],
  service: [k('svc'), 'native:kubernetes'],
  gateway: [k('crd'), 'native:kubernetes'],
  account: [k('sa'), 'native:kubernetes'],
  process: ['shape=process;', 'native:flowchart'],
  decision: ['shape=rhombus;', 'native:flowchart'],
  person: ['shape=umlActor;', 'native:uml'],
  cloud: ['shape=cloud;', 'native:cloud'],
  vault: [azure('security/Key_Vaults'), 'native:azure'],
  identity: [azure('identity/Managed_Identities'), 'native:azure'],
  store: ['shape=cylinder3;size=10;', 'native:database'],
  product: ['shape=hexagon;', 'custom:agentweaver'],
};
const runtime = 'apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:528-752,956-987,1267-1303';
const host = 'apps/Agentweaver.AgentHost/Program.cs:258-350,377-407; AgentHostRuntimeState.cs:160-173; AgentHostStartupService.cs:124-245';
const network = 'k8s/base/networkpolicy-sandbox.yaml:43-137; networkpolicy-agenthost-egress.yaml:46-107; networkpolicy-agenthost.yaml:28-73; networkpolicy-mcp.yaml:60-79';
const routes = 'k8s/base/gateway.yaml:9-39; httproute-api.yaml:19-63; mcp-httproute.yaml:14-46; httproute-frontend.yaml:22-34; api-service.yaml:9-17; mcp-service.yaml:9-17; frontend-service.yaml:9-17';
const preview = 'apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:213-276,1116-1167; k8s/base/gateway-preview.yaml:12-54';
const identity = 'scripts/azure/steps/15-setup-identity.mjs:245-260,314-423';
const broker = 'apps/Agentweaver.Api/Sandbox/RunGitHubCapabilityCredentialProvider.cs:78-119; apps/Agentweaver.Api/Auth/GitHubCapabilityBroker.cs:81-117';
const mcp = 'apps/Agentweaver.Mcp/Tools/CoordinatorTools.cs:14-190; RunTools.cs:137-302,366-394; TeamTools.cs:12-79';
function model(name, title, takeaway, pitch) {
  const m = { name, title, takeaway, pitch, nodes: [], edges: [], groups: [], notes: [] };
  models.push(m);
  return m;
}
function node(m, id, title, subtitle, meta, badge, icon, x, y, w=230, h=104, tone='teal', evidence=runtime) {
  m.nodes.push({ id, title, subtitle, meta, badge, icon, x, y, w, h, tone, evidence });
}
function edge(m, id, source, target, label, evidence, points=[], returnFlow=false, anchors='') {
  m.edges.push({ id, source, target, label, evidence, points, returnFlow, anchors });
}
function group(m, id, title, x,y,w,h) { m.groups.push({id,title,x,y,w,h}); }
function note(m,id,title,text,x,y,w,h=42) { m.notes.push({id,title,text,x,y,w,h}); }

const life = model('guide-architecture-aks-fig1', 'AgentHost: bind once, serve ready',
  'HTTP reachability is not setup readiness; claim lifetime can span Assistant turns.',
  ['Control plane claims a warm pod', 'Configured AgentHost serves turns']);
group(life,'l-launch','01  CONTROL-PLANE LAUNCH',20,58,787,143);
group(life,'l-setup','02  PURPOSE-BOUND SETUP',20,219,787,143);
group(life,'l-turns','03  TURN AND CLAIM LIFETIME',20,380,787,147);
node(life,'claim','Claim warm pod','Persist claim; wait for binding','Shared pool: 2 warm pods','CLAIM','product',34,87,230,96,'lavender',runtime);
node(life,'reachable','Probe listener','/healthz success: reachable','200 can mean standby','HEALTH','process',298,87,230,96,'teal',runtime+'; apps/Agentweaver.Api/Sandbox/AgentHostReadinessProbe.cs:64-79');
node(life,'configure','Configure once','Run, purpose and capabilities','POST /configure','ONE SHOT','process',562,87,230,96,'marigold',host);
node(life,'workspace','Select workspace','Shared or verified local checkout','LocalReadOnly: no write-back','PURPOSE','store',562,248,230,96,'teal','apps/Agentweaver.AgentHost/PodLocalWorkspaceManager.cs:98-137,220-237; '+host);
node(life,'setup','Finish setup','Assistant skips project checkout','Accepted binding stays consumed','SETUP','process',298,248,230,96,'lavender',host);
node(life,'ready','Ready for A2A','Setup finished before response','IsReady gates traffic','READY','pod',34,248,230,96,'green',host);
node(life,'turn','Stream a turn','Run-bound bearer authentication','message:stream','SERVING','product',34,410,230,96,'green',host);
node(life,'retain','Retain Assistant','Successful turn keeps its pod','Renew MCP token, not configure','REUSE','pod',298,410,230,96,'lavender','apps/Agentweaver.Api/Assistant/RemoteOperatorAssistantAgent.cs:101-110,185-208; '+runtime);
node(life,'release','Release claim','Unregister and revoke capabilities','Active preview defers cleanup','CLEANUP','process',562,410,230,96,'neutral',runtime);
edge(life,'l1','claim','reachable','bound',runtime);
edge(life,'l2','reachable','configure','reachable',runtime);
edge(life,'l3','configure','workspace','accepted',host);
edge(life,'l4','workspace','setup','prepare',host);
edge(life,'l5','setup','ready','complete',host);
edge(life,'l6','ready','turn','dispatch',runtime+'; '+host);
edge(life,'l7','turn','retain','success','apps/Agentweaver.Api/Assistant/RemoteOperatorAssistantAgent.cs:185-208');
edge(life,'l8','retain','turn','next turn',runtime,[[413,520],[148,520]],true,'exitX=0.5;exitY=1;entryX=0.5;entryY=1;');
edge(life,'l9','turn','release','run ends / failure',runtime,[[148,548],[677,548]],false,'exitX=0.5;exitY=1;entryX=0.5;entryY=1;');
note(life,'l-409','CONFIGURATION BOUNDARY','Later valid configure attempts return 409, even after accepted setup fails.',28,559,770,20);

const sec = model('guide-architecture-aks-fig5','Credentials: authority stays in the control plane',
  'Separate Azure identities; separate Copilot, repository, turn and MCP capabilities.',
  ['API / Worker redeem authorized capability', 'AgentHost receives bounded run configuration']);
group(sec,'s-control','TRUSTED CONTROL PLANE',20,60,517,316);
group(sec,'s-host','ISOLATED AGENTHOST',552,60,255,316);
group(sec,'s-delivery','DISTINCT DELIVERY CHANNELS',20,396,787,154);
node(sec,'sa','API + Worker SAs','Separate Kubernetes RBAC','AKS OIDC federation','FEDERATE','account',34,89,230,108,'teal',identity);
node(sec,'mi','API managed identity','Secrets User + Secrets Officer','agentweaver-api-identity','PRIVILEGED','identity',292,89,230,108,'marigold',identity);
node(sec,'vault','Azure Key Vault','Credential and app-secret authority','Runtime and CSI consumers','VAULT','vault',292,249,230,108,'marigold',broker);
node(sec,'broker','Capability broker','Fence run + purpose before/after read','No ambient user-token lookup','RUN BOUND','product',34,249,230,108,'lavender',broker);
node(sec,'hmi','AgentHost identity','Separate ServiceAccount federation','No Key Vault role assignments','NO VAULT','identity',566,89,226,108,'neutral',identity);
node(sec,'configured','AgentHost runtime','One-time /configure delivery','Copilot or BYOK; repo separate','IN MEMORY','pod',566,249,226,108,'green',runtime+'; apps/Agentweaver.AgentHost/AgentHostGitHubCapabilityCredentialProvider.cs:9-23');
node(sec,'csi','CSI app secrets','SecretProviderClass configuration','Files + synced secretKeyRef','API / WORKER','service',34,425,230,108,'teal','k8s/base/api-deployment.yaml:414-419; worker-deployment.yaml:265-270; secret-provider-class.yaml:1-62');
node(sec,'oauth','OAuth certificates','API runtime SecretClient','Usable active / previous versions','API ONLY','vault',292,425,230,108,'teal','apps/Agentweaver.Api/Program.cs:238-246,905-911; apps/Agentweaver.Api/Auth/OAuth/OAuthServerConfiguration.cs:319-377');
node(sec,'mcp','MCP resource server','Agentweaver broker JWT only','Exact /mcp + mcp:invoke','NO MOUNTS','service',566,425,226,108,'green','k8s/base/mcp-deployment.yaml:45-55,83-84; apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs:65-83');
edge(sec,'s1','sa','mi','federate',identity);
edge(sec,'s2','mi','vault','authorize',identity);
edge(sec,'s3','vault','broker','redeem',broker);
edge(sec,'s4','broker','configured','configure',runtime,[[149,386],[679,386]],false,'exitX=0.5;exitY=1;entryX=0.5;entryY=1;');
edge(sec,'s5','hmi','configured','pod identity',identity);
edge(sec,'s6','vault','csi','app secrets','k8s/base/secret-provider-class.yaml:1-62',[[542,303],[542,408],[149,408]],false,'exitX=1;exitY=0.5;entryX=0.5;entryY=0;');
edge(sec,'s7','vault','oauth','cert versions','apps/Agentweaver.Api/Auth/OAuth/OAuthServerConfiguration.cs:319-377');
edge(sec,'s8','configured','mcp','Assistant JWT','apps/Agentweaver.Api/Auth/OAuth/OperatorAssistantBrokerTokenIssuer.cs:23-37,102-113',[],false,'exitX=1;exitY=0.5;entryX=1;entryY=0.5;');
note(sec,'s-separation','NO AGENTHOST → VAULT EDGE','Network reachability is not authorization. Repository, A2A and MCP tokens are not Copilot credentials.',28,558,770,22);

const net = model('canonical-aks-network','AKS network: two ingress planes',
  'Routes select Services; additive policies bound AgentHost access, not a vault credential path.',
  ['Application and preview ingress', 'Bounded AgentHost network access']);
group(net,'n-app','APPLICATION ORIGIN  •  EXACT MATCHES / LONGER PREFIXES BEAT /',20,60,787,133);
group(net,'n-preview','PREVIEW ORIGIN  •  API MANAGES RESOURCES, NOT BROWSER TRAFFIC',20,218,787,133);
group(net,'n-egress','EFFECTIVE AGENTHOST POLICY UNION',20,376,787,154);
const xs=[32,192,352,512,672];
node(net,'client','Client','Browser / MCP','Public app origin','HTTPS','person',xs[0],90,124,85,'neutral',routes);
node(net,'gateway','App Gateway','agentweaver-gateway','TLS :443','CRD','gateway',xs[1],90,124,85,'teal',routes);
node(net,'routes','HTTPRoutes','API / MCP / /','Exact OAuth routes below','ROUTE','gateway',xs[2],90,124,85,'teal',routes);
node(net,'services','Services','API/MCP :8080','Frontend :80','CLUSTER IP','service',xs[3],90,124,85,'teal',routes);
node(net,'pods','App pods','API / MCP / web','All target :8080','WORKLOAD','pod',xs[4],90,124,85,'green',routes);
node(net,'browser','Browser','{token}-preview','Separate hostname','HTTPS','person',xs[0],248,124,85,'neutral',preview);
node(net,'pgateway','Preview GW','preview-gateway','TLS :443','CRD','gateway',xs[1],248,124,85,'lavender',preview);
node(net,'proute','HTTPRoute','preview-{token}','Host rewrite: localhost','DYNAMIC','gateway',xs[2],248,124,85,'lavender',preview);
node(net,'pservice','Service','preview-{token}:80','Run-label selector','DYNAMIC','service',xs[3],248,124,85,'lavender',preview);
node(net,'agenthost','AgentHost','Preview target port','Gateway: 3000–9000','KATA POD','pod',xs[4],248,124,85,'green',preview+'; '+network);
node(net,'internal','API / MCP','Selector-based TCP 8080','DNS: UDP/TCP 53 separately','EAST–WEST','service',34,407,230,103,'teal',network);
node(net,'public','Public HTTPS','TCP 443, IPv4 + IPv6','Private/link-local exclusions','WORLD','cloud',298,407,230,103,'green',network);
node(net,'bounds','Additive allows','API/Worker → A2A :8088','Preview range includes 8088','CAUTION','decision',562,407,230,103,'marigold',network);
for(const [a,b] of [['client','gateway'],['gateway','routes'],['routes','services'],['services','pods']]) edge(net,`n-${a}`,a,b,'',routes);
for(const [a,b] of [['browser','pgateway'],['pgateway','proute'],['proute','pservice'],['pservice','agenthost']]) edge(net,`n-${a}`,a,b,'',preview);
edge(net,'n-internal','agenthost','internal','TCP 8080',network,[[734,362],[149,362]],false,'exitX=0.5;exitY=1;entryX=0.5;entryY=0;');
edge(net,'n-public','agenthost','public','TCP 443',network,[[810,290],[810,390],[413,390]],false,'exitX=1;exitY=0.5;entryX=0.5;entryY=0;');
note(net,'n-routes','APP ROUTES','API: /api, /auth, /openapi + exact OAuth/discovery. MCP: /mcp + resource metadata; /mcp/health → /healthz.',28,538,770,20);
note(net,'n-exclusions','EGRESS EXCLUSIONS','10/8, 172.16/12, 192.168/16, 169.254/16; fc00::/7, fe80::/10. Not FQDN-only; no vault authority implied.',28,560,770,20);

const tools = model('guide-example-scenarios-fig3','MCP lifecycle: choose one launch path',
  'Queue pickup and explicit starts are alternatives; inspection and approvals remain explicit.',
  ['Authorize and prepare project work', 'Start, observe and review one run']);
group(tools,'m-prep','PREPARE AUTHORITY AND A CONFIRMED TEAM',20,59,787,114);
node(tools,'auth','Authorize','MCP OAuth / Entra identity','Repo capability is separate','SIGN IN','person',34,87,230,69,'teal','apps/Agentweaver.Mcp/Tools/GitHubAuthTools.cs:10-35; docs/guide/mcp-cli.md');
node(tools,'project','Choose project','project_list / project_create','Check provider + repo readiness','PROJECT','product',298,87,230,69,'lavender','apps/Agentweaver.Mcp/Tools/ProjectTools.cs:13-101; GitHubAuthTools.cs:50-106');
node(tools,'team','Confirm team','team_cast: proposal first','confirm_proposal_id or confirm','TEAM','product',562,87,230,69,'green',mcp);
group(tools,'m-launch','CHOOSE ONE  •  DO NOT QUEUE AND EXPLICITLY START THE SAME TASK',20,193,787,167);
node(tools,'manual','Define outcome','coordinator_start','defineOutcome; autopilot=false','MANUAL','product',34,222,230,120,'lavender',mcp);
node(tools,'direct','Start and poll','run_task','Default direct; creates NEW run','DIRECT','process',298,222,230,120,'teal',mcp);
node(tools,'backlog','Queue for heartbeat','backlog_capture_task','then backlog_move_to_ready','PICKUP','product',562,222,230,120,'marigold','apps/Agentweaver.Mcp/Tools/BacklogTools.cs:27-39,79-92; apps/Agentweaver.Api/Coordinator/CoordinatorPickupService.cs:183-245');
group(tools,'m-observe','OBSERVE, INSPECT AND DECIDE  •  NOT AUTOMATIC SUCCESS',20,385,787,148);
node(tools,'observe','Observe / steer','coordinator_work_plan_get','coordinator_children_get','READ / CONTROL','product',34,414,230,99,'lavender',mcp);
node(tools,'files','Inspect files','run_show_artifacts','then run_get_file','LIST FIRST','process',298,414,230,99,'teal',mcp);
node(tools,'review','Review when gated','run_review(approved: bool)','true approves; false declines','HUMAN','person',562,414,230,99,'green',mcp);
edge(tools,'m1','auth','project','authorize',mcp);
edge(tools,'m2','project','team','prepare',mcp);
edge(tools,'m3','manual','observe','confirm',mcp,[],false,'exitX=0.5;exitY=1;entryX=0.5;entryY=0;');
edge(tools,'m4','direct','observe','new run',mcp,[[413,371],[210,371]],false,'exitX=0.5;exitY=1;entryX=0.77;entryY=0;');
edge(tools,'m5','backlog','observe','reserved run','apps/Agentweaver.Api/Coordinator/CoordinatorPickupService.cs:183-245',[[677,380],[260,380]],false,'exitX=0.5;exitY=1;entryX=0.98;entryY=0;');
edge(tools,'m6','observe','files','list',mcp);
edge(tools,'m7','files','review','inspect',mcp);
note(tools,'m-confirm','MANUAL GATE','coordinator_outcome_spec_get → coordinator_outcome_spec_confirm (or coordinator_outcome_spec_revise).',42,302,210,38);
note(tools,'m-pickup','HEARTBEAT','Atomically claims Ready item. Pickup autopilot controls confirmation, not tool or merge approval.',570,301,212,34);
note(tools,'m-direct','BOUNDED WAIT','Returns artifacts, a gate, a next action, a timeout, or a failure. Never assume completion.',306,301,212,34);
note(tools,'m-outcomes','REVIEW PARITY','Request changes: web/REST review, not run_review. coordinator_steer is separate. memory_export is optional.',28,541,770,20);
note(tools,'m-gates','COLLECTIVE GATES','Children: Agent → Assemble-ready. Selected workflow gates apply to combined output, not per child.',28,564,770,18);

function cell(id,value,style,x,y,w,h,parent='1') {
  return `<mxCell id="${esc(id)}" value="${esc(value)}" style="${esc(style)}" vertex="1" parent="${parent}"><mxGeometry x="${x}" y="${y}" width="${w}" height="${h}" as="geometry"/></mxCell>`;
}
const textStyle = `text;html=1;whiteSpace=wrap;fillColor=none;strokeColor=none;fontFamily=Segoe UI;fontColor=${C.ink};align=left;verticalAlign=middle;spacing=0;`;
function label(id,text,x,y,w,h,size=11,bold=false,color=C.ink,parent='1',mono=false) {
  return cell(id,text,`${textStyle}fontSize=${size};fontStyle=${bold?1:0};fontColor=${color};${mono?'fontFamily=Cascadia Code;':''}`,x,y,w,h,parent);
}
function document(m,pitch=false) {
  const cells=[];
  cells.push(label('title',m.title,26,14,775,26,21,true));
  if (pitch) {
    for (let i=0;i<2;i++) {
      const x=60+i*405;
      cells.push(cell(`pitch-${i}`,`<b>${esc(m.pitch[i])}</b><br><br>${esc(i ? m.takeaway : 'Source-grounded initial overview; detailed boundaries follow in visual upgrade.')}`,
        `rounded=1;arcSize=16;whiteSpace=wrap;html=1;fillColor=${C.surface};strokeColor=${C.stroke};shadow=1;fontFamily=Segoe UI;fontSize=18;fontColor=${C.ink};align=left;spacing=18;spacingTop=50;`,x,174,300,220));
      cells.push(cell(`pitch-icon-${i}`,'',`${symbols[i?'pod':'product'][0]}fillColor=${tones.teal.foreground};strokeColor=none;`,x+20,193,34,34));
      cells.push(cell(`pitch-accent-${i}`,'',`rounded=1;fillColor=${tones.teal.foreground};strokeColor=none;`,x,174,5,220));
    }
    cells.push(edgeXml({id:'pitch-flow',source:'pitch-0',target:'pitch-1',label:'authorized flow',points:[]}));
  } else {
    cells.push(label('takeaway',m.takeaway,28,41,770,16,11,false,C.inkMuted));
    for (const g of m.groups) {
      cells.push(cell(g.id,'',`rounded=1;arcSize=8;fillColor=${C.group1};strokeColor=#e2ddd9;strokeWidth=1;`,g.x,g.y,g.w,g.h));
      cells.push(label(`${g.id}-title`,g.title,g.x+12,g.y+6,g.w-24,19,11,true,C.inkStrong));
    }
    for (const n of m.nodes) {
      const tone=tones[n.tone];
      const compact=n.h<90 || (m===tools && n.h===120);
      const short=n.h<80;
      cells.push(cell(n.id,'',`rounded=1;arcSize=16;absoluteArcSize=1;fillColor=${C.surface};strokeColor=${C.stroke};strokeWidth=1;shadow=1;`,n.x,n.y,n.w,n.h));
      cells.push(cell(`${n.id}-accent`,'',`rounded=1;arcSize=100;fillColor=${tone.foreground};strokeColor=none;`,0,0,5,n.h,n.id));
      cells.push(cell(`${n.id}-icon`,'',`${symbols[n.icon][0]}fillColor=${tone.foreground};strokeColor=${tone.foreground};strokeWidth=1;`,12,10,24,24,n.id));
      const bw=n.w<150?70:80;
      cells.push(cell(`${n.id}-badge`,n.badge,`rounded=1;arcSize=100;whiteSpace=wrap;html=1;fillColor=${tone.background};strokeColor=none;fontColor=${tone.foreground};fontFamily=Segoe UI;fontSize=8;fontStyle=1;align=center;verticalAlign=middle;`,n.w-bw-10,8,bw,16,n.id));
      cells.push(label(`${n.id}-title`,n.title,12,short?29:compact?31:36,n.w-24,short?14:18,compact?11:13,true,C.ink,n.id));
      cells.push(label(`${n.id}-sub`,n.subtitle,12,short?44:compact?49:58,n.w-24,short?12:16,compact?9:11,false,C.inkMuted,n.id));
      cells.push(label(`${n.id}-meta`,n.meta,12,short?57:compact?65:n.h-20,n.w-24,short?10:14,compact?8:9,false,C.inkFaint,n.id,true));
    }
    for (const e of m.edges) cells.push(edgeXml(e));
    for (const n of m.notes) {
      cells.push(label(`${n.id}-heading`,n.title,n.x,n.y,n.w,n.h<=22?10:12,n.h<=22?8:9,true,C.inkStrong));
      cells.push(label(`${n.id}-body`,n.text,n.x,n.y+(n.h<=22?10:12),n.w,n.h-(n.h<=22?10:12),n.h<=22?8:9,false,C.inkMuted));
    }
  }
  return `<?xml version="1.0" encoding="UTF-8"?>\n<mxfile host="Agentweaver" version="31.4.5" type="device" compressed="false"><diagram id="${m.name}" name="${esc(m.title)}"><mxGraphModel page="1" pageScale="1" pageWidth="827" pageHeight="583" background="${C.canvas}" grid="1" gridSize="10"><root><mxCell id="0"/><mxCell id="1" parent="0"/>${cells.join('\n')}</root></mxGraphModel></diagram></mxfile>\n`;
}
function edgeXml(e) {
  const style=`edgeStyle=orthogonalEdgeStyle;rounded=1;orthogonalLoop=1;jettySize=auto;html=1;endArrow=block;endFill=1;strokeColor=${e.returnFlow?C.loopback:C.connector};strokeWidth=1.5;fontFamily=Segoe UI;fontSize=9;fontColor=${C.inkStrong};labelBackgroundColor=${C.surface};jumpStyle=arc;jumpSize=6;${e.returnFlow?'dashed=1;dashPattern=8 6;':''}${e.anchors||''}`;
  return `<mxCell id="${e.id}" value="${esc(e.label)}" style="${esc(style)}" edge="1" parent="1" source="${e.source}" target="${e.target}"><mxGeometry relative="1" as="geometry">${e.points.length?`<Array as="points">${e.points.map(([x,y])=>`<mxPoint x="${x}" y="${y}"/>`).join('')}</Array>`:''}</mxGeometry></mxCell>`;
}
const [action,passArg] = process.argv.slice(2);
const pass=Number(passArg);
if (!['pitch','upgrade','copy','export','model'].includes(action)) throw new Error('Use pitch, upgrade, copy N, export N, or model');
if (action==='export') verifyDrawioVersion({command:cli,prefixArgs:[]});
for (const m of models) {
  const dir=path.join(reviewRoot,m.name);
  fs.mkdirSync(dir,{recursive:true});
  if(action==='model') {
    fs.writeFileSync(path.join(dir,'content-model.json'),JSON.stringify({...m,symbols,orientation:'A5-landscape',page_units:'100 draw.io units per inch; 827 x 583 approximates 210 x 148 mm',research_agents:['guide-runtime-evidence','guide-network-evidence','guide-workflow-evidence'],model:'gpt-6-astra'},null,2)+'\n');
    continue;
  }
  const suffix=action==='pitch'||(action==='export'&&!pass)?'pitch':`pass-${String(action==='upgrade'?1:pass).padStart(2,'0')}`;
  const source=path.join(dir,`${m.name}-${suffix}.drawio`);
  if (action==='pitch'||action==='upgrade') {
    if(fs.existsSync(source)) throw new Error(`Immutable review artifact exists: ${source}`);
    fs.writeFileSync(source,document(m,action==='pitch'));
  } else if(action==='copy') {
    if(pass<2||fs.existsSync(source))throw new Error('Copy requires a new correction pass >=2');
    fs.copyFileSync(path.join(dir,`${m.name}-pass-${String(pass-1).padStart(2,'0')}.drawio`),source);
  } else {
    const out=source.replace(/\.drawio$/,'.png');
    const profile=path.join(reviewRoot,'canonical-aks-network','renderer-temp',`${m.name}-${suffix}-profile`);
    execFileSync(cli,[`--user-data-dir=${profile}`,...drawioExportArgs(source,out,'png')],{cwd:repo,stdio:'inherit'});
    if(!fs.existsSync(out))throw new Error(`Export missing: ${out}`);
  }
  console.log(m.name,suffix);
}
