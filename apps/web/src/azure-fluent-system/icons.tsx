/* eslint-disable react-refresh/only-export-components */
import type { CSSProperties, ReactElement, ReactNode } from 'react';
import { createContext, useContext } from 'react';
import { mergeClasses } from '@fluentui/react-components';
import { DocumentRegular } from '@fluentui/react-icons';
import './tokens.css';

// Icon source hierarchy:
// 1. General system icons come from @fluentui/react-icons, which is the React package
//    for the microsoft/fluentui-system-icons family already installed in this app.
// 2. Azure/resource-specific glyphs should be exported by a signed-in user from
//    IconCloud and registered through AzureIconProvider/createIconCloudRegistry.
// 3. Figma Community iconography files are visual reference only unless assets are
//    explicitly exported under acceptable terms.

export type AzureIconSize = 12 | 16 | 18 | 20 | 24 | 28 | 32;

export interface AzureIconDefinition {
  element?: ReactElement;
  src?: string;
  alt?: string;
  className?: string;
}

export type AzureIconRegistry = Record<string, AzureIconDefinition | ReactElement>;

export interface IconCloudManifestIcon {
  name: string;
  collection?: string;
  category?: string;
  collections?: readonly string[];
  file: string;
  hash?: string;
}

export interface IconCloudManifest<TIcon extends IconCloudManifestIcon = IconCloudManifestIcon> {
  icons: readonly TIcon[];
}

const AzureIconRegistryContext = createContext<AzureIconRegistry>({});

export interface AzureIconProviderProps {
  registry: AzureIconRegistry;
  children: ReactNode;
}

export function AzureIconProvider({ registry, children }: AzureIconProviderProps) {
  return <AzureIconRegistryContext.Provider value={registry}>{children}</AzureIconRegistryContext.Provider>;
}

export function useAzureIconRegistry() {
  return useContext(AzureIconRegistryContext);
}

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

export interface CreateIconCloudRegistryFromManifestOptions<TIcon extends IconCloudManifestIcon = IconCloudManifestIcon> {
  basePath: string;
  filter?: (icon: TIcon) => boolean;
  getKey?: (icon: TIcon) => string;
}

function createAssetUrl(basePath: string, file: string) {
  const base = basePath.replace(/\/$/, '');
  const normalizedFile = file.replace(/\\/g, '/').replace(/^\/+/, '');
  return `${base}/${normalizedFile}`;
}

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

export interface AzureIconProps {
  name?: string;
  icon?: ReactNode;
  src?: string;
  label?: string;
  size?: AzureIconSize;
  decorative?: boolean;
  className?: string;
}

function resolveDefinition(definition: AzureIconDefinition | ReactElement | undefined): AzureIconDefinition | undefined {
  if (!definition) return undefined;
  if ('type' in definition && 'props' in definition) return { element: definition };
  return definition;
}

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
