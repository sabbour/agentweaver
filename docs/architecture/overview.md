# Architecture overview

Agentweaver runs as a single ASP.NET Core process. The API, run orchestration, event persistence, live streaming, and agent runtime all live in the same host, so the backend stays the single source of truth for every run.

The `RunOrchestrator` provisions a per-run git worktree, persists run state, and launches the agent loop as hosted background work inside the process. The agent loop uses the shared runtime, evaluates every tool call through a deny-by-default governance gate before it executes, applies content safety, and writes every event to the durable log before publishing it live.

SQLite stores both mutable run state and the append-only event log. The durable `SqliteRunEventStream` writes each event to a `RunEvents` table and fans it out to in-process subscribers through a `Channel<RunEvent>`, while `RunStreamStore` keeps a per-run in-memory entry for low-latency live streaming. The MCP server and web UI subscribe to the same stream without owning any run logic. When the run finishes, the orchestrator commits the worktree, requests human review, and lets `LibGit2Sharp` merge only after approval.

## System architecture

The diagram below shows the overall system: client entry points reach the ASP.NET API tier, which orchestrates runs, drives the agent runtime, controls sandbox workers for the execution tier, persists state to the data tier, and integrates with external systems.

```mermaid
%%{init: {'theme':'base','themeVariables':{'fontFamily':'Segoe UI, system-ui, -apple-system, sans-serif','fontSize':'14px','primaryColor':'#E8EEF9','primaryBorderColor':'#0F6CBD','primaryTextColor':'#242424','lineColor':'#605E5C','clusterBkg':'#FAF9F8','clusterBorder':'#D2D0CE','edgeLabelBackground':'#FFFFFF'}}}%%
flowchart TB
    subgraph clients["Clients"]
        web(["Web SPA<br/>React"])
        mcpc(["MCP clients<br/>Copilot CLI · VS Code"])
        cons(["API consumers"])
    end

    mcpsrv(["MCP server"])

    subgraph apitier["API tier · ASP.NET on .NET 10"]
        endpoints(["HTTP endpoints<br/>runs · projects · coordinator<br/>casting · blueprints · memory"])
        auth(["Auth<br/>GitHub OAuth AS · org gate"])
        orch(["RunOrchestrator<br/>lifecycle · review · merge"])
        coord(["Coordinator<br/>assembly · dispatch"])
        runtime(["Agent runtime<br/>governance gate · tools"])
        sbctl(["Sandbox control<br/>executor router"])
        mem(["Memory + Scribe<br/>decision inbox"])
        stream(["Run event stream<br/>SSE fan-out"])
    end

    subgraph exec["Execution tier"]
        warm(["SandboxWarmPool"])
        sbpod(["Sandbox workers<br/>Kata VM · per-run claim"])
    end

    subgraph datatier["Data tier"]
        appdb[("agentweaver.db<br/>runs · projects · events")]
        memdb[("memory.db<br/>decisions · memory")]
        ws[("Workspace volume<br/>git worktrees")]
    end

    subgraph external["External systems"]
        gh(["GitHub<br/>OAuth · REST API"])
        copilot(["GitHub Copilot SDK"])
        azure(["Azure<br/>Key Vault · AI Foundry"])
    end

    web -->|"/api"| endpoints
    cons --> endpoints
    mcpc --> mcpsrv --> endpoints
    endpoints --> auth
    auth --> gh
    auth --> azure
    endpoints --> orch
    coord --> orch
    orch --> runtime
    runtime --> sbctl
    runtime --> copilot
    runtime --> mem
    sbctl --> warm
    warm --> sbpod
    sbctl -->|"exec"| sbpod
    orch --> stream
    stream --> web
    stream --> mcpsrv
    orch --> appdb
    mem --> memdb
    orch --> ws
    sbpod --> ws
    sbpod --> azure

    classDef client fill:#E8EEF9,stroke:#0F6CBD,stroke-width:1px,color:#242424;
    classDef svc fill:#F3F2F1,stroke:#8A8886,stroke-width:1px,color:#242424;
    classDef core fill:#CFE4FA,stroke:#0F6CBD,stroke-width:2px,color:#242424;
    classDef data fill:#FFF4CE,stroke:#C19C00,stroke-width:1px,color:#242424;
    classDef ext fill:#F0E8F8,stroke:#8764B8,stroke-width:1px,color:#242424;
    classDef runtime fill:#DDF3DD,stroke:#107C10,stroke-width:1px,color:#242424;
    classDef evt fill:#D6F0F0,stroke:#038387,stroke-width:1px,color:#242424;

    class web,mcpc,cons client;
    class mcpsrv,endpoints,auth,coord,runtime,sbctl,mem svc;
    class orch core;
    class warm,sbpod runtime;
    class appdb,memdb,ws data;
    class gh,copilot,azure ext;
    class stream evt;
```

