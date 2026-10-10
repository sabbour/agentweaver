import { usePagination } from '../hooks/usePagination';
import { act, renderHook } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

describe('usePagination', () => {
  it('slices items for the current page using the default page size', () => {
    const items = Array.from({ length: 30 }, (_, i) => i + 1);
    const { result } = renderHook(() => usePagination(items));

    expect(result.current.page).toBe(1);
    expect(result.current.pageSize).toBe(25);
    expect(result.current.totalItems).toBe(30);
    expect(result.current.totalPages).toBe(2);
    expect(result.current.pageItems).toEqual(items.slice(0, 25));
  });

  it('respects an explicit page size and computes total pages', () => {
    const items = Array.from({ length: 23 }, (_, i) => i + 1);
    const { result } = renderHook(() => usePagination(items, { pageSize: 10 }));

    expect(result.current.totalPages).toBe(3);
    expect(result.current.pageItems).toEqual([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
  });

  it('navigates to a later page via setPage', () => {
    const items = Array.from({ length: 23 }, (_, i) => i + 1);
    const { result } = renderHook(() => usePagination(items, { pageSize: 10 }));

    act(() => result.current.setPage(3));

    expect(result.current.page).toBe(3);
    expect(result.current.pageItems).toEqual([21, 22, 23]);
  });

  it('clamps setPage requests to the valid [1, totalPages] range', () => {
    const items = Array.from({ length: 23 }, (_, i) => i + 1);
    const { result } = renderHook(() => usePagination(items, { pageSize: 10 }));

    act(() => result.current.setPage(99));
    expect(result.current.page).toBe(3);

    act(() => result.current.setPage(-5));
    expect(result.current.page).toBe(1);
  });

  it('clamps the current page down when the item count shrinks', () => {
    const { result, rerender } = renderHook(
      ({ items }) => usePagination(items, { pageSize: 10 }),
      { initialProps: { items: Array.from({ length: 23 }, (_, i) => i + 1) } },
    );

    act(() => result.current.setPage(3));
    expect(result.current.page).toBe(3);

    rerender({ items: Array.from({ length: 5 }, (_, i) => i + 1) });
    expect(result.current.page).toBe(1);
    expect(result.current.totalPages).toBe(1);
    expect(result.current.pageItems).toEqual([1, 2, 3, 4, 5]);
  });

  it('resets to page 1 when the page size changes', () => {
    const items = Array.from({ length: 23 }, (_, i) => i + 1);
    const { result } = renderHook(() => usePagination(items, { pageSize: 10 }));

    act(() => result.current.setPage(3));
    expect(result.current.page).toBe(3);

    act(() => result.current.setPageSize(5));
    expect(result.current.page).toBe(1);
    expect(result.current.pageSize).toBe(5);
    expect(result.current.pageItems).toEqual([1, 2, 3, 4, 5]);
  });

  it('always returns at least one page for an empty list', () => {
    const { result } = renderHook(() => usePagination<number>([], { pageSize: 10 }));

    expect(result.current.totalPages).toBe(1);
    expect(result.current.page).toBe(1);
    expect(result.current.pageItems).toEqual([]);
  });
});
