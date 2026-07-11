import type { ComponentProps, ReactNode } from 'react';
import { FluentProvider, type Theme, webLightTheme, mergeClasses } from '@fluentui/react-components';
import './tokens.css';
import type { AzfDensity } from './types';
export interface AzureFluentProviderProps extends Omit<ComponentProps<typeof FluentProvider>, 'theme'> {
  children: ReactNode;
  density?: AzfDensity;
  theme?: Theme;
}

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
