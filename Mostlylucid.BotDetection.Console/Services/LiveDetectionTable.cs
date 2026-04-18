using System.Collections.Concurrent;
using System.Threading.Channels;
using Mostlylucid.BotDetection.Orchestration;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Mostlylucid.BotDetection.Console.Services;

/// <summary>
///     A single detection event for the live table display.
/// </summary>
public sealed record DetectionEntry(
    DateTime Timestamp,
    string Path,
    double BotProbability,
    string Verdict,
    string TopDetector,
    string? BotName,
    string? ActionPolicy,
    string? Country,
    double DetectionTimeMs,
    int DetectorCount);

/// <summary>
///     Singleton channel that receives detection results from middleware
///     and feeds them to the live Spectre.Console table.
/// </summary>
public sealed class DetectionEventSink
{
    private readonly Channel<DetectionEntry> _channel = Channel.CreateBounded<DetectionEntry>(
        new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });

    public ChannelReader<DetectionEntry> Reader => _channel.Reader;

    public void Write(DetectionEntry entry) => _channel.Writer.TryWrite(entry);
}

/// <summary>
///     Middleware that taps detection results from HttpContext.Items
///     and writes them to the DetectionEventSink for the live table.
/// </summary>
public sealed class DetectionTapMiddleware
{
    private readonly RequestDelegate _next;
    private readonly DetectionEventSink _sink;

    public DetectionTapMiddleware(RequestDelegate next, DetectionEventSink sink)
    {
        _next = next;
        _sink = sink;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("response has already started", StringComparison.OrdinalIgnoreCase))
        {
            // Expected: YARP throws when bot detection already wrote a block response.
        }

        // Read detection results from HttpContext.Items (set by BotDetectionMiddleware)
        if (context.Items.TryGetValue("BotDetection.AggregatedEvidence", out var evidenceObj)
            && evidenceObj is AggregatedEvidence evidence)
        {
            var isBot = evidence.BotProbability >= 0.5;
            var topDetector = evidence.Contributions?.LastOrDefault()?.DetectorName ?? "-";
            var country = evidence.Signals?.TryGetValue("geo.country_code", out var cc) == true
                ? cc?.ToString() : null;

            _sink.Write(new DetectionEntry(
                DateTime.Now,
                context.Request.Path.Value ?? "/",
                evidence.BotProbability,
                isBot ? "BOT" : "HUMAN",
                topDetector,
                evidence.PrimaryBotName,
                evidence.TriggeredActionPolicyName,
                country,
                evidence.TotalProcessingTimeMs,
                evidence.ContributingDetectors?.Count ?? 0));
        }
    }
}

/// <summary>
///     Background service that renders a live Spectre.Console display
///     showing detection results, throughput, and stats as they arrive.
/// </summary>
public sealed class LiveDetectionTableService : BackgroundService
{
    private readonly DetectionEventSink _sink;
    private readonly string _mode;
    private readonly string _upstream;
    private readonly string _port;
    private readonly string _policy;
    private readonly bool _useTls;
    private readonly bool _tunnelEnabled;
    private readonly int _maxRows;

    // Stats
    private int _totalRequests;
    private int _totalBots;
    private int _totalHumans;
    private double _totalDetectionTimeMs;
    private double _maxDetectionTimeMs;
    private readonly DateTime _startTime = DateTime.Now;

    // Throughput tracking (sliding 10s window)
    private readonly ConcurrentQueue<DateTime> _recentRequests = new();

    // Top endpoints
    private readonly ConcurrentDictionary<string, int> _endpointHits = new();

    // Top bot signatures
    private readonly ConcurrentDictionary<string, int> _botSignatures = new();

    // High-risk bot tracking for suggestions
    private int _highRiskBots; // prob >= 0.8
    private bool _blockSuggestionShown;

