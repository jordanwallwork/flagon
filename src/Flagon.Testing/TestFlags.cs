using System.Text.Json;
using Flagon.Internal;
using Flagon.Internal.Targeting;
using Flagon.Storage;

namespace Flagon.Testing;

/// <summary>
/// Synchronous, chainable flag setup for tests. Every write is an upsert that replaces a flag of any type and state, and
/// is visible to the very next evaluation: the snapshot is refreshed before the method returns. Writes go straight to
/// the store without an audit entry, so the audit history shows only the app's own admin writes. Every write other than
/// <see cref="Archive"/> and <see cref="Remove"/> leaves the flag live: an archived flag is restored.
/// </summary>
/// <remarks>
/// <para>
/// Writes are checked with the same write-time rules as <see cref="IFlagonAdmin"/>: an invalid key or variant set throws
/// <see cref="FlagValidationException"/>, and a targeting rule the registered compiler rejects throws
/// <see cref="FlagTargetingException"/>, from the call that sets it.
/// </para>
/// <para>
/// Every <c>targeting</c> parameter is a flagd targeting rule as JSON text (JsonLogic plus flagd's operators), e.g.
/// <c>{"if":[{"in":[{"var":"email"},["a@example.com"]]},"on",null]}</c>. A rule that matches serves the variant it names;
/// otherwise the flag serves its default variant.
/// </para>
/// <para>Safe to call from parallel tasks and from inside a single-threaded <see cref="SynchronizationContext"/>.</para>
/// </remarks>
public sealed class TestFlags
{
    private readonly IFlagStore _store;
    private readonly FlagSnapshotManager _snapshots;
    private readonly ITargetingCompiler _compiler;
    private readonly FlagSeeder _seeder;

    internal TestFlags(
        IFlagStore store,
        FlagSnapshotManager snapshots,
        ITargetingCompiler compiler,
        FlagSeeder seeder,
        EvaluationLog evaluations)
    {
        _store = store;
        _snapshots = snapshots;
        _compiler = compiler;
        _seeder = seeder;
        Evaluations = evaluations;
    }

    /// <summary>
    /// Every evaluation served since startup (or the last <see cref="EvaluationLog.Clear"/>): in-process through
    /// <see cref="IFlagon"/> and remote through OFREP, but not management previews. Empty when
    /// <see cref="FlagonTestingSwitches.RecordEvaluations"/> is off.
    /// </summary>
    public EvaluationLog Evaluations { get; }

    /// <summary>Sets <paramref name="key"/> to an enabled boolean flag (<c>on</c>/<c>off</c> variants) serving <paramref name="value"/>.</summary>
    /// <param name="key">The flag key.</param>
    /// <param name="value">The value the flag serves when no targeting rule applies.</param>
    /// <param name="targeting">Optional targeting rule as JSON text; it names <c>"on"</c> or <c>"off"</c>.</param>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="FlagValidationException"><paramref name="key"/> is not a valid flag key, or <paramref name="targeting"/> is not JSON.</exception>
    /// <exception cref="FlagTargetingException"><paramref name="targeting"/> does not compile.</exception>
    public TestFlags SetFlag(string key, bool value, string? targeting = null) =>
        SetSingle(key, JsonSerializer.SerializeToElement(value), targeting);

    /// <summary>
    /// Sets <paramref name="key"/> to an enabled string flag with a single <c>default</c> variant serving
    /// <paramref name="value"/>, the same shape as a string flag defined in code or auto-created.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="value">The value the flag serves when no targeting rule applies.</param>
    /// <param name="targeting">Optional targeting rule as JSON text; it names <c>"default"</c> (or null for the caller's default).</param>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="FlagValidationException"><paramref name="key"/> is not a valid flag key, or <paramref name="targeting"/> is not JSON.</exception>
    /// <exception cref="FlagTargetingException"><paramref name="targeting"/> does not compile.</exception>
    public TestFlags SetFlag(string key, string value, string? targeting = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return SetSingle(key, JsonSerializer.SerializeToElement(value), targeting);
    }

