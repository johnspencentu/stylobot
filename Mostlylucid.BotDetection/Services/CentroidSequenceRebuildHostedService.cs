using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Wires BotClusterService.ClustersUpdated → CentroidSequenceStore.RebuildAsync
///     and initializes the SQLite table on startup.
/// </summary>
internal sealed class CentroidSequenceRebuildHostedService : IHostedService
{
    private readonly BotClusterService? _clusterService;
    private readonly CentroidSequenceStore _centroidStore;
    private readonly ILogger<CentroidSequenceRebuildHostedService> _logger;

    public CentroidSequenceRebuildHostedService(
        CentroidSequenceStore centroidStore,
        ILogger<CentroidSequenceRebuildHostedService> logger,
        BotClusterService? clusterService = null)
    {
        _centroidStore = centroidStore;
        _logger = logger;
        _clusterService = clusterService;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _centroidStore.InitializeAsync(cancellationToken);

        if (_clusterService != null)
        {
            _clusterService.ClustersUpdated += OnClustersUpdated;
            _logger.LogDebug("CentroidSequenceStore wired to BotClusterService.ClustersUpdated");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_clusterService != null)
            _clusterService.ClustersUpdated -= OnClustersUpdated;
        return Task.CompletedTask;
    }

    private void OnClustersUpdated(
        IReadOnlyList<BotCluster> clusters,
        IReadOnlyList<SignatureBehavior> behaviors)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _centroidStore.RebuildAsync(clusters, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CentroidSequenceStore rebuild failed after cluster update");
            }
        });
    }
}

/// <summary>Calls AssetHashStore.InitializeAsync on startup to create the SQLite table.</summary>
internal sealed class AssetHashInitHostedService(AssetHashStore store) : IHostedService
{
    public Task StartAsync(CancellationToken ct) => store.InitializeAsync(ct);
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
