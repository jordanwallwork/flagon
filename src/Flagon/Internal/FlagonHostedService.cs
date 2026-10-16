using Microsoft.Extensions.Hosting;

namespace Flagon.Internal;

/// <summary>Loads the initial snapshot when the host starts.</summary>
internal sealed class FlagonHostedService(FlagSnapshotManager snapshots) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => snapshots.RefreshAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
