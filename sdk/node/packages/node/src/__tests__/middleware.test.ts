import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { IncomingMessage } from 'node:http';
import { Socket } from 'node:net';
import { styloBotMiddleware } from '../middleware.js';

function mockExpressReq(headers: Record<string, string> = {}): any {
  const socket = new Socket();
  (socket as any).remoteAddress = '127.0.0.1';
  const req = new IncomingMessage(socket);
  req.method = 'GET'; req.url = '/test'; req.headers = headers;
  (req as any).originalUrl = '/test'; (req as any).ip = '127.0.0.1'; (req as any).protocol = 'https';
  return req;
}

describe('styloBotMiddleware (header mode)', () => {
  it('parses X-StyloBot-* headers into req.stylobot', (_, done) => {
    const mw = styloBotMiddleware({ mode: 'headers' });
    const req = mockExpressReq({
      'x-stylobot-isbot': 'true', 'x-stylobot-probability': '0.88', 'x-stylobot-confidence': '0.75',
      'x-stylobot-bottype': 'AiBot', 'x-stylobot-botname': 'Claude', 'x-stylobot-riskband': 'Medium',
      'x-stylobot-action': 'Challenge', 'x-stylobot-threatscore': '0.05', 'x-stylobot-threatband': 'None',
    });
    mw(req, {} as any, () => {
      assert.equal(req.stylobot.isBot, true);
      assert.equal(req.stylobot.verdict.botProbability, 0.88);
      assert.equal(req.stylobot.verdict.botType, 'AiBot');
      assert.equal(req.stylobot.verdict.recommendedAction, 'Challenge');
      done();
    });
  });

  it('returns empty verdict when no headers present', (_, done) => {
    const mw = styloBotMiddleware({ mode: 'headers' });
    mw(mockExpressReq({}), {} as any, () => {
      assert.equal(mockExpressReq({}).stylobot, undefined); // fresh req has no stylobot yet
      done();
    });
  });
});

describe('styloBotMiddleware (api mode)', () => {
  it('throws if endpoint is not provided', () => {
    assert.throws(() => styloBotMiddleware({ mode: 'api' }), /endpoint is required/);
  });
});
