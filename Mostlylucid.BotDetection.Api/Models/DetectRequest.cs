namespace Mostlylucid.BotDetection.Api.Models;
public sealed record DetectRequest
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required Dictionary<string, string> Headers { get; init; }
    public required string RemoteIp { get; init; }
    public string Protocol { get; init; } = "https";
    public TlsInfo? Tls { get; init; }
}
public sealed record TlsInfo
{
    public string? Version { get; init; }
    public string? Cipher { get; init; }
    public string? Ja3 { get; init; }
    public string? Ja4 { get; init; }
}
