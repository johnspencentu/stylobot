using System.IO.Enumeration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.SimulationPacks;

/// <summary>
///     Action policy that serves fake response templates from simulation packs
///     when high-confidence bots hit honeypot or CVE probe paths.
///     Falls through gracefully if no matching template is found.
/// </summary>
public class SimulationPackResponder : IActionPolicy
{
    private readonly ISimulationPackRegistry _registry;
    private readonly ILogger<SimulationPackResponder> _logger;

    public SimulationPackResponder(
        ISimulationPackRegistry registry,
        ILogger<SimulationPackResponder> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "simulation-pack";

    /// <inheritdoc />
    public ActionType ActionType => ActionType.Custom;

    /// <inheritdoc />
    public async Task<ActionResult> ExecuteAsync(
        HttpContext context,
        AggregatedEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        var path = context.Request.Path.Value ?? "/";

        // Check if this path matches any simulation pack
        if (!_registry.IsHoneypotPath(path, out var matchedPack, out var matchedCve))
        {
            return ActionResult.Allowed("No simulation pack match");
        }

        // Try to find a response template
        var template = FindTemplate(path, matchedPack!, matchedCve);
        if (template is null)
        {
            // No template found - serve a generic framework-appropriate 404
            template = BuildGeneric404(matchedPack!);
        }

        // Apply realistic timing delay from the pack's timing profile
        var timing = matchedPack!.TimingProfile;
        var delayMs = template.MinDelayMs > 0 || template.MaxDelayMs > 0
            ? Random.Shared.Next(
                Math.Max(template.MinDelayMs, timing.MinResponseMs),
                Math.Max(template.MaxDelayMs, timing.MaxResponseMs) + 1)
            : Random.Shared.Next(timing.MinResponseMs, timing.MaxResponseMs + 1);

        if (delayMs > 0)
        {
            await Task.Delay(delayMs, cancellationToken);
        }

        // Write the response
        context.Response.StatusCode = template.StatusCode;
        context.Response.ContentType = template.ContentType;

        // Add template headers
        if (template.Headers is not null)
        {
            foreach (var header in template.Headers)
            {
                context.Response.Headers.TryAdd(header.Key, header.Value);
            }
        }

        // Add debug headers
        context.Response.Headers.TryAdd("X-StyloBot-Pack", matchedPack.Id);
        context.Response.Headers.TryAdd("X-StyloBot-Honeypot", "true");

        if (!string.IsNullOrEmpty(template.Body))
        {
            await context.Response.WriteAsync(template.Body, cancellationToken);
        }

        _logger.LogInformation(
            "SimulationPack responded: pack={PackId}, path={Path}, status={StatusCode}, delay={DelayMs}ms, cve={CveId}",
            matchedPack.Id, path, template.StatusCode, delayMs, matchedCve?.CveId ?? "none");

        return ActionResult.Blocked(template.StatusCode,
            $"Simulation pack response: {matchedPack.Id} (path: {path})");
    }

    /// <summary>
    ///     Find the best matching response template for the given path.
    ///     CVE module ProbeResponse takes priority over pack-level templates.
    /// </summary>
    private static PackResponseTemplate? FindTemplate(string path, SimulationPack pack, PackCveModule? cve)
    {
        // If the CVE module has its own ProbeResponse, prefer that
        if (cve?.ProbeResponse is not null &&
            FileSystemName.MatchesSimpleExpression(cve.ProbeResponse.PathPattern, path, ignoreCase: true))
        {
            return cve.ProbeResponse;
        }

        // Search pack-level response templates
        foreach (var template in pack.ResponseTemplates)
        {
            if (FileSystemName.MatchesSimpleExpression(template.PathPattern, path, ignoreCase: true))
            {
                return template;
            }
        }

        return null;
    }

    /// <summary>
    ///     Build a generic 404 response that still looks like the target framework.
    /// </summary>
    private static PackResponseTemplate BuildGeneric404(SimulationPack pack)
    {
        var body = pack.Framework.ToLowerInvariant() switch
        {
            "wordpress" => """
                <!DOCTYPE html><html><head><title>Page not found &#8211; My WordPress Site</title>
                <link rel="stylesheet" href="/wp-content/themes/flavor/style.css" type="text/css">
                </head><body class="error404"><div class="content"><h1>Oops! That page can&rsquo;t be found.</h1>
                <p>It looks like nothing was found at this location.</p></div></body></html>
                """,
            "drupal" => """
                <!DOCTYPE html><html><head><title>Page not found | Drupal</title></head>
                <body><div class="content"><h1>Page not found</h1>
                <p>The requested page could not be found.</p></div></body></html>
                """,
            _ => """
                <!DOCTYPE html><html><head><title>404 Not Found</title></head>
                <body><h1>Not Found</h1><p>The requested URL was not found on this server.</p></body></html>
                """
        };

        return new PackResponseTemplate
        {
            PathPattern = "*",
            StatusCode = 404,
            ContentType = "text/html; charset=UTF-8",
            Body = body,
            Headers = new Dictionary<string, string>
            {
                ["X-Powered-By"] = pack.Framework.ToLowerInvariant() switch
                {
                    "wordpress" => "PHP/7.4.33",
                    "drupal" => "PHP/8.1.27",
                    _ => "PHP/8.2"
                }
            }
        };
    }
}
