export interface CatalogRow {
  figmaNodeReference: string;
  extractionStatus: string;
  extractionDate: string;
  extractedFrom: string;
  implementedMapping: string;
  showcase: 'Yes' | 'No' | string;
};

export interface ComponentCatalogGroup {
  id: string;
  status: string;
  figmaComponentSets: string[];
  libraryExports: string[];
  sourceNodes: string[];
  mcpNodes?: Array<{
    component: string;
    status: string;
    nodeId?: string;
    nodeUrl?: string;
    designContext?: string;
    variableDefs?: string;
    error?: string;
    notes?: string;
  }>;
  variants: string[];
  publicExamples?: string[];
  implementationFiles?: string[];
  notes?: string;
}

export interface ComponentCatalogData {
  catalogKind: 'components';
  sourceFileKey: string;
  inventoryCoverage?: {
    inventorySource?: string;
    inventoryComponentCount: number;
    coverageComputation?: string;
    exactManifestNameNodeAudit?: {
      source?: string;
      coveredCount: number;
      missingCount: number;
      note?: string;
    };
    coverageTable: Array<{
      status: string;
      count: number;
      examples?: string[];
    }>;
    components?: Array<{
      name?: string;
      nodeId: string;
      pageName?: string;
      type?: string;
      nodeUrl?: string;
      coverageStatus: string;
      mappedGroupId?: string;
      libraryExports?: string[];
      mcpStatus?: string;
      coverageReason?: string;
    }>;
  };
  portability?: {
    downstreamConsumptionDoesNotRequireFigmaMcp: boolean;
    traceabilityCitationsAreOptional: boolean;
    localArtifactsAreAuthoritativeForOrdinaryUsage: boolean;
  };
  localConsumptionWorkflow?: string[];
  traceabilityNotes?: string[];
  groups: ComponentCatalogGroup[];
  inventoryRows: CatalogRow[];
}

export interface PatternFamily {
  id: string;
  name: string;
  status: string;
  pageNodeId: string;
  pageNodeUrl: string;
  representativeNodes: Array<{
    nodeId: string;
    name: string;
    url: string;
    sourceType: string;
  }>;
  libraryMappings: string[];
  antiRules: string[];
  localExamples?: string[];
  implementationFiles?: string[];
}

export interface PatternCatalogData {
  catalogKind: 'patterns';
  sourceFile: {
    name: string;
    fileKey: string;
  };
  rules?: Record<string, unknown>;
  sharedTokenAnchors?: Record<string, string>;
  portability?: {
    downstreamConsumptionDoesNotRequireFigmaMcp: boolean;
    devModeUrlsAreTraceabilityCitations: boolean;
    localArtifactsAreAuthoritativeForOrdinaryUsage: boolean;
  };
  localConsumptionWorkflow?: string[];
  families: PatternFamily[];
  summary: {
    patternFamilyCount: number;
    uniqueTrackedDevModeNodes: number;
    statusCounts: Record<string, number>;
  };
  mappingRows: CatalogRow[];
}

