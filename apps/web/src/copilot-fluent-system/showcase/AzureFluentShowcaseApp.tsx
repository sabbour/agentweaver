import './showcase.css';
import virtualMachineIcon from '../assets/icons/azure/assets/compute--virtual-machine--7a04018565e2.svg';
import storageAccountsIcon from '../assets/icons/azure/assets/storage--storage-accounts--c993bb3f7b83.svg';
import azureIconManifest from '../assets/icons/azure/azure-icons-manifest.json';
import iconSummary from '../assets/icons/azure/iconcloud-azure-icons-full-summary.json';
import {
  Accordion,
  AccordionHeader,
  AccordionItem,
  AccordionPanel,
  AgenticApprovalPattern,
  AgenticProgress,
  ArtifactPill,
  Avatar,
  AzureAccordion,
  AzureDataGrid,
  AzureEmptyState,
  AzurePropertyList,
  AzureSummaryCard,
  AzureFluentProvider,
  AzureIcon,
  AzureStepList,
  AzureTabList,
  AzureToolbar,
  Badge,
  BladeHeader,
  Breadcrumb,
  BreadcrumbButton,
  BreadcrumbDivider,
  BreadcrumbItem,
  BrowseResourcePattern,
  Button,
  Card,
  CardHeader,
  ChainOfThought,
  Caption1,
  Checkbox,
  CodeSnippet,
  ColorSwatch,
  Combobox,
  CoordinatorRunPattern,
  CopilotComposer,
  CopilotPromptRibbon,
  CopilotResponse,
  CopilotTriagePanelPattern,
  CopyButton,
  CounterBadge,
  CreateResourcePattern,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DialogTrigger,
  Divider,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  Dropdown,
  EssentialsGrid,
  Field,
  FilteringPattern,
  FilterPills,
  FormBladePattern,
  FormFieldRow,
  InfoLabel,
  InlineCopilot,
  InlineDrawer,
  Input,
  InteractionTag,
  Label,
  Link,
  List,
  ListItem,
  ManageResourcePattern,
  Menu,
  MenuDivider,
  MenuGroup,
  MenuGroupHeader,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  NavDrawer,
  NavItem,
  NotificationPane,
  Option,
  Pager,
  Persona,
  Popover,
  PopoverSurface,
  PopoverTrigger,
  PortalLayout,
  PortalRail,
  PortalTopNav,
  PresenceBadge,
  ProgressBarWithLabel,
  Radio,
  RadioGroup,
  Rating,
  RatingDisplay,
  ResourceOperationHeaderPattern,
  ResourceTagEditor,
  SearchBox,
  Select,
  ServiceMenu,
  ServiceOverviewPattern,
  Skeleton,
  SkeletonItem,
  SpinButton,
  Spinner,
  StatusIconText,
  StepWizardPattern,
  SwatchPicker,
  Switch,
  Tab,
  Table,
  TableBody,
  TableCell,
  TableCellLayout,
  TableHeader,
  TableHeaderCell,
  TableRow,
  TabList,
  Tag,
  TagGroup,
  TeachingPopover,
  Text,
  Textarea,
  Title2,
  Title3,
  Toast,
  ToastBody,
  ToastTitle,
  Toolbar,
  ToolbarButton,
  ToolbarDivider,
  Tooltip,
  Tree,
  TreeItem,
} from '..';
import {
  AzureForm,
  AzureSlider,
  CalloutPopover,
  CommandBar,
  DataToolbar,
  DeleteConfirmationDialog,
  DeleteResourceDialog,
  FeedbackFooter,
  FileUpload,
  FilterableComboBox,
  FilterBar,
  FormFooter,
  HelpPopover,
  IconActionButton,
  PortalCommandBar,
} from '../components';
import { AzureIconProvider } from '../icons';
import { ErrorPattern, NotificationPattern } from '../patterns';
import { componentCatalogData, patternCatalogData } from './catalogData';
import {
  AvatarGroup,
  AvatarGroupItem,
  InteractionTagPrimary,
  NavDrawerBody,
  TeachingPopoverBody,
  TeachingPopoverHeader,
  TeachingPopoverSurface,
  TeachingPopoverTitle,
  TeachingPopoverTrigger,
  TreeItemLayout,
} from '@fluentui/react-components';
import {
  AddRegular,
  AppsListRegular,
  ArrowClockwiseRegular,
  CalendarLtrRegular,
  CheckmarkRegular,
  DataTrendingRegular,
  DeleteRegular,
  DismissRegular,
  EditRegular,
  HomeRegular,
  InfoRegular,
  MoreHorizontalRegular,
  NavigationRegular,
  PersonCircleRegular,
  SearchRegular,
  SettingsRegular,
  ShieldTaskRegular,
  SparkleRegular,
} from '@fluentui/react-icons';
import { useMemo, useState } from 'react';
import type { ReactElement, ReactNode } from 'react';
/* eslint-disable react-refresh/only-export-components */












type ShowcaseView = 'components' | 'patterns' | 'icons';

