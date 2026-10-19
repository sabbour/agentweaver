/** Permission descriptions and documentation links shown during sign-in. */
export interface ConsentScopeDescription {
  summary: string;
  tooltip: string;
  documentationUrl: string;
}

const AGENTWEAVER_SCOPES_URL =
  'https://sabbour.github.io/agentweaver/v1/guide/identity-scopes';

const SCOPE_DESCRIPTIONS: Readonly<Record<string, ConsentScopeDescription>> = {
  openid: {
    summary: 'Lets Agentweaver confirm who you are.',
    tooltip: 'Lets you sign in to Agentweaver. The sign-in service uses an account ID to recognize you.',
    documentationUrl: `${AGENTWEAVER_SCOPES_URL}#openid`,
  },
  profile: {
    summary: 'Requests your profile details.',
    tooltip: 'Agentweaver does not include profile details in the sign-in tokens it gives to apps.',
    documentationUrl: `${AGENTWEAVER_SCOPES_URL}#profile`,
  },
  email: {
    summary: 'Requests your email address.',
    tooltip: 'Agentweaver does not include your email address in the sign-in tokens it gives to apps.',
    documentationUrl: `${AGENTWEAVER_SCOPES_URL}#email`,
  },
  offline_access: {
    summary: 'Lets the app renew your sign-in while the page is open.',
    tooltip: 'The Web app keeps sign-in tokens in memory. Reloading the page clears them, so you may need to sign in again.',
    documentationUrl: `${AGENTWEAVER_SCOPES_URL}#offline-access`,
  },
  'api.read': {
    summary: 'Lets the app send requests to Agentweaver.',
    tooltip: 'This permission alone does not give the app project access or a role. Agentweaver checks your project access and the requested action.',
    documentationUrl: `${AGENTWEAVER_SCOPES_URL}#api-read`,
  },
  'projects.admin': {
    summary: 'Lets the app ask to manage a project.',
    tooltip: 'This permission does not make you a project administrator. Agentweaver checks your project access and role before each action.',
    documentationUrl: `${AGENTWEAVER_SCOPES_URL}#projects-admin`,
  },
  'projects.orchestrator': {
    summary: 'Lets the app work with project runs.',
    tooltip: 'For each run, Agentweaver checks your current project access and your assignment to manage runs. It checks that saved settings match this project and run. This permission alone does not give project access or a role.',
    documentationUrl: `${AGENTWEAVER_SCOPES_URL}#projects-orchestrator`,
  },
};

const UNKNOWN_SCOPE_DESCRIPTION: ConsentScopeDescription = {
  summary: 'Agentweaver does not explain this permission.',
  tooltip: 'The app or service defines what this permission allows. Ask your administrator before approving it.',
  documentationUrl: `${AGENTWEAVER_SCOPES_URL}#unknown-scopes`,
};

/** Returns the known explanation or a safe fallback for an unfamiliar permission. */
export function describeConsentScope(scope: string): ConsentScopeDescription {
  return SCOPE_DESCRIPTIONS[scope] ?? UNKNOWN_SCOPE_DESCRIPTION;
}
