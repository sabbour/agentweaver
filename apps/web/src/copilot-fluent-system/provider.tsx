/** Theme and density adapter that scopes Fluent styling tokens to an Azure Fluent System surface. */
import type { ComponentProps, ReactNode } from 'react';
import { FluentProvider, type Theme, webLightTheme, mergeClasses } from '@fluentui/react-components';
import './tokens.css';
import type { AzfDensity } from './types';
/** Props accepted by the scoped Fluent provider; density selects the system's compact or cozy spacing. */
export interface AzureFluentProviderProps extends Omit<ComponentProps<typeof FluentProvider>, 'theme'> {
  children: ReactNode;
  density?: AzfDensity;
  theme?: Theme;
}

/** Applies the selected Fluent theme and system density classes to all descendants. */
export function AzureFluentProvider({
  children,
  density = 'cozy',
  theme = webLightTheme,
  className,
  ...props
}: AzureFluentProviderProps) {
  return (
    <FluentProvider
      {...props}
      theme={theme}
      className={mergeClasses('azf-theme', density === 'compact' ? 'azf-density-compact' : 'azf-density-cozy', className)}
    >
      {children}
    </FluentProvider>
  );
}