    /// <summary>
    /// Sets <paramref name="key"/> to an enabled number flag with a single <c>default</c> variant serving
    /// <paramref name="value"/>. Otherwise as <see cref="SetFlag(string, string, string?)"/>.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="value">The value the flag serves when no targeting rule applies.</param>
    /// <param name="targeting">Optional targeting rule as JSON text.</param>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="FlagValidationException"><paramref name="key"/> is not a valid flag key, or <paramref name="targeting"/> is not JSON.</exception>
    /// <exception cref="FlagTargetingException"><paramref name="targeting"/> does not compile.</exception>
    public TestFlags SetFlag(string key, int value, string? targeting = null) =>
        SetSingle(key, JsonSerializer.SerializeToElement(value), targeting);

    /// <summary>
    /// Sets <paramref name="key"/> to an enabled number flag with a single <c>default</c> variant serving
    /// <paramref name="value"/>. Otherwise as <see cref="SetFlag(string, string, string?)"/>.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="value">The value the flag serves when no targeting rule applies.</param>
    /// <param name="targeting">Optional targeting rule as JSON text.</param>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="FlagValidationException">
    /// <paramref name="key"/> is not a valid flag key, <paramref name="value"/> is NaN or infinite (JSON has no such
    /// numbers), or <paramref name="targeting"/> is not JSON.
    /// </exception>
    /// <exception cref="FlagTargetingException"><paramref name="targeting"/> does not compile.</exception>
    public TestFlags SetFlag(string key, double value, string? targeting = null) =>
        SetSingle(key, FlagVariants.Number(key, FlagVariants.SingleVariant, value), targeting);

    /// <summary>
    /// Sets <paramref name="key"/> to an enabled object flag with a single <c>default</c> variant serving the JSON object
    /// <paramref name="json"/>. Otherwise as <see cref="SetFlag(string, string, string?)"/>.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="json">The value the flag serves: JSON text of an object, e.g. <c>{"columns":3}</c>.</param>
    /// <param name="targeting">Optional targeting rule as JSON text.</param>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="FlagValidationException">
    /// <paramref name="key"/> is not a valid flag key, <paramref name="json"/> is not valid JSON or not an object, or
    /// <paramref name="targeting"/> is not JSON.
    /// </exception>
    /// <exception cref="FlagTargetingException"><paramref name="targeting"/> does not compile.</exception>
    public TestFlags SetObjectFlag(string key, string json, string? targeting = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonElement value;
        try
        {
            using var document = JsonDocument.Parse(json);
            value = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new FlagValidationException($"Flag '{key}' object value is not valid JSON: {ex.Message}");
        }

        return SetSingle(key, RequireObject(key, value, nameof(SetObjectFlag)), targeting);
    }

    /// <summary>
    /// Sets <paramref name="key"/> to an enabled object flag with a single <c>default</c> variant serving
    /// <paramref name="value"/> serialised to JSON, as <see cref="IFlagon.GetObject{T}"/> reads it back. Otherwise as
    /// <see cref="SetFlag(string, string, string?)"/>.
    /// </summary>
    /// <typeparam name="T">The type of the value; it must serialise to a JSON object.</typeparam>
    /// <param name="key">The flag key.</param>
    /// <param name="value">The value the flag serves.</param>
    /// <param name="options">Serializer options for <paramref name="value"/>; pass the same options to the getter.</param>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="FlagValidationException">
    /// <paramref name="key"/> is not a valid flag key, or <paramref name="value"/> does not serialise to a JSON object.
    /// </exception>
    public TestFlags SetFlag<T>(string key, T value, JsonSerializerOptions? options = null) =>
        SetFlag(key, value, options, targeting: null);

    /// <summary>
    /// Sets <paramref name="key"/> to an enabled object flag with a single <c>default</c> variant serving
    /// <paramref name="value"/> serialised to JSON, with a targeting rule. Otherwise as
    /// <see cref="SetFlag{T}(string, T, JsonSerializerOptions?)"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="options"/> and <paramref name="targeting"/> are both required here so that a boolean, string or
    /// number value always picks its own overload; pass <c>options: null</c> for the default serializer options.
    /// </remarks>
    /// <typeparam name="T">The type of the value; it must serialise to a JSON object.</typeparam>
    /// <param name="key">The flag key.</param>
    /// <param name="value">The value the flag serves when no targeting rule applies.</param>
    /// <param name="options">Serializer options for <paramref name="value"/>, or null for the defaults.</param>
    /// <param name="targeting">The targeting rule as JSON text, or null for none.</param>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="FlagValidationException">
    /// <paramref name="key"/> is not a valid flag key, <paramref name="value"/> does not serialise to a JSON object, or
    /// <paramref name="targeting"/> is not JSON.
    /// </exception>
    /// <exception cref="FlagTargetingException"><paramref name="targeting"/> does not compile.</exception>
    public TestFlags SetFlag<T>(string key, T value, JsonSerializerOptions? options, string? targeting) =>
        SetSingle(key, RequireObject(key, JsonSerializer.SerializeToElement(value, options), nameof(SetFlag)), targeting);

