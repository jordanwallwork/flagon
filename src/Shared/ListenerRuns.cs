// Shared source: compiled into each storage package that pushes change notifications (linked via <Compile Include=... Link=...>).
namespace Jorrk.Flagon.Storage.Internal;

/// <summary>
/// Tracks a store's running listeners by the token each was started with, for
/// <see cref="IFlagChangeNotifierTeardown.WhenStoppedAsync"/>.
/// </summary>
internal sealed class ListenerRuns
{
    private readonly object _gate = new();
    private readonly List<(CancellationToken StartToken, Task Run)> _runs = [];

    public void Add(CancellationToken startToken, Task run)
    {
        lock (_gate)
        {
            _runs.RemoveAll(r => r.Run.IsCompleted);
            _runs.Add((startToken, run));
        }
    }

    public Task WhenStoppedAsync(CancellationToken startToken)
    {
        lock (_gate)
        {
            return Task.WhenAll(_runs.Where(r => r.StartToken == startToken).Select(r => r.Run).ToList());
        }
    }
}
