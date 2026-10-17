/** Icon registry and rendering helpers for Fluent glyphs and locally hosted IconCloud assets. */
import './tokens.css';
import { mergeClasses } from '@fluentui/react-components';
import { DocumentRegular } from '@fluentui/react-icons';
export type { FluentIcon } from '@fluentui/react-icons';
export {
  AddRegular,
  AlertRegular,
  Apps24Regular,
  AppsListDetailRegular,
  ArchiveRegular,
  ArrowClockwiseRegular,
  ArrowImportRegular,
  ArrowMaximizeRegular,
  ArrowRepeatAllRegular,
  ArrowRoutingRegular,
  ArrowSync24Regular,
  ArrowSyncRegular,
  ArrowUpload24Regular,
  BeakerRegular,
  Board24Regular,
  BookToolbox24Regular,
  Bot24Regular,
  BotRegular,
  BracesRegular,
  Brain24Regular,
  BranchFork24Regular,
  BranchRegular,
  Chat20Regular,
  ChatRegular,
  CheckmarkCircleFilled,
  CheckmarkCircleRegular,
  CheckmarkRegular,
  ChevronDownRegular,
  ChevronLeftRegular,
  ChevronRightRegular,
  ChevronUpRegular,
  CircleRegular,
  ClockRegular,
  Code24Regular,
  CodeRegular,
  ColorRegular,
  CommentRegular,
  CopyRegular,
  DataPie24Regular,
  Delete24Regular,
  DeleteRegular,
  Dismiss24Regular,
  DismissCircleFilled,
  DismissCircleRegular,
  DismissRegular,
  DocumentAddRegular,
  DocumentArrowDownRegular,
  DocumentBulletListRegular,
  DocumentDismissRegular,
  DocumentEditRegular,
  DocumentPdfRegular,
  DocumentRegular,
  DocumentTextRegular,
  EditRegular,
  ErrorCircleFilled,
  ErrorCircleRegular,
  Eye24Regular,
  EyeRegular,
  Flow24Regular,
  FlowRegular,
  Flowchart24Regular,
  FlowchartRegular,
  FolderOpenRegular,
  FolderRegular,
  GlobeRegular,
  Heart24Regular,
  Home24Regular,
  HourglassRegular,
  ImageRegular,
  InfoRegular,
  LockClosedRegular,
  MergeRegular,
  NetworkCheckRegular,
  NotebookRegular,
  OpenRegular,
  PanelLeftContract24Regular,
  PanelLeftExpand24Regular,
  People24Regular,
  PeopleTeamRegular,
  PersonAddRegular,
  PersonClockRegular,
  PersonFeedbackRegular,
  PersonRegular,
  PlayCircleRegular,
  PlayRegular,
  Pulse24Regular,
  PuzzlePiece20Regular,
  QuestionCircleFilled,
  SearchRegular,
  SendRegular,
  Server24Regular,
  ServerRegular,
  Settings24Regular,
  SettingsRegular,
  Shield24Regular,
  ShieldCheckmarkRegular,
  ShieldKeyholeRegular,
  ShieldLockRegular,
  ShieldRegular,
  SignOutRegular,
  SparkleRegular,
  Stethoscope24Regular,
  StopRegular,
  SubtractCircleRegular,
  TaskListSquareLtrRegular,
  TasksAppRegular,
  WarningFilled,
  WarningRegular,
  WindowConsoleRegular,
  WrenchRegular,
  ZoomInRegular,
  ZoomOutRegular,
} from '@fluentui/react-icons';
import { createContext, useContext } from 'react';
import type { CSSProperties, ReactElement, ReactNode } from 'react';
/* eslint-disable react-refresh/only-export-components */






// Icon source hierarchy:
// 1. General system icons come from @fluentui/react-icons, which is the React package
//    for the microsoft/fluentui-system-icons family already installed in this app.
// 2. Azure/resource-specific glyphs should be exported by a signed-in user from
//    IconCloud and registered through AzureIconProvider/createIconCloudRegistry.
// 3. Figma Community iconography files are visual reference only unless assets are
//    explicitly exported under acceptable terms.
// `useAzureIconRegistry` and the `createIconCloudRegistry*` functions are
// non-visual helper APIs for wiring icon data into AzureIconProvider; the visible
// showcase representation is AzureIconProvider + AzureIcon.

/** Supported square pixel sizes for AzureIcon output. */
export type AzureIconSize = 12 | 16 | 18 | 20 | 24 | 28 | 32;

/** Renderable icon content or asset metadata stored in an icon registry. */
export interface AzureIconDefinition {
  element?: ReactElement;
  src?: string;
  alt?: string;
  className?: string;
}

/** Name-to-icon lookup provided to AzureIcon descendants. */
export type AzureIconRegistry = Record<string, AzureIconDefinition | ReactElement>;

/** One manifest entry describing an icon asset and optional catalog metadata. */
export interface IconCloudManifestIcon {
  name: string;
  collection?: string;
  category?: string;
  collections?: readonly string[];
  file: string;
  hash?: string;
}