export const componentCatalogData: ComponentCatalogData = {
  "catalogKind": "components",
  "sourceFileKey": "q2TdO4dVcMhNWYp0N6Bc05",
  "inventoryCoverage": {
    "inventorySource": "Coordinator-relayed 148-item figma-list_file_components_for_code_connect inventory snapshot for Azure UI Kit / Fluent 2.",
    "inventoryComponentCount": 148,
    "coverageComputation": "Explicit per-component rows are derived from the 148-item figma-list_file_components_for_code_connect inventory. coverageStatus reflects current local delivery accounting (implemented-rendered, showcase-placeholder, needs-mcp-extraction, needs-implementation, or local-only-needed). Exact name/node audit counts are tracked separately and must not be conflated with showcase placeholder coverage.",
    "exactManifestNameNodeAudit": {
      "source": "Checked-in exact name/node comparison after the 2026-07-08 Inline Copilot, Copilot response/composer, workspace/nav, grounding-menu, entry-point, agentic, code-snippet child-node, pager, and data-grid MCP refreshes against the 148-item inventory.",
      "coveredCount": 105,
      "missingCount": 43,
      "note": "Exact name/node coverage now includes the workspace/nav, grounding-menu, entry-point, chain-of-thought wrapper, code-snippet child-node, pager, data-grid, portal-shell search/navigation, copilot-icon, tablist, form-detail, and message-bar batches refreshed with direct design-context and variable-def extraction. Placeholder-linked local coverage remains tracked separately."
    },
    "coverageTable": [
      {
        "status": "implemented-rendered",
        "count": 26,
        "examples": [
          "Accordion (30028:627)",
          "Azure Copilot (32382:40353)",
          "Azure F2-Data Grid (28093:32728)",
          "Code snippet (38116:47202)",
          "Inline Copilot - open start (29192:8232)",
          "Pager (27119:16070)",
          "Azure Horizontal TabList (29553:14761)",
          "Slider with numbers (28472:10338)",
          "Progress Bar with labels (28174:7417)",
          "Animated Progress Bar with labels (28209:4560)",
          "Upload File (25412:31783)",
          "Filterable combo box (25248:8173)",
          "Toolbar (Azure) (29553:7576)"
        ]
      },
      {
        "status": "needs-mcp-extraction",
        "count": 45,
        "examples": [
          "Azure Horizontal Tab (29167:8324)",
          "Azure Vertical Tab (29195:7388)",
          ".Popover (filter pill menus) (27774:7950)"
        ]
      },
      {
        "status": "showcase-placeholder",
        "count": 77,
        "examples": [
          ".Search Menu (40971:35680)",
          ".Copilot icon (31000:461)",
          "Message bar upsell (28644:76791)",
          ".Grid row / Group (28093:49456)",
          ".L1 Mobile Nav (35431:15337)",
          ".Asterix (30330:1179)"
        ]
      },
      {
        "status": "needs-implementation",
        "count": 0,
        "examples": [
          ".Table of Contents - Components (40095:5628)",
          "Scrollbar (27777:16820)",
          ".Azure UI Kit Header (local) (25365:18143)",
          ".Design System Update Notice (38080:124134)"
        ]
      },
      {
        "status": "local-only-needed",
        "count": 0,
        "examples": [
          "None"
        ]
      }
    ],
    "components": [
      {
        "name": ".Table of Contents - Components",
        "nodeId": "40095:5628",
        "pageName": "Contents",
        "type": "COMPONENT",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=40095-5628&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "service-menu",
        "libraryExports": [
          "ServiceMenu"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Grounding-menu list items were extracted on 2026-07-08 20:52 PDT and linked into ServiceMenu coverage."
      },
      {
        "name": "Azure Copilot",
        "nodeId": "32382:40353",
        "pageName": "Azure Copilot & sidecar",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-40353&m=dev",
        "coverageStatus": "implemented-rendered",
        "mappedGroupId": "copilot-workspace-pattern",
        "libraryExports": [
          "CopilotWorkspacePattern"
        ],
        "mcpStatus": "implemented-rendered",
        "coverageReason": "Root Azure Copilot shell confirmed from MCP extraction on 2026-07-08 20:52 PDT; nav/header subparts remain linked into CopilotWorkspacePattern."
      },
      {
        "name": ".Chat Input [Azure]",
        "nodeId": "32382:38450",
        "pageName": "\u21aa Chat input",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38450&m=dev",
        "coverageStatus": "implemented-rendered",
        "mappedGroupId": "copilot-composer",
        "libraryExports": [
          "CopilotComposer"
        ],
        "mcpStatus": "implemented-rendered",
        "coverageReason": "Copilot composer shell confirmed from MCP extraction on 2026-07-08 20:41 PDT."
      },
      {
        "name": "Agent Toggle",
        "nodeId": "32382:38689",
        "pageName": "\u21aa Chat input",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38689&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "copilot-composer",
        "libraryExports": [
          "CopilotComposer"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Agent toggle variants confirmed from MCP extraction on 2026-07-08 20:41 PDT."
      },
      {
        "name": "Agents Off Icon",
        "nodeId": "32382:38722",
        "pageName": "\u21aa Chat input",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38722&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "copilot-composer",
        "libraryExports": [
          "CopilotComposer"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Agents-off icon variants confirmed from MCP extraction on 2026-07-08 20:52 PDT and linked into CopilotComposer toggle states."
      },
      {
        "name": ".Input Footer_LG",
        "nodeId": "32382:38729",
        "pageName": "\u21aa Chat input",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38729&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "copilot-composer",
        "libraryExports": [
          "CopilotComposer"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Large footer anatomy confirmed from MCP extraction on 2026-07-08 20:41 PDT."
      },
      {
        "name": ".Input Footer_Sm",
        "nodeId": "33526:118139",
        "pageName": "\u21aa Chat input",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=33526-118139&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "copilot-composer",
        "libraryExports": [
          "CopilotComposer"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Compact footer anatomy confirmed from MCP extraction on 2026-07-08 20:52 PDT and linked into CopilotComposer."
      },
      {
        "name": ".Send_Icon",
        "nodeId": "32382:38835",
        "pageName": "\u21aa Chat input",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38835&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "copilot-composer",
        "libraryExports": [
          "CopilotComposer"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Send/stop icon states confirmed from MCP extraction on 2026-07-08 20:41 PDT."
      },
      {
        "name": "Copilot Row Swap",
        "nodeId": "32382:38124",
        "pageName": "\u21aa Chat output",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38124&m=dev",
        "coverageStatus": "implemented-rendered",
        "mappedGroupId": "copilot-response",
        "libraryExports": [
          "CopilotResponse"
        ],
        "mcpStatus": "implemented-rendered",
        "coverageReason": "Copilot response shell confirmed from MCP extraction on 2026-07-08 20:41 PDT."
      },
      {
        "name": "User Message",
        "nodeId": "32382:38151",
        "pageName": "\u21aa Chat output",
        "type": "COMPONENT",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38151&m=dev",
        "coverageStatus": "implemented-rendered",
        "mappedGroupId": "copilot-response",
        "libraryExports": [
          "CopilotResponse"
        ],
        "mcpStatus": "implemented-rendered",
        "coverageReason": "User message bubble confirmed from MCP extraction on 2026-07-08 20:41 PDT."
      },
      {
        "name": "Copilot Message / Response Element",
        "nodeId": "32382:38154",
        "pageName": "\u21aa Chat output",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38154&m=dev",
        "coverageStatus": "implemented-rendered",
        "mappedGroupId": "copilot-response",
        "libraryExports": [
          "CopilotResponse"
        ],
        "mcpStatus": "implemented-rendered",
        "coverageReason": "Assistant response shell confirmed from MCP extraction on 2026-07-08 20:41 PDT."
      },
      {
        "name": ".Footeractions",
        "nodeId": "32382:38177",
        "pageName": "\u21aa Chat output",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38177&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "copilot-response",
        "libraryExports": [
          "CopilotResponse"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Feedback action row confirmed from MCP extraction on 2026-07-08 20:41 PDT."
      },
      {
        "name": ".Code",
        "nodeId": "32382:38197",
        "pageName": "\u21aa Chat output",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38197&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "copilot-response",
        "libraryExports": [
          "CopilotResponse"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Inline code styling confirmed from MCP extraction on 2026-07-08 20:41 PDT."
      },
      {
        "name": ".Code Snippet",
        "nodeId": "32382:38204",
        "pageName": "\u21aa Chat output",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38204&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "copilot-response",
        "libraryExports": [
          "CopilotResponse"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Code snippet anatomy confirmed from MCP extraction on 2026-07-08 20:41 PDT."
      },
      {
        "name": ".data grid",
        "nodeId": "32382:38257",
        "pageName": "\u21aa Chat output",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38257&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "copilot-response",
        "libraryExports": [
          "CopilotResponse"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Copilot data grid treatment confirmed from MCP extraction on 2026-07-08 20:41 PDT."
      },
      {
        "name": ".single selection",
        "nodeId": "32382:38372",
        "pageName": "\u21aa Chat output",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38372&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "copilot-response",
        "libraryExports": [
          "CopilotResponse"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Single-selection response state confirmed from MCP extraction on 2026-07-08 20:41 PDT."
      },
      {
        "name": ".Multiple selection",
        "nodeId": "32382:38395",
        "pageName": "\u21aa Chat output",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38395&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "copilot-response",
        "libraryExports": [
          "CopilotResponse"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Multiple-selection response state confirmed from MCP extraction on 2026-07-08 20:41 PDT."
      },
      {
        "name": ".Confirmation Buttons",
        "nodeId": "32382:38418",
        "pageName": "\u21aa Chat output",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38418&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "copilot-response",
        "libraryExports": [
          "CopilotResponse"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Confirmation button row confirmed from MCP extraction on 2026-07-08 20:41 PDT."
      },
      {
        "name": "Request Count",
        "nodeId": "32382:38434",
        "pageName": "\u21aa Chat output",
        "type": "COMPONENT",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38434&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "copilot-response",
        "libraryExports": [
          "CopilotResponse"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Request-count metadata confirmed from MCP extraction on 2026-07-08 20:41 PDT."
      },
      {
        "name": "Latency",
        "nodeId": "32382:38442",
        "pageName": "\u21aa Chat output",
        "type": "COMPONENT",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38442&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "copilot-response",
        "libraryExports": [
          "CopilotResponse"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Latency row confirmed from MCP extraction on 2026-07-08 20:41 PDT."
      },
      {
        "name": ".ChatHeaders",
        "nodeId": "33921:19578",
        "pageName": "\u21aa Chat output",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=33921-19578&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "copilot-response",
        "libraryExports": [
          "CopilotResponse"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Chat header variants confirmed from MCP extraction on 2026-07-08 20:41 PDT."
      },
      {
        "name": "Agent Icon (Color)",
        "nodeId": "33921:19675",
        "pageName": "\u21aa Chat output",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=33921-19675&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "copilot-response",
        "libraryExports": [
          "CopilotResponse"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Agent icon variants confirmed from MCP extraction on 2026-07-08 20:41 PDT."
      },
      {
        "name": "Azure Copilot Nav Drawer",
        "nodeId": "32382:39054",
        "pageName": "\u21aa Navigation & header",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-39054&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "copilot-workspace-pattern",
        "libraryExports": [
          "CopilotWorkspacePattern"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Azure Copilot nav-drawer shell was extracted on 2026-07-08 20:52 PDT and is now tracked as linked into CopilotWorkspacePattern."
      },
      {
        "name": ".Nav item",
        "nodeId": "32382:39444",
        "pageName": "\u21aa Navigation & header",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-39444&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "copilot-workspace-pattern",
        "libraryExports": [
          "CopilotWorkspacePattern"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Nav-item states were extracted on 2026-07-08 20:52 PDT and are now tracked as linked into CopilotWorkspacePattern."
      },
      {
        "name": ". Nav link Item",
        "nodeId": "32382:39939",
        "pageName": "\u21aa Navigation & header",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-39939&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "copilot-workspace-pattern",
        "libraryExports": [
          "CopilotWorkspacePattern"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Selected nav-link anatomy was extracted on 2026-07-08 20:52 PDT and is linked into CopilotWorkspacePattern."
      },
      {
        "name": ".Nav Icon",
        "nodeId": "32382:39948",
        "pageName": "\u21aa Navigation & header",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-39948&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "copilot-workspace-pattern",
        "libraryExports": [
          "CopilotWorkspacePattern"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Nav-icon trigger/badge states were extracted on 2026-07-08 20:52 PDT and are linked into CopilotWorkspacePattern."
      },
      {
        "name": ".Copilot Hub Nav header",
        "nodeId": "32382:39961",
        "pageName": "\u21aa Navigation & header",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-39961&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "copilot-workspace-pattern",
        "libraryExports": [
          "CopilotWorkspacePattern"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Branded hub-header anatomy was extracted on 2026-07-08 20:52 PDT and is linked into CopilotWorkspacePattern."
      },
      {
        "name": ".Nav Menu",
        "nodeId": "32382:40121",
        "pageName": "\u21aa Navigation & header",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-40121&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "copilot-workspace-pattern",
        "libraryExports": [
          "CopilotWorkspacePattern"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Nav-menu anatomy was extracted on 2026-07-08 20:52 PDT and is linked into CopilotWorkspacePattern."
      },
      {
        "name": "All Chats(WIP)",
        "nodeId": "32382:40186",
        "pageName": "\u21aa Navigation & header",
        "type": "COMPONENT",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-40186&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "copilot-workspace-pattern",
        "libraryExports": [
          "CopilotWorkspacePattern"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "All-chats container anatomy was extracted on 2026-07-08 20:52 PDT and is linked into CopilotWorkspacePattern."
      },
      {
        "name": "List Chats - stacked indicators (Wip)",
        "nodeId": "32382:40313",
        "pageName": "\u21aa Navigation & header",
        "type": "COMPONENT",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-40313&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "copilot-workspace-pattern",
        "libraryExports": [
          "CopilotWorkspacePattern"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Stacked-indicator chat-list states were extracted on 2026-07-08 20:52 PDT and are linked into CopilotWorkspacePattern."
      },
      {
        "name": "Azure Copilot Header (Sidecar)",
        "nodeId": "34460:64534",
        "pageName": "\u21aa Navigation & header",
        "type": "COMPONENT",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=34460-64534&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "copilot-workspace-pattern",
        "libraryExports": [
          "CopilotWorkspacePattern"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Sidecar-header anatomy was extracted on 2026-07-08 20:52 PDT and is linked into CopilotWorkspacePattern."
      },
      {
        "name": "Azure Copilot Header (Expanded)",
        "nodeId": "34460:136270",
        "pageName": "\u21aa Navigation & header",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=34460-136270&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "copilot-workspace-pattern",
        "libraryExports": [
          "CopilotWorkspacePattern"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Expanded-header anatomy was extracted on 2026-07-08 20:52 PDT and is linked into CopilotWorkspacePattern."
      },
      {
        "name": ".GM_ListItems",
        "nodeId": "32382:38860",
        "pageName": "\u21aa Grounding menu (GM)",
        "type": "COMPONENT",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38860&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "service-menu",
        "libraryExports": [
          "ServiceMenu"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Grounding-menu shell was extracted on 2026-07-08 20:52 PDT and linked into ServiceMenu coverage."
      },
      {
        "name": ".Grounding Menu",
        "nodeId": "32382:38867",
        "pageName": "\u21aa Grounding menu (GM)",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38867&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "service-menu",
        "libraryExports": [
          "ServiceMenu"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Grounding-menu header was extracted on 2026-07-08 20:52 PDT and linked into ServiceMenu coverage."
      },
      {
        "name": ".GM_Header",
        "nodeId": "32382:38901",
        "pageName": "\u21aa Grounding menu (GM)",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38901&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "service-menu",
        "libraryExports": [
          "ServiceMenu"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Grounding-menu overflow menu was extracted on 2026-07-08 20:52 PDT and linked into ServiceMenu coverage."
      },
      {
        "name": ".GM_Overflow",
        "nodeId": "32382:38968",
        "pageName": "\u21aa Grounding menu (GM)",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38968&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "service-menu",
        "libraryExports": [
          "ServiceMenu"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Grounding-menu search states were extracted on 2026-07-08 20:52 PDT and linked into ServiceMenu coverage."
      },
      {
        "name": ".GM_Search",
        "nodeId": "32382:38987",
        "pageName": "\u21aa Grounding menu (GM)",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38987&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "service-menu",
        "libraryExports": [
          "ServiceMenu"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Grounding-menu entity rows were extracted on 2026-07-08 20:52 PDT and linked into ServiceMenu coverage."
      },
      {
        "name": ".GM_Entity list Item",
        "nodeId": "32382:38992",
        "pageName": "\u21aa Grounding menu (GM)",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38992&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "inline-copilot",
        "libraryExports": [
          "InlineCopilot"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Prompt-ribbon shell confirmed from MCP extraction on 2026-07-08 21:00 PDT and linked into InlineCopilot launch/suggestion affordances."
      },
      {
        "name": ".Reasoning (CoT)",
        "nodeId": "27865:7924",
        "pageName": "Chain of thought (Agentic chat)",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27865-7924&m=dev",
        "coverageStatus": "implemented-rendered",
        "mappedGroupId": "agentic-progress",
        "libraryExports": [
          "AgenticProgress"
        ],
        "mcpStatus": "implemented-rendered",
        "coverageReason": "Reasoning row confirmed from MCP extraction on 2026-07-08 20:41 PDT."
      },
      {
        "name": ".Artifact pill (CoT)",
        "nodeId": "27865:11293",
        "pageName": "Chain of thought (Agentic chat)",
        "type": "COMPONENT",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27865-11293&m=dev",
        "coverageStatus": "implemented-rendered",
        "mappedGroupId": "agentic-progress",
        "libraryExports": [
          "AgenticProgress"
        ],
        "mcpStatus": "implemented-rendered",
        "coverageReason": "Artifact pill confirmed from MCP extraction on 2026-07-08 20:41 PDT."
      },
      {
        "name": ".Complete (CoT)",
        "nodeId": "27880:12932",
        "pageName": "Chain of thought (Agentic chat)",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27880-12932&m=dev",
        "coverageStatus": "implemented-rendered",
        "mappedGroupId": "agentic-progress",
        "libraryExports": [
          "AgenticProgress"
        ],
        "mcpStatus": "implemented-rendered",
        "coverageReason": "Complete-state row confirmed from MCP extraction on 2026-07-08 20:41 PDT."
      },
      {
        "name": ".Needs user input (CoT)",
        "nodeId": "27880:13472",
        "pageName": "Chain of thought (Agentic chat)",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27880-13472&m=dev",
        "coverageStatus": "implemented-rendered",
        "mappedGroupId": "agentic-progress",
        "libraryExports": [
          "AgenticProgress"
        ],
        "mcpStatus": "implemented-rendered",
        "coverageReason": "Needs-input row confirmed from MCP extraction on 2026-07-08 20:41 PDT."
      },
      {
        "name": ".Action swap (CoT)",
        "nodeId": "27887:13693",
        "pageName": "Chain of thought (Agentic chat)",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27887-13693&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "agentic-progress",
        "libraryExports": [
          "AgenticProgress"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Action-swap taxonomy was extracted on 2026-07-08 20:52 PDT and linked into AgenticProgress row variants."
      },
      {
        "name": ".Show artifacts (CoT)",
        "nodeId": "27895:9236",
        "pageName": "Chain of thought (Agentic chat)",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27895-9236&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "agentic-progress",
        "libraryExports": [
          "AgenticProgress"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Show-artifacts summary control was extracted on 2026-07-08 20:52 PDT and linked into AgenticProgress."
      },
      {
        "name": "Chain of thought",
        "nodeId": "27895:11157",
        "pageName": "Chain of thought (Agentic chat)",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27895-11157&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "agentic-progress",
        "libraryExports": [
          "AgenticProgress"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Chain-of-thought wrapper was extracted via root plus sublayer follow-up on 2026-07-08 20:52 PDT and linked into AgenticProgress."
      },
      {
        "name": ".Agentic List (CoT)",
        "nodeId": "27950:10571",
        "pageName": "Chain of thought (Agentic chat)",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27950-10571&m=dev",
        "coverageStatus": "implemented-rendered",
        "mappedGroupId": "agentic-progress",
        "libraryExports": [
          "AgenticProgress"
        ],
        "mcpStatus": "implemented-rendered",
        "coverageReason": "Agentic list wrapper confirmed from MCP extraction on 2026-07-08 20:52 PDT and matches the checked-in AgenticProgress example/showcase."
      },
      {
        "name": "Button Entry Point (Copilot)",
        "nodeId": "31316:1188",
        "pageName": "Copilot entry points",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=31316-1188&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "inline-copilot",
        "libraryExports": [
          "InlineCopilot"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Button entry-point trigger + tooltip were extracted on 2026-07-08 20:52 PDT and linked into InlineCopilot launch affordances."
      },
      {
        "name": "Copilot Entry Icon ",
        "nodeId": "31323:1530",
        "pageName": "Copilot entry points",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=31323-1530&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "inline-copilot",
        "libraryExports": [
          "InlineCopilot"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Icon-only Copilot trigger + tooltip were extracted on 2026-07-08 20:52 PDT and linked into InlineCopilot launch affordances."
      },
      {
        "name": "Menu Entry Point (Copilot)",
        "nodeId": "31330:9223",
        "pageName": "Copilot entry points",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=31330-9223&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "inline-copilot",
        "libraryExports": [
          "InlineCopilot"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Menu entry-point trigger/menu anatomy were extracted on 2026-07-08 20:52 PDT and linked into InlineCopilot launch affordances."
      },
      {
        "name": "Prompt Ribbon(Copilot)",
        "nodeId": "30909:48908",
        "pageName": "Copilot entry points",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=30909-48908&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "inline-copilot",
        "libraryExports": [
          "InlineCopilot"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Prompt-ribbon shell confirmed from MCP extraction on 2026-07-08 21:00 PDT and linked into InlineCopilot launch/suggestion affordances."
      },
      {
        "name": ".Suggested Prompt Pill",
        "nodeId": "30945:10400",
        "pageName": "Copilot entry points",
        "type": "COMPONENT",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=30945-10400&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "inline-copilot",
        "libraryExports": [
          "InlineCopilot"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Suggested-prompt pill confirmed from MCP extraction on 2026-07-08 21:00 PDT and linked into InlineCopilot suggestion chips."
      },
      {
        "name": ".Copilot icon",
        "nodeId": "31000:461",
        "pageName": "Copilot entry points",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=31000-461&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "service-menu-local-navigation",
        "libraryExports": [
          "ServiceMenu"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Menu-header states were extracted on 2026-07-08 21:13 PDT and linked into ServiceMenu/local navigation coverage."
      },
      {
        "name": ".Copilot icon(Old)",
        "nodeId": "41747:68133",
        "pageName": "Copilot entry points",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=41747-68133&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "service-menu-local-navigation",
        "libraryExports": [
          "ServiceMenu"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Portal search-trigger states were extracted on 2026-07-08 21:13 PDT and linked into ServiceMenu/local navigation coverage."
      },
      {
        "name": "Inline Copilot - open start",
        "nodeId": "29192:8232",
        "pageName": "Inline Copilot",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29192-8232&m=dev",
        "coverageStatus": "implemented-rendered",
        "mappedGroupId": "inline-copilot",
        "libraryExports": [
          "InlineCopilot"
        ],
        "mcpStatus": "design-context-succeeded",
        "coverageReason": "Direct MCP extraction succeeded on 2026-07-08 and the checked-in InlineCopilot example now shows the exact open-start surface."
      },
      {
        "name": "Inline Copilot - guided start",
        "nodeId": "29192:8293",
        "pageName": "Inline Copilot",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29192-8293&m=dev",
        "coverageStatus": "implemented-rendered",
        "mappedGroupId": "inline-copilot",
        "libraryExports": [
          "InlineCopilot"
        ],
        "mcpStatus": "design-context-succeeded",
        "coverageReason": "Direct MCP extraction succeeded on 2026-07-08 and the checked-in InlineCopilot example now shows the exact guided-start surface."
      },
      {
        "name": ".Flair",
        "nodeId": "29389:12096",
        "pageName": "Inline Copilot",
        "type": "COMPONENT",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29389-12096&m=dev",
        "coverageStatus": "implemented-rendered",
        "mappedGroupId": "inline-copilot",
        "libraryExports": [
          "InlineCopilot"
        ],
        "mcpStatus": "design-context-succeeded",
        "coverageReason": "Direct MCP extraction succeeded on 2026-07-08 and the checked-in InlineCopilot surface now carries the extracted flair shell."
      },
      {
        "name": ".prompt input",
        "nodeId": "29192:8358",
        "pageName": "Inline Copilot",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29192-8358&m=dev",
        "coverageStatus": "implemented-rendered",
        "mappedGroupId": "inline-copilot",
        "libraryExports": [
          "InlineCopilot"
        ],
        "mcpStatus": "design-context-succeeded",
        "coverageReason": "Direct MCP extraction succeeded on 2026-07-08 and the checked-in InlineCopilot prompt shell now reflects the extracted input treatment."
      },
      {
        "name": ".Inline Copilot title",
        "nodeId": "29192:8429",
        "pageName": "Inline Copilot",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29192-8429&m=dev",
        "coverageStatus": "implemented-rendered",
        "mappedGroupId": "inline-copilot",
        "libraryExports": [
          "InlineCopilot"
        ],
        "mcpStatus": "design-context-succeeded",
        "coverageReason": "Direct MCP extraction succeeded on 2026-07-08 and the checked-in InlineCopilot title now reflects the extracted title treatment."
      },
      {
        "name": "Top action",
        "nodeId": "30046:9398",
        "pageName": "Top actions card",
        "type": "COMPONENT",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=30046-9398&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "portal-shell-navigation",
        "libraryExports": [
          "PortalTopNav"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Portal AI-button variants were extracted on 2026-07-08 21:13 PDT and linked into PortalTopNav coverage."
      },
      {
        "name": ".Quick Actions",
        "nodeId": "30289:2845",
        "pageName": "Top actions card",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=30289-2845&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "service-menu-local-navigation",
        "libraryExports": [
          "ServiceMenu"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Service-menu item tiles were extracted on 2026-07-08 21:13 PDT and linked into ServiceMenu coverage."
      },
      {
        "name": "Accordion",
        "nodeId": "30028:627",
        "pageName": "Accordion",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=30028-627&m=dev",
        "coverageStatus": "implemented-rendered",
        "mappedGroupId": "accordion",
        "libraryExports": [
          "AzureAccordion"
        ],
        "mcpStatus": "extracted",
        "coverageReason": "Rendered by AzureAccordion in the Components browser; exact inventory node 30028:627 was extracted in this pass and matches the checked-in implementation path."
      },
      {
        "name": ".Code line",
        "nodeId": "38108:33491",
        "pageName": "Code snippet",
        "type": "COMPONENT",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=38108-33491&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "code-snippet",
        "libraryExports": [
          "CodeSnippet"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Code-row shell confirmed from MCP extraction on 2026-07-08 21:00 PDT and linked into the shared CodeSnippet line renderer."
      },
      {
        "name": ".Number",
        "nodeId": "38108:33570",
        "pageName": "Code snippet",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=38108-33570&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "code-snippet",
        "libraryExports": [
          "CodeSnippet"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Number gutter/collapse-state variants confirmed from MCP extraction on 2026-07-08 21:00 PDT and linked into CodeSnippet line-number treatment."
      },
      {
        "name": ".Code level(s)",
        "nodeId": "38108:33579",
        "pageName": "Code snippet",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=38108-33579&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "code-snippet",
        "libraryExports": [
          "CodeSnippet"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Indentation-rail variants confirmed from MCP extraction on 2026-07-08 21:00 PDT and linked into CodeSnippet nested code rows."
      },
      {
        "name": ".JSON Collapse",
        "nodeId": "38113:34678",
        "pageName": "Code snippet",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=38113-34678&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "code-snippet",
        "libraryExports": [
          "CodeSnippet"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "JSON collapse affordance confirmed from MCP extraction on 2026-07-08 21:00 PDT and linked into CodeSnippet expand/collapse controls."
      },
      {
        "name": "Code snippet",
        "nodeId": "38116:47202",
        "pageName": "Code snippet",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=38116-47202&m=dev",
        "coverageStatus": "implemented-rendered",
        "mappedGroupId": "code-snippet",
        "libraryExports": [
          "CodeSnippet"
        ],
        "mcpStatus": "extracted",
        "coverageReason": "Rendered by CodeSnippet in the Components browser; exact inventory node 38116:47202 was extracted in this pass and aligns with the checked-in editor surface."
      },
      {
        "name": "Copy Button",
        "nodeId": "25260:8600",
        "pageName": "Copy button",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=25260-8600&m=dev",
        "coverageStatus": "implemented-rendered",
        "mappedGroupId": "copy-button",
        "libraryExports": [
          "CopyButton"
        ],
        "mcpStatus": "extracted",
        "coverageReason": "Rendered by CopyButton in the Components browser; exact inventory node 25260:8600 was extracted in this pass and aligns with the checked-in copy affordance."
      },
      {
        "name": ".F2-Grid cell / Text",
        "nodeId": "28093:48265",
        "pageName": "Data grid",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-48265&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "azure-data-grid",
        "libraryExports": [
          "AzureDataGrid"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Text-cell family confirmed from MCP extraction on 2026-07-08 21:00 PDT and linked into AzureDataGrid cell renderers."
      },
      {
        "name": ".Grid cell / Checkbox",
        "nodeId": "28093:48439",
        "pageName": "Data grid",
        "type": "COMPONENT",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-48439&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "azure-data-grid",
        "libraryExports": [
          "AzureDataGrid"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Selection-checkbox cell confirmed from MCP extraction on 2026-07-08 21:00 PDT and linked into AzureDataGrid selection columns."
      },
      {
        "name": ".Grid cell / Editable field",
        "nodeId": "28093:48441",
        "pageName": "Data grid",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-48441&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "portal-shell-navigation",
        "libraryExports": [
          "PortalTopNav"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Search-filter pill rail was extracted on 2026-07-08 21:13 PDT and linked into PortalTopNav coverage."
      },
      {
        "name": ".Grid cell / Empty",
        "nodeId": "28093:48448",
        "pageName": "Data grid",
        "type": "COMPONENT",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-48448&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "portal-shell-navigation",
        "libraryExports": [
          "PortalTopNav"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Large mobile-search shell extraction succeeded on 2026-07-08 21:13 PDT and is linked into PortalTopNav coverage."
      },
      {
        "name": ".Grid cell /  Group",
        "nodeId": "28093:48449",
        "pageName": "Data grid",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-48449&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "portal-shell-navigation",
        "libraryExports": [
          "PortalTopNav"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Large mobile-search-menu extraction succeeded on 2026-07-08 21:13 PDT and is linked into PortalTopNav coverage."
      },
      {
        "name": ".Grid cell/Tags",
        "nodeId": "28752:53376",
        "pageName": "Data grid",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28752-53376&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "data-grid-dense-list-states",
        "libraryExports": [
          "AzureDataGrid"
        ],
        "mcpStatus": "grouped-only",
        "coverageReason": "linked into the shared AzureDataGrid showcase rather than tracked as its own MCP extraction."
      },
      {
        "name": ".Grid cell/ Icons",
        "nodeId": "28817:36284",
        "pageName": "Data grid",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28817-36284&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "portal-shell-navigation",
        "libraryExports": [
          "PortalTopNav"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Header-action icon cluster was extracted on 2026-07-08 21:13 PDT and linked into PortalTopNav coverage."
      },
      {
        "name": ".Column header / Checkbox",
        "nodeId": "28093:48459",
        "pageName": "Data grid",
        "type": "COMPONENT",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-48459&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "mcp-timeout",
        "coverageReason": "Direct MCP extraction timed out on 2026-07-08 21:13 PDT; keep the broader PortalTopNav parent mapping, but this child search-menu set still needs a narrower follow-up extraction."
      },
      {
        "name": ".Column header / Empty",
        "nodeId": "28093:48461",
        "pageName": "Data grid",
        "type": "COMPONENT",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-48461&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".Column header / Label",
        "nodeId": "28093:48462",
        "pageName": "Data grid",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-48462&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "azure-data-grid",
        "libraryExports": [
          "AzureDataGrid"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Sortable column-header label confirmed from MCP extraction on 2026-07-08 21:00 PDT and linked into AzureDataGrid headers."
      },
      {
        "name": ".Column header /  Group",
        "nodeId": "28093:48474",
        "pageName": "Data grid",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-48474&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".Grid column / Data grid",
        "nodeId": "28093:48484",
        "pageName": "Data grid",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-48484&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".Grid column / Editable",
        "nodeId": "28093:49265",
        "pageName": "Data grid",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-49265&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".\u21aa\ufe0f Hierarchy level",
        "nodeId": "28093:49423",
        "pageName": "Data grid",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-49423&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".Grid row / Empty",
        "nodeId": "28093:49440",
        "pageName": "Data grid",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-49440&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".\u21aa\ufe0f Grouped row chevron",
        "nodeId": "28093:49447",
        "pageName": "Data grid",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-49447&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".Grid row / Group",
        "nodeId": "28093:49456",
        "pageName": "Data grid",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-49456&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": "Azure F2-Data Grid",
        "nodeId": "28093:32728",
        "pageName": "Data grid",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-32728&m=dev",
        "coverageStatus": "implemented-rendered",
        "mappedGroupId": "azure-data-grid",
        "libraryExports": [
          "AzureDataGrid"
        ],
        "mcpStatus": "implemented-rendered",
        "coverageReason": "Root grid shell confirmed from sparse root MCP plus sublayer extraction on 2026-07-08 21:00 PDT and matches the checked-in AzureDataGrid export."
      },
      {
        "name": "Empty state",
        "nodeId": "29232:42433",
        "pageName": "Empty state",
        "type": "COMPONENT",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29232-42433&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "notifications-errors-empty-state",
        "libraryExports": [
          "NotificationPane"
        ],
        "mcpStatus": "grouped-only",
        "coverageReason": "linked into the empty/error notification family."
      },
      {
        "name": "Essentials",
        "nodeId": "25412:8797",
        "pageName": "Essentials",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=25412-8797&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": "Feedback",
        "nodeId": "35182:761",
        "pageName": "Feedback link",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=35182-761&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": "Upload File",
        "nodeId": "25412:31783",
        "pageName": "File upload",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=25412-31783&m=dev",
        "coverageStatus": "implemented-rendered",
        "mappedGroupId": "file-upload",
        "libraryExports": ["FileUpload"],
        "mcpStatus": "implemented-rendered",
        "coverageReason": "Extracted via get_design_context + get_variable_defs and implemented as FileUpload (default/selected/progress/success/dragdrop states) with a dedicated showcase preview."
      },
      {
        "name": "Filterable combo box",
        "nodeId": "25248:8173",
        "pageName": "Filterable combo box",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=25248-8173&m=dev",
        "coverageStatus": "implemented-rendered",
        "mappedGroupId": "filterable-combo-box",
        "libraryExports": ["FilterableComboBox"],
        "mcpStatus": "implemented-rendered",
        "coverageReason": "Extracted via get_design_context + get_variable_defs and implemented as FilterableComboBox (client-side type-to-filter over Fluent Combobox) with a dedicated showcase preview."
      },
      {
        "name": ".Popover (filter pill menus)",
        "nodeId": "27774:7950",
        "pageName": "Filter pill",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27774-7950&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": "Filter Pill Dropdown",
        "nodeId": "25378:3066",
        "pageName": "Filter pill \u2013 subscription",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=25378-3066&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "command-bar-filtering",
        "libraryExports": [
          "CommandBar"
        ],
        "mcpStatus": "grouped-only",
        "coverageReason": "Mapped into the command/filtering family, not yet tracked as an exact MCP node."
      },
      {
        "name": "Message bar upsell",
        "nodeId": "28644:76791",
        "pageName": "Message bar",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28644-76791&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".Tab Number",
        "nodeId": "27113:1660",
        "pageName": "Pager",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27113-1660&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "pager",
        "libraryExports": [
          "Pager"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Tab-number states confirmed from MCP extraction on 2026-07-08 21:00 PDT and linked into Pager page tabs."
      },
      {
        "name": ".Pagination Counter",
        "nodeId": "27119:1897",
        "pageName": "Pager",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27119-1897&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "pager",
        "libraryExports": [
          "Pager"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Pagination counter shell confirmed from MCP extraction on 2026-07-08 21:00 PDT and linked into Pager navigation."
      },
      {
        "name": ".Num Dropdown",
        "nodeId": "27119:15792",
        "pageName": "Pager",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27119-15792&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "pager",
        "libraryExports": [
          "Pager"
        ],
        "mcpStatus": "showcase-placeholder",
        "coverageReason": "Rows-per-page dropdown confirmed from MCP extraction on 2026-07-08 21:00 PDT and linked into Pager page-size picker."
      },
      {
        "name": "Pager",
        "nodeId": "27119:16070",
        "pageName": "Pager",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27119-16070&m=dev",
        "coverageStatus": "implemented-rendered",
        "mappedGroupId": "pager",
        "libraryExports": [
          "Pager"
        ],
        "mcpStatus": "implemented-rendered",
        "coverageReason": "Exact pager shell confirmed from MCP extraction on 2026-07-08 21:00 PDT and matches the checked-in Pager export."
      },
      {
        "name": ".Popover Content (Brand)",
        "nodeId": "27965:13714",
        "pageName": "Popover",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27965-13714&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".Popover Content (Light)",
        "nodeId": "28035:15352",
        "pageName": "Popover",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28035-15352&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".Popover Content (Dark)",
        "nodeId": "28035:15353",
        "pageName": "Popover",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28035-15353&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "tabs-popovers-help",
        "libraryExports": [
          "TabsWithContent"
        ],
        "mcpStatus": "grouped-only",
        "coverageReason": "linked into the tabs/popovers/help family."
      },
      {
        "name": "Progress Bar with labels",
        "nodeId": "28174:7417",
        "pageName": "Progress bar",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28174-7417&m=dev",
        "coverageStatus": "implemented-rendered",
        "mappedGroupId": "progress-bar-with-label",
        "libraryExports": ["ProgressBarWithLabel"],
        "mcpStatus": "implemented-rendered",
        "coverageReason": "Extracted via get_design_context + get_variable_defs and implemented as ProgressBarWithLabel (determinate) with a dedicated showcase preview."
      },
      {
        "name": "Animated Progress Bar with labels",
        "nodeId": "28209:4560",
        "pageName": "Progress bar",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28209-4560&m=dev",
        "coverageStatus": "implemented-rendered",
        "mappedGroupId": "progress-bar-with-label",
        "libraryExports": ["ProgressBarWithLabel"],
        "mcpStatus": "implemented-rendered",
        "coverageReason": "Extracted via get_design_context + get_variable_defs; the animated/indeterminate state is covered by ProgressBarWithLabel's indeterminate prop, shown in the showcase preview."
      },
      {
        "name": "Scrollbar",
        "nodeId": "27777:16820",
        "pageName": "Scrollbar",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27777-16820&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": "Footer",
        "nodeId": "35285:10476",
        "pageName": "Shell for Azure Portal",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=35285-10476&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "portal-shell-navigation",
        "libraryExports": [
          "PortalLayout"
        ],
        "mcpStatus": "grouped-only",
        "coverageReason": "Represented by portal shell/footer compositions and grouped shell mapping."
      },
      {
        "name": ".Menu header",
        "nodeId": "32610:9876",
        "pageName": "Shell for Azure Portal",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32610-9876&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".Menu group",
        "nodeId": "32610:9930",
        "pageName": "Shell for Azure Portal",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32610-9930&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "service-menu-local-navigation",
        "libraryExports": [
          "ServiceMenu"
        ],
        "mcpStatus": "grouped-only",
        "coverageReason": "linked into ServiceMenu/local navigation mapping."
      },
      {
        "name": ".search button",
        "nodeId": "32610:9923",
        "pageName": "Shell for Azure Portal",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32610-9923&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".Menu search",
        "nodeId": "32610:9942",
        "pageName": "Shell for Azure Portal",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32610-9942&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "service-menu-local-navigation",
        "libraryExports": [
          "ServiceMenu"
        ],
        "mcpStatus": "grouped-only",
        "coverageReason": "linked into ServiceMenu/local navigation mapping."
      },
      {
        "name": "Service Menu",
        "nodeId": "32610:9824",
        "pageName": "Shell for Azure Portal",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32610-9824&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "service-menu-local-navigation",
        "libraryExports": [
          "ServiceMenu"
        ],
        "mcpStatus": "grouped-only",
        "coverageReason": "Represented through the ServiceMenu export and grouped mapping."
      },
      {
        "name": ".Menu item",
        "nodeId": "32610:9731",
        "pageName": "Shell for Azure Portal",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32610-9731&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "service-menu-local-navigation",
        "libraryExports": [
          "ServiceMenu"
        ],
        "mcpStatus": "grouped-only",
        "coverageReason": "linked into ServiceMenu/local navigation mapping."
      },
      {
        "name": ".L1 Menu item",
        "nodeId": "35357:9421",
        "pageName": "Shell for Azure Portal",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=35357-9421&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".L1 Menu Element",
        "nodeId": "35360:9984",
        "pageName": "Shell for Azure Portal",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=35360-9984&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": "L1 - Portal Menu",
        "nodeId": "35399:10636",
        "pageName": "Shell for Azure Portal",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=35399-10636&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".L1 Mobile Nav",
        "nodeId": "35431:15337",
        "pageName": "Shell for Azure Portal",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=35431-15337&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".AI button",
        "nodeId": "31147:480",
        "pageName": "Shell for Azure Portal",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=31147-480&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": "Footer",
        "nodeId": "41011:13461",
        "pageName": "Shell for Azure Portal",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=41011-13461&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "portal-shell-navigation",
        "libraryExports": [
          "PortalLayout"
        ],
        "mcpStatus": "grouped-only",
        "coverageReason": "Represented by portal shell/footer compositions and grouped shell mapping."
      },
      {
        "name": "Service Menu item",
        "nodeId": "41544:8562",
        "pageName": "Shell for Azure Portal",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=41544-8562&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": "Search Filter Pills",
        "nodeId": "41795:20148",
        "pageName": "Shell for Azure Portal",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=41795-20148&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".Azure Mobile Search",
        "nodeId": "41362:31531",
        "pageName": "Shell for Azure Portal",
        "type": "COMPONENT",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=41362-31531&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".Mobile Search Menu",
        "nodeId": "41483:18353",
        "pageName": "Shell for Azure Portal",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=41483-18353&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".Azure Global Search",
        "nodeId": "40971:40679",
        "pageName": "Shell for Azure Portal",
        "type": "COMPONENT",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=40971-40679&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "portal-shell-navigation",
        "libraryExports": [
          "PortalTopNav"
        ],
        "mcpStatus": "grouped-only",
        "coverageReason": "Represented by portal shell/top nav composition rather than exact-node MCP extraction."
      },
      {
        "name": ".Search Menu",
        "nodeId": "40971:35680",
        "pageName": "Shell for Azure Portal",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=40971-35680&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": "Site Header",
        "nodeId": "31147:439",
        "pageName": "Shell for Azure Portal",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=31147-439&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "portal-shell-navigation",
        "libraryExports": [
          "PortalTopNav"
        ],
        "mcpStatus": "grouped-only",
        "coverageReason": "Represented by portal shell/top nav composition rather than exact-node MCP extraction."
      },
      {
        "name": "Blade header",
        "nodeId": "32630:8970",
        "pageName": "Shell for Azure Portal",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32630-8970&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "blade-header-actions",
        "libraryExports": [
          "BladeHeader"
        ],
        "mcpStatus": "grouped-only",
        "coverageReason": "Represented by the BladeHeader export with grouped mapping."
      },
      {
        "name": ".Header Icons",
        "nodeId": "35292:9094",
        "pageName": "Shell for Azure Portal",
        "type": "COMPONENT",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=35292-9094&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": "Slider with numbers",
        "nodeId": "28472:10338",
        "pageName": "Slider",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28472-10338&m=dev",
        "coverageStatus": "implemented-rendered",
        "mappedGroupId": "azure-slider",
        "libraryExports": ["AzureSlider"],
        "mcpStatus": "implemented-rendered",
        "coverageReason": "Extracted via get_design_context + get_variable_defs and implemented as AzureSlider (Fluent Slider with inline label, info, and value readout) with a dedicated showcase preview."
      },
      {
        "name": "Azure Horizontal Tab",
        "nodeId": "29167:8324",
        "pageName": "Tablist",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29167-8324&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": "Azure Vertical Tab",
        "nodeId": "29195:7388",
        "pageName": "Tablist",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29195-7388&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": "Azure Horizontal TabList",
        "nodeId": "29553:14761",
        "pageName": "Tablist",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29553-14761&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": "Azure Vertical TabList",
        "nodeId": "29553:14688",
        "pageName": "Tablist",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29553-14688&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".Horizontal Swap",
        "nodeId": "29185:10188",
        "pageName": "Tablist",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29185-10188&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".Vertical Swap",
        "nodeId": "29195:8155",
        "pageName": "Tablist",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29195-8155&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "tabs-popovers-help",
        "libraryExports": [
          "TabsWithContent"
        ],
        "mcpStatus": "grouped-only",
        "coverageReason": "linked into tabs/popovers/help mapping."
      },
      {
        "name": ".Row",
        "nodeId": "29804:6859",
        "pageName": "Tags by resource",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29804-6859&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": "Tags by Resource",
        "nodeId": "36787:12056",
        "pageName": "Tags by resource",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=36787-12056&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "resource-tag-editor",
        "libraryExports": [
          "ResourceTagEditor"
        ],
        "mcpStatus": "grouped-only",
        "coverageReason": "Represented by the ResourceTagEditor export with grouped mapping."
      },
      {
        "name": "Toolbar (Azure)",
        "nodeId": "29553:7576",
        "pageName": "Toolbar for Azure",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29553-7576&m=dev",
        "coverageStatus": "implemented-rendered",
        "mappedGroupId": "azure-toolbar",
        "libraryExports": ["AzureToolbar"],
        "mcpStatus": "implemented-rendered",
        "coverageReason": "Extracted via get_design_context + get_variable_defs and implemented as AzureToolbar (Fluent Toolbar with subtle buttons, dividers, and top-of-page border) with a dedicated showcase preview."
      },
      {
        "name": "Form",
        "nodeId": "27181:1280",
        "pageName": "Form",
        "type": "COMPONENT",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27181-1280&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "forms-input-rows-footer",
        "libraryExports": [
          "FormSection"
        ],
        "mcpStatus": "grouped-only",
        "coverageReason": "Represented by the forms/input-row/footer family."
      },
      {
        "name": ".Input row",
        "nodeId": "27293:520",
        "pageName": "Form",
        "type": "COMPONENT",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27293-520&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "forms-input-rows-footer",
        "libraryExports": [
          "FormFieldRow"
        ],
        "mcpStatus": "grouped-only",
        "coverageReason": "Represented by the forms/input-row/footer family."
      },
      {
        "name": ".hierarchical indicator",
        "nodeId": "27175:3382",
        "pageName": "Form",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27175-3382&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".Label",
        "nodeId": "27878:1838",
        "pageName": "Form",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27878-1838&m=dev",
        "coverageStatus": "showcase-placeholder",
        "mappedGroupId": "forms-input-rows-footer",
        "libraryExports": [
          "FormFieldRow"
        ],
        "mcpStatus": "grouped-only",
        "coverageReason": "Represented by the forms/input-row/footer family."
      },
      {
        "name": ".Asterix",
        "nodeId": "30330:1179",
        "pageName": "Form",
        "type": "COMPONENT",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=30330-1179&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".Azure UI Kit Header (local)",
        "nodeId": "25365:18143",
        "pageName": "Local components",
        "type": "COMPONENT",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=25365-18143&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".Design System",
        "nodeId": "27218:35323",
        "pageName": "Local components",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27218-35323&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".Design System links",
        "nodeId": "38075:124063",
        "pageName": "Local components",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=38075-124063&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".Code status",
        "nodeId": "27218:35373",
        "pageName": "Local components",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27218-35373&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".Progress",
        "nodeId": "27218:35412",
        "pageName": "Local components",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27218-35412&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".component row (local)",
        "nodeId": "35718:12181",
        "pageName": "Local components",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=35718-12181&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".Latest Component",
        "nodeId": "38080:124096",
        "pageName": "Local components",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=38080-124096&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      },
      {
        "name": ".Design System Update Notice",
        "nodeId": "38080:124134",
        "pageName": "Local components",
        "type": "COMPONENT_SET",
        "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=38080-124134&m=dev",
        "coverageStatus": "needs-mcp-extraction",
        "mcpStatus": "needs-mcp-extraction",
        "coverageReason": "No explicit exact-node or grouped local mapping is recorded in the manifest yet."
      }
    ]
  },
  "portability": {
    "downstreamConsumptionDoesNotRequireFigmaMcp": true,
    "traceabilityCitationsAreOptional": true,
    "localArtifactsAreAuthoritativeForOrdinaryUsage": true
  },
  "localConsumptionWorkflow": [
    "Start with the checked-in showcase Components view, examples/README.md, and this manifest.",
    "Use the implemented component mapping plus the linked examples and local source files to find the React/CSS surface you need.",
    "Import primitives from apps/web/src/azure-fluent-system and compose product flows with the checked-in tokens.css contract.",
    "Use Figma dev-mode URLs or MCP only if they are available and you are intentionally refreshing the catalog or investigating a gap."
  ],
  "traceabilityNotes": [
    "Concrete dev-mode URLs are recorded separately for supplied example/doc nodes and for exact inventory components. When an exact inventory node is present but not yet extracted, the manifest marks it needs-mcp-extraction rather than claiming high fidelity, and downstream work can continue without Figma MCP.",
    "Downstream agents can still use the checked-in catalog, examples, showcase, CSS tokens, and React sources without Figma MCP.",
    "Coverage counts come from the full figma-list_file_components_for_code_connect inventory and remain intentionally conservative so grouped mappings do not read as full fidelity.",
    "Canonical step-wizard node 3203:24770 is referenced throughout checked-in design doctrine and catalog docs, but direct MCP lookup returned node-not-found during this pass. That does not block ordinary library consumption because the checked-in local files already carry the usable mappings and examples.",
    "Direct Create Resource pattern node 6672:54683 still resolves to update-notice-only source references, so the library implements CreateResourcePattern as a derived composition of Forms + Step Wizard references. Refreshing those source references later is optional and MCP-dependent.",
    "Exact name/node audit now stands at 105 covered and 43 missing from the external inventory comparison; use the 148-row inventory table as the authoritative list and do not treat grouped local mappings as full fidelity."
  ],
  "groups": [
    {
      "id": "portal-shell-navigation",
      "status": "implemented",
      "figmaComponentSets": [
        "Site Header",
        "global search",
        "Nav for Azure Portal"
      ],
      "libraryExports": [
        "PortalTopNav",
        "PortalRail",
        "PortalLayout"
      ],
      "sourceNodes": [
        "31147:440",
        "40971:40679",
        "35285:10476"
      ],
      "variants": [
        "brand shell with search",
        "icon-only rail navigation",
        "shell frame with breadcrumb/header/body/footer slots"
      ],
      "publicExamples": [
        "examples/portal-shell.example.tsx"
      ],
      "notes": "Conservative local portal-shell mapping: PortalTopNav and PortalLayout stay visibly selectable, but exact Azure portal chrome still needs narrower node-by-node extraction before claiming high fidelity.",
      "implementationFiles": [
        "components.tsx",
        "tokens.css",
        "examples/portal-shell.example.tsx"
      ]
    },
    {
      "id": "blade-header-actions",
      "status": "implemented",
      "figmaComponentSets": [
        "Blade header"
      ],
      "libraryExports": [
        "BladeHeader",
        "IconActionButton",
        "StatusIconText"
      ],
      "sourceNodes": [
        "32630:8970"
      ],
      "variants": [
        "large and compact title sizes",
        "resource glyph + subtitle",
        "action row + overflow",
        "dismiss and prompt ribbon"
      ],
      "publicExamples": [
        "examples/blade-header.example.tsx",
        "examples/provider-layout.example.tsx"
      ],
      "implementationFiles": [
        "components.tsx",
        "examples/blade-header.example.tsx"
      ]
    },
    {
      "id": "service-menu-local-navigation",
      "status": "implemented",
      "figmaComponentSets": [
        "Service Menu",
        "menu item",
        "menu search",
        "menu group"
      ],
      "libraryExports": [
        "ServiceMenu"
      ],
      "sourceNodes": [
        "32610:9825",
        "32610:9731",
        "32610:9943",
        "32610:9931"
      ],
      "variants": [
        "searchable expanded rail",
        "collapsed icon rail",
        "grouped items with favorites",
        "nested child rows with brand selection rail"
      ],
      "publicExamples": [
        "examples/service-menu.example.tsx",
        "examples/provider-layout.example.tsx"
      ],
      "implementationFiles": [
        "components.tsx",
        "examples/service-menu.example.tsx"
      ]
    },
    {
      "id": "command-bar-filtering",
      "status": "implemented",
      "figmaComponentSets": [
        "Toolbar",
        "Filter pill dropdown",
        "Filtering pattern"
      ],
      "libraryExports": [
        "CommandBar",
        "PortalCommandBar",
        "DataToolbar",
        "FilterBar",
        "BrowseResourcePattern",
        "FilteringPattern"
      ],
      "sourceNodes": [
        "29553:7574",
        "29553:7575",
        "25378:3066",
        "3273:15356",
        "40971:32871"
      ],
      "variants": [
        "page command strip",
        "selected and removable filter pills",
        "search + filter + footer browse composition"
      ],
      "publicExamples": [
        "examples/azure-data-grid-filtering.example.tsx",
        "examples/browse-resource-pattern.example.tsx",
        "examples/portal-shell.example.tsx"
      ],
      "implementationFiles": [
        "components.tsx",
        "patterns.tsx",
        "examples/browse-resource-pattern.example.tsx"
      ]
    },
    {
      "id": "data-grid-dense-list-states",
      "status": "implemented",
      "figmaComponentSets": [
        "Azure F2 data grid default/editable",
        "Grid cell tags",
        "List and Grids pattern"
      ],
      "libraryExports": [
        "AzureDataGrid",
        "AzureEmptyState"
      ],
      "sourceNodes": [
        "28093:32729",
        "28784:55430",
        "28752:53376",
        "3715:20982"
      ],
      "variants": [
        "compact and cozy density",
        "sortable headers",
        "interactive rows",
        "loading and empty state rows"
      ],
      "publicExamples": [
        "examples/azure-data-grid-filtering.example.tsx",
        "examples/browse-resource-pattern.example.tsx",
        "examples/portal-shell.example.tsx"
      ],
      "notes": "Resource/status/persona cell anatomy stays caller-owned through renderCell so the library can cover multiple row types without hardcoding app-specific content.",
      "implementationFiles": [
        "components.tsx",
        "tokens.css",
        "examples/azure-data-grid-filtering.example.tsx"
      ]
    },
    {
      "id": "resource-tag-editor",
      "status": "implemented",
      "figmaComponentSets": [
        "Tags by resource"
      ],
      "libraryExports": [
        "ResourceTagEditor"
      ],
      "sourceNodes": [
        "29807:5989",
        "36787:12057",
        "29804:6858"
      ],
      "variants": [
        "large and medium density row editors",
        "resource picker combobox",
        "inline validation and delete action"
      ],
      "publicExamples": [
        "examples/resource-tag-editor.example.tsx"
      ],
      "implementationFiles": [
        "components.tsx",
        "examples/resource-tag-editor.example.tsx"
      ]
    },
    {
      "id": "forms-input-rows-footer",
      "status": "implemented",
      "figmaComponentSets": [
        "Forms",
        "Form label",
        ".Input row",
        "Form footer"
      ],
      "libraryExports": [
        "AzureForm",
        "FormFieldRow",
        "FormFooter",
        "FeedbackFooter"
      ],
      "sourceNodes": [
        "27181:1280",
        "27878:1838",
        "27293:520",
        "35285:10489"
      ],
      "variants": [
        "fixed label column + control column",
        "info label affordance",
        "helper, validation, and status lines",
        "primary/secondary footer actions with restrained feedback strip"
      ],
      "publicExamples": [
        "examples/form-wizard.example.tsx",
        "examples/service-overview-feedback.example.tsx"
      ],
      "implementationFiles": [
        "components.tsx",
        "tokens.css",
        "examples/form-wizard.example.tsx"
      ]
    },
    {
      "id": "step-wizard-and-derived-create-resource",
      "status": "implemented-derived",
      "figmaComponentSets": [
        "Step Wizard",
        "Create a Resource"
      ],
      "libraryExports": [
        "AzureStepList",
        "StepWizardPattern",
        "CreateResourcePattern"
      ],
      "sourceNodes": [
        "3203:24770",
        "3203:15419",
        "3203:3981",
        "3203:19343",
        "6672:54683",
        "6744:54790"
      ],
      "variants": [
        "horizontal step context with descriptions",
        "validation-summary-first blade flow",
        "review + create footer progression"
      ],
      "publicExamples": [
        "examples/form-wizard.example.tsx",
        "examples/create-resource-pattern.example.tsx"
      ],
      "notes": "CreateResourcePattern is intentionally reference-derived until direct Create Resource frames are published. The step-list anatomy is grounded in the cached 3203:24770 workflow even though live MCP could not re-open that node during this pass.",
      "implementationFiles": [
        "patterns.tsx",
        "components.tsx",
        "examples/create-resource-pattern.example.tsx"
      ]
    },
    {
      "id": "pager",
      "status": "implemented",
      "figmaComponentSets": [
        "Pager"
      ],
      "libraryExports": [
        "Pager"
      ],
      "sourceNodes": [
        "27119:16070",
        "27162:1910"
      ],
      "variants": [
        "count summary",
        "rows-per-page picker",
        "previous/next controls"
      ],
      "publicExamples": [
        "examples/azure-data-grid-filtering.example.tsx",
        "examples/browse-resource-pattern.example.tsx"
      ],
      "implementationFiles": [
        "components.tsx",
        "examples/azure-data-grid-filtering.example.tsx"
      ]
    },
    {
      "id": "azure-slider",
      "status": "implemented",
      "figmaComponentSets": [
        "Slider with numbers"
      ],
      "libraryExports": [
        "AzureSlider"
      ],
      "sourceNodes": [
        "28472:10338"
      ],
      "mcpNodes": [
        {
          "component": "Slider with numbers",
          "nodeId": "28472:10338",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28472-10338&m=dev",
          "status": "extracted"
        }
      ],
      "variants": [
        "rest rail + accessible stroke",
        "filled compound-brand track",
        "thumb with inline value readout"
      ],
      "publicExamples": [
        "examples/azure-slider.example.tsx"
      ],
      "implementationFiles": [
        "components.tsx",
        "tokens.css",
        "showcase/AzureFluentShowcaseApp.tsx",
        "examples/azure-slider.example.tsx"
      ],
      "notes": "AzureSlider wraps the Fluent Slider with inline label, info tooltip, and an optional value readout. Exact inventory node 28472:10338 was extracted via get_design_context + get_variable_defs."
    },
    {
      "id": "progress-bar-with-label",
      "status": "implemented",
      "figmaComponentSets": [
        "Progress Bar with labels",
        "Animated Progress Bar with labels"
      ],
      "libraryExports": [
        "ProgressBarWithLabel"
      ],
      "sourceNodes": [
        "28174:7417",
        "28209:4560"
      ],
      "mcpNodes": [
        {
          "component": "Progress Bar with labels",
          "nodeId": "28174:7417",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28174-7417&m=dev",
          "status": "extracted"
        },
        {
          "component": "Animated Progress Bar with labels",
          "nodeId": "28209:4560",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28209-4560&m=dev",
          "status": "extracted"
        }
      ],
      "variants": [
        "determinate with label + description",
        "indeterminate / animated run"
      ],
      "publicExamples": [
        "examples/progress-bar-with-label.example.tsx"
      ],
      "implementationFiles": [
        "components.tsx",
        "tokens.css",
        "showcase/AzureFluentShowcaseApp.tsx",
        "examples/progress-bar-with-label.example.tsx"
      ],
      "notes": "ProgressBarWithLabel covers both the labelled determinate progress bar (28174:7417) and the animated/indeterminate variant (28209:4560) through its indeterminate prop. Both nodes were extracted via get_design_context + get_variable_defs."
    },
    {
      "id": "file-upload",
      "status": "implemented",
      "figmaComponentSets": [
        "Upload File"
      ],
      "libraryExports": [
        "FileUpload"
      ],
      "sourceNodes": [
        "25412:31783"
      ],
      "mcpNodes": [
        {
          "component": "Upload File",
          "nodeId": "25412:31783",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=25412-31783&m=dev",
          "status": "extracted"
        }
      ],
      "variants": [
        "default (browse)",
        "selected file",
        "uploading (progress + cancel)",
        "success",
        "drag-and-drop zone"
      ],
      "publicExamples": [
        "examples/file-upload.example.tsx"
      ],
      "implementationFiles": [
        "components.tsx",
        "tokens.css",
        "showcase/AzureFluentShowcaseApp.tsx",
        "examples/file-upload.example.tsx"
      ],
      "notes": "FileUpload drives default/selected/progress/success/dragdrop states from a single state prop. Exact inventory node 25412:31783 was extracted via get_design_context + get_variable_defs."
    },
    {
      "id": "filterable-combo-box",
      "status": "implemented",
      "figmaComponentSets": [
        "Filterable combo box"
      ],
      "libraryExports": [
        "FilterableComboBox"
      ],
      "sourceNodes": [
        "25248:8173"
      ],
      "mcpNodes": [
        {
          "component": "Filterable combo box",
          "nodeId": "25248:8173",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=25248-8173&m=dev",
          "status": "extracted"
        }
      ],
      "variants": [
        "rest",
        "type-to-filter",
        "multiselect"
      ],
      "publicExamples": [
        "examples/filterable-combo-box.example.tsx"
      ],
      "implementationFiles": [
        "components.tsx",
        "tokens.css",
        "showcase/AzureFluentShowcaseApp.tsx",
        "examples/filterable-combo-box.example.tsx"
      ],
      "notes": "FilterableComboBox adds client-side type-to-filter over the Fluent Combobox. Exact inventory node 25248:8173 was extracted via get_design_context + get_variable_defs."
    },
    {
      "id": "azure-toolbar",
      "status": "implemented",
      "figmaComponentSets": [
        "Toolbar (Azure)"
      ],
      "libraryExports": [
        "AzureToolbar"
      ],
      "sourceNodes": [
        "29553:7576"
      ],
      "mcpNodes": [
        {
          "component": "Toolbar (Azure)",
          "nodeId": "29553:7576",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29553-7576&m=dev",
          "status": "extracted"
        }
      ],
      "variants": [
        "subtle command buttons",
        "grouped with dividers",
        "top-of-page bottom border"
      ],
      "publicExamples": [
        "examples/azure-toolbar.example.tsx"
      ],
      "implementationFiles": [
        "components.tsx",
        "tokens.css",
        "showcase/AzureFluentShowcaseApp.tsx",
        "examples/azure-toolbar.example.tsx"
      ],
      "notes": "AzureToolbar wraps the Fluent Toolbar with subtle buttons, id-driven dividers, and an optional top-of-page border. Exact inventory node 29553:7576 was extracted via get_design_context + get_variable_defs."
    },
    {
      "id": "notifications-errors-empty-state",
      "status": "implemented",
      "figmaComponentSets": [
        "Notifications",
        "Error Messages",
        "Search No Results",
        "Empty state"
      ],
      "libraryExports": [
        "NotificationPane",
        "NotificationPattern",
        "ErrorPattern",
        "AzureEmptyState"
      ],
      "sourceNodes": [
        "5707:60107",
        "1024:309",
        "40971:35678",
        "41153:24673",
        "29232:42433"
      ],
      "variants": [
        "inline message bar guidance",
        "notification side pane list",
        "contextual no-results empty state",
        "success, warning, error, info tones"
      ],
      "publicExamples": [
        "examples/service-overview-feedback.example.tsx",
        "examples/browse-resource-pattern.example.tsx"
      ],
      "notes": "The hidden/generic standalone empty-state component remains intentionally modest; contextual no-results guidance is preferred over generic illustration-first empty cards.",
      "implementationFiles": [
        "components.tsx",
        "patterns.tsx",
        "examples/service-overview-feedback.example.tsx"
      ]
    },
    {
      "id": "delete-confirmation",
      "status": "implemented",
      "figmaComponentSets": [
        "Delete a Resource"
      ],
      "libraryExports": [
        "DeleteResourceDialog",
        "DeleteConfirmationDialog"
      ],
      "sourceNodes": [
        "5706:33046"
      ],
      "variants": [
        "soft-delete recovery copy",
        "consequence checklist",
        "optional acknowledgement gate"
      ],
      "publicExamples": [
        "examples/service-overview-feedback.example.tsx"
      ],
      "implementationFiles": [
        "components.tsx",
        "patterns.tsx",
        "examples/service-overview-feedback.example.tsx"
      ]
    },
    {
      "id": "service-overview",
      "status": "implemented",
      "figmaComponentSets": [
        "Service Overview"
      ],
      "libraryExports": [
        "ServiceOverviewPattern"
      ],
      "sourceNodes": [
        "4654:83587"
      ],
      "variants": [
        "overview cards with actions",
        "header actions + follow-up sections"
      ],
      "publicExamples": [
        "examples/service-overview-feedback.example.tsx"
      ],
      "implementationFiles": [
        "patterns.tsx",
        "examples/service-overview-feedback.example.tsx"
      ]
    },
    {
      "id": "tabs-popovers-help",
      "status": "implemented",
      "figmaComponentSets": [
        "Tab lists",
        "Popover content variants"
      ],
      "libraryExports": [
        "AzureTabList",
        "AzureStepList",
        "HelpPopover",
        "CalloutPopover"
      ],
      "sourceNodes": [
        "29553:14762",
        "29167:8291",
        "29195:8155",
        "27965:13711",
        "28024:14416",
        "28035:15353"
      ],
      "variants": [
        "horizontal and vertical tabs",
        "validation status icons",
        "light and brand callout surfaces"
      ],
      "publicExamples": [
        "examples/tab-popovers.example.tsx",
        "examples/form-wizard.example.tsx"
      ],
      "implementationFiles": [
        "components.tsx",
        "examples/tab-popovers.example.tsx"
      ]
    },
    {
      "id": "accordion",
      "status": "implemented",
      "figmaComponentSets": [
        "Accordion"
      ],
      "libraryExports": [
        "AzureAccordion"
      ],
      "sourceNodes": [
        "29739:1810"
      ],
      "mcpNodes": [
        {
          "component": "Accordion doc page example",
          "nodeId": "29739:1810",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29739-1810&m=dev",
          "status": "extracted"
        },
        {
          "component": "Accordion",
          "nodeId": "30028:627",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=30028-627&m=dev",
          "status": "extracted"
        }
      ],
      "variants": [
        "with border collapsed row",
        "with border expanded row",
        "default / without border row"
      ],
      "publicExamples": [
        "examples/accordion.example.tsx"
      ],
      "implementationFiles": [
        "components.tsx",
        "tokens.css",
        "showcase/AzureFluentShowcaseApp.tsx",
        "examples/accordion.example.tsx"
      ],
      "notes": "Named export is implemented and visibly rendered. Exact inventory component node 30028:627 was extracted in this pass."
    },
    {
      "id": "code-snippet",
      "status": "implemented",
      "figmaComponentSets": [
        "Code snippet"
      ],
      "libraryExports": [
        "CodeSnippet"
      ],
      "sourceNodes": [
        "38113:34959"
      ],
      "mcpNodes": [
        {
          "component": "Code snippet doc page example",
          "nodeId": "38113:34959",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=38113-34959&m=dev",
          "status": "extracted"
        },
        {
          "component": "Code snippet",
          "nodeId": "38116:47202",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=38116-47202&m=dev",
          "status": "extracted"
        },
        {
          "component": ".Code line",
          "nodeId": "38108:33491",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=38108-33491&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 21:00 PDT",
          "variableDefs": "Succeeded 2026-07-08 21:00 PDT",
          "notes": "Exact code-row shell confirmed and linked into the shared CodeSnippet line renderer."
        },
        {
          "component": ".Number",
          "nodeId": "38108:33570",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=38108-33570&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 21:00 PDT",
          "variableDefs": "Succeeded 2026-07-08 21:00 PDT",
          "notes": "Exact number-gutter and collapse-state variants confirmed and linked into CodeSnippet line numbering."
        },
        {
          "component": ".Code level(s)",
          "nodeId": "38108:33579",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=38108-33579&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 21:00 PDT",
          "variableDefs": "Succeeded 2026-07-08 21:00 PDT",
          "notes": "Exact indentation-rail variants confirmed and linked into CodeSnippet nested line rendering."
        },
        {
          "component": ".JSON Collapse",
          "nodeId": "38113:34678",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=38113-34678&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 21:00 PDT",
          "variableDefs": "Succeeded 2026-07-08 21:00 PDT",
          "notes": "Exact JSON collapse affordance confirmed and linked into CodeSnippet expand/collapse controls."
        }
      ],
      "variants": [
        "json and cli snippet surfaces",
        "line-number gutters with nested indentation",
        "collapsible json/code rows"
      ],
      "publicExamples": [
        "examples/code-snippet.example.tsx"
      ],
      "implementationFiles": [
        "components.tsx",
        "showcase/AzureFluentShowcaseApp.tsx",
        "examples/code-snippet.example.tsx"
      ],
      "notes": "Direct MCP extraction now covers the inventory Code snippet root plus the code-line, number gutter, indentation, and JSON-collapse child nodes. The checked-in CodeSnippet export keeps those child parts linked into one shared editor surface."
    },
    {
      "id": "copy-button",
      "status": "implemented",
      "figmaComponentSets": [
        "Copy Button"
      ],
      "libraryExports": [
        "CopyButton"
      ],
      "sourceNodes": [
        "25106:3107"
      ],
      "mcpNodes": [
        {
          "component": "Copy button doc page example",
          "nodeId": "25106:3107",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=25106-3107&m=dev",
          "status": "extracted"
        },
        {
          "component": "Copy Button",
          "nodeId": "25260:8600",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=25260-8600&m=dev",
          "status": "extracted"
        }
      ],
      "variants": [
        "icon-only copy affordance",
        "labeled copy affordance",
        "rest, hover, and copied states"
      ],
      "publicExamples": [
        "examples/copy-button.example.tsx"
      ],
      "implementationFiles": [
        "components.tsx",
        "tokens.css",
        "showcase/AzureFluentShowcaseApp.tsx",
        "examples/copy-button.example.tsx"
      ],
      "notes": "Named export is implemented and visibly rendered. Exact inventory component node 25260:8600 was extracted in this pass."
    },
    {
      "id": "copilot-composer",
      "status": "implemented-rendered",
      "figmaComponentSets": [
        ".Chat Input [Azure]"
      ],
      "libraryExports": [
        "CopilotComposer"
      ],
      "sourceNodes": [
        "32382:38450"
      ],
      "mcpNodes": [
        {
          "component": ".Chat Input [Azure]",
          "nodeId": "32382:38450",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38450&m=dev",
          "status": "implemented-rendered",
          "designContext": "Succeeded 2026-07-08 20:41 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:41 PDT",
          "notes": "Copilot composer shell confirmed from MCP extraction on 2026-07-08 20:41 PDT."
        },
        {
          "component": "Agent Toggle",
          "nodeId": "32382:38689",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38689&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:41 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:41 PDT",
          "notes": "Agent toggle variants confirmed from MCP extraction on 2026-07-08 20:41 PDT."
        },
        {
          "component": "Agents Off Icon",
          "nodeId": "32382:38722",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38722&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:52 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:52 PDT",
          "notes": "Agents-off icon variants confirmed from MCP extraction on 2026-07-08 20:52 PDT and linked into CopilotComposer toggle states."
        },
        {
          "component": ".Input Footer_LG",
          "nodeId": "32382:38729",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38729&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:41 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:41 PDT",
          "notes": "Large footer anatomy confirmed from MCP extraction on 2026-07-08 20:41 PDT."
        },
        {
          "component": ".Input Footer_Sm",
          "nodeId": "33526:118139",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=33526-118139&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:52 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:52 PDT",
          "notes": "Compact footer anatomy confirmed from MCP extraction on 2026-07-08 20:52 PDT and linked into CopilotComposer."
        },
        {
          "component": ".Send_Icon",
          "nodeId": "32382:38835",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38835&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:41 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:41 PDT",
          "notes": "Send/stop icon states confirmed from MCP extraction on 2026-07-08 20:41 PDT."
        }
      ],
      "variants": [
        "prompt box",
        "attachments row",
        "agent mode toggle",
        "send / stop action"
      ],
      "publicExamples": [
        "examples/copilot-composer-response.example.tsx"
      ],
      "implementationFiles": [
        "components.tsx",
        "showcase/AzureFluentShowcaseApp.tsx",
        "examples/copilot-composer-response.example.tsx"
      ],
      "notes": "Direct MCP extraction succeeded for the exact chat-input shell, agent toggle, large footer, and send icon. The checked-in CopilotComposer example and showcase preview now reflect those extracted states; Agents Off Icon and the small footer remain pending."
    },
    {
      "id": "copilot-response",
      "status": "implemented-rendered",
      "figmaComponentSets": [
        "Copilot Row Swap",
        "Copilot Message / Response Element"
      ],
      "libraryExports": [
        "CopilotResponse"
      ],
      "sourceNodes": [
        "32382:38124",
        "32382:38154"
      ],
      "mcpNodes": [
        {
          "component": "Copilot Row Swap",
          "nodeId": "32382:38124",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38124&m=dev",
          "status": "implemented-rendered",
          "designContext": "Succeeded 2026-07-08 20:41 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:41 PDT",
          "notes": "Copilot response shell confirmed from MCP extraction on 2026-07-08 20:41 PDT."
        },
        {
          "component": "User Message",
          "nodeId": "32382:38151",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38151&m=dev",
          "status": "implemented-rendered",
          "designContext": "Succeeded 2026-07-08 20:41 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:41 PDT",
          "notes": "User message bubble confirmed from MCP extraction on 2026-07-08 20:41 PDT."
        },
        {
          "component": "Copilot Message / Response Element",
          "nodeId": "32382:38154",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38154&m=dev",
          "status": "implemented-rendered",
          "designContext": "Succeeded 2026-07-08 20:41 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:41 PDT",
          "notes": "Assistant response shell confirmed from MCP extraction on 2026-07-08 20:41 PDT."
        },
        {
          "component": ".Footeractions",
          "nodeId": "32382:38177",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38177&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:41 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:41 PDT",
          "notes": "Feedback action row confirmed from MCP extraction on 2026-07-08 20:41 PDT."
        },
        {
          "component": ".Code",
          "nodeId": "32382:38197",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38197&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:41 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:41 PDT",
          "notes": "Inline code styling confirmed from MCP extraction on 2026-07-08 20:41 PDT."
        },
        {
          "component": ".Code Snippet",
          "nodeId": "32382:38204",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38204&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:41 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:41 PDT",
          "notes": "Code snippet anatomy confirmed from MCP extraction on 2026-07-08 20:41 PDT."
        },
        {
          "component": ".data grid",
          "nodeId": "32382:38257",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38257&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:41 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:41 PDT",
          "notes": "Copilot data grid treatment confirmed from MCP extraction on 2026-07-08 20:41 PDT."
        },
        {
          "component": ".single selection",
          "nodeId": "32382:38372",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38372&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:41 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:41 PDT",
          "notes": "Single-selection response state confirmed from MCP extraction on 2026-07-08 20:41 PDT."
        },
        {
          "component": ".Multiple selection",
          "nodeId": "32382:38395",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38395&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:41 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:41 PDT",
          "notes": "Multiple-selection response state confirmed from MCP extraction on 2026-07-08 20:41 PDT."
        },
        {
          "component": ".Confirmation Buttons",
          "nodeId": "32382:38418",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38418&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:41 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:41 PDT",
          "notes": "Confirmation button row confirmed from MCP extraction on 2026-07-08 20:41 PDT."
        },
        {
          "component": "Request Count",
          "nodeId": "32382:38434",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38434&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:41 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:41 PDT",
          "notes": "Request-count metadata confirmed from MCP extraction on 2026-07-08 20:41 PDT."
        },
        {
          "component": "Latency",
          "nodeId": "32382:38442",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38442&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:41 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:41 PDT",
          "notes": "Latency row confirmed from MCP extraction on 2026-07-08 20:41 PDT."
        },
        {
          "component": ".ChatHeaders",
          "nodeId": "33921:19578",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=33921-19578&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:41 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:41 PDT",
          "notes": "Chat header variants confirmed from MCP extraction on 2026-07-08 20:41 PDT."
        },
        {
          "component": "Agent Icon (Color)",
          "nodeId": "33921:19675",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=33921-19675&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:41 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:41 PDT",
          "notes": "Agent icon variants confirmed from MCP extraction on 2026-07-08 20:41 PDT."
        }
      ],
      "variants": [
        "assistant response bubble",
        "confirmation actions",
        "lightweight response action row"
      ],
      "publicExamples": [
        "examples/copilot-composer-response.example.tsx"
      ],
      "implementationFiles": [
        "components.tsx",
        "showcase/AzureFluentShowcaseApp.tsx",
        "examples/copilot-composer-response.example.tsx"
      ],
      "notes": "Direct MCP extraction succeeded for the exact Copilot/user response rows plus the code, choice, data-grid, latency, and footer subparts. The checked-in CopilotResponse example and showcase preview now reflect those extracted states."
    },
    {
      "id": "inline-copilot",
      "status": "implemented-rendered",
      "figmaComponentSets": [
        "Inline Copilot - open start",
        "Inline Copilot - guided start"
      ],
      "libraryExports": [
        "InlineCopilot"
      ],
      "sourceNodes": [
        "29192:8232",
        "29192:8293"
      ],
      "mcpNodes": [
        {
          "component": "Inline Copilot - open start",
          "nodeId": "29192:8232",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29192-8232&m=dev",
          "status": "implemented-rendered",
          "designContext": "Succeeded 2026-07-08 20:23 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:23 PDT",
          "notes": "Exact open-start shell confirmed against the checked-in InlineCopilot surface."
        },
        {
          "component": "Inline Copilot - guided start",
          "nodeId": "29192:8293",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29192-8293&m=dev",
          "status": "implemented-rendered",
          "designContext": "Succeeded 2026-07-08 20:23 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:23 PDT",
          "notes": "Exact guided-start shell confirmed against the checked-in InlineCopilot surface."
        },
        {
          "component": ".Flair",
          "nodeId": "29389:12096",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29389-12096&m=dev",
          "status": "implemented-rendered",
          "designContext": "Succeeded 2026-07-08 20:23 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:23 PDT",
          "notes": "Converted into the extracted flair treatment on the InlineCopilot popover surface."
        },
        {
          "component": ".prompt input",
          "nodeId": "29192:8358",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29192-8358&m=dev",
          "status": "implemented-rendered",
          "designContext": "Succeeded 2026-07-08 20:23 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:23 PDT",
          "notes": "Prompt input shell now uses the extracted title, prompt placeholder, and CTA hierarchy."
        },
        {
          "component": ".Inline Copilot title",
          "nodeId": "29192:8429",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29192-8429&m=dev",
          "status": "implemented-rendered",
          "designContext": "Succeeded 2026-07-08 20:23 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:23 PDT",
          "notes": "Title variants now map cleanly onto InlineCopilot title text in examples and showcase."
        }
      ],
      "variants": [
        "anchored popover",
        "prompt suggestions",
        "inline generate action"
      ],
      "publicExamples": [
        "examples/inline-copilot.example.tsx"
      ],
      "implementationFiles": [
        "components.tsx",
        "showcase/AzureFluentShowcaseApp.tsx",
        "examples/inline-copilot.example.tsx"
      ],
      "notes": "Direct MCP extraction succeeded for the open-start, guided-start, flair, prompt-input, and title nodes. The checked-in InlineCopilot example and showcase preview now reflect those exact surfaces."
    },
    {
      "id": "agentic-progress",
      "status": "implemented-rendered",
      "figmaComponentSets": [
        ".Agentic List (CoT)"
      ],
      "libraryExports": [
        "AgenticProgress"
      ],
      "sourceNodes": [
        "27950:10571"
      ],
      "mcpNodes": [
        {
          "component": ".Reasoning (CoT)",
          "nodeId": "27865:7924",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27865-7924&m=dev",
          "status": "implemented-rendered",
          "designContext": "Succeeded 2026-07-08 20:41 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:41 PDT",
          "notes": "Reasoning row confirmed from MCP extraction on 2026-07-08 20:41 PDT."
        },
        {
          "component": ".Artifact pill (CoT)",
          "nodeId": "27865:11293",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27865-11293&m=dev",
          "status": "implemented-rendered",
          "designContext": "Succeeded 2026-07-08 20:41 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:41 PDT",
          "notes": "Artifact pill confirmed from MCP extraction on 2026-07-08 20:41 PDT."
        },
        {
          "component": ".Complete (CoT)",
          "nodeId": "27880:12932",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27880-12932&m=dev",
          "status": "implemented-rendered",
          "designContext": "Succeeded 2026-07-08 20:41 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:41 PDT",
          "notes": "Complete-state row confirmed from MCP extraction on 2026-07-08 20:41 PDT."
        },
        {
          "component": ".Needs user input (CoT)",
          "nodeId": "27880:13472",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27880-13472&m=dev",
          "status": "implemented-rendered",
          "designContext": "Succeeded 2026-07-08 20:41 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:41 PDT",
          "notes": "Needs-input row confirmed from MCP extraction on 2026-07-08 20:41 PDT."
        },
        {
          "component": ".Action swap (CoT)",
          "nodeId": "27887:13693",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27887-13693&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:52 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:52 PDT",
          "notes": "Action-swap taxonomy confirmed from MCP extraction on 2026-07-08 20:52 PDT and linked into AgenticProgress row variants."
        },
        {
          "component": ".Show artifacts (CoT)",
          "nodeId": "27895:9236",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27895-9236&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:52 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:52 PDT",
          "notes": "Show-artifacts summary control confirmed from MCP extraction on 2026-07-08 20:52 PDT and linked into AgenticProgress."
        },
        {
          "component": "Chain of thought",
          "nodeId": "27895:11157",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27895-11157&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:52 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:52 PDT",
          "notes": "Root wrapper was confirmed via MCP root extraction plus sublayer follow-up on 2026-07-08 20:52 PDT and linked into AgenticProgress."
        },
        {
          "component": ".Agentic List (CoT)",
          "nodeId": "27950:10571",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27950-10571&m=dev",
          "status": "implemented-rendered",
          "designContext": "Succeeded 2026-07-08 20:52 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:52 PDT",
          "notes": "List-level shell confirmed from MCP extraction on 2026-07-08 20:52 PDT and matches the checked-in AgenticProgress example/showcase."
        }
      ],
      "variants": [
        "collapsed and expanded progress rows",
        "approval request state",
        "artifact pills"
      ],
      "publicExamples": [
        "examples/agentic-progress.example.tsx"
      ],
      "implementationFiles": [
        "components.tsx",
        "showcase/AzureFluentShowcaseApp.tsx",
        "examples/agentic-progress.example.tsx"
      ],
      "notes": "Direct MCP extraction now covers the reasoning row, artifact pill, complete state, needs-input state, action-swap taxonomy, show-artifacts summary, root wrapper, and list-level shell. The local AgenticProgress surface still folds some of those subparts together instead of exposing one export per Figma child."
    },
    {
      "id": "copilot-workspace-pattern",
      "status": "implemented-rendered",
      "figmaComponentSets": [
        "Azure Copilot"
      ],
      "libraryExports": [
        "CopilotWorkspacePattern"
      ],
      "sourceNodes": [
        "32382:40353"
      ],
      "mcpNodes": [
        {
          "component": "Azure Copilot",
          "nodeId": "32382:40353",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-40353&m=dev",
          "status": "implemented-rendered",
          "designContext": "Succeeded 2026-07-08 20:52 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:52 PDT",
          "notes": "Root Azure Copilot shell confirmed from MCP extraction on 2026-07-08 20:52 PDT; current local workspace pattern renders the shell while folding child nav/header details together."
        },
        {
          "component": "Azure Copilot Nav Drawer",
          "nodeId": "32382:39054",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-39054&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:52 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:52 PDT",
          "notes": "Nav-drawer shell confirmed from MCP extraction on 2026-07-08 20:52 PDT and linked into CopilotWorkspacePattern."
        },
        {
          "component": ".Nav item",
          "nodeId": "32382:39444",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-39444&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:52 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:52 PDT",
          "notes": "Nav-item states confirmed from MCP extraction on 2026-07-08 20:52 PDT and linked into CopilotWorkspacePattern."
        },
        {
          "component": ". Nav link Item",
          "nodeId": "32382:39939",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-39939&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:52 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:52 PDT",
          "notes": "Selected-link treatment confirmed from MCP extraction on 2026-07-08 20:52 PDT and linked into CopilotWorkspacePattern."
        },
        {
          "component": ".Nav Icon",
          "nodeId": "32382:39948",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-39948&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:52 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:52 PDT",
          "notes": "Nav-icon button/badge states confirmed from MCP extraction on 2026-07-08 20:52 PDT and linked into CopilotWorkspacePattern."
        },
        {
          "component": ".Copilot Hub Nav header",
          "nodeId": "32382:39961",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-39961&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:52 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:52 PDT",
          "notes": "Branded hub-header anatomy confirmed from MCP extraction on 2026-07-08 20:52 PDT and linked into CopilotWorkspacePattern."
        },
        {
          "component": ".Nav Menu",
          "nodeId": "32382:40121",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-40121&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:52 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:52 PDT",
          "notes": "Nav-menu anatomy confirmed from MCP extraction on 2026-07-08 20:52 PDT and linked into CopilotWorkspacePattern."
        },
        {
          "component": "All Chats(WIP)",
          "nodeId": "32382:40186",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-40186&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:52 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:52 PDT",
          "notes": "All-chats container anatomy confirmed from MCP extraction on 2026-07-08 20:52 PDT and linked into CopilotWorkspacePattern."
        },
        {
          "component": "List Chats - stacked indicators (Wip)",
          "nodeId": "32382:40313",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-40313&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:52 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:52 PDT",
          "notes": "Stacked-indicator chat-list states confirmed from MCP extraction on 2026-07-08 20:52 PDT and linked into CopilotWorkspacePattern."
        },
        {
          "component": "Azure Copilot Header (Sidecar)",
          "nodeId": "34460:64534",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=34460-64534&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:52 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:52 PDT",
          "notes": "Sidecar-header anatomy confirmed from MCP extraction on 2026-07-08 20:52 PDT and linked into CopilotWorkspacePattern."
        },
        {
          "component": "Azure Copilot Header (Expanded)",
          "nodeId": "34460:136270",
          "nodeUrl": "https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=34460-136270&m=dev",
          "status": "showcase-placeholder",
          "designContext": "Succeeded 2026-07-08 20:52 PDT",
          "variableDefs": "Succeeded 2026-07-08 20:52 PDT",
          "notes": "Expanded-header anatomy confirmed from MCP extraction on 2026-07-08 20:52 PDT and linked into CopilotWorkspacePattern."
        }
      ],
      "variants": [
        "service menu + response column",
        "composer below response",
        "task-focused workspace shell"
      ],
      "publicExamples": [
        "examples/copilot-workspace.example.tsx"
      ],
      "implementationFiles": [
        "patterns.tsx",
        "showcase/AzureFluentShowcaseApp.tsx",
        "examples/copilot-workspace.example.tsx"
      ],
      "notes": "Direct MCP extraction now covers the root Azure Copilot shell plus the nav drawer, nav item/link/icon/header/menu, chat-list subparts, and sidecar/expanded headers. The local workspace example still folds those subparts into one broader shell rather than exposing separate exports for each child node."
    }
  ],
  "inventoryRows": [
    {
      "figmaNodeReference": "Contents / .Table of Contents - Components / [40095:5628](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=40095-5628&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "2026-07-08 21:13 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 `get_design_context` + `get_variable_defs` on `40971:35680`",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Azure Copilot & sidecar / Azure Copilot / [32382:40353](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-40353&m=dev)",
      "extractionStatus": "implemented-rendered",
      "extractionDate": "2026-07-08 20:52 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `32382:40353`",
      "implementedMapping": "`CopilotWorkspacePattern`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Chat input / .Chat Input [Azure] / [32382:38450](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38450&m=dev)",
      "extractionStatus": "implemented-rendered",
      "extractionDate": "2026-07-08 20:41 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "`CopilotComposer`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Chat input / Agent Toggle / [32382:38689](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38689&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:41 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "`CopilotComposer`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Chat input / Agents Off Icon / [32382:38722](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38722&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:52 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `32382:38722`",
      "implementedMapping": "`CopilotComposer`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Chat input / .Input Footer_LG / [32382:38729](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38729&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:41 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "`CopilotComposer`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Chat input / .Input Footer_Sm / [33526:118139](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=33526-118139&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:52 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `33526:118139`",
      "implementedMapping": "`CopilotComposer`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Chat input / .Send_Icon / [32382:38835](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38835&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:41 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "`CopilotComposer`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Chat output / Copilot Row Swap / [32382:38124](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38124&m=dev)",
      "extractionStatus": "implemented-rendered",
      "extractionDate": "2026-07-08 20:41 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "`CopilotResponse`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Chat output / User Message / [32382:38151](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38151&m=dev)",
      "extractionStatus": "implemented-rendered",
      "extractionDate": "2026-07-08 20:41 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "`CopilotResponse`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Chat output / Copilot Message / Response Element / [32382:38154](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38154&m=dev)",
      "extractionStatus": "implemented-rendered",
      "extractionDate": "2026-07-08 20:41 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "`CopilotResponse`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Chat output / .Footeractions / [32382:38177](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38177&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:41 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "`CopilotResponse`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Chat output / .Code / [32382:38197](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38197&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:41 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "`CopilotResponse`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Chat output / .Code Snippet / [32382:38204](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38204&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:41 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "`CopilotResponse`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Chat output / .data grid / [32382:38257](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38257&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:41 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "`CopilotResponse`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Chat output / .single selection / [32382:38372](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38372&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:41 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "`CopilotResponse`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Chat output / .Multiple selection / [32382:38395](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38395&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:41 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "`CopilotResponse`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Chat output / .Confirmation Buttons / [32382:38418](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38418&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:41 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "`CopilotResponse`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Chat output / Request Count / [32382:38434](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38434&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:41 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "`CopilotResponse`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Chat output / Latency / [32382:38442](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38442&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:41 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "`CopilotResponse`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Chat output / .ChatHeaders / [33921:19578](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=33921-19578&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:41 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "`CopilotResponse`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Chat output / Agent Icon (Color) / [33921:19675](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=33921-19675&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:41 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "`CopilotResponse`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Navigation & header / Azure Copilot Nav Drawer / [32382:39054](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-39054&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:52 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `32382:39054`",
      "implementedMapping": "`CopilotWorkspacePattern`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Navigation & header / .Nav item / [32382:39444](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-39444&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:52 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `32382:39444`",
      "implementedMapping": "`CopilotWorkspacePattern`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Navigation & header / . Nav link Item / [32382:39939](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-39939&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:52 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `32382:39939`",
      "implementedMapping": "`CopilotWorkspacePattern`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Navigation & header / .Nav Icon / [32382:39948](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-39948&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:52 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `32382:39948`",
      "implementedMapping": "`CopilotWorkspacePattern`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Navigation & header / .Copilot Hub Nav header / [32382:39961](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-39961&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:52 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `32382:39961`",
      "implementedMapping": "`CopilotWorkspacePattern`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Navigation & header / .Nav Menu / [32382:40121](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-40121&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:52 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `32382:40121`",
      "implementedMapping": "`CopilotWorkspacePattern`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Navigation & header / All Chats(WIP) / [32382:40186](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-40186&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:52 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `32382:40186`",
      "implementedMapping": "`CopilotWorkspacePattern`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Navigation & header / List Chats - stacked indicators (Wip) / [32382:40313](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-40313&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:52 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `32382:40313`",
      "implementedMapping": "`CopilotWorkspacePattern`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Navigation & header / Azure Copilot Header (Sidecar) / [34460:64534](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=34460-64534&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:52 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `34460:64534`",
      "implementedMapping": "`CopilotWorkspacePattern`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Navigation & header / Azure Copilot Header (Expanded) / [34460:136270](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=34460-136270&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:52 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `34460:136270`",
      "implementedMapping": "`CopilotWorkspacePattern`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Grounding menu (GM) / .GM_ListItems / [32382:38860](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38860&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:52 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `32382:38860`",
      "implementedMapping": "linked into `ServiceMenu`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Grounding menu (GM) / .Grounding Menu / [32382:38867](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38867&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:52 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `32382:38867`",
      "implementedMapping": "linked into `ServiceMenu`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Grounding menu (GM) / .GM_Header / [32382:38901](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38901&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:52 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `32382:38901`",
      "implementedMapping": "linked into `ServiceMenu`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Grounding menu (GM) / .GM_Overflow / [32382:38968](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38968&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:52 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `32382:38968`",
      "implementedMapping": "linked into `ServiceMenu`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Grounding menu (GM) / .GM_Search / [32382:38987](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38987&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:52 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `32382:38987`",
      "implementedMapping": "linked into `ServiceMenu`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "\u21aa Grounding menu (GM) / .GM_Entity list Item / [32382:38992](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32382-38992&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:52 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `32382:38992`",
      "implementedMapping": "linked into `ServiceMenu`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Chain of thought (Agentic chat) / .Reasoning (CoT) / [27865:7924](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27865-7924&m=dev)",
      "extractionStatus": "implemented-rendered",
      "extractionDate": "2026-07-08 20:41 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "`AgenticProgress`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Chain of thought (Agentic chat) / .Artifact pill (CoT) / [27865:11293](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27865-11293&m=dev)",
      "extractionStatus": "implemented-rendered",
      "extractionDate": "2026-07-08 20:41 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "`AgenticProgress`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Chain of thought (Agentic chat) / .Complete (CoT) / [27880:12932](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27880-12932&m=dev)",
      "extractionStatus": "implemented-rendered",
      "extractionDate": "2026-07-08 20:41 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "`AgenticProgress`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Chain of thought (Agentic chat) / .Needs user input (CoT) / [27880:13472](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27880-13472&m=dev)",
      "extractionStatus": "implemented-rendered",
      "extractionDate": "2026-07-08 20:41 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "`AgenticProgress`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Chain of thought (Agentic chat) / .Action swap (CoT) / [27887:13693](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27887-13693&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:52 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `27887:13693`",
      "implementedMapping": "`AgenticProgress`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Chain of thought (Agentic chat) / .Show artifacts (CoT) / [27895:9236](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27895-9236&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:52 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `27895:9236`",
      "implementedMapping": "`AgenticProgress`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Chain of thought (Agentic chat) / Chain of thought / [27895:11157](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27895-11157&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:52 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `27895:11157`",
      "implementedMapping": "`AgenticProgress`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Chain of thought (Agentic chat) / .Agentic List (CoT) / [27950:10571](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27950-10571&m=dev)",
      "extractionStatus": "implemented-rendered",
      "extractionDate": "2026-07-08 20:52 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `27950:10571`",
      "implementedMapping": "`AgenticProgress`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Copilot entry points / Button Entry Point (Copilot) / [31316:1188](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=31316-1188&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:52 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `31316:1188`",
      "implementedMapping": "linked into `InlineCopilot`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Copilot entry points / Copilot Entry Icon  / [31323:1530](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=31323-1530&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:52 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `31323:1530`",
      "implementedMapping": "linked into `InlineCopilot`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Copilot entry points / Menu Entry Point (Copilot) / [31330:9223](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=31330-9223&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 20:52 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `31330:9223`",
      "implementedMapping": "linked into `InlineCopilot`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Copilot entry points / Prompt Ribbon(Copilot) / [30909:48908](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=30909-48908&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 21:00 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `30909:48908`",
      "implementedMapping": "linked into `InlineCopilot`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Copilot entry points / .Suggested Prompt Pill / [30945:10400](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=30945-10400&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 21:00 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `30945:10400`",
      "implementedMapping": "linked into `InlineCopilot`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Copilot entry points / .Copilot icon / [31000:461](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=31000-461&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 21:13 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 `get_design_context` + `get_variable_defs` on `32610:9923`",
      "implementedMapping": "linked into `ServiceMenu`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Copilot entry points / .Copilot icon(Old) / [41747:68133](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=41747-68133&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 21:13 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 `get_design_context` + `get_variable_defs` on `31147:480`",
      "implementedMapping": "linked into `PortalTopNav`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Inline Copilot / Inline Copilot - open start / [29192:8232](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29192-8232&m=dev)",
      "extractionStatus": "implemented-rendered",
      "extractionDate": "2026-07-08 20:23 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 `get_design_context` + `get_variable_defs` on `29192:8232`",
      "implementedMapping": "`InlineCopilot`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Inline Copilot / Inline Copilot - guided start / [29192:8293](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29192-8293&m=dev)",
      "extractionStatus": "implemented-rendered",
      "extractionDate": "2026-07-08 20:23 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 `get_design_context` + `get_variable_defs` on `29192:8293`",
      "implementedMapping": "`InlineCopilot`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Inline Copilot / .Flair / [29389:12096](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29389-12096&m=dev)",
      "extractionStatus": "implemented-rendered",
      "extractionDate": "2026-07-08 20:23 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 `get_design_context` + `get_variable_defs` on `29389:12096`",
      "implementedMapping": "`InlineCopilot`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Inline Copilot / .prompt input / [29192:8358](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29192-8358&m=dev)",
      "extractionStatus": "implemented-rendered",
      "extractionDate": "2026-07-08 20:23 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 `get_design_context` + `get_variable_defs` on `29192:8358`",
      "implementedMapping": "`InlineCopilot`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Inline Copilot / .Inline Copilot title / [29192:8429](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29192-8429&m=dev)",
      "extractionStatus": "implemented-rendered",
      "extractionDate": "2026-07-08 20:23 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 `get_design_context` + `get_variable_defs` on `29192:8429`",
      "implementedMapping": "`InlineCopilot`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Top actions card / Top action / [30046:9398](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=30046-9398&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 21:13 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 `get_design_context` + `get_variable_defs` on `41544:8562`",
      "implementedMapping": "linked into `ServiceMenu`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Top actions card / .Quick Actions / [30289:2845](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=30289-2845&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 21:13 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 `get_design_context` + `get_variable_defs` on `41795:20148`",
      "implementedMapping": "linked into `PortalTopNav`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Accordion / Accordion / [30028:627](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=30028-627&m=dev)",
      "extractionStatus": "implemented-rendered",
      "extractionDate": "2026-07-08 (time not recorded)",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 exact inventory node in row \u00b7 `get_design_context` + `get_variable_defs`",
      "implementedMapping": "`AzureAccordion` (implemented-rendered locally)",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Code snippet / .Code line / [38108:33491](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=38108-33491&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 21:00 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `38108:33491`",
      "implementedMapping": "linked into `CodeSnippet`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Code snippet / .Number / [38108:33570](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=38108-33570&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 21:00 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `38108:33570`",
      "implementedMapping": "linked into `CodeSnippet`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Code snippet / .Code level(s) / [38108:33579](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=38108-33579&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 21:00 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `38108:33579`",
      "implementedMapping": "linked into `CodeSnippet`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Code snippet / .JSON Collapse / [38113:34678](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=38113-34678&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 21:00 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `38113:34678`",
      "implementedMapping": "linked into `CodeSnippet`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Code snippet / Code snippet / [38116:47202](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=38116-47202&m=dev)",
      "extractionStatus": "implemented-rendered",
      "extractionDate": "2026-07-08 (time not recorded)",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 exact inventory node in row \u00b7 `get_design_context` + `get_variable_defs`",
      "implementedMapping": "`CodeSnippet` (implemented-rendered locally)",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Copy button / Copy Button / [25260:8600](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=25260-8600&m=dev)",
      "extractionStatus": "implemented-rendered",
      "extractionDate": "2026-07-08 (time not recorded)",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 exact inventory node in row \u00b7 `get_design_context` + `get_variable_defs`",
      "implementedMapping": "`CopyButton` (implemented-rendered locally)",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Data grid / .F2-Grid cell / Text / [28093:48265](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-48265&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 21:00 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `28093:48265`",
      "implementedMapping": "linked into `AzureDataGrid`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Data grid / .Grid cell / Checkbox / [28093:48439](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-48439&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 21:00 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `28093:48439`",
      "implementedMapping": "linked into `AzureDataGrid`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Data grid / .Grid cell / Editable field / [28093:48441](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-48441&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 21:13 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 `get_design_context` + `get_variable_defs` on `41362:31531`",
      "implementedMapping": "linked into `PortalTopNav`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Data grid / .Grid cell / Empty / [28093:48448](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-48448&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 21:13 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 `get_design_context` + `get_variable_defs` on `41483:18353`",
      "implementedMapping": "linked into `PortalTopNav`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Data grid / .Grid cell /  Group / [28093:48449](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-48449&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 21:13 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 `get_design_context` + `get_variable_defs` on `35292:9094`",
      "implementedMapping": "linked into `PortalTopNav`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Data grid / .Grid cell/Tags / [28752:53376](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28752-53376&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "linked into `AzureDataGrid`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Data grid / .Grid cell/ Icons / [28817:36284](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28817-36284&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Data grid / .Column header / Checkbox / [28093:48459](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-48459&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Data grid / .Column header / Empty / [28093:48461](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-48461&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Data grid / .Column header / Label / [28093:48462](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-48462&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 21:00 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `28093:48462`",
      "implementedMapping": "linked into `AzureDataGrid`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Data grid / .Column header /  Group / [28093:48474](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-48474&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Data grid / .Grid column / Data grid / [28093:48484](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-48484&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Data grid / .Grid column / Editable / [28093:49265](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-49265&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Data grid / .\u21aa\ufe0f Hierarchy level / [28093:49423](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-49423&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Data grid / .Grid row / Empty / [28093:49440](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-49440&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Data grid / .\u21aa\ufe0f Grouped row chevron / [28093:49447](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-49447&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Data grid / .Grid row / Group / [28093:49456](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-49456&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Data grid / Azure F2-Data Grid / [28093:32728](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28093-32728&m=dev)",
      "extractionStatus": "implemented-rendered",
      "extractionDate": "2026-07-08 21:00 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · sparse `get_design_context` on `28093:32728` plus sublayer extraction on `28093:32729` / `28784:55430` and exact child extraction on `28093:48265`, `28093:48439`, `28093:48462`",
      "implementedMapping": "`AzureDataGrid`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Empty state / Empty state / [29232:42433](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29232-42433&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "linked into `NotificationPane`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Essentials / Essentials / [25412:8797](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=25412-8797&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Feedback link / Feedback / [35182:761](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=35182-761&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "File upload / Upload File / [25412:31783](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=25412-31783&m=dev)",
      "extractionStatus": "implemented-rendered",
      "extractionDate": "2026-07-09 00:50 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 `get_design_context` + `get_variable_defs` on `25412:31783`",
      "implementedMapping": "`FileUpload`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Filterable combo box / Filterable combo box / [25248:8173](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=25248-8173&m=dev)",
      "extractionStatus": "implemented-rendered",
      "extractionDate": "2026-07-09 00:50 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 `get_design_context` + `get_variable_defs` on `25248:8173`",
      "implementedMapping": "`FilterableComboBox`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Filter pill / .Popover (filter pill menus) / [27774:7950](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27774-7950&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Filter pill \u2013 subscription / Filter Pill Dropdown / [25378:3066](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=25378-3066&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "linked into `CommandBar`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Message bar / Message bar upsell / [28644:76791](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28644-76791&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Pager / .Tab Number / [27113:1660](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27113-1660&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 21:00 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `27113:1660`",
      "implementedMapping": "linked into `Pager`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Pager / .Pagination Counter / [27119:1897](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27119-1897&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 21:00 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `27119:1897`",
      "implementedMapping": "linked into `Pager`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Pager / .Num Dropdown / [27119:15792](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27119-15792&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 21:00 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `27119:15792`",
      "implementedMapping": "linked into `Pager`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Pager / Pager / [27119:16070](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27119-16070&m=dev)",
      "extractionStatus": "implemented-rendered",
      "extractionDate": "2026-07-08 21:00 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` · `get_design_context` + `get_variable_defs` on `27119:16070`",
      "implementedMapping": "`Pager`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Popover / .Popover Content (Brand) / [27965:13714](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27965-13714&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Popover / .Popover Content (Light) / [28035:15352](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28035-15352&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Popover / .Popover Content (Dark) / [28035:15353](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28035-15353&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "linked into `TabsWithContent`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Progress bar / Progress Bar with labels / [28174:7417](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28174-7417&m=dev)",
      "extractionStatus": "implemented-rendered",
      "extractionDate": "2026-07-09 00:50 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 `get_design_context` + `get_variable_defs` on `28174:7417`",
      "implementedMapping": "`ProgressBarWithLabel`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Progress bar / Animated Progress Bar with labels / [28209:4560](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28209-4560&m=dev)",
      "extractionStatus": "implemented-rendered",
      "extractionDate": "2026-07-09 00:50 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 `get_design_context` + `get_variable_defs` on `28209:4560`",
      "implementedMapping": "`ProgressBarWithLabel` (indeterminate)",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Scrollbar / Scrollbar / [27777:16820](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27777-16820&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Shell for Azure Portal / Footer / [35285:10476](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=35285-10476&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "linked into `PortalLayout`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Shell for Azure Portal / .Menu header / [32610:9876](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32610-9876&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Shell for Azure Portal / .Menu group / [32610:9930](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32610-9930&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "linked into `ServiceMenu`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Shell for Azure Portal / .search button / [32610:9923](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32610-9923&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Shell for Azure Portal / .Menu search / [32610:9942](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32610-9942&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "linked into `ServiceMenu`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Shell for Azure Portal / Service Menu / [32610:9824](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32610-9824&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "linked into `ServiceMenu`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Shell for Azure Portal / .Menu item / [32610:9731](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32610-9731&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "linked into `ServiceMenu`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Shell for Azure Portal / .L1 Menu item / [35357:9421](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=35357-9421&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Shell for Azure Portal / .L1 Menu Element / [35360:9984](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=35360-9984&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Shell for Azure Portal / L1 - Portal Menu / [35399:10636](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=35399-10636&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Shell for Azure Portal / .L1 Mobile Nav / [35431:15337](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=35431-15337&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Shell for Azure Portal / .AI button / [31147:480](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=31147-480&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Shell for Azure Portal / Footer / [41011:13461](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=41011-13461&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "linked into `PortalLayout`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Shell for Azure Portal / Service Menu item / [41544:8562](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=41544-8562&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Shell for Azure Portal / Search Filter Pills / [41795:20148](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=41795-20148&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Shell for Azure Portal / .Azure Mobile Search / [41362:31531](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=41362-31531&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Shell for Azure Portal / .Mobile Search Menu / [41483:18353](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=41483-18353&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Shell for Azure Portal / .Azure Global Search / [40971:40679](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=40971-40679&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "linked into `PortalTopNav`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Shell for Azure Portal / .Search Menu / [40971:35680](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=40971-35680&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Shell for Azure Portal / Site Header / [31147:439](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=31147-439&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "linked into `PortalTopNav`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Shell for Azure Portal / Blade header / [32630:8970](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=32630-8970&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "linked into `BladeHeader`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Shell for Azure Portal / .Header Icons / [35292:9094](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=35292-9094&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Slider / Slider with numbers / [28472:10338](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=28472-10338&m=dev)",
      "extractionStatus": "implemented-rendered",
      "extractionDate": "2026-07-09 00:50 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 `get_design_context` + `get_variable_defs` on `28472:10338`",
      "implementedMapping": "`AzureSlider`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Tablist / Azure Horizontal Tab / [29167:8324](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29167-8324&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "2026-07-08 21:23 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 `get_design_context` + `get_variable_defs` on `29167:8324`",
      "implementedMapping": "linked into `AzureTabList`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Tablist / Azure Vertical Tab / [29195:7388](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29195-7388&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "linked into `AzureTabList`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Tablist / Azure Horizontal TabList / [29553:14761](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29553-14761&m=dev)",
      "extractionStatus": "implemented-rendered",
      "extractionDate": "2026-07-08 21:23 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 `get_design_context` + `get_variable_defs` on `29553:14761`",
      "implementedMapping": "`AzureTabList`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Tablist / Azure Vertical TabList / [29553:14688](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29553-14688&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "linked into `AzureTabList`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Tablist / .Horizontal Swap / [29185:10188](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29185-10188&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "linked into `AzureTabList`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Tablist / .Vertical Swap / [29195:8155](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29195-8155&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "linked into `TabsWithContent`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Tags by resource / .Row / [29804:6859](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29804-6859&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Tags by resource / Tags by Resource / [36787:12056](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=36787-12056&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "linked into `ResourceTagEditor`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Toolbar for Azure / Toolbar (Azure) / [29553:7576](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=29553-7576&m=dev)",
      "extractionStatus": "implemented-rendered",
      "extractionDate": "2026-07-09 00:50 PDT",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 `get_design_context` + `get_variable_defs` on `29553:7576`",
      "implementedMapping": "`AzureToolbar`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Form / Form / [27181:1280](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27181-1280&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "linked into `FormSection`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Form / .Input row / [27293:520](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27293-520&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "linked into `FormFieldRow`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Form / .hierarchical indicator / [27175:3382](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27175-3382&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Form / .Label / [27878:1838](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27878-1838&m=dev)",
      "extractionStatus": "showcase-placeholder",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "linked into `FormFieldRow`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Form / .Asterix / [30330:1179](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=30330-1179&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Local components / .Azure UI Kit Header (local) / [25365:18143](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=25365-18143&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Local components / .Design System / [27218:35323](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27218-35323&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Local components / .Design System links / [38075:124063](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=38075-124063&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Local components / .Code status / [27218:35373](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27218-35373&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Local components / .Progress / [27218:35412](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=27218-35412&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Local components / .component row (local) / [35718:12181](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=35718-12181&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Local components / .Latest Component / [38080:124096](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=38080-124096&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "Local components / .Design System Update Notice / [38080:124134](https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=38080-124134&m=dev)",
      "extractionStatus": "needs-mcp-extraction",
      "extractionDate": "Not extracted",
      "extractedFrom": "Figma `q2TdO4dVcMhNWYp0N6Bc05` \u00b7 dev-mode citation in row",
      "implementedMapping": "Needs mapping",
      "showcase": "Yes"
    }
  ]
};

export const patternCatalogData: PatternCatalogData = {
  "catalogKind": "patterns",
  "sourceFile": {
    "name": "Azure Pattern Templates / Fluent 2",
    "fileKey": "TXALL9CS0727dvGcZo84Bg"
  },
  "rules": {
    "pageNodesAreIndexesNotRenderTargets": true,
    "preferTextExtractionOverScreenshots": true,
    "convertDevModeOutputToFluentReact": true,
    "libraryRoot": "apps/web/src/azure-fluent-system",
    "preferLocalCatalogForConsumption": true,
    "figmaRefreshIsOptional": true
  },
  "sharedTokenAnchors": {
    "pageCanvas": "#ffffff",
    "brandBlueHeader": "#0f6cbd",
    "primaryActionBlue": "#0078d4",
    "fontFamily": "Segoe UI",
    "body": "14/20",
    "bodyStrong": "14/20 semibold",
    "subtitle": "16/22 semibold",
    "title": "24/32 semibold",
    "borderRadius": "4px",
    "inputControlHeight": "24px",
    "neutralStroke": "#d1d1d1",
    "commandBorder": "#cccccc",
    "shadow": "Drop Shadow - Level 2"
  },
  "portability": {
    "downstreamConsumptionDoesNotRequireFigmaMcp": true,
    "devModeUrlsAreTraceabilityCitations": true,
    "localArtifactsAreAuthoritativeForOrdinaryUsage": true
  },
  "localConsumptionWorkflow": [
    "Inspect showcase/README.md, catalog/PATTERNS.md, and this catalog first.",
    "Use family.localExamples and family.implementationFiles to find checked-in React, CSS, and example files.",
    "Compose the target flow from apps/web/src/azure-fluent-system primitives and patterns without assuming Figma MCP exists.",
    "Only use Figma dev-mode URLs or MCP if they are available and you are refreshing or extending the reference pack."
  ],
  "families": [
    {
      "id": "create-stepped-form-blade",
      "name": "Create / stepped form blade",
      "status": "rich-context",
      "pageNodeId": "3203:24770",
      "pageNodeUrl": "https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=3203-24770&m=dev",
      "representativeNodes": [
        {
          "nodeId": "3203:24770",
          "name": "Isolated / First step",
          "url": "https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=3203-24770&m=dev",
          "sourceType": "design-context+variable-defs"
        },
        {
          "nodeId": "6747:133457",
          "name": "Blade header",
          "url": "https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=6747-133457&m=dev",
          "sourceType": "child-anchor"
        },
        {
          "nodeId": "3203:24781",
          "name": "Footer bar",
          "url": "https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=3203-24781&m=dev",
          "sourceType": "child-anchor"
        }
      ],
      "libraryMappings": [
        "BladeHeader",
        "CreateResourcePattern",
        "FormFooter",
        "AzureTabList"
      ],
      "antiRules": [
        "Do not convert the blade into a modal wizard.",
        "Do not move footer actions above the form body.",
        "Do not replace the selected tab rail with chips or cards."
      ],
      "localExamples": [
        "examples/create-resource-pattern.example.tsx",
        "examples/form-wizard.example.tsx"
      ],
      "implementationFiles": [
        "patterns.tsx",
        "components.tsx",
        "tokens.css",
        "showcase/AzureFluentShowcaseApp.tsx"
      ]
    },
    {
      "id": "browse-resource",
      "name": "Browse Resource",
      "status": "page-index-only",
      "pageNodeId": "4417:3962",
      "pageNodeUrl": "https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=4417-3962&m=dev",
      "representativeNodes": [
        {
          "nodeId": "4570:40874",
          "name": "Location summary",
          "url": "https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=4570-40874&m=dev",
          "sourceType": "component-inventory"
        }
      ],
      "libraryMappings": [
        "BrowseResourcePattern",
        "DataToolbar",
        "FilterBar",
        "AzureDataGrid",
        "Pager"
      ],
      "antiRules": [
        "Do not render page node 4417:3962 directly.",
        "Do not replace browse/filter/grid structure with a marketing card gallery."
      ],
      "localExamples": [
        "examples/browse-resource-pattern.example.tsx",
        "examples/azure-data-grid-filtering.example.tsx"
      ],
      "implementationFiles": [
        "patterns.tsx",
        "components.tsx",
        "tokens.css"
      ]
    },
    {
      "id": "notifications",
      "name": "Notifications",
      "status": "component-inventory",
      "pageNodeId": "5707:60107",
      "pageNodeUrl": "https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5707-60107&m=dev",
      "representativeNodes": [
        {
          "nodeId": "5760:12271",
          "name": ".Notification pane body f2",
          "url": "https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5760-12271&m=dev",
          "sourceType": "component-inventory"
        },
        {
          "nodeId": "5760:12325",
          "name": ".Context pane - grid + empty state",
          "url": "https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5760-12325&m=dev",
          "sourceType": "component-inventory"
        }
      ],
      "libraryMappings": [
        "NotificationPattern",
        "AzureDataGrid"
      ],
      "antiRules": [
        "Do not modalize the notification pane.",
        "Do not add decorative blur or toast-wall chrome."
      ],
      "localExamples": [
        "examples/service-overview-feedback.example.tsx"
      ],
      "implementationFiles": [
        "patterns.tsx",
        "components.tsx",
        "showcase/AzureFluentShowcaseApp.tsx"
      ]
    },
    {
      "id": "delete-resource",
      "name": "Delete A Resource",
      "status": "component-inventory",
      "pageNodeId": "5649:6163",
      "pageNodeUrl": "https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5649-6163&m=dev",
      "representativeNodes": [
        {
          "nodeId": "5706:113870",
          "name": "Delete footer",
          "url": "https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5706-113870&m=dev",
          "sourceType": "component-inventory"
        },
        {
          "nodeId": "5706:110040",
          "name": "Dependent delete content",
          "url": "https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5706-110040&m=dev",
          "sourceType": "component-inventory"
        },
        {
          "nodeId": "5747:42979",
          "name": "Delete Dialog",
          "url": "https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5747-42979&m=dev",
          "sourceType": "component-inventory"
        }
      ],
      "libraryMappings": [
        "DeleteResourceDialog",
        "AzureDataGrid",
        "FormFooter"
      ],
      "antiRules": [
        "Do not flood the whole surface with danger styling.",
        "Do not enable destructive primary actions before confirmation requirements are met."
      ],
      "localExamples": [
        "examples/service-overview-feedback.example.tsx"
      ],
      "implementationFiles": [
        "patterns.tsx",
        "components.tsx",
        "tokens.css",
        "showcase/AzureFluentShowcaseApp.tsx"
      ]
    },
    {
      "id": "manage-resource",
      "name": "Manage A Resource",
      "status": "component-inventory",
      "pageNodeId": "6331:13976",
      "pageNodeUrl": "https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=6331-13976&m=dev",
      "representativeNodes": [
        {
          "nodeId": "6432:43439",
          "name": ".public network access",
          "url": "https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=6432-43439&m=dev",
          "sourceType": "component-inventory"
        },
        {
          "nodeId": "6710:173923",
          "name": "Accordion Header Content",
          "url": "https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=6710-173923&m=dev",
          "sourceType": "component-inventory"
        },
        {
          "nodeId": "6710:115802",
          "name": "Accordion Content",
          "url": "https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=6710-115802&m=dev",
          "sourceType": "component-inventory"
        }
      ],
      "libraryMappings": [
        "ManageResourcePattern",
        "ServiceMenu",
        "AzureDataGrid"
      ],
      "antiRules": [
        "Do not flatten manage flows into stacked marketing cards.",
        "Do not turn routine maintenance tasks into a wizard unless the reference shows a wizard."
      ],
      "localExamples": [
        "showcase/AzureFluentShowcaseApp.tsx"
      ],
      "implementationFiles": [
        "patterns.tsx",
        "components.tsx",
        "tokens.css"
      ]
    },
    {
      "id": "service-overview",
      "name": "Service overview",
      "status": "component-inventory",
      "pageNodeId": "4625:1737",
      "pageNodeUrl": "https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=4625-1737&m=dev",
      "representativeNodes": [
        {
          "nodeId": "5163:12001",
          "name": "Overview Card",
          "url": "https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5163-12001&m=dev",
          "sourceType": "component-inventory"
        },
        {
          "nodeId": "8195:9103",
          "name": "Footer_Overview card",
          "url": "https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=8195-9103&m=dev",
          "sourceType": "component-inventory"
        }
      ],
      "libraryMappings": [
        "ServiceOverviewPattern"
      ],
      "antiRules": [
        "Do not import the generic SaaS hero-metric template.",
        "Do not add gradient hero cards or decorative KPI counters."
      ],
      "localExamples": [
        "examples/service-overview-feedback.example.tsx"
      ],
      "implementationFiles": [
        "patterns.tsx",
        "components.tsx",
        "tokens.css"
      ]
    },
    {
      "id": "feedback-ces-cva",
      "name": "Feedback / CES / CVA",
      "status": "component-inventory",
      "pageNodeId": "4493:21",
      "pageNodeUrl": "https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=4493-21&m=dev",
      "representativeNodes": [
        {
          "nodeId": "5080:12885",
          "name": ".Next steps content",
          "url": "https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5080-12885&m=dev",
          "sourceType": "component-inventory"
        },
        {
          "nodeId": "5080:12891",
          "name": ".Feedback footer",
          "url": "https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5080-12891&m=dev",
          "sourceType": "component-inventory"
        },
        {
          "nodeId": "5080:12902",
          "name": ".Feedback content",
          "url": "https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5080-12902&m=dev",
          "sourceType": "component-inventory"
        }
      ],
      "libraryMappings": [
        "FormFooter",
        "NotificationPattern"
      ],
      "antiRules": [
        "Do not build a decorative survey microsite.",
        "Do not bury the primary action under extra card chrome or oversized illustration."
      ],
      "localExamples": [
        "examples/service-overview-feedback.example.tsx"
      ],
      "implementationFiles": [
        "components.tsx",
        "patterns.tsx",
        "tokens.css"
      ]
    },
    {
      "id": "pattern-index",
      "name": "Table of contents / pattern index",
      "status": "component-inventory",
      "pageNodeId": "1024:66",
      "pageNodeUrl": "https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=1024-66&m=dev",
      "representativeNodes": [
        {
          "nodeId": "7947:112498",
          "name": ".Table of Contents - Patterns",
          "url": "https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=7947-112498&m=dev",
          "sourceType": "component-inventory"
        }
      ],
      "libraryMappings": [
        "showcase navigation"
      ],
      "antiRules": [
        "Do not ship the index surface as if it were an end-user workflow.",
        "Do not replace pattern reference with screenshot mosaics."
      ],
      "localExamples": [
        "showcase/AzureFluentShowcaseApp.tsx",
        "showcase/README.md"
      ],
      "implementationFiles": [
        "showcase/AzureFluentShowcaseApp.tsx",
        "showcase/showcase.css"
      ]
    }
  ],
  "summary": {
    "patternFamilyCount": 8,
    "uniqueTrackedDevModeNodes": 25,
    "statusCounts": {
      "rich-context": 1,
      "page-index-only": 1,
      "component-inventory": 6
    }
  },
  "mappingRows": [
    {
      "figmaNodeReference": "**Create / stepped form blade**<br>Page / [3203:24770](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=3203-24770&m=dev)<br>Representative nodes / [3203:24770](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=3203-24770&m=dev), [6747:133457](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=6747-133457&m=dev), [3203:24781](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=3203-24781&m=dev)",
      "extractionStatus": "rich-context",
      "extractionDate": "Unknown",
      "extractedFrom": "Figma `TXALL9CS0727dvGcZo84Bg` \u00b7 `get_design_context` + `get_variable_defs` on `3203:24770`",
      "implementedMapping": "`BladeHeader`<br>`CreateResourcePattern`<br>`FormFooter`<br>`AzureTabList`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "**Browse Resource**<br>Page / [4417:3962](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=4417-3962&m=dev)<br>Representative nodes / [4570:40874](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=4570-40874&m=dev)",
      "extractionStatus": "page-index-only",
      "extractionDate": "Unknown",
      "extractedFrom": "Figma `TXALL9CS0727dvGcZo84Bg` \u00b7 page/index + representative dev-mode citations in row",
      "implementedMapping": "`BrowseResourcePattern`<br>`DataToolbar`<br>`FilterBar`<br>`AzureDataGrid`<br>`Pager`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "**Notifications**<br>Page / [5707:60107](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5707-60107&m=dev)<br>Representative nodes / [5760:12271](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5760-12271&m=dev), [5760:12325](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5760-12325&m=dev)",
      "extractionStatus": "component-inventory",
      "extractionDate": "Unknown",
      "extractedFrom": "Figma `TXALL9CS0727dvGcZo84Bg` \u00b7 page/index + representative dev-mode citations in row",
      "implementedMapping": "`NotificationPattern`<br>`AzureDataGrid`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "**Delete A Resource**<br>Page / [5649:6163](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5649-6163&m=dev)<br>Representative nodes / [5706:113870](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5706-113870&m=dev), [5706:110040](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5706-110040&m=dev), [5747:42979](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5747-42979&m=dev)",
      "extractionStatus": "component-inventory",
      "extractionDate": "Unknown",
      "extractedFrom": "Figma `TXALL9CS0727dvGcZo84Bg` \u00b7 page/index + representative dev-mode citations in row",
      "implementedMapping": "`DeleteResourceDialog`<br>`AzureDataGrid`<br>`FormFooter`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "**Manage A Resource**<br>Page / [6331:13976](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=6331-13976&m=dev)<br>Representative nodes / [6432:43439](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=6432-43439&m=dev), [6710:173923](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=6710-173923&m=dev), [6710:115802](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=6710-115802&m=dev)",
      "extractionStatus": "component-inventory",
      "extractionDate": "Unknown",
      "extractedFrom": "Figma `TXALL9CS0727dvGcZo84Bg` \u00b7 page/index + representative dev-mode citations in row",
      "implementedMapping": "`ManageResourcePattern`<br>`ServiceMenu`<br>`AzureDataGrid`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "**Service overview**<br>Page / [4625:1737](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=4625-1737&m=dev)<br>Representative nodes / [5163:12001](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5163-12001&m=dev), [8195:9103](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=8195-9103&m=dev)",
      "extractionStatus": "component-inventory",
      "extractionDate": "Unknown",
      "extractedFrom": "Figma `TXALL9CS0727dvGcZo84Bg` \u00b7 page/index + representative dev-mode citations in row",
      "implementedMapping": "`ServiceOverviewPattern`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "**Feedback / CES / CVA**<br>Page / [4493:21](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=4493-21&m=dev)<br>Representative nodes / [5080:12885](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5080-12885&m=dev), [5080:12891](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5080-12891&m=dev), [5080:12902](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=5080-12902&m=dev)",
      "extractionStatus": "component-inventory",
      "extractionDate": "Unknown",
      "extractedFrom": "Figma `TXALL9CS0727dvGcZo84Bg` \u00b7 page/index + representative dev-mode citations in row",
      "implementedMapping": "`FormFooter`<br>`NotificationPattern`",
      "showcase": "Yes"
    },
    {
      "figmaNodeReference": "**Table of contents / pattern index**<br>Page / [1024:66](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=1024-66&m=dev)<br>Representative nodes / [7947:112498](https://www.figma.com/design/TXALL9CS0727dvGcZo84Bg/Azure-Pattern-Templates---Fluent-2?node-id=7947-112498&m=dev)",
      "extractionStatus": "component-inventory",
      "extractionDate": "Unknown",
      "extractedFrom": "Figma `TXALL9CS0727dvGcZo84Bg` \u00b7 page/index + representative dev-mode citations in row",
      "implementedMapping": "Pattern doctrine only (`showcase navigation`)",
      "showcase": "Yes"
    }
  ]
};
