import type { PagedRequestOptions, PagedResult } from './types';

const DEFAULT_PAGE_SIZE = 100;

/** Creates the standard abort exception used when pagination is cancelled. */
function abortError(): DOMException {
  return new DOMException('The operation was aborted.', 'AbortError');
}

function throwIfAborted(signal?: AbortSignal) {
  if (signal?.aborted) throw abortError();
}

/** Requests every page in order and returns a single flattened result list. */
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
