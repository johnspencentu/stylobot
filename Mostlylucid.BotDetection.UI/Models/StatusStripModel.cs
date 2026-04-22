namespace Mostlylucid.BotDetection.UI.Models;

public sealed class StatusStripModel
{
    public required string ActivePackName { get; init; }
    public bool DetectionActive { get; init; } = true;
    public required IReadOnlyList<ServiceStatus> Services { get; init; }
    public bool GuardiansEnabled { get; init; }
    public int GuardianCount { get; init; }
    public int GuardianAlerts { get; init; }
    public bool LlmConnected { get; init; }
    public string? LlmProvider { get; init; }
    public bool IsCommercial { get; init; }
}

public sealed record ServiceStatus(string Name, bool Connected);
