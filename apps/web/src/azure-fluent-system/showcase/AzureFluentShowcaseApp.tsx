import { useMemo, useState, type ReactElement, type ReactNode } from 'react';
import {
  Avatar,
  AvatarGroup,
  AvatarGroupItem,
  Badge,
  Breadcrumb,
  BreadcrumbButton,
  BreadcrumbDivider,
  BreadcrumbItem,
  Card,
  CardHeader,
  Checkbox,
  ColorSwatch,
  CounterBadge,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DialogTrigger,
  Divider,
  DrawerBody,
  DrawerHeader,
  DrawerHeaderTitle,
  Dropdown,
  Field,
  InfoLabel,
  InlineDrawer,
  InteractionTag,
  InteractionTagPrimary,
  Label,
  List,
  ListItem,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  NavDrawer,
  NavDrawerBody,
  NavItem,
  Option,
  Persona,
  PresenceBadge,
  Radio,
  RadioGroup,
  Rating,
  RatingDisplay,
  SearchBox,
  Skeleton,
  SkeletonItem,
  SpinButton,
  Spinner,
  SwatchPicker,
  Switch,
  Tag,
  TagGroup,
  TeachingPopover,
  TeachingPopoverBody,
  TeachingPopoverHeader,
  TeachingPopoverSurface,
  TeachingPopoverTitle,
  TeachingPopoverTrigger,
  Textarea,
  Toast,
  ToastBody,
  ToastTitle,
  Toolbar,
  ToolbarButton,
  ToolbarDivider,
  Tooltip,
  Tree,
  TreeItem,
  TreeItemLayout,
} from '@fluentui/react-components';
import {
  AddRegular,
  AppsListRegular,
  ArrowClockwiseRegular,
  CalendarLtrRegular,
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
import {
  AgenticProgress,
  AzureAccordion,
  AzureDataGrid,
  AzureEmptyState,
  AzureFluentProvider,
  AzureForm,
  AzureIcon,
  AzureIconProvider,
  AzureSlider,
  AzureStepList,
  AzureTabList,
  AzureToolbar,
  BladeHeader,
  Button,
  CodeSnippet,
  CommandBar,
  CopyButton,
  CopilotComposer,
  CopilotResponse,
  CopilotPromptRibbon,
  ChainOfThought,
  DeleteResourceDialog,
  EssentialsGrid,
  FeedbackFooter,
  FileUpload,
  FilterableComboBox,
  FilterPills,
  FormFieldRow,
  FormFooter,
  Input,
  InlineCopilot,
  Link,
  NotificationPane,
  Pager,
  PortalLayout,
  PortalRail,
  PortalTopNav,
  ProgressBarWithLabel,
  ResourceTagEditor,
  ServiceMenu,
  Text,
} from '..';
import {
  AgenticApprovalPattern,
  BrowseResourcePattern,
  CoordinatorRunPattern,
  CopilotWorkspacePattern,
  ManageResourcePattern,
  NotificationPattern,
  ServiceOverviewPattern,
} from '../patterns';
import { componentCatalogData, patternCatalogData } from './catalogData';
import iconSummary from '../assets/icons/azure/iconcloud-azure-icons-full-summary.json';
import azureIconManifest from '../assets/icons/azure/azure-icons-manifest.json';
import storageAccountsIcon from '../assets/icons/azure/assets/storage--storage-accounts--c993bb3f7b83.svg';
import virtualMachineIcon from '../assets/icons/azure/assets/compute--virtual-machine--7a04018565e2.svg';
import './showcase.css';

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

type ComponentInventoryFilter = 'all' | 'implemented-rendered' | 'showcase-placeholder' | 'needs-mcp-extraction' | 'needs-implementation' | 'local-only-needed';

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

const componentInventory = componentCatalogData as ComponentInventoryManifest;
const patternGuide = patternCatalogData as PatternInventoryManifest;

const showcaseTabs = [
  { id: 'components', label: 'Components', description: 'Preview library primitives' },
  { id: 'patterns', label: 'Patterns', description: 'Browse composed examples' },
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
  FluentFallback: { element: <SparkleRegular />, alt: 'Fluent fallback icon' },
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
  {
    name: 'FluentFallback',
    alias: 'Generic status/action',
    source: 'Fluent UI fallback',
    note: 'Use when a local Azure asset is not part of the shipped icon set.',
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
  { id: '1', name: 'aks-observability-prod-eus', owner: 'Platform SRE', location: 'East US', status: 'Needs attention' },
  { id: '2', name: 'stsharedarchive01', owner: 'Operations', location: 'West US 2', status: 'Healthy' },
  { id: '3', name: 'vnet-platform-hub', owner: 'Networking', location: 'Central US', status: 'Updating' },
];

const gridColumns = [
  { columnId: 'name', header: 'Resource', sortable: true, sortValue: (item: ResourceRecord) => item.name, renderCell: (item: ResourceRecord) => item.name },
  { columnId: 'owner', header: 'Owner', sortable: true, sortValue: (item: ResourceRecord) => item.owner, renderCell: (item: ResourceRecord) => item.owner },
  { columnId: 'location', header: 'Location', sortable: true, sortValue: (item: ResourceRecord) => item.location, renderCell: (item: ResourceRecord) => item.location },
  { columnId: 'status', header: 'Status', sortable: true, sortValue: (item: ResourceRecord) => item.status, renderCell: (item: ResourceRecord) => item.status },
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
      { text: '"appi-observability-prod-eus"' },
    ],
  },
  { lineNumber: 10, indentLevel: 2, text: '}', tokens: [{ text: '}', tone: 'operator' as const }] },
  { lineNumber: 11, indentLevel: 1, text: ']', tokens: [{ text: ']', tone: 'operator' as const }] },
  { lineNumber: 12, text: '}', tokens: [{ text: '}', tone: 'operator' as const }] },
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
        <Text>Telemetry drift was isolated to the East US cluster and two dependent workbooks.</Text>
        <CodeSnippet title="Kusto" lines={codeSnippetLines.slice(0, 4)} maxHeight={152} />
      </div>
    ),
    supportingText: '1 request left',
  },
  {
    id: 'confirmation',
    type: 'confirmation' as const,
    content: 'Run the remediation script against the selected cluster?',
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
    title: 'Request production approval',
    body: 'The next step modifies live clusters and may increase spend.',
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
    body: "To proceed, I need your approval to access and modify resources in the 'Contoso Production' subscription. This will let me automatically apply the necessary fixes and optimizations.",
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
    id: 'copilot',
    label: 'Copilot',
    items: [
      { id: 'chat', label: 'Workspace chat' },
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
      return 'Rendered';
    case 'showcase-placeholder':
      return 'Not implemented yet';
    case 'needs-mcp-extraction':
      return 'Needs Figma extraction';
    case 'needs-implementation':
      return 'Needs local implementation';
    case 'local-only-needed':
      return 'Local follow-up';
    default:
      return status;
  }
}

