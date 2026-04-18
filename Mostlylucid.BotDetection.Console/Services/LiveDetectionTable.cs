using System.Threading.Channels;
using Mostlylucid.BotDetection.Orchestration;
using Spectre.Console;

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
    string? Country);

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
        catch (InvalidOperationException)
        {
            // "Response has already started" from YARP when bot detection blocked the request.
            // Evidence is still in Items - read it below.
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
                country));
        }
    }
}

/// <summary>
///     Background service that renders a live Spectre.Console table
///     showing detection results as they arrive.
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
    private int _totalRequests;
    private int _totalBots;
    private int _totalHumans;

    public LiveDetectionTableService(
        DetectionEventSink sink,
        string mode, string upstream, string port, string policy,
        bool useTls, bool tunnelEnabled, int maxRows = 20)
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
        // Small delay to let startup logs finish
        await Task.Delay(2000, stoppingToken);

        var entries = new LinkedList<DetectionEntry>();
        var scheme = _useTls ? "https" : "http";

        await AnsiConsole.Live(BuildLayout(entries, scheme))
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
                        if (entry.Verdict == "BOT") _totalBots++;
                        else _totalHumans++;

                        while (entries.Count > _maxRows)
                            entries.RemoveLast();
                    }

                    ctx.UpdateTarget(BuildLayout(entries, scheme));

                    try
                    {
                        // Wait for next entry or timeout for periodic refresh
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        cts.CancelAfter(1000);
                        await _sink.Reader.WaitToReadAsync(cts.Token);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Timeout - just refresh the display
                    }
                }
            });
    }

    private Table BuildLayout(LinkedList<DetectionEntry> entries, string scheme)
    {
        // Header info
        var header = new Table { Border = TableBorder.None, ShowHeaders = false };
        header.AddColumn(new TableColumn("").NoWrap());
        header.AddColumn(new TableColumn("").NoWrap());
        header.AddRow("[bold]stylobot[/]", "[dim]self-hosted bot defense[/]");
        header.AddRow("[dim]Mode[/]", $"[bold]{_mode.ToUpper()}[/]");
        header.AddRow("[dim]Policy[/]", $"[bold]{_policy}[/]");
        header.AddRow("[dim]Upstream[/]", _upstream);
        header.AddRow("[dim]Listen[/]", $"{scheme}://localhost:{_port}");
        if (_tunnelEnabled) header.AddRow("[dim]Tunnel[/]", "[green]Cloudflare[/]");
        header.AddRow("[dim]Dashboard[/]", $"{scheme}://localhost:{_port}/_stylobot");
        header.AddRow("", "");
        header.AddRow("[dim]Requests[/]",
            $"[bold]{_totalRequests}[/] total  [red]{_totalBots} bot[/]  [green]{_totalHumans} human[/]");

        // Detection table
        var table = new Table { Border = TableBorder.Rounded, Expand = true };
        table.AddColumn(new TableColumn("[dim]Time[/]").Width(10));
        table.AddColumn(new TableColumn("[dim]Path[/]"));
        table.AddColumn(new TableColumn("[dim]Prob[/]").Width(6));
        table.AddColumn(new TableColumn("[dim]Verdict[/]").Width(8));
        table.AddColumn(new TableColumn("[dim]Detector[/]").Width(18));
        table.AddColumn(new TableColumn("[dim]Bot Name[/]").Width(22));
        table.AddColumn(new TableColumn("[dim]Action[/]").Width(14));

        if (entries.Count == 0)
        {
            table.AddRow("[dim]Waiting for requests...[/]", "", "", "", "", "", "");
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
                    : "[bold green]HUMAN[/]";

                var path = e.Path.Length > 35 ? e.Path[..32] + "..." : e.Path;

                table.AddRow(
                    $"[dim]{e.Timestamp:HH:mm:ss}[/]",
                    Markup.Escape(path),
                    $"[{probColor}]{e.BotProbability:F2}[/]",
                    verdictMarkup,
                    Markup.Escape(e.TopDetector),
                    Markup.Escape(e.BotName ?? "-"),
                    Markup.Escape(e.ActionPolicy ?? "-"));
            }
        }

        // Combine into outer layout table
        var layout = new Table { Border = TableBorder.None, ShowHeaders = false, Expand = true };
        layout.AddColumn("");
        layout.AddRow(header);
        layout.AddRow(table);
        layout.AddRow(new Markup("[dim]Press Ctrl+C to stop. Dashboard: /_stylobot[/]"));

        return layout;
    }
}