    public LiveDetectionTableService(
        DetectionEventSink sink,
        string mode, string upstream, string port, string policy,
        bool useTls, bool tunnelEnabled, int maxRows = 15)
    {
        _sink = sink;
        _mode = mode;
        _upstream = upstream;
        _port = port;
        _policy = policy;
        _useTls = useTls;
        _tunnelEnabled = tunnelEnabled;
        _maxRows = maxRows;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small delay to let startup complete
        await Task.Delay(2000, stoppingToken);

        var entries = new LinkedList<DetectionEntry>();
        var scheme = _useTls ? "https" : "http";

        await AnsiConsole.Live(BuildDisplay(entries, scheme))
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .StartAsync(async ctx =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    while (_sink.Reader.TryRead(out var entry))
                    {
                        entries.AddFirst(entry);
                        _totalRequests++;
                        _totalDetectionTimeMs += entry.DetectionTimeMs;
                        if (entry.DetectionTimeMs > _maxDetectionTimeMs)
                            _maxDetectionTimeMs = entry.DetectionTimeMs;

                        if (entry.Verdict == "BOT")
                        {
                            _totalBots++;
                            if (entry.BotProbability >= 0.8) _highRiskBots++;
                            var sig = entry.BotName ?? entry.TopDetector;
                            _botSignatures.AddOrUpdate(sig, 1, (_, c) => c + 1);
                        }
                        else _totalHumans++;

                        // Normalize path (strip query string to prevent unbounded growth)
                        var normalizedPath = entry.Path.Split('?')[0];
                        _endpointHits.AddOrUpdate(normalizedPath, 1, (_, c) => c + 1);
                        _recentRequests.Enqueue(DateTime.Now);

                        // Trim dictionaries to prevent unbounded growth
                        if (_endpointHits.Count > 500) TrimDictionary(_endpointHits, 100);
                        if (_botSignatures.Count > 200) TrimDictionary(_botSignatures, 50);

                        while (entries.Count > _maxRows)
                            entries.RemoveLast();
                    }

                    // Trim throughput window to last 10 seconds
                    var cutoff = DateTime.Now.AddSeconds(-10);
                    while (_recentRequests.TryPeek(out var oldest) && oldest < cutoff)
                        _recentRequests.TryDequeue(out _);

                    ctx.UpdateTarget(BuildDisplay(entries, scheme));

                    try
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        cts.CancelAfter(1000);
                        await _sink.Reader.WaitToReadAsync(cts.Token);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Periodic refresh
                    }
                }
            });
    }

    private IRenderable BuildDisplay(LinkedList<DetectionEntry> entries, string scheme)
    {
        var uptime = DateTime.Now - _startTime;
        var reqPerSec = _recentRequests.Count / 10.0;
        var avgDetectionMs = _totalRequests > 0 ? _totalDetectionTimeMs / _totalRequests : 0;
        var botPct = _totalRequests > 0 ? (double)_totalBots / _totalRequests * 100 : 0;

        // === LEFT COLUMN: Config + Stats ===
        var configPanel = new Table { Border = TableBorder.None, ShowHeaders = false };
        configPanel.AddColumn(new TableColumn("").Width(10).NoWrap());
        configPanel.AddColumn(new TableColumn("").NoWrap());
        configPanel.AddRow("[bold blue]stylobot[/]", "[dim]self-hosted bot defense[/]");
        configPanel.AddRow("[dim]Mode[/]", $"[bold]{_mode.ToUpper()}[/]");
        configPanel.AddRow("[dim]Policy[/]", PolicyMarkup(_policy));
        configPanel.AddRow("[dim]Upstream[/]", Markup.Escape(_upstream));
        configPanel.AddRow("[dim]Listen[/]", $"{scheme}://localhost:{_port}");
        if (_tunnelEnabled) configPanel.AddRow("[dim]Tunnel[/]", "[green]Cloudflare[/]");
        configPanel.AddRow("[dim]Dashboard[/]", $"[dim]{scheme}://localhost:{_port}/_stylobot[/]");
        configPanel.AddRow("[dim]Uptime[/]", $"[dim]{uptime:hh\\:mm\\:ss}[/]");

        // Stats panel
        var statsPanel = new Table { Border = TableBorder.None, ShowHeaders = false };
        statsPanel.AddColumn(new TableColumn("").Width(10).NoWrap());
        statsPanel.AddColumn(new TableColumn("").NoWrap());
        statsPanel.AddRow("[dim]Requests[/]", $"[bold]{_totalRequests}[/]");
        statsPanel.AddRow("[dim]Rate[/]", $"[bold]{reqPerSec:F1}[/] req/s");
        statsPanel.AddRow("[dim]Bots[/]", $"[red]{_totalBots}[/] ({botPct:F0}%)");
        statsPanel.AddRow("[dim]Humans[/]", $"[green]{_totalHumans}[/]");
        statsPanel.AddRow("[dim]Avg time[/]", $"{avgDetectionMs:F1}ms");
        statsPanel.AddRow("[dim]Max time[/]", $"{_maxDetectionTimeMs:F1}ms");

        // Top endpoints (top 5)
        var endpointsTable = new Table { Border = TableBorder.Simple, Expand = true };
        endpointsTable.AddColumn(new TableColumn("[dim]Endpoint[/]"));
        endpointsTable.AddColumn(new TableColumn("[dim]Hits[/]").Width(6).RightAligned());
        var topEndpoints = _endpointHits
            .OrderByDescending(kv => kv.Value)
            .Take(5);
        foreach (var ep in topEndpoints)
        {
            var path = ep.Key.Length > 30 ? ep.Key[..27] + "..." : ep.Key;
            endpointsTable.AddRow(Markup.Escape(path), $"[bold]{ep.Value}[/]");
        }
        if (!_endpointHits.Any())
            endpointsTable.AddRow("[dim]waiting...[/]", "");

        // Top bots (top 5)
        var botsTable = new Table { Border = TableBorder.Simple, Expand = true };
        botsTable.AddColumn(new TableColumn("[dim]Bot Signature[/]"));
        botsTable.AddColumn(new TableColumn("[dim]Hits[/]").Width(6).RightAligned());
        var topBots = _botSignatures
            .OrderByDescending(kv => kv.Value)
            .Take(5);
        foreach (var bot in topBots)
        {
            var name = bot.Key.Length > 25 ? bot.Key[..22] + "..." : bot.Key;
            botsTable.AddRow($"[red]{Markup.Escape(name)}[/]", $"[bold]{bot.Value}[/]");
        }
        if (!_botSignatures.Any())
            botsTable.AddRow("[dim]none detected[/]", "");

        // Left sidebar
        var leftSide = new Rows(
            new Panel(configPanel) { Header = new PanelHeader("[bold]Config[/]"), Border = BoxBorder.Rounded, Expand = true },
            new Panel(statsPanel) { Header = new PanelHeader("[bold]Throughput[/]"), Border = BoxBorder.Rounded, Expand = true },
            new Panel(endpointsTable) { Header = new PanelHeader("[bold]Top Endpoints[/]"), Border = BoxBorder.Rounded, Expand = true },
            new Panel(botsTable) { Header = new PanelHeader("[bold]Top Bots[/]"), Border = BoxBorder.Rounded, Expand = true }
        );

        // === RIGHT COLUMN: Detection feed ===
        var detectionTable = new Table { Border = TableBorder.Rounded, Expand = true };
        detectionTable.AddColumn(new TableColumn("[dim]Time[/]").Width(8));
        detectionTable.AddColumn(new TableColumn("[dim]Path[/]"));
        detectionTable.AddColumn(new TableColumn("[dim]Prob[/]").Width(5));
        detectionTable.AddColumn(new TableColumn("[dim]Verdict[/]").Width(7));
        detectionTable.AddColumn(new TableColumn("[dim]ms[/]").Width(5));
        detectionTable.AddColumn(new TableColumn("[dim]Detector[/]"));
        detectionTable.AddColumn(new TableColumn("[dim]Bot Name[/]"));
        detectionTable.AddColumn(new TableColumn("[dim]Action[/]").Width(10));

        if (entries.Count == 0)
        {
            detectionTable.AddRow("[dim]Waiting for requests...[/]", "", "", "", "", "", "", "");
        }
        else
        {
            foreach (var e in entries)
            {
                var probColor = e.BotProbability >= 0.7 ? "red"
                    : e.BotProbability >= 0.4 ? "yellow"
                    : "green";
                var verdictMarkup = e.Verdict == "BOT"
                    ? "[bold red]BOT[/]"
                    : "[bold green]OK[/]";
                var timeColor = e.DetectionTimeMs > 50 ? "yellow"
                    : e.DetectionTimeMs > 200 ? "red"
                    : "dim";

                var path = e.Path.Length > 25 ? e.Path[..22] + "..." : e.Path;
                var detector = e.TopDetector.Length > 16 ? e.TopDetector[..13] + "..." : e.TopDetector;
                var botName = (e.BotName ?? "-");
                if (botName.Length > 18) botName = botName[..15] + "...";

                detectionTable.AddRow(
                    $"[dim]{e.Timestamp:HH:mm:ss}[/]",
                    Markup.Escape(path),
                    $"[{probColor}]{e.BotProbability:F2}[/]",
                    verdictMarkup,
                    $"[{timeColor}]{e.DetectionTimeMs:F0}[/]",
                    Markup.Escape(detector),
                    Markup.Escape(botName),
                    Markup.Escape(e.ActionPolicy ?? "-"));
            }
        }

        // === LAYOUT: Two columns ===
        var layout = new Columns(
            new Padder(leftSide, new Padding(0, 0, 1, 0)),
            detectionTable
        );

        // === SUGGESTION BAR ===
        IRenderable suggestion;
        if (_highRiskBots >= 5 && _policy.Equals("logonly", StringComparison.OrdinalIgnoreCase) && !_blockSuggestionShown)
        {
            suggestion = new Panel(
                new Markup($"[bold yellow]! {_highRiskBots} high-risk bots detected.[/] Your policy is [bold]logonly[/]. Consider: [bold]stylobot {_port} {Markup.Escape(_upstream)} --policy block[/]"))
            { Border = BoxBorder.Heavy, BorderStyle = new Style(Color.Yellow) };
        }
        else
        {
            suggestion = new Markup("[dim]Press Ctrl+C to stop  |  Dashboard: /_stylobot  |  --verbose for full logs[/]");
        }

        // === FINAL ASSEMBLY ===
        var root = new Rows(layout, suggestion);
        return root;
    }

    private static string PolicyMarkup(string policy) => policy.ToLowerInvariant() switch
    {
        "block" => "[bold red]block[/]",
        "throttle" or "throttle-stealth" => "[bold yellow]throttle[/]",
        "challenge" => "[bold yellow]challenge[/]",
        "logonly" => "[bold dim]logonly[/] [dim](monitoring only)[/]",
        _ => $"[bold]{Markup.Escape(policy)}[/]"
    };

    private static void TrimDictionary(ConcurrentDictionary<string, int> dict, int keepTop)
    {
        var toKeep = dict.OrderByDescending(kv => kv.Value).Take(keepTop).Select(kv => kv.Key).ToHashSet();
        foreach (var key in dict.Keys)
            if (!toKeep.Contains(key))
                dict.TryRemove(key, out _);
    }
}