    /// <summary>
    /// Sets <paramref name="key"/> to an enabled flag with named <paramref name="variants"/>, serving
    /// <paramref name="defaultVariant"/> when no targeting rule applies, as a flag defined in code with
    /// <see cref="FlagonOptions.Define{T}"/>.
    /// </summary>
    /// <typeparam name="T">
    /// The flag type of the variants: <see cref="bool"/>, <see cref="string"/>, <see cref="int"/>, <see cref="double"/> or
    /// <see cref="JsonElement"/> (for objects).
    /// </typeparam>
    /// <param name="key">The flag key.</param>
    /// <param name="variants">Variant name to value, e.g. <c>small</c>/<c>medium</c>/<c>large</c>.</param>
    /// <param name="defaultVariant">
    /// The variant served when no targeting rule applies, or null for none (the caller's default is served).
    /// </param>
    /// <param name="targeting">Optional targeting rule as JSON text; a rule names the variant it serves.</param>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="FlagValidationException">
    /// The key or targeting JSON is invalid; there are no variants; a variant name is empty, longer than 128 characters
    /// or repeated; a value is null, a non-finite number, an array, or of a different type from the others; or
    /// <paramref name="defaultVariant"/> is not one of the variants. Boolean variants must be exactly
    /// <c>{ "on": true, "off": false }</c>.
    /// </exception>
    /// <exception cref="FlagTargetingException"><paramref name="targeting"/> does not compile.</exception>
    /// <exception cref="ArgumentException"><typeparamref name="T"/> is not a supported variant type.</exception>
    [System.Runtime.CompilerServices.OverloadResolutionPriority(1)]
    public TestFlags SetFlag<T>(string key, IReadOnlyDictionary<string, T> variants, string? defaultVariant, string? targeting = null)
    {
        ArgumentNullException.ThrowIfNull(variants);
        FlagKey.Validate(key);
        return Set(key, FlagVariants.ToJson(key, variants), defaultVariant, targeting);
    }

    /// <summary>
    /// Replaces the targeting rule of the existing flag <paramref name="key"/>, or clears it when
    /// <paramref name="targeting"/> is null. Its variants, default variant and state are kept. An archived flag is
    /// restored, as by every write here.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <param name="targeting">The new targeting rule as JSON text, or null for none.</param>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="FlagNotFoundException">There is no flag <paramref name="key"/>.</exception>
    /// <exception cref="FlagValidationException"><paramref name="targeting"/> is not JSON.</exception>
    /// <exception cref="FlagTargetingException"><paramref name="targeting"/> does not compile.</exception>
    public TestFlags SetTargeting(string key, string? targeting)
    {
        ArgumentNullException.ThrowIfNull(key);
        var rule = Rule(key, targeting);
        return Update(key, document => document with { Targeting = rule });
    }

    /// <summary>
    /// Sets <paramref name="key"/> to a boolean flag that serves <c>true</c> only for the listed targeting keys
    /// (<see cref="EvaluationContext.TargetingKey"/>) and <c>false</c> for everyone else, including evaluations with no
    /// context.
    /// </summary>
    /// <remarks>
    /// Despite its name, this does not change the flag state (that is <see cref="Enable"/>): it writes an enabled boolean
    /// flag whose default variant is <c>off</c>, plus a targeting rule serving <c>on</c> for the listed keys, replacing
    /// any flag <paramref name="key"/> already held. It is <c>SetFlag(key, false, targeting)</c> with the rule built for you.
    /// </remarks>
    /// <param name="key">The flag key.</param>
    /// <param name="targetingKeys">The targeting keys the flag is on for; none means off for everyone.</param>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="FlagValidationException"><paramref name="key"/> is not a valid flag key.</exception>
    public TestFlags EnableFor(string key, params string[] targetingKeys)
    {
        ArgumentNullException.ThrowIfNull(targetingKeys);
        foreach (var targetingKey in targetingKeys)
        {
            ArgumentNullException.ThrowIfNull(targetingKey, nameof(targetingKeys));
        }

        var rule = $$"""{"if":[{"in":[{"var":"targetingKey"},{{JsonSerializer.Serialize(targetingKeys)}}]},"{{FlagDocument.OnVariant}}",null]}""";
        return SetFlag(key, false, rule);
    }

