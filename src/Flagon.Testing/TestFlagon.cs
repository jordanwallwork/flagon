using System.Text.Json;
using Flagon.Internal;
using Flagon.Testing.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Flagon.Testing;

/// <summary>
/// A standalone <see cref="IFlagon"/> for unit tests: the real Flagon engine (snapshot, targeting and evaluation) over
/// an in-memory store, with no host or DI needed by the caller. Pass it as <see cref="IFlagon"/> to the code under test
/// and put flags in a known state with <see cref="Flags"/>.
/// </summary>
/// <example>
/// <code>
/// using var flags = TestFlagon.Create();
/// flags.Flags.SetFlag("new-checkout", true);
/// var checkout = new Checkout(flags);
/// </code>
/// </example>
public sealed class TestFlagon : IFlagon, IDisposable, IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly FlagonEvaluator _evaluator;
    private readonly FlagonTestingSwitches _switches;
    private int _disposed;

    private TestFlagon(ServiceProvider services)
    {
        _services = services;
        _evaluator = services.GetRequiredService<FlagonEvaluator>();
        _switches = services.GetRequiredService<FlagonTestingSwitches>();
        Flags = services.GetRequiredService<TestFlags>();
        Admin = services.GetRequiredService<IFlagonAdmin>();
    }

    /// <summary>Sets up flags for the test. Every write is visible to the next evaluation.</summary>
    public TestFlags Flags { get; }

    /// <summary>Every evaluation served, for assertions: a shortcut for <see cref="TestFlags.Evaluations"/>.</summary>
    public EvaluationLog Evaluations => Flags.Evaluations;

    /// <summary>The management API over the same store, for code under test that manages flags.</summary>
    public IFlagonAdmin Admin { get; }

    /// <summary>
    /// Creates and starts a test flagon. Startup is synchronous and fails fast: definitions are validated, seeded (unless
    /// <see cref="FlagonTestingSwitches.SeedDefinitions"/> is off) and loaded, and any failure throws. No background
    /// refresh or usage flush runs; changes made through <see cref="Flags"/> or <see cref="Admin"/> are seen at once.
    /// </summary>
    /// <param name="flagon">
    /// Flagon configuration, typically the app's own <c>AddFlagon</c> delegate so its <c>Define</c>s and
    /// <see cref="FlagonOptions.ApplicationName"/> apply. Any storage choice in it is ignored: its store is never built.
    /// </param>
    /// <param name="testing">Overrides of the test-safe defaults.</param>
    /// <returns>A started instance; dispose it at the end of the test.</returns>
    /// <exception cref="FlagTargetingException">A <c>Define</c>d flag's targeting rule does not compile.</exception>
    public static TestFlagon Create(Action<FlagonOptions>? flagon = null, Action<FlagonTestingSwitches>? testing = null)
    {
        var switches = new FlagonTestingSwitches();
        testing?.Invoke(switches);

        var services = new ServiceCollection();
        FlagonOptions? options = null;
        services.AddFlagon(o =>
        {
            flagon?.Invoke(o);
            options = o;
        });
        FlagonTestServices.Apply(services, options!, switches);

        var provider = services.BuildServiceProvider();
        try
        {
            Start(provider, switches).GetAwaiter().GetResult();
            return new TestFlagon(provider);
        }
        catch
        {
            provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    /// <summary>
    /// The hosted service's startup without its refresh loop: validate definitions, seed, load. The auto-creator runs
    /// only when switched on.
    /// </summary>
    private static async Task Start(IServiceProvider services, FlagonTestingSwitches switches)
    {
        var seeder = services.GetRequiredService<FlagSeeder>();
        seeder.ValidateDefinitions();
        if (switches.SeedDefinitions)
        {
            await seeder.SeedAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await services.GetRequiredService<FlagSnapshotManager>().RefreshAsync().ConfigureAwait(false);

        if (switches.AutoCreate)
        {
            await services.GetRequiredService<FlagAutoCreator>().StartAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public bool IsEnabled(string key, bool defaultValue = false, EvaluationContext? context = null) =>
        _evaluator.IsEnabled(key, defaultValue, context);

    /// <inheritdoc />
    public ValueTask<bool> IsEnabledAsync(string key, bool defaultValue = false, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        _evaluator.IsEnabledAsync(key, defaultValue, context, cancellationToken);

    /// <inheritdoc />
    public FlagEvaluationDetails<bool> GetBooleanDetails(string key, bool defaultValue = false, EvaluationContext? context = null) =>
        _evaluator.GetBooleanDetails(key, defaultValue, context);

    /// <inheritdoc />
    public ValueTask<FlagEvaluationDetails<bool>> GetBooleanDetailsAsync(string key, bool defaultValue = false, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        _evaluator.GetBooleanDetailsAsync(key, defaultValue, context, cancellationToken);

    /// <inheritdoc />
    public string GetString(string key, string defaultValue, EvaluationContext? context = null) =>
        _evaluator.GetString(key, defaultValue, context);

    /// <inheritdoc />
    public ValueTask<string> GetStringAsync(string key, string defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        _evaluator.GetStringAsync(key, defaultValue, context, cancellationToken);

    /// <inheritdoc />
    public FlagEvaluationDetails<string> GetStringDetails(string key, string defaultValue, EvaluationContext? context = null) =>
        _evaluator.GetStringDetails(key, defaultValue, context);

    /// <inheritdoc />
    public ValueTask<FlagEvaluationDetails<string>> GetStringDetailsAsync(string key, string defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        _evaluator.GetStringDetailsAsync(key, defaultValue, context, cancellationToken);

    /// <inheritdoc />
    public int GetInt(string key, int defaultValue, EvaluationContext? context = null) =>
        _evaluator.GetInt(key, defaultValue, context);

    /// <inheritdoc />
    public ValueTask<int> GetIntAsync(string key, int defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        _evaluator.GetIntAsync(key, defaultValue, context, cancellationToken);

    /// <inheritdoc />
    public FlagEvaluationDetails<int> GetIntDetails(string key, int defaultValue, EvaluationContext? context = null) =>
        _evaluator.GetIntDetails(key, defaultValue, context);

    /// <inheritdoc />
    public ValueTask<FlagEvaluationDetails<int>> GetIntDetailsAsync(string key, int defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        _evaluator.GetIntDetailsAsync(key, defaultValue, context, cancellationToken);

    /// <inheritdoc />
    public double GetDouble(string key, double defaultValue, EvaluationContext? context = null) =>
        _evaluator.GetDouble(key, defaultValue, context);

    /// <inheritdoc />
    public ValueTask<double> GetDoubleAsync(string key, double defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        _evaluator.GetDoubleAsync(key, defaultValue, context, cancellationToken);

    /// <inheritdoc />
    public FlagEvaluationDetails<double> GetDoubleDetails(string key, double defaultValue, EvaluationContext? context = null) =>
        _evaluator.GetDoubleDetails(key, defaultValue, context);

    /// <inheritdoc />
    public ValueTask<FlagEvaluationDetails<double>> GetDoubleDetailsAsync(string key, double defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default) =>
        _evaluator.GetDoubleDetailsAsync(key, defaultValue, context, cancellationToken);

    /// <inheritdoc />
    public T GetObject<T>(string key, T defaultValue, EvaluationContext? context = null, JsonSerializerOptions? options = null) =>
        _evaluator.GetObject(key, defaultValue, context, options);

    /// <inheritdoc />
    public ValueTask<T> GetObjectAsync<T>(string key, T defaultValue, EvaluationContext? context = null, JsonSerializerOptions? options = null, CancellationToken cancellationToken = default) =>
        _evaluator.GetObjectAsync(key, defaultValue, context, options, cancellationToken);

    /// <inheritdoc />
    public FlagEvaluationDetails<T> GetObjectDetails<T>(string key, T defaultValue, EvaluationContext? context = null, JsonSerializerOptions? options = null) =>
        _evaluator.GetObjectDetails(key, defaultValue, context, options);

    /// <inheritdoc />
    public ValueTask<FlagEvaluationDetails<T>> GetObjectDetailsAsync<T>(string key, T defaultValue, EvaluationContext? context = null, JsonSerializerOptions? options = null, CancellationToken cancellationToken = default) =>
        _evaluator.GetObjectDetailsAsync(key, defaultValue, context, options, cancellationToken);

    /// <inheritdoc />
    public FlagEvaluationSet<JsonElement> GetAllDetails(EvaluationContext? context = null) =>
        _evaluator.GetAllDetails(context);

    /// <summary>Stops the auto-creator and writes recorded usage (when switched on), then releases the engine.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Stops the auto-creator and writes recorded usage (when switched on), then releases the engine.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_switches.AutoCreate)
            {
                // Drains pending creates, so a test can assert on them after disposing.
                await _services.GetRequiredService<FlagAutoCreator>().StopAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await _services.GetRequiredService<UsageTracker>().FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await _services.DisposeAsync().ConfigureAwait(false);
        }
    }
}