interface ComponentCatalogGroup {
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

interface CatalogRow {
  figmaNodeReference: string;
  extractionStatus: string;
  extractionDate: string;
  extractedFrom: string;
  implementedMapping: string;
  showcase: 'Yes' | 'No' | string;
}

interface ComponentInventoryManifest {
  sourceFileKey: string;
  inventoryCoverage?: {
    inventoryComponentCount: number;
    exactManifestNameNodeAudit?: {
      coveredCount: number;
      missingCount: number;
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

interface PatternFamily {
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

interface PatternInventoryManifest {
  sourceFile: {
    name: string;
    fileKey: string;
  };
  portability?: {
    downstreamConsumptionDoesNotRequireFigmaMcp: boolean;
    devModeUrlsAreTraceabilityCitations: boolean;
    localArtifactsAreAuthoritativeForOrdinaryUsage: boolean;
  };
  localConsumptionWorkflow?: string[];
  families: PatternFamily[];
}

interface ComponentPreviewEntry {
  exportName: string;
  title: string;
  category: string;
  summary: string;
  usageNotes: string[];
  preview: () => ReactElement;
}

interface InventorySourceNode {
  nodeId: string;
  name: string;
}

interface ComponentInventoryBrowserEntry {
  nodeId: string;
  title: string;
  pageName: string;
  type: string;
  nodeUrl?: string;
  coverageStatus: string;
  statusLabel: string;
  exportNames: string[];
  exportLabel: string;
  summary: string;
  nextAction: string;
  extractionDate: string;
  extractionStatus: string;
  showcaseStatus: string;
  figmaNodeReference: string;
  previewEntry?: ComponentPreviewEntry;
  sourceNodes: InventorySourceNode[];
}

interface PatternPreviewEntry {
  familyId: string;
  summary: string;
  anatomy: string[];
  preview: () => ReactElement;
}

interface ResourceRecord {
  id: string;
  name: string;
  owner: string;
  location: string;
  status: 'Healthy' | 'Needs attention' | 'Updating';
}

interface AksResourceRecord {
  id: string;
  name: string;
  resourceGroup: string;
  subscription: string;
  type: string;
  status: 'Running' | 'Warning' | 'Updating';
}

const componentInventory = componentCatalogData as ComponentInventoryManifest;
const patternGuide = patternCatalogData as PatternInventoryManifest;

const showcaseTabs = [
  { id: 'components', label: 'Components', description: 'Preview reusable building blocks' },
  { id: 'patterns', label: 'Patterns', description: 'Browse composed product flows' },
  { id: 'icons', label: 'Icons', description: 'Browse local icon assets' },
] as const;

const iconCatalogSnapshot = {
  collections: iconSummary.normalized.collections,
  uniqueSvgPayloads: iconSummary.normalized.uniqueSvgPayloads,
  duplicatePayloads: iconSummary.normalized.duplicatePayloads,
};

const showcaseIconRegistry = {
  'Compute/Virtual Machine': { src: virtualMachineIcon, alt: 'Virtual machine' },
  'Storage/Storage Accounts': { src: storageAccountsIcon, alt: 'Storage accounts' },
} as const;

const iconBrowserItems = [
  {
    name: 'Compute/Virtual Machine',
    alias: 'VirtualMachine',
    source: 'Vendored Azure SVG',
    note: 'Checked-in Azure icon asset available through the local icon registry.',
  },
  {
    name: 'Storage/Storage Accounts',
    alias: 'StorageAccounts',
    source: 'Vendored Azure SVG',
    note: 'Checked-in Azure icon asset for resource-browser and summary views.',
  },
] as const;

// Full Azure icon catalog for the Q4 icons surface. Sourced from the vendored
// IconCloud Azure manifest (assets/icons/azure/azure-icons-manifest.json — 1441
// glyphs across 27 collections). Vite rewrites every vendored SVG to a hashed
// asset URL at build time via import.meta.glob. This is a showcase dev-tool
// surface styled with Fluent 2 card/tile tokens; the glyphs are Azure product
// icons (not Figma component nodes), so there is no single Figma node to cite.
const azureIconAssetUrls = import.meta.glob('../assets/icons/azure/assets/*.svg', {
  query: '?url',
  import: 'default',
  eager: true,
}) as Record<string, string>;

const azureIconUrlByFile = new Map<string, string>();
for (const [assetPath, assetUrl] of Object.entries(azureIconAssetUrls)) {
  const fileName = assetPath.split('/').pop();
  if (fileName) azureIconUrlByFile.set(fileName, assetUrl);
}

interface AzureCatalogIcon {
  name: string;
  collection: string;
  src: string;
}

const azureCatalogIcons: AzureCatalogIcon[] = (
  (azureIconManifest as { icons: { name: string; collection: string; file: string }[] }).icons ?? []
)
  .map((icon) => ({
    name: icon.name,
    collection: icon.collection,
    src: azureIconUrlByFile.get(icon.file.split('/').pop() ?? ''),
  }))
  .filter((icon): icon is AzureCatalogIcon => Boolean(icon.src))
  .sort((a, b) => a.collection.localeCompare(b.collection) || a.name.localeCompare(b.name));

const azureCatalogCollections = [
  'All collections',
  ...Array.from(new Set(azureCatalogIcons.map((icon) => icon.collection))).sort((a, b) => a.localeCompare(b)),
];

const AZURE_ICON_GRID_CAP = 180;

const resourceRows: ResourceRecord[] = [
  { id: '1', name: 'aks-cluster-primary', owner: 'Platform operations', location: 'East US', status: 'Needs attention' },
  { id: '2', name: 'kubernetes-fleet-member', owner: 'Cluster team', location: 'West US 2', status: 'Healthy' },
  { id: '3', name: 'nodepool-system', owner: 'Platform operations', location: 'Central US', status: 'Updating' },
];

const gridColumns = [
  { columnId: 'name', header: 'Resource', sortable: true, sortValue: (item: ResourceRecord) => item.name, renderCell: (item: ResourceRecord) => item.name },
  { columnId: 'owner', header: 'Owner', sortable: true, sortValue: (item: ResourceRecord) => item.owner, renderCell: (item: ResourceRecord) => item.owner },
  { columnId: 'location', header: 'Location', sortable: true, sortValue: (item: ResourceRecord) => item.location, renderCell: (item: ResourceRecord) => item.location },
  { columnId: 'status', header: 'Status', sortable: true, sortValue: (item: ResourceRecord) => item.status, renderCell: (item: ResourceRecord) => item.status },
] as const;

const aksResourceRows: AksResourceRecord[] = [
  { id: 'aks-1', name: 'aks-cluster-alpha', resourceGroup: 'rg-sample-alpha', subscription: 'Sample subscription A', type: 'Kubernetes service', status: 'Running' },
  { id: 'aks-2', name: 'aks-cluster-beta', resourceGroup: 'rg-sample-beta', subscription: 'Sample subscription B', type: 'Kubernetes fleet member', status: 'Warning' },
  { id: 'aks-3', name: 'aks-nodepool-system', resourceGroup: 'rg-sample-alpha', subscription: 'Sample subscription A', type: 'Node pool', status: 'Updating' },
];

const aksResourceColumns = [
  { columnId: 'name', header: 'Name', sortable: true, sortValue: (item: AksResourceRecord) => item.name, renderCell: (item: AksResourceRecord) => item.name },
  { columnId: 'resourceGroup', header: 'Resource group', sortable: true, sortValue: (item: AksResourceRecord) => item.resourceGroup, renderCell: (item: AksResourceRecord) => item.resourceGroup },
  { columnId: 'subscription', header: 'Subscription', sortable: true, sortValue: (item: AksResourceRecord) => item.subscription, renderCell: (item: AksResourceRecord) => item.subscription },
  { columnId: 'type', header: 'Type', sortable: true, sortValue: (item: AksResourceRecord) => item.type, renderCell: (item: AksResourceRecord) => item.type },
  {
    columnId: 'status',
    header: 'Status',
    sortable: true,
    sortValue: (item: AksResourceRecord) => item.status,
    renderCell: (item: AksResourceRecord) => (
      <StatusIconText status={item.status === 'Running' ? 'success' : item.status === 'Warning' ? 'warning' : 'info'}>{item.status}</StatusIconText>
    ),
  },
] as const;

const serviceMenuGroups = [
  {
    id: 'overview',
    label: 'Manage',
    items: [
      { id: 'overview', label: 'Overview', icon: <HomeRegular />, favorite: true },
      {
        id: 'networking',
        label: 'Networking',
        icon: <ShieldTaskRegular />,
        badge: '4',
        items: [
          { id: 'private-access', label: 'Private access' },
          { id: 'dns-links', label: 'DNS links' },
        ],
      },
      { id: 'activity', label: 'Activity log', icon: <DataTrendingRegular /> },
      { id: 'saved-searches', label: 'Saved searches', icon: <SearchRegular />, badge: '2' },
    ],
  },
];

const accordionPreviewItems = [
  {
    id: 'overview',
    title: 'Overview',
    content: (
      <div className="azf-showcase-slot-card">
        <Text weight="semibold">Review what changed</Text>
        <Text className="azf-muted">Keep follow-up context inside the panel, not in the header row.</Text>
      </div>
    ),
  },
  {
    id: 'dependencies',
    title: 'Dependencies',
    content: (
      <div className="azf-showcase-slot-card">
        <Text weight="semibold">Connected resources</Text>
        <Text className="azf-muted">Collapsed sections stay available without forcing the whole blade to grow.</Text>
      </div>
    ),
  },
] as const;

const codeSnippetLines = [
  { lineNumber: 1, text: '{', foldState: 'expanded' as const },
  {
    lineNumber: 2,
    indentLevel: 1,
    tokens: [
      { text: '"$schema"', tone: 'key' as const },
      { text: ': ', tone: 'operator' as const },
      { text: '"https://schema.management.azure.com/schemas/2015-01-01/deploymentTemplate.json#"' },
      { text: ',', tone: 'operator' as const },
    ],
  },
  {
    lineNumber: 3,
    indentLevel: 1,
    tokens: [
      { text: '"contentVersion"', tone: 'key' as const },
      { text: ': ', tone: 'operator' as const },
      { text: '"1.0.0.0"' },
      { text: ',', tone: 'operator' as const },
    ],
  },
  {
    lineNumber: 4,
    indentLevel: 1,
    tokens: [
      { text: '"parameters"', tone: 'key' as const },
      { text: ': ', tone: 'operator' as const },
      { text: '{ ... }', tone: 'muted' as const },
      { text: ',', tone: 'operator' as const },
    ],
    foldState: 'collapsed' as const,
  },
  {
    lineNumber: 5,
    indentLevel: 1,
    tokens: [
      { text: '"variables"', tone: 'key' as const },
      { text: ': ', tone: 'operator' as const },
      { text: '{}', tone: 'muted' as const },
      { text: ',', tone: 'operator' as const },
    ],
  },
  {
    lineNumber: 6,
    indentLevel: 1,
    tokens: [
      { text: '"resources"', tone: 'key' as const },
      { text: ': ', tone: 'operator' as const },
      { text: '[', tone: 'operator' as const },
    ],
  },
  {
    lineNumber: 7,
    indentLevel: 2,
    tokens: [
      { text: '{', tone: 'operator' as const },
    ],
  },
  {
    lineNumber: 8,
    indentLevel: 3,
    tokens: [
      { text: '"type"', tone: 'key' as const },
      { text: ': ', tone: 'operator' as const },
      { text: '"Microsoft.Insights/components"' },
      { text: ',', tone: 'operator' as const },
    ],
  },
  {
    lineNumber: 9,
    indentLevel: 3,
    tokens: [
      { text: '"name"', tone: 'key' as const },
      { text: ': ', tone: 'operator' as const },
      { text: '"app-insights-sample"' },
    ],
  },
  { lineNumber: 10, indentLevel: 2, text: '}', tokens: [{ text: '}', tone: 'operator' as const }] },
  { lineNumber: 11, indentLevel: 1, text: ']', tokens: [{ text: ']', tone: 'operator' as const }] },
  { lineNumber: 12, text: '}', tokens: [{ text: '}', tone: 'operator' as const }] },
] as const;

const kustoSnippetLines = [
  { lineNumber: 1, tokens: [{ text: 'AKSControlPlane', tone: 'key' as const }] },
  {
    lineNumber: 2,
    tokens: [
      { text: '| ', tone: 'operator' as const },
      { text: 'where', tone: 'keyword' as const },
      { text: ' PreciseTimeStamp > ', tone: 'plain' as const },
      { text: 'ago', tone: 'keyword' as const },
      { text: '(2h)', tone: 'plain' as const },
    ],
  },
  {
    lineNumber: 3,
    tokens: [
      { text: '| ', tone: 'operator' as const },
      { text: 'where', tone: 'keyword' as const },
      { text: ' Region == ', tone: 'plain' as const },
      { text: '"eastus"', tone: 'string' as const },
    ],
  },
  {
    lineNumber: 4,
    tokens: [
      { text: '| ', tone: 'operator' as const },
      { text: 'summarize', tone: 'keyword' as const },
      { text: ' errors=countif(Level == ', tone: 'plain' as const },
      { text: '"Error"', tone: 'string' as const },
      { text: ') by ClusterName', tone: 'plain' as const },
    ],
  },
  {
    lineNumber: 5,
    tokens: [
      { text: '| ', tone: 'operator' as const },
      { text: 'order by', tone: 'keyword' as const },
      { text: ' errors ', tone: 'plain' as const },
      { text: 'desc', tone: 'keyword' as const },
    ],
  },
] as const;

const copilotResponseParts = [
  { id: 'user', type: 'text' as const, author: 'user' as const, content: 'Summarize the rollout failures and attach the Kusto query.' },
  {
    id: 'summary',
    type: 'text' as const,
    title: 'Copilot',
    badge: 'AI-generated content may be incorrect',
    content: (
      <div className="azf-stack azf-gap-s">
        <Text>Telemetry drift is isolated to the East US cluster and two dependent workbooks. The query below scopes the error rate for the review window.</Text>
        <CodeSnippet title="Kusto" lines={kustoSnippetLines} maxHeight={152} />
      </div>
    ),
    supportingText: '1 request left',
    footerActions: [
      { id: 'copy-summary', label: 'Copy summary', onClick: () => undefined },
      { id: 'open-workbook', label: 'Open workbook', onClick: () => undefined },
    ],
  },
  {
    id: 'confirmation',
    type: 'confirmation' as const,
    content: 'Run the remediation script against aks-cluster-sample?',
    confirmLabel: 'Run remediation',
    cancelLabel: 'Review first',
    onConfirm: () => undefined,
    onCancel: () => undefined,
  },
];

const agenticPreviewSteps = [
  {
    id: 'collect',
    title: 'Collect current rollout context',
    body: 'Gathering deployment summaries and artifact links.',
    status: 'complete' as const,
    artifacts: [{ id: 'artifact-summary', title: 'Rollout summary', type: 'Markdown', onOpen: () => undefined }],
  },
  {
    id: 'approve',
    title: 'Request operator approval',
    body: 'The next step modifies sample clusters and may increase spend.',
    needsInput: true,
    status: 'warning' as const,
    riskText: 'Approve to let the run continue, or deny to stop the workflow.',
    artifacts: [{ id: 'approval-packet', title: 'Approval packet', type: 'Artifact', onOpen: () => undefined }],
  },
];

const chainOfThoughtSteps = [
  {
    id: 'context',
    title: 'Collect current rollout context',
    body: 'Gathered deployment summaries, recent incidents, and artifact links for the review.',
    status: 'complete' as const,
    badge: { label: 'Approved by user', tone: 'success' as const },
  },
  {
    id: 'analyze',
    title: 'Analyze private endpoint policy gap',
    body: 'Correlated the failed rule with the subscription policy assignment and impacted clusters.',
    status: 'complete' as const,
  },
  {
    id: 'draft',
    title: 'Draft remediation plan',
    body: 'Preparing the ordered remediation steps and rollback notes.',
    status: 'running' as const,
  },
  {
    id: 'approve',
    title: 'Requesting approval to modify resources',
    body: "To proceed, I need your approval to access and modify resources in the 'Sample subscription A' subscription. This will let me automatically apply the necessary fixes and optimizations.",
    disclaimer: 'Denying will immediately stop reasoning, and it can’t be restarted. Continuing may incur costs.',
    approveLabel: 'Approve modifications',
    denyLabel: 'Deny modifications',
    needsInput: true,
    status: 'warning' as const,
    defaultOpen: true,
  },
];

const chainOfThoughtArtifacts = [
  { id: 'cot-summary', title: 'rollout-summary.md', type: 'markdown file', size: '4KB', onOpen: () => undefined, onDownload: () => undefined },
  { id: 'cot-policy', title: 'policy-findings.json', type: 'json file', size: '1KB', onOpen: () => undefined, onDownload: () => undefined },
  { id: 'cot-packet', title: 'approval-packet.pdf', type: 'pdf file', size: '212KB', onOpen: () => undefined, onDownload: () => undefined },
];

const copilotWorkspaceGroups = [
  {
    id: 'tasks',
    label: 'Tasks',
    items: [
      { id: 'chat', label: 'Chat' },
      { id: 'artifacts', label: 'Artifacts' },
    ],
  },
] as const;

const componentGroupsByExport = componentInventory.groups.reduce<Record<string, ComponentCatalogGroup[]>>((accumulator, group) => {
  group.libraryExports.forEach((exportName) => {
    accumulator[exportName] ??= [];
    accumulator[exportName].push(group);
  });
  return accumulator;
}, {});

function uniqueStrings(items: readonly string[]) {
  return Array.from(new Set(items));
}

function normalizeCatalogImplementedMapping(implementedMapping: string) {
  return uniqueStrings(
    implementedMapping
      .replace(/<br>/g, '\n')
      .split(/\n|,/)
      .map((part) => part.replace(/`/g, '').replace(/\s+\(implemented-rendered locally\)$/g, '').trim())
      .filter(Boolean)
      .map((part) => (part.startsWith('Related local export ') ? part.replace(/^Related local export\s+/, '').trim() : part)),
  );
}

export function getComponentShowcaseCoverageAudit(
  rows: readonly CatalogRow[],
  menuExportNames: readonly string[],
  implementedCoverageExports: readonly string[] = [],
) {
  const requiredVisibleExports = uniqueStrings([
    ...rows.flatMap((row) => (
      row.showcase === 'Yes' || row.extractionStatus === 'implemented-rendered'
        ? normalizeCatalogImplementedMapping(row.implementedMapping)
        : []
    )),
    ...implementedCoverageExports,
  ]);

  return {
    requiredVisibleExports,
    visibleRequiredExports: requiredVisibleExports.filter((exportName) => menuExportNames.includes(exportName)),
    missingRequiredExports: requiredVisibleExports.filter((exportName) => !menuExportNames.includes(exportName)),
    menuOnlyExports: menuExportNames.filter((exportName) => !requiredVisibleExports.includes(exportName)),
  };
}

function extractNodeIdFromCatalogReference(reference: string) {
  return reference.match(/\[(\d+:\d+)\]\(/)?.[1] ?? '';
}

function formatComponentCoverageStatus(status: string) {
  switch (status) {
    case 'implemented-rendered':
      return 'Rendered example';
    case 'showcase-placeholder':
      return 'Related example';
    case 'needs-mcp-extraction':
      return 'Needs review';
    case 'needs-implementation':
      return 'Planned example';
    case 'local-only-needed':
      return 'Review later';
    default:
      return status;
  }
}

function formatComponentReferenceStatus(status: string) {
  const coverageLabel = formatComponentCoverageStatus(status);
  if (coverageLabel !== status) return coverageLabel;
  if (/design-context|variable-def/i.test(status)) return 'Reviewed';
  if (/error|failed/i.test(status)) return 'Needs review';
  return status.replace(/[-_]/g, ' ');
}

function getComponentFallbackSummary(status: string, exportNames: readonly string[]) {
  switch (status) {
    case 'showcase-placeholder':
      return exportNames.length > 0
        ? 'This item is grouped under a parent component. Use the related example rather than adding a standalone card.'
        : 'This item is included for completeness but is not offered as a reusable library component.';
    case 'needs-mcp-extraction':
      return 'This item needs local design-system review before it becomes a rendered example.';
    case 'needs-implementation':
      return 'A dedicated example can be added when this component is implemented.';
    default:
      return 'This component is tracked for future browser improvements.';
  }
}

function getComponentNextAction(status: string, exportNames: readonly string[]) {
  switch (status) {
    case 'implemented-rendered':
      return 'Verify the rendered behavior in context.';
    case 'showcase-placeholder':
      return exportNames.length > 0
        ? 'Use the parent component example for this item.'
        : 'Keep this item listed as a non-reusable detail unless it becomes a library component.';
    case 'needs-mcp-extraction':
      return 'Review the design reference before adding a standalone preview.';
    case 'needs-implementation':
      return 'Add a rendered example when this component is ready.';
    case 'local-only-needed':
      return 'Keep it as a related detail unless it becomes a reusable component.';
    default:
      return 'Review this entry and choose the next browser update.';
  }
}

function formatShowcaseReferenceLabel(item: string) {
  return item
    .replace(/\s*\(\d+:\d+\)/g, '')
    .replace(/^\s*[.\u21aa]\s*/, '')
    .replace(/\bCoT\b/g, 'activity')
    .replace(/\bFooteractions\b/g, 'Footer actions')
    .replace(/\bSend_Icon\b/g, 'Send icon')
    .replace(/\bInput Footer_(LG|Sm)\b/g, 'Input footer')
    .replace(/\bNum Dropdown\b/g, 'Rows-per-page menu')
    .trim();
}

function formatDesignReferenceName(name: string) {
  return formatShowcaseReferenceLabel(name).replace(/\s+/g, ' ');
}

function PreviewCard({
  title,
  children,
  canvasClassName,
  frameClassName,
}: {
  title: string;
  children: ReactNode;
  canvasClassName?: string;
  frameClassName?: string;
}) {
  return (
    <section className="azf-showcase-preview" aria-label={title}>
      <div className={['azf-showcase-preview__frame', frameClassName].filter(Boolean).join(' ')}>
        <div className={['azf-showcase-preview__canvas', canvasClassName].filter(Boolean).join(' ')}>{children}</div>
      </div>
    </section>
  );
}

function RelatedCoverageNote({ title, items, body }: { title: string; items: string[]; body: string }) {
  return (
    <section className="azf-showcase-coverage-note" aria-label={title}>
      <div className="azf-showcase-coverage-note__header">
        <Badge appearance="tint">Related pieces</Badge>
        <Text weight="semibold">{title}</Text>
      </div>
      <Text className="azf-muted">{body}</Text>
      <ul className="azf-showcase-list azf-showcase-list--compact">
        {items.map((item) => (
          <li key={item}>{formatShowcaseReferenceLabel(item)}</li>
        ))}
      </ul>
    </section>
  );
}

function PortalShellPreview() {
  return (
    <PreviewCard title="Portal shell preview" frameClassName="azf-showcase-preview__frame--portal">
      <div className="azf-showcase-portal-demo">
        <PortalLayout
          className="azf-showcase-shell-preview"
          topNav={(
            <PortalTopNav
              brand={{ product: 'Microsoft Azure', area: 'Portal' }}
              startActions={[
                { id: 'all-services', label: 'All services', icon: <AppsListRegular /> },
                { id: 'toggle-nav', label: 'Toggle navigation', icon: <NavigationRegular /> },
              ]}
              searchValue="kubernetes"
              onSearchChange={() => undefined}
              copilotAction={{ id: 'copilot', label: 'Copilot', icon: <SparkleRegular /> }}
              endActions={[{ id: 'settings', label: 'Settings', icon: <SettingsRegular /> }]}
              persona={{ name: 'Signed-in user', secondaryText: 'Organization directory', icon: <PersonCircleRegular /> }}
            />
          )}
          rail={(
            <PortalRail
              items={[
                { id: 'home', label: 'Home', icon: <HomeRegular />, selected: true },
                { id: 'insights', label: 'Insights', icon: <DataTrendingRegular /> },
              ]}
            />
          )}
          breadcrumb={<Text>Home / Kubernetes services / aks-cluster-alpha</Text>}
          header={<BladeHeader title="aks-cluster-alpha" subtitle="Kubernetes service · East US" />}
          commandBar={(
            <CommandBar
              primaryActions={[
                { id: 'create', label: 'Create', appearance: 'primary', onClick: () => undefined },
                { id: 'refresh', label: 'Refresh', icon: <ArrowClockwiseRegular />, onClick: () => undefined },
              ]}
            />
          )}
        >
          <div className="azf-showcase-portal-demo__body">
            <AzureDataGrid items={aksResourceRows} columns={[...aksResourceColumns]} ariaLabel="AKS resources" />
            <div className="azf-showcase-portal-flyout-stack">
              <section className="azf-showcase-portal-flyout" aria-label="Global search flyout">
                <div className="azf-showcase-portal-flyout__header">
                  <Text weight="semibold">Global search</Text>
                </div>
                <SearchBox value="kubernetes" aria-label="Search services and resources" />
                <div className="azf-showcase-portal-flyout__results" role="list">
                  <div role="listitem"><Text weight="semibold">Kubernetes services</Text><Text className="azf-muted">Service picker result</Text></div>
                  <div role="listitem"><Text weight="semibold">AKS resource list</Text><Text className="azf-muted">Recent resource result</Text></div>
                </div>
              </section>
              <section className="azf-showcase-portal-flyout" aria-label="Settings flyout">
                <div className="azf-showcase-portal-flyout__header">
                  <Text weight="semibold">Settings</Text>
                  <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close settings" />
                </div>
                <div className="azf-showcase-portal-flyout__results" role="list">
                  <div role="listitem"><Text>Directories and subscriptions</Text></div>
                  <div role="listitem"><Text>Appearance and startup views</Text></div>
                </div>
              </section>
              <NotificationPane
                surface="flyout"
                title="Activity"
                items={[
                  {
                    id: 'portal-activity',
                    title: 'Cluster policy review needed',
                    body: 'Cluster policy requires review before rollout continues.',
                    tone: 'warning',
                    timestamp: 'Now',
                    unread: true,
                    actions: [{ id: 'open-policy', label: 'Open policy', onClick: () => undefined }],
                  },
                ]}
              />
            </div>
          </div>
        </PortalLayout>
      </div>
    </PreviewCard>
  );
}

function PortalGlobalSearchPreview() {
  return (
    <PreviewCard title="Global search">
      <section className="azf-showcase-portal-flyout" aria-label="Global search flyout">
        <div className="azf-showcase-portal-flyout__header">
          <Text weight="semibold">Global search</Text>
        </div>
        <SearchBox value="kubernetes" aria-label="Search services and resources" />
        <div className="azf-showcase-portal-flyout__results" role="list">
          <div role="listitem"><Text weight="semibold">Azure Kubernetes Service</Text><Text className="azf-muted">Service picker result</Text></div>
          <div role="listitem"><Text weight="semibold">AKS resource list</Text><Text className="azf-muted">Recent resource result</Text></div>
          <div role="listitem"><Text weight="semibold">Kubernetes services</Text><Text className="azf-muted">Browse service result</Text></div>
        </div>
      </section>
    </PreviewCard>
  );
}

function PortalSettingsFlyoutPreview() {
  return (
    <PreviewCard title="Settings flyout">
      <section className="azf-showcase-portal-flyout" aria-label="Settings flyout">
        <div className="azf-showcase-portal-flyout__header">
          <Text weight="semibold">Portal settings</Text>
          <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close settings" />
        </div>
        <div className="azf-showcase-portal-flyout__results" role="list">
          <div role="listitem"><Text>Directories and subscriptions</Text></div>
          <div role="listitem"><Text>Appearance and startup views</Text></div>
          <div role="listitem"><Text>Language and region</Text></div>
        </div>
      </section>
    </PreviewCard>
  );
}

function PortalActivityFlyoutPreview() {
  return (
    <PreviewCard title="Activity flyout">
      <NotificationPane
        surface="flyout"
        title="Portal activity"
        items={[
          {
            id: 'portal-activity-pattern',
            title: 'Cluster policy review needed',
            body: 'Synthetic AKS cluster policy requires review before rollout continues.',
            tone: 'warning',
            timestamp: 'Now',
            unread: true,
            actions: [{ id: 'open-policy', label: 'Open policy', onClick: () => undefined }],
          },
        ]}
      />
    </PreviewCard>
  );
}

function CommandBarPreview() {
  return (
    <PreviewCard title="Command bar preview">
      <CommandBar
        title="Resource actions"
        description="Toolbar and page command mapping stays flat and task-focused."
        primaryActions={[
          { id: 'create', label: 'Create resource', appearance: 'primary', onClick: () => undefined },
          { id: 'refresh', label: 'Refresh', icon: <ArrowClockwiseRegular />, onClick: () => undefined },
        ]}
        secondaryActions={[{ id: 'open-query', label: 'Open query', icon: <SearchRegular />, onClick: () => undefined }]}
      />
    </PreviewCard>
  );
}

function FormFieldRowPreview() {
  return (
    <PreviewCard title="Form row preview">
      <div className="azf-showcase-form-column">
        <FormFieldRow
          label="Subscription"
          htmlFor="component-preview-subscription"
          info="Subscriptions scope policy, quota, and billing. The info label stays inline with the fixed label column."
          hint="This row preserves the narrow Azure blade reading width."
        >
          <Input id="component-preview-subscription" value="Sample subscription A" readOnly />
        </FormFieldRow>
        <FormFieldRow
          label="Resource group"
          htmlFor="component-preview-group"
          validationMessage="Resource group is required."
        >
          <Input id="component-preview-group" value="" />
        </FormFieldRow>
      </div>
    </PreviewCard>
  );
}

function StepListPreview() {
  const [selectedValue, setSelectedValue] = useState('basics');
  return (
    <PreviewCard title="Step list preview">
      <div className="azf-showcase-form-column">
        <AzureStepList
          selectedValue={selectedValue}
          onStepSelect={setSelectedValue}
          steps={[
            { id: 'basics', label: 'Basics', description: 'Name and scope' },
            { id: 'networking', label: 'Networking', description: 'Private endpoints' },
            { id: 'review', label: 'Review', description: 'Check warnings', status: 'warning' },
          ]}
        />
      </div>
      <RelatedCoverageNote
        title="Related entry-point details visible here"
        body="This preview calls out related launch, ribbon, and prompt-pill details that use the same implementation."
        items={['Prompt Ribbon(Copilot)', '.Suggested Prompt Pill', '.Copilot icon', '.Copilot icon(Old)']}
      />
    </PreviewCard>
  );
}

function TabListPreview() {
  const [selectedValue, setSelectedValue] = useState('overview');
  return (
    <PreviewCard title="Tab list preview" canvasClassName="azf-showcase-preview__canvas--intrinsic">
      <AzureTabList
        selectedValue={selectedValue}
        onTabSelect={setSelectedValue}
        tabs={[
          { id: 'overview', label: 'Overview', description: 'Service summary' },
          { id: 'security', label: 'Security', description: 'Blocking issue', status: 'warning' },
          { id: 'settings', label: 'Settings', description: 'Editable controls', status: 'success' },
        ]}
      />
    </PreviewCard>
  );
}

function DataGridPreview() {
  return (
    <PreviewCard title="Data grid preview">
      <AzureDataGrid
        items={resourceRows}
        columns={[...gridColumns]}
        caption="Compact list density with explicit headers, neutral chrome, and caller-owned cell rendering."
      />
    </PreviewCard>
  );
}

function PagerPreview() {
  const [page, setPage] = useState(2);
  const [pageSize, setPageSize] = useState(25);

  return (
    <PreviewCard title="Range, page, and page-size controls" canvasClassName="azf-showcase-preview__canvas--intrinsic">
      <Pager
        page={page}
        pageSize={pageSize}
        totalItems={57}
        pageSizeOptions={[10, 25, 50]}
        onPageChange={setPage}
        onPageSizeChange={setPageSize}
      />
      <RelatedCoverageNote
        title="Related pager details visible here"
        body="The standalone Pager preview keeps the related tab-number, count, and rows-per-page controls reachable in one compact surface."
        items={['.Tab Number', '.Pagination Counter', '.Num Dropdown']}
      />
    </PreviewCard>
  );
}

function EmptyStatePreview() {
  return (
    <PreviewCard title="Empty state preview">
      <AzureEmptyState
        title="No resources matched the current scope."
        body="Use the contextual no-results language from search/filter guidance instead of a decorative illustration card."
        action={<Button appearance="subtle">Reset filters</Button>}
      />
    </PreviewCard>
  );
}

function SummaryCardPreview() {
  return (
    <PreviewCard title="Summary card preview">
      <AzureSummaryCard
        title="Sandboxes"
        icon={<AzureIcon icon={<InfoRegular />} decorative />}
        metrics={[
          { id: 'running', label: 'Running', value: 3, tone: 'success' },
          { id: 'idle', label: 'Idle', value: 2, tone: 'neutral' },
          { id: 'stopped', label: 'Stopped', value: 0, tone: 'danger' },
        ]}
      />
    </PreviewCard>
  );
}

function PropertyListPreview() {
  return (
    <PreviewCard title="Property list preview">
      <AzurePropertyList
        title="Basics"
        items={[
          { id: 'name', label: 'Name', value: 'sandbox-group-7d22' },
          { id: 'location', label: 'Location', value: 'swedencentral' },
          { id: 'state', label: 'Provisioning state', value: 'Succeeded' },
        ]}
      />
    </PreviewCard>
  );
}

function NotificationPanePreview() {
  return (
    <PreviewCard
      title="Notification pane preview"
      canvasClassName="azf-showcase-preview__canvas--intrinsic"
    >
      <div className="azf-showcase-notification-demo">
        <NotificationPane
          className="azf-showcase-notification-demo__pane"
          title="Activity updates"
          items={[
            {
              id: 'notification-1',
              title: 'Firewall validation blocked',
              body: 'Cluster policy requires review before rollout continues.',
              tone: 'warning',
              timestamp: 'Now',
              unread: true,
              actions: [
                { id: 'open', label: 'Open resource', onClick: () => undefined },
                { id: 'assign', label: 'Assign policy', onClick: () => undefined },
              ],
            },
            {
              id: 'notification-2',
              title: 'Backup policy updated',
              body: 'Maintenance configuration now applies to selected clusters in West US 2.',
              tone: 'success',
              timestamp: '2 min ago',
              actions: [{ id: 'view-change', label: 'View change', onClick: () => undefined }],
            },
            {
              id: 'notification-3',
              title: 'Cost alert threshold reached',
              body: 'Forecasted compute spend is near the configured monthly budget.',
              tone: 'info',
              timestamp: '18 min ago',
            },
          ]}
          footer={<Button appearance="subtle">View all activity</Button>}
        />
        <section className="azf-showcase-notification-demo__detail" aria-label="Selected notification detail">
          <Text weight="semibold">Selected update</Text>
          <StatusIconText status="warning">Firewall validation blocked</StatusIconText>
          <Text className="azf-muted">
            Open the affected Kubernetes resource and review policy before approving rollout.
          </Text>
          <div className="azf-row azf-gap-xs azf-wrap">
            <Button appearance="primary">Review policy</Button>
            <Button>Dismiss</Button>
          </div>
        </section>
      </div>
    </PreviewCard>
  );
}

function FeedbackFooterPreview() {
  return (
    <PreviewCard title="Feedback footer preview">
      <FeedbackFooter
        title="Was this task flow clear?"
        body="Customer feedback prompts stay low emphasis and right-aligned."
        action={{ id: 'give-feedback', label: 'Give feedback', onClick: () => undefined }}
      />
    </PreviewCard>
  );
}

function DeleteDialogPreview() {
  const [acknowledged, setAcknowledged] = useState(false);
  return (
    <PreviewCard title="Delete confirmation preview">
      <div className="azf-showcase-inline-actions">
        <DeleteResourceDialog
          resourceName="sample-resource"
          softDelete
          confirmationText="Soft delete remains available for 14 days, but connected workloads lose access immediately."
          consequences={[
            'Snapshots remain recoverable during retention.',
            'Dependent applications lose access immediately.',
          ]}
          acknowledgement={{
            label: 'I understand this action affects connected workloads.',
            checked: acknowledged,
            onChange: setAcknowledged,
          }}
          trigger={<Button appearance="outline" icon={<DeleteRegular />}>Delete resource</Button>}
          onCancel={() => setAcknowledged(false)}
          onConfirm={() => undefined}
        />
      </div>
    </PreviewCard>
  );
}

function AccordionPreview() {
  return (
    <PreviewCard title="Bordered and borderless states">
      <div className="azf-showcase-demo-grid azf-showcase-demo-grid--two-up">
        <section className="azf-showcase-demo-panel">
          <Badge appearance="outline">With border</Badge>
          <AzureAccordion items={[...accordionPreviewItems]} defaultOpenItems={['dependencies']} multiple ariaLabel="Bordered accordion" />
        </section>
        <section className="azf-showcase-demo-panel">
          <Badge appearance="outline">Default / without border</Badge>
          <AzureAccordion items={[...accordionPreviewItems]} bordered={false} defaultOpenItems={['overview']} multiple ariaLabel="Borderless accordion" />
        </section>
      </div>
    </PreviewCard>
  );
}

function CodeSnippetPreview() {
  return (
    <PreviewCard title="Scrollable editor region" canvasClassName="azf-showcase-preview__canvas--intrinsic azf-showcase-preview__canvas--code">
      <CodeSnippet title="ARM template" lines={[...codeSnippetLines]} maxHeight={220} />
      <RelatedCoverageNote
        title="Related code-snippet details visible here"
        body="The component preview intentionally shows the related line, gutter, indentation, and collapse treatments inside the same CodeSnippet implementation."
        items={['.Code line', '.Number', '.Code level(s)', '.JSON Collapse']}
      />
    </PreviewCard>
  );
}

function CopyButtonPreview() {
  return (
    <PreviewCard title="Rest, hover, and copied states">
      <div className="azf-showcase-state-grid">
        <div className="azf-showcase-state-grid__label" />
        <Text weight="semibold">Rest</Text>
        <Text weight="semibold">Hover</Text>
        <Text weight="semibold">Copied</Text>
        <Text weight="semibold">Icon only</Text>
        <CopyButton value="sub-12345" visualState="rest" />
        <CopyButton value="sub-12345" visualState="hover" />
        <CopyButton value="sub-12345" visualState="copied" />
        <Text weight="semibold">Labeled</Text>
        <CopyButton value="az aks show --name prod" label="Click here to copy" visualState="rest" />
        <CopyButton value="az aks show --name prod" label="Click here to copy" visualState="hover" />
        <CopyButton value="az aks show --name prod" label="Click here to copy" visualState="copied" />
      </div>
    </PreviewCard>
  );
}

function CopilotComposerPreview() {
  const [readyPrompt, setReadyPrompt] = useState('Summarize the rollout failures and attach the kubelet log snippet.');
  const [runningPrompt, setRunningPrompt] = useState('Draft the approval comment and attach the rollout log.');
  const [agentMode, setAgentMode] = useState(true);

  return (
    <PreviewCard
      title="Composer with attachment and stop flow"
      canvasClassName="azf-showcase-preview__canvas--intrinsic"
    >
      <div className="azf-showcase-composer-demo">
        <section className="azf-showcase-composer-demo__primary" aria-label="Ready composer">
          <Text weight="semibold">Ready</Text>
          <CopilotComposer
            className="azf-showcase-composer-demo__composer"
            value={readyPrompt}
            onChange={setReadyPrompt}
            onSend={() => undefined}
            agentMode={agentMode}
            onAgentModeChange={setAgentMode}
            attachments={[{ id: 'log', name: 'kubelet.log', onRemove: () => undefined }]}
            onAddAttachment={() => undefined}
            placeholder="Ask Copilot about this rollout"
          />
        </section>
        <section className="azf-showcase-composer-demo__secondary" aria-label="Running composer">
          <Text weight="semibold">Running</Text>
          <CopilotComposer
            className="azf-showcase-composer-demo__composer azf-showcase-composer-demo__composer--compact"
            value={runningPrompt}
            onChange={setRunningPrompt}
            onSend={() => undefined}
            isRunning
            onStop={() => undefined}
            agentMode={false}
            onAgentModeChange={() => undefined}
            attachments={[{ id: 'rollout', name: 'rollout-summary.md', onRemove: () => undefined }]}
            onAddAttachment={() => undefined}
            placeholder="Ask Copilot about this rollout"
          />
        </section>
      </div>
    </PreviewCard>
  );
}

function CopilotResponsePreview() {
  return (
    <PreviewCard
      title="Response, confirmation, and action row"
      canvasClassName="azf-showcase-preview__canvas--intrinsic"
    >
      <div className="azf-showcase-response-demo">
        <section className="azf-showcase-response-demo__primary" aria-label="Resolved Copilot response">
          <Text weight="semibold">Resolved response</Text>
          <CopilotResponse
            className="azf-showcase-response-demo__response"
            parts={[...copilotResponseParts]}
            actions={[
              { id: 'open-incident', label: 'Open incident', onClick: () => undefined },
              { id: 'copy-response', label: 'Copy response', onClick: () => undefined },
            ]}
          />
        </section>
        <section className="azf-showcase-response-demo__secondary" aria-label="Loading response">
          <Text weight="semibold">Loading</Text>
          <CopilotResponse
            className="azf-showcase-response-demo__response azf-showcase-response-demo__response--compact"
            parts={[
              { id: 'user-loading', type: 'text', author: 'user', content: 'Compare the East US and West US rollout failures.' },
              {
                id: 'assistant-loading',
                type: 'text',
                title: 'Copilot',
                badge: 'AI-generated content may be incorrect',
                content: 'East US is blocked by a policy assignment; West US is waiting on approval.',
                supportingText: '2 requests left',
              },
            ]}
            loading
          />
        </section>
      </div>
    </PreviewCard>
  );
}

function InlineCopilotPreview() {
  const [openPrompt, setOpenPrompt] = useState('');
  const [guidedPrompt, setGuidedPrompt] = useState('Summarize the warning so it names the private endpoint policy.');

  return (
    <PreviewCard title="Open and guided inline starts">
      <div className="azf-stack" style={{ gap: 24 }}>
        <div style={{ minHeight: 260 }}>
          <InlineCopilot
            open
            trigger={<Button appearance="secondary">Open inline Copilot</Button>}
            value={openPrompt}
            onChange={setOpenPrompt}
            onSubmit={() => undefined}
            placeholder="Ask Copilot to draft, fix, or explain"
          />
        </div>
        <div style={{ minHeight: 320 }}>
          <InlineCopilot
            open
            trigger={<Button appearance="secondary">Summarize with Copilot</Button>}
            title="Summarize with Copilot"
            value={guidedPrompt}
            onChange={setGuidedPrompt}
            onSubmit={() => undefined}
            onDismiss={() => undefined}
            suggestions={[
              { id: 'tone', label: 'Shorter wording' },
              { id: 'steps', label: 'Add next steps' },
            ]}
          />
        </div>
      </div>
      <RelatedCoverageNote
        title="Related entry-point details visible here"
        body="InlineCopilot now calls out the related launch, ribbon, and prompt-pill details used by the same implementation."
        items={['Prompt Ribbon(Copilot)', '.Suggested Prompt Pill', '.Copilot icon', '.Copilot icon(Old)']}
      />
    </PreviewCard>
  );
}

function AgenticProgressPreview() {
  const cotArtifacts = [...chainOfThoughtArtifacts];
  return (
    <>
      <PreviewCard title="Activity and approval-gated progress">
        <ChainOfThought
          title="Run activity"
          subtitle={`${cotArtifacts.length} updates captured`}
          steps={chainOfThoughtSteps.map((step) => ({ ...step }))}
          artifacts={cotArtifacts}
          onApprove={() => undefined}
          onDeny={() => undefined}
        />
        <RelatedCoverageNote
          title="Related activity details visible here"
          body="This activity panel groups progress, outputs, completed actions, step state, and inline approval in one review surface."
          items={['Activity panel', 'Review state', 'Complete state', 'Needs user input', 'Show outputs', 'Output row', 'Approval']}
        />
      </PreviewCard>
      <PreviewCard title="Automation progress list">
        <AgenticProgress steps={[...agenticPreviewSteps]} defaultOpenItems={['approve']} onApprove={() => undefined} onDeny={() => undefined} />
        <RelatedCoverageNote
          title="Compact activity stream"
          body="Use this accordion-based activity list on its own when the full review panel is not needed."
          items={['Automation list', 'Action swap', 'Output pill']}
        />
      </PreviewCard>
    </>
  );
}

function CopilotWorkspacePreview() {
  const [prompt, setPrompt] = useState('Draft the remediation comment for the deployment review.');

  return (
    <PreviewCard
      title="Copilot workspace preview"
      frameClassName="azf-showcase-preview__frame--copilot-workspace"
    >
      <section className="azf-copilot-workspace-demo" aria-label="Compact Copilot workspace preview">
        <ServiceMenu
          className="azf-copilot-workspace-demo__nav"
          groups={copilotWorkspaceGroups.map((group) => ({ ...group, items: [...group.items] }))}
          selectedId="chat"
          onSelect={() => undefined}
        />
        <div className="azf-copilot-workspace-demo__main">
          <div className="azf-copilot-workspace-demo__prompt">
            <Text weight="semibold">Prompt</Text>
            <Text>Summarize the rollout failures and attach the Kusto query.</Text>
          </div>
          <CopilotResponse
            className="azf-copilot-workspace-demo__response"
            parts={copilotResponseParts.filter((part) => part.id !== 'user')}
          />
          <CopilotComposer
            className="azf-copilot-workspace-demo__composer"
            value={prompt}
            onChange={setPrompt}
            onSend={() => undefined}
            attachments={[{ id: 'kusto', name: 'rollout-failures.kql' }]}
            placeholder="Ask Copilot about this rollout"
          />
        </div>
      </section>
    </PreviewCard>
  );
}

function CoordinatorRunPreview() {
  const [steering, setSteering] = useState('Prioritize the East US remediation and hold the rest until it clears.');
  const runArtifacts = [...chainOfThoughtArtifacts];

  return (
    <CoordinatorRunPattern
      title="Run review workspace"
      subtitle="Remediation review · 3 of 4 steps complete"
      runActions={[
        { id: 'pause', label: 'Pause run', onClick: () => undefined },
        { id: 'logs', label: 'View logs', appearance: 'subtle', onClick: () => undefined },
      ]}
      copilotActions={[{ id: 'summarize', label: 'Summarize run', onClick: () => undefined }]}
      reasoning={{
        title: 'Run reasoning',
        subtitle: `${runArtifacts.length} artifacts created`,
        steps: chainOfThoughtSteps.map((step) => ({ ...step })),
        artifacts: runArtifacts,
        onApprove: () => undefined,
        onDeny: () => undefined,
      }}
      response={{ parts: [...copilotResponseParts] }}
      composer={{
        value: steering,
        onChange: setSteering,
        onSend: () => undefined,
        attachments: [{ id: 'run', name: 'run-context.md' }],
        placeholder: 'Add guidance for the next step…',
      }}
    />
  );
}

function AgenticApprovalPreview() {
  return (
    <AgenticApprovalPattern
      title="Approve sample remediation"
      summary="The workflow paused for a human decision before modifying sample clusters."
      steps={agenticPreviewSteps.map((step) => ({ ...step }))}
      defaultOpenItems={['approve']}
      onApprove={() => undefined}
      onDeny={() => undefined}
    />
  );
}

function CopilotTriagePanelPreview() {
  const [triagePrompt, setTriagePrompt] = useState('Draft the owner update and include the blocked policy name.');

  return (
    <CopilotTriagePanelPattern
      title="Copilot triage panel"
      summary="A compact side panel for reviewing findings, recommended action, and owner follow-up in one place."
      actions={[
        { id: 'assign', label: 'Assign owner', onClick: () => undefined },
        { id: 'open-resource', label: 'Open resource', appearance: 'subtle', onClick: () => undefined },
      ]}
      steps={agenticPreviewSteps.map((step) => ({ ...step }))}
      response={{
        parts: [
          {
            id: 'triage-summary',
            type: 'text',
            title: 'Copilot',
            badge: 'AI-generated content may be incorrect',
            content: 'East US remediation is blocked by a private endpoint policy. Assign the networking owner before retrying the rollout.',
            supportingText: 'Ready to share',
          },
          {
            id: 'triage-confirm',
            type: 'confirmation',
            content: 'Send the owner update to the incident channel?',
            confirmLabel: 'Send update',
            cancelLabel: 'Review message',
            onConfirm: () => undefined,
            onCancel: () => undefined,
          },
        ],
      }}
      composer={{
        value: triagePrompt,
        onChange: setTriagePrompt,
        onSend: () => undefined,
        attachments: [{ id: 'policy', name: 'policy-findings.json' }],
      }}
    />
  );
}

function ResourceOperationHeaderPreview() {
  return (
    <ResourceOperationHeaderPattern
      title="Sample resource"
      subtitle="Storage account · East US"
      resourceIcon={<ShieldTaskRegular />}
      actions={[
        { id: 'refresh', label: 'Refresh', icon: <ArrowClockwiseRegular />, onClick: () => undefined },
        { id: 'pin', label: 'Pin', appearance: 'subtle', onClick: () => undefined },
      ]}
      commandActions={[
        { id: 'start-backup', label: 'Start backup', appearance: 'primary', onClick: () => undefined },
        { id: 'open-activity', label: 'View activity', onClick: () => undefined },
      ]}
      statusItems={[
        { id: 'health', title: 'Health', body: 'One networking policy needs attention before the next rollout.' },
        { id: 'backup', title: 'Backup', body: 'Last snapshot completed 12 minutes ago.' },
        { id: 'access', title: 'Access', body: 'Private endpoint access is enabled.' },
      ]}
    >
      <AzureDataGrid items={resourceRows.slice(0, 2)} columns={[...gridColumns]} caption="Recent operation status" />
    </ResourceOperationHeaderPattern>
  );
}

const composedScenarios: {
  id: string;
  title: string;
  parts: string;
  summary: string;
  preview: () => ReactElement;
}[] = [
  {
    id: 'run-review-workspace',
    title: 'Run review workspace',
    parts: 'Header, reasoning panel, Copilot answer, composer',
    summary:
      'Review progress, artifacts, approvals, and follow-up guidance without leaving the task workspace.',
    preview: CoordinatorRunPreview,
  },
  {
    id: 'approval-checkpoint',
    title: 'Approval checkpoint',
    parts: 'Approval card and progress list',
    summary:
      'Pause a sensitive operation, explain the risk, and keep approve or deny actions close to the affected step.',
    preview: AgenticApprovalPreview,
  },
  {
    id: 'copilot-triage-panel',
    title: 'Copilot triage panel',
    parts: 'Action strip, progress list, Copilot answer, composer',
    summary:
      'Summarize an active issue, show the recommended next step, and let the user send a focused follow-up.',
    preview: CopilotTriagePanelPreview,
  },
  {
    id: 'resource-operation-header',
    title: 'Resource operation header',
    parts: 'Blade header, command strip, status cards, grid',
    summary:
      'Place resource identity, primary commands, health cues, and recent operations at the top of a management flow.',
    preview: ResourceOperationHeaderPreview,
  },
];

function ComposedScenariosSection() {
  return (
    <section className="azf-showcase-app__surface azf-showcase-scenarios">
      <div className="azf-showcase-app__surface-header">
        <div>
          <Text as="h2" size={600} weight="semibold">Composed scenarios</Text>
          <Text className="azf-muted">
            Practical product flows built from the same reviewed Azure Fluent components shown in this browser.
          </Text>
        </div>
        <Badge appearance="tint" color="brand">Scenario patterns</Badge>
      </div>
      <div className="azf-showcase-scenarios__grid">
        {composedScenarios.map((scenario) => {
          const ScenarioPreview = scenario.preview;
          return (
            <article key={scenario.id} className="azf-stack azf-gap-s">
              <div className="azf-stack azf-gap-xs">
                <div className="azf-row azf-showcase-scenario__head">
                  <Text as="h3" size={500} weight="semibold">{scenario.title}</Text>
                  <span className="azf-showcase-scenario__export">{scenario.parts}</span>
                </div>
                <Text className="azf-muted">{scenario.summary}</Text>
              </div>
              <PreviewCard title={scenario.title}>
                <ScenarioPreview />
              </PreviewCard>
            </article>
          );
        })}
      </div>
    </section>
  );
}

const reusablePatternExamples: {
  id: string;
  title: string;
  codeName: string;
  summary: string;
  preview: () => ReactElement;
}[] = [
  {
    id: 'browse-resource-pattern',
    title: 'AKS resource list',
    codeName: 'BrowseResourcePattern',
    summary: 'Search, filter, command, and dense table anatomy for Kubernetes resources.',
    preview: BrowseResourcePatternPreview,
  },
  {
    id: 'filtering-pattern',
    title: 'Filtered resource list',
    codeName: 'FilteringPattern',
    summary: 'A browse flow variant focused on narrowing an existing list.',
    preview: FilteringPatternPreview,
  },
  {
    id: 'manage-resource-pattern',
    title: 'Manage resource',
    codeName: 'ManageResourcePattern',
    summary: 'Scoped navigation with compact forms and status lists for routine settings.',
    preview: ManageResourcePatternPreview,
  },
  {
    id: 'form-blade-pattern',
    title: 'Form blade',
    codeName: 'FormBladePattern',
    summary: 'A focused edit form with message, fields, and docked actions.',
    preview: FormBladePatternPreview,
  },
  {
    id: 'step-wizard-pattern',
    title: 'Step wizard',
    codeName: 'StepWizardPattern',
    summary: 'Guided configuration steps with contextual content and footer actions.',
    preview: StepWizardPatternPreview,
  },
  {
    id: 'create-resource-pattern',
    title: 'Create resource',
    codeName: 'CreateResourcePattern',
    summary: 'A create flow with validation and review content.',
    preview: CreateResourcePatternComponentPreview,
  },
  {
    id: 'error-pattern',
    title: 'Error message',
    codeName: 'ErrorPattern',
    summary: 'A concise blocking message with a next action.',
    preview: ErrorPatternPreview,
  },
  {
    id: 'notification-pattern',
    title: 'Notification message',
    codeName: 'NotificationPattern',
    summary: 'A lightweight status message for inline task feedback.',
    preview: () => <NotificationPattern title="Backup policy updated" body="Nightly snapshots now apply to sample storage accounts." intent="success" />,
  },
  {
    id: 'service-overview-pattern',
    title: 'Service overview',
    codeName: 'ServiceOverviewPattern',
    summary: 'Summary cards and actions for a service landing surface.',
    preview: ServiceOverviewPatternPreview,
  },
  {
    id: 'copilot-workspace-pattern',
    title: 'Copilot workspace',
    codeName: 'CopilotWorkspacePattern',
    summary: 'Navigation, response content, and prompt composer for a Copilot task area.',
    preview: CopilotWorkspacePreview,
  },
  {
    id: 'coordinator-run-pattern',
    title: 'Run review workspace',
    codeName: 'CoordinatorRunPattern',
    summary: 'A progress review workspace with artifacts, approvals, and guidance.',
    preview: CoordinatorRunPreview,
  },
  {
    id: 'agentic-approval-pattern',
    title: 'Approval checkpoint',
    codeName: 'AgenticApprovalPattern',
    summary: 'A compact approval card for sensitive workflow steps.',
    preview: AgenticApprovalPreview,
  },
  {
    id: 'copilot-triage-panel-pattern',
    title: 'Copilot triage panel',
    codeName: 'CopilotTriagePanelPattern',
    summary: 'A triage panel for findings, recommended action, and owner follow-up.',
    preview: CopilotTriagePanelPreview,
  },
  {
    id: 'resource-operation-header-pattern',
    title: 'Resource operation header',
    codeName: 'ResourceOperationHeaderPattern',
    summary: 'Resource identity, commands, health cues, and recent operations.',
    preview: ResourceOperationHeaderPreview,
  },
];

const portalCapturePatternFamilies: PatternFamily[] = [
  {
    id: 'aks-resource-list',
    name: 'AKS resource list',
    status: 'showcase',
    pageNodeId: 'local-aks-resource-list',
    pageNodeUrl: '',
    representativeNodes: [],
    libraryMappings: ['BrowseResourcePattern', 'AzureDataGrid'],
    antiRules: [],
    localExamples: ['showcase/AzureFluentShowcaseApp.tsx'],
    implementationFiles: ['patterns.tsx', 'components.tsx', 'tokens.css'],
  },
  {
    id: 'portal-global-search',
    name: 'Global search',
    status: 'showcase',
    pageNodeId: 'local-portal-global-search',
    pageNodeUrl: '',
    representativeNodes: [],
    libraryMappings: ['SearchBox'],
    antiRules: [],
    localExamples: ['showcase/AzureFluentShowcaseApp.tsx'],
    implementationFiles: ['components.tsx', 'tokens.css'],
  },
  {
    id: 'portal-settings-flyout',
    name: 'Settings flyout',
    status: 'showcase',
    pageNodeId: 'local-portal-settings-flyout',
    pageNodeUrl: '',
    representativeNodes: [],
    libraryMappings: ['Button', 'Text'],
    antiRules: [],
    localExamples: ['showcase/AzureFluentShowcaseApp.tsx'],
    implementationFiles: ['components.tsx', 'tokens.css'],
  },
  {
    id: 'portal-activity-flyout',
    name: 'Activity flyout',
    status: 'showcase',
    pageNodeId: 'local-portal-activity-flyout',
    pageNodeUrl: '',
    representativeNodes: [],
    libraryMappings: ['NotificationPane'],
    antiRules: [],
    localExamples: ['showcase/AzureFluentShowcaseApp.tsx'],
    implementationFiles: ['components.tsx', 'tokens.css'],
  },
];

const showcasePatternFamilies: PatternFamily[] = [...portalCapturePatternFamilies, ...patternGuide.families];

function ReusablePatternComponentsSection() {
  return (
    <section className="azf-showcase-app__surface azf-showcase-scenarios">
      <div className="azf-showcase-app__surface-header">
        <div>
          <Text as="h2" size={600} weight="semibold">Reusable pattern components</Text>
          <Text className="azf-muted">
            Every reusable pattern component has a visible example so consumers can choose the right building block.
          </Text>
        </div>
        <Badge appearance="tint" color="brand">All patterns shown</Badge>
      </div>
      <div className="azf-showcase-scenarios__grid">
        {reusablePatternExamples.map((example) => {
          const ExamplePreview = example.preview;
          return (
            <article key={example.id} className="azf-stack azf-gap-s">
              <div className="azf-stack azf-gap-xs">
                <div className="azf-row azf-showcase-scenario__head">
                  <Text as="h3" size={500} weight="semibold">{example.title}</Text>
                  <span className="azf-showcase-scenario__export">Reusable pattern</span>
                </div>
                <Text className="azf-muted">{example.summary}</Text>
              </div>
              <PreviewCard title={example.title}>
                <ExamplePreview />
              </PreviewCard>
            </article>
          );
        })}
      </div>
    </section>
  );
}

function PortalCaptureHighlights({
  onOpenPattern,
}: {
  onOpenPattern: (patternId: string) => void;
}) {
  const highlights = [
    { id: 'aks-resource-list', label: 'AKS resource list', description: 'Azure Kubernetes Service list with resource group, subscription, type, and status columns.' },
    { id: 'portal-global-search', label: 'Global search', description: 'Portal-style service and resource picker flyout.' },
    { id: 'portal-settings-flyout', label: 'Settings flyout', description: 'Compact Portal settings pane with flat action rows.' },
    { id: 'portal-activity-flyout', label: 'Activity flyout', description: 'Portal activity and notifications flyout with actionable updates.' },
  ];

  return (
    <section className="azf-showcase-capture-highlights" aria-label="Portal-style showcase highlights">
      <div className="azf-showcase-capture-highlights__copy">
        <Text weight="semibold">Portal-style patterns now visible</Text>
        <Text className="azf-muted">
          Open the Patterns view for the sanitized AKS and flyout examples.
        </Text>
      </div>
      <div className="azf-showcase-capture-highlights__links">
        {highlights.map((highlight) => (
          <button
            key={highlight.id}
            type="button"
            className="azf-showcase-capture-highlights__link"
            onClick={() => onOpenPattern(highlight.id)}
          >
            <Text as="span" weight="semibold">{highlight.label}</Text>
            <Text as="span" className="azf-muted">{highlight.description}</Text>
          </button>
        ))}
      </div>
    </section>
  );
}

function AzureSliderPreview() {
  const [cores, setCores] = useState(8);

  return (
    <PreviewCard title="Slider preview">
      <div className="azf-showcase-form-column">
        <AzureSlider
          label="Provisioned vCores"
          info="Scale compute for the elastic pool. Applies at the next maintenance window."
          min={2}
          max={32}
          step={2}
          value={cores}
          onChange={setCores}
          showValue
          formatValue={(value) => `${value} vCores`}
        />
        <AzureSlider label="Disabled" min={0} max={100} defaultValue={40} showValue disabled />
      </div>
    </PreviewCard>
  );
}

function ProgressBarWithLabelPreview() {
  return (
    <PreviewCard title="Progress bar preview">
      <div className="azf-showcase-form-column">
        <ProgressBarWithLabel
          label="Copying blobs"
          info="Server-side copy across regions. Safe to leave this blade."
          description="18 of 42 objects copied"
          value={0.42}
        />
        <ProgressBarWithLabel
          label="Provisioning environment"
          description="This can take a few minutes."
          indeterminate
        />
      </div>
    </PreviewCard>
  );
}

function FileUploadPreview() {
  return (
    <PreviewCard title="File upload preview">
      <div className="azf-showcase-form-column">
        <FileUpload label="Certificate (.pfx)" placeholder="Select File" />
        <FileUpload label="Uploading" state="progress" fileName="sample-cert.pfx" progress={0.6} />
        <FileUpload label="Uploaded" state="success" fileName="sample-cert.pfx" />
        <FileUpload label="Bulk import" state="dragdrop" multiple />
      </div>
    </PreviewCard>
  );
}

function FilterableComboBoxPreview() {
  const [selected, setSelected] = useState<string | undefined>('sub-a');

  return (
    <PreviewCard title="Filterable combo box preview">
      <div className="azf-showcase-form-column">
        <FilterableComboBox
          label="Subscription"
          info="Type to filter across all subscriptions you can access."
          placeholder="Select a subscription"
          options={[
            { id: 'sub-a', label: 'Sample subscription A' },
            { id: 'sub-b', label: 'Sample subscription B' },
            { id: 'sub-c', label: 'Sample subscription C' },
            { id: 'sub-shared', label: 'Shared Platform Services' },
            { id: 'sub-sandbox', label: 'Innovation Sandbox' },
          ]}
          value={selected}
          onSelect={setSelected}
        />
      </div>
    </PreviewCard>
  );
}

function AzureToolbarPreview() {
  return (
    <PreviewCard title="Toolbar preview">
      <AzureToolbar
        topOfPage
        ariaLabel="Resource commands"
        actions={[
          { id: 'create', label: 'Create', icon: <SparkleRegular />, appearance: 'primary', onClick: () => undefined },
          { id: 'refresh', label: 'Refresh', icon: <ArrowClockwiseRegular />, onClick: () => undefined },
          { id: 'divider', label: '|' },
          { id: 'delete', label: 'Delete', icon: <DeleteRegular />, onClick: () => undefined },
        ]}
      />
    </PreviewCard>
  );
}

function PortalTopNavPreview() {
  const [search, setSearch] = useState('platform');
  return (
    <PreviewCard title="Top navigation preview">
      <PortalTopNav
        brand={{ product: 'Microsoft Azure', area: 'Portal' }}
        startActions={[
          { id: 'all-services', label: 'All services', icon: <AppsListRegular /> },
          { id: 'toggle-nav', label: 'Toggle navigation', icon: <NavigationRegular /> },
        ]}
        searchValue={search}
        onSearchChange={setSearch}
        copilotAction={{ id: 'copilot', label: 'Copilot', icon: <SparkleRegular /> }}
        endActions={[
          { id: 'settings', label: 'Settings', icon: <SettingsRegular /> },
          { id: 'notifications', label: 'Notifications', icon: <InfoRegular /> },
        ]}
        persona={{ name: 'Signed-in user', secondaryText: 'Organization directory', icon: <PersonCircleRegular /> }}
      />
    </PreviewCard>
  );
}

function PortalRailPreview() {
  return (
    <PreviewCard title="Portal rail preview" canvasClassName="azf-showcase-preview__canvas--intrinsic">
      <PortalRail
        items={[
          { id: 'home', label: 'Home', icon: <HomeRegular />, selected: true },
          { id: 'insights', label: 'Insights', icon: <DataTrendingRegular /> },
          { id: 'settings', label: 'Settings', icon: <SettingsRegular /> },
        ]}
      />
    </PreviewCard>
  );
}

function ServiceMenuPreview() {
  const [menuQuery, setMenuQuery] = useState('net');
  return (
    <PreviewCard
      title="Service menu preview"
      frameClassName="azf-showcase-preview__frame--compact"
      canvasClassName="azf-showcase-preview__canvas--intrinsic"
    >
      <div className="azf-showcase-service-menu-demo">
        <ServiceMenu
          className="azf-showcase-service-menu-demo__menu"
          groups={serviceMenuGroups.map((group) => ({ ...group, items: [...group.items] }))}
          selectedId="private-access"
          searchValue={menuQuery}
          onSearchChange={setMenuQuery}
          onSelect={() => undefined}
          onToggleFavorite={() => undefined}
        />
        <section className="azf-showcase-service-menu-demo__detail" aria-label="Selected service menu item">
          <Text weight="semibold">Private access</Text>
          <Text className="azf-muted">Selected navigation rows keep focus in the service blade while related items stay searchable.</Text>
          <div className="azf-row azf-gap-xs azf-wrap">
            <Badge appearance="tint">Selected</Badge>
            <Badge appearance="outline">Networking</Badge>
          </div>
          <Button appearance="subtle">Open private endpoint settings</Button>
        </section>
      </div>
    </PreviewCard>
  );
}

function BladeHeaderPreview() {
  const [pinned, setPinned] = useState(false);
  const [starred, setStarred] = useState(true);
  return (
    <PreviewCard title="Blade header preview">
      <BladeHeader
        title="Sample resource"
        menuLabel="Overview"
        subtitle="Storage account"
        resourceIcon={<ShieldTaskRegular />}
        pinned={pinned}
        onPin={() => setPinned((value) => !value)}
        starred={starred}
        onStar={() => setStarred((value) => !value)}
        overflowActions={[
          { id: 'refresh', label: 'Refresh', icon: <ArrowClockwiseRegular />, onClick: () => undefined },
          { id: 'delete', label: 'Delete', icon: <DeleteRegular />, destructive: true, onClick: () => undefined },
        ]}
        promptRibbon={
          <CopilotPromptRibbon
            prompts={[
              { id: 'summarize', label: 'Summarize' },
              { id: 'cost', label: 'Analyze cost' },
            ]}
          />
        }
        onDismiss={() => undefined}
      />
    </PreviewCard>
  );
}

function ResourceTagEditorPreview() {
  const [rows, setRows] = useState([
    { id: 'r1', name: 'environment', value: 'production', resourceId: 'vm' },
    { id: 'r2', name: 'costCenter', value: '4415', resourceId: 'storage' },
  ]);
  return (
    <PreviewCard title="Resource tags preview">
      <ResourceTagEditor
        rows={rows}
        resources={[
          { id: 'vm', label: 'sample-vm' },
          { id: 'storage', label: 'sample-storage' },
        ]}
        onRowChange={(rowId, patch) =>
          setRows((prev) => prev.map((row) => (row.id === rowId ? { ...row, ...patch } : row)))
        }
        onAddRow={() =>
          setRows((prev) => [...prev, { id: `r${prev.length + 1}`, name: '', value: '', resourceId: 'vm' }])
        }
        onDeleteRow={(rowId) => setRows((prev) => prev.filter((row) => row.id !== rowId))}
      />
    </PreviewCard>
  );
}

function AzureFormPreview() {
  return (
    <PreviewCard title="Form preview">
      <div className="azf-showcase-form-column">
        <AzureForm
          message="Review the configuration before you deploy."
          footer={
            <FormFooter
              primaryAction={{ id: 'save', label: 'Save', appearance: 'primary', onClick: () => undefined }}
              secondaryAction={{ id: 'cancel', label: 'Cancel', onClick: () => undefined }}
            />
          }
          onSubmit={() => undefined}
        >
          <FormFieldRow label="Name" htmlFor="azure-form-name">
            <Input id="azure-form-name" value="sample-platform" readOnly />
          </FormFieldRow>
          <FormFieldRow label="Region" htmlFor="azure-form-region">
            <Input id="azure-form-region" value="East US 2" readOnly />
          </FormFieldRow>
        </AzureForm>
      </div>
    </PreviewCard>
  );
}

function FormFooterPreview() {
  return (
    <PreviewCard title="Form footer preview">
      <FormFooter
        primaryAction={{ id: 'save-footer', label: 'Save', appearance: 'primary', onClick: () => undefined }}
        secondaryAction={{ id: 'discard-footer', label: 'Discard', onClick: () => undefined }}
        feedback={<Link href="#" onClick={(event) => event.preventDefault()}>Give feedback</Link>}
      />
    </PreviewCard>
  );
}

function EssentialsGridPreview() {
  return (
    <PreviewCard
      title="Essentials preview"
      frameClassName="azf-showcase-preview__frame--compact"
      canvasClassName="azf-showcase-preview__canvas--intrinsic"
    >
      <EssentialsGrid
        className="azf-showcase-essentials-preview"
        properties={[
          { id: 'rg', label: 'Resource group', value: 'sample-platform-rg', href: '#' },
          { id: 'status', label: 'Status', value: 'Running' },
          { id: 'location', label: 'Location', value: 'East US 2' },
          { id: 'subscription', label: 'Subscription', value: 'Sample subscription A', href: '#' },
          { id: 'sub-id', label: 'Subscription ID', value: 'redacted-subscription-id' },
          { id: 'tags', label: 'Tags', value: 'environment : sample', tags: ['costCenter : 4415'] },
        ]}
      />
    </PreviewCard>
  );
}

function FilterPillsPreview() {
  const [selected, setSelected] = useState(['all']);
  const toggle = (id: string) =>
    setSelected((prev) => (prev.includes(id) ? prev.filter((value) => value !== id) : [...prev, id]));
  return (
    <PreviewCard
      title="Search filter pills preview"
      frameClassName="azf-showcase-preview__frame--compact"
      canvasClassName="azf-showcase-preview__canvas--intrinsic"
    >
      <div className="azf-showcase-filter-pills-demo" aria-label="Search filter pills example">
        <FilterPills
          pills={[
            { id: 'all', label: 'All' },
            { id: 'compute', label: 'Compute' },
            { id: 'storage', label: 'Storage' },
            { id: 'networking', label: 'Networking' },
          ]}
          selectedIds={selected}
          onToggle={toggle}
          overflowPills={[
            { id: 'databases', label: 'Databases' },
            { id: 'ai', label: 'AI + machine learning' },
            { id: 'security', label: 'Security' },
          ]}
        />
      </div>
    </PreviewCard>
  );
}

function ProviderPreview() {
  return (
    <PreviewCard title="Provider density preview" canvasClassName="azf-showcase-preview__canvas--intrinsic">
      <AzureFluentProvider density="cozy">
        <Card style={{ maxWidth: 320 }}>
          <CardHeader
            header={<Text weight="semibold">Cozy density surface</Text>}
            description={<Text className="azf-muted">Provider applies Azure tokens and density classes around product UI.</Text>}
          />
          <Button appearance="primary">Primary action</Button>
        </Card>
      </AzureFluentProvider>
    </PreviewCard>
  );
}

function IconRegistryPreview() {
  return (
    <PreviewCard title="Icon registry preview" canvasClassName="azf-showcase-preview__canvas--intrinsic">
      <AzureIconProvider registry={showcaseIconRegistry}>
        <div className="azf-showcase-inline-actions">
          <AzureIcon name="Storage/Storage Accounts" label="Storage account" size={24} />
          <AzureIcon name="Compute/Virtual Machine" label="Virtual machine" size={24} />
        </div>
      </AzureIconProvider>
    </PreviewCard>
  );
}

function IconActionButtonPreview() {
  return (
    <PreviewCard title="Icon action button preview" canvasClassName="azf-showcase-preview__canvas--intrinsic">
      <div className="azf-showcase-inline-actions">
        <IconActionButton id="refresh-icon" label="Refresh" icon={<ArrowClockwiseRegular />} onClick={() => undefined} />
        <IconActionButton id="delete-icon" label="Delete" icon={<DeleteRegular />} destructive onClick={() => undefined} />
        <IconActionButton id="loading-icon" label="Saving" loading onClick={() => undefined} />
      </div>
    </PreviewCard>
  );
}

function StatusIconTextPreview() {
  return (
    <PreviewCard title="Status icon text preview" canvasClassName="azf-showcase-preview__canvas--intrinsic">
      <div className="azf-showcase-form-column">
        <StatusIconText status="success">Backup completed</StatusIconText>
        <StatusIconText status="warning">Policy review needed</StatusIconText>
        <StatusIconText status="danger">Deployment blocked</StatusIconText>
      </div>
    </PreviewCard>
  );
}

function CopilotPromptRibbonPreview() {
  return (
    <PreviewCard title="Copilot prompt ribbon preview">
      <BladeHeader
        title="Sample resource"
        subtitle="Storage account"
        promptRibbon={(
          <CopilotPromptRibbon
            prompts={[
              { id: 'summarize', label: 'Summarize health' },
              { id: 'cost', label: 'Analyze cost' },
            ]}
          />
        )}
      />
    </PreviewCard>
  );
}

function DataToolbarPreview() {
  return (
    <PreviewCard title="Data toolbar preview">
      <DataToolbar
        title="Resource actions"
        actions={[
          { id: 'create', label: 'Create', appearance: 'primary', icon: <AddRegular />, onClick: () => undefined },
          { id: 'refresh', label: 'Refresh', icon: <ArrowClockwiseRegular />, onClick: () => undefined },
        ]}
      >
        <Badge appearance="outline">3 selected</Badge>
      </DataToolbar>
    </PreviewCard>
  );
}

function PortalCommandBarPreview() {
  return (
    <PreviewCard title="Portal command bar preview">
      <PortalCommandBar
        title="Portal commands"
        description="Use this export when product code names the command strip after the portal shell."
        primaryActions={[
          { id: 'save', label: 'Save', icon: <CheckmarkRegular />, onClick: () => undefined },
          { id: 'refresh', label: 'Refresh', icon: <ArrowClockwiseRegular />, onClick: () => undefined },
        ]}
      />
    </PreviewCard>
  );
}

function FilterBarPreview() {
  const [query, setQuery] = useState('east');

  return (
    <PreviewCard title="Filter bar preview">
      <FilterBar
        searchValue={query}
        onSearchChange={setQuery}
        searchPlaceholder="Search resources"
        filters={[
          { id: 'location', label: 'Location', value: 'East US', selected: true, removable: true, onRemove: () => undefined },
          { id: 'status', label: 'Status', value: 'Healthy', selected: false },
        ]}
      />
    </PreviewCard>
  );
}

function ArtifactPillPreview() {
  return (
    <PreviewCard title="Artifact pill preview" canvasClassName="azf-showcase-preview__canvas--intrinsic">
      <div className="azf-showcase-form-column">
        {chainOfThoughtArtifacts.slice(0, 2).map((artifact) => <ArtifactPill key={artifact.id} artifact={artifact} />)}
      </div>
    </PreviewCard>
  );
}

function ChainOfThoughtPreview() {
  return (
    <PreviewCard title="Reasoning panel preview">
      <ChainOfThought
        title="Reasoning"
        subtitle={`${chainOfThoughtArtifacts.length} artifacts created`}
        steps={chainOfThoughtSteps.map((step) => ({ ...step }))}
        artifacts={[...chainOfThoughtArtifacts]}
        onApprove={() => undefined}
        onDeny={() => undefined}
      />
    </PreviewCard>
  );
}

function HelpPopoverPreview() {
  return (
    <PreviewCard title="Help popover preview" canvasClassName="azf-showcase-preview__canvas--intrinsic">
      <HelpPopover
        trigger={<Button icon={<InfoRegular />}>Explain backup policy</Button>}
        title="Backup policy"
        body="Nightly snapshots keep recoverability available for sample accounts."
        actions={[{ id: 'learn-more', label: 'Learn more', onClick: () => undefined }]}
      />
    </PreviewCard>
  );
}

function CalloutPopoverPreview() {
  return (
    <PreviewCard title="Callout popover preview" canvasClassName="azf-showcase-preview__canvas--intrinsic">
      <CalloutPopover
        trigger={<Button appearance="primary">Review recommendation</Button>}
        title="Recommendation"
        body="Move the policy update before the rollout retry so the deployment can continue."
        tone="brand"
      />
    </PreviewCard>
  );
}

function DeleteConfirmationDialogPreview() {
  const [acknowledged, setAcknowledged] = useState(false);
  return (
    <PreviewCard title="Delete confirmation alias preview">
      <DeleteConfirmationDialog
        resourceName="resource-group-sample"
        confirmationText="Deleting this resource group removes all contained test resources."
        acknowledgement={{
          label: 'I understand the test resources will be removed.',
          checked: acknowledged,
          onChange: setAcknowledged,
        }}
        trigger={<Button appearance="outline" icon={<DeleteRegular />}>Open confirmation</Button>}
        onCancel={() => setAcknowledged(false)}
        onConfirm={() => undefined}
      />
    </PreviewCard>
  );
}

// ---------------------------------------------------------------------------
// Fluent 2 foundation components
//
// The Azure UI Kit is built on Fluent 2 (`@fluentui/react-components`). The base
// primitives on the Figma "Azure UI Kit (Fluent 2)" foundations page (node 25156-116,
// mirrored at https://fluent2.microsoft.design/components/web/react/) are consumed
// straight from Fluent v9, re-exported from `copilot-fluent-system/foundations.tsx`, and
// surfaced here as rendered examples merged into the same Components inventory so agents can
// discover and use them. Catalog table lives in `catalog/COMPONENTS.md`.
// ---------------------------------------------------------------------------
const FOUNDATION_ROW: React.CSSProperties = { display: 'flex', gap: 12, alignItems: 'center', flexWrap: 'wrap' };
const FOUNDATION_COL: React.CSSProperties = { display: 'flex', flexDirection: 'column', gap: 8, alignItems: 'flex-start', width: '100%', maxWidth: 320 };
const FOUNDATION_PAGE_URL = 'https://www.figma.com/design/q2TdO4dVcMhNWYp0N6Bc05/Azure-UI-Kit--Fluent-2-?node-id=25156-116&m=dev';
const FLUENT2_DOCS = 'https://fluent2.microsoft.design/components/web/react/';

interface FoundationSpec {
  exportName: string;
  title: string;
  summary: string;
  usageNotes: string[];
  preview: () => ReactElement;
}

const fluent2Foundations: FoundationSpec[] = [
  {
    exportName: 'Avatar',
    title: 'Avatar',
    summary: 'Represent a person or entity with an image, initials, or icon; add presence for status.',
    usageNotes: ['Fluent 2 base component consumed from @fluentui/react-components.', `Docs: ${FLUENT2_DOCS}avatar`],
    preview: () => (
      <div style={FOUNDATION_ROW}>
        <Avatar name="Cameron Evans" />
        <Avatar name="Kat Larsson" badge={{ status: 'available' }} />
        <AvatarGroup>
          <AvatarGroupItem name="Ana Bell" />
          <AvatarGroupItem name="Tim Deboer" />
          <AvatarGroupItem name="Mona Kane" />
        </AvatarGroup>
      </div>
    ),
  },
  {
    exportName: 'Badge',
    title: 'Badge',
    summary: 'Compact status, count, or presence markers on or beside another element.',
    usageNotes: ['Includes Badge, CounterBadge, and PresenceBadge.', `Docs: ${FLUENT2_DOCS}badge`],
    preview: () => (
      <div style={FOUNDATION_ROW}>
        <Badge appearance="filled" color="brand">New</Badge>
        <Badge appearance="tint" color="success">Healthy</Badge>
        <CounterBadge count={12} />
        <PresenceBadge status="available" />
      </div>
    ),
  },
  {
    exportName: 'Caption1',
    title: 'Caption',
    summary: 'Small supporting text for timestamps, metadata, and secondary labels.',
    usageNotes: ['Use for compact metadata; do not use as the only label for a form control.', `Docs: ${FLUENT2_DOCS}text`],
    preview: () => <Caption1>Last updated 2 minutes ago</Caption1>,
  },
  {
    exportName: 'Title2',
    title: 'Title 2',
    summary: 'Prominent page or section heading typography from Fluent 2.',
    usageNotes: ['Use sparingly for top-level task headings inside composed Azure surfaces.', `Docs: ${FLUENT2_DOCS}text`],
    preview: () => <Title2>Resource health summary</Title2>,
  },
  {
    exportName: 'Title3',
    title: 'Title 3',
    summary: 'Compact section heading typography used by dense Azure headers and panels.',
    usageNotes: ['Use for blade and panel headings that need emphasis without hero scale.', `Docs: ${FLUENT2_DOCS}text`],
    preview: () => <Title3>Networking policy review</Title3>,
  },
  {
    exportName: 'Accordion',
    title: 'Accordion foundation',
    summary: 'Fluent 2 disclosure sections for expandable content groups; prefer AzureAccordion for Azure-styled section lists.',
    usageNotes: ['Compose with AccordionItem, AccordionHeader, and AccordionPanel.', `Docs: ${FLUENT2_DOCS}accordion`],
    preview: () => (
      <Accordion multiple defaultOpenItems={['overview']} style={{ maxWidth: 320 }}>
        <AccordionItem value="overview">
          <AccordionHeader>Overview</AccordionHeader>
          <AccordionPanel>Cluster diagnostics are enabled for this sample resource.</AccordionPanel>
        </AccordionItem>
        <AccordionItem value="policy">
          <AccordionHeader>Policy</AccordionHeader>
          <AccordionPanel>One private endpoint policy requires review.</AccordionPanel>
        </AccordionItem>
      </Accordion>
    ),
  },
  {
    exportName: 'Breadcrumb',
    title: 'Breadcrumb',
    summary: 'Show hierarchical location and navigate back up a resource path.',
    usageNotes: ['Compose with BreadcrumbItem, BreadcrumbButton, BreadcrumbDivider.', `Docs: ${FLUENT2_DOCS}breadcrumb`],
    preview: () => (
      <Breadcrumb aria-label="Resource path">
        <BreadcrumbItem><BreadcrumbButton>Home</BreadcrumbButton></BreadcrumbItem>
        <BreadcrumbDivider />
        <BreadcrumbItem><BreadcrumbButton>Resource groups</BreadcrumbButton></BreadcrumbItem>
        <BreadcrumbDivider />
        <BreadcrumbItem><BreadcrumbButton current>rg-sample-eastus</BreadcrumbButton></BreadcrumbItem>
      </Breadcrumb>
    ),
  },
  {
    exportName: 'Card',
    title: 'Card',
    summary: 'Group related content and actions into a single contained surface.',
    usageNotes: ['Compose with CardHeader, CardFooter, CardPreview.', `Docs: ${FLUENT2_DOCS}card`],
    preview: () => (
      <Card style={{ maxWidth: 280 }}>
        <CardHeader
          header={<Text weight="semibold">Storage account</Text>}
          description={<Text className="azf-muted">storage-sample-eastus · Healthy</Text>}
        />
        <Text>2 containers · East US · Standard LRS</Text>
      </Card>
    ),
  },
  {
    exportName: 'Carousel',
    title: 'Carousel',
    summary: 'Cycle through a bounded set of DOM-rendered cards.',
    usageNotes: ['Fluent 2 Carousel with CarouselSlider/CarouselCard/CarouselNav.', `Docs: ${FLUENT2_DOCS}carousel`],
    preview: () => (
      <div style={FOUNDATION_ROW}>
        <Card style={{ width: 160 }}><Text weight="semibold">Getting started</Text><Text className="azf-muted">First guidance card</Text></Card>
        <Card style={{ width: 160 }}><Text weight="semibold">Best practices</Text><Text className="azf-muted">Second guidance card</Text></Card>
      </div>
    ),
  },
  {
    exportName: 'Checkbox',
    title: 'Checkbox',
    summary: 'Toggle an independent boolean option, or drive tri-state/mixed selection.',
    usageNotes: ['Supports checked, unchecked, and mixed states.', `Docs: ${FLUENT2_DOCS}checkbox`],
    preview: () => (
      <div style={FOUNDATION_COL}>
        <Checkbox defaultChecked label="Enable diagnostics" />
        <Checkbox label="Auto-shutdown" />
        <Checkbox checked="mixed" label="Selected tags" />
      </div>
    ),
  },
  {
    exportName: 'Combobox',
    title: 'Combobox',
    summary: 'Editable option picker for longer lists where users may type to narrow choices.',
    usageNotes: ['Use FilterableComboBox for Azure-specific labelled filtering; use Fluent Combobox for lower-level compositions.', `Docs: ${FLUENT2_DOCS}combobox`],
    preview: () => (
      <Field label="Subscription" style={{ maxWidth: 300 }}>
        <Combobox placeholder="Select a subscription">
          <Option>Sample subscription A</Option>
          <Option>Sample subscription B</Option>
          <Option>Shared platform services</Option>
        </Combobox>
      </Field>
    ),
  },
  {
    exportName: 'DatePicker',
    title: 'Date Picker',
    summary: 'Pick a calendar date. Not bundled by default — add @fluentui/react-datepicker-compat.',
    usageNotes: ['Add the compat package for the full calendar surface.', `Docs: ${FLUENT2_DOCS}date-picker`],
    preview: () => (
      <Field label="Start date" hint="Add @fluentui/react-datepicker-compat for the calendar surface">
        <Input contentBefore={<CalendarLtrRegular />} placeholder="mm/dd/yyyy" />
      </Field>
    ),
  },
  {
    exportName: 'Dialog',
    title: 'Dialog',
    summary: 'Modal or non-modal focused task and confirmation flows.',
    usageNotes: ['Compose with DialogSurface, DialogBody, DialogTitle, DialogActions.', `Docs: ${FLUENT2_DOCS}dialog`],
    preview: () => (
      <Dialog>
        <DialogTrigger disableButtonEnhancement>
          <Button appearance="primary">Delete resource…</Button>
        </DialogTrigger>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Delete rg-sample-eastus?</DialogTitle>
            <DialogContent>This permanently deletes the resource group and all resources in it.</DialogContent>
            <DialogActions>
              <DialogTrigger disableButtonEnhancement><Button>Cancel</Button></DialogTrigger>
              <Button appearance="primary">Delete</Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    ),
  },
  {
    exportName: 'Divider',
    title: 'Divider',
    summary: 'Separate content groups horizontally or vertically, optionally with a label.',
    usageNotes: ['Supports inset, vertical, and labelled variants.', `Docs: ${FLUENT2_DOCS}divider`],
    preview: () => (
      <div className="azf-showcase-divider-demo">
        <section className="azf-showcase-divider-demo__group" aria-label="Horizontal divider example">
          <div className="azf-showcase-divider-demo__content">
            <Text weight="semibold">Activity summary</Text>
            <Text className="azf-muted">Last deployment completed with two warnings.</Text>
          </div>
          <Divider />
          <div className="azf-showcase-divider-demo__content">
            <Text weight="semibold">Recommended action</Text>
            <Text className="azf-muted">Review the East US rollout before approving the next stage.</Text>
          </div>
        </section>
        <section className="azf-showcase-divider-demo__group" aria-label="Labeled divider example">
          <Divider>Review checkpoints</Divider>
          <div className="azf-showcase-divider-demo__toolbar">
            <Button appearance="subtle">Refresh</Button>
            <Divider vertical />
            <Button appearance="subtle">Export</Button>
            <Divider vertical />
            <Button appearance="subtle">Open logs</Button>
          </div>
        </section>
      </div>
    ),
  },
  {
    exportName: 'Drawer',
    title: 'Drawer',
    summary: 'Side panel for secondary content — overlay or inline.',
    usageNotes: ['Drawer, OverlayDrawer, InlineDrawer with header/body/footer slots.', `Docs: ${FLUENT2_DOCS}drawer`],
    preview: () => (
      <InlineDrawer separator open style={{ height: 200, maxWidth: 280 }}>
        <DrawerHeader>
          <DrawerHeaderTitle>Filters</DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          <Text>Filter resources by tag, location, and status.</Text>
        </DrawerBody>
        <DrawerFooter>
          <Button size="small">Apply filters</Button>
        </DrawerFooter>
      </InlineDrawer>
    ),
  },
  {
    exportName: 'Popover',
    title: 'Popover',
    summary: 'Anchored lightweight surface for contextual details and compact actions.',
    usageNotes: ['Compose with PopoverTrigger and PopoverSurface; use HelpPopover or CalloutPopover for Azure-specific guidance copy.', `Docs: ${FLUENT2_DOCS}popover`],
    preview: () => (
      <Popover open>
        <PopoverTrigger disableButtonEnhancement>
          <Button icon={<InfoRegular />}>Policy details</Button>
        </PopoverTrigger>
        <PopoverSurface>
          <Text>Private endpoint policies apply before the rollout continues.</Text>
        </PopoverSurface>
      </Popover>
    ),
  },
  {
    exportName: 'Dropdown',
    title: 'Dropdown',
    summary: 'Single or multi select from a list of options.',
    usageNotes: ['Compose with Option and OptionGroup; use FilterableComboBox when filtering.', `Docs: ${FLUENT2_DOCS}dropdown`],
    preview: () => (
      <Field label="Region" style={{ maxWidth: 280 }}>
        <Dropdown placeholder="Select a region">
          <Option>East US</Option>
          <Option>West US</Option>
          <Option>North Europe</Option>
        </Dropdown>
      </Field>
    ),
  },
  {
    exportName: 'Field',
    title: 'Field',
    summary: 'Wrap an input with label, hint, validation message, and required state.',
    usageNotes: ['Foundation for Azure FormFieldRow.', `Docs: ${FLUENT2_DOCS}field`],
    preview: () => (
      <div style={FOUNDATION_COL}>
        <Field label="Resource name" hint="Lowercase letters, numbers, and hyphens" required>
          <Input placeholder="my-resource" />
        </Field>
        <Field label="Subscription" validationState="error" validationMessage="Subscription is required">
          <Input />
        </Field>
      </div>
    ),
  },
  {
    exportName: 'InfoLabel',
    title: 'Info label',
    summary: 'A label with an attached info tooltip/popover for supplementary guidance.',
    usageNotes: ['Use for form fields that need inline help.', `Docs: ${FLUENT2_DOCS}infolabel`],
    preview: () => (
      <InfoLabel info="Billed per hour of allocated capacity, prorated to the second.">
        Pricing tier
      </InfoLabel>
    ),
  },
  {
    exportName: 'Input',
    title: 'Input',
    summary: 'Single-line text entry with optional content-before/after slots.',
    usageNotes: ['Supports sizes, appearances, and content slots.', `Docs: ${FLUENT2_DOCS}input`],
    preview: () => (
      <div style={FOUNDATION_COL}>
        <Input placeholder="Resource name" />
        <Input contentBefore={<SearchRegular />} placeholder="Search" />
        <Input disabled defaultValue="Read only" />
      </div>
    ),
  },
  {
    exportName: 'Label',
    title: 'Label',
    summary: 'Accessible caption for a form control.',
    usageNotes: ['Pair with Input, Dropdown, and other controls.', `Docs: ${FLUENT2_DOCS}label`],
    preview: () => (
      <div style={FOUNDATION_COL}>
        <Label required>Resource group</Label>
        <Label weight="semibold">Location</Label>
        <Label disabled>Disabled label</Label>
      </div>
    ),
  },
  {
    exportName: 'Link',
    title: 'Link',
    summary: 'Inline or standalone navigation/action hyperlink.',
    usageNotes: ['Renders as anchor or button depending on props.', `Docs: ${FLUENT2_DOCS}link`],
    preview: () => (
      <div style={FOUNDATION_COL}>
        <Link href="#foundation" onClick={(event) => event.preventDefault()}>Open documentation</Link>
        <Link href="#foundation" inline onClick={(event) => event.preventDefault()}>Inline link within text</Link>
      </div>
    ),
  },
  {
    exportName: 'List',
    title: 'List',
    summary: 'Semantic, optionally navigable or selectable vertical lists.',
    usageNotes: ['Compose with ListItem; supports selection.', `Docs: ${FLUENT2_DOCS}list`],
    preview: () => (
      <List style={{ maxWidth: 280 }}>
        <ListItem>vm-sample-01 · Running</ListItem>
        <ListItem>vm-sample-02 · Stopped</ListItem>
        <ListItem>vm-staging-01 · Running</ListItem>
      </List>
    ),
  },
  {
    exportName: 'Menu',
    title: 'Menu',
    summary: 'Contextual command, overflow, and split-button menus.',
    usageNotes: ['Compose with MenuTrigger, MenuPopover, MenuList, MenuItem.', `Docs: ${FLUENT2_DOCS}menu`],
    preview: () => (
      <Menu>
        <MenuTrigger disableButtonEnhancement>
          <Button icon={<MoreHorizontalRegular />}>Actions</Button>
        </MenuTrigger>
        <MenuPopover>
          <MenuList>
            <MenuGroup>
              <MenuGroupHeader>Resource actions</MenuGroupHeader>
              <MenuItem icon={<ArrowClockwiseRegular />}>Restart</MenuItem>
              <MenuDivider />
              <MenuItem icon={<DeleteRegular />}>Delete</MenuItem>
            </MenuGroup>
          </MenuList>
        </MenuPopover>
      </Menu>
    ),
  },
  {
    exportName: 'MessageBar',
    title: 'Message bar',
    summary: 'Inline info/success/warning/error messaging with optional actions.',
    usageNotes: ['Compose with MessageBarBody, MessageBarTitle, MessageBarActions.', `Docs: ${FLUENT2_DOCS}messagebar`],
    preview: () => (
      <div style={FOUNDATION_COL}>
        <MessageBar intent="success">
          <MessageBarBody><MessageBarTitle>Deployed</MessageBarTitle> vm-sample-01 is running.</MessageBarBody>
        </MessageBar>
        <MessageBar intent="warning">
          <MessageBarBody><MessageBarTitle>Action needed</MessageBarTitle> Renew the TLS certificate.</MessageBarBody>
          <MessageBarActions><Button size="small">Renew</Button></MessageBarActions>
        </MessageBar>
      </div>
    ),
  },
  {
    exportName: 'Nav',
    title: 'Nav',
    summary: 'Primary app/service navigation; use the Azure rail and service menu for portal shells.',
    usageNotes: ['Fluent NavDrawer/NavItem; Azure PortalRail and ServiceMenu build on the same idea.', `Docs: ${FLUENT2_DOCS}nav`],
    preview: () => (
      <NavDrawer open type="inline" selectedValue="overview" density="small" style={{ height: 220 }}>
        <NavDrawerBody>
          <NavItem icon={<HomeRegular />} value="overview">Overview</NavItem>
          <NavItem icon={<DataTrendingRegular />} value="metrics">Metrics</NavItem>
          <NavItem icon={<SettingsRegular />} value="settings">Settings</NavItem>
        </NavDrawerBody>
      </NavDrawer>
    ),
  },
  {
    exportName: 'Persona',
    title: 'Persona',
    summary: 'Avatar plus primary/secondary text for a person or entity.',
    usageNotes: ['Combines Avatar with name and presence.', `Docs: ${FLUENT2_DOCS}persona`],
    preview: () => (
      <Persona
        name="Kat Larsson"
        secondaryText="Subscription owner"
        presence={{ status: 'available' }}
        avatar={{ color: 'colorful' }}
      />
    ),
  },
  {
    exportName: 'RadioGroup',
    title: 'Radio group',
    summary: 'Choose exactly one option from a small mutually-exclusive set.',
    usageNotes: ['Compose RadioGroup with Radio.', `Docs: ${FLUENT2_DOCS}radio`],
    preview: () => (
      <Field label="Redundancy">
        <RadioGroup defaultValue="lrs">
          <Radio value="lrs" label="Locally redundant (LRS)" />
          <Radio value="grs" label="Geo-redundant (GRS)" />
          <Radio value="zrs" label="Zone-redundant (ZRS)" />
        </RadioGroup>
      </Field>
    ),
  },
  {
    exportName: 'Rating',
    title: 'Rating',
    summary: 'Collect or display a star (or custom) rating value.',
    usageNotes: ['Rating for input, RatingDisplay for read-only summaries.', `Docs: ${FLUENT2_DOCS}rating`],
    preview: () => (
      <div style={FOUNDATION_COL}>
        <Rating defaultValue={3} />
        <RatingDisplay value={4} count={128} />
      </div>
    ),
  },
  {
    exportName: 'SearchBox',
    title: 'Search box',
    summary: 'Text input specialized for search, with a clear affordance.',
    usageNotes: ['Includes a dismiss/clear button.', `Docs: ${FLUENT2_DOCS}searchbox`],
    preview: () => <SearchBox placeholder="Search resources" style={{ maxWidth: 280 }} />,
  },
  {
    exportName: 'Select',
    title: 'Select',
    summary: 'Native select control for short, simple option lists.',
    usageNotes: ['Use when browser-native select behavior is sufficient; prefer Dropdown or Combobox for richer option content.', `Docs: ${FLUENT2_DOCS}select`],
    preview: () => (
      <Field label="Region" style={{ maxWidth: 260 }}>
        <Select defaultValue="eastus">
          <option value="eastus">East US</option>
          <option value="westus">West US</option>
          <option value="northeurope">North Europe</option>
        </Select>
      </Field>
    ),
  },
  {
    exportName: 'Skeleton',
    title: 'Skeleton',
    summary: 'Loading placeholders that mirror eventual content shape.',
    usageNotes: ['Compose with SkeletonItem; supports shimmer animation.', `Docs: ${FLUENT2_DOCS}skeleton`],
    preview: () => (
      <Skeleton aria-label="Loading resource" style={{ maxWidth: 280 }}>
        <div style={FOUNDATION_COL}>
          <SkeletonItem />
          <SkeletonItem size={16} />
          <SkeletonItem size={16} />
        </div>
      </Skeleton>
    ),
  },
  {
    exportName: 'SpinButton',
    title: 'Spin button',
    summary: 'Numeric entry with increment/decrement steppers.',
    usageNotes: ['Supports min, max, and step.', `Docs: ${FLUENT2_DOCS}spinbutton`],
    preview: () => (
      <Field label="Instance count">
        <SpinButton defaultValue={3} min={1} max={10} />
      </Field>
    ),
  },
  {
    exportName: 'Spinner',
    title: 'Spinner',
    summary: 'Indeterminate loading indicator with an optional label.',
    usageNotes: ['Sizes from tiny to huge; supports inline labels.', `Docs: ${FLUENT2_DOCS}spinner`],
    preview: () => (
      <div style={FOUNDATION_ROW}>
        <Spinner size="tiny" />
        <Spinner label="Provisioning…" />
      </div>
    ),
  },
  {
    exportName: 'SwatchPicker',
    title: 'Swatch picker',
    summary: 'Choose from a palette of color (or other) swatches.',
    usageNotes: ['Compose with ColorSwatch/ImageSwatch.', `Docs: ${FLUENT2_DOCS}swatchpicker`],
    preview: () => (
      <SwatchPicker defaultSelectedValue="brand" aria-label="Tag color">
        <ColorSwatch color="#0f6cbd" value="brand" aria-label="Brand blue" />
        <ColorSwatch color="#107c10" value="green" aria-label="Green" />
        <ColorSwatch color="#c50f1f" value="red" aria-label="Red" />
      </SwatchPicker>
    ),
  },
  {
    exportName: 'Switch',
    title: 'Switch',
    summary: 'Toggle a single setting on/off with immediate effect.',
    usageNotes: ['Use for settings applied instantly.', `Docs: ${FLUENT2_DOCS}switch`],
    preview: () => (
      <div style={FOUNDATION_COL}>
        <Switch defaultChecked label="Auto-scale" />
        <Switch label="Public network access" />
      </div>
    ),
  },
  {
    exportName: 'Tag',
    title: 'Tag & interaction tag',
    summary: 'Read-only or interactive labels/filters, dismissible or selectable.',
    usageNotes: ['Tag, InteractionTag, TagGroup.', `Docs: ${FLUENT2_DOCS}tag`],
    preview: () => (
      <TagGroup aria-label="Resource tags">
        <Tag>env: sample</Tag>
        <Tag dismissible>owner: platform</Tag>
        <InteractionTag><InteractionTagPrimary>East US</InteractionTagPrimary></InteractionTag>
      </TagGroup>
    ),
  },
  {
    exportName: 'TabList',
    title: 'Tab list foundation',
    summary: 'Fluent 2 tabs for switching related panels; prefer AzureTabList when Azure validation/status treatment is required.',
    usageNotes: ['Compose TabList with Tab children and keep tab labels short.', `Docs: ${FLUENT2_DOCS}tablist`],
    preview: () => (
      <TabList defaultSelectedValue="overview">
        <Tab value="overview">Overview</Tab>
        <Tab value="activity">Activity</Tab>
        <Tab value="settings">Settings</Tab>
      </TabList>
    ),
  },
  {
    exportName: 'TagPicker',
    title: 'Tag picker',
    summary: 'Combobox that resolves free text into a set of tags.',
    usageNotes: ['Fluent 2 TagPicker composes a search input with a tag list.', `Docs: ${FLUENT2_DOCS}tagpicker`],
    preview: () => (
      <Field label="Regions" style={{ maxWidth: 300 }}>
        <div style={FOUNDATION_COL}>
          <SearchBox placeholder="Add a region" />
          <TagGroup aria-label="Selected regions">
            <Tag dismissible>East US</Tag>
            <Tag dismissible>West US</Tag>
          </TagGroup>
        </div>
      </Field>
    ),
  },
  {
    exportName: 'Table',
    title: 'Table foundation',
    summary: 'Semantic Fluent table primitives for compact structured data.',
    usageNotes: ['Use AzureDataGrid for sortable Azure resource lists; use Table when composing small static tabular summaries.', `Docs: ${FLUENT2_DOCS}table`],
    preview: () => (
      <Table aria-label="Resource status summary" size="small" style={{ maxWidth: 420 }}>
        <TableHeader>
          <TableRow>
            <TableHeaderCell>Resource</TableHeaderCell>
            <TableHeaderCell>Status</TableHeaderCell>
          </TableRow>
        </TableHeader>
        <TableBody>
          <TableRow>
            <TableCell><TableCellLayout>aks-cluster-alpha</TableCellLayout></TableCell>
            <TableCell><Badge appearance="tint" color="success">Running</Badge></TableCell>
          </TableRow>
          <TableRow>
            <TableCell><TableCellLayout>aks-cluster-beta</TableCellLayout></TableCell>
            <TableCell><Badge appearance="tint" color="warning">Review</Badge></TableCell>
          </TableRow>
        </TableBody>
      </Table>
    ),
  },
  {
    exportName: 'TeachingPopover',
    title: 'Teaching popover',
    summary: 'Coach-mark / onboarding callout anchored to a target element.',
    usageNotes: ['Compose with TeachingPopoverSurface/Header/Body/Title.', `Docs: ${FLUENT2_DOCS}teachingpopover`],
    preview: () => (
      <TeachingPopover>
        <TeachingPopoverTrigger>
          <Button icon={<InfoRegular />}>Show tip</Button>
        </TeachingPopoverTrigger>
        <TeachingPopoverSurface>
          <TeachingPopoverHeader>Onboarding</TeachingPopoverHeader>
          <TeachingPopoverBody>
            <TeachingPopoverTitle>Filter faster</TeachingPopoverTitle>
            <Text>Use the command bar to filter resources by tag.</Text>
          </TeachingPopoverBody>
        </TeachingPopoverSurface>
      </TeachingPopover>
    ),
  },
  {
    exportName: 'Textarea',
    title: 'Text area',
    summary: 'Multi-line text entry, optionally auto-resizing.',
    usageNotes: ['Supports resize modes and sizes.', `Docs: ${FLUENT2_DOCS}textarea`],
    preview: () => (
      <Field label="Description" style={{ maxWidth: 300 }}>
        <Textarea placeholder="Describe this resource…" resize="vertical" />
      </Field>
    ),
  },
  {
    exportName: 'Toast',
    title: 'Toast',
    summary: 'Transient, non-blocking notifications dispatched imperatively.',
    usageNotes: ['Dispatch with Toaster + useToastController at runtime.', `Docs: ${FLUENT2_DOCS}toast`],
    preview: () => (
      <Toast style={{ maxWidth: 300 }}>
        <ToastTitle>Deployment complete</ToastTitle>
        <ToastBody>vm-sample-01 started successfully.</ToastBody>
      </Toast>
    ),
  },
  {
    exportName: 'Toolbar',
    title: 'Toolbar',
    summary: 'Grouped command surfaces; use the Azure command bar/data toolbar for portal grids.',
    usageNotes: ['Toolbar, ToolbarButton, ToolbarDivider, ToolbarGroup.', `Docs: ${FLUENT2_DOCS}toolbar`],
    preview: () => (
      <Toolbar aria-label="Resource actions">
        <ToolbarButton icon={<AddRegular />}>Create</ToolbarButton>
        <ToolbarButton icon={<EditRegular />}>Edit</ToolbarButton>
        <ToolbarDivider />
        <ToolbarButton icon={<DeleteRegular />}>Delete</ToolbarButton>
      </Toolbar>
    ),
  },
  {
    exportName: 'Tooltip',
    title: 'Tooltip',
    summary: 'Brief hover/focus description for a control.',
    usageNotes: ['Requires a relationship and a single focusable child.', `Docs: ${FLUENT2_DOCS}tooltip`],
    preview: () => (
      <Tooltip content="Refresh the resource list" relationship="label">
        <Button icon={<ArrowClockwiseRegular />}>Refresh</Button>
      </Tooltip>
    ),
  },
  {
    exportName: 'Tree',
    title: 'Tree',
    summary: 'Hierarchical, expandable navigation or data structures.',
    usageNotes: ['Tree, TreeItem, TreeItemLayout, FlatTree.', `Docs: ${FLUENT2_DOCS}tree`],
    preview: () => (
      <Tree aria-label="Resource hierarchy" defaultOpenItems={['sub']} style={{ maxWidth: 300 }}>
        <TreeItem itemType="branch" value="sub">
          <TreeItemLayout>Subscription</TreeItemLayout>
          <Tree>
            <TreeItem itemType="leaf"><TreeItemLayout>rg-sample-eastus</TreeItemLayout></TreeItem>
            <TreeItem itemType="leaf"><TreeItemLayout>rg-staging</TreeItemLayout></TreeItem>
          </Tree>
        </TreeItem>
      </Tree>
    ),
  },
];


function reuseFoundationPreview(exportName: string): () => ReactElement {
  return () => {
    const Preview = fluent2Foundations.find((spec) => spec.exportName === exportName)?.preview;
    return Preview ? <Preview /> : <Text>No preview available.</Text>;
  };
}

const foundationCompanionSpecs: FoundationSpec[] = [
  { exportName: 'AccordionHeader', title: 'Accordion header', summary: 'Header control used in the Accordion foundation example.', usageNotes: ['Use inside AccordionItem to toggle the associated panel.'], preview: reuseFoundationPreview('Accordion') },
  { exportName: 'AccordionItem', title: 'Accordion item', summary: 'Item container used in the Accordion foundation example.', usageNotes: ['Use for each independently expandable section.'], preview: reuseFoundationPreview('Accordion') },
  { exportName: 'AccordionPanel', title: 'Accordion panel', summary: 'Panel region used in the Accordion foundation example.', usageNotes: ['Keep expanded content concise and related to the header.'], preview: reuseFoundationPreview('Accordion') },
  { exportName: 'CounterBadge', title: 'Counter badge', summary: 'Count badge used in the Badge foundation example.', usageNotes: ['Use with Badge for compact counts and notifications.'], preview: reuseFoundationPreview('Badge') },
  { exportName: 'PresenceBadge', title: 'Presence badge', summary: 'Presence indicator used in the Badge and Avatar foundation examples.', usageNotes: ['Use beside people or agents when availability matters.'], preview: reuseFoundationPreview('Badge') },
  { exportName: 'BreadcrumbItem', title: 'Breadcrumb item', summary: 'Breadcrumb item used in the Breadcrumb foundation example.', usageNotes: ['Compose inside Breadcrumb to describe one level of location.'], preview: reuseFoundationPreview('Breadcrumb') },
  { exportName: 'BreadcrumbButton', title: 'Breadcrumb button', summary: 'Breadcrumb link/button used in the Breadcrumb foundation example.', usageNotes: ['Use for navigable ancestors in a resource path.'], preview: reuseFoundationPreview('Breadcrumb') },
  { exportName: 'BreadcrumbDivider', title: 'Breadcrumb divider', summary: 'Separator used in the Breadcrumb foundation example.', usageNotes: ['Use between breadcrumb levels; do not replace with custom text separators.'], preview: reuseFoundationPreview('Breadcrumb') },
  { exportName: 'NavDrawer', title: 'Navigation drawer', summary: 'Navigation drawer used in the Nav foundation example.', usageNotes: ['Use for primary app or service navigation when a portal rail is not appropriate.'], preview: reuseFoundationPreview('Nav') },
  { exportName: 'NavItem', title: 'Navigation item', summary: 'Navigation row used in the Nav foundation example.', usageNotes: ['Use inside NavDrawer for destinations and selected state.'], preview: reuseFoundationPreview('Nav') },
  { exportName: 'NavCategory', title: 'Navigation category', summary: 'Navigation grouping component for larger nav drawers.', usageNotes: ['Use to group related navigation destinations when the drawer grows.'], preview: reuseFoundationPreview('Nav') },
  { exportName: 'CardHeader', title: 'Card header', summary: 'Header region used in the Card foundation example.', usageNotes: ['Use for title and supporting description inside a card.'], preview: reuseFoundationPreview('Card') },
  { exportName: 'CardFooter', title: 'Card footer', summary: 'Footer region for card-level actions.', usageNotes: ['Use for actions that belong to the card, not the whole page.'], preview: reuseFoundationPreview('Card') },
  { exportName: 'CardPreview', title: 'Card preview', summary: 'Media or visual preview region for cards.', usageNotes: ['Use when a card needs a bounded visual preview.'], preview: reuseFoundationPreview('Card') },
  { exportName: 'CarouselCard', title: 'Carousel card', summary: 'Card item used with Carousel.', usageNotes: ['Use inside carousel regions for bounded instructional content.'], preview: reuseFoundationPreview('Carousel') },
  { exportName: 'OverlayDrawer', title: 'Overlay drawer', summary: 'Overlay side panel variant for temporary secondary content.', usageNotes: ['Use when the drawer should cover content temporarily.'], preview: reuseFoundationPreview('Drawer') },
  { exportName: 'InlineDrawer', title: 'Inline drawer', summary: 'Inline side panel variant shown in the Drawer foundation example.', usageNotes: ['Use when the drawer participates in the page layout.'], preview: reuseFoundationPreview('Drawer') },
  { exportName: 'DrawerHeader', title: 'Drawer header', summary: 'Header region shown in the Drawer foundation example.', usageNotes: ['Use for drawer titles and close affordances.'], preview: reuseFoundationPreview('Drawer') },
  { exportName: 'DrawerBody', title: 'Drawer body', summary: 'Content region shown in the Drawer foundation example.', usageNotes: ['Use for drawer content that supports the current task.'], preview: reuseFoundationPreview('Drawer') },
  { exportName: 'DrawerFooter', title: 'Drawer footer', summary: 'Footer region for drawer actions.', usageNotes: ['Use for actions scoped to the drawer content.'], preview: reuseFoundationPreview('Drawer') },
  { exportName: 'DrawerHeaderTitle', title: 'Drawer header title', summary: 'Title control for drawer headers.', usageNotes: ['Use inside DrawerHeader when the drawer needs a title and close affordance.'], preview: reuseFoundationPreview('Drawer') },
  { exportName: 'DialogSurface', title: 'Dialog surface', summary: 'Dialog surface used in the Dialog foundation example.', usageNotes: ['Use as the modal surface that contains a focused task.'], preview: reuseFoundationPreview('Dialog') },
  { exportName: 'DialogBody', title: 'Dialog body', summary: 'Dialog body used in the Dialog foundation example.', usageNotes: ['Use to structure title, content, and actions.'], preview: reuseFoundationPreview('Dialog') },
  { exportName: 'DialogTitle', title: 'Dialog title', summary: 'Dialog title used in the Dialog foundation example.', usageNotes: ['Keep titles concise and specific to the decision.'], preview: reuseFoundationPreview('Dialog') },
  { exportName: 'DialogActions', title: 'Dialog actions', summary: 'Dialog action row used in the Dialog foundation example.', usageNotes: ['Keep primary and secondary choices clear.'], preview: reuseFoundationPreview('Dialog') },
  { exportName: 'DialogContent', title: 'Dialog content', summary: 'Dialog content region used in the Dialog foundation example.', usageNotes: ['Use for concise supporting explanation.'], preview: reuseFoundationPreview('Dialog') },
  { exportName: 'DialogTrigger', title: 'Dialog trigger', summary: 'Dialog trigger used in the Dialog foundation example.', usageNotes: ['Use a clear button or link to open the dialog.'], preview: reuseFoundationPreview('Dialog') },
  { exportName: 'PopoverSurface', title: 'Popover surface', summary: 'Anchored surface used in the Popover foundation example.', usageNotes: ['Keep content lightweight and contextual to the trigger.'], preview: reuseFoundationPreview('Popover') },
  { exportName: 'PopoverTrigger', title: 'Popover trigger', summary: 'Trigger used in the Popover foundation example.', usageNotes: ['Use a clear button or link as the anchor.'], preview: reuseFoundationPreview('Popover') },
  { exportName: 'Option', title: 'Option', summary: 'Selectable option used in Dropdown examples.', usageNotes: ['Use inside Dropdown or FilterableComboBox option lists.'], preview: reuseFoundationPreview('Dropdown') },
  { exportName: 'OptionGroup', title: 'Option group', summary: 'Grouping component for option lists.', usageNotes: ['Use when a long option list benefits from labeled groups.'], preview: reuseFoundationPreview('Dropdown') },
  { exportName: 'Radio', title: 'Radio', summary: 'Single choice item used in the Radio group foundation example.', usageNotes: ['Use inside RadioGroup for mutually exclusive choices.'], preview: reuseFoundationPreview('RadioGroup') },
  { exportName: 'RatingDisplay', title: 'Rating display', summary: 'Read-only rating summary shown in the Rating foundation example.', usageNotes: ['Use when summarizing a rating rather than collecting input.'], preview: reuseFoundationPreview('Rating') },
  { exportName: 'ColorSwatch', title: 'Color swatch', summary: 'Color option used in the Swatch picker foundation example.', usageNotes: ['Use inside SwatchPicker for color choices.'], preview: reuseFoundationPreview('SwatchPicker') },
  { exportName: 'ListItem', title: 'List item', summary: 'List row used in the List foundation example.', usageNotes: ['Use inside List for semantic vertical lists.'], preview: reuseFoundationPreview('List') },
  { exportName: 'SkeletonItem', title: 'Skeleton item', summary: 'Loading placeholder item used in the Skeleton foundation example.', usageNotes: ['Use to mirror the shape of loading content.'], preview: reuseFoundationPreview('Skeleton') },
  { exportName: 'InteractionTag', title: 'Interaction tag', summary: 'Interactive tag used in the Tag foundation example.', usageNotes: ['Use when a tag acts as a selectable or clickable affordance.'], preview: reuseFoundationPreview('Tag') },
  { exportName: 'TagGroup', title: 'Tag group', summary: 'Group container used in the Tag foundation example.', usageNotes: ['Use to keep related tags announced together.'], preview: reuseFoundationPreview('Tag') },
  { exportName: 'TagPickerControl', title: 'Tag picker control', summary: 'Control region for tag-picking experiences.', usageNotes: ['Use with TagPicker when building a full tokenized picker.'], preview: reuseFoundationPreview('TagPicker') },
  { exportName: 'Tab', title: 'Tab', summary: 'Tab item used in the Tab list foundation example.', usageNotes: ['Use inside TabList for each switchable panel label.'], preview: reuseFoundationPreview('TabList') },
  { exportName: 'TableBody', title: 'Table body', summary: 'Body region used in the Table foundation example.', usageNotes: ['Use for data rows inside Table.'], preview: reuseFoundationPreview('Table') },
  { exportName: 'TableCell', title: 'Table cell', summary: 'Cell used in the Table foundation example.', usageNotes: ['Use inside TableRow for cell content.'], preview: reuseFoundationPreview('Table') },
  { exportName: 'TableCellLayout', title: 'Table cell layout', summary: 'Cell content layout used in the Table foundation example.', usageNotes: ['Use to align icons, media, or primary text inside table cells.'], preview: reuseFoundationPreview('Table') },
  { exportName: 'TableHeader', title: 'Table header', summary: 'Header region used in the Table foundation example.', usageNotes: ['Use for column headers.'], preview: reuseFoundationPreview('Table') },
  { exportName: 'TableHeaderCell', title: 'Table header cell', summary: 'Header cell used in the Table foundation example.', usageNotes: ['Use inside TableHeader rows for column labels.'], preview: reuseFoundationPreview('Table') },
  { exportName: 'TableRow', title: 'Table row', summary: 'Row used in the Table foundation example.', usageNotes: ['Use inside TableHeader or TableBody.'], preview: reuseFoundationPreview('Table') },
  { exportName: 'TreeItem', title: 'Tree item', summary: 'Tree row used in the Tree foundation example.', usageNotes: ['Use inside Tree or FlatTree for hierarchical items.'], preview: reuseFoundationPreview('Tree') },
  { exportName: 'FlatTree', title: 'Flat tree', summary: 'Flat data variant of the Tree foundation component.', usageNotes: ['Use when tree data is already flattened for rendering.'], preview: reuseFoundationPreview('Tree') },
  { exportName: 'MenuTrigger', title: 'Menu trigger', summary: 'Trigger used in the Menu foundation example.', usageNotes: ['Use to anchor contextual menus to a button or split-button affordance.'], preview: reuseFoundationPreview('Menu') },
  { exportName: 'MenuList', title: 'Menu list', summary: 'Menu list used in the Menu foundation example.', usageNotes: ['Use to contain menu items inside the popover.'], preview: reuseFoundationPreview('Menu') },
  { exportName: 'MenuItem', title: 'Menu item', summary: 'Command row used in the Menu foundation example.', usageNotes: ['Use for contextual commands and overflow actions.'], preview: reuseFoundationPreview('Menu') },
  { exportName: 'MenuDivider', title: 'Menu divider', summary: 'Divider used to separate groups inside menus.', usageNotes: ['Use sparingly between related command groups.'], preview: reuseFoundationPreview('Menu') },
  { exportName: 'MenuGroup', title: 'Menu group', summary: 'Grouping container for menu commands.', usageNotes: ['Use when commands need an announced group.'], preview: reuseFoundationPreview('Menu') },
  { exportName: 'MenuGroupHeader', title: 'Menu group header', summary: 'Label for a grouped set of menu commands.', usageNotes: ['Use inside MenuGroup to name a command set.'], preview: reuseFoundationPreview('Menu') },
  { exportName: 'MenuPopover', title: 'Menu popover', summary: 'Popover surface used in the Menu foundation example.', usageNotes: ['Use as the menu surface anchored to the trigger.'], preview: reuseFoundationPreview('Menu') },
  { exportName: 'MessageBarBody', title: 'Message bar body', summary: 'Message content region used in the Message bar foundation example.', usageNotes: ['Use for the primary message copy.'], preview: reuseFoundationPreview('MessageBar') },
  { exportName: 'MessageBarActions', title: 'Message bar actions', summary: 'Action region used in the Message bar foundation example.', usageNotes: ['Use for contextual next steps.'], preview: reuseFoundationPreview('MessageBar') },
  { exportName: 'MessageBarTitle', title: 'Message bar title', summary: 'Message title used in the Message bar foundation example.', usageNotes: ['Use a short title to make the message scannable.'], preview: reuseFoundationPreview('MessageBar') },
  { exportName: 'ToastBody', title: 'Toast body', summary: 'Supporting text region used in the Toast foundation example.', usageNotes: ['Use for brief secondary detail under the toast title.'], preview: reuseFoundationPreview('Toast') },
  { exportName: 'ToastTitle', title: 'Toast title', summary: 'Toast title used in the Toast foundation example.', usageNotes: ['Use for short notification headlines.'], preview: reuseFoundationPreview('Toast') },
  { exportName: 'Toaster', title: 'Toaster', summary: 'Runtime host for toast notifications.', usageNotes: ['Place once near the application root when dispatching toasts.'], preview: reuseFoundationPreview('Toast') },
  { exportName: 'ToolbarButton', title: 'Toolbar button', summary: 'Command button used in the Toolbar foundation example.', usageNotes: ['Use inside Toolbar for related command groups.'], preview: reuseFoundationPreview('Toolbar') },
  { exportName: 'ToolbarDivider', title: 'Toolbar divider', summary: 'Divider used in the Toolbar foundation example.', usageNotes: ['Use sparingly to separate command groups.'], preview: reuseFoundationPreview('Toolbar') },
];

const compositeChildFoundationExportNames = new Set([
  'AccordionHeader',
  'AccordionItem',
  'AccordionPanel',
  'BreadcrumbButton',
  'BreadcrumbDivider',
  'BreadcrumbItem',
  'CardFooter',
  'CardHeader',
  'CardPreview',
  'CarouselCard',
  'DialogActions',
  'DialogBody',
  'DialogContent',
  'DialogSurface',
  'DialogTitle',
  'DialogTrigger',
  'DrawerBody',
  'DrawerFooter',
  'DrawerHeader',
  'DrawerHeaderTitle',
  'InlineDrawer',
  'MenuDivider',
  'MenuGroup',
  'MenuGroupHeader',
  'MenuItem',
  'MenuList',
  'MenuPopover',
  'MenuTrigger',
  'MessageBarActions',
  'MessageBarBody',
  'MessageBarTitle',
  'Option',
  'OptionGroup',
  'OverlayDrawer',
  'PopoverSurface',
  'PopoverTrigger',
  'Tab',
  'TableBody',
  'TableCell',
  'TableCellLayout',
  'TableHeader',
  'TableHeaderCell',
  'TableRow',
  'TagPickerControl',
  'ToastBody',
  'ToastTitle',
  'ToolbarButton',
  'ToolbarDivider',
]);
const publicFoundationExportNames = new Set(fluent2Foundations.map((spec) => spec.exportName));
const allFluent2Foundations: FoundationSpec[] = [...fluent2Foundations, ...foundationCompanionSpecs];

const componentCatalog: ComponentPreviewEntry[] = [
  {
    exportName: 'AzureFluentProvider',
    title: 'Azure Fluent provider',
    category: 'System setup',
    summary: 'Wrap product UI with Azure Fluent tokens, theme, and density settings.',
    usageNotes: [
      'Place it near the application or showcase root so all child components share the same theme contract.',
      'Use compact density for dense portal surfaces and cozy density for more spacious task flows.',
    ],
    preview: ProviderPreview,
  },
  {
    exportName: 'AzureIcon',
    title: 'Azure icon',
    category: 'System setup',
    summary: 'Show registered Azure resource glyphs at supported portal sizes.',
    usageNotes: [
      'Wrap product screens in AzureIconProvider when icons come from a shared registry.',
      'Keep labels on meaningful icons and mark purely decorative icons as decorative.',
    ],
    preview: IconRegistryPreview,
  },
  {
    exportName: 'AzureIconProvider',
    title: 'Azure icon provider',
    category: 'System setup',
    summary: 'AzureIconProvider supplies the icon registry consumed by AzureIcon in product screens.',
    usageNotes: [
      'Use the provider for resource-specific icon sets instead of passing image URLs through every component.',
      'Registry helper functions are code utilities and are documented in source; the provider and AzureIcon are the visible UI pieces.',
    ],
    preview: IconRegistryPreview,
  },
  {
    exportName: 'IconActionButton',
    title: 'Icon action button',
    category: 'Core controls',
    summary: 'Use compact, tooltip-backed icon commands in headers, rails, and dense action rows.',
    usageNotes: [
      'Use for secondary actions where the icon is familiar and the tooltip provides the accessible label.',
      'Pair destructive actions with clear surrounding copy rather than relying only on the icon.',
    ],
    preview: IconActionButtonPreview,
  },
  {
    exportName: 'StatusIconText',
    title: 'Status icon text',
    category: 'Status and feedback',
    summary: 'StatusIconText pairs success, warning, danger, or info glyphs with short status copy.',
    usageNotes: [
      'Keep text specific to the object being summarized.',
      'Use inside lists, notifications, and detail cards where a full message bar would be too heavy.',
    ],
    preview: StatusIconTextPreview,
  },
  {
    exportName: 'CopilotPromptRibbon',
    title: 'Copilot prompt ribbon',
    category: 'Copilot and automation',
    summary: 'CopilotPromptRibbon adds a compact Copilot entry point and suggested prompts inside a task header.',
    usageNotes: [
      'Keep suggested prompts short and task-specific.',
      'Use the ribbon near the surface it helps, not as a global replacement for search or navigation.',
    ],
    preview: CopilotPromptRibbonPreview,
  },
  {
    exportName: 'AzureAccordion',
    title: 'Accordion',
    category: 'Core controls',
    summary: 'AzureAccordion provides explicit bordered and borderless section groups without dropping into generic card stacks.',
    usageNotes: [
      'Keep required task-critical content outside the collapsed region, matching the Azure UI Kit guidance.',
      'Use borderless mode when the surrounding blade already provides enough structure.',
    ],
    preview: AccordionPreview,
  },
  {
    exportName: 'CodeSnippet',
    title: 'Code snippet',
    category: 'Core controls',
    summary: 'CodeSnippet turns ARM/CLI examples into compact, scrollable editor-style surfaces with line numbers and practical fold markers.',
    usageNotes: [
      'Keep the code region scrollable instead of forcing the whole blade to grow.',
      'Use syntax emphasis sparingly to support scanning, not to mimic a full IDE theme.',
    ],
    preview: CodeSnippetPreview,
  },
  {
    exportName: 'CopyButton',
    title: 'Copy button',
    category: 'Core controls',
    summary: 'CopyButton is a named Fluent-backed affordance for copying IDs, snippets, and commands with icon-only or labeled treatments.',
    usageNotes: [
      'Use the icon-only variant near compact values and the labeled variant near commands or snippets.',
      'Expose copied state as brief confirmation, not as a persistent status banner.',
    ],
    preview: CopyButtonPreview,
  },
  {
    exportName: 'AzureSlider',
    title: 'Slider',
    category: 'Core controls',
    summary: 'Use a labeled slider with optional info tooltip and live value readout for scalar Azure inputs like vCores or throughput.',
    usageNotes: [
      'Pair the value readout with a formatter so the number carries its unit (for example "8 vCores").',
      'Use info labels for capacity or billing consequences instead of long helper paragraphs.',
    ],
    preview: AzureSliderPreview,
  },
  {
    exportName: 'ProgressBarWithLabel',
    title: 'Progress bar with labels',
    category: 'Core controls',
    summary: 'ProgressBarWithLabel adds Azure label, info, and description scaffolding around the Fluent ProgressBar and covers both determinate and animated indeterminate runs.',
    usageNotes: [
      'Omit the value (or set indeterminate) for long provisioning work with no reliable percentage.',
      'Use the description line for counts or elapsed context, not for error messaging.',
    ],
    preview: ProgressBarWithLabelPreview,
  },
  {
    exportName: 'PortalLayout',
    title: 'Portal shell / top nav / rail',
    category: 'Shell and navigation',
    summary: 'Portal shell primitives keep the showcase and downstream consumers aligned with Azure header, rail, breadcrumb, and task-body hierarchy.',
    usageNotes: [
      'Compose PortalTopNav, PortalRail, and PortalLayout when a page needs Azure shell hierarchy without app-specific imports.',
      'Keep the content region flat: breadcrumb, blade header, commands, and task body should read in one scan.',
    ],
    preview: PortalShellPreview,
  },
  {
    exportName: 'CommandBar',
    title: 'CommandBar',
    category: 'Shell and navigation',
    summary: 'Command bars translate Azure toolbar guidance into portable primary/secondary action strips.',
    usageNotes: [
      'Use page-level commands for Create, Refresh, Query, or Export without wrapping them in oversized hero cards.',
      'Pair with FilterBar when the surrounding page also has search or facet pills.',
    ],
    preview: CommandBarPreview,
  },
  {
    exportName: 'PortalCommandBar',
    title: 'Portal command bar',
    category: 'Shell and navigation',
    summary: 'PortalCommandBar is the portal-named command strip export for pages that want shell-specific terminology.',
    usageNotes: [
      'Use it the same way as CommandBar when aligning a page with portal shell language.',
      'Keep primary and secondary actions grouped so the top of the blade remains scannable.',
    ],
    preview: PortalCommandBarPreview,
  },
  {
    exportName: 'DataToolbar',
    title: 'Data toolbar',
    category: 'Data and lists',
    summary: 'DataToolbar places compact list actions above grids and result sets.',
    usageNotes: [
      'Use it with grid-level actions such as create, refresh, export, or bulk operations.',
      'Keep selection summaries short and adjacent to the related action group.',
    ],
    preview: DataToolbarPreview,
  },
  {
    exportName: 'FilterBar',
    title: 'Filter bar',
    category: 'Data and lists',
    summary: 'FilterBar combines search with selected filter chips above a browse surface.',
    usageNotes: [
      'Use removable chips for active filters and keep search scoped to the list below.',
      'Pair with DataToolbar and AzureDataGrid for browse pages.',
    ],
    preview: FilterBarPreview,
  },
  {
    exportName: 'AzureToolbar',
    title: 'Toolbar',
    category: 'Shell and navigation',
    summary: 'Group subtle command buttons with dividers for blade command strips.',
    usageNotes: [
      'Enable topOfPage when the toolbar anchors the top of a blade so it reads as a bounded command surface.',
      'Insert an action with id "divider" to separate destructive or grouped commands.',
    ],
    preview: AzureToolbarPreview,
  },
  {
    exportName: 'FormFieldRow',
    title: 'FormFieldRow',
    category: 'Forms and create flows',
    summary: 'FormFieldRow preserves the fixed label column, helper/status line, and inline validation from Azure form-row guidance.',
    usageNotes: [
      'Use info labels only for non-blocking explanation; validation stays under the field column.',
      'Keep form content in a narrow column rather than stretching inputs edge-to-edge across the blade.',
    ],
    preview: FormFieldRowPreview,
  },
  {
    exportName: 'AzureStepList',
    title: 'AzureStepList',
    category: 'Forms and create flows',
    summary: 'AzureStepList provides numbered, horizontal step context for stepped blades and review flows.',
    usageNotes: [
      'Selected steps should preserve the underline/rail emphasis instead of turning into pills or chips.',
      'Use descriptions sparingly to reinforce orientation, not to replace form copy.',
    ],
    preview: StepListPreview,
  },
  {
    exportName: 'AzureTabList',
    title: 'AzureTabList',
    category: 'Forms and create flows',
    summary: 'AzureTabList covers related categories and validation states without promoting tabs into dashboard chrome.',
    usageNotes: [
      'Use status icons only when the tab content has a meaningful warning or success state.',
      'Prefer vertical tabs only when the surrounding layout already reads as a split blade.',
    ],
    preview: TabListPreview,
  },
  {
    exportName: 'FileUpload',
    title: 'File upload',
    category: 'Forms and create flows',
    summary: 'FileUpload gives create flows a labelled file input with browse affordance, upload button, and progress, success, and drag-and-drop states.',
    usageNotes: [
      'Drive the visual with the state prop (default, selected, progress, success, dragdrop) rather than composing separate widgets.',
      'Keep the label column aligned with surrounding FormFieldRow rows so create blades read as one column.',
    ],
    preview: FileUploadPreview,
  },
  {
    exportName: 'FilterableComboBox',
    title: 'Filterable combo box',
    category: 'Forms and create flows',
    summary: 'Use type-to-filter selection for long subscription, region, or resource pickers.',
    usageNotes: [
      'Use freeform filtering for long lists; keep option labels short so matches stay scannable.',
      'Reach for multiselect only when the field genuinely accepts several values, and surface selections as tags nearby.',
    ],
    preview: FilterableComboBoxPreview,
  },
  {
    exportName: 'AzureDataGrid',
    title: 'AzureDataGrid',
    category: 'Data and lists',
    summary: 'AzureDataGrid anchors dense browse/manage pages with compact list semantics, sortable headers, and clear empty/loading states.',
    usageNotes: [
      'Keep cells caller-owned through renderCell so the grid can host resource links, status text, or personas without hardcoding app data.',
      'Use AzureEmptyState or a contextual empty row instead of replacing the grid with decorative cards.',
    ],
    preview: DataGridPreview,
  },
  {
    exportName: 'Pager',
    title: 'Pager',
    category: 'Data and lists',
    summary: 'Pager keeps count summary, page navigation, and page-size controls reachable as one compact browse primitive.',
    usageNotes: [
      'Keep Pager directly reachable when it is part of the supported preview set, instead of implying it only exists inside larger browse examples.',
      'Pair it with grids or result lists, but preserve the count summary and page-size picker on the same row.',
    ],
    preview: PagerPreview,
  },
  {
    exportName: 'AzureEmptyState',
    title: 'AzureEmptyState',
    category: 'Data and lists',
    summary: 'AzureEmptyState stays compact and task-focused, following contextual no-results guidance rather than illustration-first marketing empty states.',
    usageNotes: [
      'Use it inline inside grids, panes, or task regions when the current filter/query returns nothing.',
      'Keep actions limited to one clear reset or next step.',
    ],
    preview: EmptyStatePreview,
  },
  {
    exportName: 'AzureSummaryCard',
    title: 'Summary card',
    category: 'Data and lists',
    summary: 'AzureSummaryCard shows an icon + title header with right-aligned metric rows and optional status dots, for overview/dashboard blades.',
    usageNotes: [
      'Compose several in a card flow at the top of an overview blade (varied sizes, not a rigid equal-tile grid).',
      'Use status-dot tones for at-a-glance health; keep values compact and tabular.',
    ],
    preview: SummaryCardPreview,
  },
  {
    exportName: 'AzurePropertyList',
    title: 'Property list',
    category: 'Data and lists',
    summary: 'AzurePropertyList renders a titled card of uppercase label + value rows — the Essentials/properties pattern.',
    usageNotes: [
      'Use for resource "Basics"/properties summaries below the summary cards.',
      'Keep labels short; values wrap and can host links or status text.',
    ],
    preview: PropertyListPreview,
  },
  {
    exportName: 'CopilotComposer',
    title: 'Copilot composer',
    category: 'Copilot and automation',
    summary: 'Keep prompts, attachments, and stop controls in one compact task-local Copilot input.',
    usageNotes: [
      'Use one attachment row and a short prompt region instead of stacking a full card shell around the composer.',
      'Reserve agent mode for workflow-oriented prompts that may request approvals or produce artifacts.',
    ],
    preview: CopilotComposerPreview,
  },
  {
    exportName: 'CopilotResponse',
    title: 'Copilot response',
    category: 'Copilot and automation',
    summary: 'Show Copilot answers, confirmations, and lightweight action rows without exposing hidden reasoning.',
    usageNotes: [
      'Keep actions local to the generated answer, such as copy, open, or confirm.',
      'Use confirmation parts when the next step changes real resources or costs.',
    ],
    preview: CopilotResponsePreview,
  },
  {
    exportName: 'InlineCopilot',
    title: 'Inline Copilot',
    category: 'Copilot and automation',
    summary: 'InlineCopilot provides contextual rewrite/generate help anchored to the field the user is already editing.',
    usageNotes: [
      'Anchor it near the current task instead of navigating the user to a separate chat surface.',
      'Offer only a few suggestion chips so the inline prompt stays small.',
    ],
    preview: InlineCopilotPreview,
  },
  {
    exportName: 'AgenticProgress',
    title: 'Agentic progress',
    category: 'Copilot and automation',
    summary: 'Automation progress exposes run activity, generated outputs, and approvals as a readable operator-first list.',
    usageNotes: [
      'Keep risk and approval language in the expanded row so the operator can decide in context.',
      'Artifact rows should point to specific outputs, not to generic activity feeds.',
    ],
    preview: AgenticProgressPreview,
  },
  {
    exportName: 'ArtifactPill',
    title: 'Artifact pill',
    category: 'Copilot and automation',
    summary: 'ArtifactPill shows a compact file or output chip with type information and an open action.',
    usageNotes: [
      'Use for small sets of generated outputs or review packets inside activity panels.',
      'Keep titles recognizable and reserve size/type details for secondary text.',
    ],
    preview: ArtifactPillPreview,
  },
  {
    exportName: 'ChainOfThought',
    title: 'Reasoning panel',
    category: 'Copilot and automation',
    summary: 'The reasoning panel presents activity, outputs, step state, and approvals in a reviewable Copilot workflow panel.',
    usageNotes: [
      'Use when the user needs to audit progress or approve a sensitive next step.',
      'Keep approval copy close to the affected step so decisions stay contextual.',
    ],
    preview: ChainOfThoughtPreview,
  },
  {
    exportName: 'CopilotWorkspacePattern',
    title: 'Copilot workspace',
    category: 'Copilot and automation',
    summary: 'A compact Copilot task workspace with navigation, response, actions, and composer.',
    usageNotes: [
      'Use it when Copilot is a primary task surface, not when the prompt should stay inline.',
      'Keep the menu narrow and the main content column dedicated to response + composer flow.',
    ],
    preview: CopilotWorkspacePreview,
  },
  {
    exportName: 'NotificationPane',
    title: 'Notification pane',
    category: 'Status and feedback',
    summary: 'Notification pane turns actionable updates into a reusable side-pane list with local actions and unread emphasis.',
    usageNotes: [
      'Use notification panes for actionable status near the affected task surface, not as a detached toast wall.',
      'Pair with contextual grids or detail panes when the notification opens a remediation workflow.',
    ],
    preview: NotificationPanePreview,
  },
  {
    exportName: 'FeedbackFooter',
    title: 'Feedback footer',
    category: 'Status and feedback',
    summary: 'Feedback footer captures customer satisfaction prompts with restrained copy and right-aligned action emphasis.',
    usageNotes: [
      'Reserve the footer for feedback or next-step affordances after the main work is readable.',
      'Do not let feedback compete with primary task completion actions.',
    ],
    preview: FeedbackFooterPreview,
  },
  {
    exportName: 'DeleteResourceDialog',
    title: 'Delete resource dialog',
    category: 'Dialogs and confirmations',
    summary: 'Keep destructive actions explicit, consequence-driven, and optionally gated by acknowledgement.',
    usageNotes: [
      'Use soft-delete or recoverability copy when the service supports it.',
      'Keep danger styling focused on the destructive affordance, not the entire surrounding surface.',
    ],
    preview: DeleteDialogPreview,
  },
  {
    exportName: 'DeleteConfirmationDialog',
    title: 'Delete confirmation dialog',
    category: 'Dialogs and confirmations',
    summary: 'DeleteConfirmationDialog is the confirmation-oriented export for the same destructive action flow.',
    usageNotes: [
      'Use when product copy names the flow as a confirmation rather than a resource-specific dialog.',
      'Keep acknowledgement requirements explicit before enabling the destructive action.',
    ],
    preview: DeleteConfirmationDialogPreview,
  },
  {
    exportName: 'HelpPopover',
    title: 'Help popover',
    category: 'Dialogs and confirmations',
    summary: 'HelpPopover provides lightweight contextual guidance anchored to the control it explains.',
    usageNotes: [
      'Use for short explanations that would otherwise interrupt form scanning.',
      'Keep actions secondary to the explanation unless the popover is part of onboarding.',
    ],
    preview: HelpPopoverPreview,
  },
  {
    exportName: 'CalloutPopover',
    title: 'Callout popover',
    category: 'Dialogs and confirmations',
    summary: 'CalloutPopover is the callout-named export for the same anchored guidance surface.',
    usageNotes: [
      'Use when product copy treats the anchored content as a recommendation or callout.',
      'Keep the trigger close to the recommendation it explains.',
    ],
    preview: CalloutPopoverPreview,
  },
  {
    exportName: 'PortalTopNav',
    title: 'Top navigation',
    category: 'Shell and navigation',
    summary: 'PortalTopNav assembles the Azure portal masthead: product/area brand, global search, Copilot entry, utility actions, and persona.',
    usageNotes: [
      'Keep the brand product label stable; use the area slot for the current portal context.',
      'Surface Copilot as a dedicated action so it stays discoverable next to global search.',
    ],
    preview: PortalTopNavPreview,
  },
  {
    exportName: 'PortalRail',
    title: 'Portal rail',
    category: 'Shell and navigation',
    summary: 'PortalRail provides the compact left rail for high-level portal destinations.',
    usageNotes: [
      'Use it for primary destinations that need to stay reachable while the blade content changes.',
      'Keep labels available through tooltips when the rail is icon-only.',
    ],
    preview: PortalRailPreview,
  },
  {
    exportName: 'ServiceMenu',
    title: 'Service menu',
    category: 'Shell and navigation',
    summary: 'Provide grouped portal navigation with search, nested items, favorites, badges, and collapsed mode.',
    usageNotes: [
      'Enable search once the menu passes a handful of groups so deep items stay reachable.',
      'Use the collapsed mode for narrow layouts instead of hiding navigation entirely.',
    ],
    preview: ServiceMenuPreview,
  },
  {
    exportName: 'BladeHeader',
    title: 'Blade header',
    category: 'Shell and navigation',
    summary: 'BladeHeader anchors a blade with the resource icon, title, subtitle, command actions, and an optional dismiss affordance.',
    usageNotes: [
      'Keep the title to the resource name and use the subtitle for the resource type or scope.',
      'Move lower-priority commands into overflow rather than crowding the primary action row.',
    ],
    preview: BladeHeaderPreview,
  },
  {
    exportName: 'ResourceTagEditor',
    title: 'Resource tags',
    category: 'Forms and create flows',
    summary: 'ResourceTagEditor provides an editable name/value tag grid with per-resource assignment, add/remove rows, and inline validation.',
    usageNotes: [
      'Validate tag names inline so users fix duplicates or invalid characters before saving.',
      'Let rows target specific resources when tags apply to a subset of the selection.',
    ],
    preview: ResourceTagEditorPreview,
  },
  {
    exportName: 'AzureForm',
    title: 'Form',
    category: 'Forms and create flows',
    summary: 'Use stacked form rows with an optional status message and sticky footer actions.',
    usageNotes: [
      'Keep the form in a narrow reading column and let FormFieldRow own the label alignment.',
      'Use the message slot for form-level status instead of repeating it on every field.',
    ],
    preview: AzureFormPreview,
  },
  {
    exportName: 'FormFooter',
    title: 'Form footer',
    category: 'Forms and create flows',
    summary: 'FormFooter keeps primary, secondary, and feedback actions docked together at the end of a task form.',
    usageNotes: [
      'Use it for save/cancel flows so actions remain predictable across edit and create blades.',
      'Keep feedback secondary to the primary task action.',
    ],
    preview: FormFooterPreview,
  },
  {
    exportName: 'EssentialsGrid',
    title: 'Essentials',
    category: 'Data and lists',
    summary: 'Essentials renders the collapsible resource-summary property grid with label/value pairs, links, and inline tags.',
    usageNotes: [
      'Keep property labels short and let values carry links or tags rather than extra helper text.',
      'Collapse to a single column on narrow blades so the summary stays readable.',
    ],
    preview: EssentialsGridPreview,
  },
  {
    exportName: 'FilterPills',
    title: 'Search filter pills',
    category: 'Data and lists',
    summary: 'Use compact category pills to refine search results, with overflow for less-common facets.',
    usageNotes: [
      'Keep the most common facets visible and move the long tail into the Filters overflow menu.',
      'Drive selection through selectedIds so the pills stay controlled alongside the result query.',
    ],
    preview: FilterPillsPreview,
  },
];

const componentPreviewByExport = componentCatalog.reduce<Record<string, ComponentPreviewEntry>>((accumulator, entry) => {
  accumulator[entry.exportName] = entry;
  return accumulator;
}, {});

const componentInventoryRowsByNodeId = new Map(
  componentInventory.inventoryRows.map((row) => [extractNodeIdFromCatalogReference(row.figmaNodeReference), row] as const),
);

// Some inventory nodes are genuine standalone components that were never assigned a
// library export in the generated catalog, and a handful of internal Figma layers carry
// stale export names. These overrides map those nodes onto the correct local export so the
// grouped browser can render them live instead of stranding them as look-alike placeholders.
const manualInventoryExportOverrides: Record<string, string> = {
  '25412:8797': 'EssentialsGrid',
  '41795:20148': 'FilterPills',
  '35182:761': 'FeedbackFooter',
  '28644:76791': 'NotificationPane',
  '35399:10636': 'ServiceMenu',
  '41544:8562': 'ServiceMenu',
  '29167:8324': 'AzureTabList',
  '29195:7388': 'AzureTabList',
  '29553:14761': 'AzureTabList',
  '29553:14688': 'AzureTabList',
  '28093:48459': 'AzureDataGrid',
  '28093:48461': 'AzureDataGrid',
  '28093:48474': 'AzureDataGrid',
  '28093:48484': 'AzureDataGrid',
  '28093:49265': 'AzureDataGrid',
  '28093:49423': 'AzureDataGrid',
  '28093:49440': 'AzureDataGrid',
  '28093:49447': 'AzureDataGrid',
  '28093:49456': 'AzureDataGrid',
};

// The generated catalog references a `FormSection` export that never shipped; the real export is `AzureForm`.
const inventoryExportRenames: Record<string, string> = {
  FormSection: 'AzureForm',
};

const INTERNAL_LAYERS_GROUP_KEY = 'internal-component-layers';

const coverageStatusRank: Record<string, number> = {
  'implemented-rendered': 0,
  'showcase-placeholder': 1,
  'needs-mcp-extraction': 2,
  'needs-implementation': 3,
  'local-only-needed': 4,
};

function isInternalLayerName(name: string) {
  return /^\s*[.\u21aa]/.test(name);
}

type InventoryComponent = NonNullable<NonNullable<ComponentInventoryManifest['inventoryCoverage']>['components']>[number];

function resolveComponentExports(component: InventoryComponent): string[] {
  const inventoryRow = componentInventoryRowsByNodeId.get(component.nodeId);
  const raw = component.libraryExports ?? normalizeCatalogImplementedMapping(inventoryRow?.implementedMapping ?? '');
  const renamed = raw.map((exportName) => inventoryExportRenames[exportName] ?? exportName);
  const override = manualInventoryExportOverrides[component.nodeId];
  return uniqueStrings(override ? [override, ...renamed] : renamed);
}

function resolveInventoryGroupKey(component: InventoryComponent, exportNames: string[]): string {
  if (exportNames.length > 0) return `export:${exportNames[0]}`;
  if (isInternalLayerName(component.name ?? '')) return INTERNAL_LAYERS_GROUP_KEY;
  return `unmapped:${component.nodeId}`;
}

interface InventoryGroupAccumulator {
  key: string;
  members: InventoryComponent[];
  exportNames: string[];
}

const inventoryGroupOrder: string[] = [];
const inventoryGroupsByKey = new Map<string, InventoryGroupAccumulator>();

for (const component of componentInventory.inventoryCoverage?.components ?? []) {
  const exportNames = resolveComponentExports(component);
  const key = resolveInventoryGroupKey(component, exportNames);
  let group = inventoryGroupsByKey.get(key);
  if (!group) {
    group = { key, members: [], exportNames: [] };
    inventoryGroupsByKey.set(key, group);
    inventoryGroupOrder.push(key);
  }
  group.members.push(component);
  group.exportNames = uniqueStrings([...group.exportNames, ...exportNames]);
}

function pickRepresentative(group: InventoryGroupAccumulator): InventoryComponent {
  return (
    group.members.find((member) => member.coverageStatus === 'implemented-rendered')
    ?? group.members.find((member) => !isInternalLayerName(member.name ?? ''))
    ?? group.members[0]
  );
}

function bestCoverageStatus(group: InventoryGroupAccumulator): string {
  return group.members
    .map((member) => member.coverageStatus)
    .sort((a, b) => (coverageStatusRank[a] ?? 9) - (coverageStatusRank[b] ?? 9))[0]
    ?? 'needs-mcp-extraction';
}

const groupedComponentInventoryEntries: ComponentInventoryBrowserEntry[] = inventoryGroupOrder.map((key) => {
  const group = inventoryGroupsByKey.get(key)!;
  const representative = pickRepresentative(group);
  const inventoryRow = componentInventoryRowsByNodeId.get(representative.nodeId);
  const exportNames = group.exportNames;
  const previewEntry = exportNames.map((exportName) => componentPreviewByExport[exportName]).find(Boolean);
  const sourceNodes: InventorySourceNode[] = group.members.map((member) => ({
    nodeId: member.nodeId,
    name: member.name ?? member.nodeId,
  }));

  const isInternalBucket = key === INTERNAL_LAYERS_GROUP_KEY;
  const coverageStatus = previewEntry
    ? 'implemented-rendered'
    : isInternalBucket
      ? 'showcase-placeholder'
      : bestCoverageStatus(group);

  const summary = isInternalBucket
    ? `${group.members.length} shared rows, headers, popovers, and menu parts are represented through the component previews above.`
    : previewEntry?.summary ?? getComponentFallbackSummary(coverageStatus, exportNames);

  const title = isInternalBucket
    ? 'Shared component details'
    : previewEntry?.title ?? representative.name ?? representative.nodeId;

  const exportLabel = isInternalBucket
    ? 'Composed into other components'
    : exportNames.length > 0
      ? exportNames.join(', ')
      : 'No reusable code export yet';

  return {
    nodeId: representative.nodeId,
    title,
    pageName: representative.pageName ?? 'Unsorted',
    type: representative.type ?? 'COMPONENT',
    nodeUrl: representative.nodeUrl,
    coverageStatus,
    statusLabel: formatComponentCoverageStatus(coverageStatus),
    exportNames,
    exportLabel,
    summary,
    nextAction: getComponentNextAction(coverageStatus, exportNames),
    extractionDate: inventoryRow?.extractionDate ?? 'Not recorded',
    extractionStatus: inventoryRow?.extractionStatus ?? representative.mcpStatus ?? coverageStatus,
    showcaseStatus: inventoryRow?.showcase ?? (previewEntry ? 'Yes' : 'No'),
    figmaNodeReference: inventoryRow?.figmaNodeReference ?? representative.nodeId,
    previewEntry,
    sourceNodes,
  };
});

// Build inventory entries for the Fluent 2 foundation primitives so they appear in the
// same Components inventory (not a separate tab). Each uses a synthetic, collision-free
// nodeId and empty sourceNodes — the 148 Figma source-node audit stays intact.
const foundationInventoryEntries: ComponentInventoryBrowserEntry[] = allFluent2Foundations.map((spec) => {
  const previewEntry: ComponentPreviewEntry = {
    exportName: spec.exportName,
    title: spec.title,
    category: 'Fluent 2 foundation',
    summary: spec.summary,
    usageNotes: spec.usageNotes,
    preview: spec.preview,
  };
  return {
    nodeId: `foundation-${spec.exportName}`,
    title: spec.title,
    pageName: 'Fluent 2 foundations',
    type: 'COMPONENT',
    nodeUrl: FOUNDATION_PAGE_URL,
    coverageStatus: 'implemented-rendered',
    statusLabel: formatComponentCoverageStatus('implemented-rendered'),
    exportNames: [spec.exportName],
    exportLabel: spec.exportName,
    summary: spec.summary,
    nextAction: getComponentNextAction('implemented-rendered', [spec.exportName]),
    extractionDate: 'Fluent 2 base',
    extractionStatus: 'fluent-2-foundation',
    showcaseStatus: 'Yes',
    figmaNodeReference: FOUNDATION_PAGE_URL,
    previewEntry,
    sourceNodes: [],
  };
});

const copilotComponentExportOrder = [
  'CopilotComposer',
  'CopilotResponse',
  'InlineCopilot',
  'CopilotPromptRibbon',
  'ArtifactPill',
  'AgenticProgress',
  'ChainOfThought',
  'CopilotWorkspacePattern',
];

const copilotComponentExportNames = new Set(copilotComponentExportOrder);

function isCopilotComponentInventoryEntry(entry: ComponentInventoryBrowserEntry): boolean {
  return entry.exportNames.some((exportName) => copilotComponentExportNames.has(exportName));
}

function compareInventoryTitle(a: ComponentInventoryBrowserEntry, b: ComponentInventoryBrowserEntry): number {
  const aInternal = a.title === 'Shared component details';
  const bInternal = b.title === 'Shared component details';
  if (aInternal !== bInternal) return aInternal ? 1 : -1;

  const aCopilot = isCopilotComponentInventoryEntry(a);
  const bCopilot = isCopilotComponentInventoryEntry(b);
  if (aCopilot !== bCopilot) return aCopilot ? -1 : 1;
  if (aCopilot && bCopilot) {
    const aRank = Math.min(...a.exportNames.map((exportName) => {
      const rank = copilotComponentExportOrder.indexOf(exportName);
      return rank >= 0 ? rank : copilotComponentExportOrder.length;
    }));
    const bRank = Math.min(...b.exportNames.map((exportName) => {
      const rank = copilotComponentExportOrder.indexOf(exportName);
      return rank >= 0 ? rank : copilotComponentExportOrder.length;
    }));
    if (aRank !== bRank) return aRank - bRank;
  }

  return a.title.localeCompare(b.title, 'en', { sensitivity: 'base' });
}

export const showcaseComponentInventoryEntries: ComponentInventoryBrowserEntry[] = [
  ...groupedComponentInventoryEntries,
  ...foundationInventoryEntries,
].sort(compareInventoryTitle);

export const publicShowcaseComponentInventoryEntries = showcaseComponentInventoryEntries.filter(
  (entry) => entry.coverageStatus === 'implemented-rendered'
    && Boolean(entry.previewEntry)
    && !entry.exportNames.some((exportName) => compositeChildFoundationExportNames.has(exportName))
    && (entry.sourceNodes.length > 0 || entry.exportNames.some((exportName) => publicFoundationExportNames.has(exportName))),
);

export const showcaseComponentInventoryNodeIds = publicShowcaseComponentInventoryEntries.map(({ nodeId }) => nodeId);

// Flat list of every Figma node represented across the grouped inventory (one entry per catalog row),
// used to guarantee the grouping never drops a component from coverage.
export const showcaseComponentInventorySourceNodeIds = showcaseComponentInventoryEntries.flatMap(
  ({ sourceNodes }) => sourceNodes.map((node) => node.nodeId),
);

export const showcaseComponentMenuExportNames = componentCatalog.map(({ exportName }) => exportName);
// Showcase marker: Three primary experiences: component preview, pattern example browser, and icon browser
// Showcase marker: Built from `catalog/COMPONENTS.md`
// Showcase marker: Built from `catalog/ICONS.md`
// Showcase marker: Built from `catalog/PATTERNS.md`
// Showcase marker: Local source mappings

function CreateSteppedFormPatternPreview() {
  const [currentStep, setCurrentStep] = useState('basics');
  const [resourceName, setResourceName] = useState('aks-cluster-primary');
  const [resourceGroup, setResourceGroup] = useState('resource-group-sample');

  return (
    <PortalLayout
      className="azf-showcase-pattern-frame"
      topNav={(
        <PortalTopNav
          brand={{ product: 'Microsoft Azure', area: 'Portal' }}
          startActions={[
            { id: 'all-services', label: 'All services', icon: <AppsListRegular /> },
            { id: 'toggle-nav', label: 'Toggle navigation', icon: <NavigationRegular /> },
          ]}
          searchValue="monitored resource"
          onSearchChange={() => undefined}
          endActions={[{ id: 'help', label: 'Help', icon: <InfoRegular /> }, { id: 'settings', label: 'Settings', icon: <SettingsRegular /> }]}
          persona={{ name: 'Signed-in user', secondaryText: 'Organization directory', icon: <PersonCircleRegular /> }}
        />
      )}
      breadcrumb={<Text>Home / Observability / Create monitored resource</Text>}
      header={<BladeHeader title="Create monitored resource" subtitle="Sample resource" actions={[{ id: 'dismiss', label: 'Close', icon: <DismissRegular />, onClick: () => undefined }]} />}
      footer={(
        <FormFooter
          primaryAction={{ id: 'next', label: currentStep === 'review' ? 'Create' : 'Next', appearance: 'primary', onClick: () => setCurrentStep(currentStep === 'review' ? 'review' : currentStep === 'basics' ? 'networking' : 'review') }}
          secondaryAction={{ id: 'previous', label: 'Previous', disabled: currentStep === 'basics', onClick: () => setCurrentStep(currentStep === 'review' ? 'networking' : 'basics') }}
          feedback={<Link href="#" onClick={(event) => event.preventDefault()}>Give feedback</Link>}
        />
      )}
      contentClassName="azf-showcase-create-blade"
    >
      <div className="azf-showcase-create-blade__column">
        <AzureStepList
          selectedValue={currentStep}
          onStepSelect={setCurrentStep}
          steps={[
            { id: 'basics', label: 'Basics', description: 'Name and scope' },
            { id: 'networking', label: 'Networking', description: 'Private access' },
            { id: 'review', label: 'Review + create', description: 'Validate warnings', status: 'warning' },
          ]}
        />
        <Text className="azf-showcase-copy">
          This flow keeps orientation, form content, validation, and footer actions visible without turning the task into a modal wizard.
        </Text>
        {currentStep === 'basics' && (
          <>
            <FormFieldRow label="Subscription" htmlFor="create-subscription" info="Inherited from the current landing zone context.">
              <Input id="create-subscription" value="Sample subscription A" readOnly />
            </FormFieldRow>
            <FormFieldRow label="Resource name" htmlFor="create-resource-name" hint="Use the Azure naming guidance before moving to networking.">
              <Input id="create-resource-name" value={resourceName} onChange={(_, data) => setResourceName(data.value)} />
            </FormFieldRow>
            <FormFieldRow label="Resource group" htmlFor="create-resource-group">
              <Input id="create-resource-group" value={resourceGroup} onChange={(_, data) => setResourceGroup(data.value)} />
            </FormFieldRow>
          </>
        )}
        {currentStep === 'networking' && (
          <>
            <FormFieldRow label="Private endpoint" htmlFor="create-private-endpoint" hint="Keep create flows task-focused instead of widening into dashboard chrome.">
              <Input id="create-private-endpoint" value="Enable private access" readOnly />
            </FormFieldRow>
            <FormFieldRow label="Subnet" htmlFor="create-subnet">
              <Input id="create-subnet" value="subnet-private-sample" readOnly />
            </FormFieldRow>
          </>
        )}
        {currentStep === 'review' && (
          <NotificationPattern
            title="Ready for review"
            body={`Create ${resourceName} in ${resourceGroup} with diagnostics, inherited tags, and private access enabled.`}
            intent="info"
          />
        )}
      </div>
    </PortalLayout>
  );
}

function BrowseResourcePatternPreview() {
  const [query, setQuery] = useState('aks');
  const filteredRows = useMemo(
    () => aksResourceRows.filter((row) => !query.trim() || row.name.toLowerCase().includes(query.toLowerCase()) || row.type.toLowerCase().includes(query.toLowerCase())),
    [query],
  );

  return (
    <BrowseResourcePattern
      title="AKS resource list"
      subtitle="Browse Kubernetes resources with resource group, subscription, type, and status anatomy."
      items={filteredRows}
      columns={[...aksResourceColumns]}
      filters={[{ id: 'type', label: 'Type', value: 'Kubernetes services', selected: true }]}
      toolbarActions={[{ id: 'create', label: 'Create resource', appearance: 'primary', onClick: () => undefined }]}
      headerActions={[{ id: 'refresh', label: 'Refresh', icon: <ArrowClockwiseRegular />, onClick: () => undefined }]}
      searchValue={query}
      onSearchChange={setQuery}
      emptyState="No AKS resources match the current filters."
    />
  );
}

function FilteringPatternPreview() {
  const [query, setQuery] = useState('aks');
  const filteredRows = useMemo(
    () => resourceRows.filter((row) => !query.trim() || row.name.toLowerCase().includes(query.toLowerCase())),
    [query],
  );

  return (
    <FilteringPattern
      title="Filtered resources"
      subtitle="A list surface for narrowing resources without leaving the current task."
      items={filteredRows}
      columns={[...gridColumns]}
      filters={[
        { id: 'status', label: 'Status', value: 'Needs attention', selected: true },
        { id: 'location', label: 'Location', value: 'All regions', selected: false },
      ]}
      searchValue={query}
      onSearchChange={setQuery}
      toolbarActions={[{ id: 'export', label: 'Export results', onClick: () => undefined }]}
      emptyState="No resources match the selected filters."
    />
  );
}

function FormBladePatternPreview() {
  return (
    <FormBladePattern
      title="Update diagnostic settings"
      subtitle="Sample resource"
      primaryAction={{ id: 'save', label: 'Save', appearance: 'primary', onClick: () => undefined }}
      secondaryAction={{ id: 'cancel', label: 'Cancel', onClick: () => undefined }}
      message="Changes apply to new telemetry after saving."
      feedback={<Link href="#" onClick={(event) => event.preventDefault()}>Give feedback</Link>}
    >
      <FormFieldRow label="Destination" htmlFor="form-blade-destination" hint="Choose where diagnostic logs are sent.">
        <Input id="form-blade-destination" value="Monitoring workspace" readOnly />
      </FormFieldRow>
      <FormFieldRow label="Retention" htmlFor="form-blade-retention">
        <Input id="form-blade-retention" value="30 days" readOnly />
      </FormFieldRow>
    </FormBladePattern>
  );
}

function StepWizardPatternPreview() {
  const [currentStep, setCurrentStep] = useState('basics');

  return (
    <StepWizardPattern
      title="Configure backup policy"
      subtitle="Step through scope, schedule, and review before saving."
      currentStepId={currentStep}
      onStepSelect={setCurrentStep}
      steps={[
        {
          id: 'basics',
          label: 'Basics',
          description: 'Scope',
          content: <FormFieldRow label="Policy name" htmlFor="wizard-policy"><Input id="wizard-policy" value="Nightly sample backup" readOnly /></FormFieldRow>,
        },
        {
          id: 'schedule',
          label: 'Schedule',
          description: 'Frequency',
          content: <FormFieldRow label="Run time" htmlFor="wizard-time"><Input id="wizard-time" value="02:00 UTC" readOnly /></FormFieldRow>,
        },
        {
          id: 'review',
          label: 'Review',
          description: 'Confirm',
          status: 'warning',
          content: <NotificationPattern title="Ready to review" body="Confirm retention and destination before saving." intent="info" />,
        },
      ]}
      primaryAction={{ id: 'continue', label: currentStep === 'review' ? 'Save' : 'Continue', appearance: 'primary', onClick: () => undefined }}
      secondaryAction={{ id: 'back', label: 'Back', onClick: () => undefined }}
    />
  );
}

function CreateResourcePatternComponentPreview() {
  const [currentStep, setCurrentStep] = useState('basics');

  return (
    <CreateResourcePattern
      title="Create monitored resource"
      subtitle="Sample resource"
      currentStepId={currentStep}
      onStepSelect={setCurrentStep}
      steps={[
        {
          id: 'basics',
          label: 'Basics',
          description: 'Name and scope',
          content: <FormFieldRow label="Name" htmlFor="create-component-name"><Input id="create-component-name" value="aks-cluster-primary" readOnly /></FormFieldRow>,
        },
        {
          id: 'networking',
          label: 'Networking',
          description: 'Private access',
          content: <FormFieldRow label="Private endpoint" htmlFor="create-component-private"><Input id="create-component-private" value="Enabled" readOnly /></FormFieldRow>,
        },
        {
          id: 'review',
          label: 'Review',
          description: 'Validate',
          status: 'warning',
          content: <Text>Review diagnostics, tags, and private access before creating.</Text>,
        },
      ]}
      reviewContent={currentStep === 'review' ? <NotificationPattern title="Validation warning" body="One policy assignment will be checked during create." intent="warning" /> : undefined}
      primaryAction={{ id: 'create', label: currentStep === 'review' ? 'Create' : 'Next', appearance: 'primary', onClick: () => undefined }}
      secondaryAction={{ id: 'previous', label: 'Previous', onClick: () => undefined }}
    />
  );
}

function ErrorPatternPreview() {
  return (
    <ErrorPattern
      title="Deployment retry blocked"
      body="Resolve the private endpoint policy assignment before retrying the operation."
      actions={<Button appearance="secondary">Open policy</Button>}
    />
  );
}

function NotificationsPatternPreview() {
  return (
    <div className="azf-showcase-pattern-stack">
      <div className="azf-showcase-notification-demo">
        <NotificationPane
          className="azf-showcase-notification-demo__pane"
          title="Activity updates"
          items={[
            {
              id: 'pane-1',
              title: 'Backup policy updated',
              body: 'Nightly snapshots now apply to every sample account in West US 2.',
              tone: 'success',
              timestamp: '2 min ago',
              actions: [{ id: 'view-change', label: 'View change', onClick: () => undefined }],
            },
            {
              id: 'pane-2',
              title: 'Firewall validation blocked',
              body: 'Resolve the private endpoint policy before rollout.',
              tone: 'warning',
              unread: true,
              timestamp: 'Now',
              actions: [{ id: 'open', label: 'Open resource', onClick: () => undefined }],
            },
          ]}
        />
        <section className="azf-showcase-notification-demo__detail" aria-label="Notification remediation detail">
          <Text weight="semibold">Remediation</Text>
          <Text className="azf-muted">Apply the private endpoint policy, then rerun validation from the affected resource.</Text>
          <div className="azf-row azf-gap-xs azf-wrap">
            <Button appearance="primary">Open policy</Button>
            <Button>View activity log</Button>
          </div>
        </section>
      </div>
    </div>
  );
}

function DeleteResourcePatternPreview() {
  const [acknowledged, setAcknowledged] = useState(false);
  return (
    <div className="azf-showcase-pattern-stack">
      <Text className="azf-showcase-copy">
        Review dependent resources and recovery details before enabling a destructive action.
      </Text>
      <AzureDataGrid
        items={[
          { id: 'dep-1', name: 'Orders dashboard workbook', owner: 'Observability', location: 'West US 2', status: 'Healthy' },
          { id: 'dep-2', name: 'Retail snapshot export', owner: 'Finance', location: 'East US', status: 'Needs attention' },
        ]}
        columns={[...gridColumns]}
        caption="Representative dependent resources"
      />
      <div className="azf-showcase-inline-actions">
        <DeleteResourceDialog
          resourceName="sample-resource"
          softDelete
          confirmationText="Soft delete remains available for 14 days, but connected workloads lose access immediately."
          consequences={[
            'Dependent workbooks and exports lose access immediately.',
            'Recovery remains available during the retention window.',
          ]}
          acknowledgement={{
            label: 'I understand the dependent resources listed above will be affected.',
            checked: acknowledged,
            onChange: setAcknowledged,
          }}
          trigger={<Button appearance="outline" icon={<DeleteRegular />}>Review delete</Button>}
          onCancel={() => setAcknowledged(false)}
          onConfirm={() => undefined}
        />
      </div>
    </div>
  );
}

function ManageResourcePatternPreview() {
  return (
    <ManageResourcePattern
      header={<BladeHeader title="Manage monitored resource" subtitle="Private access and network settings" />}
      serviceMenu={<ServiceMenu groups={serviceMenuGroups.map((group) => ({ ...group, items: [...group.items] }))} selectedId="networking" onSelect={() => undefined} />}
    >
      <div className="azf-showcase-form-column">
        <FormFieldRow label="Public network access" htmlFor="manage-public-access" hint="Routine network changes stay inline instead of expanding into a wizard.">
          <Input id="manage-public-access" value="Disabled" readOnly />
        </FormFieldRow>
        <FormFieldRow label="Private endpoint" htmlFor="manage-private-endpoint">
          <Input id="manage-private-endpoint" value="private-endpoint-sample" readOnly />
        </FormFieldRow>
        <AzureDataGrid items={resourceRows.slice(0, 2)} columns={[...gridColumns]} caption="Policy status and compact resource lists remain visible inside the management view." />
      </div>
    </ManageResourcePattern>
  );
}

function ServiceOverviewPatternPreview() {
  return (
    <ServiceOverviewPattern
      title="Service overview"
    subtitle="Summarize service health, recommendations, and next actions in concise cards."
      primaryAction={{ id: 'create', label: 'Create resource', appearance: 'primary', onClick: () => undefined }}
      secondaryAction={{ id: 'open-docs', label: 'Open docs', onClick: () => undefined }}
      overviewCards={[
        { id: 'health', title: 'Health', body: '2 resources need policy updates.', actions: <Button appearance="subtle">Review recommendations</Button> },
        { id: 'automation', title: 'Automation', body: 'Lifecycle policies are active on 6 of 8 resources.', actions: <Button appearance="subtle">Open assignments</Button> },
      ]}
    />
  );
}

function FeedbackPatternPreview() {
  return (
    <div className="azf-showcase-pattern-stack">
      <div className="azf-showcase-form-column">
        <FormFieldRow label="What was unclear?" htmlFor="feedback-summary" hint="Keep the surface lightweight: short prompt, local copy, and a right-aligned footer action.">
          <Input id="feedback-summary" value="The remediation steps need one clearer sentence." readOnly />
        </FormFieldRow>
        <Textarea value="The task flow was easy to scan, but the dependency warning should mention the private endpoint policy by name." readOnly resize="vertical" />
      </div>
      <FeedbackFooter
        title="Did this page help you finish the task?"
        body="Customer feedback relies on a clear prompt and restrained footer placement."
        action={{ id: 'share-feedback', label: 'Share feedback', onClick: () => undefined }}
      />
    </div>
  );
}

function PatternIndexPreview() {
  return (
    <div className="azf-showcase-pattern-index">
      {showcasePatternFamilies.map((family) => {
        const preview = patternPreviewCatalog.find((entry) => entry.familyId === family.id);
        return (
          <button key={family.id} type="button" className="azf-showcase-pattern-index__item">
            <div className="azf-showcase-pattern-index__copy">
              <Text weight="semibold">{formatPatternFamilyName(family)}</Text>
              <Text className="azf-muted">{preview?.summary ?? 'Open the pattern for guidance and a rendered example.'}</Text>
            </div>
          </button>
        );
      })}
    </div>
  );
}

const patternPreviewCatalog: PatternPreviewEntry[] = [
  {
    familyId: 'aks-resource-list',
    summary: 'Azure Kubernetes Service list with compact command, filter, and resource group/subscription/type/status table anatomy.',
    anatomy: ['AKS resource list', 'Resource group column', 'Subscription column', 'Type column', 'Status column'],
    preview: BrowseResourcePatternPreview,
  },
  {
    familyId: 'portal-global-search',
    summary: 'Portal-style global search flyout that combines service picker and recent resource result rows.',
    anatomy: ['Global search input', 'Service picker result', 'Recent resource result', 'Compact result rows'],
    preview: PortalGlobalSearchPreview,
  },
  {
    familyId: 'portal-settings-flyout',
    summary: 'Portal settings flyout with compact flyout header, close action, and flat settings rows.',
    anatomy: ['Settings flyout header', 'Close command', 'Flat settings rows'],
    preview: PortalSettingsFlyoutPreview,
  },
  {
    familyId: 'portal-activity-flyout',
    summary: 'Activity flyout using the notification pane surface for actionable Portal activity updates.',
    anatomy: ['Activity flyout', 'Notification row', 'Affected resource copy', 'Inline action'],
    preview: PortalActivityFlyoutPreview,
  },
  {
    familyId: 'create-stepped-form-blade',
    summary: 'A worked create flow with portal shell, breadcrumb, blade header, horizontal numbered steps, narrow form column, and docked footer.',
    anatomy: ['Portal header', 'Breadcrumb row', 'Blade title', 'Horizontal numbered step list', '728px form column', 'Docked footer'],
    preview: CreateSteppedFormPatternPreview,
  },
  {
    familyId: 'browse-resource',
    summary: 'Find and filter resources that need attention while keeping actions, grid results, and pagination in one flow.',
    anatomy: ['Blade header', 'Toolbar', 'Filter strip', 'Dense grid', 'Pager or footer actions'],
    preview: BrowseResourcePatternPreview,
  },
  {
    familyId: 'notifications',
    summary: 'Actionable notifications connect the update, the affected resource, and the remediation step.',
    anatomy: ['Notification pane', 'Context pane', 'Status rows', 'Empty state'],
    preview: NotificationsPatternPreview,
  },
  {
    familyId: 'delete-resource',
    summary: 'Delete flows are implication-first confirmations with explicit recovery/dependency language.',
    anatomy: ['Dependency review', 'Implication copy', 'Danger action', 'Acknowledgement gate'],
    preview: DeleteResourcePatternPreview,
  },
  {
    familyId: 'manage-resource',
    summary: 'Manage private access and network settings with compact forms, scoped navigation, and nearby status lists.',
    anatomy: ['Scoped navigation', 'Compact form rows', 'Expandable sections', 'Status cells'],
    preview: ManageResourcePatternPreview,
  },
  {
    familyId: 'service-overview',
    summary: 'Summarize service health, recommendations, and next actions with tightly scoped cards.',
    anatomy: ['Overview cards', 'Action strip', 'Concise status copy', 'Card footer'],
    preview: ServiceOverviewPatternPreview,
  },
  {
    familyId: 'feedback-ces-cva',
    summary: 'Feedback surfaces stay lightweight with a clear prompt, local input, and restrained footer action.',
    anatomy: ['Prompt copy', 'Input area', 'Footer affordance', 'Non-blocking action hierarchy'],
    preview: FeedbackPatternPreview,
  },
  {
    familyId: 'pattern-index',
    summary: 'The pattern index helps teams browse available examples without treating the index as a product workflow.',
    anatomy: ['Pattern list', 'Short descriptions', 'Clean row alignment'],
    preview: PatternIndexPreview,
  },
];

function formatPatternFamilyName(family: PatternFamily) {
  switch (family.id) {
    case 'aks-resource-list':
      return 'AKS resource list';
    case 'portal-global-search':
      return 'Global search';
    case 'portal-settings-flyout':
      return 'Settings flyout';
    case 'portal-activity-flyout':
      return 'Activity flyout';
    case 'create-stepped-form-blade':
      return 'Create resource flow';
    case 'browse-resource':
      return 'Browse resources';
    case 'delete-resource':
      return 'Delete resource';
    case 'manage-resource':
      return 'Manage resource';
    case 'feedback-ces-cva':
      return 'Feedback footer';
    case 'pattern-index':
      return 'Pattern index';
    default:
      return family.name;
  }
}

function getPatternUsageGuidance(familyId: string): string[] {
  switch (familyId) {
    case 'aks-resource-list':
      return ['Use for Azure Kubernetes Service browse pages that need resource group, subscription, type, and status in the first scan.', 'Keep names synthetic in examples and keep commands, filters, and results in one compact flow.'];
    case 'portal-global-search':
      return ['Use for shell-level search surfaces that combine services and recent resources.', 'Keep result rows compact and grouped by the task people are trying to resume.'];
    case 'portal-settings-flyout':
      return ['Use for shell settings that should open without navigating away from the current blade.', 'Keep settings rows flat and short so the flyout scans quickly.'];
    case 'portal-activity-flyout':
      return ['Use for actionable Portal activity or notification updates.', 'Keep the affected resource, severity, and next action close together.'];
    case 'create-stepped-form-blade':
      return ['Use for multi-step create flows that need validation before submit.', 'Keep the step list, form column, and footer actions visible together.'];
    case 'browse-resource':
      return ['Use when people need to find resources that need attention.', 'Keep search, filters, result count, grid, and pagination in one compact flow.'];
    case 'notifications':
      return ['Use when an update should lead directly to a remediation task.', 'Keep the message, affected resource, and action close together.'];
    case 'delete-resource':
      return ['Use when destructive actions require consequence review.', 'Keep dependent resources and recovery details visible before confirmation.'];
    case 'manage-resource':
      return ['Use for focused configuration tasks such as private access or network settings.', 'Keep routine changes inline unless the task truly requires a wizard.'];
    case 'service-overview':
      return ['Use for service health, recommendations, and next actions.', 'Keep each card scoped to one decision or follow-up action.'];
    case 'feedback-ces-cva':
      return ['Use for lightweight customer feedback after the primary task is complete.', 'Keep the prompt short and subordinate to the page task.'];
    default:
      return ['Use this index to choose a pattern before opening the rendered example.', 'Treat the index as navigation, not as a product workflow.'];
  }
}

function getPatternInteractionGuidance(familyId: string): string[] {
  switch (familyId) {
    case 'aks-resource-list':
      return ['Default to dense table rows for scan-heavy Kubernetes resource lists.', 'Use clear synthetic names in reusable examples rather than real tenant or resource identifiers.'];
    case 'portal-global-search':
      return ['Preserve keyboard focus in the search box while results update.', 'Avoid turning the search flyout into a full blade unless people need a multi-step task.'];
    case 'portal-settings-flyout':
      return ['Keep close and escape behavior predictable.', 'Use short labels and avoid nested cards inside the flyout.'];
    case 'portal-activity-flyout':
      return ['Use unread emphasis only for items that still require attention.', 'Prefer one direct action over multiple competing links.'];
    case 'delete-resource':
      return ['Do not enable destructive actions until acknowledgement requirements are met.', 'Use danger styling on the action, not across the whole page.'];
    case 'manage-resource':
      return ['Preserve scoped navigation for larger settings sets.', 'Use compact rows and expandable sections to keep the current task readable.'];
    case 'service-overview':
      return ['Prefer recommendations and next steps over decorative metrics.', 'Keep primary actions close to the overview they affect.'];
    case 'feedback-ces-cva':
      return ['Do not let feedback compete with save, create, or delete actions.', 'Use plain language such as feedback or customer satisfaction.'];
    case 'notifications':
      return ['Keep notifications actionable when they indicate a problem.', 'Avoid disconnected notification walls that do not lead to a next step.'];
    default:
      return ['Start with the rendered example for layout and interaction behavior.', 'Compose only the reusable parts needed for the task at hand.'];
  }
}

function MetadataCard({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="azf-showcase-metadata-card">
      <Text as="h3" weight="semibold">{title}</Text>
      {children}
    </section>
  );
}

function IconBrowserView() {
  const [query, setQuery] = useState('');
  const [collection, setCollection] = useState('All collections');

  const normalizedQuery = query.trim().toLowerCase();

  const filteredIcons = useMemo(() => (
    iconBrowserItems.filter((item) => (
      normalizedQuery.length === 0
      || item.name.toLowerCase().includes(normalizedQuery)
      || item.alias.toLowerCase().includes(normalizedQuery)
      || item.source.toLowerCase().includes(normalizedQuery)
    ))
  ), [normalizedQuery]);

  const matchingCatalogIcons = useMemo(() => (
    azureCatalogIcons.filter((icon) => {
      const matchesCollection = collection === 'All collections' || icon.collection === collection;
      const matchesQuery = normalizedQuery.length === 0
        || icon.name.toLowerCase().includes(normalizedQuery)
        || icon.collection.toLowerCase().includes(normalizedQuery);
      return matchesCollection && matchesQuery;
    })
  ), [normalizedQuery, collection]);

  const visibleCatalogIcons = matchingCatalogIcons.slice(0, AZURE_ICON_GRID_CAP);
  const hiddenCatalogCount = matchingCatalogIcons.length - visibleCatalogIcons.length;

  return (
    <AzureIconProvider registry={showcaseIconRegistry}>
      <section className="azf-showcase-app__surface azf-showcase-icon-catalog">
        <div className="azf-showcase-app__surface-header">
          <div className="azf-showcase-component-browser__header-block">
            <Text as="h2" size={600} weight="semibold">Icon browser</Text>
            <Text className="azf-muted">
              Browse the Azure icon set ({azureCatalogIcons.length} glyphs) plus a few registered icon examples.
              Filter by name or collection and inspect rendered icons.
            </Text>
          </div>
        </div>

        <div className="azf-showcase-icon-catalog__controls">
          <Input
            aria-label="Filter icons"
            value={query}
            onChange={(_, data) => setQuery(data.value)}
            contentBefore={<SearchRegular />}
            placeholder="Filter icons by name or alias"
            className="azf-showcase-icon-catalog__search"
          />
          <Dropdown
            aria-label="Filter by collection"
            value={collection}
            selectedOptions={[collection]}
            onOptionSelect={(_, data) => setCollection(data.optionValue ?? 'All collections')}
          >
            {azureCatalogCollections.map((name) => (
              <Option key={name} value={name}>{name}</Option>
            ))}
          </Dropdown>
        </div>

        <div className="azf-showcase-icon-catalog__stats">
          <Badge appearance="outline">{iconCatalogSnapshot.collections} collections</Badge>
          <Badge appearance="outline">{iconCatalogSnapshot.uniqueSvgPayloads} unique SVGs</Badge>
          <Badge appearance="outline">{iconCatalogSnapshot.duplicatePayloads} duplicate aliases</Badge>
        </div>

        <div className="azf-showcase-icon-catalog__section">
          <Text as="h3" size={400} weight="semibold">Icon examples</Text>
          <div className="azf-showcase-icon-catalog__tiles">
            {filteredIcons.map((item) => (
              <div key={item.name} className="azf-showcase-icon-catalog__tile">
                <div className="azf-showcase-inline-actions">
                  <AzureIcon name={item.name} label={item.name} size={20} />
                  <div className="azf-showcase-icon-catalog__tile-copy">
                    <Text weight="semibold">{item.name}</Text>
                    <Text className="azf-muted">{item.alias}</Text>
                  </div>
                </div>
                <Text className="azf-muted">{item.source}</Text>
                <Text className="azf-muted">{item.note}</Text>
              </div>
            ))}
            {filteredIcons.length === 0 && (
              <div className="azf-showcase-icon-catalog__tile">
                <Text weight="semibold">No registry icons matched</Text>
                <Text className="azf-muted">Try a broader name, alias, or source term.</Text>
              </div>
            )}
          </div>
        </div>

        <div className="azf-showcase-icon-catalog__section">
          <div className="azf-showcase-icon-catalog__section-head">
            <Text as="h3" size={400} weight="semibold">Azure icon catalog</Text>
            <Text className="azf-muted">
              Showing {visibleCatalogIcons.length} of {matchingCatalogIcons.length} icons
              {hiddenCatalogCount > 0 ? ' — refine the search or collection to narrow further' : ''}
            </Text>
          </div>
          <div className="azf-showcase-icon-grid">
            {visibleCatalogIcons.map((icon) => (
              <figure
                key={`${icon.collection}/${icon.name}`}
                className="azf-showcase-icon-grid__tile"
                title={`${icon.collection} · ${icon.name}`}
              >
                <img
                  className="azf-showcase-icon-grid__glyph"
                  src={icon.src}
                  alt={icon.name}
                  loading="lazy"
                  width={24}
                  height={24}
                />
                <figcaption className="azf-showcase-icon-grid__caption">
                  <Text size={200} weight="semibold" wrap={false} truncate className="azf-showcase-icon-grid__name">
                    {icon.name}
                  </Text>
                  <Text size={100} wrap={false} truncate className="azf-muted azf-showcase-icon-grid__collection">
                    {icon.collection}
                  </Text>
                </figcaption>
              </figure>
            ))}
            {matchingCatalogIcons.length === 0 && (
              <div className="azf-showcase-icon-catalog__tile">
                <Text weight="semibold">No catalog icons matched</Text>
                <Text className="azf-muted">Try a different name or collection.</Text>
              </div>
            )}
          </div>
        </div>
      </section>
    </AzureIconProvider>
  );
}

function ComponentInventoryPlaceholder({
  item,
  onOpenRelatedPreview,
}: {
  item: ComponentInventoryBrowserEntry;
  onOpenRelatedPreview?: () => void;
}) {
  return (
    <section className="azf-showcase-preview" aria-label="Component preview">
      <div className="azf-showcase-preview__canvas azf-showcase-preview__canvas--placeholder">
        <div className="azf-showcase-placeholder">
          <Text weight="semibold">Standalone example not available</Text>
          <Text className="azf-muted">{item.summary}</Text>
          <div className="azf-showcase-placeholder__meta">
            <Text className="azf-muted">Guidance: {item.nextAction}</Text>
          </div>
          {onOpenRelatedPreview && (
            <div>
              <Button appearance="secondary" onClick={onOpenRelatedPreview}>
                Open related example
              </Button>
            </div>
          )}
        </div>
      </div>
    </section>
  );
}

export function AzureFluentShowcaseApp() {
  const [view, setView] = useState<ShowcaseView>('components');
  const [selectedComponentNodeId, setSelectedComponentNodeId] = useState('30028:627');
  const [componentQuery, setComponentQuery] = useState('');
  const [selectedPattern, setSelectedPattern] = useState('aks-resource-list');

  const selectedInventoryItem = publicShowcaseComponentInventoryEntries.find((item) => item.nodeId === selectedComponentNodeId) ?? publicShowcaseComponentInventoryEntries[0];
  const componentPreview = selectedInventoryItem.previewEntry;
  const componentInventoryGroups = uniqueStrings(selectedInventoryItem.exportNames).flatMap((exportName) => componentGroupsByExport[exportName] ?? []);
  const componentMcpNodes = componentInventoryGroups.flatMap((group) => group.mcpNodes ?? []);
  const componentExamplePaths = uniqueStrings(componentInventoryGroups.flatMap((group) => group.publicExamples ?? []));
  const componentImplementationFiles = uniqueStrings(componentInventoryGroups.flatMap((group) => group.implementationFiles ?? []));
  const renderedPreviewNodeIdByExport = useMemo(() => showcaseComponentInventoryEntries.reduce<Record<string, string>>((accumulator, item) => {
    if (item.coverageStatus === 'implemented-rendered') {
      item.exportNames.forEach((exportName) => {
        accumulator[exportName] ??= item.nodeId;
      });
    }
    return accumulator;
  }, {}), []);
  const filteredComponentInventory = useMemo(() => {
    const normalizedQuery = componentQuery.trim().toLowerCase();

    return publicShowcaseComponentInventoryEntries.filter((item) => {
      const matchesQuery = normalizedQuery.length === 0
        || item.title.toLowerCase().includes(normalizedQuery)
        || item.pageName.toLowerCase().includes(normalizedQuery)
        || item.exportLabel.toLowerCase().includes(normalizedQuery)
        || item.nodeId.toLowerCase().includes(normalizedQuery);

      return matchesQuery;
    });
  }, [componentQuery]);
  const filteredCopilotInventory = filteredComponentInventory.filter(isCopilotComponentInventoryEntry);
  const filteredGeneralInventory = filteredComponentInventory.filter((item) => !isCopilotComponentInventoryEntry(item));
  const SelectedComponentPreview = componentPreview?.preview;
  const canRenderSelectedPreview = selectedInventoryItem.coverageStatus === 'implemented-rendered' && Boolean(SelectedComponentPreview);

  const selectedPatternFamily = showcasePatternFamilies.find((family) => family.id === selectedPattern) ?? showcasePatternFamilies[0];
  const selectedPatternPreview = patternPreviewCatalog.find((entry) => entry.familyId === selectedPattern) ?? patternPreviewCatalog[0];
  const SelectedPatternPreview = selectedPatternPreview.preview;

  const primaryTabs = showcaseTabs.map((tab) => ({
    id: tab.id,
    label: tab.label,
    description: tab.description,
  }));

  return (
    <AzureFluentProvider density="compact">
      <div className="azf-showcase-app">
        <header className="azf-showcase-app__header">
          <div className="azf-showcase-app__header-copy">
            <Text as="h1" size={700} weight="semibold">Azure Fluent System showcase</Text>
            <Text className="azf-muted">
              Three focused views: rendered component examples, composed product patterns, and a dedicated icon browser.
            </Text>
          </div>
          <AzureTabList
            ariaLabel="Showcase primary views"
            className="azf-showcase-app__view-tabs"
            selectedValue={view}
            onTabSelect={(value) => setView(value as ShowcaseView)}
            tabs={primaryTabs.map((tab) => ({ id: tab.id, label: tab.label, description: tab.description }))}
          />
        </header>

        <PortalCaptureHighlights
          onOpenPattern={(patternId) => {
            setSelectedPattern(patternId);
            setView('patterns');
          }}
        />

        <div className="azf-showcase-app__content">
          <aside className="azf-showcase-app__sidebar">
            {view === 'components' ? (
              <>
                <Text as="h2" weight="semibold">Component browser</Text>
                <div className="azf-showcase-form-column">
                  <Input
                    aria-label="Filter components"
                    value={componentQuery}
                    onChange={(_, data) => setComponentQuery(data.value)}
                    contentBefore={<SearchRegular />}
                    placeholder="Filter by component or area"
                  />
                </div>
                <div className="azf-showcase-nav-list" role="list" aria-label="Component entries">
                  {filteredCopilotInventory.length > 0 && (
                    <div className="azf-showcase-nav-list__family">
                      <Text as="span" size={200} weight="semibold">Copilot</Text>
                    </div>
                  )}
                  {filteredCopilotInventory.map((item) => (
                    <div key={item.nodeId} role="listitem">
                      <button
                        type="button"
                        className="azf-showcase-nav-item azf-showcase-nav-item--inventory"
                        aria-label={item.title}
                        data-selected={item.nodeId === selectedInventoryItem.nodeId || undefined}
                        onClick={() => setSelectedComponentNodeId(item.nodeId)}
                      >
                        <span className="azf-showcase-nav-item__copy">
                          <span>{item.title}</span>
                        </span>
                      </button>
                    </div>
                  ))}
                  {filteredGeneralInventory.map((item) => (
                    <div key={item.nodeId} role="listitem">
                      <button
                        type="button"
                        className="azf-showcase-nav-item azf-showcase-nav-item--inventory"
                        aria-label={item.title}
                        data-selected={item.nodeId === selectedInventoryItem.nodeId || undefined}
                        onClick={() => setSelectedComponentNodeId(item.nodeId)}
                      >
                        <span className="azf-showcase-nav-item__copy">
                          <span>{item.title}</span>
                          <span className="azf-showcase-nav-item__meta-copy">{item.pageName}</span>
                        </span>
                      </button>
                    </div>
                  ))}
                  {filteredComponentInventory.length === 0 && (
                    <div className="azf-showcase-empty-note" role="listitem">
                      <Text weight="semibold">No components matched</Text>
                      <Text className="azf-muted">Try a broader name or area.</Text>
                    </div>
                  )}
                </div>
              </>
            ) : view === 'patterns' ? (
              <>
                <Text as="h2" weight="semibold">Pattern browser</Text>
                <Text className="azf-muted">
                  Explore task-focused Azure Fluent flows and see how components work together.
                </Text>
                <div className="azf-showcase-nav-list">
                  {showcasePatternFamilies.map((family) => (
                    <button
                      key={family.id}
                      type="button"
                      className="azf-showcase-nav-item azf-showcase-nav-item--pattern"
                      data-selected={family.id === selectedPattern || undefined}
                      onClick={() => setSelectedPattern(family.id)}
                    >
                      <span>{formatPatternFamilyName(family)}</span>
                    </button>
                  ))}
                </div>
              </>
            ) : (
              <>
                <Text as="h2" weight="semibold">Icons</Text>
                <Text className="azf-muted">
                  Search icon names and aliases, then inspect the rendered icons.
                </Text>
              </>
            )}
          </aside>

          <main className="azf-showcase-app__main">
            {view === 'components' ? (
              <>
                <section className="azf-showcase-app__surface azf-showcase-component-browser">
                  <div className="azf-showcase-app__surface-header azf-showcase-component-browser__surface-header">
                    <div className="azf-showcase-component-browser__header-block">
                      <Text as="h2" size={500} weight="semibold">{selectedInventoryItem.title}</Text>
                      <Text size={300} className="azf-muted">{selectedInventoryItem.summary}</Text>
                    </div>
                  </div>

                  <div className="azf-showcase-component-browser__body">
                    <section className="azf-showcase-component-preview-panel" aria-label="Component example">
                    {canRenderSelectedPreview && SelectedComponentPreview ? (
                      <SelectedComponentPreview />
                      ) : (
                        <ComponentInventoryPlaceholder
                          item={selectedInventoryItem}
                          onOpenRelatedPreview={selectedInventoryItem.exportNames
                            .map((exportName) => renderedPreviewNodeIdByExport[exportName])
                            .find(Boolean)
                            ? () => {
                              const mappedNodeId = selectedInventoryItem.exportNames
                                .map((exportName) => renderedPreviewNodeIdByExport[exportName])
                                .find(Boolean);
                              if (mappedNodeId) setSelectedComponentNodeId(mappedNodeId);
                            }
                            : undefined}
                        />
                      )}
                    </section>
                  </div>
                </section>

                <details className="azf-showcase-disclosure">
                  <summary className="azf-showcase-disclosure__summary">
                    <div className="azf-showcase-disclosure__summary-copy">
                      <Text as="span" weight="semibold">Reference details</Text>
                      <Text as="span" className="azf-muted">
                        Additional source and code details stay below the main preview.
                      </Text>
                    </div>
                    <div className="azf-showcase-badge-row">
                      <Badge appearance="outline">{selectedInventoryItem.pageName}</Badge>
                      <Badge appearance="outline">{selectedInventoryItem.type}</Badge>
                    </div>
                  </summary>

                  <div className="azf-showcase-disclosure__body">
                    <div className="azf-showcase-disclosure__grid">
                      <section className="azf-showcase-disclosure__section">
                        <Text as="h3" weight="semibold">Browser guidance</Text>
                        <Text className="azf-muted">{selectedInventoryItem.summary}</Text>
                        <Text className="azf-muted">Guidance: {selectedInventoryItem.nextAction}</Text>
                      </section>

                      <section className="azf-showcase-disclosure__section">
                        <Text as="h3" weight="semibold">Design reference</Text>
                        <Text className="azf-muted">{selectedInventoryItem.pageName}</Text>
                        {selectedInventoryItem.nodeUrl && (
                          <Text className="azf-muted">
                            <Link href={selectedInventoryItem.nodeUrl} target="_blank" rel="noreferrer">
                              Open design reference
                            </Link>
                          </Text>
                        )}
                        <Text className="azf-muted">Component API: {selectedInventoryItem.exportLabel}</Text>
                        {selectedInventoryItem.sourceNodes.length > 1 && (
                          <div className="azf-showcase-metadata-block">
                            <Text weight="semibold">Related design pieces</Text>
                            <ul className="azf-showcase-list azf-showcase-list--compact">
                              {selectedInventoryItem.sourceNodes.map((node) => (
                                <li key={node.nodeId}>{formatDesignReferenceName(node.name)}</li>
                              ))}
                            </ul>
                          </div>
                        )}
                      </section>

                      <section className="azf-showcase-disclosure__section">
                        <Text as="h3" weight="semibold">Build references</Text>
                        <div className="azf-showcase-metadata-block">
                          <Text weight="semibold">Example paths</Text>
                          {componentExamplePaths.length > 0 ? (
                            <ul className="azf-showcase-list azf-showcase-list--compact">
                              {componentExamplePaths.map((examplePath) => <li key={examplePath}>{examplePath}</li>)}
                            </ul>
                          ) : (
                            <Text className="azf-muted">No example path listed.</Text>
                          )}
                        </div>
                        <div className="azf-showcase-metadata-block">
                          <Text weight="semibold">Library files</Text>
                          {componentImplementationFiles.length > 0 ? (
                            <ul className="azf-showcase-list azf-showcase-list--compact">
                              {componentImplementationFiles.map((filePath) => <li key={filePath}>{filePath}</li>)}
                            </ul>
                          ) : (
                            <Text className="azf-muted">No library file list available.</Text>
                          )}
                        </div>
                      </section>

                      {(componentPreview || componentMcpNodes.length > 0) && (
                        <section className="azf-showcase-disclosure__section">
                          <Text as="h3" weight="semibold">Preview notes</Text>
                          {componentPreview ? (
                            <ul className="azf-showcase-list azf-showcase-list--compact">
                              {componentPreview.usageNotes.map((note) => <li key={note}>{note}</li>)}
                            </ul>
                          ) : (
                            <Text className="azf-muted">This entry does not have a standalone rendered example yet.</Text>
                          )}
                          {componentMcpNodes.length > 0 && (
                            <ul className="azf-showcase-list azf-showcase-list--compact">
                              {componentMcpNodes.slice(0, 4).map((node) => (
                                <li key={`${selectedInventoryItem.nodeId}-${node.component}-${node.nodeId ?? node.status}`}>
                                  {formatDesignReferenceName(node.component)}: {formatComponentReferenceStatus(node.status)}
                                </li>
                              ))}
                            </ul>
                          )}
                        </section>
                      )}
                    </div>
                  </div>
                </details>
              </>
            ) : view === 'patterns' ? (
              <>
                <section className="azf-showcase-app__surface">
                  <div className="azf-showcase-app__surface-header">
                    <div>
                      <Text as="h2" size={600} weight="semibold">{formatPatternFamilyName(selectedPatternFamily)}</Text>
                      <Text className="azf-muted">{selectedPatternPreview.summary}</Text>
                    </div>
                  </div>
                  <SelectedPatternPreview />
                </section>

                <ComposedScenariosSection />
                <ReusablePatternComponentsSection />

                <div className="azf-showcase-app__metadata-grid">
                  <MetadataCard title="When to use">
                    <ul className="azf-showcase-list">
                      {getPatternUsageGuidance(selectedPatternFamily.id).map((item) => <li key={item}>{item}</li>)}
                    </ul>
                  </MetadataCard>
                  <MetadataCard title="Key anatomy">
                    <ul className="azf-showcase-list">
                      {selectedPatternPreview.anatomy.map((item) => <li key={item}>{item}</li>)}
                    </ul>
                  </MetadataCard>
                  <MetadataCard title="Interaction guidance">
                    <ul className="azf-showcase-list">
                      {getPatternInteractionGuidance(selectedPatternFamily.id).map((item) => <li key={item}>{item}</li>)}
                    </ul>
                  </MetadataCard>
                  <MetadataCard title="Using this pattern">
                    <ul className="azf-showcase-list">
                      <li>Start with the rendered example for layout and interaction behavior.</li>
                      <li>Keep the copy specific to the product task and affected resource.</li>
                      <li>Use only the pieces needed for the task at hand.</li>
                    </ul>
                  </MetadataCard>
                </div>
              </>
            ) : (
              <IconBrowserView />
            )}
          </main>
        </div>
      </div>
    </AzureFluentProvider>
  );
}
