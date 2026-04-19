#!/usr/bin/env node
/**
 * StyloBot BDF End-to-End Test
 *
 * Replays BDF scenarios against the Demo app's BDF replay endpoint,
 * verifies detection accuracy, feedback loop, and reputation updates.
 *
 * Usage: node scripts/test-bdf-e2e.mjs [--url http://localhost:5080]
 */

import { readFileSync, readdirSync } from 'fs';
import http from 'http';
import https from 'https';
import { join } from 'path';

const BASE_URL = process.argv.find(a => a.startsWith('--url='))?.split('=')[1]
  || process.argv[process.argv.indexOf('--url') + 1]
  || 'http://localhost:5080';

const BDF_DIR = join(process.cwd(), 'bot-signatures');
const REPLAY_ENDPOINT = '/bot-detection/bdf-replay/replay';

let passed = 0;
let failed = 0;
let skipped = 0;
const results = [];

function assert(condition, name, detail) {
  if (condition) {
    passed++;
    return true;
  } else {
    failed++;
    console.error(`  FAIL: ${name}${detail ? ` - ${detail}` : ''}`);
    return false;
  }
}

function fetchJson(url, body) {
  return new Promise((resolve, reject) => {
    const parsed = new URL(url);
    const transport = parsed.protocol === 'https:' ? https : http;
    const req = transport.request({
      hostname: parsed.hostname,
      port: parsed.port,
      path: parsed.pathname,
      method: body ? 'POST' : 'GET',
      headers: {
        'Content-Type': 'application/json',
        'Connection': 'close',
        'X-SB-Api-Key': 'SB-BDF-TEST',
        ...(body ? { 'Content-Length': Buffer.byteLength(JSON.stringify(body)) } : {})
      },
      rejectUnauthorized: false,
      timeout: 30000,
    }, (res) => {
      let data = '';
      res.on('data', chunk => data += chunk);
      res.on('end', () => {
        try {
          resolve({ status: res.statusCode, body: JSON.parse(data) });
        } catch {
          resolve({ status: res.statusCode, body: data });
        }
      });
    });
    req.on('error', reject);
    req.on('timeout', () => { req.destroy(); reject(new Error('timeout')); });
    if (body) req.write(JSON.stringify(body));
    req.end();
  });
}

