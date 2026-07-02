import type { ReactElement } from 'react';
import {
  Apps24Regular,
  DataPie24Regular,
  Pulse24Regular,
  Board24Regular,
  People24Regular,
  Flow24Regular,
  Code24Regular,
  Bot24Regular,
  Brain24Regular,
  Flowchart24Regular,
  Settings24Regular,
  Server24Regular,
  Stethoscope24Regular,
  Heart24Regular,
} from '@fluentui/react-icons';

// Web app shell information architecture (locked IA, 2026-06-22).
// Project-scoped sections: WORK / SQUAD / OPERATIONS / SYSTEM. Above them sit
// global (non-project) destinations: Overview ("Now") and the Projects gallery.
export interface NavItemDef {
  // Stable key used as the NavItem value and for active-state matching.
  key: string;
  label: string;
  icon: ReactElement;
  // Path suffix appended to /projects/:projectId. Empty string targets the
  // project Dashboard (home) route.
  segment: string;
  // Additional first path segments (after the project id) that should mark this
  // item active — used so deep run / execution routes keep a sensible active item.
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
}

export interface NavSectionDef {
  heading: string;
  items: NavItemDef[];
  // SYSTEM is anchored to the bottom of the sidebar.
  anchorBottom?: boolean;
}

// Global (project-independent) destinations rendered above the project sections.
// Overview is the live "Now" page; Projects is the gallery / switcher landing.
export const GLOBAL_NAV_ITEMS: GlobalNavItemDef[] = [
  { key: 'overview', label: 'Overview', icon: <Pulse24Regular />, path: '/overview', matchPrefixes: ['/overview', '/'] },
  { key: 'projects', label: 'Projects', icon: <Apps24Regular />, path: '/projects', matchPrefixes: ['/projects'] },
];

// WORK / SQUAD / OPERATIONS / SYSTEM.
export const NAV_SECTIONS: NavSectionDef[] = [
  {
    heading: 'WORK',
    items: [
      // Dashboard is the project home (index) route at /projects/:projectId.
      { key: 'dashboard', label: 'Dashboard', icon: <DataPie24Regular />, segment: '' },
      // Board moved to its own /board segment; deep run/execution routes keep it active.
      { key: 'board', label: 'Board', icon: <Board24Regular />, segment: 'board', matchSegments: ['runs'] },
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
      { key: 'memories', label: 'Memories', icon: <Brain24Regular />, segment: 'memories' },
    ],
  },
  {
    heading: 'OPERATIONS',
    items: [
      // Workflows (formerly the "Flow" item) — route unchanged at /workflows.
      { key: 'workflows', label: 'Workflows', icon: <Flowchart24Regular />, segment: 'workflows' },
      { key: 'settings', label: 'Settings', icon: <Settings24Regular />, segment: 'settings' },
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
export const NAV_ITEMS: NavItemDef[] = NAV_SECTIONS.flatMap((s) => s.items);

// Build the absolute route for a project-scoped nav item.
export function navItemPath(projectId: string, item: NavItemDef): string {
  const base = `/projects/${projectId}`;
  return item.segment ? `${base}/${item.segment}` : base;
}

// Resolve which nav item is active for the given pathname. Returns the matching
// item key. When no project is in scope, global keys ('overview' / 'projects')
// are returned; project-scoped routes fall back to the Dashboard home.
export function resolveActiveKey(pathname: string, projectId: string | undefined): string {
  if (!projectId) {
    // /projects (gallery) — but NOT /projects/:id (handled below).
    if (pathname === '/projects' || pathname.startsWith('/projects/')) return 'projects';
    // '/' and /overview both surface the global Overview.
    return 'overview';
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