/** Manifest container accepted by the registry builder. */
export interface IconCloudManifest<TIcon extends IconCloudManifestIcon = IconCloudManifestIcon> {
  icons: readonly TIcon[];
}

const AzureIconRegistryContext = createContext<AzureIconRegistry>({});

/** Props for making a registry available to AzureIcon components in a React subtree. */
export interface AzureIconProviderProps {
  registry: AzureIconRegistry;
  children: ReactNode;
}

/** Supplies icon definitions to nested AzureIcon components through context. */
export function AzureIconProvider({ registry, children }: AzureIconProviderProps) {
  return <AzureIconRegistryContext.Provider value={registry}>{children}</AzureIconRegistryContext.Provider>;
}

/** Reads the nearest icon registry, or the empty registry when no provider is mounted. */
export function useAzureIconRegistry() {
  return useContext(AzureIconRegistryContext);
}

/**
 * Builds a typed icon registry from names and a shared asset path.
 * The generated source path follows `basePath/prefix/name.extension`.
 */
export function createIconCloudRegistry<TName extends string>(
  iconNames: readonly TName[],
  options: { basePath: string; extension?: 'svg' | 'png' | 'webp'; prefix?: string },
): Record<TName, AzureIconDefinition> {
  const extension = options.extension ?? 'svg';
  return Object.fromEntries(
    iconNames.map((name) => [
      name,
      {
        src: `${options.basePath.replace(/\/$/, '')}/${options.prefix ?? ''}${name}.${extension}`,
        alt: name,
      },
    ]),
  ) as unknown as Record<TName, AzureIconDefinition>;
}

/** Path and filtering options for converting a manifest into icon definitions. */
export interface CreateIconCloudRegistryFromManifestOptions<TIcon extends IconCloudManifestIcon = IconCloudManifestIcon> {
  basePath: string;
  filter?: (icon: TIcon) => boolean;
  getKey?: (icon: TIcon) => string;
}

/** Joins an asset path while normalizing separators from manifest file names. */
function createAssetUrl(basePath: string, file: string) {
  const base = basePath.replace(/\/$/, '');
  const normalizedFile = file.replace(/\\/g, '/').replace(/^\/+/, '');
  return `${base}/${normalizedFile}`;
}

/**
 * Builds registry entries from either a manifest object or a list of icon records.
 * A custom key function can distinguish repeated icon names across categories.
 */
export function createIconCloudRegistryFromManifest<TIcon extends IconCloudManifestIcon = IconCloudManifestIcon>(
  manifest: IconCloudManifest<TIcon> | readonly TIcon[],
  options: CreateIconCloudRegistryFromManifestOptions<TIcon>,
): AzureIconRegistry {
  const icons: readonly TIcon[] = Array.isArray(manifest) ? manifest : (manifest as IconCloudManifest<TIcon>).icons;
  return Object.fromEntries(
    icons.filter((icon) => options.filter?.(icon) ?? true).map((icon) => {
      const key = options.getKey?.(icon) ?? icon.name;
      return [
        key,
        {
          src: createAssetUrl(options.basePath, icon.file),
          alt: icon.name,
        },
      ];
    }),
  );
}

/** Accessible or decorative icon inputs; a registry entry may provide the source and alt text. */
export interface AzureIconProps {
  name?: string;
  icon?: ReactNode;
  src?: string;
  label?: string;
  size?: AzureIconSize;
  decorative?: boolean;
  className?: string;
}

/** Normalizes direct React elements and structured registry entries to one definition shape. */
function resolveDefinition(definition: AzureIconDefinition | ReactElement | undefined): AzureIconDefinition | undefined {
  if (!definition) return undefined;
  if ('type' in definition && 'props' in definition) return { element: definition };
  return definition;
}

/**
 * Renders a direct icon, registered asset, or fallback document glyph at the requested size.
 * Without a label the icon is decorative by default; provide one to expose it as an image.
 */
export function AzureIcon({ name, icon, src, label, size = 20, decorative = !label, className }: AzureIconProps) {
  const registry = useAzureIconRegistry();
  const definition = resolveDefinition(name ? registry[name] : undefined);
  const resolvedSrc = src ?? definition?.src;
  const resolvedIcon = icon ?? definition?.element ?? (!resolvedSrc ? <DocumentRegular /> : undefined);
  const accessibleLabel = label ?? definition?.alt ?? name;
  const style: CSSProperties = { width: size, height: size, justifyContent: 'center', flex: `0 0 ${size}px` };

  return (
    <span
      className={mergeClasses('azf-row', definition?.className, className)}
      style={style}
      aria-hidden={decorative ? true : undefined}
      aria-label={!decorative ? accessibleLabel : undefined}
      role={!decorative ? 'img' : undefined}
    >
      {resolvedSrc ? <img src={resolvedSrc} alt="" width={size} height={size} /> : resolvedIcon}
    </span>
  );
}