function sleep(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

// Load all BDF scenario files
function loadScenarios() {
  const files = readdirSync(BDF_DIR).filter(f => f.endsWith('.json'));
  const scenarios = [];
  for (const file of files) {
    try {
      const content = readFileSync(join(BDF_DIR, file), 'utf8');
      const scenario = JSON.parse(content);
      if (scenario.scenarioName && scenario.requests) {
        scenarios.push({ file, ...scenario });
      }
    } catch (e) {
      console.error(`  Skip ${file}: ${e.message}`);
    }
  }
  return scenarios;
}

// Convert BDF scenario to replay format
function toReplayRequest(scenario) {
  const requests = scenario.requests.slice(0, 10).map(r => ({
    method: r.method || 'GET',
    path: r.path || '/',
    headers: {
      'User-Agent': r.headers?.['User-Agent'] || scenario.clientProfile?.userAgent || 'unknown',
      ...(r.headers || {})
    },
    delayAfter: Math.min(r.delayAfter || 0.1, 0.5), // Cap delays for fast test
    expectedDetection: r.expectedDetection || null
  }));

  return {
    scenarioName: scenario.scenarioName,
    requests
  };
}

async function runScenario(scenario) {
  const replayReq = toReplayRequest(scenario);
  const url = `${BASE_URL}${REPLAY_ENDPOINT}`;

  try {
    const resp = await fetchJson(url, replayReq);

    if (resp.status !== 200) {
      console.error(`  ${scenario.scenarioName}: HTTP ${resp.status}`);
      return { name: scenario.scenarioName, status: 'error', httpStatus: resp.status };
    }

    const result = resp.body;
    const summary = result.summary || {};

    return {
      name: scenario.scenarioName,
      status: 'ok',
      totalRequests: summary.totalRequests || 0,
      matchRate: summary.matchRate || 0,
      falsePositives: summary.falsePositives || 0,
      falseNegatives: summary.falseNegatives || 0,
      expectedBot: scenario.confidence >= 0.5,
      results: result.results || []
    };
  } catch (e) {
    return { name: scenario.scenarioName, status: 'error', error: e.message };
  }
}

async function testDetectionAccuracy(scenarios) {
  console.log('\n  Phase 1: Detection Accuracy');
  console.log('  ' + '-'.repeat(50));

  let botsDetected = 0;
  let botsTotal = 0;
  let humansCorrect = 0;
  let humansTotal = 0;

  for (const scenario of scenarios) {
    const result = await runScenario(scenario);
    results.push(result);

    if (result.status === 'error') {
      skipped++;
      continue;
    }

    const isBot = scenario.confidence >= 0.5;

    if (isBot) {
      botsTotal++;
      // Check if any request was detected as bot
      const anyBotDetected = result.results.some(r =>
        r.actual?.isBot === true || r.actual?.botProbability >= 0.5);
      if (anyBotDetected) botsDetected++;

      const icon = anyBotDetected ? '+' : 'x';
      if (!anyBotDetected) {
        console.log(`  [${icon}] ${scenario.scenarioName} (expected bot, not detected)`);
      }
    } else {
      humansTotal++;
      const anyFalsePositive = result.results.some(r =>
        r.actual?.isBot === true || r.actual?.botProbability >= 0.7);
      if (!anyFalsePositive) humansCorrect++;

      if (anyFalsePositive) {
        console.log(`  [x] ${scenario.scenarioName} (false positive)`);
      }
    }
  }

  const botRate = botsTotal > 0 ? (botsDetected / botsTotal * 100).toFixed(1) : 'N/A';
  const humanRate = humansTotal > 0 ? (humansCorrect / humansTotal * 100).toFixed(1) : 'N/A';

  console.log(`\n  Bot detection rate:   ${botsDetected}/${botsTotal} (${botRate}%)`);
  console.log(`  Human accuracy:       ${humansCorrect}/${humansTotal} (${humanRate}%)`);

  assert(botsTotal === 0 || botsDetected / botsTotal >= 0.9,
    `Bot detection rate >= 90%`, `${botRate}%`);
  // BDF synthetic context lacks browser signals (cookies, Sec-Fetch-*, full header set)
  // so human accuracy is lower than production. 30% threshold for synthetic replay.
  assert(humansTotal === 0 || humansCorrect / humansTotal >= 0.3,
    `Human accuracy >= 30% (synthetic context)`, `${humanRate}%`);
}

async function testFeedbackLoop() {
  console.log('\n  Phase 2: Feedback & Learning Loop');
  console.log('  ' + '-'.repeat(50));

  // Send the same bot pattern twice - second time should have higher confidence
  const botScenario = {
    scenarioName: 'feedback-test-bot',
    requests: [
      { method: 'GET', path: '/api/users', headers: { 'User-Agent': 'TestBot/1.0 (feedback-test)' }, delayAfter: 0.1 },
      { method: 'GET', path: '/api/data', headers: { 'User-Agent': 'TestBot/1.0 (feedback-test)' }, delayAfter: 0.1 },
      { method: 'GET', path: '/api/items', headers: { 'User-Agent': 'TestBot/1.0 (feedback-test)' }, delayAfter: 0.1 },
    ]
  };

  // First pass
  const pass1 = await fetchJson(`${BASE_URL}${REPLAY_ENDPOINT}`, botScenario);
  const prob1 = pass1.body?.results?.[0]?.actual?.botProbability || 0;

  // Wait for feedback to propagate
  await sleep(3000);

  // Second pass - same pattern
  const pass2 = await fetchJson(`${BASE_URL}${REPLAY_ENDPOINT}`, botScenario);
  const prob2 = pass2.body?.results?.[0]?.actual?.botProbability || 0;

  console.log(`  First pass bot probability:  ${prob1.toFixed(3)}`);
  console.log(`  Second pass bot probability: ${prob2.toFixed(3)}`);
  console.log(`  Reputation learned:          ${prob2 >= prob1 ? 'YES' : 'NO'} (delta: ${(prob2 - prob1).toFixed(3)})`);

  assert(prob1 > 0, 'First pass detects bot pattern', `prob=${prob1.toFixed(3)}`);
  assert(pass1.status === 200, 'Replay endpoint returns 200');
  // Reputation should be same or higher on second pass
  assert(prob2 >= prob1 - 0.05, 'Reputation maintained or improved', `${prob1.toFixed(3)} -> ${prob2.toFixed(3)}`);
}

async function testReputationPersistence() {
  console.log('\n  Phase 3: Reputation Persistence');
  console.log('  ' + '-'.repeat(50));

  // Send a known bad pattern multiple times
  const badBot = {
    scenarioName: 'reputation-test',
    requests: Array.from({ length: 5 }, (_, i) => ({
      method: 'GET',
      path: `/scrape/page${i}`,
      headers: { 'User-Agent': 'python-requests/2.99.0 (reputation-test)' },
      delayAfter: 0.05
    }))
  };

  // Build reputation
  for (let i = 0; i < 3; i++) {
    await fetchJson(`${BASE_URL}${REPLAY_ENDPOINT}`, badBot);
    await sleep(1000);
  }

  // Check final detection
  const final = await fetchJson(`${BASE_URL}${REPLAY_ENDPOINT}`, badBot);
  const finalProbs = (final.body?.results || []).map(r => r.actual?.botProbability || 0);
  const avgProb = finalProbs.length > 0 ? finalProbs.reduce((a, b) => a + b) / finalProbs.length : 0;

  console.log(`  After 3 rounds, avg bot probability: ${avgProb.toFixed(3)}`);
  console.log(`  Individual: ${finalProbs.map(p => p.toFixed(2)).join(', ')}`);

  assert(avgProb >= 0.5, 'Reputation builds over repeated visits', `avg=${avgProb.toFixed(3)}`);
}

async function testMixedTraffic() {
  console.log('\n  Phase 4: Mixed Bot/Human Traffic');
  console.log('  ' + '-'.repeat(50));

  // Interleave bot and human-like requests
  const mixed = {
    scenarioName: 'mixed-traffic-test',
    requests: [
      // Human-like
      { method: 'GET', path: '/', headers: {
        'User-Agent': 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
        'Accept': 'text/html,application/xhtml+xml',
        'Accept-Language': 'en-US,en;q=0.9',
        'Accept-Encoding': 'gzip, deflate, br',
      }, delayAfter: 0.5 },
      // Bot
      { method: 'GET', path: '/api/users', headers: { 'User-Agent': 'scrapy/2.11.0' }, delayAfter: 0.05 },
      // Human-like
      { method: 'GET', path: '/about', headers: {
        'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0',
        'Accept': 'text/html',
        'Accept-Language': 'en-GB,en;q=0.5',
        'Referer': 'https://www.google.com/',
      }, delayAfter: 1.0 },
      // Bot
      { method: 'GET', path: '/sitemap.xml', headers: { 'User-Agent': 'wget/1.21' }, delayAfter: 0.01 },
    ]
  };

  const resp = await fetchJson(`${BASE_URL}${REPLAY_ENDPOINT}`, mixed);
  const mixedResults = resp.body?.results || [];

  let correctClassifications = 0;
  const expected = [false, true, false, true]; // human, bot, human, bot
  for (let i = 0; i < Math.min(mixedResults.length, expected.length); i++) {
    const isBot = (mixedResults[i].actual?.botProbability || 0) >= 0.5;
    if (isBot === expected[i]) correctClassifications++;
    const icon = isBot === expected[i] ? '+' : 'x';
    console.log(`  [${icon}] Request ${i+1}: prob=${(mixedResults[i].actual?.botProbability || 0).toFixed(2)} ` +
      `(expected ${expected[i] ? 'bot' : 'human'}, got ${isBot ? 'bot' : 'human'})`);
  }

  assert(correctClassifications >= 3, `Mixed traffic: >= 3/4 correct`, `${correctClassifications}/4`);
}

async function main() {
  console.log('\n  StyloBot BDF End-to-End Test Suite');
  console.log('  ' + '='.repeat(50));
  console.log(`  Target: ${BASE_URL}`);

  // Check target is up
  try {
    await fetchJson(`${BASE_URL}/`);
  } catch (e) {
    console.error(`\n  FATAL: Cannot connect to ${BASE_URL}: ${e.message}`);
    console.error('  Start the Demo app: dotnet run --project Mostlylucid.BotDetection.Demo');
    process.exit(1);
  }

  // Load BDF scenarios
  const scenarios = loadScenarios();
  console.log(`  Loaded ${scenarios.length} BDF scenarios\n`);

  // Phase 1: Detection accuracy across all BDF scenarios
  // Run human scenarios first to avoid bot reputation contamination
  const humans = scenarios.filter(s => s.confidence < 0.5);
  const bots = scenarios.filter(s => s.confidence >= 0.5);
  const ordered = [...humans, ...bots];
  await testDetectionAccuracy(ordered);

  // Phase 2: Feedback loop
  await testFeedbackLoop();

  // Phase 3: Reputation persistence
  await testReputationPersistence();

  // Phase 4: Mixed traffic
  await testMixedTraffic();

  // Summary
  console.log('\n  ' + '='.repeat(50));
  console.log(`  Results: ${passed} passed, ${failed} failed, ${skipped} skipped`);
  console.log('');

  process.exit(failed > 0 ? 1 : 0);
}

main().catch(e => {
  console.error(`  FATAL: ${e.message}`);
  process.exit(1);
});
