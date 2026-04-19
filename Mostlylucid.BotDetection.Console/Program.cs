using System.Diagnostics;
using System.Net;
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
using Mostlylucid.BotDetection.Llm.Cloud.Extensions;
using Mostlylucid.BotDetection.Llm.LlamaSharp.Extensions;
using Mostlylucid.BotDetection.Llm.Ollama.Extensions;
using Mostlylucid.BotDetection.Metrics;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Telemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using SQLitePCL;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

// Initialize SQLite bundle BEFORE anything else
Batteries.Init();

// Parse command-line arguments
var cmdArgs = Environment.GetCommandLineArgs();

// Route subcommands: stop, status, logs don't need the full server startup
var firstArg = cmdArgs.Length > 1 ? cmdArgs[1].ToLowerInvariant() : null;
switch (firstArg)
{
    case "stop":
        return DaemonCommands.Stop();
    case "status":
        return await DaemonCommands.Status();
    case "logs":
        return DaemonCommands.Logs();
    case "start":
        return DaemonCommands.Start(cmdArgs);
    case "man":
        ShowManPage();
        return 0;
}

// Show help if no args or --help
if (cmdArgs.Length <= 1 || cmdArgs.Contains("--help") || cmdArgs.Contains("-h"))
{
    Console.WriteLine();
    Console.WriteLine("  stylobot · self-hosted bot defense · free forever");
    Console.WriteLine("  https://stylobot.net");
    Console.WriteLine();
    Console.WriteLine("  Usage:");
    Console.WriteLine("    stylobot <port> <upstream>                  Proxy to upstream on port");
    Console.WriteLine("    stylobot --port <port> --upstream <url>    Standard named options");
    Console.WriteLine("    stylobot <port> <upstream> --mode production     Enable blocking");
    Console.WriteLine();
    Console.WriteLine("  Commands:");
    Console.WriteLine("    stylobot start <port> <upstream> [opts]     Start as background daemon");
    Console.WriteLine("    stylobot stop                               Stop the running daemon");
    Console.WriteLine("    stylobot status                             Check if daemon is running");
    Console.WriteLine("    stylobot logs                               Show recent log output");
    Console.WriteLine("    stylobot man                                Full reference manual");
    Console.WriteLine();
    Console.WriteLine("  Options:");
    Console.WriteLine("    --port <port>               Port to listen on (default: 5080)");
    Console.WriteLine("    --upstream <url>            Upstream server URL");
    Console.WriteLine("    --mode <demo|production>    Detection mode (default: demo)");
    Console.WriteLine("    --policy <name>             Default action policy (default: logonly)");
    Console.WriteLine("    --cert <path>               TLS certificate (.pfx or .pem)");
    Console.WriteLine("    --key <path>                TLS private key (required with .pem cert)");
    Console.WriteLine("    --cert-password <pass>      PFX certificate password");
    Console.WriteLine("    --tunnel [[token]]            Cloudflare Tunnel (requires cloudflared)");
    Console.WriteLine("    --threshold <0.0-1.0>       Bot probability threshold (default: 0.7)");
    Console.WriteLine("    --llm <provider>            LLM provider (openai, anthropic, gemini, groq,");
    Console.WriteLine("                                mistral, deepseek, ollama, or custom URL)");
    Console.WriteLine("    --llm-key <key>             API key (or env: STYLOBOT_LLM_KEY)");
    Console.WriteLine("    --llm-url <url>             Custom provider base URL");
    Console.WriteLine("    --model <name>              Model name (overrides provider default)");
    Console.WriteLine("    --config <path>             Path to appsettings.json override");
    Console.WriteLine("    --log-level <level>         Minimum log level (default: Warning)");
    Console.WriteLine("    --verbose                   Show all log output (disables live table)");
    Console.WriteLine("    -h, --help                  Show this help");
    Console.WriteLine();
    Console.WriteLine("  Examples:");
    Console.WriteLine("    stylobot 5080 http://localhost:3000");
    Console.WriteLine("    stylobot 8000 http://192.168.0.6:2040 --mode production");
    Console.WriteLine("    stylobot start 5080 http://localhost:3000 --policy block");
    Console.WriteLine("    stylobot 443 https://api.example.com --cert cert.pfx");
    Console.WriteLine("    stylobot 5080 http://localhost:3000 --tunnel");
    Console.WriteLine();
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
    { "--port", "--upstream", "--mode", "--policy", "--cert", "--key", "--cert-password", "--config", "--log-level", "--threshold", "--llm", "--llm-key", "--llm-url", "--model" };
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

var cliPort = GetArg(cmdArgs, "--port");
var cliUpstream = GetArg(cmdArgs, "--upstream");
var envPort = Environment.GetEnvironmentVariable("PORT");
var envUpstream = Environment.GetEnvironmentVariable("UPSTREAM")
                  ?? Environment.GetEnvironmentVariable("DEFAULT_UPSTREAM");

string? positionalPort = null;
string? positionalUpstream = null;
if (positionals.Count >= 2)
{
    positionalPort = positionals[0];
    positionalUpstream = positionals[1];
}
else if (positionals.Count == 1)
{
    // Single arg: if it looks like a number, it's the port; otherwise it's the upstream
    if (int.TryParse(positionals[0], out _))
        positionalPort = positionals[0];
    else
        positionalUpstream = positionals[0];
}

var port = cliPort ?? positionalPort ?? envPort ?? "5080";
var upstream = cliUpstream ?? positionalUpstream ?? envUpstream ?? "http://localhost:8080";
var mode = GetArg(cmdArgs, "--mode") ?? Environment.GetEnvironmentVariable("MODE") ?? "demo";
var actionPolicy = GetArg(cmdArgs, "--policy") ?? Environment.GetEnvironmentVariable("STYLOBOT_POLICY") ?? "logonly";
var certPath = GetArg(cmdArgs, "--cert");
var keyPath = GetArg(cmdArgs, "--key");
var certPassword = GetArg(cmdArgs, "--cert-password");
var configPath = GetArg(cmdArgs, "--config");
var logLevel = GetArg(cmdArgs, "--log-level");
var thresholdArg = GetArg(cmdArgs, "--threshold");
var llmProvider = GetArg(cmdArgs, "--llm");
var llmKey = GetArg(cmdArgs, "--llm-key") ?? Environment.GetEnvironmentVariable("STYLOBOT_LLM_KEY");
var llmUrl = GetArg(cmdArgs, "--llm-url");
var llmModel = GetArg(cmdArgs, "--model");
var useTls = certPath != null;
var verbose = cmdArgs.Contains("--verbose");
double? botThreshold = thresholdArg != null && double.TryParse(thresholdArg, out var t) ? t : null;

if (!int.TryParse(port, out var portNumber) || portNumber is < 1 or > 65535)
{
    Console.Error.WriteLine($"  Invalid port: {port}");
    return 1;
}

if (!Uri.TryCreate(upstream, UriKind.Absolute, out var upstreamUri) ||
    (upstreamUri.Scheme != Uri.UriSchemeHttp && upstreamUri.Scheme != Uri.UriSchemeHttps))
{
    Console.Error.WriteLine($"  Invalid upstream URL: {upstream}");
    return 1;
}

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

// Parse log level override (default: Warning when live table active, Debug when verbose)
var minLogLevel = verbose ? LogEventLevel.Debug : LogEventLevel.Warning;
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
    .MinimumLevel.Override("Yarp", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Mode", mode)
    .Enrich.WithProperty("Port", port);

// Only write to console in verbose mode; live table replaces console output otherwise
if (verbose)
{
    logConfig = logConfig.WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}",
        restrictedToMinimumLevel: LogEventLevel.Debug);
}

