using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jorrk.Flagon.Storage.SqlServer;

/// <summary>
/// A <see cref="SqlServerFlagStore"/> that also pushes change notifications, so apps refresh within milliseconds of a
/// change instead of at the next poll. Built by <c>UseSqlServerStorage(..., push: true)</c>.
/// </summary>
/// <remarks>
/// <para>Push uses query notifications (<c>SqlDependency</c>) on the change counter every version-moving write already
/// updates, so the write path and schema are unchanged. Polling stays on as the backstop.</para>
/// <para>Requirements: Service Broker enabled on the database (on by default for databases made with
/// <c>CREATE DATABASE</c>), and a login that can create a queue, service and procedure (<c>db_owner</c>), because
/// <c>SqlDependency</c> creates them for each process. Azure SQL Database has neither feature. When push cannot start, it
/// logs a warning, keeps retrying with backoff, and the store is polled meanwhile.</para>
/// <para>While a subscription exists, writes to the counter from a session with <c>QUOTED_IDENTIFIER OFF</c> fail
/// (error 1934). Flagon's own writes are unaffected.</para>
/// </remarks>
public sealed class SqlServerPushFlagStore : SqlServerFlagStore, IFlagChangeNotifier, IFlagChangeNotifierTeardown, IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _disposing = new();
    private readonly object _listenersGate = new();
    private readonly List<Task> _listenerRuns = [];
    private int _liveListeners;

    /// <summary>Creates a pushing store over <paramref name="connectionString"/> using tables in <paramref name="schema"/>.</summary>
    /// <param name="connectionString">A SQL Server connection string.</param>
    /// <param name="schema">The schema holding Flagon's tables (letters, digits, underscores).</param>
    /// <param name="logger">Receives the listener's connect, disconnect, backoff and notification logs.</param>
    /// <exception cref="ArgumentException">The connection string is empty or the schema name is invalid.</exception>
    public SqlServerPushFlagStore(string connectionString, string schema = SqlServerMigrations.DefaultSchema, ILogger? logger = null)
        : base(connectionString, schema)
    {
        _connectionString = connectionString;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>How long each subscription lasts before the listener renews it. Tests shorten it.</summary>
    internal TimeSpan SubscriptionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public bool IsConnected => Volatile.Read(ref _liveListeners) > 0;

    /// <inheritdoc />
    /// <remarks>
    /// Subscribes before returning, so a write that commits after this returns is delivered. If push cannot start or is
    /// later lost, it reconnects in the background with backoff and signals a resubscribe (see
    /// <see cref="FlagChangeSignal.Resubscribed"/>). Polling covers the gap. Signals carry no commit time. The listener
    /// stops when <paramref name="cancellationToken"/> is cancelled or the store is disposed.
    /// </remarks>
    public async Task StartAsync(Action<FlagChangeSignal> onChanged, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposing.Token);
        var listener = new SqlServerChangeListener(_connectionString, $"SELECT [value] FROM [{Schema}].[change_sequence] WHERE [id] = 1",
            SubscriptionTimeout, _logger, connected => Interlocked.Add(ref _liveListeners, connected ? 1 : -1));
        var run = await listener.StartAsync(onChanged, stop).ConfigureAwait(false);
        lock (_listenersGate)
        {
            _listenerRuns.Add(run);
        }
    }

    /// <summary>Completes once every listener started by <see cref="StartAsync"/> has stopped and released its listener.</summary>
    /// <inheritdoc />
    public Task ListenersStopped
    {
        get
        {
            lock (_listenersGate)
            {
                return Task.WhenAll(_listenerRuns);
            }
        }
    }

    /// <summary>Stops every listener this store started.</summary>
    public void Dispose() => _disposing.Cancel();
}
