import { describe, it, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import { createServer, type Server } from 'node:http';
import { StyloBotClient, StyloBotApiError } from '../client.ts';

let server: Server;
let port: number;

function startServer(handler: (req: any, res: any) => void): Promise<void> {
  return new Promise((resolve) => {
    server = createServer(handler);
    server.listen(0, () => { port = (server.address() as any).port; resolve(); });
  });
}
function stopServer(): Promise<void> {
  return new Promise((resolve) => { if (server) server.close(() => resolve()); else resolve(); });
}

describe('StyloBotClient', () => {
  afterEach(async () => { await stopServer(); });

  it('sends detect request with API key header', async () => {
    let receivedHeaders: Record<string, string | string[] | undefined> = {};
    let receivedBody = '';

    await startServer((req, res) => {
      receivedHeaders = req.headers;
      let body = '';
      req.on('data', (chunk: string) => { body += chunk; });
      req.on('end', () => {
        receivedBody = body;
        res.writeHead(200, { 'content-type': 'application/json' });
        res.end(JSON.stringify({
          verdict: { isBot: true, botProbability: 0.9, confidence: 0.8, botType: 'Scraper', botName: 'TestBot', riskBand: 'High', recommendedAction: 'Block', threatScore: 0, threatBand: 'None' },
          reasons: [], signals: {}, meta: { processingTimeMs: 1, detectorsRun: 5, policyName: null, aiRan: false }
        }));
      });
    });

    const client = new StyloBotClient({ endpoint: `http://localhost:${port}`, apiKey: 'SB-TEST-KEY' });
    const result = await client.detect({ method: 'GET', path: '/', headers: { 'user-agent': 'test' }, remoteIp: '127.0.0.1' });

    assert.equal(receivedHeaders['x-sb-api-key'], 'SB-TEST-KEY');
    assert.equal(result.verdict.isBot, true);
    assert.equal(result.verdict.botType, 'Scraper');
    const parsed = JSON.parse(receivedBody);
    assert.equal(parsed.method, 'GET');
    assert.equal(parsed.remoteIp, '127.0.0.1');
  });

  it('throws StyloBotApiError on 4xx', async () => {
    await startServer((_req, res) => { res.writeHead(403); res.end('{"title":"Forbidden"}'); });

    const client = new StyloBotClient({ endpoint: `http://localhost:${port}`, apiKey: 'SB-BAD', retries: 0 });
    await assert.rejects(
      () => client.detect({ method: 'GET', path: '/', headers: {}, remoteIp: '127.0.0.1' }),
      (err: any) => { assert.ok(err instanceof StyloBotApiError); assert.equal(err.status, 403); return true; }
    );
  });

  it('sends bearer token for management operations', async () => {
    let authHeader = '';
    await startServer((req, res) => {
      authHeader = req.headers['authorization'] ?? '';
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ data: { keyName: 'test' }, meta: { generatedAt: new Date().toISOString() } }));
    });

    const client = new StyloBotClient({ endpoint: `http://localhost:${port}`, bearerToken: 'ey.token.here' });
    await client.me();
    assert.equal(authHeader, 'Bearer ey.token.here');
  });
});
