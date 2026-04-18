using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Mostlylucid.BotDetection.Console.Helpers;
using Mostlylucid.BotDetection.Console.Logging;
using Mostlylucid.BotDetection.Console.Models;
using Mostlylucid.BotDetection.Console.Services;
using Mostlylucid.BotDetection.Console.Transforms;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Middleware;
using Serilog;
using Serilog.Events;
using SQLitePCL;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

// Initialize SQLite bundle BEFORE anything else
Batteries.Init();

// Parse command-line arguments
var cmdArgs = Environment.GetCommandLineArgs();

// Show help if no args or --help
if (cmdArgs.Length <= 1 || cmdArgs.Contains("--help") || cmdArgs.Contains("-h"))
{
    Console.WriteLine();
    Console.WriteLine("  stylobot · self-hosted bot defense");
    Console.WriteLine("  https://stylobot.net");
    Console.WriteLine();
    Console.WriteLine("  Usage:");
    Console.WriteLine("    stylobot <port> <upstream>                  Proxy to upstream on port");
    Console.WriteLine("    stylobot <port> <upstream> --mode production     Enable blocking");
    Console.WriteLine();
    Console.WriteLine("  Options:");
    Console.WriteLine("    --mode <demo|production>    Detection mode (default: demo)");
    Console.WriteLine("    --cert <path>               TLS certificate (.pfx or .pem)");
    Console.WriteLine("    --key <path>                TLS private key (required with .pem cert)");
    Console.WriteLine("    --cert-password <pass>      PFX certificate password");
    Console.WriteLine("    --tunnel [token]            Cloudflare Tunnel (quick or named)");
    Console.WriteLine("    --config <path>             Path to appsettings.json override");
    Console.WriteLine("    --log-level <level>         Minimum log level (default: Debug)");
    Console.WriteLine("    -h, --help                  Show this help");
    Console.WriteLine();
    Console.WriteLine("  Examples:");
    Console.WriteLine("    stylobot 5080 http://localhost:3000");
    Console.WriteLine("    stylobot 8000 http://192.168.0.6:2040 --mode production");
    Console.WriteLine("    stylobot 443 https://api.example.com --cert cert.pfx");
    Console.WriteLine("    stylobot 5080 http://localhost:3000 --tunnel");
    Console.WriteLine("    stylobot 5080 http://localhost:3000 --tunnel eyJhIjoiNjQ2...");
    Console.WriteLine();
    Console.WriteLine("  Dashboard:  http://localhost:<port>/_stylobot");
    Console.WriteLine("  Health:     http://localhost:<port>/health");
    Console.WriteLine();
    Console.WriteLine("  Docs:       https://github.com/scottgal/stylobot");
    Console.WriteLine("  Commercial: https://stylobot.net/pricing");
    Console.WriteLine();
    return 0;
}

// Parse: stylobot <port> <upstream> [--mode demo|production]
// Collect positional args, skipping values that belong to known flags
var flagsWithValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "--mode", "--cert", "--key", "--cert-password", "--config", "--log-level" };
var positionals = new List<string>();
for (var i = 1; i < cmdArgs.Length; i++)
{
    if (cmdArgs[i].StartsWith("-"))
    {
        if (flagsWithValues.Contains(cmdArgs[i]) && i + 1 < cmdArgs.Length)
            i++; // skip the flag's value
        else if (cmdArgs[i].Equals("--tunnel", StringComparison.OrdinalIgnoreCase)
                 && i + 1 < cmdArgs.Length && !cmdArgs[i + 1].StartsWith("-")
                 && !Uri.TryCreate(cmdArgs[i + 1], UriKind.Absolute, out _)
                 && !int.TryParse(cmdArgs[i + 1], out _))
            i++; // skip tunnel token (not a URL or port)
    }
    else
    {
        positionals.Add(cmdArgs[i]);
    }
}
string port, upstream;
if (positionals.Count >= 2)
{
    port = positionals[0];
    upstream = positionals[1];
}
else if (positionals.Count == 1)
{
    // Single arg: if it looks like a number, it's the port; otherwise it's the upstream
    if (int.TryParse(positionals[0], out _))
    {
        port = positionals[0];
        upstream = Environment.GetEnvironmentVariable("DEFAULT_UPSTREAM") ?? "http://localhost:8080";
    }
    else
    {
        upstream = positionals[0];
        port = Environment.GetEnvironmentVariable("PORT") ?? "5080";
    }
}
else
{
    port = Environment.GetEnvironmentVariable("PORT") ?? "5080";
    upstream = Environment.GetEnvironmentVariable("DEFAULT_UPSTREAM") ?? "http://localhost:8080";
}
var mode = GetArg(cmdArgs, "--mode") ?? Environment.GetEnvironmentVariable("MODE") ?? "demo";
var certPath = GetArg(cmdArgs, "--cert");
var keyPath = GetArg(cmdArgs, "--key");
var certPassword = GetArg(cmdArgs, "--cert-password");
var configPath = GetArg(cmdArgs, "--config");
var logLevel = GetArg(cmdArgs, "--log-level");
var useTls = certPath != null;

