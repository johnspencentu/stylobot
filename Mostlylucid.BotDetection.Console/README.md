# StyloBot Console Gateway

Minimal, single-file YARP gateway with bot detection. Built on .NET 10 with Native AOT compilation and minimal APIs for
maximum performance and minimal footprint.

## Features

- **Single-file executable** - Native AOT compiled and trimmed
- **Cross-platform** - Windows (x64, ARM64), Linux (x64, ARM64 including Raspberry Pi 4/5), macOS (x64, ARM64)
- **Modern .NET 10** - Uses ASP.NET Core minimal APIs and latest runtime optimizations
- **Signature tracking** - Stores high-confidence detections as HMAC-SHA256 hashed signatures (core feature)
- **Two modes**:
    - **Demo Mode** (default) - Full verbose logging, all detectors, no blocking
    - **Production Mode** - Blocking, background learning, AI escalation
- **Tiny footprint** - ~10-27MB single executable (varies by platform)
- **Self-contained** - No runtime required; ships with required native libraries alongside binary
- **Console logging** - All detections logged with full details
- **Zero-PII by default** - No raw identifiers logged or stored; only keyed HMAC-SHA256 signatures for correlation and
  lookup
- **CSP header removal** - Removes Content-Security-Policy headers from upstream responses (
  see [Security Considerations](#security-considerations))

> **Note**: This console gateway ships with traditional detection only. The full commercial product includes LLM-based
> detection for advanced bot classification. See [LLM Detection (Optional)](#llm-detection-optional) for configuration
> details.

## Quick Start

### Demo Mode (Default)

```bash
# Windows
stylobot.exe --upstream http://localhost:8080 --port 5080

# Linux
./stylobot --upstream http://localhost:8080 --port 5080
```

### Production Mode

```bash
# Windows
stylobot.exe --mode production --upstream http://backend:8080 --port 80

# Linux
./stylobot --mode production --upstream http://backend:8080 --port 80
```

### Using Environment Variables

```bash
export UPSTREAM=http://backend:8080
export PORT=5080
export MODE=production

./stylobot
```

## File Structure & Portability

**Core files** (required):

- `stylobot.exe` (or `stylobot` on Linux/macOS) - Single-file executable with all dependencies and static files embedded
- `appsettings.json` - Configuration file (optional - uses defaults if missing)

**Auto-created files** (all paths relative to executable directory):

| File                          | Purpose                                                    | Default Location         | Configuration                                                             |
|-------------------------------|------------------------------------------------------------|--------------------------|---------------------------------------------------------------------------|
| `logs/errors-YYYY-MM-DD.log`  | Error logs (Warning+ only)                                 | `logs/` in exe directory | `Serilog:WriteTo:File:Args:path` in appsettings.json                      |
| `botdetection.db`             | SQLite database for bot lists (user agents, IPs, patterns) | Exe directory            | `BotDetection:BotListDatabase:DbPath` in appsettings.json                 |
| `signatures-YYYY-MM-DD.jsonl` | High-confidence bot signatures (HMAC-SHA256 hashed)        | Exe directory            | `BotDetection:SignatureLogging:Directory` in appsettings.json             |
| `patterns.db`                 | Pattern reputation cache (ephemeral, auto-maintained)      | Exe directory            | `BotDetection:EphemeralPatternReputationCache:DbPath` in appsettings.json |

**Configuration example** (appsettings.json):

```json
{
  "Serilog": {
    "WriteTo": [
      {
        "Name": "File",
        "Args": {
          "path": "logs/errors-.log",
          "rollingInterval": "Day"
        }
      }
    ]
  },
  "BotDetection": {
    "BotListDatabase": {
      "DbPath": "botdetection.db"
    },
    "SignatureLogging": {
      "Enabled": true,
      "Directory": ".",
      "MinConfidence": 0.8
    },
    "EphemeralPatternReputationCache": {
      "DbPath": "patterns.db"
    }
  }
}
```

**Fully portable**: Copy the executable and optional appsettings.json to any location and the app will work. No
installation required, no registry entries, no system dependencies, no separate wwwroot folder needed. All runtime files
are created automatically in configurable locations.

**Example deployment**:

```bash
# Copy to production server
scp stylobot appsettings.json user@server:/opt/stylobot/

# Run it
ssh user@server
cd /opt/stylobot
./stylobot --upstream http://backend:8080 --port 80 --mode production

# Logs appear in /opt/stylobot/logs/
```

**Running as a service**: The executable includes built-in support for Windows Services and Linux systemd. Just register
it with your OS's service manager - no special installation scripts required.

## Modes

### Demo Mode

- **Purpose**: Testing, development, observability
- **Detectors**: Full pipeline with trigger bypass for maximum visibility
- **Blocking**: Disabled - all traffic allowed
- **Learning**: Disabled
- **Logging**: Full verbose output with all signals and detector contributions
- **Action**: Log only

Example output (default zero-PII mode):

```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
🔍 Bot Detection Result
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  Request:     GET /api/data
  IP:          f89fa40dca80b591bc5dd6716928737a
  User-Agent:  d84d795b8eb696aaa6d0d393f7c3634b

  IsBot:       ✗ YES
  Confidence:  0.76
  Bot Type:    Unknown
  Bot Name:    (none)
  Time:        2.30ms

  Detection Reasons: 3
  ┌─────────────────────────────────────────────────────
  │ UserAgent                   0.90 - Matched pattern: ^\w+\/[\d\.]+$
  │ Header                     -0.15 - Headers appear normal
  │ SecurityTool                0.00 - No security tools detected
  └─────────────────────────────────────────────────────

  Primary Category: UserAgent
  Policy Used:      demo
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

**Note**: By default, IP and User-Agent are logged as HMAC-SHA256 hashes only. See [Zero-PII Logging](#zero-pii-logging)
for details.

### Production Mode

- **Purpose**: Real-world deployment with bot blocking
- **Detectors**: Fast-path + slow-path + AI escalation
    - Fast: FastPathReputation, UserAgent, Header, IP, SecurityTool
    - Slow: Behavioral, AdvancedBehavioral, ClientSide, Inconsistency, VersionAge, HTTP/2, TLS, TCP/IP, MultiLayerCorrelation, BehavioralWaveform
    - AI: `Llm` and `HeuristicLate` when AI escalation is enabled
- **Blocking**: Enabled with adaptive policies
    - > 0.95 risk: Immediate block (403)
    - > 0.70 risk: Block with logging
    - > 0.50 risk: Throttle (rate limit)
    - <0.30 risk: Allow with minimal logging
- **Learning**: Background learning enabled
- **Logging**: Concise one-line format
- **Response Path**: Honeypot tracking, error pattern detection

Example output (always zero-PII in production):

```
✓ HUMAN  0.12 -              1.2ms GET  /        [f89fa40dca80b591] -
✗ BOT    0.95 Unknown        2.3ms GET  /api     [22fd81614ede36a3] -
✓ HUMAN  0.25 -              1.5ms POST /submit  [adc0960d7d74a88c] -
✗ BOT    0.87 Scraper        8.7ms GET  /admin   [31490e9910a08af3] -
```

**Note**: Production mode ALWAYS uses HMAC-SHA256 hashes for IP addresses (no raw IPs logged).

## Command-Line Options

| Option              | Environment Variable | Default                 | Description                             |
|---------------------|----------------------|-------------------------|-----------------------------------------|
| `--upstream`        | `UPSTREAM`           | `http://localhost:8080` | Upstream server URL                     |
| `--port`            | `PORT`               | `5080`                  | Port to listen on                       |
| `--mode`            | `MODE`               | `demo`                  | Mode: `demo` or `production`            |
| `--policy`          | `STYLOBOT_POLICY`    | `logonly`               | Override default action policy          |
| `--threshold`       | -                    | `0.7`                   | Override bot threshold                  |
| `--llm`             | -                    | -                       | Ollama base URL for LLM escalation      |
| `--model`           | -                    | `qwen3:0.6b`            | Ollama model name                       |
| `--config`          | -                    | -                       | Additional JSON config file             |
| `--log-level`       | -                    | `Warning`               | Minimum Serilog level                   |
| `--verbose`         | -                    | `false`                 | Disable live table and print full logs  |
| `--cert` / `--key`  | -                    | -                       | Enable TLS with PFX or PEM certificate  |
| `--tunnel [token]`  | -                    | -                       | Start `cloudflared` tunnel              |

## Configuration

### Configuration Files

The gateway uses two configuration files:

- `appsettings.json` - Demo mode configuration (full logging, no blocking)
- `appsettings.production.json` - Production mode configuration (blocking, learning, AI escalation)

**Switching between configurations:**

```bash
# Demo mode (uses appsettings.json)
./stylobot --mode demo

# Production mode (uses appsettings.production.json)
./stylobot --mode production

# Using environment variable
export MODE=production
./stylobot
```

The `--mode` flag determines which configuration file is loaded. This allows you to maintain separate configurations for
development/testing and production without modifying files.

### Environment Variables

Supported operational environment variables include:

- `UPSTREAM`
- `DEFAULT_UPSTREAM`
- `PORT`
- `MODE`
- `STYLOBOT_POLICY`
- `KNOWN_NETWORKS`
- `KNOWN_PROXIES`
- `TRUST_ALL_FORWARDED_PROXIES`

## Zero-PII Logging

**Default Behavior**: No raw identifiers are logged or stored. Only keyed HMAC-SHA256 signatures of selected request
factors for correlation and lookup.

### How It Works

Raw identifiers (IP, User-Agent, referrer, etc.) are **observed transiently** to compute detection signals, but only *
*HMAC-SHA256 hashed signatures** are **persisted** to logs and database.

**Key Properties**:

- **Not reversible**: HMAC cannot be decoded to the original value
- **Searchable**: Recompute `HMAC(key, value)` to find matches
- **Controllable**: The secret key is the capability boundary - possession of the key = ability to correlate/search

### Configuration

```json
{
  "SignatureLogging": {
    "Enabled": true,
    "MinConfidence": 0.7,
    "SignatureHashKey": "YOUR_SECRET_KEY_HERE",
    "LogRawPii": false  // DEFAULT: false (zero-PII mode)
  }
}
```

**Production**: ALWAYS uses hashes (ignores `LogRawPii` setting)
**Demo**: Uses hashes by default; set `LogRawPii=true` to show raw values alongside hashes

### Example Outputs

**Default (LogRawPii=false)**:

```
IP:          f89fa40dca80b591bc5dd6716928737a
User-Agent:  d84d795b8eb696aaa6d0d393f7c3634b
```

**Demo with LogRawPii=true**:

```
IP:          192.168.1.100 (hash: f89fa40dca80b591bc5dd6716928737a)
User-Agent:  curl/8.4.0 (hash: d84d795b8eb696aaa6d0d393f7c3634b)
```

### Threat Model

**Attacker steals database WITHOUT key**:

- ❌ Cannot recover IP/UA from hashes
- ✓ Can perform frequency analysis
- ✓ Can cluster requests by signature

**Attacker steals database AND key**:

- ✓ Can compute matches for candidate values (dictionary attack for low-entropy fields)
- Mitigation: Store key separately, rotate regularly, reduce stored factors, use TTL/decay

**Operational Security**:

- Store `SignatureHashKey` in secrets manager (Azure Key Vault, AWS Secrets Manager, etc.)
- Rotate key periodically (invalidates old signatures)
- Consider split keys: `K_sig` (persistent), `K_session` (ephemeral), `K_ip`, `K_ua`, `K_path`

## Security Considerations

### CSP Header Handling (Mode-Dependent)

The gateway handles Content Security Policy headers differently based on mode:

#### Demo Mode (`--mode demo`)

**CSP headers are PASSED THROUGH unchanged** from the upstream server.

**Why?**

- Demo mode is for testing and development
- Preserves the upstream site's security policies
- Allows you to see how CSP affects client-side detection
- No security weakening - you see the real site behavior

**Use case**: Testing against production sites, development, demonstrations

#### Production Mode (`--mode production`)

**CSP headers are REMOVED** from upstream responses.

**⚠️ IMPORTANT**: This **disables a security feature** of the upstream site.

**Why remove CSP in production?**

1. **Client-side bot detection requires `eval()`** - CSP with `script-src 'unsafe-eval'` would block it
2. **Inline scripts and dynamic code** - Bot detection uses runtime script injection
3. **Third-party script loading** - Detection may load additional resources
4. **CSP reporting endpoints** - Upstream CSP reports would be invalid/confusing

**Affected Headers**:

- `Content-Security-Policy` - Removed
- `Content-Security-Policy-Report-Only` - Removed
- `X-Frame-Options` - Removed (allows embedding for detection)

**Recommendations for Production**:

- Only proxy traffic you control and understand
- Re-implement CSP at your origin if needed (behind the gateway)
- Consider this a **detection tool**, not a security enhancement
- If your upstream has strict CSP, client-side detection may not work
- Evaluate whether server-side detection alone is sufficient

**Alternative**: If you need CSP in production, disable client-side detection and rely only on server-side detectors:

```json
{
  "BotDetection": {
    "Policies": {
      "production": {
        "FastPath": ["UserAgent", "IP", "Header", "Behavioral"],
        "SlowPath": [],
        "ResponsePath": []  // Disable client-side
      }
    }
  }
}
```

#### Learning Mode (`--mode learning`)

**CSP headers are REMOVED** (same as production).

**Why?** Learning mode needs to inject client-side detection JavaScript to train signatures, requiring CSP removal.

### SQLite on Shared Storage

**⚠️ WARNING**: Shared SQLite over NFS/SMB/EFS works for small deployments but has operational risks:

**Risks**:

- Locking semantics vary by filesystem (POSIX locks not guaranteed on all NFS)
- Network latency can cause performance degradation
- Corruption possible with concurrent writes
- Known to cause "works in staging, corrupts in prod" scenarios

**Supported Scenarios**:

- ✓ Small deployments (2-3 gateways)
- ✓ Controlled storage with guaranteed POSIX locks
- ✓ Development/testing environments

**Recommended for Production HA**:

- Per-node local SQLite + periodic merge/replication of signatures
- Central store: Postgres, Redis, rqlite, LiteFS
- Treat SQLite as write-behind cache with async central persistence

**Safe Alternative**:

```
┌─────────┐    ┌─────────┐    ┌─────────┐
│Gateway 1│    │Gateway 2│    │Gateway 3│
│ (local  │    │ (local  │    │ (local  │
│ SQLite) │    │ SQLite) │    │ SQLite) │
└────┬────┘    └────┬────┘    └────┬────┘
     │              │              │
     └──────────────┼──────────────┘
                    │
             ┌──────▼──────┐
             │  PostgreSQL │
             │   (Central) │
             └─────────────┘
```

## LLM Detection (Optional)

The console gateway supports optional Ollama-based LLM escalation. It is off unless you provide an endpoint with
`--llm` or configure the equivalent `BotDetection:AiDetection:Ollama:Endpoint` setting.

### Why Use LLM Detection?

LLM detectors provide:

- **Natural language reasoning** - Explains *why* a request looks like a bot
- **Context-aware analysis** - Understands relationships between signals
- **Adaptive learning** - Improves with examples
- **High accuracy** - Especially for sophisticated bots that pass heuristic checks

**Trade-offs**:

- ⚠️ **Latency**: 500-2000ms per request (use async/background)
- ⚠️ **Cost**: $0.001-0.01 per request depending on model
- ⚠️ **External dependency**: Requires LLM API access

### Configuration

The shipped production policy already contains `AiPath: ["Llm", "HeuristicLate"]`. To activate it, point the CLI at
an Ollama instance:

```bash
stylobot --mode production --llm http://localhost:11434 --model qwen3:0.6b
```

Equivalent config:

```json
{
  "BotDetection": {
    "AiDetection": {
      "Provider": "Ollama",
      "TimeoutMs": 2000,
      "MaxConcurrentRequests": 5,
      "Ollama": {
        "Endpoint": "http://localhost:11434",
        "Model": "qwen3:0.6b"
      }
    }
  }
}
```

### Performance Optimization

**Recommended Settings for Production**:

```json
{
  "BotDetection": {
    "AiDetection": {
      "Provider": "Ollama",
      "TimeoutMs": 1000,
      "MaxConcurrentRequests": 5,
      "Ollama": {
        "Endpoint": "http://localhost:11434",
        "Model": "qwen3:0.6b"
      }
    },
    "Policies": {
      "production": {
        "EscalateToAi": true,
        "AiEscalationThreshold": 0.6,
        "ImmediateBlockThreshold": 0.95,
        "EarlyExitThreshold": 0.3
      }
    }
  }
}
```

**Expected Impact**:

- ~5-10% of requests escalate to LLM (only uncertain cases)
- ~90% cache hit rate (same bots repeat patterns)
- Effective latency: <50ms (async + cache)
- Cost: ~$0.50-5.00 per 100k requests

### Example LLM Output

When enabled, you'll see LLM reasoning in detection results:

```json
{
  "detector": "Llm",
  "category": "AI",
  "impact": 0.85,
  "reason": "LLM classified as bot: User agent indicates Puppeteer headless browser. The combination of missing Accept-Language header and datacenter IP (AWS) strongly suggests automated scraping rather than human browsing.",
  "signals": {
    "ai.confidence": 0.85,
    "ai.prediction": "bot",
    "ai.reasoning": "Puppeteer + datacenter IP + missing headers"
  }
}
```

### Testing LLM Detection

```bash
ollama serve
ollama pull qwen3:0.6b
stylobot --mode production --llm http://localhost:11434
curl -A "HeadlessChrome/120.0.0.0" http://localhost:5080/

# Check logs for LLM reasoning
# Look for "LLM classified as bot: ..." in detection reasons
```

## Client-Side Detection

The gateway automatically enables client-side detection for proxied websites. This allows JavaScript running on your
website to perform additional bot checks and report results back to the gateway.

### How It Works

1. **Server-side detection runs first** - Gateway analyzes request and forwards headers to backend
2. **Response headers added** - Gateway adds special headers to every response:
    - `X-Bot-Detection-Callback-Url` - URL where client-side results should be sent
    - `X-Bot-Detection` - Server-side bot detection result (true/false)
    - `X-Bot-Probability` - Server confidence score (0.00-1.00)
    - `X-Bot-Name` - Bot name if identified
3. **Client-side checks run** - JavaScript on the page performs browser fingerprinting
4. **Results sent back** - JavaScript POSTs results to callback URL
5. **Gateway logs validation** - Helps identify bot evasion techniques

### Test Page

Access the built-in test page:

```
http://localhost:5080/test-client-side.html
```

This interactive test page demonstrates:

- Reading server-side detection headers
- Performing client-side checks (Canvas, WebGL, Audio, etc.)
- Sending results back to the callback endpoint
- Displaying full detection flow

### Example Client-Side Implementation

```html
<!DOCTYPE html>
<html>
<head>
    <title>My Site with Bot Detection</title>
</head>
<body>
    <h1>Welcome</h1>

    <script>
    // Automatic client-side bot detection
    (async function() {
        try {
            // Get detection headers from current page
            const response = await fetch(window.location.href, {
                method: 'HEAD',
                cache: 'no-cache'
            });

            const callbackUrl = response.headers.get('X-Bot-Detection-Callback-Url');
            const serverIsBot = response.headers.get('X-Bot-Detection') === 'True';
            const serverProbability = parseFloat(response.headers.get('X-Bot-Probability') || '0');

            console.log('Server detection:', { serverIsBot, serverProbability });

            if (!callbackUrl) {
                console.log('No callback URL provided - client-side detection disabled');
                return;
            }

            // Perform client-side checks
            const clientChecks = {
                // Canvas fingerprinting
                hasCanvas: (() => {
                    try {
                        const canvas = document.createElement('canvas');
                        const ctx = canvas.getContext('2d');
                        return !!ctx;
                    } catch (e) {
                        return false;
                    }
                })(),

                // WebGL support
                hasWebGL: (() => {
                    try {
                        const canvas = document.createElement('canvas');
                        return !!(canvas.getContext('webgl') || canvas.getContext('experimental-webgl'));
                    } catch (e) {
                        return false;
                    }
                })(),

                // Touch support
                maxTouchPoints: navigator.maxTouchPoints || 0,
                touchSupport: 'ontouchstart' in window,

                // Hardware info
                hardwareConcurrency: navigator.hardwareConcurrency || 0,
                deviceMemory: navigator.deviceMemory || 0,

                // Plugin count
                pluginCount: navigator.plugins ? navigator.plugins.length : 0,

                // Languages
                languages: navigator.languages || [navigator.language],

                // Screen info
                screen: {
                    width: screen.width,
                    height: screen.height,
                    colorDepth: screen.colorDepth
                },

                // Timezone
                timezone: Intl.DateTimeFormat().resolvedOptions().timeZone,
                timezoneOffset: new Date().getTimezoneOffset()
            };

            // Send results back to gateway
            const callbackResponse = await fetch(callbackUrl, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({
                    timestamp: new Date().toISOString(),
                    serverDetection: {
                        isBot: serverIsBot,
                        probability: serverProbability
                    },
                    clientChecks: clientChecks,
                    userAgent: navigator.userAgent,
                    referrer: document.referrer
                })
            });

            const result = await callbackResponse.json();
            console.log('Client-side detection sent:', result);

        } catch (error) {
            console.error('Client-side detection error:', error);
        }
    })();
    </script>
</body>
</html>
```

### Client-Side Check Examples

Common checks to detect bots:

**Canvas Fingerprinting:**

```javascript
const canvas = document.createElement('canvas');
const ctx = canvas.getContext('2d');
canvas.width = 200;
canvas.height = 50;
ctx.textBaseline = 'top';
ctx.font = '14px Arial';
ctx.fillStyle = '#f60';
ctx.fillRect(125, 1, 62, 20);
ctx.fillStyle = '#069';
ctx.fillText('Test 🤖', 2, 15);
const fingerprint = canvas.toDataURL();
```

**WebGL Vendor Detection:**

```javascript
const canvas = document.createElement('canvas');
const gl = canvas.getContext('webgl');
if (gl) {
    const vendor = gl.getParameter(gl.VENDOR);
    const renderer = gl.getParameter(gl.RENDERER);
    // Real browsers: "Google Inc.", "ANGLE (Intel...)"
    // Headless: "Brian Paul", "Mesa OffScreen", etc.
}
```

**Audio Context Fingerprinting:**

```javascript
const AudioContext = window.AudioContext || window.webkitAudioContext;
if (AudioContext) {
    const audioCtx = new AudioContext();
    const oscillator = audioCtx.createOscillator();
    // Create unique fingerprint from audio processing
}
```

**Plugin Detection:**

```javascript
const pluginCount = navigator.plugins.length;
const plugins = Array.from(navigator.plugins).map(p => p.name);
// Headless browsers typically have 0 plugins
```

### Callback Endpoint

**POST** `/api/bot-detection/client-result`

**Request Body:**

```json
{
  "timestamp": "2025-12-12T15:30:00Z",
  "serverDetection": {
    "isBot": false,
    "probability": 0.15
  },
  "clientChecks": {
    "hasCanvas": true,
    "hasWebGL": true,
    "maxTouchPoints": 0,
    "touchSupport": false,
    "hardwareConcurrency": 8,
    "pluginCount": 3,
    "languages": ["en-US", "en"],
    "screen": {
      "width": 1920,
      "height": 1080,
      "colorDepth": 24
    },
    "timezone": "America/New_York",
    "timezoneOffset": 300
  },
  "userAgent": "Mozilla/5.0 ...",
  "referrer": "https://google.com"
}
```

**Response:**

```json
{
  "status": "accepted",
  "message": "Client-side detection result processed"
}
```

### Benefits of Client-Side Detection

- **Detects headless browsers** - Puppeteer, Playwright, Selenium often fail client-side checks
- **Validates server detection** - Confirms server-side bot detection was accurate
- **Catches sophisticated bots** - Bots that pass server checks may fail client checks
- **No latency impact** - Runs after response is sent to client
- **Privacy-safe** - Only fingerprinting data sent, no PII
- **Adaptive learning** - Gateway can learn from client-side validation results

### Configuration Reference

#### Global Settings

```json
{
  "BotDetection": {
    // Which detection policy to use by default
    "DefaultPolicyName": "production",

    // Which action policy to use when bots are detected
    "DefaultActionPolicyName": "block",

    // Enable background learning (updates signature database)
    "EnableLearning": true
  }
}
```

#### Detection Policies

Detection policies define **WHICH** detectors run and **HOW** they're orchestrated. Each policy can specify four
detector paths:

**Fast Path (Wave 0)** - Synchronous detectors that run on every request (<100ms expected):

- `FastPathReputation` - Check cached signatures for instant allow/block
- `UserAgent` - Analyze User-Agent header for bot patterns
- `Header` - Check HTTP headers for anomalies
- `Ip` - IP reputation, datacenter detection, VPN/proxy identification
- `SecurityTool` - Detect security scanners (Acunetix, SQLMap, etc.)
- `Behavioral` - Analyze request patterns, timing, cookies
- `ClientSide` - JavaScript capabilities, WebGL, Canvas fingerprinting
- `Inconsistency` - Header/UA mismatches, impossible combinations
- `VersionAge` - Detect outdated browser versions

**Slow Path (Wave 1+)** - Asynchronous detectors for deeper analysis:

- `AdvancedBehavioral` - Deep behavioral analysis with ML features
- `Http2Fingerprint` - HTTP/2 frame analysis (SETTINGS, PRIORITY)
- `TlsFingerprint` - TLS cipher suites and handshake patterns
- `TcpIpFingerprint` - TCP/IP stack fingerprinting
- `MultiLayerCorrelation` - Cross-layer consistency checks
- `BehavioralWaveform` - Request timing waveform analysis

**AI Path** - Expensive detectors (only run when escalated):

- `Llm` - Ollama LLM analysis
- `HeuristicLate` - learned heuristic follow-up analysis

**Response Path** - Post-request detectors (zero latency impact):

- `ResponseBehavior` - Track response patterns for learning

**Orchestration Settings:**

```json
{
  "Policies": {
    "production": {
      "Description": "PRODUCTION MODE - Full detection pipeline with blocking",

      "FastPath": ["FastPathReputation", "UserAgent", "Header", "Ip", "SecurityTool"],
      "SlowPath": ["Behavioral", "AdvancedBehavioral", "ClientSide"],
      "AiPath": ["Llm", "HeuristicLate"],
      "ResponsePath": ["ResponseBehavior"],

      // Use fast path before slow path
      "UseFastPath": true,

      // Force slow path even if fast path is conclusive (high security)
      "ForceSlowPath": false,

      // Escalate to AI when uncertainty is high
      "EscalateToAi": true,

      // Risk threshold to trigger AI escalation (0.0-1.0)
      // 0.6 = escalate when 60% confident it's a bot but not certain
      "AiEscalationThreshold": 0.6,

      // Risk threshold for early exit (allow without slow path)
      // 0.3 = if risk <30%, skip slow path and allow immediately
      "EarlyExitThreshold": 0.3,

      // Risk threshold for immediate block (skip slow path)
      // 0.95 = if risk >95%, block immediately without further analysis
      "ImmediateBlockThreshold": 0.95,

      // Default action policy for this detection policy
      "ActionPolicyName": "block"
    }
  }
}
```

**Dynamic Policy Transitions** - Automatically switch action policies based on risk:

```json
{
  "Transitions": [
    {
      // Very high risk (>95%) - immediate hard block
      "WhenRiskExceeds": 0.95,
      "ActionPolicyName": "block-hard"
    },
    {
      // High risk (>70%) - standard block with logging
      "WhenRiskExceeds": 0.7,
      "ActionPolicyName": "block"
    },
    {
      // Medium risk (>50%) - throttle/rate limit
      "WhenRiskExceeds": 0.5,
      "ActionPolicyName": "throttle"
    },
    {
      // Low risk (<30%) - log only, allow through
      "WhenRiskBelow": 0.3,
      "ActionPolicyName": "logonly"
    }
  ]
}
```

#### Action Policies

Action policies define **WHAT HAPPENS** when a bot is detected:

**block-hard** - Immediate block for very high risk (>95% confidence):

```json
{
  "ActionPolicies": {
    "block-hard": {
      "Description": "Immediate block for very high risk (>95% confidence)",
      "BlockAction": {
        "Enabled": true,
        "StatusCode": 403,
        "Body": "Access denied"
      },
      "LogAction": {
        "Enabled": true,
        "LogLevel": "Warning",
        "IncludeSignature": true
      }
    }
  }
}
```

**block** - Block suspected bots (70-95% confidence):

```json
{
  "block": {
    "Description": "Block suspected bots (70-95% confidence)",
    "BlockAction": {
      "Enabled": true,
      "StatusCode": 403,
      "Body": "Access denied - bot detected"
    },
    "LogAction": {
      "Enabled": true,
      "LogLevel": "Information",
      "IncludeSignature": true
    }
  }
}
```

**throttle** - Rate limit suspicious traffic (50-70% confidence):

```json
{
  "throttle": {
    "Description": "Rate limit suspicious traffic (50-70% confidence)",
    "ThrottleAction": {
      "Enabled": true,
      "MaxRequests": 10,
      "WindowSeconds": 60
    },
    "LogAction": {
      "Enabled": true,
      "LogLevel": "Information",
      "IncludeSignature": false
    }
  }
}
```

**logonly** - Log but allow low-risk traffic (<30% confidence):

```json
{
  "logonly": {
    "Description": "Log but allow low-risk traffic (<30% confidence)",
    "AllowAction": {
      "Enabled": true
    },
    "LogAction": {
      "Enabled": true,
      "LogLevel": "Debug",
      "IncludeSignature": false
    }
  }
}
```

### YARP Clustering with Bot Detection

You can deploy multiple gateway instances behind a load balancer for high availability and horizontal scaling. Each
gateway instance shares the same SQLite signature database via network storage.

#### Architecture

```
                    ┌────────────────────┐
                    │  Load Balancer     │
                    │  (nginx/HAProxy)   │
                    └──────────┬─────────┘
                               │
              ┌────────────────┼────────────────┐
              │                │                │
        ┌─────▼─────┐    ┌────▼─────┐    ┌────▼─────┐
        │ Gateway 1 │    │Gateway 2 │    │Gateway 3 │
        │ (stylobot)  │    │(stylobot)  │    │(stylobot)  │
        └─────┬─────┘    └────┬─────┘    └────┬─────┘
              │               │               │
              └───────────────┼───────────────┘
                              │
                    ┌─────────▼──────────┐
                    │   Shared Storage   │
                    │  botdetection.db   │
                    │  (NFS/SMB/EFS)     │
                    └────────────────────┘
                              │
                    ┌─────────▼──────────┐
                    │   Backend App      │
                    │ (your application) │
                    └────────────────────┘
```

#### Setup with nginx

**1. Deploy gateway instances on multiple servers:**

```bash
# Server 1 (10.0.0.10)
./stylobot --mode production --upstream http://backend:8080 --port 5080

# Server 2 (10.0.0.11)
./stylobot --mode production --upstream http://backend:8080 --port 5080

# Server 3 (10.0.0.12)
./stylobot --mode production --upstream http://backend:8080 --port 5080
```

**2. Mount shared storage on all gateway servers:**

```bash
# Mount NFS share for signature database
sudo mount -t nfs nas.local:/botdetection /mnt/botdetection

# Or use SMB/CIFS
sudo mount -t cifs //nas.local/botdetection /mnt/botdetection

# Or AWS EFS
sudo mount -t efs fs-12345678:/ /mnt/botdetection
```

**3. Update gateway working directory to use shared storage:**

```bash
# Create symlink to shared database
cd /opt/stylobot
ln -s /mnt/botdetection/botdetection.db ./botdetection.db

# Or run gateway with shared working directory
cd /mnt/botdetection
/opt/stylobot/stylobot --mode production --upstream http://backend:8080 --port 5080
```

**4. Configure nginx load balancer:**

```nginx
upstream bot_gateways {
    # IP hash ensures same client goes to same gateway (sticky sessions)
    ip_hash;

    server 10.0.0.10:5080 weight=1 max_fails=3 fail_timeout=30s;
    server 10.0.0.11:5080 weight=1 max_fails=3 fail_timeout=30s;
    server 10.0.0.12:5080 weight=1 max_fails=3 fail_timeout=30s;
}

server {
    listen 80;
    server_name example.com;

    location / {
        proxy_pass http://bot_gateways;

        # Preserve client IP (important for bot detection!)
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header Host $host;

        # Timeouts
        proxy_connect_timeout 5s;
        proxy_send_timeout 60s;
        proxy_read_timeout 60s;
    }

    # Health check endpoint
    location /health {
        proxy_pass http://bot_gateways/health;
        access_log off;
    }
}
```

**5. Configure gateway to trust X-Forwarded-For headers:**

The gateway only trusts forwarded headers from explicitly configured proxies/networks.
Configure one of these in `appsettings.json` or environment variables:

```json
{
  "Network": {
    "KnownNetworks": "10.0.0.0/8,192.168.0.0/16",
    "KnownProxies": "127.0.0.1,::1"
  }
}
```

Environment variable equivalents:

```bash
export KNOWN_NETWORKS="10.0.0.0/8,192.168.0.0/16"
export KNOWN_PROXIES="127.0.0.1,::1"
```

`TrustAllForwardedProxies=true` is available for testing, but it is not safe on an internet-facing port because it
allows client IP spoofing via `X-Forwarded-For`.

## Observability

The CLI now exposes standard health and metrics endpoints:

- `/health` for liveness/basic config state
- `/metrics` for Prometheus scraping

OpenTelemetry is wired for:

- ASP.NET Core request instrumentation
- `Mostlylucid.BotDetection` metrics
- `Mostlylucid.BotDetection.Signals` metrics
- `Mostlylucid.BotDetection` tracing

#### Setup with HAProxy

```haproxy
global
    log /dev/log local0
    maxconn 4096

defaults
    log     global
    mode    http
    option  httplog
    timeout connect 5000ms
    timeout client  50000ms
    timeout server  50000ms

frontend http_front
    bind *:80
    default_backend bot_gateways

backend bot_gateways
    balance roundrobin
    option httpchk GET /health
    http-check expect status 200

    # Preserve client IP
    option forwardfor header X-Forwarded-For

    server gateway1 10.0.0.10:5080 check inter 2000ms rise 2 fall 3
    server gateway2 10.0.0.11:5080 check inter 2000ms rise 2 fall 3
    server gateway3 10.0.0.12:5080 check inter 2000ms rise 2 fall 3
```

#### Kubernetes Deployment

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: bot-gateway
spec:
  replicas: 3
  selector:
    matchLabels:
      app: bot-gateway
  template:
    metadata:
      labels:
        app: bot-gateway
    spec:
      containers:
      - name: stylobot
        image: your-registry/stylobot:latest
        ports:
        - containerPort: 5080
        env:
        - name: MODE
          value: "production"
        - name: UPSTREAM
          value: "http://backend-service:8080"
        - name: PORT
          value: "5080"
        volumeMounts:
        - name: botdetection-db
          mountPath: /app/data
        livenessProbe:
          httpGet:
            path: /health
            port: 5080
          initialDelaySeconds: 5
          periodSeconds: 10
        readinessProbe:
          httpGet:
            path: /health
            port: 5080
          initialDelaySeconds: 3
          periodSeconds: 5
      volumes:
      - name: botdetection-db
        persistentVolumeClaim:
          claimName: botdetection-pvc
---
apiVersion: v1
kind: Service
metadata:
  name: bot-gateway-service
spec:
  type: LoadBalancer
  selector:
    app: bot-gateway
  ports:
  - protocol: TCP
    port: 80
    targetPort: 5080
---
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: botdetection-pvc
spec:
  accessModes:
  - ReadWriteMany  # Important: all pods need read/write access
  resources:
    requests:
      storage: 5Gi
  storageClassName: efs-sc  # Or your NFS/EFS storage class
```

#### Clustering Notes

**⚠️ IMPORTANT**: See [SQLite on Shared Storage](#sqlite-on-shared-storage) warnings before deploying to production.

**Shared Database:**

- SQLite database (`botdetection.db`) MUST be on shared storage (NFS/SMB/EFS)
- All gateway instances read/write to the same database
- Ephemeral coordinator batches writes to avoid file locks (500ms batching)
- Database includes learned signatures and pattern reputations
- **Recommended only for small deployments (2-3 gateways)**

**Sticky Sessions:**

- Use `ip_hash` (nginx) or `source` (HAProxy) for client affinity
- Ensures same client goes to same gateway instance
- Improves cache hit rate for response tracking

**Health Checks:**

- All load balancers should check `/health` endpoint
- Gateway returns `{"status":"healthy","mode":"production","upstream":"http://backend:8080"}`
- Unhealthy instances automatically removed from rotation

**Signature Learning:**

- Learning is distributed across instances
- Each instance writes signatures to shared database
- All instances benefit from collective learning
- Signatures propagate within ~30 seconds (cache flush interval)

**Performance:**

- Horizontal scaling: Add more gateway instances for higher throughput
- Each gateway handles ~1000-2000 req/s (depends on detector configuration)
- Shared database is not a bottleneck (batched writes + in-memory cache)

**High Availability:**

- Deploy minimum 3 gateway instances for redundancy
- Load balancer automatically fails over to healthy instances
- No single point of failure (except shared storage)

## Testing

Use the included `test.http` file with Visual Studio Code REST Client extension or similar tools.

```bash
# Install VS Code REST Client extension
code --install-extension humao.rest-client

# Open test.http and click "Send Request" links
code test.http
```

Test cases included:

- Normal human requests (Chrome, Firefox)
- Bot requests (curl, Python requests, Selenium)
- Legitimate bots (Googlebot)
- Security scanners (Acunetix)
- Edge cases (missing User-Agent, old browsers)
- Load testing

## Building

### Prerequisites

- .NET 10 SDK (or later)
- Native AOT compiler toolchain:
    - **Windows**: Visual Studio 2022 (17.8+) with "Desktop development with C++" workload
    - **Linux**: `build-essential`, `zlib1g-dev`, `clang` (for ARM64: `gcc-aarch64-linux-gnu`)
    - **macOS**: Xcode command-line tools

### Build for Current Platform

```bash
dotnet publish -c Release
```

Output: `bin/Release/net10.0/{platform}/publish/stylobot` or `stylobot.exe`

### Cross-Platform Builds

```bash
# Linux x64
dotnet publish -c Release -r linux-x64

# Linux ARM64 (Raspberry Pi 4/5)
dotnet publish -c Release -r linux-arm64

# Windows x64
dotnet publish -c Release -r win-x64

# Windows ARM64 (Surface Pro X, Windows on ARM)
dotnet publish -c Release -r win-arm64

# macOS x64 (Intel)
dotnet publish -c Release -r osx-x64

# macOS ARM64 (Apple Silicon M1/M2/M3)
dotnet publish -c Release -r osx-arm64
```

### Size Optimization

The project is already configured for maximum size optimization:

- Native AOT compilation (`PublishAot=true`)
- Full trimming (`TrimMode=full`)
- Symbol stripping (`StripSymbols=true`)
- Request delegate generation (source generators)
- Configuration binding generation (source generators)
- Minimal APIs (no MVC/Razor overhead)

Typical sizes with .NET 10:

- Windows x64: ~10MB (includes `e_sqlite3.dll`)
- Windows ARM64: ~10MB (includes `e_sqlite3.dll`)
- Linux x64: ~12MB
- Linux ARM64: ~11MB (Raspberry Pi 4/5)
- macOS ARM64: ~11MB (Apple Silicon)
- macOS x64: ~13MB (Intel)

## Deployment

### Standalone Executable

**⚠️ IMPORTANT**: Always deploy the executable alongside its native library:

- **Windows**: `stylobot.exe` + `e_sqlite3.dll`
- **Linux**: `stylobot` + `libe_sqlite3.so`
- **macOS**: `stylobot` + `libe_sqlite3.dylib`

```bash
# Copy executable AND native library to target system
scp bin/Release/net10.0/linux-x64/publish/stylobot user@server:/usr/local/bin/
scp bin/Release/net10.0/linux-x64/publish/libe_sqlite3.so user@server:/usr/local/bin/

# Copy configuration
scp appsettings*.json user@server:/etc/stylobot/

# Run
ssh user@server
cd /etc/stylobot
/usr/local/bin/stylobot --mode production --upstream http://backend:8080 --port 80
```

### Docker

```dockerfile
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine

# Copy executable and native dependencies
COPY bin/Release/net10.0/linux-x64/publish/stylobot /app/stylobot
COPY bin/Release/net10.0/linux-x64/publish/*.so /app/
COPY appsettings*.json /app/

WORKDIR /app

ENTRYPOINT ["./stylobot"]
```

```bash
docker build -t stylobot:latest .
docker run -p 5080:5080 -e UPSTREAM=http://backend:8080 -e MODE=production stylobot:latest
```

### Systemd Service (Linux)

```ini
[Unit]
Description=StyloBot Gateway
After=network.target

[Service]
Type=simple
User=gateway
WorkingDirectory=/etc/stylobot
ExecStart=/usr/local/bin/stylobot --mode production --upstream http://backend:8080 --port 80
Restart=on-failure
RestartSec=5
Environment="DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false"

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl enable stylobot
sudo systemctl start stylobot
sudo systemctl status stylobot
```

## Performance

Typical latency (measured on a Raspberry Pi 5):

- Fast-path only policy: ~1-3ms
- Demo mode (full detector visibility): higher than fast-path-only and workload-dependent
- Fast + slow path: ~5-15ms
- With AI escalation: ~20-50ms

Memory usage:

- Idle: ~30MB
- Under load (1000 req/s): ~80MB

## Raspberry Pi Notes

Tested on:

- Raspberry Pi 4 (1GB, 2GB, 4GB, 8GB)
- Raspberry Pi 5 (4GB, 8GB)

**Not supported**:

- Raspberry Pi 3 and older (ARM v7)
- Raspberry Pi Zero

Build for Pi:

```bash
dotnet publish -c Release -r linux-arm64
```

Deploy:

```bash
# Copy executable and SQLite native library
scp bin/Release/net10.0/linux-arm64/publish/stylobot pi@raspberrypi.local:~/
scp bin/Release/net10.0/linux-arm64/publish/libe_sqlite3.so pi@raspberrypi.local:~/
scp appsettings*.json pi@raspberrypi.local:~/

ssh pi@raspberrypi.local
chmod +x stylobot
./stylobot --upstream http://localhost:8080
```

## Demo Loop: YARP → Backend with Detection Display

This demonstrates the complete flow from gateway through to backend with visual detection results.

### Setup

1. **Start Backend** (Demo app with YarpProxyDemo page):

```bash
cd ../Mostlylucid.BotDetection.Demo
dotnet run
# Runs on http://localhost:5080
```

2. **Start Gateway**:

```bash
# From Mostlylucid.BotDetection.Console directory
dotnet run -- --upstream http://localhost:5080 --port 5100 --mode demo
# Gateway runs on http://localhost:5100
```

3. **Access Demo Page**:

```bash
# Via gateway (bot detection runs)
open http://localhost:5100/YarpProxyDemo

# Or directly (fallback to inline middleware)
open http://localhost:5080/YarpProxyDemo
```

### What You'll See

The `/YarpProxyDemo` page displays:

- **Bot Detection Status** (Bot/Human with icon)
- **Detection Reasons** in plain English bullets
- **Detector Contributions** with visual contribution bars
- **YARP Routing Info** (when accessed via gateway)
- **Request Metadata** (ID, timestamp, processing time)
- **Architecture Explanation** (how the headers work)

### How It Works

```
┌──────┐     ┌─────────────────┐     ┌──────────────────┐
│Client│────▶│stylobot (Gateway) │────▶│Demo App (Backend)│
└──────┘     │ Bot Detection   │     │ Display Results  │
             │ + Headers       │     └──────────────────┘
             └─────────────────┘
```

1. **Gateway** runs bot detection on request
2. **Headers** serialized to `X-Bot-Detection-*` headers:
    - `X-Bot-Detection-Result`: `true`/`false`
    - `X-Bot-Detection-Probability`: `0.85`
    - `X-Bot-Detection-Reasons`: `["Headless browser","Datacenter IP"]`
    - `X-Bot-Detection-Contributions`: JSON array of detector data
3. **Backend** reads headers via `BotDetectionDetailsViewComponent`
4. **Page** displays results in nice CSS with `<details>` tags

### Testing Different Bots

Try different user agents via the gateway:

**Googlebot:**

```bash
curl -A "Mozilla/5.0 (compatible; Googlebot/2.1)" http://localhost:5100/YarpProxyDemo
```

**Scraper:**

```bash
curl -A "Scrapy/2.5.0" http://localhost:5100/YarpProxyDemo
```

**Headless Chrome:**

```bash
curl -A "HeadlessChrome/120.0.0.0" http://localhost:5100/YarpProxyDemo
```

**Human (Browser):**

```bash
curl -A "Mozilla/5.0 (Windows NT 10.0) Chrome/120.0.0.0" \
     -H "Accept: text/html" \
     -H "Accept-Language: en-US" \
     http://localhost:5100/YarpProxyDemo
```

Watch the gateway console for colorful detection logs, then view the page HTML to see formatted results!

### Backend Integration

To add detection display to your own backend app:

1. **Install UI package:**

```bash
dotnet add package Mostlylucid.BotDetection.UI
```

2. **Add to _ViewImports.cshtml:**

```cshtml
@addTagHelper *, Mostlylucid.BotDetection.UI
```

3. **Use in your page:**

```cshtml
<link rel="stylesheet" href="~/_content/Mostlylucid.BotDetection.UI/bot-detection-details.css" />

<details open>
    <summary>Bot Detection</summary>
    <bot-detection-details />
</details>
```

The ViewComponent automatically handles both modes:

- **YARP Mode**: Reads `X-Bot-Detection-*` headers
- **Inline Mode**: Reads `HttpContext.Items["BotDetection.Evidence"]`

## Troubleshooting

### "Unable to load DLL 'e_sqlite3'"

The SQLite native library is missing. Make sure to copy it alongside the executable:

- **Windows**: `e_sqlite3.dll`
- **Linux**: `libe_sqlite3.so`
- **macOS**: `libe_sqlite3.dylib`

These files are in the publish output directory and must be deployed together with the `stylobot` executable.

### macOS Gatekeeper Block

On macOS, unsigned binaries downloaded from the internet are quarantined by Gatekeeper. You'll see a message like
_"stylobot" cannot be opened because it is from an unidentified developer._

**Fix:**

```bash
# Remove quarantine attribute from downloaded binary
xattr -d com.apple.quarantine stylobot

# Make executable
chmod +x stylobot
```

> **Note**: The console gateway is distributed as a bare native binary, not a signed `.app` bundle. Code signing and
> notarization are not currently applied. This is standard for server-side CLI tools distributed via GitHub Releases.

### "Permission denied" on Linux

```bash
chmod +x stylobot
```

### "Cannot execute binary file"

Wrong architecture. Rebuild for target platform:

```bash
dotnet publish -c Release -r linux-arm64  # For Pi
dotnet publish -c Release -r linux-x64    # For x86_64 servers
```

### High memory usage

If running on low-memory systems (1GB Pi 4):

1. Use demo mode (disables learning and slow-path)
2. Reduce detector count in appsettings.json
3. Increase `EarlyExitThreshold` to 0.5

### Slow startup

With Native AOT, startup is instant (~50-100ms). If you experience slow startup, the executable may not be AOT-compiled.

## License

Same as parent project (Mostlylucid.BotDetection).
