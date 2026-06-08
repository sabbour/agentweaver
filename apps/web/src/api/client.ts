import type { RunDetail, SubmitRunRequest, SubmitRunResponse } from './types';

export class ApiError extends Error {
  readonly status: number;
  readonly body: string;

  constructor(status: number, body: string) {
    super(`API error ${status}: ${body}`);
    this.name = 'ApiError';
    this.status = status;
    this.body = body;
  }
}

export class ScaffolderApiClient {
  private readonly baseUrl: string;
  private readonly apiKey: string;

  constructor(baseUrl: string, apiKey: string) {
    this.baseUrl = baseUrl.replace(/\/+$/, '');
    this.apiKey = apiKey;
  }

  submitRun(req: SubmitRunRequest): Promise<SubmitRunResponse> {
    return this.request<SubmitRunResponse>('POST', '/api/runs', req);
  }

  getRun(runId: string): Promise<RunDetail> {
    return this.request<RunDetail>('GET', `/api/runs/${encodeURIComponent(runId)}`);
  }

  private async request<T>(method: string, path: string, body?: unknown): Promise<T> {
    const headers: Record<string, string> = {
      Authorization: `Bearer ${this.apiKey}`,
    };
    if (body !== undefined) headers['Content-Type'] = 'application/json';

    const response = await fetch(`${this.baseUrl}${path}`, {
      method,
      headers,
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });

    const text = await response.text();
    if (!response.ok) throw new ApiError(response.status, text);
    return (text ? JSON.parse(text) : null) as T;
  }
}