    /// <summary>
    /// Sets the existing flag <paramref name="key"/>, of any type, to <see cref="FlagState.Disabled"/>: evaluation serves
    /// the caller's default with reason <see cref="EvaluationReason.Disabled"/>. An archived flag is restored.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="FlagNotFoundException">There is no flag <paramref name="key"/>.</exception>
    public TestFlags Disable(string key) => Update(key, document => document with { State = FlagState.Disabled });

    /// <summary>
    /// Sets the existing flag <paramref name="key"/>, of any type, to <see cref="FlagState.Enabled"/>, keeping its
    /// variants, default variant and targeting. An archived flag is restored.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="FlagNotFoundException">There is no flag <paramref name="key"/>.</exception>
    public TestFlags Enable(string key) => Update(key, document => document with { State = FlagState.Enabled });

    /// <summary>
    /// Archives the existing flag <paramref name="key"/>: evaluation serves the caller's default with reason
    /// <see cref="EvaluationReason.Archived"/>. Archiving an archived flag does nothing; any <c>SetFlag</c> restores it.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="FlagNotFoundException">There is no flag <paramref name="key"/>.</exception>
    public TestFlags Archive(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Run(async () =>
        {
            while (true)
            {
                var current = await _store.GetAsync(key).ConfigureAwait(false) ?? throw new FlagNotFoundException(key);
                if (current.Archived || await TryArchiveAsync(current).ConfigureAwait(false) is not null)
                {
                    return;
                }
            }
        });
    }

    /// <summary>
    /// Removes the flag <paramref name="key"/> entirely (purges it, in any state): evaluation serves the caller's default
    /// with error <see cref="ErrorCode.FlagNotFound"/>. Removing a flag that does not exist does nothing.
    /// </summary>
    /// <param name="key">The flag key.</param>
    /// <returns>This instance, for chaining.</returns>
    public TestFlags Remove(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Run(() => RemoveAsync(key));
    }

    /// <summary>Archives (if needed) and purges <paramref name="key"/>, retrying on a lost race, until it is gone.</summary>
    private async Task RemoveAsync(string key)
    {
        while (await _store.GetAsync(key).ConfigureAwait(false) is { } current)
        {
            try
            {
                // The store purges only an archived flag.
                if (!current.Archived)
                {
                    current = await _store.ArchiveAsync(key, current.Version).ConfigureAwait(false);
                }

                await _store.PurgeAsync(key, current.Version).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsLostRace(ex))
            {
            }
        }
    }

    /// <summary>
    /// Returns to the starting state: every flag is removed and, when <see cref="FlagonTestingSwitches.SeedDefinitions"/>
    /// is on, the flags declared with <c>Define</c> are seeded again, and the <see cref="Evaluations"/> log is cleared.
    /// The next evaluation sees the reset state. Use it between tests that share one flagon or host.
    /// </summary>
    /// <returns>This instance, for chaining.</returns>
    public TestFlags Reset() => Run(async () =>
    {
        Evaluations.Clear();

        // Flags are purged in place rather than the store replaced, so its change version keeps rising and the snapshot reloads.
        foreach (var flag in await _store.LoadAllAsync().ConfigureAwait(false))
        {
            await RemoveAsync(flag.Key).ConfigureAwait(false);
        }

        // The seeder seeds nothing when SeedDefinitions is switched off.
        await _seeder.SeedAsync(CancellationToken.None).ConfigureAwait(false);
    });

