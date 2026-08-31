import { AzureFluentProvider } from '../copilot-fluent-system';
import { PlatformSettingsPage } from '../pages/PlatformSettingsPage';
import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

describe('PlatformSettingsPage', () => {
  it('renders the platform settings shell', () => {
    render(
      <AzureFluentProvider density="compact">
        <PlatformSettingsPage />
      </AzureFluentProvider>,
    );

    expect(screen.getByText('Platform settings')).toBeDefined();
    expect(screen.getByText('Platform configuration')).toBeDefined();
  });
});
