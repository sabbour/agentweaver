import type { AuthConfigResponse, AuthMode } from './types';

/** An Entra administration destination and the label shown for it. */
export interface EntraAdminLink {
  href: string;
  label: string;
}

/** Converts the server's authentication mode label to the browser contract. */
export function authConfigModeToAuthMode(mode: AuthConfigResponse['mode'] | null | undefined): AuthMode | null {
  if (mode === 'Entra') return 'entra';
  return null;
}

/**
 * Builds the most specific Entra administration link available from auth config.
 * Returns `null` when required tenant or client identifiers are absent.
 */
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