    /// <summary>Archives <paramref name="current"/>, or returns null when a concurrent write got there first.</summary>
    private async Task<FlagRecord?> TryArchiveAsync(FlagRecord current)
    {
        try
        {
            return await _store.ArchiveAsync(current.Key, current.Version).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsLostRace(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// True when a concurrent write to the same key (another task, or the app's own admin write) moved or removed the
    /// flag: the caller re-reads and retries.
    /// </summary>
    private static bool IsLostRace(Exception ex) => ex is FlagVersionConflictException or FlagNotFoundException;

    /// <summary>
    /// Returns <paramref name="value"/> if it is a JSON object, else throws. Production rejects a single value that is not
    /// an object only on the typed getter; an object setter checks it here.
    /// </summary>
    private static JsonElement RequireObject(string key, JsonElement value, string method) =>
        value.ValueKind == JsonValueKind.Object
            ? value
            : throw new FlagValidationException(
                $"Flag '{key}' object value is a JSON {value.ValueKind.ToString().ToLowerInvariant()}; {method} takes a JSON object.");

    /// <summary>A flag of one value, the shape <c>Define</c> and auto-create give it: <c>on</c>/<c>off</c> for a boolean, else <c>default</c>.</summary>
    private TestFlags SetSingle(string key, JsonElement value, string? targeting)
    {
        FlagKey.Validate(key);
        var single = FlagDocument.SingleValue(value);
        return Set(key, single.Variants, single.DefaultVariant, targeting);
    }

    /// <summary>Validates an enabled flag with the write-time rules and upserts it.</summary>
    private TestFlags Set(string key, IEnumerable<KeyValuePair<string, JsonElement>> variants, string? defaultVariant, string? targeting)
    {
        var (validated, _) = FlagVariants.Validate(key, variants, defaultVariant);
        var document = new FlagDocument(FlagState.Enabled, validated, defaultVariant, new FlagMetadata(), Rule(key, targeting));
        return Upsert(key, _ => document);
    }

    /// <summary>The rule to store for <paramref name="targeting"/>, compiled now with the registered compiler.</summary>
    private JsonElement? Rule(string key, string? targeting) =>
        TargetingRules.Validate(_compiler, key, TargetingRules.ParseJson(key, targeting));

    /// <summary>Rewrites the document of the existing flag <paramref name="key"/>.</summary>
    /// <exception cref="FlagNotFoundException">There is no flag <paramref name="key"/>.</exception>
    private TestFlags Update(string key, Func<FlagDocument, FlagDocument> update) =>
        Upsert(key, current => current is null ? throw new FlagNotFoundException(key) : update(current.Document));

    /// <summary>
    /// Writes the document <paramref name="build"/> makes from the current record (null when there is none), restoring
    /// an archived flag, then refreshes the snapshot.
    /// </summary>
    private TestFlags Upsert(string key, Func<FlagRecord?, FlagDocument> build) => Run(async () =>
    {
        // A concurrent write to the same key (another task, or the app's own admin write) moves the version: re-read and retry.
        while (!await TryWriteAsync(key, build).ConfigureAwait(false))
        {
        }
    });

    /// <summary>
    /// Runs <paramref name="write"/> then refreshes the snapshot, blocking until both finish. The in-memory store
    /// completes synchronously and every awaited path uses <c>ConfigureAwait(false)</c>, so blocking here cannot deadlock
    /// a test framework's synchronization context.
    /// </summary>
    private TestFlags Run(Func<Task> write)
    {
        RunAsync().GetAwaiter().GetResult();
        return this;

        async Task RunAsync()
        {
            await write().ConfigureAwait(false);
            await _snapshots.RefreshAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One attempt at the upsert: creates, or restores and updates, <paramref name="key"/>. Returns false when a concurrent
    /// write got there first.
    /// </summary>
    private async Task<bool> TryWriteAsync(string key, Func<FlagRecord?, FlagDocument> build)
    {
        var current = await _store.GetAsync(key).ConfigureAwait(false);
        var document = build(current);
        if (current is null)
        {
            return await _store.TryCreateAsync(key, document).ConfigureAwait(false) is not null;
        }

        try
        {
            if (current.Archived)
            {
                current = await _store.RestoreAsync(key, current.Version).ConfigureAwait(false);
            }

            await _store.UpdateAsync(key, document, current.Version).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (IsLostRace(ex))
        {
            return false;
        }
    }
}