// Cloudflare tunnel: --tunnel (quick) or --tunnel <token> (named)
string? tunnelToken = null;
var tunnelEnabled = false;
var tunnelArgIndex = Array.FindIndex(cmdArgs, a => a.Equals("--tunnel", StringComparison.OrdinalIgnoreCase));
if (tunnelArgIndex >= 0)
{
    tunnelEnabled = true;
    // Next arg is the token if it doesn't start with --
    if (tunnelArgIndex + 1 < cmdArgs.Length && !cmdArgs[tunnelArgIndex + 1].StartsWith("-"))
        tunnelToken = cmdArgs[tunnelArgIndex + 1];
}

// Validate TLS cert exists
if (certPath != null && !File.Exists(certPath))
{
    Console.Error.WriteLine($"  Certificate file not found: {certPath}");
    return 1;
}
if (keyPath != null && !File.Exists(keyPath))
{
    Console.Error.WriteLine($"  Private key file not found: {keyPath}");
    return 1;
}

// Parse log level override
var minLogLevel = LogEventLevel.Debug;
if (logLevel != null && Enum.TryParse<LogEventLevel>(logLevel, true, out var parsed))
    minLogLevel = parsed;

// Configure Serilog (console + file logging for errors/warnings only)
// File logging can be configured via appsettings.json Serilog section
var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
Directory.CreateDirectory(logsDir);

