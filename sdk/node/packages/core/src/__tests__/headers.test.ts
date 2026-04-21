import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { parseStyloBotHeaders, STYLOBOT_HEADERS } from '../headers.ts';

describe('parseStyloBotHeaders', () => {
  it('returns null when X-StyloBot-IsBot header is missing', () => {
    assert.equal(parseStyloBotHeaders({}), null);
  });

  it('parses a full bot verdict', () => {
    const headers = {
      [STYLOBOT_HEADERS.IS_BOT]: 'true',
      [STYLOBOT_HEADERS.PROBABILITY]: '0.92',
      [STYLOBOT_HEADERS.CONFIDENCE]: '0.87',
      [STYLOBOT_HEADERS.BOT_TYPE]: 'Scraper',
      [STYLOBOT_HEADERS.BOT_NAME]: 'GPTBot',
      [STYLOBOT_HEADERS.RISK_BAND]: 'High',
      [STYLOBOT_HEADERS.ACTION]: 'Block',
      [STYLOBOT_HEADERS.THREAT_SCORE]: '0.15',
      [STYLOBOT_HEADERS.THREAT_BAND]: 'Low',
    };
    const v = parseStyloBotHeaders(headers)!;
    assert.equal(v.isBot, true);
    assert.equal(v.botProbability, 0.92);
    assert.equal(v.confidence, 0.87);
    assert.equal(v.botType, 'Scraper');
    assert.equal(v.botName, 'GPTBot');
    assert.equal(v.riskBand, 'High');
    assert.equal(v.recommendedAction, 'Block');
    assert.equal(v.threatScore, 0.15);
    assert.equal(v.threatBand, 'Low');
  });

  it('parses a human verdict with defaults', () => {
    const v = parseStyloBotHeaders({ [STYLOBOT_HEADERS.IS_BOT]: 'false', [STYLOBOT_HEADERS.PROBABILITY]: '0.12', [STYLOBOT_HEADERS.CONFIDENCE]: '0.95' })!;
    assert.equal(v.isBot, false);
    assert.equal(v.botType, null);
    assert.equal(v.riskBand, 'Unknown');
    assert.equal(v.recommendedAction, 'Allow');
  });

  it('handles array header values', () => {
    const v = parseStyloBotHeaders({ [STYLOBOT_HEADERS.IS_BOT]: ['true', 'false'], [STYLOBOT_HEADERS.PROBABILITY]: ['0.80'] })!;
    assert.equal(v.isBot, true);
    assert.equal(v.botProbability, 0.80);
  });
});
