import { useMemo, useState } from 'react';

export interface UsePaginationOptions {
  /** Initial page size. Defaults to 25 to match the backend pagination contract's default. */
  pageSize?: number;
  /** Initial 1-based page. Defaults to 1. */
  initialPage?: number;
}

export interface UsePaginationResult<T> {
  /** Current 1-based page, clamped to [1, totalPages]. */
  page: number;
  pageSize: number;
  /** Total number of items across all pages. */
  totalItems: number;
  totalPages: number;
  /** The slice of `items` belonging to the current page. */
  pageItems: T[];
  setPage: (page: number) => void;
  setPageSize: (pageSize: number) => void;
}

/**
 * Client-side pagination over an already-fetched array. Use this to slice a full result set
 * for the shared `Pager` control (`../copilot-fluent-system`) — the reusable pagination UI
 * already checked-in there — when a list endpoint isn't (yet, or by design) paginated at the
 * fetch layer.
 *
 * When a list endpoint's client method already returns a server-paged envelope
 * (`PagedResult<T>` from `../api/types`, per `.squad/decisions/inbox/niobe-pagination-contract.md`),
 * pass `page`/`pageSize` straight through to the fetch call instead and drive `Pager` from the
 * envelope's `total_count`/`total_pages` — this hook is for pages that still fetch the full list.
 */
export function usePagination<T>(items: readonly T[], options?: UsePaginationOptions): UsePaginationResult<T> {
  const [page, setPageState] = useState(options?.initialPage ?? 1);
  const [pageSize, setPageSizeState] = useState(options?.pageSize ?? 25);

  const totalItems = items.length;
  const totalPages = Math.max(1, Math.ceil(totalItems / Math.max(1, pageSize)));
  const safePage = Math.min(Math.max(1, page), totalPages);

  const pageItems = useMemo(() => {
    const start = (safePage - 1) * pageSize;
    return items.slice(start, start + pageSize);
  }, [items, safePage, pageSize]);

  const setPage = (next: number) => setPageState(Math.min(Math.max(1, next), totalPages));
  const setPageSize = (next: number) => {
    setPageSizeState(next);
    setPageState(1);
  };

  return { page: safePage, pageSize, totalItems, totalPages, pageItems, setPage, setPageSize };
}