logConfig = logConfig.WriteTo.File(
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
    .AddJsonFile("appsettings.json", true, false)
    .AddJsonFile($"appsettings.{mode}.json", true, false)
    .AddEnvironmentVariables()
    .AddEnvironmentVariables("STYLOBOT_");
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
    if (verbose)
    {
        Log.Information("");
        Log.Information("  ┌─────────────────────────────────────────┐");
        Log.Information("  │  stylobot  ·  self-hosted bot defense   │");
        Log.Information("  │  https://stylobot.net                   │");
        Log.Information("  └─────────────────────────────────────────┘");
        Log.Information("");
        Log.Information("  Mode:     {Mode}", mode.ToUpper());
        Log.Information("  Policy:   {Policy}", actionPolicy);
        Log.Information("  Upstream: {Upstream}", upstream);
        Log.Information("  Port:     {Port}", port);
        if (useTls) Log.Information("  TLS:      {CertPath}", certPath);
        if (tunnelEnabled) Log.Information("  Tunnel:   Cloudflare {TunnelType}", tunnelToken != null ? "(named)" : "(quick)");
        Log.Information("  Docs:     https://github.com/scottgal/stylobot");
        Log.Information("");
    }

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
        options.ForwardLimit = 1;

        var trustAllProxies = builder.Configuration.GetValue("Network:TrustAllForwardedProxies", false) ||
                              bool.TryParse(Environment.GetEnvironmentVariable("TRUST_ALL_FORWARDED_PROXIES"), out var trustAll) &&
                              trustAll;

        if (trustAllProxies)
        {
            Log.Warning("TrustAllForwardedProxies is enabled. This allows IP spoofing via X-Forwarded-For. Configure Network:KnownNetworks/KnownProxies instead.");
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            return;
        }

        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        var configNetworks = builder.Configuration["Network:KnownNetworks"];
        var knownNetworks = string.IsNullOrEmpty(configNetworks)
            ? Environment.GetEnvironmentVariable("KNOWN_NETWORKS") ?? string.Empty
            : configNetworks;
        foreach (var network in knownNetworks.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseNetwork(network, out var parsedNetwork))
                options.KnownIPNetworks.Add(parsedNetwork);
            else
                Log.Warning("Ignoring invalid forwarded-header trusted network: {Network}", network);
        }

        var configProxies = builder.Configuration["Network:KnownProxies"];
        var knownProxies = string.IsNullOrEmpty(configProxies)
            ? Environment.GetEnvironmentVariable("KNOWN_PROXIES") ?? string.Empty
            : configProxies;
        foreach (var proxy in knownProxies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(proxy, out var parsedProxy))
                options.KnownProxies.Add(parsedProxy);
            else
                Log.Warning("Ignoring invalid forwarded-header trusted proxy: {Proxy}", proxy);
        }

        if (tunnelEnabled)
        {
            // Local cloudflared acts as the direct proxy in tunnel mode.
            options.KnownProxies.Add(IPAddress.Loopback);
            options.KnownProxies.Add(IPAddress.IPv6Loopback);
        }
    });

    // Load configuration from appsettings.json (with mode override + CLI config)
    builder.Configuration.AddJsonFile("appsettings.json", true, true);
    builder.Configuration.AddJsonFile($"appsettings.{mode}.json", true, true);
    if (configPath != null)
        builder.Configuration.AddJsonFile(Path.GetFullPath(configPath), optional: false, reloadOnChange: true);
    builder.Configuration.AddEnvironmentVariables();
    builder.Configuration.AddEnvironmentVariables("STYLOBOT_");

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

    // Add Bot Detection + optional LLM provider
    if (llmProvider != null)
    {
        // Always register core detection first
        builder.Services.AddBotDetection();

        if (llmProvider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddStylobotOllama(
                llmUrl ?? "http://localhost:11434",
                llmModel ?? "qwen3:0.6b");
        }
        else
        {

            if (llmProvider.Equals("llamasharp", StringComparison.OrdinalIgnoreCase))
            {
                builder.Services.AddStylobotLlamaSharp(opts =>
                {
                    if (llmModel != null) opts.ModelPath = llmModel;
                });
            }
            else
            {
                builder.Services.AddStylobotCloudLlm(llmProvider, llmKey, llmModel, llmUrl);
            }
        }
    }
    else
    {
        builder.Services.AddBotDetection();
    }

    // Apply CLI overrides
    builder.Services.PostConfigure<Mostlylucid.BotDetection.Models.BotDetectionOptions>(opts =>
    {
        opts.DefaultActionPolicyName = actionPolicy;
        if (botThreshold.HasValue) opts.BotThreshold = botThreshold.Value;
        if (llmProvider != null) opts.EnableLlmDetection = true;
    });

    builder.Services.AddBotDetectionTelemetry();
    builder.Services.AddOpenTelemetry()
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddMeter(BotDetectionMetrics.MeterName)
            .AddMeter(BotDetectionSignalMeter.MeterName)
            .AddPrometheusExporter())
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddSource(BotDetectionTelemetry.ActivitySourceName));

    // Apply CLI overrides on top of config
    builder.Services.PostConfigure<Mostlylucid.BotDetection.Models.BotDetectionOptions>(opts =>
    {
        opts.DefaultActionPolicyName = actionPolicy;
        if (botThreshold.HasValue) opts.BotThreshold = botThreshold.Value;
    });

    // Detection event sink for live table
    var detectionSink = new DetectionEventSink();
    builder.Services.AddSingleton(detectionSink);

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
            kestrel.ListenAnyIP(portNumber, listenOptions =>
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

    // Tap detection results for the live table (placed BEFORE bot detection
    // so it wraps around it and always sees the evidence in Items after _next completes)
    if (!verbose)
        app.UseMiddleware<DetectionTapMiddleware>();

    // Use Bot Detection middleware
    app.UseBotDetection();

    // Health check endpoint (AOT-compatible) - mapped BEFORE YARP to avoid being proxied
    app.MapGet("/health",
        () => Results.Text(
            $"{{\"status\":\"healthy\",\"mode\":\"{mode}\",\"upstream\":\"{upstream}\",\"port\":\"{port}\"}}",
            "application/json"));

    app.MapPrometheusScrapingEndpoint("/metrics");

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
        app.Urls.Add($"http://*:{portNumber}");

    var scheme = useTls ? "https" : "http";
    if (verbose)
    {
        Log.Information("  ✓ Ready on {Scheme}://localhost:{Port}", scheme, port);
        Log.Information("  ✓ Upstream: {Upstream}", upstream);
        Log.Information("  ✓ Health:   {Scheme}://localhost:{Port}/health", scheme, port);
        Log.Information("");
    }

    // Shared tunnel URL (populated by cloudflared log parser, read by live table)
    string? tunnelUrl = null;

    // Launch Cloudflare tunnel if requested
    Process? tunnelProcess = null;
    if (tunnelEnabled)
    {
        tunnelProcess = LaunchCloudflaredTunnel(port, scheme, tunnelToken, url => tunnelUrl = url);
    }

    // Start live detection table (replaces verbose log output)
    CancellationTokenSource? liveTableCts = null;
    Task? liveTableTask = null;
    if (!verbose)
    {
        liveTableCts = new CancellationTokenSource();
        var liveTable = new LiveDetectionTableService(
            detectionSink, mode, upstream, port, actionPolicy,
            useTls, tunnelEnabled, () => tunnelUrl);
        liveTableTask = liveTable.StartAsync(liveTableCts.Token);
    }
    else
    {
        Log.Information("  Press Ctrl+C to stop.");
    }

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
        // Stop live table
        if (liveTableCts != null)
        {
            await liveTableCts.CancelAsync();
            if (liveTableTask != null)
                try { await liveTableTask; } catch (OperationCanceledException) { }
            liveTableCts.Dispose();
        }

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

static void ShowManPage()
{
    var man = """
    [bold blue]STYLOBOT(1)[/]                    [dim]Self-hosted bot defense[/]                    [bold blue]STYLOBOT(1)[/]

    [bold]NAME[/]
        stylobot - reverse proxy with 31-detector bot defense. Free forever.

    [bold]SYNOPSIS[/]
        [bold]stylobot[/] <port> <upstream> [[options]]
        [bold]stylobot[/] start <port> <upstream> [[options]]
        [bold]stylobot[/] stop | status | logs | man

    [bold]DESCRIPTION[/]
        StyloBot proxies HTTP traffic to an upstream server while running real-time
        bot detection. 31 detectors analyze every request in <1ms. Results display
        in a live terminal table with color-coded verdicts.

        Detection works fully without any LLM. LLM enrichment (optional) adds bot
        naming and intent classification asynchronously.

    [bold]COMMANDS[/]
        [bold]start[/]     Start as a background daemon (writes PID file)
        [bold]stop[/]      Stop the running daemon (SIGTERM)
        [bold]status[/]    Check if daemon is running + hit /health
        [bold]logs[/]      Show recent log output
        [bold]man[/]       This manual page

    [bold]OPTIONS[/]
        [bold]--mode[/] <demo|production>       Detection mode (default: demo)
        [bold]--policy[/] <name>                Action: logonly, block, throttle, challenge
        [bold]--threshold[/] <0.0-1.0>          Bot probability threshold (default: 0.7)
        [bold]--cert[/] <path>                  TLS certificate (.pfx or .pem)
        [bold]--key[/] <path>                   TLS private key (with .pem cert)
        [bold]--tunnel[/] [[token]]               Cloudflare Tunnel (requires cloudflared)
        [bold]--llm[/] <provider>               LLM: openai, anthropic, gemini, groq, ollama...
        [bold]--llm-key[/] <key>                API key (or env STYLOBOT_LLM_KEY)
        [bold]--model[/] <name>                 Override default model for provider
        [bold]--config[/] <path>                Custom appsettings.json
        [bold]--verbose[/]                      Full log output (disables live table)

    [bold]LLM PROVIDERS[/]
        Any provider, any tier. Bring your own API key.

        [dim]Provider     Model               Cost[/]
        openai       gpt-4o-mini         ~$0.15/1M tokens
        anthropic    claude-haiku-4-5     ~$0.25/1M tokens
        gemini       gemini-2.0-flash     Free tier
        groq         llama-3.3-70b        Free tier
        mistral      mistral-small        ~$0.10/1M tokens
        deepseek     deepseek-chat        ~$0.07/1M tokens
        ollama       qwen3:0.6b           Free (local)
        llamasharp   qwen2.5:0.5b         Free (in-process CPU)

    [bold]ENVIRONMENT[/]
        PORT                 Listen port
        UPSTREAM             Upstream URL
        MODE                 Detection mode
        STYLOBOT_POLICY      Default action policy
        STYLOBOT_LLM_KEY     LLM API key
        KNOWN_NETWORKS       Trusted proxy CIDRs (comma-separated)
        KNOWN_PROXIES        Trusted proxy IPs (comma-separated)

    [bold]CONFIGURATION[/]
        Config priority (highest first):
          1. CLI flags
          2. Environment variables (STYLOBOT_* prefix)
          3. --config <file>
          4. appsettings.{mode}.json
          5. appsettings.json

        Full reference: https://github.com/scottgal/stylobot/blob/main/Mostlylucid.BotDetection/docs/configuration.md

    [bold]EXAMPLES[/]
        stylobot 5080 http://localhost:3000
        stylobot 8000 http://api.mysite.com --mode production --policy block
        stylobot 5080 http://localhost:3000 --tunnel
        stylobot 5080 http://localhost:3000 --llm groq --llm-key gsk-...
        stylobot start 443 https://backend:8080 --cert cert.pfx --policy block

    [bold]ENDPOINTS[/]
        /health          Health check (JSON)
        /metrics         Prometheus metrics
        /**              All other paths proxied with detection

    [bold]FILES[/]
        ~/.config/stylobot/stylobot.pid     Daemon PID file
        ~/.config/stylobot/stylobot.port    Daemon port file
        ./appsettings.json                  Default configuration
        ./logs/                             Log output directory

    [bold]SEE ALSO[/]
        https://stylobot.net
        https://github.com/scottgal/stylobot

    [dim]StyloBot Community Edition                Free forever                    v5.6[/]
    """;

    Spectre.Console.AnsiConsole.Write(new Spectre.Console.Markup(man));
    Console.WriteLine();
}

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

static bool TryParseNetwork(string value, out System.Net.IPNetwork network)
{
    network = default;
    var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length != 2 ||
        !IPAddress.TryParse(parts[0], out var prefix) ||
        !int.TryParse(parts[1], out var prefixLength))
        return false;

    // Validate prefix length range (IPv4: 0-32, IPv6: 0-128)
    var maxPrefix = prefix.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
    if (prefixLength < 0 || prefixLength > maxPrefix)
        return false;

    network = new System.Net.IPNetwork(prefix, prefixLength);
    return true;
}

// Launch cloudflared tunnel subprocess
static Process? LaunchCloudflaredTunnel(string port, string scheme, string? token, Action<string>? onTunnelUrl = null)
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
            Log.Error("cloudflared not found. Install: brew install cloudflared (macOS) | apt install cloudflared (Linux) | winget install Cloudflare.cloudflared (Windows)");
            return null;
        }
    }
    catch
    {
        Log.Error("cloudflared not found. Install: brew install cloudflared (macOS) | apt install cloudflared (Linux) | winget install Cloudflare.cloudflared (Windows)");
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
                {
                    Log.Information("  ✓ Tunnel: {TunnelInfo}", line.Trim());
                    // Extract URL from the line (e.g., "https://foo-bar.trycloudflare.com")
                    var match = System.Text.RegularExpressions.Regex.Match(line, @"(https://[^\s|]+\.trycloudflare\.com)");
                    if (match.Success) onTunnelUrl?.Invoke(match.Groups[1].Value);
                }
                else if (line.Contains("ERR"))
                    Log.Warning("[cloudflared] {Line}", line.Trim());
                else
                    Log.Debug("[cloudflared] {Line}", line.Trim());
            }
        }
    });

    return process;
}
