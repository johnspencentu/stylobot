import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { IncomingMessage } from 'node:http';
import { Socket } from 'node:net';
import { extractDetectRequest } from '../extract.ts';

function mockReq(overrides: Record<string, any> = {}): IncomingMessage & Record<string, any> {
  const socket = new Socket();
  Object.defineProperty(socket, 'remoteAddress', { value: '192.168.1.1', writable: true, configurable: true });
  const req = new IncomingMessage(socket);
  req.method = overrides.method ?? 'GET';
  req.url = overrides.url ?? '/test';
  req.headers = overrides.headers ?? { 'user-agent': 'TestAgent/1.0' };
  if (overrides.ip) (req as any).ip = overrides.ip;
  if (overrides.originalUrl) (req as any).originalUrl = overrides.originalUrl;
  if (overrides.protocol) (req as any).protocol = overrides.protocol;
  return req as any;
}

describe('extractDetectRequest', () => {
  it('extracts method, path, and headers', () => {
    const req = mockReq({ method: 'POST', url: '/api/data', headers: { 'user-agent': 'Bot/1.0', 'accept': 'application/json' } });
    const result = extractDetectRequest(req);
    assert.equal(result.method, 'POST');
    assert.equal(result.path, '/api/data');
    assert.equal(result.headers['user-agent'], 'Bot/1.0');
  });

  it('prefers Express ip property', () => {
    const result = extractDetectRequest(mockReq({ ip: '10.0.0.1' }));
    assert.equal(result.remoteIp, '10.0.0.1');
  });

  it('falls back to x-forwarded-for', () => {
    const result = extractDetectRequest(mockReq({ headers: { 'x-forwarded-for': '203.0.113.42, 10.0.0.1' } }));
    assert.equal(result.remoteIp, '203.0.113.42');
  });

  it('falls back to socket remoteAddress', () => {
    const result = extractDetectRequest(mockReq({ headers: {} }));
    assert.equal(result.remoteIp, '192.168.1.1');
  });

  it('uses originalUrl when available (Express)', () => {
    const result = extractDetectRequest(mockReq({ url: '/rewritten', originalUrl: '/original?q=1' }));
    assert.equal(result.path, '/original?q=1');
  });

  it('flattens array header values', () => {
    const result = extractDetectRequest(mockReq({ headers: { 'set-cookie': ['a=1', 'b=2'] } }));
    assert.equal(result.headers['set-cookie'], 'a=1, b=2');
  });
});
