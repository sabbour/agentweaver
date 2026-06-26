import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { Project } from '../api/types';

interface ProjectListState {
  projects: Project[];
  loading: boolean;
  authError: boolean;
  loadError: boolean;
  errorMessage: string | null;
  appendProject: (p: Project) => void;
  refetch: () => void;
}

const ProjectListContext = createContext<ProjectListState | null>(null);

export function ProjectListProvider({ children }: { children: ReactNode }) {
  const [projects, setProjects] = useState<Project[]>([]);
  const [loading, setLoading] = useState(true);
  const [authError, setAuthError] = useState(false);
  const [loadError, setLoadError] = useState(false);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [fetchKey, setFetchKey] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setAuthError(false);
    setLoadError(false);
    setErrorMessage(null);
    apiClient
      .listProjects()
      .then((list) => {
        if (!cancelled) {
          setProjects(list);
          setLoading(false);
        }
      })
      .catch((err) => {
        if (!cancelled) {
          setLoading(false);
          if (err instanceof ApiError && err.status === 401) {
            setAuthError(true);
          } else {
            setLoadError(true);
            setErrorMessage(
              err instanceof ApiError
                ? `API error ${err.status}: ${err.body}`
                : err instanceof Error
                  ? err.message
                  : String(err),
            );
          }
        }
      });
    return () => {
      cancelled = true;
    };
  }, [fetchKey]);

  const appendProject = useCallback((p: Project) => {
    setProjects((prev) => [...prev, p]);
  }, []);

  const refetch = useCallback(() => setFetchKey((k) => k + 1), []);

  return (
    <ProjectListContext.Provider
      value={{ projects, loading, authError, loadError, errorMessage, appendProject, refetch }}
    >
      {children}
    </ProjectListContext.Provider>
  );
}

export function useProjectList(): ProjectListState {
  const ctx = useContext(ProjectListContext);
  if (!ctx) throw new Error('useProjectList must be used inside ProjectListProvider');
  return ctx;
}
