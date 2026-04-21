export class StyloBotClient {
    endpoint;
    apiKey;
    bearerToken;
    timeout;
    retries;
    constructor(options) {
        this.endpoint = options.endpoint.replace(/\/$/, '');
        this.apiKey = options.apiKey;
        this.bearerToken = options.bearerToken;
        this.timeout = options.timeout ?? 5000;
        this.retries = options.retries ?? 1;
    }
    async detect(request) { return this.post('/api/v1/detect', request); }
    async detectBatch(requests) { return this.post('/api/v1/detect/batch', requests); }
    async detections(params) { return this.get('/api/v1/detections', params); }
    async signatures(params) { return this.get('/api/v1/signatures', params); }
    async summary(params) { return this.get('/api/v1/summary', params); }
    async timeseries(params) { return this.get('/api/v1/timeseries', params); }
    async countries(params) { return this.get('/api/v1/countries', params); }
    async endpoints(params) { return this.get('/api/v1/endpoints', params); }
    async topBots(params) { return this.get('/api/v1/topbots', params); }
    async threats(params) { return this.get('/api/v1/threats', params); }
    async me() { return this.get('/api/v1/me'); }
    headers() {
        const h = { 'content-type': 'application/json' };
        if (this.apiKey)
            h['x-sb-api-key'] = this.apiKey;
        if (this.bearerToken)
            h['authorization'] = `Bearer ${this.bearerToken}`;
        return h;
    }
    async request(method, path, body) {
        const url = `${this.endpoint}${path}`;
        let lastError;
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
                return (await res.json());
            }
            catch (err) {
                lastError = err instanceof Error ? err : new Error(String(err));
                if (err instanceof StyloBotApiError && err.status < 500)
                    throw err;
            }
        }
        throw lastError ?? new Error('Request failed');
    }
    get(path, params) {
        const qs = params ? toQueryString(params) : '';
        return this.request('GET', qs ? `${path}?${qs}` : path);
    }
    post(path, body) {
        return this.request('POST', path, body);
    }
}
export class StyloBotApiError extends Error {
    status;
    body;
    url;
    constructor(status, body, url) {
        super(`StyloBot API error ${status}: ${body.slice(0, 200)}`);
        this.name = 'StyloBotApiError';
        this.status = status;
        this.body = body;
        this.url = url;
    }
}
function toQueryString(params) {
    const entries = Object.entries(params).filter(([, v]) => v !== undefined && v !== null);
    if (entries.length === 0)
        return '';
    return entries.map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(String(v))}`).join('&');
}
//# sourceMappingURL=client.js.map