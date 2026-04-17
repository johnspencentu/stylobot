import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend } from 'k6/metrics';

// Custom metrics
const botDetected = new Rate('bot_detected');
const detectionTime = new Trend('detection_time_ms');

// Staging target
const BASE_URL = 'http://192.168.0.89:8090';
const API_KEY = 'STAGING-BYPASS';

export const options = {
  stages: [
    { duration: '30s', target: 20 },   // Ramp up to 20 VUs
    { duration: '1m', target: 50 },    // Sustained 50 VUs
    { duration: '30s', target: 100 },  // Peak at 100 VUs
    { duration: '1m', target: 100 },   // Sustained peak
    { duration: '30s', target: 0 },    // Ramp down
  ],
  thresholds: {
    http_req_duration: ['p(95)<500', 'p(99)<1000'],  // 95th < 500ms, 99th < 1s
    http_req_failed: ['rate<0.01'],                   // <1% errors
    detection_time_ms: ['p(95)<50'],                  // Detection <50ms at p95
  },
};

// Representative request profiles
const profiles = [
  // Real browser (human-like)
  {
    name: 'chrome_human',
    weight: 40,
    headers: {
      'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36',
      'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8',
      'Accept-Language': 'en-US,en;q=0.9',
      'Accept-Encoding': 'gzip, deflate, br',
      'Sec-Fetch-Dest': 'document',
      'Sec-Fetch-Mode': 'navigate',
      'Sec-Fetch-Site': 'none',
      'Sec-Fetch-User': '?1',
      'Upgrade-Insecure-Requests': '1',
    },
    paths: ['/', '/about', '/features', '/docs', '/pricing', '/contact'],
  },
  // curl bot
  {
    name: 'curl_bot',
    weight: 15,
    headers: {
      'User-Agent': 'curl/8.7.1',
      'Accept': '*/*',
    },
    paths: ['/', '/api/health', '/.env', '/wp-admin'],
  },
  // Python scraper
  {
    name: 'python_scraper',
    weight: 15,
    headers: {
      'User-Agent': 'python-requests/2.31.0',
      'Accept': '*/*',
      'Accept-Encoding': 'gzip, deflate',
    },
    paths: ['/', '/api/data', '/sitemap.xml', '/robots.txt'],
  },
  // Googlebot
  {
    name: 'googlebot',
    weight: 10,
    headers: {
      'User-Agent': 'Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)',
      'Accept': 'text/html',
    },
    paths: ['/', '/about', '/docs'],
  },
  // Headless Chrome (automation)
  {
    name: 'headless_chrome',
    weight: 10,
    headers: {
      'User-Agent': 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/120.0.0.0 Safari/537.36',
      'Accept': 'text/html',
    },
    paths: ['/', '/login', '/api/data', '/checkout'],
  },
  // API client with bypass key (monitoring)
  {
    name: 'api_monitor',
    weight: 10,
    headers: {
      'User-Agent': 'StyloBot-Monitor/1.0',
      'Accept': 'application/json',
      'X-SB-Api-Key': API_KEY,
    },
    paths: ['/api/health', '/bot-detection/check'],
  },
];

// Weighted random selection
function pickProfile() {
  const total = profiles.reduce((sum, p) => sum + p.weight, 0);
  let r = Math.random() * total;
  for (const p of profiles) {
    r -= p.weight;
    if (r <= 0) return p;
  }
  return profiles[0];
}

export default function () {
  const profile = pickProfile();
  const path = profile.paths[Math.floor(Math.random() * profile.paths.length)];

  const res = http.get(`${BASE_URL}${path}`, {
    headers: profile.headers,
    tags: { profile: profile.name },
  });

  // Extract detection metrics from response headers
  const riskScore = parseFloat(res.headers['X-Bot-Risk-Score'] || '0');
  const processingMs = parseFloat(res.headers['X-Bot-Processing-Ms'] || '0');

  botDetected.add(riskScore > 0.7 ? 1 : 0);
  detectionTime.add(processingMs);

  check(res, {
    'status is not 5xx': (r) => r.status < 500,
    'has detection headers': (r) => r.headers['X-Bot-Risk-Score'] !== undefined,
    'detection under 50ms': () => processingMs < 50,
  });

  // Simulate realistic think time
  sleep(Math.random() * 2 + 0.5);
}

export function handleSummary(data) {
  const p95 = data.metrics.http_req_duration.values['p(95)'];
  const p99 = data.metrics.http_req_duration.values['p(99)'];
  const detP95 = data.metrics.detection_time_ms ? data.metrics.detection_time_ms.values['p(95)'] : 'N/A';
  const rps = data.metrics.http_reqs.values.rate;
  const botRate = data.metrics.bot_detected ? data.metrics.bot_detected.values.rate : 'N/A';

  console.log('\n=== StyloBot Staging Load Test Results ===');
  console.log(`Throughput: ${rps.toFixed(1)} req/s`);
  console.log(`Response p95: ${p95.toFixed(1)}ms, p99: ${p99.toFixed(1)}ms`);
  console.log(`Detection p95: ${detP95}ms`);
  console.log(`Bot detection rate: ${(botRate * 100).toFixed(1)}%`);
  console.log('==========================================\n');

  return {
    stdout: JSON.stringify(data, null, 2),
  };
}