// Build initial configuration from code (will be enriched by appsettings.json)
var logConfig = new LoggerConfiguration()
    .MinimumLevel.Is(minLogLevel)
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Yarp", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Mode", mode)
    .Enrich.WithProperty("Port", port)
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}",
        restrictedToMinimumLevel: LogEventLevel.Debug)
    .WriteTo.File(
        Path.Combine(logsDir, "errors-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate:
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
        restrictedToMinimumLevel: LogEventLevel.Warning,
        flushToDiskInterval: TimeSpan.FromSeconds(1));

// Read configuration from appsettings.json if available
var configBuilder = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", true, false);
if (configPath != null)
    configBuilder.AddJsonFile(Path.GetFullPath(configPath), optional: false, reloadOnChange: false);

var tempConfig = configBuilder.Build();
if (tempConfig.GetSection("Serilog").Exists())
{
    try
    {
        // Explicit assembly reference for single-file/AOT compatibility
        var readerOptions = new Serilog.Settings.Configuration.ConfigurationReaderOptions(
            typeof(Serilog.ConsoleLoggerConfigurationExtensions).Assembly,
            typeof(Serilog.FileLoggerConfigurationExtensions).Assembly);
        logConfig = logConfig.ReadFrom.Configuration(tempConfig, readerOptions);
    }
    catch (InvalidOperationException)
    {
        // Single-file publish: Serilog assembly scanning fails, use code-based config only
    }
}

Log.Logger = logConfig.CreateLogger();

Log.Information("Logging initialized. Logs directory: {LogsDir}", logsDir);
Log.Information("  - File logging: Warning+ only (configure via appsettings.json Serilog section)");

// Add global unhandled exception handlers to catch silent failures
AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
{
    var exception = e.ExceptionObject as Exception;
    Log.Fatal(exception, "UNHANDLED EXCEPTION in AppDomain - IsTerminating: {IsTerminating}", e.IsTerminating);
    Log.CloseAndFlush();
};

TaskScheduler.UnobservedTaskException += (sender, e) =>
{
    Log.Fatal(e.Exception,
        "UNOBSERVED TASK EXCEPTION - TERMINATING PROCESS (this should never happen - indicates a critical bug)");
    // DO NOT call e.SetObserved() - let the process crash
    // The service manager (systemd/Windows Service) will restart it
    // This forces investigation and prevents zombie state where app appears healthy but is broken
};

try
{
    Log.Information("");
    Log.Information("  ┌─────────────────────────────────────────┐");
    Log.Information("  │  stylobot  ·  self-hosted bot defense   │");
    Log.Information("  │  https://stylobot.net                   │");
    Log.Information("  └─────────────────────────────────────────┘");
    Log.Information("");
    Log.Information("  Mode:     {Mode}", mode.ToUpper());
    Log.Information("  Upstream: {Upstream}", upstream);
    Log.Information("  Port:     {Port}", port);
    if (useTls) Log.Information("  TLS:      {CertPath}", certPath);
    if (tunnelEnabled) Log.Information("  Tunnel:   Cloudflare {TunnelType}", tunnelToken != null ? "(named)" : "(quick)");
    Log.Information("  Docs:     https://github.com/scottgal/stylobot");
    Log.Information("");

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args
    });

    // Use Serilog
    builder.Host.UseSerilog();

    // Enable service hosting (Windows SCM + Linux systemd)
    builder.Host.UseWindowsService();
    builder.Host.UseSystemd();

    // Configure forwarded headers to extract real client IP from Cloudflare/proxies
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        // Trust all proxies (Cloudflare, reverse proxies, etc.)
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        // Limit to first proxy for security
        options.ForwardLimit = 1;
    });

    // Load configuration from appsettings.json (with mode override + CLI config)
    builder.Configuration.AddJsonFile("appsettings.json", false, true);
    builder.Configuration.AddJsonFile($"appsettings.{mode}.json", true, true);
    if (configPath != null)
        builder.Configuration.AddJsonFile(Path.GetFullPath(configPath), optional: false, reloadOnChange: true);

    // Read signature logging configuration early (needed by YARP transforms)
    // DEMO MODE: Enable PII logging by default for debugging (can be disabled in appsettings.json)
    // PRODUCTION MODE: PII logging disabled by default (zero-PII)
    var defaultLogPii = mode.Equals("demo", StringComparison.OrdinalIgnoreCase);

    var sigLoggingConfig = new SignatureLoggingConfig
    {
        Enabled = builder.Configuration.GetValue("SignatureLogging:Enabled", true),
        MinConfidence = builder.Configuration.GetValue("SignatureLogging:MinConfidence", 0.7),
        PrettyPrintJsonLd = builder.Configuration.GetValue("SignatureLogging:PrettyPrintJsonLd", false),
        SignatureHashKey = builder.Configuration.GetValue<string>("SignatureLogging:SignatureHashKey") ??
                           "DEFAULT_INSECURE_KEY_CHANGE_ME",
        LogRawPii = builder.Configuration.GetValue("SignatureLogging:LogRawPii",
            defaultLogPii) // Demo: true, Production: false
    };

    // Validate HMAC key (fail-fast on default key in production)
    ConfigValidator.ValidateHmacKey(sigLoggingConfig, mode);

    // Create signature logger with async background queue
    var signatureLogger = new SignatureLogger();
    builder.Services.AddSingleton(signatureLogger);

    // Create YARP transforms
    var requestTransform = new BotDetectionRequestTransform(mode, sigLoggingConfig, signatureLogger);
    var responseTransform = new BotDetectionResponseTransform(mode);

    // Add YARP
    var yarpBuilder = builder.Services.AddReverseProxy()
        .LoadFromMemory(
            new[]
            {
                new RouteConfig
                {
                    RouteId = "catch-all",
                    Match = new RouteMatch
                    {
                        Path = "{**catch-all}"
                    },
                    ClusterId = "upstream"
                }
            },
            new[]
            {
                new ClusterConfig
                {
                    ClusterId = "upstream",
                    Destinations = new Dictionary<string, DestinationConfig>
                    {
                        ["default"] = new() { Address = upstream }
                    }
                }
            });

    // Add Bot Detection (configured via appsettings.json)
    builder.Services.AddBotDetection();

    // Add heartbeat service to detect silent failures (logs every 5 minutes)
    builder.Services.AddHostedService<HeartbeatService>();

    // Add YARP transforms for bot detection headers and CSP fixes
    yarpBuilder.AddTransforms(builderContext =>
    {
        builderContext.AddRequestTransform(async transformContext =>
            await requestTransform.TransformAsync(transformContext));

        builderContext.AddResponseTransform(async transformContext =>
            await responseTransform.TransformAsync(transformContext));
    });

    // Configure Kestrel for TLS if certificate provided (must be before Build)
    if (useTls)
    {
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenAnyIP(int.Parse(port), listenOptions =>
            {
                if (certPath!.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase))
                {
                    listenOptions.UseHttps(certPath, certPassword);
                }
                else
                {
                    // PEM cert + key
                    if (keyPath == null)
                    {
                        Log.Fatal("--key is required when using a .pem certificate");
                        throw new InvalidOperationException("--key is required when using a .pem certificate");
                    }
                    listenOptions.UseHttps(httpsOptions =>
                    {
                        httpsOptions.ServerCertificateSelector = (_, _) =>
                            System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(certPath, keyPath);
                    });
                }
            });
        });
    }

    var app = builder.Build();

    // Load signatures from JSON-L files on startup
    await SignatureLoaderService.LoadSignaturesFromJsonL(app.Services, Log.Logger);

    // Use Forwarded Headers middleware FIRST to extract real client IP
    app.UseForwardedHeaders();

    // Use Bot Detection middleware
    app.UseBotDetection();

    // Health check endpoint (AOT-compatible) - mapped BEFORE YARP to avoid being proxied
    app.MapGet("/health",
        () => Results.Text(
            $"{{\"status\":\"healthy\",\"mode\":\"{mode}\",\"upstream\":\"{upstream}\",\"port\":\"{port}\"}}",
            "application/json"));

    // Serve embedded test page - mapped BEFORE YARP to avoid being proxied
    app.MapGet("/test-client-side.html", async (HttpContext context) =>
    {
        var assembly = typeof(Program).Assembly;
        var resourceName = "wwwroot.test-client-side.html";

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return Results.NotFound($"Embedded resource '{resourceName}' not found");

        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();
        return Results.Content(content, "text/html");
    });

    // Learning endpoint - Active in demo and learning modes (demo is default)
    // MUST be mapped BEFORE YARP to avoid being proxied
    if (mode.Equals("demo", StringComparison.OrdinalIgnoreCase) ||
        mode.Equals("learning", StringComparison.OrdinalIgnoreCase))
    {
        Log.Information("Signature learning endpoint enabled - /stylobot-learning/ active (mode: {Mode})", mode);

        // Supports status code simulation via path markers: /404/, /403/, /500/, etc.
        // Example: /stylobot-learning/404/admin.php -> returns 404
        // Example: /stylobot-learning/products -> returns 200
        app.MapMethods("/stylobot-learning/{**path}", new[] { "GET", "POST", "HEAD", "PUT", "DELETE", "PATCH" },
            (HttpContext context) =>
            {
                // Use actual request path instead of route values to avoid double prefix
                var requestPath = context.Request.Path.Value ?? "/";
                // Remove /stylobot-learning prefix and normalize
                var path = requestPath.StartsWith("/stylobot-learning/", StringComparison.OrdinalIgnoreCase)
                    ? requestPath.Substring("/stylobot-learning/".Length).Trim('/')
                    : requestPath.StartsWith("/stylobot-learning", StringComparison.OrdinalIgnoreCase)
                        ? requestPath.Substring("/stylobot-learning".Length).Trim('/')
                        : "";

                var method = context.Request.Method;
                var userAgent = context.Request.Headers.UserAgent.ToString();

                // Determine status code from path markers
                var statusCode = 200;
                var statusReason = "OK";

                if (path.Contains("/404/") || path.EndsWith(".php") || path.Contains("admin") || path.Contains("wp-"))
                {
                    statusCode = 404;
                    statusReason = "Not Found";
                }
                else if (path.Contains("/403/") || path.Contains("forbidden"))
                {
                    statusCode = 403;
                    statusReason = "Forbidden";
                }
                else if (path.Contains("/500/") || path.Contains("error"))
                {
                    statusCode = 500;
                    statusReason = "Internal Server Error";
                }

                // Build normalized URL path (avoid double slashes)
                var urlPath = string.IsNullOrEmpty(path) ? "/stylobot-learning" : $"/stylobot-learning/{path}";

                Log.Information(
                    "[LEARNING-MODE] Request handled internally: {Method} {UrlPath} UA={UserAgent} -> {StatusCode}",
                    method, urlPath, userAgent.Length > 50 ? userAgent.Substring(0, 47) + "..." : userAgent,
                    statusCode);

                // Return appropriate response based on status code
                var responseJson = statusCode == 404
                    ? $$"""
                        {
                          "@context": "https://schema.org",
                          "@type": "WebPage",
                          "name": "404 Not Found",
                          "description": "The requested resource was not found.",
                          "url": "{{urlPath}}",
                          "metadata": {
                            "statusCode": 404,
                            "statusText": "Not Found",
                            "learningMode": true
                          }
                        }
                        """
                    : $$"""
                        {
                          "@context": "https://schema.org",
                          "@type": "WebPage",
                          "name": "Stylobot Learning Mode",
                          "url": "{{urlPath}}",
                          "description": "This is a synthetic response for bot detection training. No real website was contacted.",
                          "provider": {
                            "@type": "Organization",
                            "name": "Stylobot Bot Detection",
                            "url": "https://stylobot.net"
                          },
                          "mainEntity": {
                            "@type": "Dataset",
                            "name": "Training Data",
                            "description": "Request processed for bot detection learning",
                            "temporalCoverage": "{{DateTime.UtcNow:O}}",
                            "distribution": {
                              "@type": "DataDownload",
                              "contentUrl": "{{urlPath}}",
                              "encodingFormat": "application/json"
                            }
                          },
                          "metadata": {
                            "requestMethod": "{{method}}",
                            "requestPath": "{{urlPath}}",
                            "statusCode": {{statusCode}},
                            "statusText": "{{statusReason}}",
                            "detectionApplied": true,
                            "learningMode": true
                          }
                        }
                        """;

                context.Response.StatusCode = statusCode;
                return Results.Content(responseJson, "application/json");
            });
    }

    // Client-side detection callback endpoint (AOT-compatible)
    app.MapPost("/api/bot-detection/client-result", async (HttpContext context, ILearningEventBus? eventBus) =>
    {
        try
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();

            Log.Information("[CLIENT-SIDE-CALLBACK] Received client-side detection result");

            // Parse JSON (AOT-compatible using JsonDocument)
            using var jsonDoc = JsonDocument.Parse(body);
            var root = jsonDoc.RootElement;

            // Extract server detection results (echoed back from client)
            var serverDetection = root.TryGetProperty("serverDetection", out var serverDet)
                ? serverDet
                : (JsonElement?)null;
            var serverIsBot = serverDetection?.TryGetProperty("isBot", out var isBotProp) == true &&
                              isBotProp.GetString() == "True";
            var serverProbability = serverDetection?.TryGetProperty("probability", out var probProp) == true
                ? double.Parse(probProp.GetString() ?? "0")
                : 0.0;

            // Extract client-side checks
            var clientChecks = root.TryGetProperty("clientChecks", out var checks) ? checks : (JsonElement?)null;
            if (clientChecks.HasValue)
            {
                var hasCanvas = clientChecks.Value.TryGetProperty("hasCanvas", out var canvas) && canvas.GetBoolean();
                var hasWebGL = clientChecks.Value.TryGetProperty("hasWebGL", out var webgl) && webgl.GetBoolean();
                var hasAudioContext = clientChecks.Value.TryGetProperty("hasAudioContext", out var audio) &&
                                      audio.GetBoolean();
                var pluginCount = clientChecks.Value.TryGetProperty("pluginCount", out var plugins)
                    ? plugins.GetInt32()
                    : 0;
                var hardwareConcurrency = clientChecks.Value.TryGetProperty("hardwareConcurrency", out var hardware)
                    ? hardware.GetInt32()
                    : 0;

                // Calculate client-side "bot score" based on checks
                var clientBotScore = CalculateClientBotScore(hasCanvas, hasWebGL, hasAudioContext, pluginCount,
                    hardwareConcurrency);

                Log.Information(
                    "[CLIENT-SIDE-VALIDATION] Server: IsBot={ServerIsBot} (prob={ServerProb:F2}), Client: Score={ClientScore:F2}",
                    serverIsBot, serverProbability, clientBotScore);

                // Detect mismatches (server says bot, but client looks human - or vice versa)
                var mismatch = (serverIsBot && clientBotScore < 0.3) || (!serverIsBot && clientBotScore > 0.7);
                if (mismatch)
                    Log.Warning(
                        "[CLIENT-SIDE-MISMATCH] Server detection ({ServerIsBot}) conflicts with client score ({ClientScore:F2})",
                        serverIsBot, clientBotScore);

                // Publish learning event for pattern improvement
                if (eventBus != null)
                {
                    var learningEvent = new LearningEvent
                    {
                        Type = LearningEventType.ClientSideValidation,
                        Source = "ClientSideCallback",
                        Timestamp = DateTimeOffset.UtcNow,
                        Label = serverIsBot, // Server's verdict
                        Confidence = clientBotScore, // Client-side bot score
                        Metadata = new Dictionary<string, object>
                        {
                            ["ipAddress"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                            ["userAgent"] = root.TryGetProperty("userAgent", out var ua) ? ua.GetString() ?? "" : "",
                            ["serverIsBot"] = serverIsBot,
                            ["serverProbability"] = serverProbability,
                            ["clientBotScore"] = clientBotScore,
                            ["hasCanvas"] = hasCanvas,
                            ["hasWebGL"] = hasWebGL,
                            ["hasAudioContext"] = hasAudioContext,
                            ["pluginCount"] = pluginCount,
                            ["hardwareConcurrency"] = hardwareConcurrency,
                            ["mismatch"] = mismatch
                        }
                    };

                    if (eventBus.TryPublish(learningEvent))
                        Log.Debug("[CLIENT-SIDE-CALLBACK] Published learning event for client-side validation");
                    else
                        Log.Warning("[CLIENT-SIDE-CALLBACK] Failed to publish learning event (channel full?)");
                }
            }

            return Results.Text("{\"status\":\"accepted\",\"message\":\"Client-side detection result processed\"}",
                "application/json");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to process client-side detection callback");
            return Results.Text("{\"status\":\"error\",\"message\":\"Invalid request\"}", "application/json",
                statusCode: 400);
        }
    });

    // Map YARP reverse proxy (catch-all, should be LAST)
    app.MapReverseProxy();

    // Set listen URL (when not using TLS; TLS uses ConfigureKestrel before Build)
    if (!useTls)
        app.Urls.Add($"http://*:{port}");

    var scheme = useTls ? "https" : "http";
    Log.Information("  ✓ Ready on {Scheme}://localhost:{Port}", scheme, port);
    Log.Information("  ✓ Upstream: {Upstream}", upstream);
    Log.Information("  ✓ Health:   {Scheme}://localhost:{Port}/health", scheme, port);
    Log.Information("");

    // Launch Cloudflare tunnel if requested
    Process? tunnelProcess = null;
    if (tunnelEnabled)
    {
        tunnelProcess = LaunchCloudflaredTunnel(port, scheme, tunnelToken);
    }

    Log.Information("  Press Ctrl+C to stop.");

    try
    {
        await app.RunAsync();
        Log.Warning("Application host stopped normally (this should only happen on shutdown)");
    }
    catch (OperationCanceledException)
    {
        Log.Information("Application shutdown requested (Ctrl+C or SIGTERM)");
    }
    catch (Exception innerEx)
    {
        Log.Fatal(innerEx, "Application host crashed with unhandled exception");
        throw;
    }
    finally
    {
        // Kill tunnel process if we started one
        if (tunnelProcess is { HasExited: false })
        {
            Log.Information("Stopping Cloudflare tunnel...");
            tunnelProcess.Kill(entireProcessTree: true);
            tunnelProcess.Dispose();
        }

        // Flush signature logger before shutdown
        await signatureLogger.FlushAndStopAsync();
    }
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application startup or configuration failed");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

