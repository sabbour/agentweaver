import { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  Combobox,
  Option,
  OptionGroup,
  Tooltip,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { apiClient } from '../../api/apiClient';
import { ApiError } from '../../api/client';
import type { Project } from '../../api/types';
import { NAV_ITEMS, type NavItemDef } from './navConfig';

// Spec 011, FR-011/FR-012/FR-014 — switch-only project switcher. Lists existing
// projects from the real projects API and navigates to the selected one. It does
// NOT offer inline create/add (clarification C5); creation stays on the gallery.

const RECENT_KEY = 'agentweaver:recent-project-ids';
const MAX_RECENT = 5;

function getRecentIds(): string[] {
  try {
    return JSON.parse(localStorage.getItem(RECENT_KEY) ?? '[]') as string[];
  } catch {
    return [];
  }
}

function pushRecentId(id: string): string[] {
  const next = [id, ...getRecentIds().filter((x) => x !== id)].slice(0, MAX_RECENT);
  localStorage.setItem(RECENT_KEY, JSON.stringify(next));
  return next;
}

const useStyles = makeStyles({
  combobox: {
    minWidth: '240px',
    maxWidth: '420px',
    fontWeight: tokens.fontWeightSemibold,
    flexShrink: 1,
    '& input': {
      overflow: 'hidden',
      textOverflow: 'ellipsis',
      whiteSpace: 'nowrap',
    },
  },
});

// The project-scoped category to preserve when switching projects (e.g. stay on
// Settings). Sub-resource ids are intentionally dropped — switching lands on the
// category root of the target project. Deep run/execution routes (e.g.
// /runs/:runId/workflow) are mapped to their owning nav category via the nav
// item's matchSegments, so they preserve Board instead of falling to the root.
function currentCategorySegment(pathname: string, projectId: string | undefined): NavItemDef | null {
  if (!projectId) return null;
  const prefix = `/projects/${projectId}`;
  if (!pathname.startsWith(prefix)) return null;
  const rest = pathname.slice(prefix.length).replace(/^\//, '');
  const firstSeg = rest.split('/')[0] ?? '';
  if (!firstSeg) return null;
  // Prefer an exact segment match (e.g. 'team/cast' resolves to Agents at /team).
  const direct = NAV_ITEMS.find((i) => i.segment && i.segment === firstSeg);
  if (direct) return direct;
  // Otherwise honor matchSegments so deep routes keep their category (e.g.
  // 'runs/:runId/workflow' maps to Board).
  const viaMatch = NAV_ITEMS.find((i) => i.matchSegments?.includes(firstSeg));
  if (viaMatch) return viaMatch;
  return null;
}

// Compute the path to navigate to when switching from the current page to a
// target project. Preserves the page category where possible (mapping deep
// sub-resource routes to their category root); otherwise lands on the target
// project home.
export function projectSwitchTarget(
  pathname: string,
  currentProjectId: string | undefined,
  targetId: string,
): string {
  const category = currentCategorySegment(pathname, currentProjectId);
  return category && category.segment
    ? `/projects/${targetId}/${category.segment}`
    : `/projects/${targetId}`;
}

export interface ProjectSwitcherProps {
  projectId: string | undefined;
  pathname: string;
  // True when projectId is a persisted fallback (the route carries no :projectId).
  isFallbackProject?: boolean;
  // Called when a persisted fallback project no longer exists in the project list.
  onFallbackProjectMissing?: () => void;
}

export function ProjectSwitcher({
  projectId,
  pathname,
  isFallbackProject,
  onFallbackProjectMissing,
}: ProjectSwitcherProps) {
  const styles = useStyles();
  const navigate = useNavigate();

  const [projects, setProjects] = useState<Project[]>([]);
  const [loaded, setLoaded] = useState(false);
  const [loadError, setLoadError] = useState(false);
  const [authError, setAuthError] = useState(false);
  const [comboValue, setComboValue] = useState('');
  const [recentIds, setRecentIds] = useState<string[]>(() => getRecentIds());

  useEffect(() => {
    let cancelled = false;
    apiClient
      .listProjects()
      .then((list) => {
        if (!cancelled) {
          setProjects(list);
          setLoaded(true);
          setLoadError(false);
          setAuthError(false);
        }
      })
      .catch((err) => {
        if (!cancelled) {
          if (err instanceof ApiError && err.status === 401) {
            setAuthError(true);
          } else {
            setLoadError(true);
          }
        }
      });
    return () => {
      cancelled = true;
    };
  }, []);

  // Drop the persisted fallback if its project was deleted/renamed away, so the
  // switcher and nav don't point at a project that no longer exists.
  useEffect(() => {
    if (
      isFallbackProject &&
      projectId &&
      loaded &&
      !projects.some((p) => p.project_id === projectId)
    ) {
      onFallbackProjectMissing?.();
    }
  }, [isFallbackProject, projectId, loaded, projects, onFallbackProjectMissing]);

  const currentProject = useMemo(
    () => projects.find((p) => p.project_id === projectId) ?? null,
    [projects, projectId],
  );
  const currentName = currentProject?.name ?? '';

  // Keep the combobox display in sync with the active project.
  useEffect(() => {
    setComboValue(currentName);
  }, [currentName]);

  const allSorted = useMemo(
    () => [...projects].sort((a, b) => a.name.localeCompare(b.name)),
    [projects],
  );

  const isFiltering = useMemo(() => {
    const q = comboValue.trim().toLowerCase();
    return Boolean(q) && q !== currentName.toLowerCase();
  }, [comboValue, currentName]);

  const filtered = useMemo(() => {
    if (!isFiltering) return allSorted;
    const q = comboValue.trim().toLowerCase();
    return allSorted.filter((p) => p.name.toLowerCase().includes(q));
  }, [isFiltering, comboValue, allSorted]);

  const recentProjects = useMemo(() => {
    const q = comboValue.trim().toLowerCase();
    return recentIds
      .filter((rid) => rid !== projectId)
      .map((rid) => projects.find((p) => p.project_id === rid))
      .filter((p): p is Project => Boolean(p))
      .filter((p) => !isFiltering || p.name.toLowerCase().includes(q));
  }, [recentIds, projects, projectId, isFiltering, comboValue]);

  function handleSwitch(targetId: string) {
    const target = projects.find((p) => p.project_id === targetId);
    if (!target) return;
    setComboValue(target.name);
    setRecentIds(pushRecentId(targetId));
    // Preserve the current page category under the target project where possible;
    // otherwise land on its home.
    navigate(projectSwitchTarget(pathname, projectId, targetId));
  }

  const placeholder = authError
    ? 'Sign in to view projects'
    : loadError
      ? 'Projects unavailable'
      : projectId
        ? 'Select project…'
        : 'No project selected';

  return (
    <Tooltip content={currentName || placeholder} relationship="label" positioning="below-start">
      <Combobox
        className={styles.combobox}
        aria-label="Project switcher"
        value={comboValue}
        selectedOptions={projectId ? [projectId] : []}
        placeholder={placeholder}
        disabled={loadError}
        onInput={(e) => setComboValue(e.currentTarget.value)}
        onOptionSelect={(_, data) => {
          if (data.optionValue === '__signin__') {
            window.location.href = '/auth/github/authorize';
            return;
          }
          if (data.optionValue) handleSwitch(data.optionValue);
        }}
        onBlur={() => setComboValue(currentName)}
      >
        {recentProjects.length > 0 && !authError && (
          <OptionGroup label="Recent">
            {recentProjects.map((p) => (
              <Option key={p.project_id} value={p.project_id} text={p.name}>
                {p.name}
              </Option>
            ))}
          </OptionGroup>
        )}
        <OptionGroup label={recentProjects.length > 0 && !authError ? 'All projects' : undefined}>
          {!authError && filtered.map((p) => (
            <Option key={p.project_id} value={p.project_id} text={p.name}>
              {p.name}
            </Option>
          ))}
          {!authError && filtered.length === 0 && (
            <Option value="" disabled text="">
              {projects.length === 0 ? 'No projects yet' : `No projects match "${comboValue}"`}
            </Option>
          )}
          {authError && (
            <Option value="__signin__" text="Sign in with GitHub">
              Sign in with GitHub
            </Option>
          )}
        </OptionGroup>
      </Combobox>
    </Tooltip>
  );
}
