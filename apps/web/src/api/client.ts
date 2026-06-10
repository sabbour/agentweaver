import type { RetriableReviewErrorBody, RunDetail, ReviewRequest, ReviewResponse, SandboxPolicy, SubmitRunRequest, SubmitRunResponse } from './types';

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

export class RetriableReviewError extends Error {
  readonly serverMessage: string;
  readonly runStatus: string;

  constructor(serverMessage: string, runStatus: string) {
    super(serverMessage);
    this.name = 'RetriableReviewError';
    this.serverMessage = serverMessage;
    this.runStatus = runStatus;
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

  getSandboxPolicy(repositoryPath: string): Promise<SandboxPolicy> {
    const encoded = encodeURIComponent(repositoryPath);
    return this.request<SandboxPolicy>('GET', `/api/sandbox-policy?repository_path=${encoded}`);
  }

  updateSandboxPolicy(policy: Pick<SandboxPolicy, 'repository_path' | 'shell_enabled'>): Promise<SandboxPolicy> {
    return this.request<SandboxPolicy>('PUT', '/api/sandbox-policy', policy);
  }

  async submitReview(runId: string, approved: boolean): Promise<ReviewResponse> {
    const body: ReviewRequest = { approved };
    const headers: Record<string, string> = {
      Authorization: `Bearer ${this.apiKey}`,
      'Content-Type': 'application/json',
    };
    const response = await fetch(
      `${this.baseUrl}/api/runs/${encodeURIComponent(runId)}/review`,
      { method: 'POST', headers, body: JSON.stringify(body) },
    );
    const text = await response.text();
    if (response.status === 409) {
      let parsed: RetriableReviewErrorBody | null = null;
      try {
        parsed = JSON.parse(text) as RetriableReviewErrorBody;
      } catch {
        // fall through to ApiError below
      }
      if (parsed?.error) throw new RetriableReviewError(parsed.error, parsed.status ?? 'awaiting_review');
      throw new ApiError(409, text);
    }
    if (!response.ok) throw new ApiError(response.status, text);
    return (text ? JSON.parse(text) : null) as ReviewResponse;
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
