const api = 'apps/Agentweaver.Api/';
const web = 'apps/web/src/';
const n = (id, title, subtitle, detail, meta, badge, evidence, shape = 'process') =>
  ({ id, title, subtitle, detail, meta, badge, evidence, shape });
const e = (source, target, label, evidence, extra = {}) =>
  ({ id: `${source}-to-${target}`, source, target, label, evidence, ...extra });
const model = (name, title, takeaway, groups, nodes, edges, scope) =>
  ({ name, title, takeaway, groups, nodes, edges, scope });
export const models = [
  model('frontend-fig1', 'Frontend: intent and projection',
    'The browser presents backend facts; the API remains the authority.',
    ['BROWSER · OPERATOR INTENT', 'BACKEND FACTS · UI PROJECTION'], [
      n('auth', 'AuthGate', 'Validated SPA session', 'REST and SSE use the session bearer', 'App.tsx', 'SESSION', web+'App.tsx:399-403', 'umlActor'),
      n('controls', 'Rendered controls', 'Status, timeline and graph', 'Operator decisions become new API calls', 'CoordinatorRunPage.tsx', 'PRESENT', web+'pages/CoordinatorRunPage.tsx:2842'),
      n('shell', 'AppShell', 'TopBar · LeftNav · project switcher', 'ProjectList + Notifications providers', 'AppShell.tsx:134-182', 'CONTEXT', web+'components/shell/AppShell.tsx:134-182'),
      n('projection', 'Client projection', 'Reducers combine backend facts', 'Topology is server-authored, not invented', 'CoordinatorRunPage.tsx', 'DERIVE', web+'pages/CoordinatorRunPage.tsx:2842'),
      n('pages', 'Route pages', 'Board · run · workspace', 'Route parameters select the current scope', 'App.tsx:80-127', 'ROUTES', web+'App.tsx:80-127'),
      n('live', 'Seed + live events', 'Independent REST and SSE inputs', 'Positive sequence IDs deduplicate events', 'useSeededRunStream.ts', 'MERGE', web+'hooks/useSeededRunStream.ts:43-151'),
      n('client', 'API client', 'Typed requests and error handling', 'API_URL origin + endpoint /api paths', 'config.ts:13-36', 'REQUEST', web+'config.ts:13-36'),
      n('api', 'Agentweaver API', 'Projects · runs · graph · events', 'Server owns persisted state and topology', 'CoordinatorRunPage.tsx', 'AUTHORITY', web+'pages/CoordinatorRunPage.tsx:2842')
    ], [
      e('auth','shell','enter',web+'App.tsx:399-403'),
      e('shell','pages','contains',web+'App.tsx:80-127'),
      e('pages','client','request',web+'pages/CoordinatorRunPage.tsx:2842'),
      e('client','api','HTTP',web+'config.ts:13-36'),
      e('api','live','history + SSE',web+'hooks/useSeededRunStream.ts:43-151'),
      e('live','projection','events',web+'timeline/mergeRunEvents.ts:23-79'),
      e('projection','controls','render',web+'pages/CoordinatorRunPage.tsx:2842')
    ], 'Read direction: intent down the left; backend facts rise on the right.'),
  model('frontend-fig2', 'Routes: global and project scope',
    'App.tsx declares routes; AppShell supplies shared context, not a route registry.',
    ['GLOBAL · NO PROJECT PARAMETER', 'PROJECT · /projects/:projectId'], [
      n('router', 'App.tsx Routes', '/ · /overview · /projects', 'Global sessions, settings and assistant', 'App.tsx:80-100', 'GLOBAL', web+'App.tsx:80-100'),
      n('shell', 'AppShell', 'ProjectListProvider', 'NotificationsProvider wraps shell content', 'AppShell.tsx:134-182', 'CONTEXT', web+'components/shell/AppShell.tsx:134-182'),
      n('operator', 'Operator destinations', '/console → /assistant', '/sessions is global; ?project scopes it', 'App.tsx:93-100,130-135', 'REDIRECT', web+'App.tsx:93-100,130-135'),
      n('project', 'Project route family', 'Dashboard · board · flow', 'Orchestrations and /:runId inspection', 'App.tsx:104-126', 'PROJECT', web+'App.tsx:104-126'),
      n('admin', 'Platform settings', '/platform-settings', 'Non-admin users redirect to /overview', 'App.tsx:87-92', 'GUARDED', web+'App.tsx:87-92', 'hexagon'),
      n('assets', 'Project resources', 'Workspace · settings · team', 'Cast · agent memory · memories · skills', 'App.tsx:110-117', 'RESOURCES', web+'App.tsx:110-117'),
      n('global-observe', 'Global observability', '/observability · /traces · /agents', 'Redirect pages resolve destination scope', 'App.tsx:99-101', 'REDIRECT', web+'App.tsx:99-101'),
      n('operations', 'Project operations', 'Observability · workflows', 'Diagnostics · heartbeat · cluster', 'App.tsx:118-125', 'OPERATE', web+'App.tsx:118-125')
    ], [
      e('shell','router','wraps',web+'App.tsx:80-127'),
      e('router','operator','declares',web+'App.tsx:93-100'),
      e('router','project','declares',web+'App.tsx:104-126',{route:'middle'}),
      e('project','assets','also includes',web+'App.tsx:110-117'),
      e('assets','operations','same prefix',web+'App.tsx:118-125')
    ], 'Cards group declared paths, not navigation dependencies. Global admin/observability are independent routes.'),
  model('frontend-fig3', 'Static hosting and API origins',
    'The Web host serves the SPA; API_URL is an origin or empty, never /api.',
    ['WEB HOST · STATIC DELIVERY', 'BROWSER · RUNTIME DESTINATIONS'], [
      n('browser', 'Browser request', 'Assets or a client-side route', 'The Web host is not the API host', 'Web/Program.cs:39-65', 'HTTP', 'apps/Agentweaver.Web/Program.cs:39-65','umlActor'),
      n('config', 'Runtime configuration', 'window.__AGENTWEAVER_CONFIG__', 'API_URL selects the API origin', 'config.ts:13-36', 'CONFIG', web+'config.ts:13-36'),
      n('static', 'Static file middleware', 'Default files + static assets', 'Non-HTML assets get immutable caching', 'Web/Program.cs:39-50', 'FILES', 'apps/Agentweaver.Web/Program.cs:39-50','folder'),
      n('client-origin', 'Origin resolution', 'Origin string, or "" = same-origin', 'Client endpoints append their own /api', 'config.ts:13-36', 'ORIGIN', web+'config.ts:13-36'),
      n('fallback', 'SPA route fallback', 'Unknown route → index.html', 'React handles the resulting route', 'Web/Program.cs:61-65', 'SPA', 'apps/Agentweaver.Web/Program.cs:61-65'),
      n('api-host', 'API destination', 'REST + authenticated fetch SSE', 'Same-origin still uses /api endpoints', 'api/sse.ts:239-337', 'API', web+'api/sse.ts:239-337'),
      n('docs-route', 'Documentation route', '/docs and /docs/{path}', 'Temporary redirect preserves suffix', 'Web/Program.cs:52-59', 'REDIRECT', 'apps/Agentweaver.Web/Program.cs:52-59'),
      n('external-docs', 'External documentation', 'Configured documentation base URL', 'Not the local SPA fallback', 'Web/Program.cs:52-59', 'EXTERNAL', 'apps/Agentweaver.Web/Program.cs:52-59','cloud')
    ], [
      e('browser','static','asset', 'apps/Agentweaver.Web/Program.cs:39-50'),
      e('static','fallback','unmatched', 'apps/Agentweaver.Web/Program.cs:61-65'),
      e('config','client-origin','supplies',web+'config.ts:13-36'),
      e('client-origin','api-host','requests',web+'api/sse.ts:239-337'),
      e('docs-route','external-docs','302 redirect','apps/Agentweaver.Web/Program.cs:52-59')
    ], 'Parallel concerns: static delivery, runtime API selection and external docs redirection.'),
  model('frontend-fig6', 'Entra sign-in and browser session',
    'The callback returns a one-time code; session exchange delivers the SPA bearer.',
    ['SIGN-IN · API + ENTRA', 'SESSION · PER-TAB BROWSER STATE'], [
      n('begin','Browser sign-in','Begin API Entra authorization','State binds the browser callback','AuthEndpoints.cs','AUTHORIZE',api+'Endpoints/AuthEndpoints.cs:398-455','umlActor'),
      n('exchange','Session exchange','POST the one-time exchange code','Returns validated token + browser session','AuthEndpoints.cs:398-455','EXCHANGE',api+'Endpoints/AuthEndpoints.cs:398-455'),
      n('entra','Microsoft Entra ID','Authenticate the user','Identity authority, not GitHub OAuth','AuthEndpoints.cs','IDENTITY',api+'Endpoints/AuthEndpoints.cs:398-455','mxgraph.azure2.azure_active_directory'),
      n('storage','Per-tab sessionStorage','Store the SPA bearer','No durable localStorage token','config.ts:60-138','TAB',web+'config.ts:60-138','cylinder3'),
      n('callback','API callback','Validate callback and state','Issue a one-time frontend exchange code','AuthEndpoints.cs:398-455','CALLBACK',api+'Endpoints/AuthEndpoints.cs:398-455'),
      n('peer','Same-origin peer tab','BroadcastChannel request/response','Transient token transfer to a new tab','config.ts:207-240','PEER',web+'config.ts:207-240'),
      n('frontend','Frontend callback','Receive exchange code','Do not treat the code as an access token','AuthEndpoints.cs:398-455','ONE-TIME',api+'Endpoints/AuthEndpoints.cs:398-455'),
      n('requests','Authenticated requests','REST and fetch-based SSE','Bearer token; API authorizes resources','api/sse.ts:239-337','BEARER',web+'api/sse.ts:239-337')
    ], [
      e('begin','entra','sign in',api+'Endpoints/AuthEndpoints.cs:398-455'),
      e('entra','callback','callback',api+'Endpoints/AuthEndpoints.cs:398-455'),
      e('callback','frontend','code',api+'Endpoints/AuthEndpoints.cs:398-455'),
      e('frontend','exchange','POST code',api+'Endpoints/AuthEndpoints.cs:398-455',{route:'outer'}),
      e('exchange','storage','session',web+'config.ts:60-138'),
      e('storage','peer','transfer',web+'config.ts:207-240'),
      e('storage','requests','bearer',web+'api/sse.ts:239-337',{route:'outer'})
    ], 'Only a one-time exchange code crosses the callback URL; the session token stays out of URLs.'),
  model('frontend-fig7', 'Live timeline: independent inputs',
    'REST seed and live SSE run concurrently, then merge into a guarded projection.',
    ['INDEPENDENT INPUTS', 'MERGE · PROJECT · RECOVER'], [
      n('run','Current run ID','Route chooses stream scope','Run/generation guards reject stale seeds','useSeededRunStream.ts:43-151','SCOPE',web+'hooks/useSeededRunStream.ts:43-151'),
      n('merge','Merge run events','Positive-sequence deduplication','Restricted sequence-zero singleton rules','mergeRunEvents.ts:23-79','MERGE',web+'timeline/mergeRunEvents.ts:23-79'),
      n('rest','Persisted event history','REST seed requested independently','A seed failure does not block live SSE','useSeededRunStream.ts:85-134','REST',web+'hooks/useSeededRunStream.ts:85-134','cylinder3'),
      n('project','Timeline projection','Reducers + server topology seed','Render graph, timeline, status and controls','CoordinatorRunPage.tsx','DERIVE',web+'pages/CoordinatorRunPage.tsx:2842'),
      n('sse','Live event transport','Authenticated fetch + credentials','Last-Event-ID resumes the cursor','api/sse.ts:239-337','SSE',web+'api/sse.ts:239-337'),
      n('buffer','Parser and event buffer','Dedupe and bounded retention','Cursor advances with accepted events','api/sse.ts:239-337','BUFFER',web+'api/sse.ts:239-337'),
      n('reconnect','Unexpected disconnect','Bounded reconnect backoff','Explicit reconnect reopens after gate action','api/sse.ts:239-337','RETRY',web+'api/sse.ts:239-337'),
      n('stop','done / terminal','Stop the current transport','A gate done is not universal run completion','api/sse.ts; RunEndpoints.cs','STOP',web+'api/sse.ts:239-337; '+api+'Endpoints/RunEndpoints.cs:514-555','doubleEllipse')
    ], [
      e('run','rest','seed',web+'hooks/useSeededRunStream.ts:85-134'),
      e('run','sse','live',web+'hooks/useSeededRunStream.ts:43-51',{route:'outer'}),
      e('rest','merge','seed events',web+'hooks/useSeededRunStream.ts:139-142',{route:'middle'}),
      e('sse','buffer','frames',web+'api/sse.ts:239-337'),
      e('buffer','merge','live events',web+'hooks/useSeededRunStream.ts:139-142',{route:'outer'}),
      e('merge','project','merged',web+'timeline/mergeRunEvents.ts:23-79'),
      e('sse','reconnect','disconnect',web+'api/sse.ts:239-337'),
      e('buffer','stop','done',web+'api/sse.ts:239-337')
    ], 'No REST→SSE prerequisite. Backend topology is an input; browser reducers do not author it.'),
  model('mcp-server-fig2','MCP: discover, consent, invoke',
    'API-owned OAuth issues broker tokens; MCP validates and forwards the same bearer.',
    ['DISCOVERY + API AUTHORIZATION', 'TOKEN USE + INDEPENDENT API CHECK'], [
      n('client','MCP client','/mcp challenge → resource metadata','Configured resource, issuer and scope','mcp-httproute.yaml:30-46','DISCOVER','k8s/base/mcp-httproute.yaml:30-46','umlActor'),
      n('token','API /oauth/token','Authorization code + PKCE','OpenIddict issues access / refresh tokens','Program.cs:982-998','TOKEN',api+'Program.cs:982-998'),
      n('authorize','API authorization server','Exact resource + mcp:invoke','OpenIddict owns OAuth endpoint state','OAuthAuthorizationServerEndpoints','VALIDATE',api+'Endpoints/OAuthAuthorizationServerEndpoints.cs:40-190'),
      n('refresh','Refresh grant checks','Exact resource + grant state','Family revocation can reject renewal','OAuthAuthorizationServerEndpoints','REFRESH',api+'Endpoints/OAuthAuthorizationServerEndpoints.cs:40-190'),
      n('entra','Microsoft Entra ID','Sign in when needed','API broker authenticates the human','OAuthAuthorizationServerEndpoints','IDENTITY',api+'Endpoints/OAuthAuthorizationServerEndpoints.cs:40-190','mxgraph.azure2.azure_active_directory'),
      n('mcp','MCP broker validation','Validate accepted broker JWT only','Tool call forwards that same bearer','McpBrokerAuthenticationHandler','INVOKE','apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs:64-115'),
      n('consent','Consent decision','Approve → authorization code','Deny → access_denied; grant may skip UI','OAuthAuthorizationServerEndpoints','CONSENT',api+'Endpoints/OAuthAuthorizationServerEndpoints.cs:40-190','hexagon'),
      n('api','API resource authorization','Forwarded broker bearer','API independently checks access','AgentweaverApiClient.cs:353-391','AUTHORIZE','apps/Agentweaver.Mcp/AgentweaverApiClient.cs:353-391')
    ], [
      e('client','authorize','discover issuer',api+'Endpoints/OAuthAuthorizationServerEndpoints.cs:40-190'),
      e('authorize','entra','if needed',api+'Endpoints/OAuthAuthorizationServerEndpoints.cs:40-190'),
      e('entra','consent','identity',api+'Endpoints/OAuthAuthorizationServerEndpoints.cs:40-190'),
      e('consent','token','code + PKCE',api+'Program.cs:982-998',{route:'outer'}),
      e('token','refresh','renewal',api+'Endpoints/OAuthAuthorizationServerEndpoints.cs:40-190'),
      e('token','mcp','broker bearer','apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs:64-115',{route:'outer'}),
      e('mcp','api','same bearer','apps/Agentweaver.Mcp/AgentweaverApiClient.cs:353-391')
    ], 'Gateway routes MCP + metadata to MCP; authorization/token/revocation/JWKS belong to the API.'),
  model('project-generation-model-settings-fig1','Generation preferences, not authority',
    'Three project preferences select models; execution admission separately authorizes use.',
    ['PERSISTED SELECTION + PRECEDENCE', 'FLOW CONSUMERS + AUTHORITY'], [
      n('settings','Project settings','Blueprint · workflow · outcome spec','Three nullable model preferences','ProjectSettingsPage.tsx:617-648','EDIT',web+'pages/ProjectSettingsPage.tsx:617-648'),
      n('blueprint','Blueprint generation','Resolved blueprint preference','Generation still requires admission','BlueprintEndpoints.cs:119-141','BLUEPRINT',api+'Endpoints/BlueprintEndpoints.cs:119-141'),
      n('record','Project record','Save nullable preference values','Preferences do not contain credentials','ProjectEndpoints.cs:815-817','PERSIST',api+'Endpoints/ProjectEndpoints.cs:815-817','cylinder3'),
      n('workflow','Fallback workflow generation','Resolved workflow preference','Used when no library workflow selected','BlueprintService.cs:765-773','FALLBACK',api+'Blueprints/BlueprintService.cs:765-773'),
      n('resolve','Generation model resolver','Project → per-flow configuration','Then shared generation model → default','GenerationModelOptions.cs:37-75','RESOLVE',api+'Generation/GenerationModelOptions.cs:37-75'),
      n('outcome','Coordinator spec drafter','Resolved outcome-spec preference','Coordinator input supplies the preference','CopilotCoordinatorSpecDrafter:145','SPEC',api+'Coordinator/CopilotCoordinatorSpecDrafter.cs:145'),
      n('caller','Caller + project authority','Execution-plan / provider admission','Selection never grants credential access','AiExecutionPlanService.cs','ADMIT',api+'Auth/AiExecutionPlanService.cs:293-330,384-402','umlActor'),
      n('invoke','Admitted model invocation','Authorized provider execution','Selected model and admitted access differ','BlueprintEndpoints.cs:119-141','EXECUTE',api+'Endpoints/BlueprintEndpoints.cs:119-141')
    ], [
      e('settings','record','save',api+'Endpoints/ProjectEndpoints.cs:815-817'),
      e('record','resolve','preferences',api+'Generation/GenerationModelOptions.cs:37-75'),
      e('resolve','blueprint','model',api+'Endpoints/BlueprintEndpoints.cs:140-141',{route:'middle'}),
      e('resolve','workflow','model',api+'Blueprints/BlueprintService.cs:765-773',{route:'middle',lane:425}),
      e('resolve','outcome','model',api+'Coordinator/CopilotCoordinatorSpecDrafter.cs:145'),
      e('caller','invoke','authorize',api+'Auth/AiExecutionPlanService.cs:293-330,384-402')
    ], 'Consumer cards are separate generation flows. The authority row is not a preference inheritance step.'),
  model('project-skills-fig1','Skills: catalog to safe delivery',
    'Only successful shared-filesystem writes produce pointers; all other delivery is inline.',
    ['ACQUIRE · VALIDATE · ASSIGN', 'DELIVERY BRANCHES · EXECUTION'], [
      n('sources','Skill sources','Checkout · repo · marketplace','Upload and manual entry are also inputs','SkillCatalogService.cs:335-372','ACQUIRE',api+'Skills/SkillCatalogService.cs:335-372','folder'),
      n('active','Active assignments','Look up skills for this agent','No active assignments → no skill block','SkillPromptComposer.cs:45-81','SELECT',api+'Skills/SkillPromptComposer.cs:45-81'),
      n('catalog','Project skill catalog','Parse / validate / content-hash upsert','Missing / malformed sources are tracked','SkillCatalogService:1045-1156','CATALOG',api+'Skills/SkillCatalogService.cs:1045-1156','cylinder3'),
      n('shared','Shared worktree available','Best-effort stale-folder cleanup','Attempt each assigned skill materialization','SkillPromptComposer.cs:63-106','WRITE',api+'Skills/SkillPromptComposer.cs:63-106','folder'),
      n('assign','Explicit agent assignment','Catalog skills bind to agents','Delivery selects active assigned entries','SkillPromptComposer.cs:49-54','ASSIGN',api+'Skills/SkillPromptComposer.cs:49-54'),
      n('pointer','Successful write only','Prompt metadata + SKILL.md path','Agent reads relevant skill instructions','SkillPromptComposer.cs:91-98,145','POINTER',api+'Skills/SkillPromptComposer.cs:91-98,145','folder'),
      n('defaults','Defaults preview / apply','Confirmed team + preview digest','Apply recomputes; stale digest rejects','SkillDefaultsService.cs:231-250','GUARDED',api+'Skills/SkillDefaultsService.cs:31-52,231-250','hexagon'),
      n('inline','Inline full instructions','Pod-local / unavailable filesystem','Also used for each failed materialization','SkillPromptComposer.cs:99-160','FALLBACK',api+'Skills/SkillPromptComposer.cs:99-160')
    ], [
      e('sources','catalog','validate',api+'Skills/SkillCatalogService.cs:1045-1156'),
      e('catalog','assign','assign',api+'Skills/SkillPromptComposer.cs:49-54'),
      e('assign','active','lookup',api+'Skills/SkillPromptComposer.cs:49-54',{route:'middle'}),
      e('active','shared','shared FS',api+'Skills/SkillPromptComposer.cs:63-106'),
      e('shared','pointer','write succeeds',api+'Skills/SkillPromptComposer.cs:91-98'),
      e('active','inline','no shared FS',api+'Skills/SkillPromptComposer.cs:108-123',{route:'outer'}),
      e('shared','inline','write fails',api+'Skills/SkillPromptComposer.cs:99-106',{route:'outer',lane:800})
    ], 'Defaults preview has no side effects; explicit apply is digest-checked. Cleanup is best-effort.'),
  model('projects-fig1','Project workspace provisioning',
    'Provider paths differ; only a healthy, initialized workspace becomes a persisted project.',
    ['REQUEST + PROVIDER POLICY', 'PROVISION · INITIALIZE · PERSIST'], [
      n('create','Create project request','Allocate stable project ID','GitHub origin consumes selection capability','ProjectEndpoints.cs:1232-1283','REQUEST',api+'Endpoints/ProjectEndpoints.cs:1232-1283','umlActor'),
      n('probe','Create directory + probe','Write / delete confirms workspace health','Provider returns the resolved handle','WorkspaceProvider implementations','PROBE',api+'Infrastructure/LocalFilesystemWorkspaceProvider.cs:33-62'),
      n('provider','Configured workspace provider','Resolve a project-specific path','Local and persistent-volume policies differ','ProjectService.cs:47-111','SELECT',api+'Projects/ProjectService.cs:47-111','hexagon'),
      n('git','Initialize or clone Git','Use the healthy workspace handle','Server resolves repository capability','ProjectService.cs:47-111','GIT',api+'Projects/ProjectService.cs:47-111','folder'),
      n('local','Local filesystem policy','Supplied absolute path is accepted','Otherwise workspace root / project ID','LocalFilesystemWorkspaceProvider','LOCAL',api+'Infrastructure/LocalFilesystemWorkspaceProvider.cs:33-62','folder'),
      n('persist','Scaffold and persist project','Project ownership is bootstrapped','Record stable project and workspace data','ProjectService.cs:47-111','PERSIST',api+'Projects/ProjectService.cs:47-111','cylinder3'),
      n('volume','Persistent-volume policy','Mount root / project ID','Ignore a caller-supplied workspace path','PersistentVolumeWorkspaceProvider','VOLUME',api+'Infrastructure/PersistentVolumeWorkspaceProvider.cs:29-75','folder'),
      n('failure','Provisioning failure','Error / rollback path','Do not report an active healthy project','ProjectService.cs:47-111','FAILURE',api+'Projects/ProjectService.cs:47-111','hexagon')
    ], [
      e('create','provider','create',api+'Projects/ProjectService.cs:47-111'),
      e('provider','local','local',api+'Infrastructure/LocalFilesystemWorkspaceProvider.cs:33-62'),
      e('provider','volume','volume',api+'Infrastructure/PersistentVolumeWorkspaceProvider.cs:29-75',{route:'outer'}),
      e('local','probe','resolved path',api+'Infrastructure/LocalFilesystemWorkspaceProvider.cs:33-62',{route:'middle'}),
      e('volume','probe','resolved path',api+'Infrastructure/PersistentVolumeWorkspaceProvider.cs:29-75',{route:'middle',lane:432}),
      e('probe','git','healthy',api+'Projects/ProjectService.cs:47-111'),
      e('git','persist','scaffold',api+'Projects/ProjectService.cs:47-111'),
      e('persist','failure','if create fails',api+'Projects/ProjectService.cs:47-111')
    ], 'GitHub creation uses a caller-bound single-use selection code, not a caller-supplied credential.'),
  model('projects-fig2','Project, run and execution ownership',
    'A project base checkout is not a run worktree, shared Git index or sandbox.',
    ['PROJECT-OWNED CONFIGURATION', 'RUN-OWNED EXECUTION'], [
      n('project','Project','Stable identity + defaults','Owns base checkout and project settings','ProjectService.cs:47-111','OWNER',api+'Projects/ProjectService.cs:47-111'),
      n('run','Run','Belongs to one project','Worktree and branch are run-specific','RunOrchestrator.cs:194-245','RUN',api+'Runs/RunOrchestrator.cs:194-245'),
      n('checkout','Stable base checkout','Provider provisions project path','Shared volume ≠ shared Git index','WorkspaceProvider implementations','BASE',api+'Infrastructure/PersistentVolumeWorkspaceProvider.cs:29-75','folder'),
      n('worktree','Run worktree + branch','Isolated execution changes','Not the stable project base checkout','RunOrchestrator.cs:277-317','ISOLATED',api+'Runs/RunOrchestrator.cs:277-317','folder'),
      n('team','Team + workflow files','Project-scoped definitions','Defaults configure subsequent execution','ProjectService.cs:47-111','CONFIG',api+'Projects/ProjectService.cs:47-111','folder'),
      n('sandbox','Sandbox / AgentHost','Executes against run workspace','Execution host is not workspace provider','RunOrchestrator.cs:277-317','EXECUTE',api+'Runs/RunOrchestrator.cs:277-317'),
      n('provider','Workspace provider','Provisions the project checkout','Local or persistent-volume path policy','WorkspaceProvider implementations','PROVISION',api+'Infrastructure/LocalFilesystemWorkspaceProvider.cs:33-62'),
      n('child','Child run contribution','Own branch + worktree + tree hash','Contributes artifact to collective assembly','RunOrchestrator.cs:277-317','CHILD',api+'Runs/RunOrchestrator.cs:277-317')
    ], [
      e('run','project','belongs to',api+'Runs/RunOrchestrator.cs:194-245'),
      e('project','checkout','owns',api+'Projects/ProjectService.cs:47-111'),
      e('project','team','configures',api+'Projects/ProjectService.cs:47-111',{route:'outer'}),
      e('provider','checkout','provisions',api+'Infrastructure/LocalFilesystemWorkspaceProvider.cs:33-62',{route:'outer',lane:18}),
      e('run','worktree','owns',api+'Runs/RunOrchestrator.cs:277-317'),
      e('sandbox','worktree','executes in',api+'Runs/RunOrchestrator.cs:277-317')
    ], 'Ownership relationships, not a lifecycle chain. Child worktrees contribute artifacts, not a shared index.'),
  model('repo-blueprint-suggestions-fig1','Repository suggestions are heuristic',
    'Anonymous metadata feeds deterministic matching; suggestions never invoke a model.',
    ['INPUT + CURRENT CREDENTIAL BOUNDARY', 'METADATA · MATCHING · OUTCOMES'], [
      n('picker','Blueprint picker: Suggested','Repository string or GitHub URL','Generate is a separate tab and model path','BlueprintPicker.tsx:487-517','UI',web+'components/BlueprintPicker.tsx:487-517','umlActor'),
      n('metadata','GitHub metadata requests','Repository · languages · root contents','No bearer when boundary returns null','SuggestionService.cs:55-68','ANONYMOUS',api+'Blueprints/GitHubRepoBlueprintSuggestionService.cs:55-68','cloud'),
      n('parse','Suggestion service','Parse owner / repository','Invalid input returns fallback response','SuggestionService.cs:40-48','PARSE',api+'Blueprints/GitHubRepoBlueprintSuggestionService.cs:40-48'),
      n('match','Deterministic catalog matching','Signals + ordered heuristic rules','No model invocation or repository clone','SuggestionService.cs:70-84','HEURISTIC',api+'Blueprints/GitHubRepoBlueprintSuggestionService.cs:70-84'),
      n('boundary','Ambient credential boundary','Current registration returns null','Not a forwarded caller GitHub token','EntraOnlyGitHubCredentialBoundary','NULL',api+'Auth/EntraOnlyGitHubCredentialBoundary.cs:38-39','hexagon'),
      n('response','Suggested blueprint response','Blueprint + rationale + confidence','Signals explain the recommendation','SuggestionService.cs:78-88','RESULT',api+'Blueprints/GitHubRepoBlueprintSuggestionService.cs:78-88'),
      n('fallback','Recoverable failure','Unavailable metadata / transport','Fallback response offers Templates option','SuggestionService.cs:90-108','FALLBACK',api+'Blueprints/GitHubRepoBlueprintSuggestionService.cs:90-108'),
      n('cancel','Caller cancellation','Propagate OperationCanceledException','Not converted into fallback suggestions','SuggestionService.cs:90-94','CANCEL',api+'Blueprints/GitHubRepoBlueprintSuggestionService.cs:90-94','doubleEllipse')
    ], [
      e('picker','parse','suggest',web+'components/BlueprintPicker.tsx:487-517'),
      e('parse','boundary','resolve',api+'Blueprints/GitHubRepoBlueprintSuggestionService.cs:51-53'),
      e('boundary','metadata','null token',api+'Auth/EntraOnlyGitHubCredentialBoundary.cs:38-39',{route:'middle'}),
      e('metadata','match','signals',api+'Blueprints/GitHubRepoBlueprintSuggestionService.cs:70-74'),
      e('match','response','recommend',api+'Blueprints/GitHubRepoBlueprintSuggestionService.cs:78-88'),
      e('parse','fallback','invalid input',api+'Blueprints/GitHubRepoBlueprintSuggestionService.cs:46-48',{route:'outer'}),
      e('metadata','cancel','caller canceled',api+'Blueprints/GitHubRepoBlueprintSuggestionService.cs:90-94',{route:'outer'})
    ], 'Cancellation propagates; recoverable failures fall back. Current ambient token source is explicitly null.')
];
const routes=models.find(m=>m.name==='frontend-fig2');
routes.groups[1]='SHARED SHELL + PROJECT ROUTES';
routes.nodes.find(n=>n.id==='project').subtitle='/projects/:projectId';
routes.nodes.find(n=>n.id==='project').detail='Dashboard · board · flow · orchestrations';
routes.edges=routes.edges.filter(e=>!['project-to-assets','assets-to-operations'].includes(e.id));
routes.edges.push(
  e('router','assets','declares',web+'App.tsx:110-117',{lane:410,exitY:0.6,entryY:0.35}),
  e('router','operations','declares',web+'App.tsx:118-125',{lane:435,exitY:0.85,entryY:0.5})
);
Object.assign(routes.edges.find(e=>e.id==='router-to-project'),{lane:385,exitY:0.35,entryY:0.5});
Object.assign(routes.edges.find(e=>e.id==='shell-to-router'),{exitY:0.12,entryY:0.12,labelOffset:[0,8]});
for(const m of models) {
  for(const node of m.nodes) if(node.shape.startsWith('mxgraph.azure2'))
    node.shape='image;image=img/lib/azure2/identity/Azure_Active_Directory.svg;imageAspect=1';
}
const gen=models.find(m=>m.name==='project-generation-model-settings-fig1');
Object.assign(gen.edges.find(e=>e.target==='blueprint'),{lane:390,exitY:0.22,entryY:0.5,labelOffset:[0,18]});
Object.assign(gen.edges.find(e=>e.target==='workflow'),{lane:425,exitY:0.5,entryY:0.5,labelOffset:[0,8]});
Object.assign(gen.edges.find(e=>e.target==='outcome'),{exitY:0.82,entryY:0.82});
const skills=models.find(m=>m.name==='project-skills-fig1');
Object.assign(skills.edges.find(e=>e.id==='active-to-inline'),{label:'no FS',entryY:0.3});
Object.assign(skills.edges.find(e=>e.id==='shared-to-inline'),{label:'write fail',side:'left',lane:438,entryY:0.7});
const provision=models.find(m=>m.name==='projects-fig1');
Object.assign(provision.edges.find(e=>e.id==='local-to-probe'),{label:'local path',lane:395,entryY:0.3,labelOffset:[0,0]});
Object.assign(provision.edges.find(e=>e.id==='volume-to-probe'),{label:'PV path',lane:433,entryY:0.7,labelOffset:[0,18]});
const ownership=models.find(m=>m.name==='projects-fig2');
Object.assign(ownership.edges.find(e=>e.id==='project-to-team'),{side:'right',lane:397,label:'configures'});
Object.assign(ownership.edges.find(e=>e.id==='provider-to-checkout'),{label:'path'});
models.find(m=>m.name==='frontend-fig7').edges.find(e=>e.id==='buffer-to-merge').label='events';
models.find(m=>m.name==='mcp-server-fig2').edges.find(e=>e.id==='token-to-mcp').label='bearer';
models.find(m=>m.name==='repo-blueprint-suggestions-fig1').edges.find(e=>e.id==='metadata-to-cancel').label='cancel';
models.find(m=>m.name==='repo-blueprint-suggestions-fig1').edges.find(e=>e.id==='parse-to-fallback').label='invalid';
