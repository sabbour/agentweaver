import type { PagedRequestOptions, PagedResult } from './types';

const DEFAULT_PAGE_SIZE = 100;

function abortError(): DOMException {
  return new DOMException('The operation was aborted.', 'AbortError');
}

function throwIfAborted(signal?: AbortSignal) {
  if (signal?.aborted) throw abortError();
}

export async function collectPagedItems<T>(
  fetchPage: (options: PagedRequestOptions) => Promise<PagedResult<T>>,
  options?: { pageSize?: number; signal?: AbortSignal },
): Promise<T[]> {
  const items: T[] = [];
  const pageSize = options?.pageSize ?? DEFAULT_PAGE_SIZE;
  let page = 1;

  while (true) {
    throwIfAborted(options?.signal);
    const result = await fetchPage({ page, pageSize, signal: options?.signal });
    items.push(...result.items);

    if (page >= Math.max(1, result.total_pages)) break;
    page += 1;
  }

  return items;
}
