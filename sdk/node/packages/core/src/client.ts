import type {
  StyloBotClientOptions, DetectRequest, DetectResponse, PaginatedResponse, SingleResponse,
  Summary, TimeseriesPoint, CountryStat, EndpointStat, BotStat, Threat, ApiKeyInfo,
  DetectionsQuery, SignaturesQuery, SummaryQuery, TimeseriesQuery, PaginationQuery, ThreatsQuery,
} from './types.js';

export class StyloBotClient {
  private readonly endpoint: string;
  private readonly apiKey?: string;
  private readonly bearerToken?: string;
  private readonly timeout: number;
  private readonly retries: number;

  constructor(options: StyloBotClientOptions) {
    this.endpoint = options.endpoint.replace(/\/$/, '');
    this.apiKey = options.apiKey;
    this.bearerToken = options.bearerToken;
    this.timeout = options.timeout ?? 5000;
    this.retries = options.retries ?? 1;
  }

  async detect(request: DetectRequest): Promise<DetectResponse> { return this.post('/api/v1/detect', request); }
  async detectBatch(requests: DetectRequest[]): Promise<DetectResponse[]> { return this.post('/api/v1/detect/batch', requests); }

  async detections(params?: DetectionsQuery): Promise<PaginatedResponse<unknown>> { return this.get('/api/v1/detections', params); }
  async signatures(params?: SignaturesQuery): Promise<PaginatedResponse<unknown>> { return this.get('/api/v1/signatures', params); }
  async summary(params?: SummaryQuery): Promise<SingleResponse<Summary>> { return this.get('/api/v1/summary', params); }
  async timeseries(params?: TimeseriesQuery): Promise<PaginatedResponse<TimeseriesPoint>> { return this.get('/api/v1/timeseries', params); }
  async countries(params?: PaginationQuery): Promise<PaginatedResponse<CountryStat>> { return this.get('/api/v1/countries', params); }
  async endpoints(params?: PaginationQuery): Promise<PaginatedResponse<EndpointStat>> { return this.get('/api/v1/endpoints', params); }
  async topBots(params?: PaginationQuery): Promise<PaginatedResponse<BotStat>> { return this.get('/api/v1/topbots', params); }
  async threats(params?: ThreatsQuery): Promise<PaginatedResponse<Threat>> { return this.get('/api/v1/threats', params); }
  async me(): Promise<SingleResponse<ApiKeyInfo>> { return this.get('/api/v1/me'); }

  private headers(): Record<string, string> {
    const h: Record<string, string> = { 'content-type': 'application/json' };
    if (this.apiKey) h['x-sb-api-key'] = this.apiKey;
    if (this.bearerToken) h['authorization'] = `Bearer ${this.bearerToken}`;
    return h;
  }

  private async request<T>(method: string, path: string, body?: unknown): Promise<T> {
    const url = `${this.endpoint}${path}`;
    let lastError: Error | undefined;

    for (let attempt = 0; attempt <= this.retries; attempt++) {
      try {
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), this.timeout);
        const res = await fetch(url, {
          method, headers: this.headers(),
          body: body ? JSON.stringify(body) : undefined,
          signal: controller.signal,
        });
        clearTimeout(timer);

        if (!res.ok) {
          const text = await res.text().catch(() => '');
          throw new StyloBotApiError(res.status, text, url);
        }
        return (await res.json()) as T;
      } catch (err) {
        lastError = err instanceof Error ? err : new Error(String(err));
        if (err instanceof StyloBotApiError && err.status < 500) throw err;
      }
    }
    throw lastError ?? new Error('Request failed');
  }

  private get<T>(path: string, params?: Record<string, unknown>): Promise<T> {
    const qs = params ? toQueryString(params) : '';
    return this.request<T>('GET', qs ? `${path}?${qs}` : path);
  }

  private post<T>(path: string, body: unknown): Promise<T> {
    return this.request<T>('POST', path, body);
  }
}

export class StyloBotApiError extends Error {
  readonly status: number;
  readonly body: string;
  readonly url: string;

  constructor(status: number, body: string, url: string) {
    super(`StyloBot API error ${status}: ${body.slice(0, 200)}`);
    this.name = 'StyloBotApiError';
    this.status = status;
    this.body = body;
    this.url = url;
  }
}

function toQueryString(params: Record<string, unknown>): string {
  const entries = Object.entries(params).filter(([, v]) => v !== undefined && v !== null);
  if (entries.length === 0) return '';
  return entries.map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(String(v))}`).join('&');
}
