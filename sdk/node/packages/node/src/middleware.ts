import type { Request, Response, NextFunction, RequestHandler } from 'express';
import { StyloBotClient, parseStyloBotHeaders, type Verdict, type DetectResponse } from '@stylobot/core';
import { extractDetectRequest } from './extract.js';

export interface StyloBotMiddlewareOptions {
  mode: 'headers' | 'api';
  endpoint?: string;
  apiKey?: string;
  timeout?: number;
}

export interface StyloBotResult {
  isBot: boolean;
  verdict: Verdict;
  signals: Record<string, unknown>;
  reasons: DetectResponse['reasons'];
  meta: DetectResponse['meta'] | null;
}

declare global {
  namespace Express {
    interface Request { stylobot: StyloBotResult; }
  }
}

const EMPTY_VERDICT: Verdict = {
  isBot: false, botProbability: 0, confidence: 0, botType: null, botName: null,
  riskBand: 'Unknown', recommendedAction: 'Allow', threatScore: 0, threatBand: 'None',
};

export function styloBotMiddleware(options: StyloBotMiddlewareOptions): RequestHandler {
  if (options.mode === 'api') {
    if (!options.endpoint) throw new Error('endpoint is required for api mode');

    const client = new StyloBotClient({ endpoint: options.endpoint, apiKey: options.apiKey, timeout: options.timeout });

    return async (req: Request, res: Response, next: NextFunction) => {
      try {
        const detectReq = extractDetectRequest(req);
        const response = await client.detect(detectReq);
        req.stylobot = { isBot: response.verdict.isBot, verdict: response.verdict, signals: response.signals, reasons: response.reasons, meta: response.meta };
      } catch {
        req.stylobot = { isBot: false, verdict: EMPTY_VERDICT, signals: {}, reasons: [], meta: null };
      }
      next();
    };
  }

  return (req: Request, _res: Response, next: NextFunction) => {
    const verdict = parseStyloBotHeaders(req.headers as Record<string, string>) ?? EMPTY_VERDICT;
    req.stylobot = { isBot: verdict.isBot, verdict, signals: {}, reasons: [], meta: null };
    next();
  };
}
