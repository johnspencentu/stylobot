namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>Filter for the unified investigation view. Each entity type becomes a WHERE clause.</summary>
public sealed record InvestigationFilter
{
    public required string EntityType { get; init; }  // signature, country, path, ua_family, ip, fingerprint
    public required string EntityValue { get; init; }
    public DateTime? Start { get; init; }
    public DateTime? End { get; init; }
    public int Limit { get; init; } = 50;
    public int Offset { get; init; } = 0;
    public string? Tab { get; init; }  // which tab to render (detections, signatures, endpoints, geo, fingerprints, signaltrace)
}

/// <summary>Aggregated result for the investigation view. Tabs pull from different fields.</summary>
public sealed record InvestigationResult
{
    public required InvestigationSummary Summary { get; init; }
    public IReadOnlyList<DashboardDetectionEvent> Detections { get; init; } = [];
    public IReadOnlyList<SignatureSummary> Signatures { get; init; } = [];
    public IReadOnlyList<EndpointStat> EndpointStats { get; init; } = [];
    public IReadOnlyList<CountryStat> CountryBreakdown { get; init; } = [];
    public int TotalCount { get; init; }
}

/// <summary>At-a-glance summary for the filtered segment.</summary>
public sealed record InvestigationSummary
{
    public long TotalDetections { get; init; }
    public DateTime? FirstSeen { get; init; }
    public DateTime? LastSeen { get; init; }
    public int HighRisk { get; init; }
    public int MediumRisk { get; init; }
    public int LowRisk { get; init; }
    public IReadOnlyList<string> TopReasons { get; init; } = [];
}

/// <summary>Distinct signature seen in the result set.</summary>
public sealed record SignatureSummary
{
    public required string PrimarySignature { get; init; }
    public int HitCount { get; init; }
    public string? BotName { get; init; }
    public string? BotType { get; init; }
    public string? RiskBand { get; init; }
    public string? UaFamily { get; init; }
    public bool IsKnownBot { get; init; }
    public DateTime LastSeen { get; init; }
    public string? ClientSideSignature { get; init; }
}

/// <summary>Endpoint stats grouped by method + path.</summary>
public sealed record EndpointStat
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public int Count { get; init; }
    public double AvgBotProbability { get; init; }
    public string? DominantRiskBand { get; init; }
}

/// <summary>Country breakdown within the result set.</summary>
public sealed record CountryStat
{
    public required string CountryCode { get; init; }
    public int Count { get; init; }
    public int BotCount { get; init; }
    public string? DominantRiskBand { get; init; }
}
