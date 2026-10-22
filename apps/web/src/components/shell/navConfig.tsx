import {
  Apps24Regular,
  Board24Regular,
  BookToolbox24Regular,
  Bot24Regular,
  Brain24Regular,
  Chat24Regular,
  Code24Regular,
  DataPie24Regular,
  Flow24Regular,
  Flowchart24Regular,
  Heart24Regular,
  Home24Regular,
  People24Regular,
  Pulse24Regular,
  Server24Regular,
  Stethoscope24Regular,
} from '@fluentui/react-icons';
import type { ReactElement } from 'react';
// Web app shell information architecture (locked IA, 2026-06-22).
// Project-scoped sections: WORK / SQUAD / OPERATIONS / SYSTEM. Above them sit
// global destinations: Overview ("Now"), the v1 Chief of Staff shortcut, the
// Projects gallery, and the user-wide Sessions hub.
export interface NavItemDef {
  // Stable key used as the NavItem value and for active-state matching.
  key: string;
  label: string;
  icon: ReactElement;
  // Path suffix appended to /projects/:projectId. Empty string targets the
  // project Dashboard (home) route.
  segment: string;
  // Additional first path segments (after the project id) that should mark this
  // item active for supported sub-resource routes.
  matchSegments?: string[];
}

export interface GlobalNavItemDef {
  key: string;
  label: string;
  icon: ReactElement;
  // Absolute route (not project-scoped).
  path: string;
  // Path prefixes that should also mark this item active.
  matchPrefixes?: string[];
  // These entries appear only when a version-specific shell explicitly requests them.
  v1Only?: boolean;
}

export interface NavSectionDef {
  heading: string;
  items: NavItemDef[];
  // SYSTEM is anchored to the bottom of the sidebar.
  anchorBottom?: boolean;
}

// Global (project-independent) destinations rendered above the project sections.
// Chief of Staff is a v1-only shortcut to a section of Overview; Overview is the
// live "Now" page, Projects is the gallery, and Sessions is the cross-project hub.
// A chat opened at /assistant also highlights Sessions.
/** Route definitions for navigation entries that are not scoped to a project. */
export const GLOBAL_NAV_ITEMS: GlobalNavItemDef[] = [
  { key: 'overview', label: 'Overview', icon: <Home24Regular />, path: '/overview', matchPrefixes: ['/overview', '/'] },
  { key: 'chief-of-staff', label: 'Chief of Staff', icon: <Bot24Regular />, path: '/overview#chief-of-staff', v1Only: true },
  { key: 'projects', label: 'Projects', icon: <Apps24Regular />, path: '/projects', matchPrefixes: ['/projects'] },
  { key: 'sessions', label: 'Sessions', icon: <Chat24Regular />, path: '/sessions', matchPrefixes: ['/sessions', '/assistant'] },
];

// Active keys for rail controls that are not nav links. The New session
// button and the Settings menu button show as selected for these keys.
/** Active key that highlights the New session button. */
export const NEW_SESSION_NAV_KEY = 'new-session';
/** Active key that highlights the Settings menu button. */
export const SETTINGS_NAV_KEY = 'settings';

// WORK / SQUAD / OPERATIONS / SYSTEM.
/** Section and item definitions used to construct the main navigation. */
export const NAV_SECTIONS: NavSectionDef[] = [
  {
    heading: 'WORK',
    items: [
      // Dashboard is the project home (index) route at /projects/:projectId.
      { key: 'dashboard', label: 'Dashboard', icon: <DataPie24Regular />, segment: '' },
      // Board moved to its own /board segment.
      { key: 'board', label: 'Board', icon: <Board24Regular />, segment: 'board' },
      // Flow — live "what each agent is working on" view.
      { key: 'flow', label: 'Flow', icon: <People24Regular />, segment: 'flow' },
      // Orchestrations — list of coordinator orchestration runs; the detail route
      // (/orchestrations/:runId) keeps this item active.
      { key: 'orchestrations', label: 'Orchestrations', icon: <Flow24Regular />, segment: 'orchestrations' },
      // Workspace — read-only file browser for the project repo + run worktrees.
      { key: 'workspace', label: 'Workspace', icon: <Code24Regular />, segment: 'workspace' },
    ],
  },
  {
    heading: 'SQUAD',
    items: [
      // Agents reuses the existing Team page at /team (label adaptation only).
      { key: 'agents', label: 'Agents', icon: <Bot24Regular />, segment: 'team' },
      { key: 'skills', label: 'Skills', icon: <BookToolbox24Regular />, segment: 'skills' },
      { key: 'memories', label: 'Memories', icon: <Brain24Regular />, segment: 'memories' },
    ],
  },
  {
    heading: 'OPERATIONS',
    items: [
      // Workflows (formerly the "Flow" item) — route unchanged at /workflows.
      { key: 'workflows', label: 'Workflows', icon: <Flowchart24Regular />, segment: 'workflows' },
    ],
  },
  {
    heading: 'OBSERVABILITY',
    items: [
      { key: 'observability', label: 'Observability', icon: <Pulse24Regular />, segment: 'observability' },
    ],
  },
  {
    heading: 'SYSTEM',
    anchorBottom: true,
    items: [
      { key: 'diagnostics', label: 'Diagnostics', icon: <Stethoscope24Regular />, segment: 'diagnostics' },
      { key: 'heartbeat', label: 'Heartbeat', icon: <Heart24Regular />, segment: 'heartbeat' },
      { key: 'cluster', label: 'Cluster', icon: <Server24Regular />, segment: 'cluster' },
    ],
  },
];

// All project-scoped items flattened, used for active-state resolution.
/** Flattened navigation definitions for lookups shared by navigation surfaces. */
export const NAV_ITEMS: NavItemDef[] = NAV_SECTIONS.flatMap((s) => s.items);

// Build the absolute route for a project-scoped nav item.
/** Builds the route path for an item, substituting the selected project when required. */
export function navItemPath(projectId: string, item: NavItemDef): string {
  const base = `/projects/${projectId}`;
  return item.segment ? `${base}/${item.segment}` : base;
}

// Resolve which nav item is active for the given pathname. Returns the matching
// item key. When no project is in scope, global keys are resolved first;
// project-scoped routes fall back to the Dashboard home.
/** Resolves which configured navigation item matches the current pathname. */
export function resolveActiveKey(pathname: string, projectId: string | undefined): string {
  if (!projectId) {
    const directGlobal = GLOBAL_NAV_ITEMS.find((item) => {
      if (pathname === item.path) return true;
      return item.matchPrefixes?.some((prefix) => pathname === prefix || (prefix !== '/' && pathname.startsWith(`${prefix}/`)));
    });
    return directGlobal?.key ?? 'overview';
  }
  const prefix = `/projects/${projectId}`;
  if (!pathname.startsWith(prefix)) return 'overview';
  const rest = pathname.slice(prefix.length).replace(/^\//, '');
  const firstSeg = rest.split('/')[0] ?? '';
  if (!firstSeg) return 'dashboard';

  // Prefer an exact segment match (so e.g. 'team/cast' resolves to Agents).
  const direct = NAV_ITEMS.find((i) => i.segment && firstSeg === i.segment);
  if (direct) return direct.key;

  const viaMatch = NAV_ITEMS.find((i) => i.matchSegments?.includes(firstSeg));
  if (viaMatch) return viaMatch.key;

  // Unknown project-scoped route → fall back to the Dashboard home.
  return 'dashboard';
}