## End-to-end flow

```mermaid
%%{init: {'theme':'base','themeVariables':{'fontFamily':'Segoe UI, system-ui, -apple-system, sans-serif','fontSize':'14px','primaryColor':'#E8EEF9','primaryBorderColor':'#0F6CBD','primaryTextColor':'#242424','lineColor':'#605E5C','clusterBkg':'#FAF9F8','clusterBorder':'#D2D0CE','edgeLabelBackground':'#FFFFFF'}}}%%
flowchart TD
    Submit["Submit task or goal<br/>POST /api/runs"]

    subgraph setup["Run setup"]
        Record["Create run record"]
        Worktree["Create worktree branch + path"]
        Started["Emit run.started"]
    end

    subgraph loop["Agent loop"]
        Gate["Governance gate<br/>deny-by-default"]
        Tools["Execute tool<br/>read_file / write_file / shell<br/>inside worktree boundary"]
        Events["Append event to SQLite<br/>Publish to live subscribers"]
    end

    Terminal{{"run.completed<br/>run.failed<br/>run.bounded"}}

    Review["Human review gate<br/>review.requested"]

    Declined["review.declined<br/>originating branch unchanged"]
    Approved["review.approved"]
    Merge["LibGit2Sharp merge"]
    MergeOK["merge.completed"]
    MergeFail["merge.failed<br/>originating branch unchanged"]

    Submit --> Record --> Worktree --> Started
    Started --> Gate
    Gate -->|"allowed"| Tools --> Events --> Gate
    Gate -->|"denied"| Events
    Events --> Terminal
    Terminal -->|"completed"| Review
    Review --> Declined
    Review --> Approved --> Merge
    Merge --> MergeOK
    Merge --> MergeFail

    classDef client fill:#E8EEF9,stroke:#0F6CBD,stroke-width:1px,color:#242424;
    classDef svc fill:#F3F2F1,stroke:#8A8886,stroke-width:1px,color:#242424;
    classDef core fill:#CFE4FA,stroke:#0F6CBD,stroke-width:2px,color:#242424;
    classDef data fill:#FFF4CE,stroke:#C19C00,stroke-width:1px,color:#242424;
    classDef ext fill:#F0E8F8,stroke:#8764B8,stroke-width:1px,color:#242424;
    classDef runtime fill:#DDF3DD,stroke:#107C10,stroke-width:1px,color:#242424;
    classDef evt fill:#D6F0F0,stroke:#038387,stroke-width:1px,color:#242424;

    class Submit client;
    class Record,Worktree,Review,Merge svc;
    class Gate core;
    class Tools runtime;
    class Started,Events,Terminal,Declined,Approved,MergeOK,MergeFail evt;
```

## Main components

| Component | Responsibility |
| --- | --- |
| ASP.NET Core API | Accepts requests, authorizes users, and exposes run endpoints |
| `RunOrchestrator` | Owns run lifecycle, review gate, and merge decisions |
| Agent runtime | Executes the single-agent loop with provider selection, content safety, and run bounds |
| Governance gate | Per-run AGT kernel that evaluates every tool call against a deny-by-default policy and path-containment backend before execution |
| `SandboxedFileTools` | Defense-in-depth file reads and writes inside the run worktree; validates and re-verifies every path after open |
| SQLite stores | Persist `runs`, the durable `RunEvents` log, and operational records |
| `RunStreamStore` / `SqliteRunEventStream` | Fan events out to live subscribers (in-memory entries) and persist them durably |
| MCP server and web UI | Thin clients that submit runs, watch events, and record review decisions |

## Review and merge model

A completed run does not merge automatically. The orchestrator commits the worktree state, emits `review.requested`, and waits for the run owner to approve or decline. On approval, the merge step verifies that the approved tree hash still matches the worktree branch, then fast-forwards or creates a merge commit through `LibGit2Sharp`. On conflict or any merge failure, the originating branch stays unchanged and the worktree remains available for inspection.