function getComponentNextAction(status: string, exportNames: readonly string[]) {
  switch (status) {
    case 'implemented-rendered':
      return exportNames.length > 0
        ? `Open the ${exportNames.join(', ')} preview and verify the local implementation.`
        : 'Open the live preview and verify the local implementation.';
    case 'showcase-placeholder':
      return exportNames.length > 0
        ? `This entry can reuse local work from ${exportNames.join(', ')} once it has its own dedicated implementation.`
        : 'This entry still needs its own dedicated implementation.';
    case 'needs-mcp-extraction':
      return 'Extract this Figma component before promoting it into a standalone local preview.';
    case 'needs-implementation':
      return 'Build a dedicated local preview for this Figma component.';
    case 'local-only-needed':
      return 'Decide whether this inventory row should become a reusable local export or remain a documented follow-up.';
    default:
      return 'Review the inventory row and decide the next local implementation step.';
  }
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
        <Badge appearance="tint">Showcase verification</Badge>
        <Text weight="semibold">{title}</Text>
      </div>
      <Text className="azf-muted">{body}</Text>
      <ul className="azf-showcase-list azf-showcase-list--compact">
        {items.map((item) => (
          <li key={item}>{item}</li>
        ))}
      </ul>
    </section>
  );
}

function PortalShellPreview() {
  const [menuQuery, setMenuQuery] = useState('net');

  return (
    <PreviewCard title="Portal shell preview" frameClassName="azf-showcase-preview__frame--portal">
      <div className="azf-showcase-demo-grid azf-showcase-demo-grid--two-up">
        <section className="azf-showcase-demo-panel">
          <div className="azf-showcase-demo-panel__copy">
            <Text weight="semibold">Portal shell</Text>
            <Text className="azf-muted">Local shell preview with neutral showcase framing so the portal chrome keeps its own shape.</Text>
          </div>
          <PortalLayout
            className="azf-showcase-shell-preview"
            topNav={(
              <PortalTopNav
                brand={{ product: 'Microsoft Azure', area: 'Portal' }}
                startActions={[
                  { id: 'all-services', label: 'All services', icon: <AppsListRegular /> },
                  { id: 'toggle-nav', label: 'Toggle navigation', icon: <NavigationRegular /> },
                ]}
                searchValue="platform"
                onSearchChange={() => undefined}
                copilotAction={{ id: 'copilot', label: 'Copilot', icon: <SparkleRegular /> }}
                endActions={[{ id: 'settings', label: 'Settings', icon: <SettingsRegular /> }]}
                persona={{ name: 'Ahmed Sabbour', secondaryText: 'Contoso Engineering', icon: <PersonCircleRegular /> }}
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
            breadcrumb={<Text>Home / Resource type</Text>}
            header={<BladeHeader title="Resource Type" subtitle="Portal shell, blade header, and focused task body" />}
            commandBar={(
              <CommandBar
                primaryActions={[
                  { id: 'create', label: 'Create', appearance: 'primary', onClick: () => undefined },
                  { id: 'refresh', label: 'Refresh', icon: <ArrowClockwiseRegular />, onClick: () => undefined },
                ]}
              />
            )}
            footer={<FeedbackFooter body="Keep command surfaces flat and restrained inside the shell." action={{ id: 'feedback', label: 'Give feedback', onClick: () => undefined }} />}
          >
            <AzureDataGrid items={resourceRows.slice(0, 2)} columns={[...gridColumns]} />
          </PortalLayout>
        </section>
        <section className="azf-showcase-demo-panel">
          <div className="azf-showcase-demo-panel__copy">
            <Text weight="semibold">Portal navigation details</Text>
            <Text className="azf-muted">Search and navigation states stay adjacent to the shell without wrapping the shell itself in showcase labels.</Text>
          </div>
          <div className="azf-showcase-demo-grid">
            <section className="azf-showcase-demo-panel azf-showcase-demo-panel--subtle">
              <Text weight="semibold">Expanded search menu</Text>
              <Text className="azf-muted">Search, nested navigation, badges, and favorites stay visible instead of hiding behind metadata.</Text>
              <ServiceMenu
                groups={serviceMenuGroups.map((group) => ({ ...group, items: [...group.items] }))}
                selectedId="private-access"
                searchValue={menuQuery}
                onSearchChange={setMenuQuery}
                onSelect={() => undefined}
                onToggleFavorite={() => undefined}
              />
            </section>
            <section className="azf-showcase-demo-panel azf-showcase-demo-panel--subtle">
              <Text weight="semibold">Collapsed navigation row</Text>
              <Text className="azf-muted">Compact icon-only mode keeps the same information architecture reachable in narrow layouts.</Text>
              <ServiceMenu
                groups={serviceMenuGroups.map((group) => ({ ...group, items: [...group.items] }))}
                selectedId="overview"
                searchable={false}
                collapsed
                onSelect={() => undefined}
              />
            </section>
          </div>
          <RelatedCoverageNote
            title="Related shell details visible here"
            body="The shell preview calls out related portal surfaces that share the same local implementation."
            items={[
              '.Search Menu (40971:35680)',
              '.Menu header (32610:9876)',
              '.search button (32610:9923)',
              'Service Menu item (41544:8562)',
              '.L1 Mobile Nav (35431:15337)',
            ]}
          />
        </section>
      </div>
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
          <Input id="component-preview-subscription" value="Contoso Platform Production" readOnly />
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

function NotificationPanePreview() {
  return (
    <PreviewCard title="Notification pane preview">
      <NotificationPane
        items={[
          {
            id: 'notification-1',
            title: 'Firewall validation blocked',
            body: 'Resolve the private endpoint policy before the next rollout.',
            tone: 'warning',
            timestamp: 'Now',
            unread: true,
            actions: [{ id: 'open', label: 'Open resource', onClick: () => undefined }],
          },
          {
            id: 'notification-2',
            title: 'Backup policy updated',
            body: 'Nightly snapshots now apply to every production account in West US 2.',
            tone: 'success',
            timestamp: '2 min ago',
          },
        ]}
      />
    </PreviewCard>
  );
}

function FeedbackFooterPreview() {
  return (
    <PreviewCard title="Feedback footer preview">
      <FeedbackFooter
        title="Was this task flow clear?"
        body="Feedback/CES/CVA guidance keeps the affordance low emphasis and right-aligned."
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
          resourceName="stcontososhared01"
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
  const [agentsOff, setAgentsOff] = useState(false);

  return (
    <PreviewCard title="Composer with attachment and stop flow">
      <div className="azf-showcase-demo-grid azf-showcase-demo-grid--two-up">
        <section className="azf-showcase-demo-panel">
          <Badge appearance="outline">Ready to send</Badge>
          <CopilotComposer
            value={readyPrompt}
            onChange={setReadyPrompt}
            onSend={() => undefined}
            agentMode={agentMode}
            onAgentModeChange={setAgentMode}
            attachments={[{ id: 'log', name: 'kubelet.log', onRemove: () => undefined }]}
            onAddAttachment={() => undefined}
          />
        </section>
        <section className="azf-showcase-demo-panel">
          <Badge appearance="outline">Running / agents off</Badge>
          <CopilotComposer
            value={runningPrompt}
            onChange={setRunningPrompt}
            onSend={() => undefined}
            isRunning
            onStop={() => undefined}
            agentMode={agentsOff}
            onAgentModeChange={setAgentsOff}
            attachments={[{ id: 'rollout', name: 'rollout-summary.md', onRemove: () => undefined }]}
            onAddAttachment={() => undefined}
          />
        </section>
      </div>
      <RelatedCoverageNote
        title="Related composer details visible here"
        body="This standalone card is the showcase gate for the extracted composer children rather than relying on metadata-only coverage."
        items={['Agent Toggle', 'Agents Off Icon', '.Input Footer_LG', '.Input Footer_Sm', '.Send_Icon']}
      />
    </PreviewCard>
  );
}

function CopilotResponsePreview() {
  return (
    <PreviewCard title="Response, confirmation, and action row">
      <div className="azf-showcase-demo-grid azf-showcase-demo-grid--two-up">
        <section className="azf-showcase-demo-panel">
          <Badge appearance="outline">Resolved answer</Badge>
          <CopilotResponse
            parts={[...copilotResponseParts]}
            actions={[
              { id: 'open-incident', label: 'Open incident', onClick: () => undefined },
              { id: 'copy-response', label: 'Copy response', onClick: () => undefined },
            ]}
          />
        </section>
        <section className="azf-showcase-demo-panel">
          <Badge appearance="outline">Loading and request count</Badge>
          <CopilotResponse
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
      <RelatedCoverageNote
        title="Related response details visible here"
        body="The response preview doubles as the verification surface for related Copilot response parts that share one reusable implementation."
        items={['.Footeractions', '.Code Snippet', '.data grid', '.single selection', '.Multiple selection', 'Request Count / Latency']}
      />
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
      <PreviewCard title="Chain of thought and approval-gated progress">
        <ChainOfThought
          title="Reasoning"
          subtitle={`${cotArtifacts.length} artifacts created`}
          steps={chainOfThoughtSteps.map((step) => ({ ...step }))}
          artifacts={cotArtifacts}
          onApprove={() => undefined}
          onDeny={() => undefined}
        />
        <RelatedCoverageNote
          title="Related chain-of-thought details visible here"
          body="ChainOfThought owns the reasoning header, Activity/Artifacts tabs, the actions-completed summary, the status-icon step rows, and the inline approval block (body, disclaimer, Approve/Deny) — all rebuilt from the Figma spec nodes rather than the generic accordion primitive."
          items={['Chain of thought', '.Reasoning (CoT)', '.Complete (CoT)', '.Needs user input (CoT)', '.Show artifacts (CoT)', '.Artifact row (CoT)', '.Approval (CoT)']}
        />
      </PreviewCard>
      <PreviewCard title="Agentic progress list (underlying primitive)">
        <AgenticProgress steps={[...agenticPreviewSteps]} defaultOpenItems={['approve']} onApprove={() => undefined} onDeny={() => undefined} />
        <RelatedCoverageNote
          title="Primitive reasoning stream"
          body="AgenticProgress is the accordion-based reasoning list used on its own where the full Chain-of-thought panel chrome isn't needed."
          items={['.Agentic List (CoT)', '.Action swap (CoT)', '.Artifact pill (CoT)']}
        />
      </PreviewCard>
    </>
  );
}

function CopilotWorkspacePreview() {
  const [prompt, setPrompt] = useState('Draft the remediation comment for the deployment review.');

  return (
    <PreviewCard title="Copilot workspace composition">
      <CopilotWorkspacePattern
        title="Copilot workspace"
        serviceMenuGroups={copilotWorkspaceGroups.map((group) => ({ ...group, items: [...group.items] }))}
        selectedMenuId="chat"
        onMenuSelect={() => undefined}
        response={{ parts: [...copilotResponseParts] }}
        composer={{
          value: prompt,
          onChange: setPrompt,
          onSend: () => undefined,
          attachments: [{ id: 'summary', name: 'summary.md' }],
        }}
      />
    </PreviewCard>
  );
}

function CoordinatorRunPreview() {
  const [steering, setSteering] = useState('Prioritize the East US remediation and hold the rest until it clears.');
  const runArtifacts = [...chainOfThoughtArtifacts];

  return (
    <CoordinatorRunPattern
      title="Coordinator · rollout-remediation-run"
      subtitle="Multi-agent run · 3 of 4 steps complete"
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
        placeholder: 'Steer the coordinator run…',
      }}
    />
  );
}

function AgenticApprovalPreview() {
  return (
    <AgenticApprovalPattern
      title="Approve production remediation"
      summary="The coordinator paused for a human decision before modifying live clusters."
      steps={agenticPreviewSteps.map((step) => ({ ...step }))}
      defaultOpenItems={['approve']}
      onApprove={() => undefined}
      onDeny={() => undefined}
    />
  );
}

const composedScenarios: {
  id: string;
  title: string;
  exportName: string;
  summary: string;
  lineage: string;
  preview: () => ReactElement;
}[] = [
  {
    id: 'coordinator-run',
    title: 'Coordinator run workspace',
    exportName: 'CoordinatorRunPattern',
    summary:
      'Blade header over a run reasoning stream (ChainOfThought) with an aside for the run summary (CopilotResponse) and operator steering (CopilotComposer). Maps directly to the Agentweaver CoordinatorRunPage.',
    lineage: 'BladeHeader 32615:9834 · ChainOfThought 386:75088 · CopilotResponse 32382:38129 · CopilotComposer 32382:38468',
    preview: CoordinatorRunPreview,
  },
  {
    id: 'agentic-approval',
    title: 'Agentic approval checkpoint',
    exportName: 'AgenticApprovalPattern',
    summary:
      'Compact human-in-the-loop approval card built on AgenticProgress. Maps to the CoordinatorRunPage automation-toggle / approval-gate moment where a run pauses for consent.',
    lineage: 'AgenticProgress 27950:10571 / 27880:13472 · loader 386:75129 · ArtifactPill 27865:11293',
    preview: AgenticApprovalPreview,
  },
];

function ComposedScenariosSection() {
  return (
    <section className="azf-showcase-app__surface azf-showcase-scenarios">
      <div className="azf-showcase-app__surface-header">
        <div>
          <Text as="h2" size={600} weight="semibold">Composed scenarios</Text>
          <Text className="azf-muted">
            Library-authored compositions beyond the eight Figma pattern families. Each reuses only MCP-grounded primitives; constituent node IDs are cited under every preview.
          </Text>
        </div>
        <Badge appearance="tint" color="brand">Library composition</Badge>
      </div>
      <div className="azf-showcase-scenarios__grid">
        {composedScenarios.map((scenario) => {
          const ScenarioPreview = scenario.preview;
          return (
            <article key={scenario.id} className="azf-stack azf-gap-s">
              <div className="azf-stack azf-gap-xs">
                <div className="azf-row azf-showcase-scenario__head">
                  <Text as="h3" size={500} weight="semibold">{scenario.title}</Text>
                  <span className="azf-showcase-scenario__export">{scenario.exportName}</span>
                </div>
                <Text className="azf-muted">{scenario.summary}</Text>
              </div>
              <PreviewCard title={scenario.title}>
                <ScenarioPreview />
              </PreviewCard>
              <Text size={200} className="azf-showcase-scenario__lineage">MCP lineage · {scenario.lineage}</Text>
            </article>
          );
        })}
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
        <FileUpload label="Uploading" state="progress" fileName="prod-cert.pfx" progress={0.6} />
        <FileUpload label="Uploaded" state="success" fileName="prod-cert.pfx" />
        <FileUpload label="Bulk import" state="dragdrop" multiple />
      </div>
    </PreviewCard>
  );
}

function FilterableComboBoxPreview() {
  const [selected, setSelected] = useState<string | undefined>('sub-prod');

  return (
    <PreviewCard title="Filterable combo box preview">
      <div className="azf-showcase-form-column">
        <FilterableComboBox
          label="Subscription"
          info="Type to filter across all subscriptions you can access."
          placeholder="Select a subscription"
          options={[
            { id: 'sub-prod', label: 'Contoso Production' },
            { id: 'sub-stage', label: 'Contoso Staging' },
            { id: 'sub-dev', label: 'Contoso Development' },
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
        persona={{ name: 'Ahmed Sabbour', secondaryText: 'Contoso Engineering', icon: <PersonCircleRegular /> }}
      />
    </PreviewCard>
  );
}

function ServiceMenuPreview() {
  const [menuQuery, setMenuQuery] = useState('');
  return (
    <PreviewCard title="Service menu preview">
      <ServiceMenu
        groups={serviceMenuGroups.map((group) => ({ ...group, items: [...group.items] }))}
        selectedId="overview"
        searchValue={menuQuery}
        onSearchChange={setMenuQuery}
        onSelect={() => undefined}
        onToggleFavorite={() => undefined}
      />
    </PreviewCard>
  );
}

function BladeHeaderPreview() {
  const [pinned, setPinned] = useState(false);
  const [starred, setStarred] = useState(true);
  return (
    <PreviewCard title="Blade header preview">
      <BladeHeader
        title="Contoso Platform Production"
        menuLabel="Overview"
        subtitle="Storage account"
        resourceIcon={<img src={storageAccountsIcon} alt="" width={28} height={28} />}
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
          { id: 'vm', label: 'contoso-vm' },
          { id: 'storage', label: 'contosostorage' },
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
            <Input id="azure-form-name" value="contoso-platform" readOnly />
          </FormFieldRow>
          <FormFieldRow label="Region" htmlFor="azure-form-region">
            <Input id="azure-form-region" value="East US 2" readOnly />
          </FormFieldRow>
        </AzureForm>
      </div>
    </PreviewCard>
  );
}

function EssentialsGridPreview() {
  return (
    <PreviewCard title="Essentials preview">
      <EssentialsGrid
        properties={[
          { id: 'rg', label: 'Resource group', value: 'contoso-platform-rg', href: '#' },
          { id: 'status', label: 'Status', value: 'Running' },
          { id: 'location', label: 'Location', value: 'East US 2' },
          { id: 'subscription', label: 'Subscription', value: 'Contoso Production', href: '#' },
          { id: 'sub-id', label: 'Subscription ID', value: '9f2a1c40-c71b' },
          { id: 'tags', label: 'Tags', value: 'environment : production', tags: ['costCenter : 4415'] },
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
    <PreviewCard title="Filter pills preview">
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
    </PreviewCard>
  );
}

// ---------------------------------------------------------------------------
// Fluent 2 foundation components
//
// The Azure UI Kit is built on Fluent 2 (`@fluentui/react-components`). The base
// primitives on the Figma "Azure UI Kit (Fluent 2)" foundations page (node 25156-116,
// mirrored at https://fluent2.microsoft.design/components/web/react/) are consumed
// straight from Fluent v9, re-exported from `azure-fluent-system/foundations.tsx`, and
// surfaced here as live previews merged into the same Components inventory so agents can
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
        <BreadcrumbItem><BreadcrumbButton current>rg-prod-eastus</BreadcrumbButton></BreadcrumbItem>
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
          description={<Text className="azf-muted">stprodeastus · Healthy</Text>}
        />
        <Text>2 containers · East US · Standard LRS</Text>
      </Card>
    ),
  },
  {
    exportName: 'Carousel',
    title: 'Carousel',
    summary: 'Cycle through a set of cards or promotional slides in a bounded region.',
    usageNotes: ['Fluent 2 Carousel with CarouselSlider/CarouselCard/CarouselNav.', `Docs: ${FLUENT2_DOCS}carousel`],
    preview: () => (
      <div style={FOUNDATION_ROW}>
        <Card style={{ width: 160 }}><Text weight="semibold">Slide 1</Text><Text className="azf-muted">Getting started</Text></Card>
        <Card style={{ width: 160 }}><Text weight="semibold">Slide 2</Text><Text className="azf-muted">Best practices</Text></Card>
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
            <DialogTitle>Delete rg-prod-eastus?</DialogTitle>
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
      <div style={FOUNDATION_COL}>
        <Text>Overview</Text>
        <Divider />
        <Divider>Networking</Divider>
        <Text>Inbound rules</Text>
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
      </InlineDrawer>
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
        <ListItem>vm-prod-01 · Running</ListItem>
        <ListItem>vm-prod-02 · Stopped</ListItem>
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
            <MenuItem icon={<ArrowClockwiseRegular />}>Restart</MenuItem>
            <MenuItem icon={<DeleteRegular />}>Delete</MenuItem>
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
          <MessageBarBody><MessageBarTitle>Deployed</MessageBarTitle> vm-prod-01 is running.</MessageBarBody>
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
        <Tag>env: prod</Tag>
        <Tag dismissible>owner: platform</Tag>
        <InteractionTag><InteractionTagPrimary>East US</InteractionTagPrimary></InteractionTag>
      </TagGroup>
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
        <ToastBody>vm-prod-01 started successfully.</ToastBody>
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
            <TreeItem itemType="leaf"><TreeItemLayout>rg-prod-eastus</TreeItemLayout></TreeItem>
            <TreeItem itemType="leaf"><TreeItemLayout>rg-staging</TreeItemLayout></TreeItem>
          </Tree>
        </TreeItem>
      </Tree>
    ),
  },
];

const componentCatalog: ComponentPreviewEntry[] = [
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
    summary: 'AzureSlider wraps the Fluent Slider with an inline label, optional info tooltip, and a live value readout for scalar Azure inputs like vCores or throughput.',
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
    exportName: 'AzureToolbar',
    title: 'Toolbar',
    category: 'Shell and navigation',
    summary: 'AzureToolbar renders a Fluent Toolbar of subtle command buttons with dividers and an optional top-of-page bottom border for blade command strips.',
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
    summary: 'FilterableComboBox wraps the Fluent Combobox with client-side type-to-filter for long subscription, region, or resource pickers.',
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
    exportName: 'CopilotComposer',
    title: 'CopilotComposer',
    category: 'Copilot and agentic',
    summary: 'CopilotComposer keeps prompting, attachments, and stop controls in one compact task-local surface.',
    usageNotes: [
      'Use one attachment row and a short prompt region instead of stacking a full card shell around the composer.',
      'Reserve agent mode for workflow-oriented prompts that may request approvals or produce artifacts.',
    ],
    preview: CopilotComposerPreview,
  },
  {
    exportName: 'CopilotResponse',
    title: 'CopilotResponse',
    category: 'Copilot and agentic',
    summary: 'CopilotResponse handles text, confirmations, and lightweight action rows without exposing hidden reasoning.',
    usageNotes: [
      'Keep actions local to the generated answer, such as copy, open, or confirm.',
      'Use confirmation parts when the next step changes real resources or costs.',
    ],
    preview: CopilotResponsePreview,
  },
  {
    exportName: 'InlineCopilot',
    title: 'InlineCopilot',
    category: 'Copilot and agentic',
    summary: 'InlineCopilot provides contextual rewrite/generate help anchored to the field the user is already editing.',
    usageNotes: [
      'Anchor it near the current task instead of navigating the user to a separate chat surface.',
      'Offer only a few suggestion chips so the inline prompt stays small.',
    ],
    preview: InlineCopilotPreview,
  },
  {
    exportName: 'AgenticProgress',
    title: 'AgenticProgress',
    category: 'Copilot and agentic',
    summary: 'AgenticProgress exposes run status, artifacts, and approvals as a readable operator-first list.',
    usageNotes: [
      'Keep risk and approval language in the expanded row so the operator can decide in context.',
      'Artifact rows should point to specific outputs, not to generic activity feeds.',
    ],
    preview: AgenticProgressPreview,
  },
  {
    exportName: 'CopilotWorkspacePattern',
    title: 'CopilotWorkspacePattern',
    category: 'Copilot and agentic',
    summary: 'CopilotWorkspacePattern composes service navigation, response content, and the composer into a focused workspace.',
    usageNotes: [
      'Use it when Copilot is a primary task surface, not when the prompt should stay inline.',
      'Keep the menu narrow and the main content column dedicated to response + composer flow.',
    ],
    preview: CopilotWorkspacePreview,
  },
  {
    exportName: 'NotificationPane',
    title: 'NotificationPane',
    category: 'Status and feedback',
    summary: 'NotificationPane turns notification-family guidance into a reusable side-pane list with local actions and unread emphasis.',
    usageNotes: [
      'Use notification panes for actionable status near the affected task surface, not as a detached toast wall.',
      'Pair with contextual grids or detail panes when the notification opens a remediation workflow.',
    ],
    preview: NotificationPanePreview,
  },
  {
    exportName: 'FeedbackFooter',
    title: 'FeedbackFooter',
    category: 'Status and feedback',
    summary: 'FeedbackFooter captures CES/CVA footer guidance with restrained copy and right-aligned action emphasis.',
    usageNotes: [
      'Reserve the footer for feedback or next-step affordances after the main work is readable.',
      'Do not let feedback compete with primary task completion actions.',
    ],
    preview: FeedbackFooterPreview,
  },
  {
    exportName: 'DeleteResourceDialog',
    title: 'DeleteResourceDialog',
    category: 'Dialogs and confirmations',
    summary: 'DeleteResourceDialog keeps destructive actions explicit, consequence-driven, and optionally gated by acknowledgement.',
    usageNotes: [
      'Use soft-delete or recoverability copy when the service supports it.',
      'Keep danger styling focused on the destructive affordance, not the entire surrounding surface.',
    ],
    preview: DeleteDialogPreview,
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
    exportName: 'ServiceMenu',
    title: 'Service menu',
    category: 'Shell and navigation',
    summary: 'ServiceMenu renders grouped, searchable portal navigation with nested items, favorites, badges, and a collapsed icon-only mode.',
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
    summary: 'AzureForm wraps stacked form rows with an optional status message and a sticky footer for the primary and secondary actions.',
    usageNotes: [
      'Keep the form in a narrow reading column and let FormFieldRow own the label alignment.',
      'Use the message slot for form-level status instead of repeating it on every field.',
    ],
    preview: AzureFormPreview,
  },
  {
    exportName: 'EssentialsGrid',
    title: 'Essentials',
    category: 'Data and lists',
    summary: 'EssentialsGrid renders the collapsible resource-summary property grid with label/value pairs, links, and inline tags.',
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
    summary: 'FilterPills renders selectable category pills with an optional overflow menu for less-common facets above a result list.',
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

  const title = isInternalBucket
    ? 'Internal component layers'
    : previewEntry?.title ?? representative.name ?? representative.nodeId;

  const summary = isInternalBucket
    ? `${group.members.length} internal Figma sub-layers (rows, headers, popovers, and menu parts) that compose the components above rather than shipping as standalone exports.`
    : previewEntry?.summary ?? representative.coverageReason ?? inventoryRow?.implementedMapping ?? 'No checked-in preview summary yet.';

  const exportLabel = isInternalBucket
    ? 'Composed into other components'
    : exportNames.length > 0
      ? exportNames.join(', ')
      : 'No local export yet';

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
const foundationInventoryEntries: ComponentInventoryBrowserEntry[] = fluent2Foundations.map((spec) => {
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

function compareInventoryTitle(a: ComponentInventoryBrowserEntry, b: ComponentInventoryBrowserEntry): number {
  // Force the internal-layers bucket to the very end; otherwise sort alphabetically by title.
  const aInternal = a.title === 'Internal component layers';
  const bInternal = b.title === 'Internal component layers';
  if (aInternal !== bInternal) return aInternal ? 1 : -1;
  return a.title.localeCompare(b.title, 'en', { sensitivity: 'base' });
}

export const showcaseComponentInventoryEntries: ComponentInventoryBrowserEntry[] = [
  ...groupedComponentInventoryEntries,
  ...foundationInventoryEntries,
].sort(compareInventoryTitle);

export const showcaseComponentInventoryNodeIds = showcaseComponentInventoryEntries.map(({ nodeId }) => nodeId);

// Flat list of every Figma node represented across the grouped inventory (one entry per catalog row),
// used to guarantee the grouping never drops a component from coverage.
export const showcaseComponentInventorySourceNodeIds = showcaseComponentInventoryEntries.flatMap(
  ({ sourceNodes }) => sourceNodes.map((node) => node.nodeId),
);

export const showcaseComponentMenuExportNames = componentCatalog.map(({ exportName }) => exportName);
// Doctrine marker: Exactly two primary experiences: a component preview and a pattern example browser
// Doctrine marker: Built from `catalog/COMPONENTS.md`
// Doctrine marker: Built from `catalog/ICONS.md`
// Doctrine marker: Local source mappings

function CreateSteppedFormPatternPreview() {
  const [currentStep, setCurrentStep] = useState('basics');
  const [resourceName, setResourceName] = useState('aks-observability-prod-eus');
  const [resourceGroup, setResourceGroup] = useState('rg-observability-prod');

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
          persona={{ name: 'Ahmed Sabbour', secondaryText: 'Contoso Engineering', icon: <PersonCircleRegular /> }}
        />
      )}
      breadcrumb={<Text>Home / Observability / Create monitored resource</Text>}
      header={<BladeHeader title="Create monitored resource" subtitle="Contoso Platform Production" actions={[{ id: 'dismiss', label: 'Close', icon: <DismissRegular />, onClick: () => undefined }]} />}
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
          This example follows the concrete `3203:24770` anatomy: breadcrumb above blade title, horizontal numbered step list, narrow form column, and a docked footer.
        </Text>
        {currentStep === 'basics' && (
          <>
            <FormFieldRow label="Subscription" htmlFor="create-subscription" info="Inherited from the current landing zone context.">
              <Input id="create-subscription" value="Contoso Platform Production" readOnly />
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
              <Input id="create-subnet" value="snet-observability-private" readOnly />
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
    () => resourceRows.filter((row) => !query.trim() || row.name.toLowerCase().includes(query.toLowerCase())),
    [query],
  );

  return (
    <BrowseResourcePattern
      title="Browse resource"
      subtitle="Browse surfaces keep search, filter, grid, and pager in one compact flow."
      items={filteredRows}
      columns={[...gridColumns]}
      filters={[{ id: 'location', label: 'Location', value: 'All', selected: false }]}
      toolbarActions={[{ id: 'create', label: 'Create resource', appearance: 'primary', onClick: () => undefined }]}
      headerActions={[{ id: 'refresh', label: 'Refresh', icon: <ArrowClockwiseRegular />, onClick: () => undefined }]}
      searchValue={query}
      onSearchChange={setQuery}
      emptyState="No resources matched the current browse query."
    />
  );
}

function NotificationsPatternPreview() {
  return (
    <div className="azf-showcase-pattern-stack">
      <NotificationPane
        items={[
          {
            id: 'pane-1',
            title: 'Backup policy updated',
            body: 'Nightly snapshots now apply to every production account in West US 2.',
            tone: 'success',
            timestamp: '2 min ago',
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
      <AzureEmptyState
        title="Context pane is clear."
        body="Notification families often pair a side pane with an empty or grid-backed remediation region."
      />
    </div>
  );
}

function DeleteResourcePatternPreview() {
  const [acknowledged, setAcknowledged] = useState(false);
  return (
    <div className="azf-showcase-pattern-stack">
      <Text className="azf-showcase-copy">
        Delete families combine implication copy, dependency review, and a dialog or footer confirmation. The destructive action stays gated until the operator has reviewed the consequences.
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
          resourceName="stcontososhared01"
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
      header={<BladeHeader title="Manage monitored resource" subtitle="Compact management surfaces with local navigation" />}
      serviceMenu={<ServiceMenu groups={serviceMenuGroups.map((group) => ({ ...group, items: [...group.items] }))} selectedId="networking" onSelect={() => undefined} />}
    >
      <div className="azf-showcase-form-column">
        <FormFieldRow label="Public network access" htmlFor="manage-public-access" hint="Routine manage flows should stay inline instead of expanding into a wizard.">
          <Input id="manage-public-access" value="Disabled" readOnly />
        </FormFieldRow>
        <FormFieldRow label="Private endpoint" htmlFor="manage-private-endpoint">
          <Input id="manage-private-endpoint" value="pe-observability-prod-001" readOnly />
        </FormFieldRow>
        <AzureDataGrid items={resourceRows.slice(0, 2)} columns={[...gridColumns]} caption="Status cells and compact lists remain visible inside the management view." />
      </div>
    </ManageResourcePattern>
  );
}

function ServiceOverviewPatternPreview() {
  return (
    <ServiceOverviewPattern
      title="Service overview"
      subtitle="Overview families summarize status, follow-up actions, and concise card details."
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
        body="Feedback / CES / CVA families rely on clear prompts and restrained footer placement."
        action={{ id: 'share-feedback', label: 'Share feedback', onClick: () => undefined }}
      />
    </div>
  );
}

function PatternIndexPreview() {
  return (
    <div className="azf-showcase-pattern-index">
      {patternGuide.families.map((family) => (
        <button key={family.id} type="button" className="azf-showcase-pattern-index__item">
          <div className="azf-showcase-pattern-index__copy">
            <Text weight="semibold">{family.name}</Text>
            <Text className="azf-muted">{formatPatternDesignSource(family.status)} · {family.pageNodeId}</Text>
          </div>
          <Badge appearance="tint" color={getPatternReadiness(family.id).color}>
            {getPatternReadiness(family.id).label}
          </Badge>
        </button>
      ))}
    </div>
  );
}

const patternPreviewCatalog: PatternPreviewEntry[] = [
  {
    familyId: 'create-stepped-form-blade',
    summary: 'Concrete `3203:24770` worked example with portal shell, breadcrumb, blade header, horizontal numbered steps, narrow form column, and docked footer.',
    anatomy: ['Portal header', 'Breadcrumb row', 'Blade title', 'Horizontal numbered step list', '728px form column', 'Docked footer'],
    preview: CreateSteppedFormPatternPreview,
  },
  {
    familyId: 'browse-resource',
    summary: 'Browse flows keep command bar, filter strip, dense grid, and pager readable inside the shell.',
    anatomy: ['Blade header', 'Toolbar', 'Filter strip', 'Dense grid', 'Pager or footer actions'],
    preview: BrowseResourcePatternPreview,
  },
  {
    familyId: 'notifications',
    summary: 'Notification families combine pane content, local actions, and contextual empty/grid regions.',
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
    summary: 'Manage flows combine local navigation, compact forms, accordions, and status lists without widening into dashboard cards.',
    anatomy: ['Local navigation', 'Compact form rows', 'Accordion-like sections', 'Status cells'],
    preview: ManageResourcePatternPreview,
  },
  {
    familyId: 'service-overview',
    summary: 'Service overview families summarize action readiness with tightly scoped cards and concise follow-up actions.',
    anatomy: ['Overview cards', 'Action strip', 'Concise status copy', 'Card footer'],
    preview: ServiceOverviewPatternPreview,
  },
  {
    familyId: 'feedback-ces-cva',
    summary: 'Feedback/CES/CVA surfaces stay lightweight: clear prompt, local input, and restrained footer action.',
    anatomy: ['Prompt copy', 'Input area', 'Footer affordance', 'Non-blocking action hierarchy'],
    preview: FeedbackPatternPreview,
  },
  {
    familyId: 'pattern-index',
    summary: 'The pattern index is a taxonomy reference: use it to classify families and navigate examples, not as an end-user workflow.',
    anatomy: ['Pattern list', 'Status badges', 'Node references'],
    preview: PatternIndexPreview,
  },
];

type PatternReadiness = { label: string; color: 'success' | 'informative' };

// Q2 cleanup: implementation readiness and Figma extraction depth are two orthogonal
// signals that used to be collapsed into one raw "status" badge. Readiness answers
// "is this built and previewable?" — every family with a live preview plus a library
// mapping is "Live preview"; anything missing a preview falls back to "Reference only"
// so the badge can never overstate readiness.
function getPatternReadiness(familyId: string): PatternReadiness {
  const family = patternGuide.families.find((entry) => entry.id === familyId);
  const hasPreview = patternPreviewCatalog.some((entry) => entry.familyId === familyId);
  const hasImplementation = (family?.libraryMappings.length ?? 0) > 0;
  return hasPreview && hasImplementation
    ? { label: 'Live preview', color: 'success' }
    : { label: 'Reference only', color: 'informative' };
}

// Humanize the raw Figma extraction-depth label into a readable design-source badge.
function formatPatternDesignSource(status: string): string {
  switch (status) {
    case 'rich-context':
      return 'Rich design context';
    case 'page-index-only':
      return 'Page index';
    case 'component-inventory':
      return 'Component inventory';
    default:
      return status;
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
              Browse the vendored Azure icon set ({azureCatalogIcons.length} glyphs) plus the registry
              references used by the library. Filter by name or collection and inspect live local previews.
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
          <Text as="h3" size={400} weight="semibold">Registry references</Text>
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
    <section className="azf-showcase-preview" aria-label="Component inventory status">
      <div className="azf-showcase-preview__canvas azf-showcase-preview__canvas--placeholder">
        <div className="azf-showcase-placeholder">
          <div className="azf-showcase-section-heading">
            <Badge appearance="outline">{item.statusLabel}</Badge>
            {item.exportNames.length > 0 && <Badge appearance="outline">{item.exportLabel}</Badge>}
          </div>
          <Text weight="semibold">Live preview not available yet</Text>
          <Text className="azf-muted">{item.summary}</Text>
          <div className="azf-showcase-placeholder__meta">
            <Text className="azf-muted">Source node: {item.nodeId}</Text>
            <Text className="azf-muted">Next action: {item.nextAction}</Text>
          </div>
          {onOpenRelatedPreview && (
            <div>
              <Button appearance="secondary" onClick={onOpenRelatedPreview}>
                Open related preview
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
  const [componentFilter, setComponentFilter] = useState<ComponentInventoryFilter>('all');
  const [selectedPattern, setSelectedPattern] = useState('create-stepped-form-blade');

  const selectedInventoryItem = showcaseComponentInventoryEntries.find((item) => item.nodeId === selectedComponentNodeId) ?? showcaseComponentInventoryEntries[0];
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

    return showcaseComponentInventoryEntries.filter((item) => {
      const matchesFilter = componentFilter === 'all' || item.coverageStatus === componentFilter;
      const matchesQuery = normalizedQuery.length === 0
        || item.title.toLowerCase().includes(normalizedQuery)
        || item.pageName.toLowerCase().includes(normalizedQuery)
        || item.exportLabel.toLowerCase().includes(normalizedQuery)
        || item.nodeId.toLowerCase().includes(normalizedQuery);

      return matchesFilter && matchesQuery;
    });
  }, [componentFilter, componentQuery]);
  const componentCoverageSummary = `Showing ${showcaseComponentInventoryEntries.length} inventory items: ${showcaseComponentInventoryEntries.filter((item) => item.coverageStatus === 'implemented-rendered').length} rendered, ${showcaseComponentInventoryEntries.filter((item) => item.coverageStatus === 'showcase-placeholder').length} placeholders, ${showcaseComponentInventoryEntries.filter((item) => item.coverageStatus === 'needs-mcp-extraction').length} needing extraction, ${showcaseComponentInventoryEntries.filter((item) => item.coverageStatus === 'needs-implementation').length} needing implementation, ${showcaseComponentInventoryEntries.filter((item) => item.coverageStatus === 'local-only-needed').length} local follow-up.`;
  const SelectedComponentPreview = componentPreview?.preview;
  const canRenderSelectedPreview = selectedInventoryItem.coverageStatus === 'implemented-rendered' && Boolean(SelectedComponentPreview);

  const selectedPatternFamily = patternGuide.families.find((family) => family.id === selectedPattern) ?? patternGuide.families[0];
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
              Three focused views: live component previews, composed patterns, and a dedicated local icon browser.
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

        <div className="azf-showcase-app__content">
          <aside className="azf-showcase-app__sidebar">
            {view === 'components' ? (
              <>
                <Text as="h2" weight="semibold">Component inventory</Text>
                <Text className="azf-muted">
                  Browse every cataloged Figma component. Rendered entries open a live preview; the rest stay visible with status and next-step guidance.
                </Text>
                <Text className="azf-muted">{componentCoverageSummary}</Text>
                <div className="azf-showcase-form-column">
                  <Input
                    aria-label="Filter component inventory"
                    value={componentQuery}
                    onChange={(_, data) => setComponentQuery(data.value)}
                    contentBefore={<SearchRegular />}
                    placeholder="Filter by component, export, page, or node"
                  />
                </div>
                <div className="azf-showcase-filter-row" aria-label="Component inventory filters">
                  {([
                    ['all', 'All'],
                    ['implemented-rendered', 'Rendered'],
                    ['showcase-placeholder', 'Placeholder'],
                    ['needs-mcp-extraction', 'Needs extraction'],
                    ['needs-implementation', 'Needs implementation'],
                    ['local-only-needed', 'Local follow-up'],
                  ] as const).map(([filterValue, label]) => (
                    <button
                      key={filterValue}
                      type="button"
                      className="azf-showcase-filter-chip"
                      aria-pressed={componentFilter === filterValue}
                      data-selected={componentFilter === filterValue || undefined}
                      onClick={() => setComponentFilter(filterValue)}
                    >
                      {label}
                    </button>
                  ))}
                </div>
                <div className="azf-showcase-nav-list" role="list" aria-label="Component inventory entries">
                  {filteredComponentInventory.map((item) => (
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
                          <span className="azf-showcase-nav-item__meta-copy">{item.statusLabel} · {item.exportLabel}</span>
                          <span className="azf-showcase-nav-item__meta-copy">{item.pageName} · {item.nodeId}</span>
                        </span>
                      </button>
                    </div>
                  ))}
                  {filteredComponentInventory.length === 0 && (
                    <div className="azf-showcase-empty-note" role="listitem">
                      <Text weight="semibold">No components matched</Text>
                      <Text className="azf-muted">Try a broader name, export, page, or node.</Text>
                    </div>
                  )}
                </div>
              </>
            ) : view === 'patterns' ? (
              <>
                <Text as="h2" weight="semibold">Pattern browser</Text>
                <Text className="azf-muted">
                  Built from `catalog/PATTERNS.md`. Local examples, source mappings, and citations are checked in.
                </Text>
                <div className="azf-showcase-nav-list">
                  {patternGuide.families.map((family) => (
                    <button
                      key={family.id}
                      type="button"
                      className="azf-showcase-nav-item azf-showcase-nav-item--pattern"
                      data-selected={family.id === selectedPattern || undefined}
                      onClick={() => setSelectedPattern(family.id)}
                    >
                      <span>{family.name}</span>
                      <Badge appearance="tint" color={getPatternReadiness(family.id).color}>
                        {getPatternReadiness(family.id).label}
                      </Badge>
                    </button>
                  ))}
                </div>
              </>
            ) : (
              <>
                <Text as="h2" weight="semibold">Icons</Text>
                <Text className="azf-muted">
                  Search icon names and aliases, then inspect the live local previews.
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
                      <Text as="h2" size={600} weight="semibold">{selectedInventoryItem.title}</Text>
                      <Text className="azf-muted">{selectedInventoryItem.summary}</Text>
                    </div>
                    <div className="azf-showcase-badge-row">
                      <Badge appearance="outline">{selectedInventoryItem.statusLabel}</Badge>
                      <Badge appearance="outline">{selectedInventoryItem.exportLabel}</Badge>
                    </div>
                  </div>

                  <div className="azf-showcase-component-browser__body">
                    <section className="azf-showcase-component-preview-panel" aria-label="Live component preview">
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
                      <Text as="span" weight="semibold">Selection details</Text>
                      <Text as="span" className="azf-muted">
                        Selected-component metadata stays below the main surface so the preview or placeholder remains first.
                      </Text>
                    </div>
                    <div className="azf-showcase-badge-row">
                      <Badge appearance="outline">{selectedInventoryItem.pageName}</Badge>
                      <Badge appearance="outline">{selectedInventoryItem.type}</Badge>
                      <Badge appearance="outline">Showcase: {selectedInventoryItem.showcaseStatus}</Badge>
                    </div>
                  </summary>

                  <div className="azf-showcase-disclosure__body">
                    <div className="azf-showcase-disclosure__grid">
                      <section className="azf-showcase-disclosure__section">
                        <Text as="h3" weight="semibold">Status</Text>
                        <Text className="azf-muted">{selectedInventoryItem.summary}</Text>
                        <Text className="azf-muted">Next action: {selectedInventoryItem.nextAction}</Text>
                        <Text className="azf-muted">Extraction status: {selectedInventoryItem.extractionStatus}</Text>
                        <Text className="azf-muted">Extraction date: {selectedInventoryItem.extractionDate}</Text>
                      </section>

                      <section className="azf-showcase-disclosure__section">
                        <Text as="h3" weight="semibold">Source node</Text>
                        <Text className="azf-muted">{selectedInventoryItem.figmaNodeReference}</Text>
                        {selectedInventoryItem.nodeUrl && (
                          <Text className="azf-muted">
                            <Link href={selectedInventoryItem.nodeUrl} target="_blank" rel="noreferrer">
                              Open {selectedInventoryItem.nodeId}
                            </Link>
                          </Text>
                        )}
                        <Text className="azf-muted">Related local export: {selectedInventoryItem.exportLabel}</Text>
                        {selectedInventoryItem.sourceNodes.length > 1 && (
                          <div className="azf-showcase-metadata-block">
                            <Text weight="semibold">Grouped from {selectedInventoryItem.sourceNodes.length} Figma nodes</Text>
                            <ul className="azf-showcase-list azf-showcase-list--compact">
                              {selectedInventoryItem.sourceNodes.map((node) => (
                                <li key={node.nodeId}>{node.name} ({node.nodeId})</li>
                              ))}
                            </ul>
                          </div>
                        )}
                      </section>

                      <section className="azf-showcase-disclosure__section">
                        <Text as="h3" weight="semibold">Local files</Text>
                        <div className="azf-showcase-metadata-block">
                          <Text weight="semibold">Checked-in examples</Text>
                          {componentExamplePaths.length > 0 ? (
                            <ul className="azf-showcase-list azf-showcase-list--compact">
                              {componentExamplePaths.map((examplePath) => <li key={examplePath}>{examplePath}</li>)}
                            </ul>
                          ) : (
                            <Text className="azf-muted">No checked-in example path mapped.</Text>
                          )}
                        </div>
                        <div className="azf-showcase-metadata-block">
                          <Text weight="semibold">Implementation files</Text>
                          {componentImplementationFiles.length > 0 ? (
                            <ul className="azf-showcase-list azf-showcase-list--compact">
                              {componentImplementationFiles.map((filePath) => <li key={filePath}>{filePath}</li>)}
                            </ul>
                          ) : (
                            <Text className="azf-muted">No implementation file list mapped.</Text>
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
                            <Text className="azf-muted">This inventory row does not have a standalone live preview yet.</Text>
                          )}
                          {componentMcpNodes.length > 0 && (
                            <ul className="azf-showcase-list azf-showcase-list--compact">
                              {componentMcpNodes.slice(0, 4).map((node) => (
                                <li key={`${selectedInventoryItem.nodeId}-${node.component}-${node.nodeId ?? node.status}`}>
                                  {node.component}: {node.status}
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
                      <Text as="h2" size={600} weight="semibold">{selectedPatternFamily.name}</Text>
                      <Text className="azf-muted">{selectedPatternPreview.summary}</Text>
                    </div>
                    <div className="azf-showcase-badge-row">
                      <Badge appearance="tint" color={getPatternReadiness(selectedPatternFamily.id).color}>
                        {getPatternReadiness(selectedPatternFamily.id).label}
                      </Badge>
                      <Badge appearance="outline">{formatPatternDesignSource(selectedPatternFamily.status)}</Badge>
                    </div>
                  </div>
                  <SelectedPatternPreview />
                </section>

                <div className="azf-showcase-app__metadata-grid">
                  <MetadataCard title="Local examples & source mappings">
                    <ul className="azf-showcase-list">
                      {(selectedPatternFamily.localExamples ?? []).map((examplePath) => <li key={examplePath}>{examplePath}</li>)}
                      {(selectedPatternFamily.implementationFiles ?? []).map((filePath) => <li key={filePath}>{filePath}</li>)}
                    </ul>
                  </MetadataCard>
                  <MetadataCard title="Traceability citations">
                    <Text className="azf-muted">Local files are authoritative for ordinary usage; dev-mode URLs are citations only.</Text>
                    <Text className="azf-muted">
                      Dev-mode URL:{' '}
                      <Link href={selectedPatternFamily.pageNodeUrl} target="_blank" rel="noreferrer">
                        {selectedPatternFamily.pageNodeId}
                      </Link>
                    </Text>
                    <ul className="azf-showcase-list">
                      {selectedPatternFamily.representativeNodes.map((node) => (
                        <li key={node.nodeId}>
                          <Link href={node.url} target="_blank" rel="noreferrer">{node.nodeId}</Link> — {node.name} ({node.sourceType})
                        </li>
                      ))}
                    </ul>
                  </MetadataCard>
                  <MetadataCard title="Key anatomy">
                    <ul className="azf-showcase-list">
                      {selectedPatternPreview.anatomy.map((item) => <li key={item}>{item}</li>)}
                    </ul>
                  </MetadataCard>
                  <MetadataCard title="Anti-rules">
                    <ul className="azf-showcase-list">
                      {selectedPatternFamily.antiRules.map((rule) => <li key={rule}>{rule}</li>)}
                    </ul>
                  </MetadataCard>
                  <MetadataCard title="Library mappings">
                    <Text className="azf-muted">{selectedPatternFamily.libraryMappings.join(', ')}</Text>
                  </MetadataCard>
                  <MetadataCard title="Local workflow">
                    <ul className="azf-showcase-list">
                      {(patternGuide.localConsumptionWorkflow ?? []).map((step) => <li key={step}>{step}</li>)}
                    </ul>
                  </MetadataCard>
                </div>
                <ComposedScenariosSection />
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