return 0;

// Calculate client-side bot score based on browser fingerprinting checks
static double CalculateClientBotScore(bool hasCanvas, bool hasWebGL, bool hasAudioContext, int pluginCount,
    int hardwareConcurrency)
{
    var score = 0.0;

    // Headless browsers typically fail these checks
    if (!hasCanvas) score += 0.30; // Major red flag
    if (!hasWebGL) score += 0.25; // Very suspicious
    if (!hasAudioContext) score += 0.15; // Somewhat suspicious

    // Real browsers typically have 1-5 plugins (though modern browsers have few)
    if (pluginCount == 0) score += 0.10; // Suspicious but not definitive

    // Headless browsers often report 0 or suspiciously high values
    if (hardwareConcurrency == 0) score += 0.10;
    else if (hardwareConcurrency > 32) score += 0.05; // Unusual but possible

    // If all checks pass, give strong confidence it's a real browser
    if (hasCanvas && hasWebGL && hasAudioContext && hardwareConcurrency > 0 && hardwareConcurrency <= 32)
        score = Math.Max(0, score - 0.20); // Bonus for passing all checks

    return Math.Clamp(score, 0.0, 1.0);
}

// Helper to get command-line argument value
static string? GetArg(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];

    return null;
}

// Launch cloudflared tunnel subprocess
static Process? LaunchCloudflaredTunnel(string port, string scheme, string? token)
{
    // Check cloudflared is installed
    try
    {
        var check = Process.Start(new ProcessStartInfo
        {
            FileName = "cloudflared",
            Arguments = "version",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        check?.WaitForExit(5000);
        if (check is null || check.ExitCode != 0)
        {
            Log.Error("cloudflared not found. Install it: https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/");
            return null;
        }
    }
    catch
    {
        Log.Error("cloudflared not found. Install it: https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/");
        return null;
    }

    ProcessStartInfo psi;
    if (token != null)
    {
        // Named tunnel with pre-configured token
        Log.Information("Starting Cloudflare named tunnel...");
        psi = new ProcessStartInfo
        {
            FileName = "cloudflared",
            Arguments = $"tunnel run --token {token}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
    }
    else
    {
        // Quick tunnel (random *.trycloudflare.com URL)
        Log.Information("Starting Cloudflare quick tunnel...");
        psi = new ProcessStartInfo
        {
            FileName = "cloudflared",
            Arguments = $"tunnel --url {scheme}://localhost:{port}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
    }

    var process = Process.Start(psi);
    if (process == null)
    {
        Log.Error("Failed to start cloudflared process");
        return null;
    }

    // Log tunnel output in background (tunnel URL appears in stderr)
    _ = Task.Run(async () =>
    {
        while (!process.HasExited)
        {
            var line = await process.StandardError.ReadLineAsync();
            if (line != null)
            {
                // Look for the tunnel URL in output
                if (line.Contains(".trycloudflare.com") || line.Contains("Registered tunnel connection"))
                    Log.Information("  ✓ Tunnel: {TunnelInfo}", line.Trim());
                else if (line.Contains("ERR"))
                    Log.Warning("[cloudflared] {Line}", line.Trim());
                else
                    Log.Debug("[cloudflared] {Line}", line.Trim());
            }
        }
    });

    return process;
}