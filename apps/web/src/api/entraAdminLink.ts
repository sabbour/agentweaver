import type { AuthConfigResponse, AuthMode } from './types';

export interface EntraAdminLink {
  href: string;
  label: string;
}

export function authConfigModeToAuthMode(mode: AuthConfigResponse['mode'] | null | undefined): AuthMode | null {
  if (mode === 'Entra') return 'entra';
  if (mode === 'GitHubLegacy') return 'github-legacy';
  return null;
}

export function buildEntraAdminLink(
  entra: AuthConfigResponse['entra'] | null | undefined,
): EntraAdminLink | null {
  if (!entra?.client_id) return null;

  if (entra.enterprise_app_object_id) {
    return {
      href: `https://ms.portal.azure.com/#view/Microsoft_AAD_IAM/ManagedAppMenuBlade/~/Users/objectId/${encodeURIComponent(entra.enterprise_app_object_id)}/appId/${encodeURIComponent(entra.client_id)}`,
      label: 'Manage users in Azure Portal',
    };
  }

  if (!entra.tenant_id) return null;

  return {
    href: `https://entra.microsoft.com/${encodeURIComponent(entra.tenant_id)}/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/AppRoles/appId/${encodeURIComponent(entra.client_id)}/isMSAApp~/false`,
    label: 'Manage in Microsoft Entra ID',
  };
}
