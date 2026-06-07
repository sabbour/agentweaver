import type {
  RunDetail,
  RunEvent,
  ReviewRequest,
  ReviewResponse,
  SubmitRunRequest,
  SubmitRunResponse,
} from './types';

/** Raised when an API call returns a non-success status. */
export class ApiError extends Error {
  readonly status: number;
  readonly body: string;

  constructor(status: number, body: string) {
    super(`API request failed with status ${status}: ${body}`);
    this.name = 'ApiError';
    this.status = status;
    this.body = body;
  }
}

/** Typed thin wrapper over the Scaffolder backend API. */
export class ScaffolderApiClient {
  private readonly baseUrl: string;
  private readonly apiKey: string;

  constructor(baseUrl: string, apiKey: string) {
    this.baseUrl = baseUrl.replace(/\/+$/, '');
    this.apiKey = apiKey;
  }

  async submitRun(req: SubmitRunRequest): Promise<SubmitRunResponse> {
    return this.request<SubmitRunResponse>('POST', '/api/runs', req);
  }

  async getRun(runId: string): Promise<RunDetail> {
    return this.request<RunDetail>('GET', `/api/runs/${encodeURIComponent(runId)}`);
  }

  async getEvents(runId: string, afterSequence = -1): Promise<RunEvent[]> {
    const path = `/api/runs/${encodeURIComponent(runId)}/events?afterSequence=${afterSequence}`;
    return this.request<RunEvent[]>('GET', path);
  }

  async submitReview(runId: string, req: ReviewRequest): Promise<ReviewResponse> {
    return this.request<ReviewResponse>(
      'POST',
      `/api/runs/${encodeURIComponent(runId)}/review`,
      req,
    );
  }

  private async request<T>(method: string, path: string, body?: unknown): Promise<T> {
    const headers: Record<string, string> = {
      Authorization: `Bearer ${this.apiKey}`,
    };
    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
    }

    const response = await fetch(`${this.baseUrl}${path}`, {
      method,
      headers,
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });

    const text = await response.text();
    if (!response.ok) {
      throw new ApiError(response.status, text);
    }

    return (text ? JSON.parse(text) : null) as T;
  }
}
